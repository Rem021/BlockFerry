namespace BlockFerry.Core.Content;

internal readonly record struct ResourceLocation1211(
    string RawValue,
    string CanonicalValue);

internal static class ResourceLocationValidator
{
    private const string DefaultNamespace = "minecraft";

    internal static bool TryParse1211(
        string value,
        out ResourceLocation1211 parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator >= 0 && value.IndexOf(':', separator + 1) >= 0)
        {
            return false;
        }

        var namespacePart = separator < 0
            ? DefaultNamespace
            : separator == 0
                ? DefaultNamespace
                : value[..separator];
        var pathPart = separator < 0 ? value : value[(separator + 1)..];
        if (pathPart.Length == 0 ||
            !namespacePart.All(IsNamespaceCharacter) ||
            !pathPart.All(IsPathCharacter))
        {
            return false;
        }

        parsed = new ResourceLocation1211(
            value,
            namespacePart + ":" + pathPart);
        return true;
    }

    private static bool IsNamespaceCharacter(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-';

    private static bool IsPathCharacter(char value) =>
        IsNamespaceCharacter(value) || value == '/';
}
