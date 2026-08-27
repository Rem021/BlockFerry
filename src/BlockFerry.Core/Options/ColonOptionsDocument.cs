using System.Collections.ObjectModel;

namespace BlockFerry.Core.Options;

/// <summary>
/// Parses Minecraft's colon-delimited options format without interpreting the value.
/// Values may themselves contain colons, so only the first separator is significant.
/// </summary>
public sealed class ColonOptionsDocument
{
    private readonly IReadOnlyList<OptionsLine> _lines;
    private readonly IReadOnlyDictionary<string, OptionsLine> _lastLineByKey;
    private readonly IReadOnlyDictionary<string, int> _lastIndexByKey;

    private ColonOptionsDocument(
        string originalContent,
        string newline,
        bool hasTrailingNewline,
        IReadOnlyList<OptionsLine> lines,
        IReadOnlyDictionary<string, OptionsLine> lastLineByKey,
        IReadOnlyDictionary<string, int> lastIndexByKey)
    {
        OriginalContent = originalContent;
        Newline = newline;
        HasTrailingNewline = hasTrailingNewline;
        _lines = lines;
        _lastLineByKey = lastLineByKey;
        _lastIndexByKey = lastIndexByKey;
    }

    public string OriginalContent { get; }

    public string Newline { get; }

    public bool HasTrailingNewline { get; }

    public IReadOnlyList<OptionsLine> Lines => _lines;

    public IReadOnlyDictionary<string, OptionsLine> LastLineByKey => _lastLineByKey;

    public IReadOnlyDictionary<string, int> LastIndexByKey => _lastIndexByKey;

    public static ColonOptionsDocument Parse(string? content)
    {
        content ??= string.Empty;
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var hasTrailingNewline = content.EndsWith('\n');
        var rawLines = content.Length == 0
            ? []
            : content.Split(["\r\n", "\n"], StringSplitOptions.None);

        if (hasTrailingNewline && rawLines.Length > 0 && rawLines[^1].Length == 0)
        {
            rawLines = rawLines[..^1];
        }

        var lines = new List<OptionsLine>(rawLines.Length);
        var lastLineByKey = new Dictionary<string, OptionsLine>(StringComparer.Ordinal);
        var lastIndexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var rawLine in rawLines)
        {
            var separator = rawLine.IndexOf(':', StringComparison.Ordinal);
            var line = separator > 0
                ? new OptionsLine(rawLine, rawLine[..separator], rawLine[(separator + 1)..])
                : new OptionsLine(rawLine, null, null);

            lines.Add(line);
            if (line.Key is not null)
            {
                lastLineByKey[line.Key] = line;
                lastIndexByKey[line.Key] = lines.Count - 1;
            }
        }

        return new ColonOptionsDocument(
            content,
            newline,
            hasTrailingNewline,
            new ReadOnlyCollection<OptionsLine>(lines),
            new ReadOnlyDictionary<string, OptionsLine>(lastLineByKey),
            new ReadOnlyDictionary<string, int>(lastIndexByKey));
    }
}

public sealed record OptionsLine(string Raw, string? Key, string? Value);
