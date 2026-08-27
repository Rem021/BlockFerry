namespace BlockFerry.Core.Options;

public sealed record OptionsSelectionCatalog(
    IReadOnlyList<OptionSettingDescriptor> SelectableDifferences,
    IReadOnlyList<OptionsMergeItem> RequiredChanges,
    IReadOnlyList<OptionsMergeItem> ProtectedDifferences,
    IReadOnlyList<OptionsMergeItem> TargetOnlyItems);
