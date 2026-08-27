using System.Text;
using BlockFerry.Core.Content;

namespace BlockFerry.Core.Mods;

internal static class ManifestVersionReader
{
    private const int MaximumUnfoldedLineUtf8Bytes = 32 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool TryReadImplementationVersion(
        ImmutableByteBuffer bytes,
        out string? version)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        version = null;
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes.CopyBytes());
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (text.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        var physicalLines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
        var logicalLines = new List<string>();
        foreach (var physical in physicalLines)
        {
            if (physical.Contains('\r', StringComparison.Ordinal))
            {
                return false;
            }

            if (physical.StartsWith(' '))
            {
                if (logicalLines.Count == 0)
                {
                    return false;
                }

                logicalLines[^1] += physical[1..];
                if (StrictUtf8.GetByteCount(logicalLines[^1]) > MaximumUnfoldedLineUtf8Bytes)
                {
                    return false;
                }

                continue;
            }

            if (StrictUtf8.GetByteCount(physical) > MaximumUnfoldedLineUtf8Bytes)
            {
                return false;
            }

            logicalLines.Add(physical);
        }

        var found = false;
        foreach (var line in logicalLines)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 || separator + 1 >= line.Length || line[separator + 1] != ' ')
            {
                return false;
            }

            var name = line[..separator];
            var value = line[(separator + 2)..];
            if (name.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_') ||
                value.Length == 0 ||
                value.Any(char.IsControl))
            {
                return false;
            }

            if (!string.Equals(name, "Implementation-Version", StringComparison.Ordinal))
            {
                continue;
            }

            if (found || !ContentValueValidation.IsOptionalTechnicalValue(value))
            {
                return false;
            }

            found = true;
            version = value;
        }

        return found;
    }
}
