using System.Globalization;

namespace BlockFerry.Core.Options;

internal static class OptionsSchemaVersionPolicy
{
    internal const string Key = "version";

    internal static bool IsSafeForMigration(string? sourceContent, string? targetContent)
    {
        var source = Read(sourceContent);
        var target = Read(targetContent);
        return source.State == VersionState.Valid &&
               target.State is VersionState.Missing or VersionState.Valid;
    }

    private static VersionRead Read(string? content)
    {
        var document = ColonOptionsDocument.Parse(content);
        var lines = document.Lines
            .Where(line => string.Equals(line.Key, Key, StringComparison.Ordinal))
            .ToArray();
        if (lines.Length == 0)
        {
            return new VersionRead(VersionState.Missing, null);
        }

        if (lines.Length != 1 ||
            lines[0].Value is not { Length: > 0 } value ||
            value.Any(character => character is < '0' or > '9') ||
            !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed <= 0)
        {
            return new VersionRead(VersionState.Invalid, null);
        }

        return new VersionRead(VersionState.Valid, value);
    }

    private enum VersionState
    {
        Missing,
        Valid,
        Invalid,
    }

    private sealed record VersionRead(VersionState State, string? Value);
}

internal sealed class OptionsSchemaVersionException : Exception
{
    internal OptionsSchemaVersionException()
        : base("The options data version is missing or invalid for a safe migration.")
    {
    }
}
