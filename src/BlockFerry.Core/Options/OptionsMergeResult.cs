namespace BlockFerry.Core.Options;

public sealed record OptionsMergeResult(
    string Content,
    bool Changed,
    IReadOnlyList<OptionsMergeItem> Items);
