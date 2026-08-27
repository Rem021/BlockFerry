using System.Text;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Processes;

internal interface IMinecraftArgumentFileReader
{
    bool TryRead(
        string path,
        IReadOnlyList<string> approvedRoots,
        out string content);
}

internal enum MinecraftProcessClassification
{
    Unrelated,
    Minecraft,
    UnsafeCandidate,
}

internal sealed class MinecraftProcessEvidence
{
    internal MinecraftProcessEvidence(
        MinecraftProcessClassification classification,
        string? mainClass,
        string? gameDirectory,
        IReadOnlyList<string> argumentFileLocations)
    {
        Classification = classification;
        MainClass = mainClass;
        GameDirectory = gameDirectory;
        ArgumentFileLocations = argumentFileLocations;
    }

    internal MinecraftProcessClassification Classification { get; }

    internal string? MainClass { get; }

    internal string? GameDirectory { get; }

    internal IReadOnlyList<string> ArgumentFileLocations { get; }

    public override string ToString() =>
        $"Minecraft process evidence: {Classification}; main class: {MainClass ?? "unavailable"}; " +
        $"game directory: {(GameDirectory is null ? "unavailable" : "verified candidate")}; " +
        $"argument files: {ArgumentFileLocations.Count}";
}

internal sealed class MinecraftCommandLineParser(IMinecraftArgumentFileReader argumentFileReader)
{
    private const int MaximumCommandLineUtf16Length = 32_768;
    private const int MaximumTokens = 4_096;
    private const int MaximumTokenUtf16Length = 32_768;
    private readonly IMinecraftArgumentFileReader _argumentFileReader =
        argumentFileReader ?? throw new ArgumentNullException(nameof(argumentFileReader));

    private static readonly HashSet<string> KnownMainClasses = new(
        [
            "net.minecraft.client.main.Main",
            "net.minecraft.launchwrapper.Launch",
            "cpw.mods.modlauncher.Launcher",
            "net.fabricmc.loader.impl.launch.knot.KnotClient",
            "org.quiltmc.loader.impl.launch.knot.KnotClient",
        ],
        StringComparer.Ordinal);

    internal MinecraftProcessEvidence Parse(
        ProcessInventoryEntry entry,
        IReadOnlyList<string> approvedArgumentFileRoots)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(approvedArgumentFileRoots);
        if (!WindowsProcessInventory.IsJavaImage(entry.ImageName))
        {
            return Evidence(MinecraftProcessClassification.Unrelated);
        }

        if (!entry.IsCommandLineReadable ||
            entry.CommandLine is not { Length: > 0 } commandLine ||
            commandLine.Length > MaximumCommandLineUtf16Length ||
            !TryTokenize(commandLine, out var directTokens) ||
            !TryExpandArgumentFiles(
                directTokens,
                approvedArgumentFileRoots,
                out var tokens,
                out var argumentFiles))
        {
            return Evidence(MinecraftProcessClassification.UnsafeCandidate);
        }

        var mainClasses = tokens.Where(KnownMainClasses.Contains).Distinct(StringComparer.Ordinal).ToArray();
        if (mainClasses.Length == 0)
        {
            return tokens.Any(LooksMinecraftLike)
                ? Evidence(MinecraftProcessClassification.UnsafeCandidate)
                : Evidence(MinecraftProcessClassification.Unrelated);
        }

        if (mainClasses.Length != 1 || !TryReadGameDirectory(tokens, out var gameDirectory))
        {
            return Evidence(
                MinecraftProcessClassification.UnsafeCandidate,
                mainClasses[0],
                argumentFiles: argumentFiles);
        }

        return Evidence(
            MinecraftProcessClassification.Minecraft,
            mainClasses[0],
            gameDirectory,
            argumentFiles);
    }

    private bool TryExpandArgumentFiles(
        IReadOnlyList<string> directTokens,
        IReadOnlyList<string> approvedRoots,
        out IReadOnlyList<string> expanded,
        out IReadOnlyList<string> argumentFiles)
    {
        var tokens = new List<string>(directTokens.Count);
        var locations = new List<string>();
        foreach (var token in directTokens)
        {
            if (!token.StartsWith('@'))
            {
                tokens.Add(token);
                continue;
            }

            var location = token[1..];
            if (location.Length == 0 ||
                locations.Count >= 16 ||
                !_argumentFileReader.TryRead(location, approvedRoots, out var content) ||
                content.Length > MaximumCommandLineUtf16Length ||
                !TryTokenize(content, out var fromFile) ||
                fromFile.Any(value => value.StartsWith('@')) ||
                fromFile.Count > MaximumTokens - tokens.Count)
            {
                expanded = Array.Empty<string>();
                argumentFiles = Array.Empty<string>();
                return false;
            }

            locations.Add(location);
            tokens.AddRange(fromFile);
        }

        expanded = Array.AsReadOnly(tokens.ToArray());
        argumentFiles = Array.AsReadOnly(locations.ToArray());
        return true;
    }

    private static bool TryReadGameDirectory(
        IReadOnlyList<string> tokens,
        out string? gameDirectory)
    {
        gameDirectory = null;
        for (var index = 0; index < tokens.Count; index++)
        {
            string? candidate = null;
            if (string.Equals(tokens[index], "--gameDir", StringComparison.Ordinal))
            {
                if (++index >= tokens.Count)
                {
                    return false;
                }

                candidate = tokens[index];
            }
            else if (tokens[index].StartsWith("--gameDir=", StringComparison.Ordinal))
            {
                candidate = tokens[index]["--gameDir=".Length..];
            }

            if (candidate is null)
            {
                continue;
            }

            if (candidate.Length == 0 || candidate.Length > 32_767)
            {
                return false;
            }

            string normalized;
            try
            {
                normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }

            if (gameDirectory is not null &&
                !string.Equals(gameDirectory, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            gameDirectory = normalized;
        }

        return gameDirectory is not null;
    }

    private static bool TryTokenize(string commandLine, out IReadOnlyList<string> tokens)
    {
        var parsed = new List<string>();
        var index = 0;
        while (index < commandLine.Length)
        {
            while (index < commandLine.Length && char.IsWhiteSpace(commandLine[index]))
            {
                index++;
            }

            if (index == commandLine.Length)
            {
                break;
            }

            if (parsed.Count == MaximumTokens)
            {
                tokens = Array.Empty<string>();
                return false;
            }

            var builder = new StringBuilder();
            var inQuotes = false;
            while (index < commandLine.Length)
            {
                var backslashes = 0;
                while (index < commandLine.Length && commandLine[index] == '\\')
                {
                    backslashes++;
                    index++;
                }

                if (index < commandLine.Length && commandLine[index] == '"')
                {
                    builder.Append('\\', backslashes / 2);
                    if ((backslashes & 1) == 1)
                    {
                        builder.Append('"');
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }

                    index++;
                    continue;
                }

                builder.Append('\\', backslashes);
                if (index == commandLine.Length ||
                    (!inQuotes && char.IsWhiteSpace(commandLine[index])))
                {
                    break;
                }

                builder.Append(commandLine[index++]);
                if (builder.Length > MaximumTokenUtf16Length)
                {
                    tokens = Array.Empty<string>();
                    return false;
                }
            }

            if (inQuotes)
            {
                tokens = Array.Empty<string>();
                return false;
            }

            parsed.Add(builder.ToString());
        }

        tokens = Array.AsReadOnly(parsed.ToArray());
        return parsed.Count > 0;
    }

    private static bool LooksMinecraftLike(string token) =>
        token.Contains("minecraft", StringComparison.OrdinalIgnoreCase) ||
        token.Contains("modlauncher", StringComparison.OrdinalIgnoreCase) ||
        token.Contains("launchwrapper", StringComparison.OrdinalIgnoreCase) ||
        token.Contains("knotclient", StringComparison.OrdinalIgnoreCase);

    private static MinecraftProcessEvidence Evidence(
        MinecraftProcessClassification classification,
        string? mainClass = null,
        string? gameDirectory = null,
        IReadOnlyList<string>? argumentFiles = null) =>
        new(
            classification,
            mainClass,
            gameDirectory,
            argumentFiles ?? Array.Empty<string>());
}

internal sealed class WindowsMinecraftArgumentFileReader : IMinecraftArgumentFileReader
{
    private const int MaximumBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16LittleEndian = new(false, true, true);
    private static ReadOnlySpan<byte> Utf16LittleEndianBom => [0xFF, 0xFE];
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];
    private readonly IFileSystemCapability _fileSystem;

    internal WindowsMinecraftArgumentFileReader()
        : this(new WindowsFileSystemCapability())
    {
    }

    internal WindowsMinecraftArgumentFileReader(IFileSystemCapability fileSystem)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public bool TryRead(
        string path,
        IReadOnlyList<string> approvedRoots,
        out string content)
    {
        content = string.Empty;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var approvedRoot = approvedRoots
                .Select(Path.GetFullPath)
                .FirstOrDefault(root => IsDescendant(root, fullPath));
            if (approvedRoot is null ||
                !NormalizedRelativePath.TryCreate(
                    Path.GetRelativePath(approvedRoot, fullPath),
                    out var relativePath,
                    out _) ||
                relativePath is null ||
                relativePath.Value.Length == 0)
            {
                return false;
            }

            using var root = _fileSystem.OpenRoot(
                approvedRoot,
                FileSystemOpenPurpose.MigrationSource,
                CancellationToken.None);
            var snapshot = _fileSystem.ReadFile(
                root,
                relativePath,
                new FileReadLimits(MaximumBytes),
                CancellationToken.None);
            if (!snapshot.Exists || snapshot.Length is <= 0 or > MaximumBytes)
            {
                return false;
            }

            var bytes = snapshot.CopyBytes();
            try
            {
                content = bytes.AsSpan().StartsWith(Utf16LittleEndianBom)
                    ? StrictUtf16LittleEndian.GetString(bytes.AsSpan(2))
                    : StrictUtf8.GetString(bytes.AsSpan().StartsWith(Utf8Bom)
                        ? bytes.AsSpan(3)
                        : bytes);
                return true;
            }
            finally
            {
                global::System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            IOException or
            UnauthorizedAccessException or
            DecoderFallbackException or
            NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsDescendant(string root, string candidate)
    {
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return string.Equals(Path.TrimEndingDirectorySeparator(root), candidate, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

}
