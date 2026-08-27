using System.Collections.ObjectModel;
using BlockFerry.Core.Discovery;
using BlockFerry.Core.Mods;
using BlockFerry.Core.Pcl2;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Content;

internal sealed class RecoveryCatalogContext : IDisposable
{
    private readonly DiscoverySession session;
    private readonly ContentAccessLifetime lifetime;
    private IVerifiedDirectoryHandle? targetRoot;

    internal RecoveryCatalogContext(
        DiscoverySession session,
        DiscoveredInstanceChoice targetChoice,
        ContentAccessLifetime lifetime,
        IReadOnlyInstanceAccess target,
        IVerifiedDirectoryHandle targetRoot,
        IReadOnlyDictionary<string, string> targetModVersions,
        IReadOnlySet<string> unsupportedModIds)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        TargetChoice = targetChoice ?? throw new ArgumentNullException(nameof(targetChoice));
        this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        this.targetRoot = targetRoot ?? throw new ArgumentNullException(nameof(targetRoot));
        TargetModVersions = targetModVersions ?? throw new ArgumentNullException(nameof(targetModVersions));
        UnsupportedModIds = unsupportedModIds ?? throw new ArgumentNullException(nameof(unsupportedModIds));
    }

    internal DiscoveredInstanceChoice TargetChoice { get; }

    internal IReadOnlyInstanceAccess Target { get; }

    internal IReadOnlyDictionary<string, string> TargetModVersions { get; }

    internal IReadOnlySet<string> UnsupportedModIds { get; }

    internal string? TargetMinecraftVersion => Target.Identity.MinecraftVersion;

    internal void ThrowIfUnavailable()
    {
        lifetime.ThrowIfUnavailable();
        ObjectDisposedException.ThrowIf(
            !ReferenceEquals(
                session.RevalidateTarget(TargetChoice.Instance.Id, CancellationToken.None),
                TargetChoice),
            this);
    }

    public void Dispose()
    {
        if (!lifetime.TryDeactivate())
        {
            return;
        }

        var root = Interlocked.Exchange(ref targetRoot, null);
        root?.Dispose();
    }
}

internal sealed class RecoveryCatalogContextFactory(
    IFileSystemCapability fileSystem,
    ModPresenceProbe modPresenceProbe)
{
    private static readonly IReadOnlySet<string> RequiredModIds =
        new ReadOnlySet<string>(
            ["jei", "extremesoundmuffler", "emi", "fancymenu", "darkmodeeverywhere"],
            StringComparer.Ordinal);
    private static readonly ModProbeLimits ProbeLimits = new(
        MaximumJarFiles: 2_048,
        MaximumZipEntries: 65_536,
        MaximumEntryBytes: 2 * 1024 * 1024,
        MaximumTotalBytes: 32L * 1024 * 1024,
        MaximumArchiveBytes: 256L * 1024 * 1024,
        MaximumCentralDirectoryBytes: 32L * 1024 * 1024);
    private readonly IFileSystemCapability fileSystem =
        fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly ModPresenceProbe modPresenceProbe =
        modPresenceProbe ?? throw new ArgumentNullException(nameof(modPresenceProbe));

    internal RecoveryCatalogContext? Open(
        DiscoverySession session,
        string targetInstanceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetInstanceId);
        cancellationToken.ThrowIfCancellationRequested();
        var targetChoice = session.RevalidateTarget(targetInstanceId, cancellationToken);
        if (targetChoice is null)
        {
            return null;
        }

        IVerifiedDirectoryHandle? targetRoot = null;
        ContentAccessLifetime? lifetime = null;
        try
        {
            targetRoot = fileSystem.OpenRoot(
                targetChoice.GameRoot.CanonicalPath,
                FileSystemOpenPurpose.MigrationTarget,
                cancellationToken);
            if (!RootMatches(targetRoot, targetChoice.GameRoot, cancellationToken))
            {
                return null;
            }

            lifetime = new ContentAccessLifetime(session);
            var target = new CapabilityBoundInstanceAccess(
                fileSystem,
                targetRoot,
                new ContentInstanceIdentity(
                    targetChoice.Instance.Id,
                    targetChoice.Instance.MinecraftVersion,
                    new ContentFileIdentity(
                        targetRoot.Identity.VolumeSerialNumber,
                        targetRoot.Identity.FileIdLow,
                        targetRoot.Identity.FileIdHigh)),
                lifetime,
                new ContentAccessBudget(ContentAccessLimits.Beta3));
            var mods = modPresenceProbe.Probe(
                target,
                RequiredModIds,
                ProbeLimits,
                cancellationToken);
            var versions = mods.Evidence
                .Where(evidence => evidence.Version is not null)
                .OrderBy(evidence => evidence.ModId, StringComparer.Ordinal)
                .ToDictionary(
                    evidence => evidence.ModId,
                    evidence => evidence.Version!,
                    StringComparer.Ordinal);
            var unsupported = mods.Diagnostics
                .Select(diagnostic => diagnostic.ItemId)
                .Where(itemId => itemId is not null &&
                                 string.Equals(itemId.Value.AdapterId, "mods", StringComparison.Ordinal))
                .Select(itemId => itemId!.Value.TechnicalKey)
                .ToHashSet(StringComparer.Ordinal);
            var result = new RecoveryCatalogContext(
                session,
                targetChoice,
                lifetime,
                target,
                targetRoot,
                new ReadOnlyDictionary<string, string>(versions),
                new ReadOnlySet<string>(unsupported, StringComparer.Ordinal));
            targetRoot = null;
            lifetime = null;
            return result;
        }
        finally
        {
            if (lifetime is not null)
            {
                _ = lifetime.TryDeactivate();
            }

            targetRoot?.Dispose();
        }
    }

    private bool RootMatches(
        IVerifiedDirectoryHandle opened,
        VerifiedDirectorySnapshot expected,
        CancellationToken cancellationToken)
    {
        var volume = fileSystem.InspectVolume(opened, cancellationToken);
        return expected.IsReparseFree &&
               expected.IsLocalVolume &&
               !expected.IsNetworkRedirected &&
               opened.Identity == expected.Identity &&
               opened.IsLocalVolume &&
               !opened.IsNetworkRedirected &&
               volume.IsLocalVolume &&
               !volume.IsNetworkRedirected &&
               volume.SupportsPersistentAcls &&
               !string.IsNullOrWhiteSpace(volume.FileSystemName) &&
               Pcl2PathNormalizer.AreEquivalent(opened.FinalPath, expected.CanonicalPath);
    }
}
