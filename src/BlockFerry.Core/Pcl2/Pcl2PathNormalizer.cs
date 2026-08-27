namespace BlockFerry.Core.Pcl2;

public static class Pcl2PathNormalizer
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>
    /// Compares normalized Windows path spellings (absolute path, dot segments,
    /// trailing separator, and casing). Physical aliases such as junctions and
    /// 8.3 short names require an additional transaction-time safety gate before
    /// any future write-capable workflow.
    /// </summary>
    public static bool AreEquivalent(string firstPath, string secondPath) =>
        string.Equals(
            Normalize(firstPath),
            Normalize(secondPath),
            StringComparison.OrdinalIgnoreCase);

    internal static bool TryNormalize(string? path, out string? normalizedPath)
    {
        normalizedPath = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            normalizedPath = Normalize(path);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
