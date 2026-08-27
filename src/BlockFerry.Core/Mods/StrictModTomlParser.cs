using System.Text;
using BlockFerry.Core.Content;

namespace BlockFerry.Core.Mods;

internal sealed record ModTomlDeclaration(
    string ModId,
    string Version,
    bool RequiresManifestVersion);

internal static class StrictModTomlParser
{
    private const int MaximumLineUtf8Bytes = 32 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool TryParse(
        ImmutableByteBuffer bytes,
        out ModTomlDeclaration? declaration)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        declaration = null;
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes.CopyBytes());
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (text.Contains('\0', StringComparison.Ordinal) ||
            text.Contains("\"\"\"", StringComparison.Ordinal))
        {
            return false;
        }

        var table = string.Empty;
        var seenTables = new HashSet<string>(StringComparer.Ordinal);
        var arrayTableOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenKeys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [string.Empty] = new(StringComparer.Ordinal),
        };
        string? modId = null;
        string? version = null;
        var modTables = 0;
        var readingDescriptionLiteral = false;
        var descriptionLiteralUtf8Bytes = 0;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var rawLine in lines)
        {
            if (rawLine.Contains('\r', StringComparison.Ordinal) ||
                StrictUtf8.GetByteCount(rawLine) > MaximumLineUtf8Bytes)
            {
                return false;
            }

            if (readingDescriptionLiteral)
            {
                if (!TryConsumeMultilineLiteral(
                        rawLine,
                        ref descriptionLiteralUtf8Bytes,
                        out var closed))
                {
                    return false;
                }

                readingDescriptionLiteral = !closed;
                continue;
            }

            if (TryStartDescriptionLiteral(rawLine, out var literalRemainder))
            {
                if (!string.Equals(table, "array:mods", StringComparison.Ordinal) ||
                    !seenKeys.TryGetValue(table, out var literalKeys) ||
                    !literalKeys.Add("description") ||
                    !TryConsumeMultilineLiteral(
                        literalRemainder,
                        ref descriptionLiteralUtf8Bytes,
                        out var closed))
                {
                    return false;
                }

                readingDescriptionLiteral = !closed;
                continue;
            }

            if (!TryStripComment(rawLine, out var uncommented))
            {
                return false;
            }

            var line = uncommented.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                if (!TryReadTable(line, out table, out var isArrayTable) ||
                    !IsSafeDottedIdentifier(table))
                {
                    return false;
                }

                var declaredTable = table;
                var tableKey = (isArrayTable ? "array:" : "table:") + declaredTable;
                if (isArrayTable)
                {
                    var occurrence = arrayTableOccurrences.TryGetValue(
                        declaredTable,
                        out var previous)
                        ? checked(previous + 1)
                        : 1;
                    arrayTableOccurrences[declaredTable] = occurrence;
                    if (string.Equals(declaredTable, "mods", StringComparison.Ordinal))
                    {
                        modTables++;
                        if (modTables != 1)
                        {
                            return false;
                        }
                    }
                    else
                    {
                        tableKey += "#" + occurrence.ToString(
                            global::System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
                else if (!seenTables.Add(tableKey))
                {
                    return false;
                }

                seenKeys[tableKey] = new HashSet<string>(StringComparer.Ordinal);
                table = tableKey;
                continue;
            }

            if (!TrySplitAssignment(line, out var key, out var rawValue) ||
                !IsSafeIdentifier(key) ||
                !TryValidateValue(rawValue, out var stringValue))
            {
                return false;
            }

            if (!seenKeys.TryGetValue(table, out var keys) || !keys.Add(key))
            {
                return false;
            }

            if (!string.Equals(table, "array:mods", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(key, "modId", StringComparison.Ordinal))
            {
                if (modId is not null || stringValue is null)
                {
                    return false;
                }

                modId = stringValue;
            }
            else if (string.Equals(key, "version", StringComparison.Ordinal))
            {
                if (version is not null || stringValue is null)
                {
                    return false;
                }

                version = stringValue;
            }
        }

        if (readingDescriptionLiteral ||
            modTables != 1 ||
            !ContentValueValidation.IsTechnicalId(modId) ||
            !ContentValueValidation.IsOptionalTechnicalValue(version) ||
            version!.Contains("${", StringComparison.Ordinal) &&
            !string.Equals(version, "${file.jarVersion}", StringComparison.Ordinal))
        {
            return false;
        }

        declaration = new ModTomlDeclaration(
            modId!,
            version,
            string.Equals(version, "${file.jarVersion}", StringComparison.Ordinal));
        return true;
    }

    private static bool TryStartDescriptionLiteral(
        string rawLine,
        out string remainder)
    {
        remainder = string.Empty;
        var line = rawLine.AsSpan().TrimStart();
        const string key = "description";
        if (!line.StartsWith(key, StringComparison.Ordinal))
        {
            return false;
        }

        line = line[key.Length..].TrimStart();
        if (line.IsEmpty || line[0] != '=')
        {
            return false;
        }

        line = line[1..].TrimStart();
        if (!line.StartsWith("'''", StringComparison.Ordinal))
        {
            return false;
        }

        remainder = line[3..].ToString();
        return true;
    }

    private static bool TryConsumeMultilineLiteral(
        string line,
        ref int consumedUtf8Bytes,
        out bool closed)
    {
        var closing = line.IndexOf("'''", StringComparison.Ordinal);
        var content = closing < 0 ? line : line[..closing];
        var contentBytes = StrictUtf8.GetByteCount(content);
        var lineBreakBytes = closing < 0 ? 1 : 0;
        if (contentBytes > MaximumLineUtf8Bytes - consumedUtf8Bytes - lineBreakBytes)
        {
            closed = false;
            return false;
        }

        consumedUtf8Bytes += contentBytes + lineBreakBytes;
        if (closing < 0)
        {
            closed = false;
            return true;
        }

        var trailing = line[(closing + 3)..];
        if (!TryStripComment(trailing, out var uncommented) ||
            uncommented.Trim().Length != 0)
        {
            closed = false;
            return false;
        }

        closed = true;
        return true;
    }

    private static bool TryStripComment(string line, out string value)
    {
        var inString = false;
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (inString && escaped)
            {
                escaped = false;
                continue;
            }

            if (inString && character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                inString = !inString;
                continue;
            }

            if (!inString && character == '#')
            {
                value = line[..index];
                return true;
            }
        }

        value = line;
        return !inString && !escaped;
    }

    private static bool TryReadTable(
        string line,
        out string table,
        out bool isArrayTable)
    {
        table = string.Empty;
        isArrayTable = line.StartsWith("[[", StringComparison.Ordinal);
        var opening = isArrayTable ? 2 : 1;
        var closing = isArrayTable ? "]]" : "]";
        if (!line.EndsWith(closing, StringComparison.Ordinal) ||
            line.Length <= opening + closing.Length)
        {
            return false;
        }

        table = line[opening..^closing.Length].Trim();
        return table.Length > 0 && !table.Contains('[', StringComparison.Ordinal) &&
               !table.Contains(']', StringComparison.Ordinal);
    }

    private static bool TrySplitAssignment(
        string line,
        out string key,
        out string value)
    {
        key = string.Empty;
        value = string.Empty;
        var inString = false;
        var escaped = false;
        var separator = -1;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (inString && escaped)
            {
                escaped = false;
            }
            else if (inString && character == '\\')
            {
                escaped = true;
            }
            else if (character == '"')
            {
                inString = !inString;
            }
            else if (!inString && character == '=')
            {
                if (separator >= 0)
                {
                    return false;
                }

                separator = index;
            }
        }

        if (inString || escaped || separator <= 0 || separator == line.Length - 1)
        {
            return false;
        }

        key = line[..separator].Trim();
        value = line[(separator + 1)..].Trim();
        return key.Length > 0 && value.Length > 0;
    }

    private static bool TryValidateValue(string value, out string? stringValue)
    {
        stringValue = null;
        if (value.StartsWith('"'))
        {
            if (!value.EndsWith('"') || value.Length < 2)
            {
                return false;
            }

            var builder = new StringBuilder(value.Length - 2);
            for (var index = 1; index < value.Length - 1; index++)
            {
                var character = value[index];
                if (character == '\\')
                {
                    if (++index >= value.Length - 1)
                    {
                        return false;
                    }

                    character = value[index] switch
                    {
                        '"' => '"',
                        '\\' => '\\',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => '\0',
                    };
                    if (character == '\0')
                    {
                        return false;
                    }
                }

                if (char.IsControl(character))
                {
                    return false;
                }

                builder.Append(character);
            }

            stringValue = builder.ToString();
            return stringValue.Length <= ContentContractLimits.MaximumVisibleTextUtf16Length;
        }

        if (value is "true" or "false")
        {
            return true;
        }

        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            return value.Length <= MaximumLineUtf8Bytes &&
                   value.Count(character => character == '[') == 1 &&
                   value.Count(character => character == ']') == 1;
        }

        return value.Length <= 128 && value.All(character =>
            char.IsAsciiDigit(character) || character is '-' or '+' or '.');
    }

    private static bool IsSafeDottedIdentifier(string value) =>
        value.Split('.').All(IsSafeIdentifier);

    private static bool IsSafeIdentifier(string value) =>
        value is { Length: > 0 and <= 128 } &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
