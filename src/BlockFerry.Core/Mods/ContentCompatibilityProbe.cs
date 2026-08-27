using BlockFerry.Core.Content;

namespace BlockFerry.Core.Mods;

internal sealed class ContentCompatibilityProbe(ModPresenceProbe modPresenceProbe)
{
    private static readonly IReadOnlySet<string> RequiredModIds =
        new ReadOnlySet<string>(
            ["jei", "extremesoundmuffler", "emi", "fancymenu", "darkmodeeverywhere"],
            StringComparer.Ordinal);

    internal ContentProbeContext ProbeAndCreateContext(
        ContentAccessLease lease,
        ModProbeLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(limits);
        lease.ThrowIfUnavailable();
        var source = modPresenceProbe.Probe(
            lease.Source,
            RequiredModIds,
            limits,
            cancellationToken);
        var target = modPresenceProbe.Probe(
            lease.Target,
            RequiredModIds,
            limits,
            cancellationToken);
        lease.ThrowIfUnavailable();

        var sourceVersions = UniqueVersions(source.Evidence);
        var targetVersions = UniqueVersions(target.Evidence);
        var unsupported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var diagnostic in source.Diagnostics.Concat(target.Diagnostics))
        {
            if (diagnostic.ItemId is { } itemId &&
                string.Equals(itemId.AdapterId, "mods", StringComparison.Ordinal))
            {
                unsupported.Add(itemId.TechnicalKey);
            }
        }

        ApplyFormatCompatibility(
            lease.Source.Identity.MinecraftVersion,
            lease.Target.Identity.MinecraftVersion,
            sourceVersions,
            targetVersions,
            unsupported);
        return lease.CreateProbeContext(AdapterCompatibilityEvidence.Create(
            lease.Source.Identity.MinecraftVersion,
            lease.Target.Identity.MinecraftVersion,
            sourceVersions,
            targetVersions,
            unsupported));
    }

    private static List<KeyValuePair<string, string>> UniqueVersions(
        IReadOnlyList<ModPresenceEvidence> evidence) =>
        evidence
            .Where(item => item.Version is not null)
            .OrderBy(item => item.ModId, StringComparer.Ordinal)
            .Select(item => new KeyValuePair<string, string>(item.ModId, item.Version!))
            .ToList();

    private static void ApplyFormatCompatibility(
        string? sourceMinecraftVersion,
        string? targetMinecraftVersion,
        IReadOnlyList<KeyValuePair<string, string>> sourceVersions,
        IReadOnlyList<KeyValuePair<string, string>> targetVersions,
        HashSet<string> unsupported)
    {
        var minecraftMatches = ModDataCompatibilityPolicy.IsSupportedMinecraftPair(
            sourceMinecraftVersion,
            targetMinecraftVersion);
        RequireCompatibleFamily(
            "jei",
            minecraftMatches,
            sourceVersions,
            targetVersions,
            unsupported);
        RequireCompatibleFamily(
            "extremesoundmuffler",
            minecraftMatches,
            sourceVersions,
            targetVersions,
            unsupported);
        RequireOptionalCompatibleFamily(
            "fancymenu",
            minecraftMatches,
            sourceVersions,
            targetVersions,
            unsupported);
        RequireOptionalCompatibleFamily(
            "darkmodeeverywhere",
            minecraftMatches,
            sourceVersions,
            targetVersions,
            unsupported);
        if (sourceVersions.Any(pair => pair.Key == "emi") ||
            targetVersions.Any(pair => pair.Key == "emi"))
        {
            unsupported.Add("emi");
        }
    }

    private static void RequireOptionalCompatibleFamily(
        string modId,
        bool minecraftMatches,
        IReadOnlyList<KeyValuePair<string, string>> sourceVersions,
        IReadOnlyList<KeyValuePair<string, string>> targetVersions,
        HashSet<string> unsupported)
    {
        var source = sourceVersions.SingleOrDefault(pair => pair.Key == modId);
        var target = targetVersions.SingleOrDefault(pair => pair.Key == modId);
        if (source.Key is null && target.Key is null)
        {
            return;
        }

        if (!minecraftMatches ||
            !ModDataCompatibilityPolicy.AreModVersionsCompatible(
                modId,
                source.Value,
                target.Value))
        {
            unsupported.Add(modId);
            return;
        }

        unsupported.Remove(modId);
    }

    private static void RequireCompatibleFamily(
        string modId,
        bool minecraftMatches,
        IReadOnlyList<KeyValuePair<string, string>> sourceVersions,
        IReadOnlyList<KeyValuePair<string, string>> targetVersions,
        HashSet<string> unsupported)
    {
        var source = sourceVersions.SingleOrDefault(pair => pair.Key == modId);
        var target = targetVersions.SingleOrDefault(pair => pair.Key == modId);
        if (!minecraftMatches ||
            !ModDataCompatibilityPolicy.AreModVersionsCompatible(
                modId,
                source.Value,
                target.Value))
        {
            unsupported.Add(modId);
        }
    }
}
