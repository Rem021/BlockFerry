using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlockFerry.Core.Pcl2;
using BlockFerry.Core.System;
using BlockFerry.Core.Transactions;

namespace BlockFerry.Core.Discovery;

public enum DiscoveryRootStoreDiagnosticCode
{
    InMemoryOnly,
    ProtectedPayloadRejected,
    PayloadLimitExceeded,
    UnexpectedSchema,
    MalformedPayload,
    RecoveryRequired,
}

public sealed record DiscoveryRootStoreDiagnostic(
    DiscoveryRootStoreDiagnosticCode Code,
    string Message);

public sealed class DiscoveryRootStore(
    AppStorageGuard appStorage,
    IProtectedData protectedData)
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumRoots = 64;
    private const int MaximumRootUtf16Length = 4096;
    private const int MaximumPlaintextBytes = 256 * 1024;
    private const int MaximumCiphertextBytes = 1024 * 1024;
    private const int MaximumJsonDepth = 8;
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("BlockFerry/discovery-roots/schema-1/current-user");
    private static readonly NormalizedRelativePath PayloadPath = CreatePayloadPath();
    private readonly object gate = new();
    private readonly Guid storeId = Guid.NewGuid();
    private readonly byte[] approvalKey = RandomNumberGenerator.GetBytes(32);
    private long generation = 1;
    private RememberedDiscoveryRoots memory = RememberedDiscoveryRoots.Empty;

    public DiscoveryRootStoreDiagnostic? LastDiagnostic { get; private set; }

    public ManualRootApprovalToken? ApproveManualRoot(
        Pcl2RootCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var access = candidate.ResolvedAccess;
        if (access is null ||
            access.ProofFileSystem is null ||
            access.ManualSelectionProvenance is null ||
            !access.ManualSelectionProvenance.Validates(access) ||
            !HasIdentity(access.ApprovedRootIdentity))
        {
            return null;
        }

        string normalized;
        try
        {
            normalized = NormalizeRoot(access.ApprovedRootPath);
        }
        catch (ArgumentException)
        {
            return null;
        }

        lock (gate)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var proof = access.ProofFileSystem.OpenRoot(
                    normalized,
                    FileSystemOpenPurpose.Discovery,
                    cancellationToken);
                var volume = access.ProofFileSystem.InspectVolume(
                    proof,
                    cancellationToken);
                if (proof.Identity != access.ApprovedRootIdentity ||
                    !SameCanonicalPath(proof.FinalPath, normalized) ||
                    !volume.IsLocalVolume ||
                    volume.IsNetworkRedirected)
                {
                    return null;
                }

                var nonce = RandomNumberGenerator.GetBytes(32);
                try
                {
                    var authenticator = Authenticate(
                        storeId,
                        generation,
                        normalized,
                        proof.Identity,
                        nonce);
                    try
                    {
                        return new ManualRootApprovalToken(
                            storeId,
                            generation,
                            normalized,
                            proof.Identity,
                            nonce,
                            authenticator,
                            access.ProofFileSystem);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(authenticator);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(nonce);
                }
            }
            catch (Exception exception) when (
                exception is CapabilityBoundaryException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }
    }

    public RememberedDiscoveryRoots Load(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!appStorage.IsAvailable)
            {
                LastDiagnostic = InMemoryOnlyDiagnostic();
                return memory;
            }

            var readResult = appStorage.TryRead(
                PayloadPath,
                MaximumCiphertextBytes,
                cancellationToken);
            if (readResult.State == AppStorageReadState.Unavailable)
            {
                LastDiagnostic = InMemoryOnlyDiagnostic();
                return memory;
            }

            if (readResult.State == AppStorageReadState.RecoveryRequired)
            {
                LastDiagnostic = RecoveryDiagnostic();
                return memory;
            }

            if (readResult.State == AppStorageReadState.LimitExceeded)
            {
                memory = RememberedDiscoveryRoots.Empty;
                LastDiagnostic = LimitDiagnostic();
                return memory;
            }

            if (readResult.State == AppStorageReadState.Missing)
            {
                memory = RememberedDiscoveryRoots.Empty;
                LastDiagnostic = null;
                return memory;
            }

            var ciphertext = readResult.Bytes ??
                throw new InvalidOperationException("A guarded read result lacked its bounded bytes.");
            if (ciphertext.Length > MaximumCiphertextBytes)
            {
                memory = RememberedDiscoveryRoots.Empty;
                LastDiagnostic = LimitDiagnostic();
                return memory;
            }

            byte[] plaintext;
            try
            {
                plaintext = protectedData.Unprotect(ciphertext, Entropy, MaximumPlaintextBytes);
            }
            catch (ProtectedDataLimitException)
            {
                memory = RememberedDiscoveryRoots.Empty;
                LastDiagnostic = LimitDiagnostic();
                return memory;
            }
            catch (Exception exception) when (
                exception is CryptographicException or ArgumentException or InvalidOperationException)
            {
                memory = RememberedDiscoveryRoots.Empty;
                LastDiagnostic = new DiscoveryRootStoreDiagnostic(
                    DiscoveryRootStoreDiagnosticCode.ProtectedPayloadRejected,
                    "The remembered-location payload could not be authenticated for this Windows user.");
                return memory;
            }

            try
            {
                if (plaintext.Length > MaximumPlaintextBytes)
                {
                    memory = RememberedDiscoveryRoots.Empty;
                    LastDiagnostic = LimitDiagnostic();
                    return memory;
                }

                memory = ParsePlaintext(plaintext, cancellationToken);
                LastDiagnostic = null;
                return memory;
            }
            catch (PayloadValidationException exception)
            {
                memory = RememberedDiscoveryRoots.Empty;
                LastDiagnostic = new DiscoveryRootStoreDiagnostic(exception.Code, exception.SafeMessage);
                return memory;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public AppStorageMutationResult Save(
        RememberedDiscoveryRoots value,
        CancellationToken cancellationToken = default) =>
        Save(value, [], cancellationToken);

    public AppStorageMutationResult Save(
        RememberedDiscoveryRoots value,
        IEnumerable<ManualRootApprovalToken> approvals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(approvals);
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var validatedSave = ValidateForSave(value, approvals, cancellationToken);
            var validated = validatedSave.Value;
            var precommitAuthority = new ManualRootPrecommitAuthority(
                this,
                validatedSave.Approvals,
                generation);
            generation = checked(generation + 1);
            byte[] plaintext;
            try
            {
                plaintext = DiscoveryRootPayloadCodec.Serialize(
                    validated,
                    MaximumPlaintextBytes,
                    cancellationToken);
            }
            catch (PayloadLimitException)
            {
                LastDiagnostic = LimitDiagnostic();
                return AppStorageMutationResult.NotCommitted();
            }

            try
            {
                var ciphertext = protectedData.Protect(plaintext, Entropy, MaximumCiphertextBytes);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ciphertext.Length > MaximumCiphertextBytes)
                    {
                        LastDiagnostic = LimitDiagnostic();
                        return AppStorageMutationResult.NotCommitted();
                    }

                    if (!appStorage.IsAvailable)
                    {
                        memory = validated;
                        LastDiagnostic = InMemoryOnlyDiagnostic();
                        return AppStorageMutationResult.NotCommitted(appStorage.LastDiagnostic);
                    }

                    var result = appStorage.TryAtomicReplace(
                        PayloadPath,
                        ciphertext,
                        precommitAuthority,
                        cancellationToken);
                    if (result.State == AppStorageMutationState.CommittedVerified)
                    {
                        memory = validated;
                        LastDiagnostic = null;
                    }
                    else if (result.State == AppStorageMutationState.RecoveryRequired)
                    {
                        LastDiagnostic = RecoveryDiagnostic();
                    }
                    else
                    {
                        memory = validated;
                        LastDiagnostic = InMemoryOnlyDiagnostic();
                    }

                    return result;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(ciphertext);
                }
            }
            catch (OperationCanceledException)
            {
                LastDiagnostic = InMemoryOnlyDiagnostic();
                return AppStorageMutationResult.NotCommitted();
            }
            catch (ProtectedDataLimitException)
            {
                LastDiagnostic = LimitDiagnostic();
                return AppStorageMutationResult.NotCommitted();
            }
            catch (Exception exception) when (
                exception is CryptographicException or ArgumentException or InvalidOperationException)
            {
                LastDiagnostic = new DiscoveryRootStoreDiagnostic(
                    DiscoveryRootStoreDiagnosticCode.ProtectedPayloadRejected,
                    "The remembered-location payload could not be protected for this Windows user.");
                return AppStorageMutationResult.NotCommitted();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public AppStorageMutationResult Clear(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            generation = checked(generation + 1);
            memory = RememberedDiscoveryRoots.Empty;
            if (!appStorage.IsAvailable)
            {
                LastDiagnostic = InMemoryOnlyDiagnostic();
                return AppStorageMutationResult.NotCommitted(appStorage.LastDiagnostic);
            }

            var result = appStorage.TryDelete(PayloadPath, cancellationToken);
            LastDiagnostic = result.State switch
            {
                AppStorageMutationState.CommittedVerified => null,
                AppStorageMutationState.RecoveryRequired => RecoveryDiagnostic(),
                _ => InMemoryOnlyDiagnostic(),
            };
            return result;
        }
    }

    private ValidatedSave ValidateForSave(
        RememberedDiscoveryRoots value,
        IEnumerable<ManualRootApprovalToken> approvals,
        CancellationToken cancellationToken)
    {
        if (value.SchemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Only the current remembered-location schema may be saved.");
        }

        var rawRoots = AcquireBounded(
            value.ApprovedRoots,
            MaximumRoots,
            nameof(value),
            cancellationToken);
        var rawApprovals = AcquireBounded(
            approvals,
            MaximumRoots,
            nameof(approvals),
            cancellationToken);
        var roots = new List<string>(rawRoots.Count);
        var validatedApprovals = new List<ManualRootApprovalToken>(rawRoots.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedNonces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in rawRoots)
        {
            if (root is null || root.Length > MaximumRootUtf16Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    $"Each approved root must be at most {MaximumRootUtf16Length} UTF-16 code units.");
            }

            var normalized = NormalizeRoot(root);
            var token = rawApprovals.SingleOrDefault(candidate =>
                string.Equals(candidate.CanonicalPath, normalized, StringComparison.OrdinalIgnoreCase));
            if (token is null ||
                token.StoreId != storeId ||
                token.Generation != generation ||
                !HasIdentity(token.Identity) ||
                !usedNonces.Add(Convert.ToHexString(token.Nonce)))
            {
                throw new InvalidOperationException(
                    "A remembered root lacked a current store-owned manual approval token.");
            }

            var expectedAuthenticator = Authenticate(
                token.StoreId,
                token.Generation,
                token.CanonicalPath,
                token.Identity,
                token.Nonce);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(
                        expectedAuthenticator,
                        token.Authenticator))
                {
                    throw new InvalidOperationException("A remembered-root approval token was not authentic.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedAuthenticator);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var reproved = token.ProofFileSystem.OpenRoot(
                    token.CanonicalPath,
                    FileSystemOpenPurpose.Discovery,
                    cancellationToken);
                var volume = token.ProofFileSystem.InspectVolume(reproved, cancellationToken);
                if (reproved.Identity != token.Identity ||
                    !SameCanonicalPath(reproved.FinalPath, token.CanonicalPath) ||
                    !volume.IsLocalVolume ||
                    volume.IsNetworkRedirected)
                {
                    throw new InvalidOperationException(
                        "A remembered-root approval identity changed before persistence.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is CapabilityBoundaryException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new InvalidOperationException(
                    "A remembered-root approval identity could not be reproved before persistence.",
                    exception);
            }

            if (!seen.Add(normalized))
            {
                throw new ArgumentException("Duplicate approved roots are not allowed.", nameof(value));
            }

            roots.Add(normalized);
            validatedApprovals.Add(token);
        }

        if (rawApprovals.Count != roots.Count)
        {
            throw new InvalidOperationException("Every manual approval token must match exactly one remembered root.");
        }

        if (value.LastSourceInstanceId is not null || value.LastTargetInstanceId is not null)
        {
            throw new InvalidOperationException(
                "Last-used instance IDs require Task 4 session-owned approvals and cannot be saved by Task 3.");
        }
        var remembered = roots.Count == 0 &&
            value.LastSourceInstanceId is null &&
            value.LastTargetInstanceId is null
            ? RememberedDiscoveryRoots.Empty
            : RememberedDiscoveryRoots.CreateFrozen(
                CurrentSchemaVersion,
                roots,
                value.LastSourceInstanceId,
                value.LastTargetInstanceId);
        return new ValidatedSave(
            remembered,
            Array.AsReadOnly(validatedApprovals.ToArray()));
    }

    private ManualRootAuthorityLease RevalidateAtPrecommit(
        IReadOnlyList<ManualRootApprovalToken> approvals,
        long approvedGeneration,
        CancellationToken cancellationToken)
    {
        var retained = new List<IVerifiedDirectoryHandle>(approvals.Count);
        try
        {
            foreach (var token in approvals)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (token.StoreId != storeId ||
                    token.Generation != approvedGeneration ||
                    !HasIdentity(token.Identity))
                {
                    throw new AppStoragePrecommitAuthorityException(
                        "A remembered-root approval was stale at the storage precommit boundary.");
                }

                var expectedAuthenticator = Authenticate(
                    token.StoreId,
                    token.Generation,
                    token.CanonicalPath,
                    token.Identity,
                    token.Nonce);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(
                            expectedAuthenticator,
                            token.Authenticator))
                    {
                        throw new AppStoragePrecommitAuthorityException(
                            "A remembered-root approval failed authentication at the storage precommit boundary.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(expectedAuthenticator);
                }

                IVerifiedDirectoryHandle? reproved = null;
                try
                {
                    reproved = token.ProofFileSystem.OpenRoot(
                        token.CanonicalPath,
                        FileSystemOpenPurpose.Discovery,
                        cancellationToken);
                    var volume = token.ProofFileSystem.InspectVolume(
                        reproved,
                        cancellationToken);
                    if (reproved.Identity != token.Identity ||
                        !SameCanonicalPath(reproved.FinalPath, token.CanonicalPath) ||
                        !volume.IsLocalVolume ||
                        volume.IsNetworkRedirected)
                    {
                        throw new AppStoragePrecommitAuthorityException(
                            "A remembered-root approval identity changed at the storage precommit boundary.");
                    }

                    retained.Add(reproved);
                    reproved = null;
                }
                finally
                {
                    reproved?.Dispose();
                }
            }

            var lease = new ManualRootAuthorityLease(retained);
            retained = null!;
            return lease;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AppStoragePrecommitAuthorityException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is CapabilityBoundaryException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new AppStoragePrecommitAuthorityException(
                "A remembered-root approval identity could not be reproved at the storage precommit boundary.",
                exception);
        }
        finally
        {
            if (retained is not null)
            {
                for (var index = retained.Count - 1; index >= 0; index--)
                {
                    retained[index].Dispose();
                }
            }
        }
    }

    private static RememberedDiscoveryRoots ParsePlaintext(
        byte[] plaintext,
        CancellationToken cancellationToken)
    {
        try
        {
            var reader = new Utf8JsonReader(
                plaintext,
                new JsonReaderOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumJsonDepth,
                });
            if (!ReadNext(ref reader, cancellationToken) ||
                reader.TokenType != JsonTokenType.StartObject)
            {
                throw Malformed();
            }

            var fields = PayloadField.None;
            var schemaVersion = 0;
            var roots = new List<string>();
            var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (ReadNext(ref reader, cancellationToken))
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw Malformed();
                }

                var field = IdentifyField(ref reader);
                if (field == PayloadField.None || (fields & field) != 0)
                {
                    throw Malformed();
                }

                fields |= field;
                if (!ReadNext(ref reader, cancellationToken))
                {
                    throw Malformed();
                }

                switch (field)
                {
                    case PayloadField.SchemaVersion:
                        if (reader.TokenType != JsonTokenType.Number ||
                            !reader.TryGetInt32(out schemaVersion))
                        {
                            throw Malformed();
                        }

                        if (schemaVersion != CurrentSchemaVersion)
                        {
                            throw new PayloadValidationException(
                                DiscoveryRootStoreDiagnosticCode.UnexpectedSchema,
                                "The remembered-location payload uses an unsupported schema.");
                        }

                        break;
                    case PayloadField.ApprovedRoots:
                        ReadRoots(
                            ref reader,
                            roots,
                            seenRoots,
                            cancellationToken);
                        break;
                    case PayloadField.LastSourceInstanceId:
                    case PayloadField.LastTargetInstanceId:
                        if (reader.TokenType != JsonTokenType.Null)
                        {
                            throw Malformed();
                        }

                        break;
                    default:
                        throw Malformed();
                }
            }

            var allFields =
                PayloadField.SchemaVersion |
                PayloadField.ApprovedRoots |
                PayloadField.LastSourceInstanceId |
                PayloadField.LastTargetInstanceId;
            if (reader.TokenType != JsonTokenType.EndObject ||
                fields != allFields ||
                ReadNext(ref reader, cancellationToken))
            {
                throw Malformed();
            }

            return roots.Count == 0
                ? RememberedDiscoveryRoots.Empty
                : RememberedDiscoveryRoots.CreateFrozen(
                    schemaVersion,
                    roots,
                    null,
                    null);
        }
        catch (PayloadValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or InvalidOperationException or PathTooLongException)
        {
            throw Malformed();
        }
    }

    private static void ReadRoots(
        ref Utf8JsonReader reader,
        List<string> roots,
        HashSet<string> seenRoots,
        CancellationToken cancellationToken)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw Malformed();
        }

        var stringBuffer = new char[MaximumRootUtf16Length + 1];
        try
        {
            while (ReadNext(ref reader, cancellationToken))
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    return;
                }

                if (roots.Count >= MaximumRoots)
                {
                    throw Limit();
                }

                if (reader.TokenType != JsonTokenType.String)
                {
                    throw Malformed();
                }

                var rawLength = reader.HasValueSequence
                    ? reader.ValueSequence.Length
                    : reader.ValueSpan.Length;
                if (rawLength > MaximumRootUtf16Length * 6L)
                {
                    throw Limit();
                }

                int characterCount;
                try
                {
                    characterCount = reader.CopyString(stringBuffer);
                }
                catch (ArgumentException)
                {
                    throw Limit();
                }

                if (characterCount > MaximumRootUtf16Length)
                {
                    throw Limit();
                }

                var value = new string(stringBuffer.AsSpan(0, characterCount));
                var normalized = NormalizeRoot(value);
                if (!seenRoots.Add(normalized))
                {
                    throw Malformed();
                }

                roots.Add(normalized);
            }

            throw Malformed();
        }
        finally
        {
            Array.Clear(stringBuffer);
        }
    }

    private static PayloadField IdentifyField(ref Utf8JsonReader reader)
    {
        if (reader.ValueTextEquals("schemaVersion"u8))
        {
            return PayloadField.SchemaVersion;
        }

        if (reader.ValueTextEquals("approvedRoots"u8))
        {
            return PayloadField.ApprovedRoots;
        }

        if (reader.ValueTextEquals("lastSourceInstanceId"u8))
        {
            return PayloadField.LastSourceInstanceId;
        }

        if (reader.ValueTextEquals("lastTargetInstanceId"u8))
        {
            return PayloadField.LastTargetInstanceId;
        }

        return PayloadField.None;
    }

    private static bool ReadNext(
        ref Utf8JsonReader reader,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return reader.Read();
    }

    [Flags]
    private enum PayloadField
    {
        None = 0,
        SchemaVersion = 1,
        ApprovedRoots = 2,
        LastSourceInstanceId = 4,
        LastTargetInstanceId = 8,
    }

    private static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) ||
            root.Length > MaximumRootUtf16Length ||
            root.Any(character => char.IsControl(character)) ||
            !Path.IsPathFullyQualified(root) ||
            root.StartsWith("\\\\", StringComparison.Ordinal) ||
            root.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            root.StartsWith("\\\\.\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("A normal local fully-qualified root path is required.", nameof(root));
        }

        return Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool HasIdentity(PhysicalDirectoryIdentity identity) =>
        identity.FileIdLow != 0 || identity.FileIdHigh != 0;

    private static List<T> AcquireBounded<T>(
        IEnumerable<T> values,
        int maximumCount,
        string parameterName,
        CancellationToken cancellationToken)
    {
        var result = new List<T>(maximumCount);
        using var enumerator = values.GetEnumerator();
        while (result.Count < maximumCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!enumerator.MoveNext())
            {
                return result;
            }

            cancellationToken.ThrowIfCancellationRequested();
            result.Add(enumerator.Current);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (enumerator.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"At most {maximumCount} raw values may be supplied.");
        }

        return result;
    }

    private byte[] Authenticate(
        Guid ownerStoreId,
        long tokenGeneration,
        string canonicalPath,
        PhysicalDirectoryIdentity identity,
        ReadOnlySpan<byte> nonce)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, approvalKey);
        Span<byte> fixedFields = stackalloc byte[16 + (5 * sizeof(ulong))];
        ownerStoreId.TryWriteBytes(fixedFields);
        BinaryPrimitives.WriteInt64LittleEndian(fixedFields[16..], tokenGeneration);
        BinaryPrimitives.WriteUInt64LittleEndian(fixedFields[24..], identity.VolumeSerialNumber);
        BinaryPrimitives.WriteUInt64LittleEndian(fixedFields[32..], identity.FileIdLow);
        BinaryPrimitives.WriteUInt64LittleEndian(fixedFields[40..], identity.FileIdHigh);
        BinaryPrimitives.WriteUInt64LittleEndian(fixedFields[48..], checked((ulong)canonicalPath.Length));
        hmac.AppendData(fixedFields);
        var pathBytes = Encoding.UTF8.GetBytes(canonicalPath);
        try
        {
            hmac.AppendData(pathBytes);
            hmac.AppendData(nonce);
            return hmac.GetHashAndReset();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pathBytes);
            CryptographicOperations.ZeroMemory(fixedFields);
        }
    }

    private static bool SameCanonicalPath(string left, string right) =>
        string.Equals(
            NormalizeRoot(left),
            NormalizeRoot(right),
            StringComparison.OrdinalIgnoreCase);

    private static NormalizedRelativePath CreatePayloadPath()
    {
        if (!NormalizedRelativePath.TryCreate(
                "discovery-roots.json",
                out var path,
                out var rejection))
        {
            throw new InvalidOperationException(rejection);
        }

        return path!;
    }

    private static DiscoveryRootStoreDiagnostic InMemoryOnlyDiagnostic() =>
        new(
            DiscoveryRootStoreDiagnosticCode.InMemoryOnly,
            "Guarded app storage could not be proven; remembered locations remain in memory only.");

    private static DiscoveryRootStoreDiagnostic LimitDiagnostic() =>
        new(
            DiscoveryRootStoreDiagnosticCode.PayloadLimitExceeded,
            "The remembered-location payload exceeded a fixed safety limit.");

    private static DiscoveryRootStoreDiagnostic RecoveryDiagnostic() =>
        new(
            DiscoveryRootStoreDiagnosticCode.RecoveryRequired,
            "The remembered-location mutation requires guarded recovery before its state is trusted.");

    private static PayloadValidationException Malformed() =>
        new(
            DiscoveryRootStoreDiagnosticCode.MalformedPayload,
            "The remembered-location payload was malformed.");

    private static PayloadValidationException Limit() =>
        new(
            DiscoveryRootStoreDiagnosticCode.PayloadLimitExceeded,
            "The remembered-location payload exceeded a fixed safety limit.");

    private sealed record ValidatedSave(
        RememberedDiscoveryRoots Value,
        IReadOnlyList<ManualRootApprovalToken> Approvals);

    private sealed class ManualRootPrecommitAuthority(
        DiscoveryRootStore owner,
        IReadOnlyList<ManualRootApprovalToken> approvals,
        long approvedGeneration) : IAppStoragePrecommitAuthority
    {
        private readonly IReadOnlyList<ManualRootApprovalToken> approvals =
            Array.AsReadOnly(approvals.ToArray());

        public IDisposable Revalidate(CancellationToken cancellationToken) =>
            owner.RevalidateAtPrecommit(
                approvals,
                approvedGeneration,
                cancellationToken);
    }

    private sealed class ManualRootAuthorityLease(
        List<IVerifiedDirectoryHandle> retained) : IDisposable
    {
        private List<IVerifiedDirectoryHandle>? retained = retained;

        public void Dispose()
        {
            var handles = Interlocked.Exchange(ref retained, null);
            if (handles is null)
            {
                return;
            }

            for (var index = handles.Count - 1; index >= 0; index--)
            {
                handles[index].Dispose();
            }
        }
    }

    private sealed class PayloadValidationException(
        DiscoveryRootStoreDiagnosticCode code,
        string safeMessage) : Exception
    {
        public DiscoveryRootStoreDiagnosticCode Code { get; } = code;
        public string SafeMessage { get; } = safeMessage;
    }
}
