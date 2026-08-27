using System.Buffers.Binary;
using System.Security.Cryptography;
using BlockFerry.Core.Discovery;
using BlockFerry.Core.System;
using Microsoft.Win32.SafeHandles;

namespace BlockFerry.Core.Transactions;

public sealed class FileMetadataSnapshot
{
    internal FileMetadataSnapshot(
        PhysicalFileIdentity identity,
        long length,
        string sha256,
        DateTimeOffset creationTimeUtc,
        DateTimeOffset lastAccessTimeUtc,
        DateTimeOffset lastWriteTimeUtc,
        FileAttributes attributes,
        uint linkCount,
        byte[] securityDescriptor,
        IReadOnlyList<string> streamNames)
    {
        Identity = identity;
        Length = length;
        Sha256 = sha256;
        CreationTimeUtc = creationTimeUtc;
        LastAccessTimeUtc = lastAccessTimeUtc;
        LastWriteTimeUtc = lastWriteTimeUtc;
        Attributes = attributes;
        LinkCount = linkCount;
        SecurityDescriptor = securityDescriptor;
        StreamNames = streamNames;
        MetadataDigest = ComputeMetadataDigest();
    }

    public PhysicalFileIdentity Identity { get; }

    public long Length { get; }

    public string Sha256 { get; }

    public DateTimeOffset CreationTimeUtc { get; }

    public DateTimeOffset LastAccessTimeUtc { get; }

    public DateTimeOffset LastWriteTimeUtc { get; }

    public FileAttributes Attributes { get; }

    public uint LinkCount { get; }

    public IReadOnlyList<string> StreamNames { get; }

    public string MetadataDigest { get; }

    internal byte[] SecurityDescriptor { get; }

    internal FileMetadataSnapshot WithContentIdentity(
        PhysicalFileIdentity identity,
        long length,
        string sha256) =>
        new(
            identity,
            length,
            sha256,
            CreationTimeUtc,
            LastAccessTimeUtc,
            LastWriteTimeUtc,
            Attributes,
            LinkCount,
            SecurityDescriptor.ToArray(),
            StreamNames.ToArray());

    internal bool SemanticallyEquals(FileMetadataSnapshot other) =>
        other is not null &&
        Length == other.Length &&
        string.Equals(Sha256, other.Sha256, StringComparison.Ordinal) &&
        CreationTimeUtc == other.CreationTimeUtc &&
        LastAccessTimeUtc == other.LastAccessTimeUtc &&
        LastWriteTimeUtc == other.LastWriteTimeUtc &&
        Attributes == other.Attributes &&
        LinkCount == other.LinkCount &&
        SecurityDescriptorsSemanticallyEqual(SecurityDescriptor, other.SecurityDescriptor) &&
        StreamNames.SequenceEqual(other.StreamNames, StringComparer.Ordinal);

    internal bool StableStateEquals(FileMetadataSnapshot other) =>
        other is not null &&
        Length == other.Length &&
        string.Equals(Sha256, other.Sha256, StringComparison.Ordinal) &&
        CreationTimeUtc == other.CreationTimeUtc &&
        LastWriteTimeUtc == other.LastWriteTimeUtc &&
        Attributes == other.Attributes &&
        LinkCount == other.LinkCount &&
        SecurityDescriptorsSemanticallyEqual(SecurityDescriptor, other.SecurityDescriptor) &&
        StreamNames.SequenceEqual(other.StreamNames, StringComparer.Ordinal);

    internal static bool SecurityDescriptorsSemanticallyEqual(
        byte[] left,
        byte[] right)
    {
        if (left.AsSpan().SequenceEqual(right))
        {
            return true;
        }

        const ushort relevantControlFlags = 0x0004 | 0x1000;
        return (ReadControl(left) & relevantControlFlags) ==
               (ReadControl(right) & relevantControlFlags) &&
               ReadSid(left, 4).SequenceEqual(ReadSid(right, 4)) &&
               ReadSid(left, 8).SequenceEqual(ReadSid(right, 8)) &&
               ReadAcl(left, 16).SequenceEqual(ReadAcl(right, 16));
    }

    internal static string DescribeSecurityDescriptorDifference(byte[] left, byte[] right) =>
        $"control=0x{ReadControl(left):X4}/0x{ReadControl(right):X4}, " +
        $"owner={ReadSid(left, 4).SequenceEqual(ReadSid(right, 4))}, " +
        $"group={ReadSid(left, 8).SequenceEqual(ReadSid(right, 8))}, " +
        $"dacl={ReadAcl(left, 16).SequenceEqual(ReadAcl(right, 16))}";

    internal static bool SecurityDescriptorMatchesReplaceFileMerge(
        byte[] replacement,
        byte[] replaced,
        byte[] observed)
    {
        RequireSelfRelativeSecurityDescriptor(replacement);
        RequireSelfRelativeSecurityDescriptor(replaced);
        RequireSelfRelativeSecurityDescriptor(observed);
        const ushort daclState = 0x0004 | 0x1000;
        var expectedOwner = ReadSid(replacement, 4);
        var expectedGroup = ReadSid(replacement, 8);
        var expectedDacl = ReadAcl(replaced, 16);
        var observedOwner = ReadSid(observed, 4);
        var observedGroup = ReadSid(observed, 8);
        var observedDacl = ReadAcl(observed, 16);
        return !expectedOwner.IsEmpty &&
               !expectedGroup.IsEmpty &&
               !expectedDacl.IsEmpty &&
               !observedOwner.IsEmpty &&
               !observedGroup.IsEmpty &&
               !observedDacl.IsEmpty &&
               expectedOwner.SequenceEqual(observedOwner) &&
               expectedGroup.SequenceEqual(observedGroup) &&
               (ReadControl(replaced) & daclState) == (ReadControl(observed) & daclState) &&
               expectedDacl.SequenceEqual(observedDacl);
    }

    private static ushort ReadControl(byte[] descriptor)
    {
        RequireSecurityDescriptorHeader(descriptor);
        return BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(2, sizeof(ushort)));
    }

    private static ReadOnlySpan<byte> ReadSid(byte[] descriptor, int offsetField)
    {
        RequireSecurityDescriptorHeader(descriptor);
        var offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            descriptor.AsSpan(offsetField, sizeof(uint))));
        if (offset == 0)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        if (offset > descriptor.Length - 8)
        {
            throw new IOException("A retained security descriptor SID was malformed.");
        }

        var length = checked(8 + (descriptor[offset + 1] * sizeof(uint)));
        if (length > descriptor.Length - offset)
        {
            throw new IOException("A retained security descriptor SID exceeded its bound.");
        }

        return descriptor.AsSpan(offset, length);
    }

    private static ReadOnlySpan<byte> ReadAcl(byte[] descriptor, int offsetField)
    {
        RequireSecurityDescriptorHeader(descriptor);
        var offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            descriptor.AsSpan(offsetField, sizeof(uint))));
        if (offset == 0)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        if (offset > descriptor.Length - 8)
        {
            throw new IOException("A retained security descriptor ACL was malformed.");
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(
            descriptor.AsSpan(offset + 2, sizeof(ushort)));
        if (length < 8 || length > descriptor.Length - offset)
        {
            throw new IOException("A retained security descriptor ACL exceeded its bound.");
        }

        return descriptor.AsSpan(offset, length);
    }

    private static void RequireSecurityDescriptorHeader(byte[] descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Length < 20 || descriptor[0] != 1)
        {
            throw new IOException("A retained security descriptor header was malformed.");
        }
    }

    private static void RequireSelfRelativeSecurityDescriptor(byte[] descriptor)
    {
        RequireSecurityDescriptorHeader(descriptor);
        const ushort securityDescriptorSelfRelative = 0x8000;
        if ((ReadControl(descriptor) & securityDescriptorSelfRelative) == 0)
        {
            throw new IOException("A retained security descriptor was not self-relative.");
        }

        _ = ReadSid(descriptor, 4);
        _ = ReadSid(descriptor, 8);
        _ = ReadAcl(descriptor, 16);
    }

    private string ComputeMetadataDigest()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Length);
        Append(hash, CreationTimeUtc.UtcTicks);
        Append(hash, LastAccessTimeUtc.UtcTicks);
        Append(hash, LastWriteTimeUtc.UtcTicks);
        Append(hash, (long)Attributes);
        Append(hash, LinkCount);
        hash.AppendData(Convert.FromHexString(Sha256));
        hash.AppendData(SecurityDescriptor);
        foreach (var stream in StreamNames)
        {
            var bytes = global::System.Text.Encoding.UTF8.GetBytes(stream);
            try
            {
                hash.AppendData(bytes);
                hash.AppendData([0]);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        global::System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        global::System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }
}

internal sealed class TransactionRootLease : IDisposable
{
    private readonly object gate = new();
    private TransactionId? boundTransactionId;
    private bool disposed;

    internal TransactionRootLease(
        Func<bool> authorityIsActive,
        IReadOnlySet<NormalizedRelativePath> writeAllowlist,
        SafeFileHandle rootHandle,
        PhysicalDirectoryIdentity identity,
        string finalPath)
    {
        this.authorityIsActive = authorityIsActive ??
            throw new ArgumentNullException(nameof(authorityIsActive));
        WriteAllowlist = writeAllowlist ?? throw new ArgumentNullException(nameof(writeAllowlist));
        RootHandle = rootHandle;
        Identity = identity;
        FinalPath = finalPath;
    }

    private readonly Func<bool> authorityIsActive;

    internal IReadOnlySet<NormalizedRelativePath> WriteAllowlist { get; }

    internal SafeFileHandle RootHandle { get; }

    internal PhysicalDirectoryIdentity Identity { get; }

    internal string FinalPath { get; }

    internal void ConsumePermit(
        JournalMutationPermit permit,
        TransactionRecordKind kind,
        string opaqueObjectId,
        NormalizedRelativePath path)
    {
        ArgumentNullException.ThrowIfNull(permit);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (boundTransactionId is { } bound && bound != permit.TransactionId)
            {
                throw new InvalidOperationException("The target-root lease is already bound to another transaction.");
            }

            permit.Consume(permit.TransactionId, kind, opaqueObjectId, path);
            boundTransactionId ??= permit.TransactionId;
        }
    }

    internal void EnsureActive()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!authorityIsActive())
            {
                throw new InvalidOperationException("The original migration authority is no longer active.");
            }
        }
    }

    internal void ConsumePostCommitCleanupAuthority(
        MigrationTransactionCoordinator.PostCommitCleanupAuthority authority,
        DisplacedObject displaced)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(displaced);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!authorityIsActive() || boundTransactionId is not { } transactionId)
            {
                throw new InvalidOperationException(
                    "The target-root lease cannot authorize post-commit cleanup.");
            }

            authority.Consume(transactionId, Identity, displaced);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            RootHandle.Dispose();
        }
    }
}

public abstract class VerifiedTransactionObject : IDisposable
{
    private SafeFileHandle? handle;

    internal VerifiedTransactionObject(
        string opaqueObjectId,
        NormalizedRelativePath relativePath,
        string retainedPath,
        SafeFileHandle handle,
        FileMetadataSnapshot metadata)
    {
        OpaqueObjectId = opaqueObjectId;
        RelativePath = relativePath;
        RetainedPath = retainedPath;
        this.handle = handle;
        Metadata = metadata;
    }

    public string OpaqueObjectId { get; }

    public NormalizedRelativePath RelativePath { get; }

    public FileMetadataSnapshot Metadata { get; }

    internal string RetainedPath { get; }

    internal SafeFileHandle Handle => handle ??
        throw new ObjectDisposedException(GetType().Name);

    internal SafeFileHandle DetachHandle()
    {
        var detached = Interlocked.Exchange(ref handle, null);
        return detached ?? throw new ObjectDisposedException(GetType().Name);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref handle, null)?.Dispose();
        GC.SuppressFinalize(this);
    }

    public override string ToString() => $"{GetType().Name}: {OpaqueObjectId}; {RelativePath.Value}";
}

public sealed class ExpectedTargetObject
{
    internal ExpectedTargetObject(
        NormalizedRelativePath relativePath,
        FileMetadataSnapshot metadata)
    {
        RelativePath = relativePath;
        Metadata = metadata;
    }

    public NormalizedRelativePath RelativePath { get; }

    public FileMetadataSnapshot Metadata { get; }
}

public sealed class BackupObject
{
    internal BackupObject(
        string opaqueObjectId,
        NormalizedRelativePath relativePath,
        FileMetadataSnapshot metadata)
    {
        OpaqueObjectId = opaqueObjectId;
        RelativePath = relativePath;
        Metadata = metadata;
        ExpectedTarget = new ExpectedTargetObject(relativePath, metadata);
    }

    public string OpaqueObjectId { get; }

    public NormalizedRelativePath RelativePath { get; }

    public FileMetadataSnapshot Metadata { get; }

    public ExpectedTargetObject ExpectedTarget { get; }
}

public sealed class CreatedDirectory : IDisposable
{
    private SafeFileHandle? handle;

    internal CreatedDirectory(
        string opaqueObjectId,
        NormalizedRelativePath relativePath,
        string retainedPath,
        SafeFileHandle handle,
        PhysicalDirectoryIdentity identity)
    {
        OpaqueObjectId = opaqueObjectId;
        RelativePath = relativePath;
        RetainedPath = retainedPath;
        this.handle = handle;
        Identity = identity;
    }

    public string OpaqueObjectId { get; }

    public NormalizedRelativePath RelativePath { get; }

    public PhysicalDirectoryIdentity Identity { get; }

    internal string RetainedPath { get; }

    internal SafeFileHandle Handle => handle ?? throw new ObjectDisposedException(nameof(CreatedDirectory));

    public void Dispose() => Interlocked.Exchange(ref handle, null)?.Dispose();
}

public sealed class StagedObject : VerifiedTransactionObject
{
    internal StagedObject(
        string opaqueObjectId,
        NormalizedRelativePath relativePath,
        string retainedPath,
        SafeFileHandle handle,
        FileMetadataSnapshot metadata)
        : base(opaqueObjectId, relativePath, retainedPath, handle, metadata)
    {
    }
}

public sealed class CommittedObject : VerifiedTransactionObject
{
    internal CommittedObject(
        string opaqueObjectId,
        NormalizedRelativePath relativePath,
        string retainedPath,
        SafeFileHandle handle,
        FileMetadataSnapshot metadata)
        : base(opaqueObjectId, relativePath, retainedPath, handle, metadata)
    {
    }
}

public sealed class DisplacedObject : VerifiedTransactionObject
{
    private VerifiedTransactionObject? linkedReplacement;

    internal DisplacedObject(
        string opaqueObjectId,
        NormalizedRelativePath relativePath,
        string retainedPath,
        string finalPath,
        SafeFileHandle handle,
        FileMetadataSnapshot metadata,
        FileMetadataSnapshot expectedFinalMetadata)
        : base(opaqueObjectId, relativePath, retainedPath, handle, metadata)
    {
        FinalPath = finalPath;
        ExpectedFinalMetadata = expectedFinalMetadata ??
            throw new ArgumentNullException(nameof(expectedFinalMetadata));
    }

    internal string FinalPath { get; }

    internal FileMetadataSnapshot ExpectedFinalMetadata { get; }

    internal SafeFileHandle LinkedReplacementHandle =>
        linkedReplacement?.Handle ?? throw new InvalidOperationException(
            "The displaced object had no retained replacement handle.");

    internal void LinkReplacement(VerifiedTransactionObject replacement) =>
        linkedReplacement = replacement ?? throw new ArgumentNullException(nameof(replacement));

    internal void ReleaseRetainedObjectsForRollback()
    {
        Interlocked.Exchange(ref linkedReplacement, null)?.Dispose();
        Dispose();
    }
}

public sealed class VerifiedObject : VerifiedTransactionObject
{
    internal VerifiedObject(
        string opaqueObjectId,
        NormalizedRelativePath relativePath,
        string retainedPath,
        SafeFileHandle handle,
        FileMetadataSnapshot metadata)
        : base(opaqueObjectId, relativePath, retainedPath, handle, metadata)
    {
    }
}

public sealed class ReplaceOutcome : IDisposable
{
    internal ReplaceOutcome(
        CommittedObject replacement,
        DisplacedObject displaced,
        bool displacedMatchesExpected)
    {
        Replacement = replacement;
        Displaced = displaced;
        DisplacedMatchesExpected = displacedMatchesExpected;
        displaced.LinkReplacement(replacement);
    }

    public CommittedObject Replacement { get; }

    public DisplacedObject Displaced { get; }

    public bool DisplacedMatchesExpected { get; }

    public void Dispose()
    {
        Replacement.Dispose();
        Displaced.Dispose();
    }
}
