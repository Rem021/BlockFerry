namespace BlockFerry.Core.Options;

public sealed record OptionSettingDescriptor(
    string Key,
    string DisplayName,
    string DisplayKey,
    OptionSettingCategory Category,
    string? SourceValue,
    string? TargetValue);
