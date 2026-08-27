using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Transactions;

internal interface ITransactionStorageDirectory : IDisposable
{
    TransactionId TransactionId { get; }

    long GetAvailableBytes(CancellationToken cancellationToken);

    IReadOnlyList<string> ListNames(CancellationToken cancellationToken);

    void CreateDirectory(string opaqueName, CancellationToken cancellationToken);

    void CreateNewFile(
        string opaqueName,
        ReadOnlySpan<byte> bytes,
        int maximumBytes,
        CancellationToken cancellationToken);

    void AppendAndFlush(
        string opaqueName,
        ReadOnlySpan<byte> bytes,
        int maximumTotalBytes,
        CancellationToken cancellationToken);

    byte[] ReadFile(
        string opaqueName,
        int maximumBytes,
        CancellationToken cancellationToken);

    void CreateNewFileInDirectory(
        string directoryName,
        string opaqueName,
        ReadOnlySpan<byte> bytes,
        int maximumBytes,
        CancellationToken cancellationToken);

    byte[] ReadFileInDirectory(
        string directoryName,
        string opaqueName,
        int maximumBytes,
        CancellationToken cancellationToken);

    void DeleteBootstrapArtifacts(CancellationToken cancellationToken);
}

internal sealed class AuthenticatedTransactionStore : IDisposable
{
    private const int KeyLength = 32;
    private const int MaximumProtectedKeyBytes = 16 * 1024;
    private const int MaximumProtectedPayloadBytes = 512 * 1024;
    private const int MaximumManifestBytes = 16 * 1024 * 1024;
    private const int MaximumManifestRecords = 10_000;
    private const string KeyName = "key.dpapi";
    private const string LocatorName = "recovery-locator.dpapi";
    private const string PlanName = "plan.dpapi";
    private const string JournalName = "journal.log";
    private const string ManifestName = "manifest.log";
    private const string BeforeDirectoryName = "before";
    private readonly ITransactionStorageDirectory storage;
    private readonly byte[] key;
    private bool disposed;

    private AuthenticatedTransactionStore(
        ITransactionStorageDirectory storage,
        byte[] key,
        RecoveryLocator locator,
        StoredMigrationPlan plan)
    {
        this.storage = storage;
        this.key = key;
        Locator = locator;
        Plan = plan;
        Journal = new TransactionJournal(storage, key);
    }

    internal TransactionId TransactionId => storage.TransactionId;

    internal RecoveryLocator Locator { get; }

    internal StoredMigrationPlan Plan { get; }

    internal TransactionJournal Journal { get; }

    internal ITransactionStorageDirectory Storage => storage;

    internal void AppendManifestRecord(
        string opaqueObjectId,
        string contentSha256,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        TransactionValueValidation.RequireOpaqueId(opaqueObjectId, nameof(opaqueObjectId));
        TransactionValueValidation.RequireSha256(contentSha256, nameof(contentSha256));
        var current = storage.ReadFile(ManifestName, MaximumManifestBytes, cancellationToken);
        try
        {
            var state = VerifyManifest(current.ToArray(), storage.TransactionId, key);
            if (state.Sequence >= MaximumManifestRecords)
            {
                throw new IOException("The authenticated manifest record count exceeded its bound.");
            }

            var record = EncodeManifestRecord(
                storage.TransactionId,
                checked(state.Sequence + 1),
                opaqueObjectId,
                contentSha256,
                state.LastMac,
                key);
            try
            {
                storage.AppendAndFlush(
                    ManifestName,
                    record,
                    MaximumManifestBytes,
                    cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(record);
                CryptographicOperations.ZeroMemory(state.LastMac);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(current);
        }
    }

    internal static AuthenticatedTransactionStore Bootstrap(
        AppStorageGuard appStorage,
        RecoveryLocator locator,
        StoredMigrationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(appStorage);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(plan);
        var storage = appStorage.CreateTransactionStorage(locator.TransactionId, cancellationToken);
        try
        {
            return Bootstrap(
                storage,
                new WindowsCurrentUserProtectedData(),
                locator,
                plan,
                cancellationToken);
        }
        catch
        {
            storage.Dispose();
            throw;
        }
    }

    internal static AuthenticatedTransactionStore Bootstrap(
        ITransactionStorageDirectory storage,
        IProtectedData protectedData,
        RecoveryLocator locator,
        StoredMigrationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(protectedData);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(plan);
        TransactionValueValidation.RequireId(storage.TransactionId);
        if (locator.TransactionId != storage.TransactionId || plan.TransactionId != storage.TransactionId)
        {
            throw new ArgumentException("All transaction bootstrap objects must share one immutable ID.");
        }

        var key = RandomNumberGenerator.GetBytes(KeyLength);
        try
        {
            storage.CreateDirectory(BeforeDirectoryName, cancellationToken);
            WriteProtected(
                storage,
                protectedData,
                KeyName,
                EncodeKey(storage.TransactionId, key),
                KeyName,
                MaximumProtectedKeyBytes,
                cancellationToken);
            WriteProtected(
                storage,
                protectedData,
                LocatorName,
                RecoveryLocatorCodec.Encode(locator),
                LocatorName,
                MaximumProtectedPayloadBytes,
                cancellationToken);
            WriteProtected(
                storage,
                protectedData,
                PlanName,
                RecoveryLocatorCodec.Encode(plan),
                PlanName,
                MaximumProtectedPayloadBytes,
                cancellationToken);

            var journal = TransactionJournal.CreatePreparedPayload(
                storage.TransactionId,
                plan.AcceptedPlanDigest,
                key);
            try
            {
                storage.CreateNewFile(
                    JournalName,
                    journal,
                    TransactionJournalCodec.MaximumJournalBytes,
                    cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(journal);
            }

            var manifest = CreateEmptyManifest(storage.TransactionId, key);
            try
            {
                storage.CreateNewFile(
                    ManifestName,
                    manifest,
                    MaximumProtectedPayloadBytes,
                    cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(manifest);
            }

            var result = new AuthenticatedTransactionStore(
                storage,
                key.ToArray(),
                locator,
                plan);
            try
            {
                _ = result.Journal.ReadAndVerify(storage.TransactionId, cancellationToken);
                VerifyManifest(
                    storage.ReadFile(ManifestName, MaximumProtectedPayloadBytes, cancellationToken),
                    storage.TransactionId,
                    key);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }
        catch
        {
            try
            {
                storage.DeleteBootstrapArtifacts(CancellationToken.None);
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    "Transaction bootstrap failed and its fresh app-storage directory could not be fully cleaned.",
                    cleanupException);
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    internal static AuthenticatedTransactionStore Open(
        ITransactionStorageDirectory storage,
        IProtectedData protectedData,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(protectedData);
        var keyPayload = ReadProtected(
            storage,
            protectedData,
            KeyName,
            KeyName,
            MaximumProtectedKeyBytes,
            cancellationToken);
        byte[] key;
        try
        {
            key = DecodeKey(keyPayload, storage.TransactionId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyPayload);
        }
        try
        {
            var locatorPayload = ReadProtected(
                storage,
                protectedData,
                LocatorName,
                LocatorName,
                MaximumProtectedPayloadBytes,
                cancellationToken);
            RecoveryLocator locator;
            try
            {
                locator = RecoveryLocatorCodec.DecodeLocator(locatorPayload, storage.TransactionId);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(locatorPayload);
            }

            var planPayload = ReadProtected(
                storage,
                protectedData,
                PlanName,
                PlanName,
                MaximumProtectedPayloadBytes,
                cancellationToken);
            StoredMigrationPlan plan;
            try
            {
                plan = RecoveryLocatorCodec.DecodePlan(planPayload, storage.TransactionId);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(planPayload);
            }

            VerifyManifest(
                storage.ReadFile(ManifestName, MaximumProtectedPayloadBytes, cancellationToken),
                storage.TransactionId,
                key);
            var result = new AuthenticatedTransactionStore(
                storage,
                key.ToArray(),
                locator,
                plan);
            try
            {
                _ = result.Journal.ReadAndVerify(storage.TransactionId, cancellationToken);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    internal static AuthenticatedTransactionStore Open(
        AppStorageGuard appStorage,
        TransactionId transactionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(appStorage);
        var storage = appStorage.OpenTransactionStorage(transactionId, cancellationToken);
        try
        {
            return Open(storage, new WindowsCurrentUserProtectedData(), cancellationToken);
        }
        catch
        {
            storage.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Journal.Dispose();
        CryptographicOperations.ZeroMemory(key);
        storage.Dispose();
    }

    private static void WriteProtected(
        ITransactionStorageDirectory storage,
        IProtectedData protectedData,
        string fileName,
        byte[] plaintext,
        string label,
        int maximumCiphertextBytes,
        CancellationToken cancellationToken)
    {
        var entropy = CreateEntropy(storage.TransactionId, label);
        byte[]? ciphertext = null;
        try
        {
            ciphertext = protectedData.Protect(plaintext, entropy, maximumCiphertextBytes);
            if (ciphertext.Length == 0 || ciphertext.Length > maximumCiphertextBytes)
            {
                throw new TransactionAuthenticationException("A protected bootstrap payload exceeded its bound.");
            }

            storage.CreateNewFile(fileName, ciphertext, maximumCiphertextBytes, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(entropy);
            if (ciphertext is not null)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }
        }
    }

    private static byte[] ReadProtected(
        ITransactionStorageDirectory storage,
        IProtectedData protectedData,
        string fileName,
        string label,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var ciphertext = storage.ReadFile(fileName, maximumBytes, cancellationToken);
        var entropy = CreateEntropy(storage.TransactionId, label);
        try
        {
            var plaintext = protectedData.Unprotect(ciphertext, entropy, maximumBytes);
            if (plaintext.Length == 0 || plaintext.Length > maximumBytes)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new TransactionAuthenticationException("A decrypted transaction payload exceeded its bound.");
            }

            return plaintext;
        }
        catch (Exception exception) when (exception is CryptographicException or ProtectedDataLimitException)
        {
            throw new TransactionAuthenticationException("A protected transaction payload could not be authenticated.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    private static byte[] CreateEntropy(TransactionId transactionId, string label)
    {
        var text = Encoding.UTF8.GetBytes($"BlockFerry.Transaction.v1|{transactionId.Value:N}|{label}");
        try
        {
            return SHA256.HashData(text);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(text);
        }
    }

    private static byte[] EncodeKey(TransactionId transactionId, ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyLength)
        {
            throw new ArgumentException("A 256-bit transaction key is required.", nameof(key));
        }

        var payload = new byte[8 + sizeof(int) + 16 + KeyLength];
        "BFTKEY01"u8.CopyTo(payload);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), 1);
        transactionId.Value.TryWriteBytes(payload.AsSpan(12, 16));
        key.CopyTo(payload.AsSpan(28));
        return payload;
    }

    private static byte[] DecodeKey(ReadOnlySpan<byte> payload, TransactionId expectedTransactionId)
    {
        if (payload.Length != 8 + sizeof(int) + 16 + KeyLength ||
            !payload[..8].SequenceEqual("BFTKEY01"u8) ||
            BinaryPrimitives.ReadInt32LittleEndian(payload[8..]) != 1 ||
            new Guid(payload.Slice(12, 16)) != expectedTransactionId.Value)
        {
            throw new TransactionAuthenticationException("The protected transaction key header was invalid.");
        }

        return payload[28..].ToArray();
    }

    private static byte[] CreateEmptyManifest(TransactionId transactionId, ReadOnlySpan<byte> key)
    {
        var signed = new byte[8 + sizeof(int) + 16 + sizeof(int)];
        "BFMAN001"u8.CopyTo(signed);
        BinaryPrimitives.WriteInt32LittleEndian(signed.AsSpan(8), 1);
        transactionId.Value.TryWriteBytes(signed.AsSpan(12, 16));
        BinaryPrimitives.WriteInt32LittleEndian(signed.AsSpan(28), 0);
        var mac = HMACSHA256.HashData(key, signed);
        try
        {
            var result = new byte[signed.Length + mac.Length];
            signed.CopyTo(result, 0);
            mac.CopyTo(result, signed.Length);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signed);
            CryptographicOperations.ZeroMemory(mac);
        }
    }

    private static ManifestState VerifyManifest(
        byte[] payload,
        TransactionId expectedTransactionId,
        ReadOnlySpan<byte> key)
    {
        try
        {
            const int signedLength = 8 + sizeof(int) + 16 + sizeof(int);
            if (payload.Length < signedLength + 32 || payload.Length > MaximumManifestBytes ||
                !payload.AsSpan(0, 8).SequenceEqual("BFMAN001"u8) ||
                BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8)) != 1 ||
                new Guid(payload.AsSpan(12, 16)) != expectedTransactionId.Value ||
                BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(28)) != 0)
            {
                throw new TransactionAuthenticationException("The transaction manifest header was invalid.");
            }

            var computed = HMACSHA256.HashData(key, payload.AsSpan(0, signedLength));
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(
                        computed,
                        payload.AsSpan(signedLength, 32)))
                {
                    throw new TransactionAuthenticationException("The transaction manifest MAC was invalid.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(computed);
            }

            var offset = signedLength + 32;
            var sequence = 0;
            var previousMac = payload.AsSpan(signedLength, 32).ToArray();
            try
            {
                while (offset < payload.Length)
                {
                    if (++sequence > MaximumManifestRecords)
                    {
                        throw new TransactionAuthenticationException("The transaction manifest record count exceeded its bound.");
                    }

                    var recordStart = offset;
                    RequireManifestBytes(payload, ref offset, "BFMREC01"u8);
                    if (ReadManifestInt32(payload, ref offset) != 1 ||
                        new Guid(ReadManifestBytes(payload, ref offset, 16)) != expectedTransactionId.Value ||
                        ReadManifestInt64(payload, ref offset) != sequence)
                    {
                        throw new TransactionAuthenticationException("A transaction manifest record header was invalid.");
                    }

                    var objectLength = ReadManifestInt32(payload, ref offset);
                    if (objectLength is <= 0 or > 128)
                    {
                        throw new TransactionAuthenticationException("A transaction manifest object ID exceeded its bound.");
                    }

                    string objectId;
                    try
                    {
                        objectId = new UTF8Encoding(false, true).GetString(
                            ReadManifestBytes(payload, ref offset, objectLength));
                        TransactionValueValidation.RequireOpaqueId(objectId, nameof(objectId));
                    }
                    catch (Exception exception) when (exception is DecoderFallbackException or ArgumentException)
                    {
                        throw new TransactionAuthenticationException("A transaction manifest object ID was invalid.", exception);
                    }

                    _ = ReadManifestBytes(payload, ref offset, 32);
                    var chainedMac = ReadManifestBytes(payload, ref offset, 32);
                    if (!CryptographicOperations.FixedTimeEquals(chainedMac, previousMac))
                    {
                        throw new TransactionAuthenticationException("The transaction manifest MAC chain was invalid.");
                    }

                    var recordSignedLength = offset - recordStart;
                    var recordMac = ReadManifestBytes(payload, ref offset, 32);
                    var computedRecordMac = HMACSHA256.HashData(
                        key,
                        payload.AsSpan(recordStart, recordSignedLength));
                    try
                    {
                        if (!CryptographicOperations.FixedTimeEquals(recordMac, computedRecordMac))
                        {
                            throw new TransactionAuthenticationException("A transaction manifest record MAC was invalid.");
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(computedRecordMac);
                    }

                    CryptographicOperations.ZeroMemory(previousMac);
                    previousMac = recordMac.ToArray();
                }

                return new ManifestState(sequence, previousMac.ToArray());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(previousMac);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static byte[] EncodeManifestRecord(
        TransactionId transactionId,
        int sequence,
        string opaqueObjectId,
        string contentSha256,
        ReadOnlySpan<byte> previousMac,
        ReadOnlySpan<byte> key)
    {
        var objectBytes = Encoding.UTF8.GetBytes(opaqueObjectId);
        var digest = Convert.FromHexString(contentSha256);
        try
        {
            var signedLength = checked(
                8 + sizeof(int) + 16 + sizeof(long) + sizeof(int) + objectBytes.Length + 32 + 32);
            var result = new byte[checked(signedLength + 32)];
            var offset = 0;
            "BFMREC01"u8.CopyTo(result.AsSpan(offset));
            offset += 8;
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), 1);
            offset += sizeof(int);
            transactionId.Value.TryWriteBytes(result.AsSpan(offset, 16));
            offset += 16;
            BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(offset), sequence);
            offset += sizeof(long);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), objectBytes.Length);
            offset += sizeof(int);
            objectBytes.CopyTo(result, offset);
            offset += objectBytes.Length;
            digest.CopyTo(result, offset);
            offset += 32;
            previousMac.CopyTo(result.AsSpan(offset));
            offset += 32;
            var mac = HMACSHA256.HashData(key, result.AsSpan(0, signedLength));
            try
            {
                mac.CopyTo(result, offset);
                return result;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(mac);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(objectBytes);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static ReadOnlySpan<byte> ReadManifestBytes(byte[] payload, ref int offset, int count)
    {
        if (count < 0 || count > payload.Length - offset)
        {
            throw new TransactionAuthenticationException("The transaction manifest was truncated.");
        }

        var result = payload.AsSpan(offset, count);
        offset += count;
        return result;
    }

    private static void RequireManifestBytes(
        byte[] payload,
        ref int offset,
        ReadOnlySpan<byte> expected)
    {
        if (!ReadManifestBytes(payload, ref offset, expected.Length).SequenceEqual(expected))
        {
            throw new TransactionAuthenticationException("A transaction manifest record header was invalid.");
        }
    }

    private static int ReadManifestInt32(byte[] payload, ref int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(ReadManifestBytes(payload, ref offset, sizeof(int)));

    private static long ReadManifestInt64(byte[] payload, ref int offset) =>
        BinaryPrimitives.ReadInt64LittleEndian(ReadManifestBytes(payload, ref offset, sizeof(long)));

    private sealed record ManifestState(int Sequence, byte[] LastMac);
}
