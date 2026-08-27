using System.Text;

namespace BlockFerry.Core.Options;

/// <summary>
/// Produces an idempotent, semantic merge of a player's options into a newer instance.
/// Pack-owned resource selection stays with the target. An initialized target keeps its
/// schema version; an uninitialized target receives the source version as a required precondition.
/// </summary>
public sealed class OptionsMergePlanner
{
    private static readonly IReadOnlySet<string> DefaultProtectedKeys = new HashSet<string>(
        ["resourcePacks", "incompatibleResourcePacks"],
        StringComparer.Ordinal);

    private readonly HashSet<string> _protectedKeys;

    public OptionsMergePlanner(IReadOnlySet<string>? protectedKeys = null)
    {
        var effectiveProtectedKeys = new HashSet<string>(DefaultProtectedKeys, StringComparer.Ordinal);
        if (protectedKeys is not null)
        {
            effectiveProtectedKeys.UnionWith(protectedKeys);
        }

        _protectedKeys = effectiveProtectedKeys;
    }

    public OptionsMergeResult Plan(string? sourceContent, string? targetContent)
    {
        var source = ColonOptionsDocument.Parse(sourceContent);
        var target = ColonOptionsDocument.Parse(targetContent);
        var output = new List<string>();
        var emittedKeys = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<OptionsMergeItem>();
        var targetHasSchemaVersion = target.LastLineByKey.ContainsKey(OptionsSchemaVersionPolicy.Key);

        for (var index = 0; index < target.Lines.Count; index++)
        {
            var targetLine = target.Lines[index];
            if (targetLine.Key is null)
            {
                output.Add(targetLine.Raw);
                continue;
            }

            if (target.LastIndexByKey[targetLine.Key] != index || !emittedKeys.Add(targetLine.Key))
            {
                continue;
            }

            if (IsProtected(targetLine.Key, targetHasSchemaVersion))
            {
                output.Add(targetLine.Raw);
                source.LastLineByKey.TryGetValue(targetLine.Key, out var protectedSourceLine);
                items.Add(new OptionsMergeItem(
                    targetLine.Key,
                    protectedSourceLine?.Value,
                    targetLine.Value,
                    targetLine.Value,
                    OptionsMergeDecision.PreserveTarget,
                    string.Equals(targetLine.Key, OptionsSchemaVersionPolicy.Key, StringComparison.Ordinal)
                        ? "The initialized target keeps its own options data version."
                        : "This key belongs to the target pack."));
                continue;
            }

            if (source.LastLineByKey.TryGetValue(targetLine.Key, out var sourceLine))
            {
                output.Add(sourceLine.Raw);
                items.Add(new OptionsMergeItem(
                    targetLine.Key,
                    sourceLine.Value,
                    targetLine.Value,
                    sourceLine.Value,
                    OptionsMergeDecision.UseSource,
                    "This is a player-owned option and is carried forward from the source instance."));
            }
            else
            {
                output.Add(targetLine.Raw);
                items.Add(new OptionsMergeItem(
                    targetLine.Key,
                    null,
                    targetLine.Value,
                    targetLine.Value,
                    OptionsMergeDecision.PreserveTargetOnly,
                    "This option exists only in the target and is preserved."));
            }
        }

        foreach (var sourceEntry in source.LastLineByKey
                     .OrderBy(entry => string.Equals(
                         entry.Key,
                         OptionsSchemaVersionPolicy.Key,
                         StringComparison.Ordinal) ? 0 : 1)
                     .ThenBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (IsProtected(sourceEntry.Key, targetHasSchemaVersion) ||
                !emittedKeys.Add(sourceEntry.Key))
            {
                continue;
            }

            output.Add(sourceEntry.Value.Raw);
            items.Add(new OptionsMergeItem(
                sourceEntry.Key,
                sourceEntry.Value.Value,
                null,
                sourceEntry.Value.Value,
                OptionsMergeDecision.UseSource,
                string.Equals(sourceEntry.Key, OptionsSchemaVersionPolicy.Key, StringComparison.Ordinal)
                    ? "The uninitialized target requires the source data version before modern options can be loaded safely."
                    : "This player-owned option is missing in the target and is added."));
        }

        var newline = target.OriginalContent.Length > 0 ? target.Newline : source.Newline;
        var hasTrailingNewline = target.OriginalContent.Length > 0
            ? target.HasTrailingNewline
            : source.HasTrailingNewline;
        var merged = output.Count == 0
            ? string.Empty
            : string.Join(newline, output) + (hasTrailingNewline ? newline : string.Empty);

        return new OptionsMergeResult(
            merged,
            !string.Equals(merged, target.OriginalContent, StringComparison.Ordinal),
            items.AsReadOnly());
    }

    public SelectedOptionsMergeResult PlanSelected(
        string? sourceContent,
        string? targetContent,
        IReadOnlySet<string> selectedSourceKeys)
    {
        ArgumentNullException.ThrowIfNull(selectedSourceKeys);

        var source = ColonOptionsDocument.Parse(sourceContent);
        var target = ColonOptionsDocument.Parse(targetContent);
        var selectedKeys = new HashSet<string>(selectedSourceKeys, StringComparer.Ordinal);
        var targetHasSchemaVersion = target.LastLineByKey.ContainsKey(OptionsSchemaVersionPolicy.Key);
        var shouldCarrySchemaVersion = !targetHasSchemaVersion && selectedKeys.Any(key =>
            !string.Equals(key, OptionsSchemaVersionPolicy.Key, StringComparison.Ordinal) &&
            !IsProtected(key, targetHasSchemaVersion) &&
            source.LastLineByKey.TryGetValue(key, out var sourceLine) &&
            (!target.LastLineByKey.TryGetValue(key, out var targetLine) ||
             !string.Equals(sourceLine.Value, targetLine.Value, StringComparison.Ordinal)));
        var effectiveSelectedKeys = selectedKeys
            .Where(key => !string.Equals(
                key,
                OptionsSchemaVersionPolicy.Key,
                StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        if (shouldCarrySchemaVersion && source.LastLineByKey.ContainsKey(OptionsSchemaVersionPolicy.Key))
        {
            effectiveSelectedKeys.Add(OptionsSchemaVersionPolicy.Key);
        }

        var plannedChanges = new List<OptionsMergeItem>();
        var skippedDifferences = new List<OptionsMergeItem>();
        var protectedDifferences = new List<OptionsMergeItem>();

        foreach (var sourceEntry in source.LastLineByKey.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            target.LastLineByKey.TryGetValue(sourceEntry.Key, out var targetLine);
            if (targetLine is not null && string.Equals(sourceEntry.Value.Value, targetLine.Value, StringComparison.Ordinal))
            {
                continue;
            }

            if (IsProtected(sourceEntry.Key, targetHasSchemaVersion))
            {
                protectedDifferences.Add(new OptionsMergeItem(
                    sourceEntry.Key,
                    sourceEntry.Value.Value,
                    targetLine?.Value,
                    targetLine?.Value,
                    OptionsMergeDecision.PreserveTarget,
                    string.Equals(sourceEntry.Key, OptionsSchemaVersionPolicy.Key, StringComparison.Ordinal)
                        ? "The initialized target keeps its own options data version."
                        : "This key belongs to the target pack."));
            }
            else if (effectiveSelectedKeys.Contains(sourceEntry.Key))
            {
                plannedChanges.Add(new OptionsMergeItem(
                    sourceEntry.Key,
                    sourceEntry.Value.Value,
                    targetLine?.Value,
                    sourceEntry.Value.Value,
                    OptionsMergeDecision.UseSource,
                    string.Equals(sourceEntry.Key, OptionsSchemaVersionPolicy.Key, StringComparison.Ordinal)
                        ? "The uninitialized target requires the source data version before modern options can be loaded safely."
                        : targetLine is null
                        ? "This selected player-owned option is missing in the target and is added."
                        : "This selected player-owned option is carried forward from the source instance."));
            }
            else
            {
                skippedDifferences.Add(new OptionsMergeItem(
                    sourceEntry.Key,
                    sourceEntry.Value.Value,
                    targetLine?.Value,
                    targetLine?.Value,
                    OptionsMergeDecision.PreserveTarget,
                    "This player-owned option differs but was not selected."));
            }
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

        if (plannedChanges.Count == 0)
        {
            return new SelectedOptionsMergeResult(
                target.OriginalContent,
                false,
                plannedChanges.AsReadOnly(),
                skippedDifferences.AsReadOnly(),
                protectedDifferences.AsReadOnly(),
                targetOnlyItems.AsReadOnly());
        }

        var output = new StringBuilder();
        var targetSegments = SplitRawSegments(target.OriginalContent);
        var outputEndsWithTerminator = false;
        for (var index = 0; index < target.Lines.Count; index++)
        {
            var targetLine = target.Lines[index];
            var targetSegment = targetSegments[index];
            if (targetLine.Key is null
                || IsProtected(targetLine.Key, targetHasSchemaVersion)
                || !effectiveSelectedKeys.Contains(targetLine.Key)
                || !source.LastLineByKey.TryGetValue(targetLine.Key, out var sourceLine))
            {
                output.Append(targetSegment.Raw);
                output.Append(targetSegment.Terminator);
                outputEndsWithTerminator = targetSegment.Terminator.Length > 0;
                continue;
            }

            if (target.LastIndexByKey[targetLine.Key] == index)
            {
                output.Append(sourceLine.Raw);
                output.Append(targetSegment.Terminator);
                outputEndsWithTerminator = targetSegment.Terminator.Length > 0;
            }
        }

        var selectedSourceOnlyEntries = source.LastLineByKey
            .Where(entry => !IsProtected(entry.Key, targetHasSchemaVersion)
                && effectiveSelectedKeys.Contains(entry.Key)
                && !target.LastLineByKey.ContainsKey(entry.Key))
            .OrderBy(entry => string.Equals(
                entry.Key,
                OptionsSchemaVersionPolicy.Key,
                StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .ToList();

        var newline = target.OriginalContent.Length > 0 ? target.Newline : source.Newline;
        var hasTrailingNewline = target.OriginalContent.Length > 0
            ? target.HasTrailingNewline
            : source.HasTrailingNewline;
        if (selectedSourceOnlyEntries.Count > 0 && output.Length > 0 && !outputEndsWithTerminator)
        {
            output.Append(newline);
        }

        for (var index = 0; index < selectedSourceOnlyEntries.Count; index++)
        {
            output.Append(selectedSourceOnlyEntries[index].Value.Raw);
            if (index < selectedSourceOnlyEntries.Count - 1 || hasTrailingNewline)
            {
                output.Append(newline);
            }
        }

        var merged = output.ToString();

        return new SelectedOptionsMergeResult(
            merged,
            !string.Equals(merged, target.OriginalContent, StringComparison.Ordinal),
            plannedChanges.AsReadOnly(),
            skippedDifferences.AsReadOnly(),
            protectedDifferences.AsReadOnly(),
            targetOnlyItems.AsReadOnly());
    }

    private bool IsProtected(string key, bool targetHasSchemaVersion) =>
        string.Equals(key, OptionsSchemaVersionPolicy.Key, StringComparison.Ordinal)
            ? targetHasSchemaVersion
            : _protectedKeys.Contains(key);

    private static List<RawOptionsSegment> SplitRawSegments(string content)
    {
        var segments = new List<RawOptionsSegment>();
        var rawStart = 0;
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] == '\r' && index + 1 < content.Length && content[index + 1] == '\n')
            {
                segments.Add(new RawOptionsSegment(content[rawStart..index], "\r\n"));
                index++;
                rawStart = index + 1;
            }
            else if (content[index] == '\n')
            {
                segments.Add(new RawOptionsSegment(content[rawStart..index], "\n"));
                rawStart = index + 1;
            }
        }

        if (rawStart < content.Length)
        {
            segments.Add(new RawOptionsSegment(content[rawStart..], string.Empty));
        }

        return segments;
    }

    private sealed record RawOptionsSegment(string Raw, string Terminator);
}
