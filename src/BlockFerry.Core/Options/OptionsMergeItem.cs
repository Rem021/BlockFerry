namespace BlockFerry.Core.Options;

public sealed record OptionsMergeItem(
    string Key,
    string? SourceValue,
    string? TargetValue,
    string? FinalValue,
    OptionsMergeDecision Decision,
    string Reason);
