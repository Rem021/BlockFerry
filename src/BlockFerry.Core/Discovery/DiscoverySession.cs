using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using BlockFerry.Core.Pcl2;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Discovery;

public sealed record DiscoveredInstanceChoice(
    Pcl2Instance Instance,
    VerifiedDirectorySnapshot GameRoot,
    string ProofToken);

public sealed record DiscoveredInstancePair(
    DiscoveredInstanceChoice Source,
    DiscoveredInstanceChoice Target,
    long Generation);

public sealed record DiscoveryPairValidation(
    bool IsValid,
    bool IsStale,
    DiscoveredInstancePair? Pair,
    IReadOnlyList<Pcl2Diagnostic> Diagnostics);

public sealed class DiscoverySession : IDisposable
{
    private const int GenerationKeyBytes = 32;
    private const int MaximumDomainUtf8Bytes = 256;
    private const int MaximumOpaquePayloadBytes = 1024 * 1024;
    private static readonly byte[] OpaqueTagPrefix =
        Encoding.ASCII.GetBytes("BlockFerry.GenerationOpaqueTag.v1\0");
    private readonly object gate = new();
    private readonly byte[] generationKey;
    private readonly Dictionary<string, DiscoverySessionEntry> entries;
    private readonly ReadOnlyDictionary<string, Pcl2Diagnostic> rejections;
    private readonly ReadOnlyCollection<DiscoveredInstanceChoice> instances;
    private int active = 1;

    internal DiscoverySession(
        long generation,
        IReadOnlyList<DiscoverySessionEntry> acceptedEntries,
        IReadOnlyDictionary<string, Pcl2Diagnostic> rejectedInstances)
    {
        ArgumentNullException.ThrowIfNull(acceptedEntries);
        ArgumentNullException.ThrowIfNull(rejectedInstances);
        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generation),
                "A discovery generation must be positive.");
        }

        Generation = generation;
        generationKey = RandomNumberGenerator.GetBytes(GenerationKeyBytes);
        entries = new Dictionary<string, DiscoverySessionEntry>(
            acceptedEntries.Count,
            StringComparer.Ordinal);
        try
        {
            foreach (var entry in acceptedEntries)
            {
                var proofToken = CreateChoiceProofLocked(entry);
                entry.SetChoice(new DiscoveredInstanceChoice(
                    entry.Instance,
                    entry.GameRootSnapshot,
                    proofToken));
                entries.Add(entry.Instance.Id, entry);
            }

            instances = Array.AsReadOnly(entries.Values
                .Select(entry => entry.Choice)
                .ToArray());
            rejections = new ReadOnlyDictionary<string, Pcl2Diagnostic>(
                new Dictionary<string, Pcl2Diagnostic>(
                    rejectedInstances,
                    StringComparer.Ordinal));
        }
        catch
        {
            try
            {
                _ = DisposeEntries(acceptedEntries);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(generationKey);
                Volatile.Write(ref active, 0);
            }

            throw;
        }
    }

    public long Generation { get; }
    public bool IsActive => Volatile.Read(ref active) == 1;
    public IReadOnlyList<DiscoveredInstanceChoice> Instances => instances;

    public bool TryGetPair(
        string sourceId,
        string targetId,
        out DiscoveredInstancePair pair)
    {
        lock (gate)
        {
            if (!IsActive ||
                !TryGetPairLocked(sourceId, targetId, out var acceptedPair, out _))
            {
                pair = null!;
                return false;
            }

            pair = acceptedPair!;
            return true;
        }
    }

    internal string CreateGenerationOpaqueTag(
        string domain,
        ReadOnlySpan<byte> payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        var domainBytes = Encoding.UTF8.GetBytes(domain);
        try
        {
            if (domainBytes.Length > MaximumDomainUtf8Bytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(domain),
                    "The opaque-tag domain exceeds its UTF-8 bound.");
            }

            if (payload.Length > MaximumOpaquePayloadBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(payload),
                    "The opaque-tag payload exceeds its byte bound.");
            }

            lock (gate)
            {
                ObjectDisposedException.ThrowIf(!IsActive, this);
                return ComputeTagLocked(domainBytes, payload);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(domainBytes);
        }
    }

    internal DiscoveryPairValidation Revalidate(
        string sourceId,
        string targetId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!IsActive)
            {
                return Invalid(
                    isStale: false,
                    Diagnostic(
                        Pcl2DiagnosticCode.DiscoverySessionInactive,
                        "The discovery session is no longer active."));
            }

            if (!TryGetPairLocked(
                    sourceId,
                    targetId,
                    out var pair,
                    out var pairDiagnostic))
            {
                return Invalid(isStale: false, pairDiagnostic!);
            }

            var sourceEntry = entries[pair!.Source.Instance.Id];
            if (!TryRevalidateEntry(
                    sourceEntry,
                    cancellationToken,
                    out var sourceDiagnostic))
            {
                return Invalid(isStale: true, sourceDiagnostic!);
            }

            var targetEntry = entries[pair.Target.Instance.Id];
            if (!TryRevalidateEntry(
                    targetEntry,
                    cancellationToken,
                    out var targetDiagnostic))
            {
                return Invalid(isStale: true, targetDiagnostic!);
            }

            return new DiscoveryPairValidation(
                true,
                false,
                pair,
                Array.Empty<Pcl2Diagnostic>());
        }
    }

    internal DiscoveredInstanceChoice? RevalidateTarget(
        string targetId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!IsActive ||
                !IsBoundedInstanceId(targetId) ||
                !entries.TryGetValue(targetId, out var target) ||
                !ChoiceProofIsCurrentLocked(target) ||
                !TryRevalidateEntry(target, cancellationToken, out _))
            {
                return null;
            }

            return target.Choice;
        }
    }

    internal DiscoveryPairValidation ValidateEvidence(
        DiscoveredInstancePair evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        lock (gate)
        {
            if (!IsActive)
            {
                return Invalid(
                    isStale: false,
                    Diagnostic(
                        Pcl2DiagnosticCode.DiscoverySessionInactive,
                        "The discovery session is no longer active."));
            }

            if (evidence.Generation != Generation)
            {
                return Invalid(
                    isStale: false,
                    Diagnostic(
                        Pcl2DiagnosticCode.DiscoveryGenerationMismatch,
                        "The discovery evidence belongs to a different generation."));
            }

            var evidenceSource = evidence.Source;
            var evidenceTarget = evidence.Target;
            if (evidenceSource is null ||
                evidenceTarget is null ||
                evidenceSource.Instance is null ||
                evidenceTarget.Instance is null ||
                !IsBoundedInstanceId(evidenceSource.Instance.Id) ||
                !IsBoundedInstanceId(evidenceTarget.Instance.Id) ||
                !entries.TryGetValue(evidenceSource.Instance.Id, out var source) ||
                !entries.TryGetValue(evidenceTarget.Instance.Id, out var target) ||
                ReferenceEquals(source, target) ||
                source.GameRootSnapshot.Identity == target.GameRootSnapshot.Identity ||
                !ReferenceEquals(evidenceSource, source.Choice) ||
                !ReferenceEquals(evidenceTarget, target.Choice) ||
                !ChoiceProofIsCurrentLocked(source) ||
                !ChoiceProofIsCurrentLocked(target))
            {
                return Invalid(
                    isStale: false,
                    Diagnostic(
                        Pcl2DiagnosticCode.DiscoveryProofInvalid,
                        "The discovery evidence is not authentic for this session."));
            }

            return new DiscoveryPairValidation(
                true,
                false,
                evidence,
                Array.Empty<Pcl2Diagnostic>());
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (Interlocked.Exchange(ref active, 0) == 0)
            {
                return;
            }

            Exception? disposeFailure;
            try
            {
                disposeFailure = DisposeEntries(entries.Values);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(generationKey);
            }

            if (disposeFailure is not null)
            {
                global::System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(disposeFailure)
                    .Throw();
            }
        }
    }

    private bool TryGetPairLocked(
        string sourceId,
        string targetId,
        out DiscoveredInstancePair? pair,
        out Pcl2Diagnostic? diagnostic)
    {
        pair = null;
        diagnostic = null;
        if (!IsBoundedInstanceId(sourceId) || !IsBoundedInstanceId(targetId))
        {
            diagnostic = Diagnostic(
                Pcl2DiagnosticCode.DiscoveryInstanceUnavailable,
                "The selected discovery instance is unavailable.");
            return false;
        }

        if (string.Equals(sourceId, targetId, StringComparison.Ordinal))
        {
            diagnostic = Diagnostic(
                Pcl2DiagnosticCode.SameSourceAndTarget,
                "The source and target must be different physical game roots.",
                sourceId);
            return false;
        }

        if (!entries.TryGetValue(sourceId, out var source))
        {
            diagnostic = RejectionOrUnavailable(sourceId);
            return false;
        }

        if (!entries.TryGetValue(targetId, out var target))
        {
            diagnostic = RejectionOrUnavailable(targetId);
            return false;
        }

        if (source.GameRootSnapshot.Identity == target.GameRootSnapshot.Identity)
        {
            diagnostic = Diagnostic(
                Pcl2DiagnosticCode.SameSourceAndTarget,
                "The source and target resolve to the same physical game root.");
            return false;
        }

        if (!ChoiceProofIsCurrentLocked(source) ||
            !ChoiceProofIsCurrentLocked(target))
        {
            diagnostic = Diagnostic(
                Pcl2DiagnosticCode.DiscoveryProofInvalid,
                "The selected discovery proof is not authentic for this session.");
            return false;
        }

        pair = new DiscoveredInstancePair(
            source.Choice,
            target.Choice,
            Generation);
        return true;
    }

    private static bool TryRevalidateEntry(
        DiscoverySessionEntry entry,
        CancellationToken cancellationToken,
        out Pcl2Diagnostic? diagnostic)
    {
        diagnostic = null;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!Pcl2InstanceProof.IsValid(entry.Instance) ||
                !ReferenceEquals(
                    entry.Instance.CapabilityAccess,
                    entry.CapabilityAccess) ||
                entry.RetainedGameRoot.Identity != entry.GameRootSnapshot.Identity ||
                !entry.RetainedGameRoot.IsLocalVolume ||
                entry.RetainedGameRoot.IsNetworkRedirected)
            {
                diagnostic = Stale(entry.Instance.Id);
                return false;
            }

            using var retainedGameRoot = entry.RetainedPathGuard.OpenMinecraftDirectory(
                entry.CapabilityAccess.GameRootRelativePath!,
                cancellationToken);
            var retainedVolume = entry.FileSystem.InspectVolume(
                retainedGameRoot,
                cancellationToken);
            if (!EntryMatchesLiveProof(
                    entry,
                    retainedGameRoot,
                    retainedVolume))
            {
                diagnostic = Stale(entry.Instance.Id);
                return false;
            }

            using var guard = new Pcl2ReadPathGuard(
                entry.FileSystem,
                entry.CapabilityAccess.RootAccess,
                cancellationToken);
            using var gameRoot = guard.OpenMinecraftDirectory(
                entry.CapabilityAccess.GameRootRelativePath!,
                cancellationToken);
            var volume = entry.FileSystem.InspectVolume(
                gameRoot,
                cancellationToken);
            if (!EntryMatchesLiveProof(entry, gameRoot, volume))
            {
                diagnostic = Stale(entry.Instance.Id);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableCapabilityFailure(exception))
        {
            diagnostic = Stale(entry.Instance.Id);
            return false;
        }
    }

    private static bool EntryMatchesLiveProof(
        DiscoverySessionEntry entry,
        IVerifiedDirectoryHandle gameRoot,
        VolumeCapabilitySnapshot volume) =>
        gameRoot.Identity == entry.GameRootSnapshot.Identity &&
        gameRoot.Identity == entry.CapabilityAccess.GameRootIdentity &&
        gameRoot.IsLocalVolume &&
        !gameRoot.IsNetworkRedirected &&
        volume.IsLocalVolume &&
        !volume.IsNetworkRedirected &&
        volume.SupportsPersistentAcls &&
        !string.IsNullOrWhiteSpace(volume.FileSystemName) &&
        Pcl2PathNormalizer.AreEquivalent(
            gameRoot.FinalPath,
            entry.GameRootSnapshot.CanonicalPath);

    private string CreateChoiceProofLocked(DiscoverySessionEntry entry)
    {
        var snapshot = entry.GameRootSnapshot;
        var rootAccess = entry.CapabilityAccess.RootAccess;
        var material = string.Join(
            '\u001F',
            entry.Instance.Id,
            entry.Instance.DiscoveryProof,
            ((int)entry.Instance.Isolation).ToString(global::System.Globalization.CultureInfo.InvariantCulture),
            snapshot.Identity.VolumeSerialNumber.ToString("X16", global::System.Globalization.CultureInfo.InvariantCulture),
            snapshot.Identity.FileIdHigh.ToString("X16", global::System.Globalization.CultureInfo.InvariantCulture),
            snapshot.Identity.FileIdLow.ToString("X16", global::System.Globalization.CultureInfo.InvariantCulture),
            rootAccess.ApprovedRootIdentity.VolumeSerialNumber.ToString("X16", global::System.Globalization.CultureInfo.InvariantCulture),
            rootAccess.ApprovedRootIdentity.FileIdHigh.ToString("X16", global::System.Globalization.CultureInfo.InvariantCulture),
            rootAccess.ApprovedRootIdentity.FileIdLow.ToString("X16", global::System.Globalization.CultureInfo.InvariantCulture),
            rootAccess.MinecraftRootIdentity.VolumeSerialNumber.ToString("X16", global::System.Globalization.CultureInfo.InvariantCulture),
            rootAccess.MinecraftRootIdentity.FileIdHigh.ToString("X16", global::System.Globalization.CultureInfo.InvariantCulture),
            rootAccess.MinecraftRootIdentity.FileIdLow.ToString("X16", global::System.Globalization.CultureInfo.InvariantCulture),
            rootAccess.CandidateProofId.ToString("N"));
        var payload = Encoding.UTF8.GetBytes(material);
        var domain = Encoding.ASCII.GetBytes("blockferry.discovery.choice.v1");
        try
        {
            return ComputeTagLocked(domain, payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(domain);
        }
    }

    private string ComputeTagLocked(
        ReadOnlySpan<byte> domain,
        ReadOnlySpan<byte> payload)
    {
        var material = new byte[
            OpaqueTagPrefix.Length +
            sizeof(long) +
            sizeof(int) +
            domain.Length +
            sizeof(int) +
            payload.Length];
        var offset = 0;
        OpaqueTagPrefix.CopyTo(material, offset);
        offset += OpaqueTagPrefix.Length;
        BinaryPrimitives.WriteInt64BigEndian(
            material.AsSpan(offset, sizeof(long)),
            Generation);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt32BigEndian(
            material.AsSpan(offset, sizeof(int)),
            domain.Length);
        offset += sizeof(int);
        domain.CopyTo(material.AsSpan(offset, domain.Length));
        offset += domain.Length;
        BinaryPrimitives.WriteInt32BigEndian(
            material.AsSpan(offset, sizeof(int)),
            payload.Length);
        offset += sizeof(int);
        payload.CopyTo(material.AsSpan(offset, payload.Length));
        try
        {
            var digest = HMACSHA256.HashData(generationKey, material);
            try
            {
                return Convert.ToBase64String(digest)
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_');
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private bool ChoiceProofIsCurrentLocked(DiscoverySessionEntry entry)
    {
        var expected = CreateChoiceProofLocked(entry);
        return FixedTimeEqualsBase64Url(expected, entry.Choice.ProofToken);
    }

    private static bool FixedTimeEqualsBase64Url(string expected, string actual)
    {
        try
        {
            var expectedBytes = Encoding.ASCII.GetBytes(expected);
            var actualBytes = Encoding.ASCII.GetBytes(actual);
            try
            {
                return CryptographicOperations.FixedTimeEquals(
                    expectedBytes,
                    actualBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedBytes);
                CryptographicOperations.ZeroMemory(actualBytes);
            }
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private Pcl2Diagnostic RejectionOrUnavailable(string instanceId) =>
        rejections.TryGetValue(instanceId, out var rejection)
            ? rejection
            : Diagnostic(
                Pcl2DiagnosticCode.DiscoveryInstanceUnavailable,
                "The selected discovery instance is unavailable.",
                instanceId);

    private static bool IsBoundedInstanceId(string? instanceId) =>
        !string.IsNullOrEmpty(instanceId) && instanceId.Length <= 128;

    private static bool IsRecoverableCapabilityFailure(Exception exception) =>
        exception is CapabilityBoundaryException or
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            InvalidOperationException or
            ObjectDisposedException;

    private static Exception? DisposeEntries(
        IEnumerable<DiscoverySessionEntry> sessionEntries)
    {
        Exception? firstFailure = null;
        foreach (var entry in sessionEntries)
        {
            try
            {
                entry.Dispose();
            }
            catch (Exception exception)
            {
                firstFailure ??= exception;
            }
        }

        return firstFailure;
    }

    private static DiscoveryPairValidation Invalid(
        bool isStale,
        Pcl2Diagnostic diagnostic) =>
        new(
            false,
            isStale,
            null,
            Array.AsReadOnly([diagnostic]));

    private static Pcl2Diagnostic Stale(string instanceId) =>
        Diagnostic(
            Pcl2DiagnosticCode.DiscoveryRootStale,
            "A selected game root changed after discovery.",
            instanceId);

    private static Pcl2Diagnostic Diagnostic(
        Pcl2DiagnosticCode code,
        string message,
        string? instanceId = null) =>
        new(
            code,
            Pcl2DiagnosticSeverity.Error,
            message,
            null,
            IsSafeInstanceId(instanceId) ? instanceId : null);

    private static bool IsSafeInstanceId(string? value) =>
        value is { Length: > 0 and <= 64 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
}

internal sealed class DiscoverySessionEntry(
    Pcl2Instance instance,
    Pcl2InstanceCapabilityAccess capabilityAccess,
    IFileSystemCapability fileSystem,
    Pcl2ReadPathGuard retainedPathGuard,
    IVerifiedDirectoryHandle retainedGameRoot,
    VerifiedDirectorySnapshot gameRootSnapshot) : IDisposable
{
    private int disposed;

    public Pcl2Instance Instance { get; } = instance;
    public Pcl2InstanceCapabilityAccess CapabilityAccess { get; } = capabilityAccess;
    public IFileSystemCapability FileSystem { get; } = fileSystem;
    public Pcl2ReadPathGuard RetainedPathGuard { get; } = retainedPathGuard;
    public IVerifiedDirectoryHandle RetainedGameRoot { get; } = retainedGameRoot;
    public VerifiedDirectorySnapshot GameRootSnapshot { get; } = gameRootSnapshot;
    public DiscoveredInstanceChoice Choice { get; private set; } = null!;

    public void SetChoice(DiscoveredInstanceChoice choice)
    {
        if (Choice is not null)
        {
            throw new InvalidOperationException(
                "A discovery choice can only be initialized once.");
        }

        Choice = choice;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Exception? firstFailure = null;
        try
        {
            RetainedGameRoot.Dispose();
        }
        catch (Exception exception)
        {
            firstFailure = exception;
        }

        try
        {
            RetainedPathGuard.Dispose();
        }
        catch (Exception exception)
        {
            firstFailure ??= exception;
        }

        if (firstFailure is not null)
        {
            global::System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(firstFailure)
                .Throw();
        }
    }
}
