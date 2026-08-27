using BlockFerry.Core.System;

namespace BlockFerry.TestSupport;

public sealed record CapabilityAuditEvent(
    string Operation,
    Guid? RootId,
    string RequestedPath,
    string DesiredAccess,
    string ShareMode,
    string? FinalPath,
    PhysicalDirectoryIdentity? DirectoryIdentity,
    PhysicalFileIdentity? FileIdentity,
    bool WasRejected,
    bool IsMutation);

public sealed record CapabilityAuditSummary(
    int EventCount,
    int WriteCount,
    int RealRootAccessCount)
{
    public static CapabilityAuditSummary From(IEnumerable<CapabilityAuditEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var snapshot = events.ToArray();
        return new CapabilityAuditSummary(
            snapshot.Length,
            snapshot.Count(entry => entry.IsMutation),
            snapshot.Count(entry => !entry.WasRejected && entry.RootId is null));
    }
}

public sealed class AuditedFileSystemCapability : IFileSystemCapability
{
    private readonly IFileSystemCapability inner;
    private readonly Dictionary<string, FixtureRootProof> allowedRoots;
    private readonly List<CapabilityAuditEvent> auditLog = [];
    private readonly object auditGate = new();
    private readonly object ownerToken = new();

    public AuditedFileSystemCapability(IEnumerable<FixtureRootProof> allowedRoots)
        : this(new WindowsFileSystemCapability(), allowedRoots)
    {
    }

    public AuditedFileSystemCapability(
        IFileSystemCapability inner,
        IEnumerable<FixtureRootProof> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(allowedRoots);
        this.inner = inner;
        this.allowedRoots = allowedRoots
            .ToDictionary(
                proof => NormalizeAbsolutePath(proof.RootPath),
                proof => proof,
                StringComparer.OrdinalIgnoreCase);
        if (this.allowedRoots.Count == 0)
        {
            throw new ArgumentException("At least one fixture root proof must be allowed.", nameof(allowedRoots));
        }

        if (this.allowedRoots.Any(entry =>
                !string.Equals(
                    entry.Key,
                    NormalizeAbsolutePath(entry.Value.RootPath),
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Every fixture root proof must match its normalized root path.", nameof(allowedRoots));
        }
    }

    public IReadOnlyList<CapabilityAuditEvent> AuditLog
    {
        get
        {
            lock (auditGate)
            {
                return Array.AsReadOnly(auditLog.ToArray());
            }
        }
    }

    public IReadOnlySet<Guid> AllowedRootIds => allowedRoots.Values.Select(proof => proof.RootId).ToHashSet();

    public IVerifiedDirectoryHandle OpenRoot(
        string absolutePath,
        FileSystemOpenPurpose purpose,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(absolutePath);
        var normalized = NormalizeAbsolutePath(absolutePath);
        if (!allowedRoots.TryGetValue(normalized, out var proof))
        {
            Record(
                "OpenRoot",
                null,
                normalized,
                "FILE_READ_ATTRIBUTES|FILE_LIST_DIRECTORY|SYNCHRONIZE",
                "READ|WRITE|DELETE",
                null,
                null,
                null,
                wasRejected: true);
            throw new CapabilityBoundaryException("The requested root is outside the fixture allowlist.");
        }

        try
        {
            var opened = inner.OpenRoot(normalized, purpose, cancellationToken);
            if (opened.Identity != proof.PhysicalIdentity)
            {
                opened.Dispose();
                throw new CapabilityBoundaryException(
                    "The opened fixture root no longer matches its issued physical identity.");
            }

            Record(
                "OpenRoot",
                proof.RootId,
                normalized,
                "FILE_READ_ATTRIBUTES|FILE_LIST_DIRECTORY|SYNCHRONIZE",
                "READ|WRITE|DELETE",
                opened.FinalPath,
                opened.Identity,
                null,
                wasRejected: false);
            return new AuditedHandle(ownerToken, opened, proof);
        }
        catch
        {
            Record(
                "OpenRoot",
                proof.RootId,
                normalized,
                "FILE_READ_ATTRIBUTES|FILE_LIST_DIRECTORY|SYNCHRONIZE",
                "READ|WRITE|DELETE",
                null,
                null,
                null,
                wasRejected: true);
            throw;
        }
    }

    public IVerifiedDirectoryHandle OpenDirectory(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        CancellationToken cancellationToken)
    {
        var audited = RequireHandle(root);
        try
        {
            var opened = inner.OpenDirectory(audited.Inner, relativePath, cancellationToken);
            Record(
                "OpenDirectory",
                audited.RootId,
                relativePath.Value,
                "FILE_READ_ATTRIBUTES|FILE_LIST_DIRECTORY|SYNCHRONIZE",
                "READ|WRITE|DELETE",
                opened.FinalPath,
                opened.Identity,
                null,
                wasRejected: false);
            return new AuditedHandle(ownerToken, opened, audited.RootProof);
        }
        catch
        {
            RecordReadFailure("OpenDirectory", audited, relativePath.Value);
            throw;
        }
    }

    public IReadOnlyList<FileSystemEntrySnapshot> EnumerateEntries(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        EnumerationLimits limits,
        CancellationToken cancellationToken)
    {
        var audited = RequireHandle(root);
        try
        {
            var entries = inner.EnumerateEntries(audited.Inner, relativePath, limits, cancellationToken);
            Record(
                "EnumerateEntries",
                audited.RootId,
                relativePath.Value,
                "FILE_LIST_DIRECTORY|FILE_READ_ATTRIBUTES|SYNCHRONIZE",
                "READ|WRITE|DELETE",
                audited.FinalPath,
                audited.Identity,
                null,
                wasRejected: false);
            return entries;
        }
        catch
        {
            RecordReadFailure("EnumerateEntries", audited, relativePath.Value);
            throw;
        }
    }

    public BoundedFileSnapshot ReadFile(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        FileReadLimits limits,
        CancellationToken cancellationToken)
    {
        var audited = RequireHandle(root);
        try
        {
            var snapshot = inner.ReadFile(audited.Inner, relativePath, limits, cancellationToken);
            Record(
                "ReadFile",
                audited.RootId,
                relativePath.Value,
                "GENERIC_READ|SYNCHRONIZE",
                "READ",
                audited.FinalPath,
                audited.Identity,
                snapshot.Metadata.Identity,
                wasRejected: false);
            return snapshot;
        }
        catch
        {
            RecordReadFailure("ReadFile", audited, relativePath.Value, "GENERIC_READ|SYNCHRONIZE", "READ");
            throw;
        }
    }

    public IReadOnlyDictionary<string, BoundedFileSnapshot> ReadZipEntries(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath zipPath,
        IReadOnlySet<string> allowedEntryNames,
        ZipReadLimits limits,
        CancellationToken cancellationToken)
    {
        var audited = RequireHandle(root);
        try
        {
            var snapshots = inner.ReadZipEntries(
                audited.Inner,
                zipPath,
                allowedEntryNames,
                limits,
                cancellationToken);
            Record(
                "ReadZipEntries",
                audited.RootId,
                zipPath.Value,
                "GENERIC_READ|SYNCHRONIZE",
                "READ",
                audited.FinalPath,
                audited.Identity,
                snapshots.Values.Select(value => value.Metadata.Identity).FirstOrDefault(value => value is not null),
                wasRejected: false);
            return snapshots;
        }
        catch
        {
            RecordReadFailure("ReadZipEntries", audited, zipPath.Value, "GENERIC_READ|SYNCHRONIZE", "READ");
            throw;
        }
    }

    public VolumeCapabilitySnapshot InspectVolume(
        IVerifiedDirectoryHandle root,
        CancellationToken cancellationToken)
    {
        var audited = RequireHandle(root);
        try
        {
            var snapshot = inner.InspectVolume(audited.Inner, cancellationToken);
            Record(
                "InspectVolume",
                audited.RootId,
                string.Empty,
                "FILE_READ_ATTRIBUTES",
                "READ|WRITE|DELETE",
                audited.FinalPath,
                audited.Identity,
                null,
                wasRejected: false);
            return snapshot;
        }
        catch
        {
            RecordReadFailure("InspectVolume", audited, string.Empty);
            throw;
        }
    }

    private AuditedHandle RequireHandle(IVerifiedDirectoryHandle handle)
    {
        if (handle is not AuditedHandle audited ||
            audited.IsDisposed ||
            !ReferenceEquals(audited.OwnerToken, ownerToken) ||
            !allowedRoots.TryGetValue(audited.RootProof.RootPath, out var allowedProof) ||
            !ReferenceEquals(audited.RootProof, allowedProof) ||
            !ReferenceEquals(audited.RootProof.OwnerToken, allowedProof.OwnerToken))
        {
            throw new CapabilityBoundaryException("The directory handle was not issued by this audited capability or is disposed.");
        }

        return audited;
    }

    private void RecordReadFailure(
        string operation,
        AuditedHandle handle,
        string requestedPath,
        string desiredAccess = "FILE_READ_ATTRIBUTES|FILE_LIST_DIRECTORY|SYNCHRONIZE",
        string shareMode = "READ|WRITE|DELETE") =>
        Record(
            operation,
            handle.RootId,
            requestedPath,
            desiredAccess,
            shareMode,
            handle.FinalPath,
            handle.Identity,
            null,
            wasRejected: true);

    private void Record(
        string operation,
        Guid? rootId,
        string requestedPath,
        string desiredAccess,
        string shareMode,
        string? finalPath,
        PhysicalDirectoryIdentity? directoryIdentity,
        PhysicalFileIdentity? fileIdentity,
        bool wasRejected)
    {
        var auditEvent = new CapabilityAuditEvent(
            operation,
            rootId,
            requestedPath,
            desiredAccess,
            shareMode,
            finalPath,
            directoryIdentity,
            fileIdentity,
            wasRejected,
            IsMutation: false);
        lock (auditGate)
        {
            auditLog.Add(auditEvent);
        }
    }

    private static string NormalizeAbsolutePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private sealed class AuditedHandle : IVerifiedDirectoryHandle
    {
        public AuditedHandle(
            object ownerToken,
            IVerifiedDirectoryHandle inner,
            FixtureRootProof rootProof)
        {
            OwnerToken = ownerToken;
            Inner = inner;
            RootProof = rootProof;
        }

        public object OwnerToken { get; }
        public IVerifiedDirectoryHandle Inner { get; }
        public FixtureRootProof RootProof { get; }
        public Guid RootId => RootProof.RootId;
        public bool IsDisposed { get; private set; }
        public string FinalPath => Inner.FinalPath;
        public PhysicalDirectoryIdentity Identity => Inner.Identity;
        public bool IsLocalVolume => Inner.IsLocalVolume;
        public bool IsNetworkRedirected => Inner.IsNetworkRedirected;

        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            Inner.Dispose();
        }
    }
}
