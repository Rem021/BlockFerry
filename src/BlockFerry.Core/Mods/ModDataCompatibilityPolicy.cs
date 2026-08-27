using System.Globalization;

namespace BlockFerry.Core.Mods;

internal static class ModDataCompatibilityPolicy
{
    internal const string SupportedMinecraftVersion = "1.21.1";

    internal static bool IsSupportedMinecraftPair(
        string? sourceMinecraftVersion,
        string? targetMinecraftVersion) =>
        string.Equals(
            sourceMinecraftVersion,
            SupportedMinecraftVersion,
            StringComparison.Ordinal) &&
        string.Equals(
            targetMinecraftVersion,
            SupportedMinecraftVersion,
            StringComparison.Ordinal);

    internal static bool IsSupportedMinecraftTarget(string? targetMinecraftVersion) =>
        string.Equals(
            targetMinecraftVersion,
            SupportedMinecraftVersion,
            StringComparison.Ordinal);

    internal static bool AreModVersionsCompatible(
        string modId,
        string? sourceVersion,
        string? targetVersion) =>
        TryGetSupportedMajor(modId, out var supportedMajor) &&
        TryReadModMajor(modId, sourceVersion, out var sourceMajor) &&
        TryReadModMajor(modId, targetVersion, out var targetMajor) &&
        sourceMajor == supportedMajor &&
        targetMajor == supportedMajor;

    internal static bool IsSupportedTargetModVersion(
        string modId,
        string? targetVersion) =>
        TryGetSupportedMajor(modId, out var supportedMajor) &&
        TryReadModMajor(modId, targetVersion, out var targetMajor) &&
        targetMajor == supportedMajor;

    internal static string? SupportedLineDisplay(string adapterId) => adapterId switch
    {
        "jei" => "JEI 19.x",
        "esm" => "Extreme Sound Muffler 3.x",
        "appearance" => "Dark Mode Everywhere 1.x",
        _ => null,
    };

    internal static string? ModIdForAdapter(string adapterId) => adapterId switch
    {
        "jei" => "jei",
        "esm" => "extremesoundmuffler",
        "appearance" => "darkmodeeverywhere",
        _ => null,
    };

    private static bool TryGetSupportedMajor(string modId, out int major)
    {
        major = modId switch
        {
            "jei" => 19,
            "extremesoundmuffler" => 3,
            "fancymenu" => 3,
            "darkmodeeverywhere" => 1,
            _ => 0,
        };
        return major > 0;
    }

    private static bool TryReadMajor(string? version, out int major)
    {
        major = 0;
        if (string.IsNullOrEmpty(version) || version.Length > 128)
        {
            return false;
        }

        var dot = version.IndexOf('.');
        if (dot <= 0 || dot == version.Length - 1)
        {
            return false;
        }

        for (var index = 0; index < version.Length; index++)
        {
            var value = version[index];
            if (!(value is >= '0' and <= '9' or
                  >= 'A' and <= 'Z' or
                  >= 'a' and <= 'z' or
                  '.' or '+' or '-' or '_'))
            {
                return false;
            }
        }

        return int.TryParse(
                   version.AsSpan(0, dot),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out major) &&
               major > 0;
    }

    private static bool TryReadModMajor(
        string modId,
        string? version,
        out int major)
    {
        major = 0;
        if (string.Equals(modId, "darkmodeeverywhere", StringComparison.Ordinal) &&
            version is { Length: > 0 })
        {
            var minecraftPrefix = SupportedMinecraftVersion + "-";
            if (!version.StartsWith(minecraftPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            version = version[minecraftPrefix.Length..];
        }

        return TryReadMajor(version, out major);
    }
}
