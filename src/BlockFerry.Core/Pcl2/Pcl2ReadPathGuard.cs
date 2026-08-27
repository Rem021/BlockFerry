using BlockFerry.Core.System;

namespace BlockFerry.Core.Pcl2;

internal sealed class Pcl2ReadPathGuard : IDisposable
{
    private readonly IFileSystemCapability fileSystem;
    private readonly IVerifiedDirectoryHandle approvedRoot;
    private readonly IVerifiedDirectoryHandle minecraftRoot;
    private readonly Pcl2DiscoveryBudget? budget;
    private bool disposed;

    public Pcl2ReadPathGuard(
        IFileSystemCapability fileSystem,
        Pcl2ResolvedRootAccess access,
        CancellationToken cancellationToken,
        Pcl2DiscoveryBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(access);
        this.fileSystem = fileSystem;
        this.budget = budget;
        approvedRoot = fileSystem.OpenRoot(
            access.ApprovedRootPath,
            FileSystemOpenPurpose.Discovery,
            cancellationToken);
        try
        {
            if (approvedRoot.Identity != access.ApprovedRootIdentity ||
                !approvedRoot.IsLocalVolume ||
                approvedRoot.IsNetworkRedirected)
            {
                throw new CapabilityBoundaryException(
                    "The approved discovery root identity or local-volume disposition changed.");
            }

            var openedMinecraft = fileSystem.OpenDirectory(
                approvedRoot,
                access.MinecraftRootRelativePath,
                cancellationToken);
            if (openedMinecraft.Identity != access.MinecraftRootIdentity ||
                !openedMinecraft.IsLocalVolume ||
                openedMinecraft.IsNetworkRedirected)
            {
                openedMinecraft.Dispose();
                throw new CapabilityBoundaryException(
                    "The Minecraft root identity or local-volume disposition changed.");
            }

            minecraftRoot = openedMinecraft;
            Access = access;
        }
        catch
        {
            approvedRoot.Dispose();
            throw;
        }
    }

    public Pcl2ResolvedRootAccess Access { get; }
    public PhysicalDirectoryIdentity MinecraftRootIdentity => Access.MinecraftRootIdentity;

    public IReadOnlyList<FileSystemEntrySnapshot> EnumerateMinecraft(
        NormalizedRelativePath relativePath,
        int maximumEntries,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        budget?.ReserveEnumeration();
        return fileSystem.EnumerateEntries(
            minecraftRoot,
            relativePath,
            new EnumerationLimits(maximumEntries),
            cancellationToken);
    }

    public BoundedFileSnapshot ReadMinecraftFile(
        NormalizedRelativePath relativePath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var effectiveMaximumBytes = budget?.ReserveRead(maximumBytes) ?? maximumBytes;
        var snapshot = fileSystem.ReadFile(
            minecraftRoot,
            relativePath,
            new FileReadLimits(effectiveMaximumBytes),
            cancellationToken);
        budget?.CommitRead(snapshot.Length);
        return snapshot;
    }

    public IVerifiedDirectoryHandle OpenMinecraftDirectory(
        NormalizedRelativePath relativePath,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return fileSystem.OpenDirectory(
            minecraftRoot,
            relativePath,
            cancellationToken);
    }

    public string GetMinecraftAbsolutePath(NormalizedRelativePath relativePath) =>
        relativePath.Value.Length == 0
            ? Access.MinecraftRootPath
            : Pcl2PathNormalizer.Normalize(Path.Combine(
                Access.MinecraftRootPath,
                relativePath.Value));

    public bool TryGetMinecraftRelativePath(
        string absolutePath,
        out NormalizedRelativePath? relativePath,
        out string? rejection)
    {
        relativePath = null;
        rejection = null;
        if (!Pcl2PathNormalizer.TryNormalize(absolutePath, out var normalized) ||
            normalized is null)
        {
            rejection = "The read path could not be normalized.";
            return false;
        }

        var relative = Path.GetRelativePath(Access.MinecraftRootPath, normalized);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            rejection = "The read path resolves outside the approved Minecraft root.";
            return false;
        }

        if (relative.Equals(".", StringComparison.Ordinal))
        {
            relative = string.Empty;
        }

        return NormalizedRelativePath.TryCreate(relative, out relativePath, out rejection);
    }

    public static NormalizedRelativePath Combine(
        NormalizedRelativePath parent,
        params string[] children)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(children);
        var value = parent.Value;
        foreach (var child in children)
        {
            value = value.Length == 0 ? child : Path.Combine(value, child);
        }

        if (!NormalizedRelativePath.TryCreate(value, out var combined, out var rejection) ||
            combined is null)
        {
            throw new CapabilityBoundaryException(
                rejection ?? "The PCL relative path could not be normalized.");
        }

        return combined;
    }

    public static NormalizedRelativePath Relative(string value)
    {
        if (!NormalizedRelativePath.TryCreate(value, out var path, out var rejection) || path is null)
        {
            throw new CapabilityBoundaryException(
                rejection ?? "The PCL relative path could not be normalized.");
        }

        return path;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        minecraftRoot.Dispose();
        approvedRoot.Dispose();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(disposed, this);
}
