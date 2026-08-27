using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using BlockFerry.Core.Content;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Transactions;

public sealed class RecoveryLocator
{
    private RecoveryLocator(
        TransactionId transactionId,
        string targetInstanceId,
        string canonicalTargetRoot,
        PhysicalDirectoryIdentity targetRootIdentity)
    {
        TransactionId = transactionId;
        TargetInstanceId = targetInstanceId;
        CanonicalTargetRoot = canonicalTargetRoot;
        TargetRootIdentity = targetRootIdentity;
    }

    public TransactionId TransactionId { get; }

    public string TargetInstanceId { get; }

    public string CanonicalTargetRoot { get; }

    public PhysicalDirectoryIdentity TargetRootIdentity { get; }

    public static RecoveryLocator Create(
        TransactionId transactionId,
        string targetInstanceId,
        string canonicalTargetRoot,
        PhysicalDirectoryIdentity targetRootIdentity)
    {
        TransactionValueValidation.RequireId(transactionId);
        TransactionValueValidation.RequireBoundedText(
            targetInstanceId,
            nameof(targetInstanceId),
            maximumUtf16Length: 512,
            allowDirectorySeparators: false);
        TransactionValueValidation.RequireAbsolutePath(canonicalTargetRoot, nameof(canonicalTargetRoot));
        if (targetRootIdentity == default)
        {
            throw new ArgumentException("A physical target-root identity is required.", nameof(targetRootIdentity));
        }

        return new RecoveryLocator(
            transactionId,
            targetInstanceId,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonicalTargetRoot)),
            targetRootIdentity);
    }
}

public sealed class StoredPlanPath
{
    private StoredPlanPath(
        string adapterId,
        NormalizedRelativePath relativePath,
        ConflictResolution resolution,
        bool beforeExists,
        string expectedBeforeSha256,
        bool afterExists,
        string expectedAfterSha256)
    {
        AdapterId = adapterId;
        RelativePath = relativePath;
        Resolution = resolution;
        BeforeExists = beforeExists;
        ExpectedBeforeSha256 = expectedBeforeSha256;
        AfterExists = afterExists;
        ExpectedAfterSha256 = expectedAfterSha256;
    }

    public string AdapterId { get; }

    public NormalizedRelativePath RelativePath { get; }

    public ConflictResolution Resolution { get; }

    public bool BeforeExists { get; }

    public string ExpectedBeforeSha256 { get; }

    public bool AfterExists { get; }

    public string ExpectedAfterSha256 { get; }

    public static StoredPlanPath Create(
        string adapterId,
        NormalizedRelativePath relativePath,
        ConflictResolution resolution) =>
        Create(
            adapterId,
            relativePath,
            resolution,
            beforeExists: false,
            EmptySha256,
            afterExists: true,
            EmptySha256);

    public static StoredPlanPath Create(
        string adapterId,
        NormalizedRelativePath relativePath,
        ConflictResolution resolution,
        bool beforeExists,
        string expectedBeforeSha256,
        bool afterExists,
        string expectedAfterSha256)
    {
        TransactionValueValidation.RequireOpaqueId(adapterId, nameof(adapterId));
        ArgumentNullException.ThrowIfNull(relativePath);
        TransactionValueValidation.RequireSha256(
            expectedBeforeSha256,
            nameof(expectedBeforeSha256));
        TransactionValueValidation.RequireSha256(
            expectedAfterSha256,
            nameof(expectedAfterSha256));
        if (!WritePathGuard.TryNormalize(relativePath.Value, out var normalized) ||
            normalized is null ||
            normalized.Value.Length == 0 ||
            !Enum.IsDefined(resolution) ||
            resolution == ConflictResolution.Unresolved ||
            !beforeExists && !string.Equals(expectedBeforeSha256, EmptySha256, StringComparison.Ordinal) ||
            !afterExists && !string.Equals(expectedAfterSha256, EmptySha256, StringComparison.Ordinal))
        {
            throw new ArgumentException("A stored plan path must be safe and fully resolved.", nameof(relativePath));
        }

        return new StoredPlanPath(
            adapterId,
            normalized,
            resolution,
            beforeExists,
            expectedBeforeSha256,
            afterExists,
            expectedAfterSha256);
    }

    private static string EmptySha256 { get; } = Convert.ToHexString(SHA256.HashData([]));
}

public enum StoredTransactionPurpose
{
    Migration = 0,
    Undo = 1,
}

public sealed class StoredMigrationPlan
{
    private StoredMigrationPlan(
        TransactionId transactionId,
        string acceptedPlanDigest,
        IReadOnlyList<StoredPlanPath> paths,
        StoredTransactionPurpose purpose)
    {
        TransactionId = transactionId;
        AcceptedPlanDigest = acceptedPlanDigest;
        Paths = paths;
        Purpose = purpose;
    }

    public TransactionId TransactionId { get; }

    public string AcceptedPlanDigest { get; }

    public IReadOnlyList<StoredPlanPath> Paths { get; }

    public StoredTransactionPurpose Purpose { get; }

    public static StoredMigrationPlan Create(
        TransactionId transactionId,
        string acceptedPlanDigest,
        IEnumerable<StoredPlanPath> paths,
        StoredTransactionPurpose purpose = StoredTransactionPurpose.Migration)
    {
        TransactionValueValidation.RequireId(transactionId);
        TransactionValueValidation.RequireSha256(acceptedPlanDigest, nameof(acceptedPlanDigest));
        ArgumentNullException.ThrowIfNull(paths);
        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose));
        }

        var copy = paths.Take(ContentContractLimits.MaximumFileChanges + 1).ToArray();
        if (copy.Length is 0 or > ContentContractLimits.MaximumFileChanges ||
            copy.Any(path => path is null))
        {
            throw new ArgumentException("A bounded non-empty stored path set is required.", nameof(paths));
        }

        var normalized = new HashSet<NormalizedRelativePath>(NormalizedRelativePathComparer.Instance);
        foreach (var path in copy)
        {
            if (!WritePathGuard.TryNormalize(path.RelativePath.Value, out var checkedPath) ||
                checkedPath is null ||
                !normalized.Add(checkedPath))
            {
                throw new ArgumentException("Stored plan paths must be unique after Windows normalization.", nameof(paths));
            }
        }

        Array.Sort(copy, static (left, right) =>
        {
            var adapter = StringComparer.Ordinal.Compare(left.AdapterId, right.AdapterId);
            return adapter != 0
                ? adapter
                : StringComparer.OrdinalIgnoreCase.Compare(
                    WritePathGuard.CollisionKey(left.RelativePath),
                    WritePathGuard.CollisionKey(right.RelativePath));
        });
        return new StoredMigrationPlan(
            transactionId,
            acceptedPlanDigest,
            new ReadOnlyCollection<StoredPlanPath>(copy),
            purpose);
    }
}

internal static class RecoveryLocatorCodec
{
    private const int LocatorSchemaVersion = 1;
    private const int PlanSchemaVersion = 3;
    private const int LegacyPlanSchemaVersion = 2;
    private const int MaximumPayloadBytes = 256 * 1024;
    private static ReadOnlySpan<byte> LocatorMagic => "BFRLOC01"u8;
    private static ReadOnlySpan<byte> PlanMagic => "BFRPLN01"u8;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Encode(RecoveryLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        using var stream = new MemoryStream(capacity: 1024);
        stream.Write(LocatorMagic);
        WriteInt32(stream, LocatorSchemaVersion);
        WriteGuid(stream, locator.TransactionId.Value);
        WriteUInt64(stream, locator.TargetRootIdentity.VolumeSerialNumber);
        WriteUInt64(stream, locator.TargetRootIdentity.FileIdLow);
        WriteUInt64(stream, locator.TargetRootIdentity.FileIdHigh);
        WriteText(stream, locator.TargetInstanceId, 2048);
        WriteText(stream, locator.CanonicalTargetRoot, 128 * 1024);
        return Finish(stream);
    }

    internal static RecoveryLocator DecodeLocator(ReadOnlySpan<byte> payload, TransactionId expectedTransactionId)
    {
        var reader = new BoundedBinaryReader(payload, MaximumPayloadBytes);
        reader.RequireMagic(LocatorMagic);
        reader.RequireInt32(LocatorSchemaVersion);
        var transactionId = new TransactionId(reader.ReadGuid());
        if (transactionId != expectedTransactionId)
        {
            throw new TransactionAuthenticationException("The protected recovery locator belongs to another transaction.");
        }

        var identity = new PhysicalDirectoryIdentity(
            reader.ReadUInt64(),
            reader.ReadUInt64(),
            reader.ReadUInt64());
        var targetId = reader.ReadText(2048);
        var targetRoot = reader.ReadText(128 * 1024);
        reader.RequireEnd();
        try
        {
            return RecoveryLocator.Create(transactionId, targetId, targetRoot, identity);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            throw new TransactionAuthenticationException("The protected recovery locator was invalid.", exception);
        }
    }

    internal static byte[] Encode(StoredMigrationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using var stream = new MemoryStream(capacity: 4096);
        stream.Write(PlanMagic);
        WriteInt32(stream, PlanSchemaVersion);
        WriteGuid(stream, plan.TransactionId.Value);
        WriteHash(stream, plan.AcceptedPlanDigest);
        WriteInt32(stream, (int)plan.Purpose);
        WriteInt32(stream, plan.Paths.Count);
        foreach (var path in plan.Paths)
        {
            WriteText(stream, path.AdapterId, 1024);
            WriteText(stream, path.RelativePath.Value, 128 * 1024);
            WriteInt32(stream, (int)path.Resolution);
            WriteInt32(stream, path.BeforeExists ? 1 : 0);
            WriteHash(stream, path.ExpectedBeforeSha256);
            WriteInt32(stream, path.AfterExists ? 1 : 0);
            WriteHash(stream, path.ExpectedAfterSha256);
        }

        return Finish(stream);
    }

    internal static StoredMigrationPlan DecodePlan(ReadOnlySpan<byte> payload, TransactionId expectedTransactionId)
    {
        var reader = new BoundedBinaryReader(payload, MaximumPayloadBytes);
        reader.RequireMagic(PlanMagic);
        var schemaVersion = reader.ReadInt32();
        if (schemaVersion is not (LegacyPlanSchemaVersion or PlanSchemaVersion))
        {
            throw new TransactionAuthenticationException("The protected transaction schema is unsupported.");
        }

        var transactionId = new TransactionId(reader.ReadGuid());
        if (transactionId != expectedTransactionId)
        {
            throw new TransactionAuthenticationException("The protected plan belongs to another transaction.");
        }

        var digest = Convert.ToHexString(reader.ReadBytes(32));
        var purpose = schemaVersion == LegacyPlanSchemaVersion
            ? StoredTransactionPurpose.Migration
            : (StoredTransactionPurpose)reader.ReadInt32();
        if (!Enum.IsDefined(purpose))
        {
            throw new TransactionAuthenticationException("The protected plan purpose was invalid.");
        }

        var count = reader.ReadInt32();
        if (count is <= 0 or > ContentContractLimits.MaximumFileChanges)
        {
            throw new TransactionAuthenticationException("The protected plan path count was invalid.");
        }

        var paths = new StoredPlanPath[count];
        for (var index = 0; index < paths.Length; index++)
        {
            var adapterId = reader.ReadText(1024);
            var pathText = reader.ReadText(128 * 1024);
            var resolutionValue = reader.ReadInt32();
            var beforeExistsValue = reader.ReadInt32();
            var beforeSha256 = Convert.ToHexString(reader.ReadBytes(32));
            var afterExistsValue = reader.ReadInt32();
            var afterSha256 = Convert.ToHexString(reader.ReadBytes(32));
            if (!WritePathGuard.TryNormalize(pathText, out var path) ||
                path is null ||
                !Enum.IsDefined(typeof(ConflictResolution), resolutionValue) ||
                beforeExistsValue is not (0 or 1) ||
                afterExistsValue is not (0 or 1))
            {
                throw new TransactionAuthenticationException("The protected plan contained an unsafe path or resolution.");
            }

            paths[index] = StoredPlanPath.Create(
                adapterId,
                path,
                (ConflictResolution)resolutionValue,
                beforeExistsValue == 1,
                beforeSha256,
                afterExistsValue == 1,
                afterSha256);
        }

        reader.RequireEnd();
        try
        {
            return StoredMigrationPlan.Create(transactionId, digest, paths, purpose);
        }
        catch (ArgumentException exception)
        {
            throw new TransactionAuthenticationException("The protected plan failed validation.", exception);
        }
    }

    private static byte[] Finish(MemoryStream stream)
    {
        if (stream.Length > MaximumPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(stream), "The protected transaction payload exceeded its bound.");
        }

        return stream.ToArray();
    }

    private static void WriteGuid(Stream stream, Guid value) => stream.Write(value.ToByteArray());

    private static void WriteHash(Stream stream, string value) => stream.Write(Convert.FromHexString(value));

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteText(Stream stream, string value, int maximumUtf8Bytes)
    {
        var bytes = StrictUtf8.GetBytes(value);
        try
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(
                bytes.Length,
                maximumUtf8Bytes,
                nameof(value));

            WriteInt32(stream, bytes.Length);
            stream.Write(bytes);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    private ref struct BoundedBinaryReader
    {
        private readonly ReadOnlySpan<byte> payload;
        private int offset;

        internal BoundedBinaryReader(ReadOnlySpan<byte> payload, int maximumBytes)
        {
            if (payload.Length == 0 || payload.Length > maximumBytes)
            {
                throw new TransactionAuthenticationException("The protected transaction payload exceeded its bound.");
            }

            this.payload = payload;
            offset = 0;
        }

        internal void RequireMagic(ReadOnlySpan<byte> magic)
        {
            if (!ReadBytes(magic.Length).SequenceEqual(magic))
            {
                throw new TransactionAuthenticationException("The protected transaction payload header was invalid.");
            }
        }

        internal void RequireInt32(int expected)
        {
            if (ReadInt32() != expected)
            {
                throw new TransactionAuthenticationException("The protected transaction schema is unsupported.");
            }
        }

        internal int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(sizeof(int)));

        internal ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(sizeof(ulong)));

        internal Guid ReadGuid() => new(ReadBytes(16));

        internal string ReadText(int maximumUtf8Bytes)
        {
            var length = ReadInt32();
            if (length < 0 || length > maximumUtf8Bytes)
            {
                throw new TransactionAuthenticationException("A protected transaction string exceeded its bound.");
            }

            try
            {
                return StrictUtf8.GetString(ReadBytes(length));
            }
            catch (DecoderFallbackException exception)
            {
                throw new TransactionAuthenticationException("A protected transaction string was malformed UTF-8.", exception);
            }
        }

        internal ReadOnlySpan<byte> ReadBytes(int count)
        {
            if (count < 0 || count > payload.Length - offset)
            {
                throw new TransactionAuthenticationException("The protected transaction payload was truncated.");
            }

            var result = payload.Slice(offset, count);
            offset += count;
            return result;
        }

        internal void RequireEnd()
        {
            if (offset != payload.Length)
            {
                throw new TransactionAuthenticationException("The protected transaction payload had trailing data.");
            }
        }
    }
}

internal static class TransactionValueValidation
{
    internal static void RequireId(TransactionId transactionId)
    {
        if (transactionId.Value == Guid.Empty)
        {
            throw new ArgumentException("A non-empty transaction ID is required.", nameof(transactionId));
        }
    }

    internal static void RequireOpaqueId(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128 || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new ArgumentException("An opaque bounded identifier is required.", parameterName);
        }
    }

    internal static void RequireSha256(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != 64 || !value.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("A SHA-256 hexadecimal value is required.", parameterName);
        }
    }

    internal static void RequireBoundedText(
        string value,
        string parameterName,
        int maximumUtf16Length,
        bool allowDirectorySeparators)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumUtf16Length ||
            value.Any(char.IsControl) ||
            !allowDirectorySeparators && value.IndexOfAny(['\\', '/']) >= 0)
        {
            throw new ArgumentException("A bounded text value is required.", parameterName);
        }
    }

    internal static void RequireAbsolutePath(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 32_767 ||
            value.Any(character => character == '\0' || char.IsControl(character)) ||
            !Path.IsPathFullyQualified(value) ||
            value.StartsWith("\\\\", StringComparison.Ordinal) ||
            value.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            value.StartsWith("\\\\.\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("A bounded local absolute path is required.", parameterName);
        }
    }
}
