using System.Globalization;
using System.Text;
using BlockFerry.Core.Options;

namespace BlockFerry.App.WinUI.Selection;

internal static class OptionsPreviewResultFormatter
{
    private const int TechnicalKeyLimit = 120;
    private const int ValueLimit = 72;

    public static string FormatDifference(OptionsMergeItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return $"{EscapeAndCap(item.Key, TechnicalKeyLimit)}: " +
               $"{FormatValue(item.TargetValue)} → {FormatValue(item.FinalValue)}";
    }

    private static string FormatValue(string? value) =>
        value is null ? "∅" : EscapeAndCap(value, ValueLimit);

    private static string EscapeAndCap(string value, int limit)
    {
        var escaped = new StringBuilder(Math.Min(value.Length, limit) + 1);
        foreach (var character in value)
        {
            var visible = char.IsControl(character)
                ? string.Format(CultureInfo.InvariantCulture, "\\u{0:X4}", (int)character)
                : character.ToString();
            if (escaped.Length + visible.Length > limit)
            {
                escaped.Append('…');
                break;
            }

            escaped.Append(visible);
        }

        return escaped.ToString();
    }
}
