namespace BlockFerry.Core.System;

public interface IVerifiedDirectoryHandle : IDisposable
{
    string FinalPath { get; }
    PhysicalDirectoryIdentity Identity { get; }
    bool IsLocalVolume { get; }
    bool IsNetworkRedirected { get; }
}

public interface IFileSystemCapability
{
    IVerifiedDirectoryHandle OpenRoot(
        string absolutePath,
        FileSystemOpenPurpose purpose,
        CancellationToken cancellationToken);

    IVerifiedDirectoryHandle OpenDirectory(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        CancellationToken cancellationToken);

    IReadOnlyList<FileSystemEntrySnapshot> EnumerateEntries(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        EnumerationLimits limits,
        CancellationToken cancellationToken);

    BoundedFileSnapshot ReadFile(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        FileReadLimits limits,
        CancellationToken cancellationToken);

    IReadOnlyDictionary<string, BoundedFileSnapshot> ReadZipEntries(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath zipPath,
        IReadOnlySet<string> allowedEntryNames,
        ZipReadLimits limits,
        CancellationToken cancellationToken);

    VolumeCapabilitySnapshot InspectVolume(
        IVerifiedDirectoryHandle root,
        CancellationToken cancellationToken);
}
