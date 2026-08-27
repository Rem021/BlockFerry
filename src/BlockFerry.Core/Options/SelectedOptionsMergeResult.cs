namespace BlockFerry.Core.Options;

public sealed record SelectedOptionsMergeResult(
    string Content,
    bool Changed,
    IReadOnlyList<OptionsMergeItem> PlannedChanges,
    IReadOnlyList<OptionsMergeItem> SkippedDifferences,
    IReadOnlyList<OptionsMergeItem> ProtectedDifferences,
    IReadOnlyList<OptionsMergeItem> TargetOnlyItems);
