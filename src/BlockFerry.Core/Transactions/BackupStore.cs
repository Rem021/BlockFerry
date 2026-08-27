using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Transactions;

internal sealed class BackupStore
{
    private const string BeforeDirectoryName = "before";
    private const int MaximumBackupBytes = 256 * 1024 * 1024;
    private const int MaximumMetadataBytes = 1024 * 1024;
    private readonly AuthenticatedTransactionStore store;
    private readonly IProtectedData protectedData;

    internal BackupStore(AuthenticatedTransactionStore store)
        : this(store, new WindowsCurrentUserProtectedData())
    {
    }

    internal BackupStore(
        AuthenticatedTransactionStore store,
        IProtectedData protectedData)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.protectedData = protectedData ?? throw new ArgumentNullException(nameof(protectedData));
    }

    internal void WriteVerified(
        string opaqueObjectId,
        ReadOnlySpan<byte> bytes,
        FileMetadataSnapshot metadata,
        CancellationToken cancellationToken)
    {
        TransactionValueValidation.RequireOpaqueId(opaqueObjectId, nameof(opaqueObjectId));
        ArgumentNullException.ThrowIfNull(metadata);
        if (bytes.Length > MaximumBackupBytes ||
            bytes.Length != metadata.Length ||
            !string.Equals(
                Convert.ToHexString(SHA256.HashData(bytes)),
                metadata.Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The backup bytes did not match their verified metadata.");
        }

        const long safetyMargin = 16L * 1024 * 1024;
        if (store.Storage.GetAvailableBytes(cancellationToken) < bytes.Length + safetyMargin)
        {
            throw new IOException("The authenticated backup volume does not have enough verified free space.");
        }

        var bytesName = opaqueObjectId + ".bin";
        var metadataName = opaqueObjectId + ".meta.dpapi";
        store.Storage.CreateNewFileInDirectory(
            BeforeDirectoryName,
            bytesName,
            bytes,
            MaximumBackupBytes,
            cancellationToken);
        var rereadBytes = store.Storage.ReadFileInDirectory(
            BeforeDirectoryName,
            bytesName,
            MaximumBackupBytes,
            cancellationToken);
        try
        {
            if (rereadBytes.Length != bytes.Length ||
                !CryptographicOperations.FixedTimeEquals(rereadBytes, bytes))
            {
                throw new IOException("The durable backup bytes did not reread exactly.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rereadBytes);
        }

        var plaintext = BackupMetadataCodec.Encode(store.TransactionId, opaqueObjectId, metadata);
        var entropy = CreateEntropy(store.TransactionId, opaqueObjectId);
        byte[]? ciphertext = null;
        try
        {
            ciphertext = protectedData.Protect(plaintext, entropy, MaximumMetadataBytes);
            store.Storage.CreateNewFileInDirectory(
                BeforeDirectoryName,
                metadataName,
                ciphertext,
                MaximumMetadataBytes,
                cancellationToken);
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

        var verified = Read(opaqueObjectId, cancellationToken);
        try
        {
            if (!metadata.SemanticallyEquals(verified.Metadata) ||
                !CryptographicOperations.FixedTimeEquals(bytes, verified.Bytes))
            {
                throw new IOException("The durable backup object failed its reread verification.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(verified.Bytes);
        }

        var manifestMaterial = Encoding.UTF8.GetBytes(metadata.Sha256 + metadata.MetadataDigest);
        try
        {
            store.AppendManifestRecord(
                opaqueObjectId,
                Convert.ToHexString(SHA256.HashData(manifestMaterial)),
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(manifestMaterial);
        }
    }

    internal BackupPayload Read(
        string opaqueObjectId,
        CancellationToken cancellationToken)
    {
        TransactionValueValidation.RequireOpaqueId(opaqueObjectId, nameof(opaqueObjectId));
        var bytes = store.Storage.ReadFileInDirectory(
            BeforeDirectoryName,
            opaqueObjectId + ".bin",
            MaximumBackupBytes,
            cancellationToken);
        var ciphertext = store.Storage.ReadFileInDirectory(
            BeforeDirectoryName,
            opaqueObjectId + ".meta.dpapi",
            MaximumMetadataBytes,
            cancellationToken);
        var entropy = CreateEntropy(store.TransactionId, opaqueObjectId);
        byte[]? plaintext = null;
        try
        {
            plaintext = protectedData.Unprotect(ciphertext, entropy, MaximumMetadataBytes);
            var metadata = BackupMetadataCodec.Decode(
                plaintext,
                store.TransactionId,
                opaqueObjectId);
            if (bytes.Length != metadata.Length ||
                !string.Equals(
                    Convert.ToHexString(SHA256.HashData(bytes)),
                    metadata.Sha256,
                    StringComparison.Ordinal))
            {
                throw new TransactionAuthenticationException("The persisted backup bytes did not match authenticated metadata.");
            }

            return new BackupPayload(bytes, metadata);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(entropy);
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private static byte[] CreateEntropy(TransactionId transactionId, string opaqueObjectId)
    {
        var bytes = Encoding.UTF8.GetBytes(
            $"BlockFerry.Transaction.Backup.v1|{transactionId.Value:N}|{opaqueObjectId}");
        try
        {
            return SHA256.HashData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

internal sealed record BackupPayload(byte[] Bytes, FileMetadataSnapshot Metadata) : IDisposable
{
    public void Dispose() => CryptographicOperations.ZeroMemory(Bytes);
}

internal static class BackupMetadataCodec
{
    private const int MaximumPayloadBytes = 1024 * 1024;
    private const int MaximumSecurityDescriptorBytes = 256 * 1024;
    private const int MaximumStreams = 64;
    private static ReadOnlySpan<byte> Magic => "BFBMETA1"u8;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Encode(
        TransactionId transactionId,
        string opaqueObjectId,
        FileMetadataSnapshot metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        using var stream = new MemoryStream(capacity: 4096);
        stream.Write(Magic);
        WriteInt32(stream, 1);
        stream.Write(transactionId.Value.ToByteArray());
        WriteText(stream, opaqueObjectId, 128);
        WriteUInt64(stream, metadata.Identity.VolumeSerialNumber);
        WriteUInt64(stream, metadata.Identity.FileIdLow);
        WriteUInt64(stream, metadata.Identity.FileIdHigh);
        WriteInt64(stream, metadata.Length);
        stream.Write(Convert.FromHexString(metadata.Sha256));
        WriteInt64(stream, metadata.CreationTimeUtc.UtcTicks);
        WriteInt64(stream, metadata.LastAccessTimeUtc.UtcTicks);
        WriteInt64(stream, metadata.LastWriteTimeUtc.UtcTicks);
        WriteUInt32(stream, (uint)metadata.Attributes);
        WriteUInt32(stream, metadata.LinkCount);
        if (metadata.SecurityDescriptor.Length is 0 or > MaximumSecurityDescriptorBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(metadata));
        }

        WriteInt32(stream, metadata.SecurityDescriptor.Length);
        stream.Write(metadata.SecurityDescriptor);
        if (metadata.StreamNames.Count > MaximumStreams)
        {
            throw new ArgumentOutOfRangeException(nameof(metadata));
        }

        WriteInt32(stream, metadata.StreamNames.Count);
        foreach (var name in metadata.StreamNames)
        {
            WriteText(stream, name, 1024);
        }

        if (stream.Length > MaximumPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(metadata));
        }

        return stream.ToArray();
    }

    internal static FileMetadataSnapshot Decode(
        ReadOnlySpan<byte> payload,
        TransactionId expectedTransactionId,
        string expectedOpaqueObjectId)
    {
        if (payload.Length == 0 || payload.Length > MaximumPayloadBytes)
        {
            throw new TransactionAuthenticationException("The backup metadata length was invalid.");
        }

        var offset = 0;
        Require(payload, ref offset, Magic);
        if (ReadInt32(payload, ref offset) != 1 ||
            new Guid(Read(payload, ref offset, 16)) != expectedTransactionId.Value ||
            !string.Equals(ReadText(payload, ref offset, 128), expectedOpaqueObjectId, StringComparison.Ordinal))
        {
            throw new TransactionAuthenticationException("The backup metadata header was invalid.");
        }

        var identity = new PhysicalFileIdentity(
            ReadUInt64(payload, ref offset),
            ReadUInt64(payload, ref offset),
            ReadUInt64(payload, ref offset));
        var length = ReadInt64(payload, ref offset);
        var sha256 = Convert.ToHexString(Read(payload, ref offset, 32));
        var creation = ReadInt64(payload, ref offset);
        var access = ReadInt64(payload, ref offset);
        var write = ReadInt64(payload, ref offset);
        var attributes = ReadUInt32(payload, ref offset);
        var links = ReadUInt32(payload, ref offset);
        var securityLength = ReadInt32(payload, ref offset);
        if (securityLength is <= 0 or > MaximumSecurityDescriptorBytes)
        {
            throw new TransactionAuthenticationException("The backup security descriptor exceeded its bound.");
        }

        var security = Read(payload, ref offset, securityLength).ToArray();
        var streamCount = ReadInt32(payload, ref offset);
        if (streamCount is < 0 or > MaximumStreams)
        {
            throw new TransactionAuthenticationException("The backup stream count exceeded its bound.");
        }

        var streams = new string[streamCount];
        for (var index = 0; index < streams.Length; index++)
        {
            streams[index] = ReadText(payload, ref offset, 1024);
        }

        if (offset != payload.Length || length < 0)
        {
            throw new TransactionAuthenticationException("The backup metadata payload was malformed.");
        }

        try
        {
            return new FileMetadataSnapshot(
                identity,
                length,
                sha256,
                new DateTimeOffset(creation, TimeSpan.Zero),
                new DateTimeOffset(access, TimeSpan.Zero),
                new DateTimeOffset(write, TimeSpan.Zero),
                (FileAttributes)attributes,
                links,
                security,
                Array.AsReadOnly(streams));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new TransactionAuthenticationException("The backup metadata timestamp was invalid.", exception);
        }
    }

    private static ReadOnlySpan<byte> Read(ReadOnlySpan<byte> payload, ref int offset, int count)
    {
        if (count < 0 || count > payload.Length - offset)
        {
            throw new TransactionAuthenticationException("The backup metadata was truncated.");
        }

        var result = payload.Slice(offset, count);
        offset += count;
        return result;
    }

    private static void Require(ReadOnlySpan<byte> payload, ref int offset, ReadOnlySpan<byte> expected)
    {
        if (!Read(payload, ref offset, expected.Length).SequenceEqual(expected))
        {
            throw new TransactionAuthenticationException("The backup metadata magic was invalid.");
        }
    }

    private static int ReadInt32(ReadOnlySpan<byte> payload, ref int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(Read(payload, ref offset, sizeof(int)));

    private static uint ReadUInt32(ReadOnlySpan<byte> payload, ref int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(Read(payload, ref offset, sizeof(uint)));

    private static long ReadInt64(ReadOnlySpan<byte> payload, ref int offset) =>
        BinaryPrimitives.ReadInt64LittleEndian(Read(payload, ref offset, sizeof(long)));

    private static ulong ReadUInt64(ReadOnlySpan<byte> payload, ref int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(Read(payload, ref offset, sizeof(ulong)));

    private static string ReadText(ReadOnlySpan<byte> payload, ref int offset, int maximumBytes)
    {
        var length = ReadInt32(payload, ref offset);
        if (length < 0 || length > maximumBytes)
        {
            throw new TransactionAuthenticationException("A backup metadata string exceeded its bound.");
        }

        try
        {
            return StrictUtf8.GetString(Read(payload, ref offset, length));
        }
        catch (DecoderFallbackException exception)
        {
            throw new TransactionAuthenticationException("A backup metadata string was malformed UTF-8.", exception);
        }
    }

    private static void WriteText(Stream stream, string value, int maximumBytes)
    {
        var bytes = StrictUtf8.GetBytes(value);
        try
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes.Length, maximumBytes, nameof(value));
            WriteInt32(stream, bytes.Length);
            stream.Write(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
