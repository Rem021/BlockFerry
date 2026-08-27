using System.Buffers.Binary;
using System.Security.Cryptography;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Transactions;

internal enum AppStorageRecoveryKind : byte
{
    ReplaceExisting = 1,
    ReplaceMissing = 2,
    Clear = 3,
}

internal sealed class AppStorageRecoveryManifest
{
    public AppStorageRecoveryManifest(
        Guid transactionId,
        AppStorageRecoveryKind kind,
        PhysicalDirectoryIdentity appRootIdentity,
        PhysicalFileIdentity? stagedIdentity,
        long stagedLength,
        byte[]? stagedSha256,
        PhysicalFileIdentity? oldIdentity,
        long oldLength,
        byte[]? oldSha256)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("A recovery transaction ID must not be empty.", nameof(transactionId));
        }

        ValidateHash(stagedIdentity, stagedLength, stagedSha256, nameof(stagedSha256));
        ValidateHash(oldIdentity, oldLength, oldSha256, nameof(oldSha256));
        if ((kind is AppStorageRecoveryKind.ReplaceExisting &&
                (stagedIdentity is null || oldIdentity is null)) ||
            (kind is AppStorageRecoveryKind.ReplaceMissing &&
                (stagedIdentity is null || oldIdentity is not null)) ||
            (kind is AppStorageRecoveryKind.Clear &&
                (stagedIdentity is not null || oldIdentity is null)))
        {
            throw new ArgumentException("Recovery transaction fields did not match their operation kind.", nameof(kind));
        }

        TransactionId = transactionId;
        Kind = kind;
        AppRootIdentity = appRootIdentity;
        StagedIdentity = stagedIdentity;
        StagedLength = stagedLength;
        stagedHash = stagedSha256 is null ? null : (byte[])stagedSha256.Clone();
        OldIdentity = oldIdentity;
        OldLength = oldLength;
        oldHash = oldSha256 is null ? null : (byte[])oldSha256.Clone();
    }

    private readonly byte[]? stagedHash;
    private readonly byte[]? oldHash;

    public Guid TransactionId { get; }
    public AppStorageRecoveryKind Kind { get; }
    public PhysicalDirectoryIdentity AppRootIdentity { get; }
    public PhysicalFileIdentity? StagedIdentity { get; }
    public long StagedLength { get; }
    public PhysicalFileIdentity? OldIdentity { get; }
    public long OldLength { get; }
    public ReadOnlySpan<byte> StagedSha256 => stagedHash;
    public ReadOnlySpan<byte> OldSha256 => oldHash;

    private static void ValidateHash(
        PhysicalFileIdentity? identity,
        long length,
        byte[]? hash,
        string parameterName)
    {
        if (identity is null)
        {
            if (length != 0 || hash is not null)
            {
                throw new ArgumentException("An absent recovery object must have no length or hash.", parameterName);
            }

            return;
        }

        if (length < 0 || hash?.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException("A recovery object requires a nonnegative length and SHA-256 hash.", parameterName);
        }
    }
}

internal static class AppStorageRecoveryManifestCodec
{
    private const int EncodedLength = 216;
    private const uint Version = 1;
    private const byte HasStage = 0x01;
    private const byte HasOld = 0x02;
    private static readonly byte[] Magic = "BFRCV001"u8.ToArray();
    private static readonly byte[] TargetNameHash =
        SHA256.HashData("discovery-roots.json"u8);

    public static byte[] Encode(AppStorageRecoveryManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var bytes = new byte[EncodedLength];
        var span = bytes.AsSpan();
        Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], Version);
        span[12] = (byte)manifest.Kind;
        span[13] = (byte)(
            (manifest.StagedIdentity is null ? 0 : HasStage) |
            (manifest.OldIdentity is null ? 0 : HasOld));
        manifest.TransactionId.TryWriteBytes(span[16..32]);
        WriteDirectoryIdentity(span[32..56], manifest.AppRootIdentity);
        WriteFile(
            span[56..120],
            manifest.StagedIdentity,
            manifest.StagedLength,
            manifest.StagedSha256);
        WriteFile(
            span[120..184],
            manifest.OldIdentity,
            manifest.OldLength,
            manifest.OldSha256);
        TargetNameHash.CopyTo(span[184..216]);
        return bytes;
    }

    public static AppStorageRecoveryManifest Decode(
        ReadOnlySpan<byte> bytes,
        PhysicalDirectoryIdentity expectedAppRootIdentity)
    {
        if (bytes.Length != EncodedLength ||
            !bytes[..8].SequenceEqual(Magic) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != Version ||
            bytes[14] != 0 ||
            bytes[15] != 0 ||
            !bytes[184..216].SequenceEqual(TargetNameHash))
        {
            throw new CryptographicException("The recovery manifest header was invalid.");
        }

        var kind = (AppStorageRecoveryKind)bytes[12];
        if (!Enum.IsDefined(kind))
        {
            throw new CryptographicException("The recovery manifest operation was invalid.");
        }

        var flags = bytes[13];
        if ((flags & ~(HasStage | HasOld)) != 0)
        {
            throw new CryptographicException("The recovery manifest flags were invalid.");
        }

        var transactionId = new Guid(bytes[16..32]);
        var appRootIdentity = ReadDirectoryIdentity(bytes[32..56]);
        if (transactionId == Guid.Empty || appRootIdentity != expectedAppRootIdentity)
        {
            throw new CryptographicException("The recovery manifest identity binding was invalid.");
        }

        var staged = ReadFile(bytes[56..120], (flags & HasStage) != 0);
        var old = ReadFile(bytes[120..184], (flags & HasOld) != 0);
        try
        {
            return new AppStorageRecoveryManifest(
                transactionId,
                kind,
                appRootIdentity,
                staged.Identity,
                staged.Length,
                staged.Hash,
                old.Identity,
                old.Length,
                old.Hash);
        }
        catch (ArgumentException exception)
        {
            throw new CryptographicException("The recovery manifest shape was invalid.", exception);
        }
    }

    private static void WriteDirectoryIdentity(
        Span<byte> destination,
        PhysicalDirectoryIdentity identity)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, identity.VolumeSerialNumber);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], identity.FileIdLow);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], identity.FileIdHigh);
    }

    private static PhysicalDirectoryIdentity ReadDirectoryIdentity(ReadOnlySpan<byte> source) =>
        new(
            BinaryPrimitives.ReadUInt64LittleEndian(source),
            BinaryPrimitives.ReadUInt64LittleEndian(source[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[16..]));

    private static void WriteFile(
        Span<byte> destination,
        PhysicalFileIdentity? identity,
        long length,
        ReadOnlySpan<byte> hash)
    {
        if (identity is null)
        {
            destination.Clear();
            return;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(destination, identity.Value.VolumeSerialNumber);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], identity.Value.FileIdLow);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], identity.Value.FileIdHigh);
        BinaryPrimitives.WriteInt64LittleEndian(destination[24..], length);
        hash.CopyTo(destination[32..64]);
    }

    private static (PhysicalFileIdentity? Identity, long Length, byte[]? Hash) ReadFile(
        ReadOnlySpan<byte> source,
        bool present)
    {
        if (!present)
        {
            if (!source.SequenceEqual(new byte[source.Length]))
            {
                throw new CryptographicException("An absent recovery object carried unexpected data.");
            }

            return (null, 0, null);
        }

        var length = BinaryPrimitives.ReadInt64LittleEndian(source[24..]);
        if (length < 0)
        {
            throw new CryptographicException("A recovery object length was invalid.");
        }

        return (
            new PhysicalFileIdentity(
                BinaryPrimitives.ReadUInt64LittleEndian(source),
                BinaryPrimitives.ReadUInt64LittleEndian(source[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(source[16..])),
            length,
            source[32..64].ToArray());
    }
}
