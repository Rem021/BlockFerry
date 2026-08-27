namespace BlockFerry.Core.Options;

public sealed class OptionsSelectionCatalogBuilder
{
    private static readonly string[] FixedProtectedKeys =
    [
        "resourcePacks",
        "incompatibleResourcePacks",
    ];

    private readonly OptionSettingClassifier _classifier;

    public OptionsSelectionCatalogBuilder(OptionSettingClassifier? classifier = null)
    {
        _classifier = classifier ?? new OptionSettingClassifier();
    }

    public OptionsSelectionCatalog Build(
        string? sourceContent,
        string? targetContent,
        IReadOnlySet<string>? protectedKeys = null)
    {
        var source = ColonOptionsDocument.Parse(sourceContent);
        var target = ColonOptionsDocument.Parse(targetContent);
        var effectiveProtectedKeys = new HashSet<string>(FixedProtectedKeys, StringComparer.Ordinal);
        if (protectedKeys is not null)
        {
            effectiveProtectedKeys.UnionWith(protectedKeys);
        }

        var selectableDifferences = new List<OptionSettingDescriptor>();
        var requiredChanges = new List<OptionsMergeItem>();
        var protectedDifferences = new List<OptionsMergeItem>();
        foreach (var sourceEntry in source.LastLineByKey.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            target.LastLineByKey.TryGetValue(sourceEntry.Key, out var targetLine);
            if (targetLine is not null && string.Equals(sourceEntry.Value.Value, targetLine.Value, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(sourceEntry.Key, OptionsSchemaVersionPolicy.Key, StringComparison.Ordinal))
            {
                if (targetLine is null)
                {
                    requiredChanges.Add(new OptionsMergeItem(
                        sourceEntry.Key,
                        sourceEntry.Value.Value,
                        null,
                        sourceEntry.Value.Value,
                        OptionsMergeDecision.UseSource,
                        "The uninitialized target requires a data version before modern options can be loaded safely."));
                }
                else
                {
                    protectedDifferences.Add(new OptionsMergeItem(
                        sourceEntry.Key,
                        sourceEntry.Value.Value,
                        targetLine.Value,
                        targetLine.Value,
                        OptionsMergeDecision.PreserveTarget,
                        "The initialized target keeps its own options data version."));
                }

                continue;
            }

            if (effectiveProtectedKeys.Contains(sourceEntry.Key))
            {
                protectedDifferences.Add(new OptionsMergeItem(
                    sourceEntry.Key,
                    sourceEntry.Value.Value,
                    targetLine?.Value,
                    targetLine?.Value,
                    OptionsMergeDecision.PreserveTarget,
                    "This key belongs to the target pack or target options schema."));
                continue;
            }

            selectableDifferences.Add(new OptionSettingDescriptor(
                sourceEntry.Key,
                _classifier.GetDisplayName(sourceEntry.Key),
                _classifier.GetDisplayKey(sourceEntry.Key),
                _classifier.Classify(sourceEntry.Key),
                sourceEntry.Value.Value,
                targetLine?.Value));
        }

        var targetOnlyItems = target.LastLineByKey
            .Where(entry => !source.LastLineByKey.ContainsKey(entry.Key))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new OptionsMergeItem(
                entry.Key,
                null,
                entry.Value.Value,
                entry.Value.Value,
                OptionsMergeDecision.PreserveTargetOnly,
                "This option exists only in the target and is preserved."))
            .ToList();

        return new OptionsSelectionCatalog(
            selectableDifferences.AsReadOnly(),
            requiredChanges.AsReadOnly(),
            protectedDifferences.AsReadOnly(),
            targetOnlyItems.AsReadOnly());
    }
}
