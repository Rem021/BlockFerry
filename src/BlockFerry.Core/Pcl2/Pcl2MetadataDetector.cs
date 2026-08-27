using System.Text.Json;
using System.Text.RegularExpressions;
using BlockFerry.Core.Discovery;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Pcl2;

internal sealed record Pcl2MetadataResult(
    string DisplayName,
    string? MinecraftVersion,
    IReadOnlyList<Pcl2ModLoader> ModLoaders,
    Pcl2ModpackIdentity ModpackIdentity,
    bool HasUsableVersionMetadata);

internal static class Pcl2MetadataDetector
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static Pcl2MetadataResult Read(
        Pcl2ReadPathGuard access,
        NormalizedRelativePath instanceRoot,
        string instanceName,
        NormalizedRelativePath? instanceJsonPath,
        string instanceId,
        List<Pcl2Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var builder = new MetadataBuilder(instanceName);
        if (instanceJsonPath is not null)
        {
            ReadJsonChain(
                access,
                instanceJsonPath,
                instanceId,
                diagnostics,
                builder,
                cancellationToken);
        }

        DetectClientNote(access, instanceRoot, instanceId, diagnostics, builder, cancellationToken);
        DetectManifest(access, instanceRoot, instanceId, diagnostics, builder, cancellationToken);

        if (builder.ParsedAnyJson && !builder.SawMainClass)
        {
            diagnostics.Add(new Pcl2Diagnostic(
                Pcl2DiagnosticCode.InstanceJsonSchemaInvalid,
                Pcl2DiagnosticSeverity.Error,
                "The merged version JSON chain contains no mainClass and is not usable instance metadata.",
                instanceJsonPath is null
                    ? access.GetMinecraftAbsolutePath(instanceRoot)
                    : access.GetMinecraftAbsolutePath(instanceJsonPath),
                instanceId));
        }

        var hasUsableVersionMetadata = builder.ParsedAnyJson &&
            builder.SawMainClass &&
            builder.MinecraftVersion is not null &&
            !builder.HasFatalMetadataError;

        IReadOnlyList<Pcl2ModLoader> loaders;
        if (builder.Loaders.Count > 0)
        {
            loaders = builder.Loaders.Values
                .OrderBy(loader => loader.Kind)
                .ToArray();
        }
        else if (hasUsableVersionMetadata)
        {
            loaders =
            [
                new Pcl2ModLoader(
                    Pcl2ModLoaderKind.Vanilla,
                    null,
                    "No recognized mod-loader metadata is present in the version JSON chain."),
            ];
        }
        else
        {
            loaders =
            [
                new Pcl2ModLoader(
                    Pcl2ModLoaderKind.Unknown,
                    null,
                    "No complete, usable version JSON chain was available for loader detection."),
            ];
            diagnostics.Add(new Pcl2Diagnostic(
                Pcl2DiagnosticCode.ModLoaderUnknown,
                Pcl2DiagnosticSeverity.Warning,
                "The mod loader could not be identified because the version JSON chain is incomplete or unusable.",
                instanceJsonPath is null
                    ? access.GetMinecraftAbsolutePath(instanceRoot)
                    : access.GetMinecraftAbsolutePath(instanceJsonPath),
                instanceId));
        }

        if (builder.MinecraftVersion is null)
        {
            diagnostics.Add(new Pcl2Diagnostic(
                Pcl2DiagnosticCode.MinecraftVersionUnknown,
                Pcl2DiagnosticSeverity.Warning,
                "The Minecraft version could not be identified from the instance JSON chain.",
                instanceJsonPath is null
                    ? access.GetMinecraftAbsolutePath(instanceRoot)
                    : access.GetMinecraftAbsolutePath(instanceJsonPath),
                instanceId));
        }

        return new Pcl2MetadataResult(
            builder.DisplayName,
            builder.MinecraftVersion,
            loaders,
            builder.Identity,
            hasUsableVersionMetadata);
    }

    private static void ReadJsonChain(
        Pcl2ReadPathGuard access,
        NormalizedRelativePath instanceJsonPath,
        string instanceId,
        List<Pcl2Diagnostic> diagnostics,
        MetadataBuilder builder,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentRelative = instanceJsonPath;
        for (var depth = 0; depth < 16; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentPath = access.GetMinecraftAbsolutePath(currentRelative);
            if (!visited.Add(currentRelative.Value))
            {
                builder.HasFatalMetadataError = true;
                diagnostics.Add(new Pcl2Diagnostic(
                    Pcl2DiagnosticCode.InheritanceCycle,
                    Pcl2DiagnosticSeverity.Error,
                    "The version JSON inheritance chain contains a cycle.",
                    currentPath,
                    instanceId));
                return;
            }

            JsonDocument document;
            try
            {
                var snapshot = access.ReadMinecraftFile(
                    currentRelative,
                    4 * 1024 * 1024,
                    cancellationToken);
                if (!snapshot.Exists)
                {
                    builder.HasFatalMetadataError = true;
                    diagnostics.Add(new Pcl2Diagnostic(
                        Pcl2DiagnosticCode.InheritanceParentMissing,
                        Pcl2DiagnosticSeverity.Error,
                        "The version JSON snapshot is missing.",
                        currentPath,
                        instanceId));
                    return;
                }

                document = JsonDocument.Parse(snapshot.CopyBytes(), JsonOptions);
            }
            catch (CapabilityLimitExceededException)
            {
                throw;
            }
            catch (Exception exception) when (exception is CapabilityBoundaryException or JsonException)
            {
                builder.HasFatalMetadataError = true;
                diagnostics.Add(new Pcl2Diagnostic(
                    Pcl2DiagnosticCode.InstanceJsonInvalid,
                    Pcl2DiagnosticSeverity.Error,
                    $"The instance version JSON could not be read: {DiagnosticText.EscapeTechnicalValue(exception.Message)}",
                    currentPath,
                    instanceId));
                return;
            }

            using (document)
            {
                var isPrimary = depth == 0;
                var root = document.RootElement;
                builder.ParsedAnyJson = true;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    builder.HasFatalMetadataError = true;
                    diagnostics.Add(new Pcl2Diagnostic(
                        Pcl2DiagnosticCode.InstanceJsonSchemaInvalid,
                        Pcl2DiagnosticSeverity.Error,
                        $"The version JSON root must be an object, not {root.ValueKind}.",
                        currentPath,
                        instanceId));
                    return;
                }

                ReadDocumentMetadata(root, currentPath, isPrimary, builder);

                var inheritsFrom = ReadString(root, "inheritsFrom");
                builder.ConsiderMinecraftVersion(
                    inheritsFrom,
                    MinecraftVersionPriority.Inheritance);

                if (string.IsNullOrWhiteSpace(inheritsFrom))
                {
                    return;
                }

                if (!TryBuildParentJsonPath(inheritsFrom, out var parentJsonPath))
                {
                    builder.HasFatalMetadataError = true;
                    diagnostics.Add(new Pcl2Diagnostic(
                        Pcl2DiagnosticCode.InheritancePathInvalid,
                        Pcl2DiagnosticSeverity.Error,
                        $"The inheritsFrom value '{DiagnosticText.EscapeTechnicalValue(inheritsFrom)}' is not a safe version-directory name.",
                        currentPath,
                        instanceId));
                    return;
                }

                currentRelative = parentJsonPath;
            }
        }

        builder.HasFatalMetadataError = true;
        diagnostics.Add(new Pcl2Diagnostic(
            Pcl2DiagnosticCode.InheritanceDepthExceeded,
            Pcl2DiagnosticSeverity.Error,
            "The version JSON inheritance chain exceeded the safe depth limit of 16.",
            access.GetMinecraftAbsolutePath(currentRelative),
            instanceId));
    }

    private static void ReadDocumentMetadata(
        JsonElement root,
        string jsonPath,
        bool isPrimary,
        MetadataBuilder builder)
    {
        if (isPrimary)
        {
            var displayName = ReadString(root, "name");
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                builder.DisplayName = displayName;
                builder.ConsiderIdentity(new Pcl2ModpackIdentity(
                    displayName,
                    ReadString(root, "version"),
                    Pcl2IdentityConfidence.Medium,
                    Pcl2IdentitySource.InstanceJson,
                    $"Explicit name in {Path.GetFileName(jsonPath)}"));
            }

            var explicitIdentity = ReadExplicitIdentity(root, jsonPath);
            if (explicitIdentity is not null)
            {
                builder.ConsiderIdentity(explicitIdentity);
            }
        }

        builder.ConsiderMinecraftVersion(
            ReadString(root, "clientVersion"),
            MinecraftVersionPriority.ClientVersion);
        builder.ConsiderMinecraftVersion(
            ReadString(root, "minecraftVersion"),
            MinecraftVersionPriority.ExplicitMinecraftVersion);
        ReadPatches(root, jsonPath, builder);
        ReadFmlMinecraftVersionArgument(root, builder);
        ReadLibraries(root, jsonPath, builder);
        var hasInheritance = !string.IsNullOrWhiteSpace(ReadString(root, "inheritsFrom"));
        builder.ConsiderMinecraftVersion(
            ReadString(root, "jar"),
            hasInheritance
                ? MinecraftVersionPriority.JarWithInheritance
                : MinecraftVersionPriority.JarWithoutInheritance);
        builder.ConsiderMinecraftVersion(
            ReadString(root, "id"),
            MinecraftVersionPriority.Id);
        ReadMainClass(root, jsonPath, builder);
    }

    private static void ReadPatches(JsonElement root, string jsonPath, MetadataBuilder builder)
    {
        if (!root.TryGetProperty("patches", out var patches) || patches.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var patch in patches.EnumerateArray())
        {
            if (patch.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadString(patch, "id") ?? string.Empty;
            var version = ReadString(patch, "version");
            if (id.Equals("game", StringComparison.OrdinalIgnoreCase) ||
                id.Equals("minecraft", StringComparison.OrdinalIgnoreCase))
            {
                builder.ConsiderMinecraftVersion(
                    version,
                    MinecraftVersionPriority.GamePatch);
            }

            var kind = LoaderKindFromText(id);
            if (kind is not Pcl2ModLoaderKind.Unknown and not Pcl2ModLoaderKind.Vanilla)
            {
                builder.AddLoader(kind, version, $"Patch '{id}' in {Path.GetFileName(jsonPath)}");
            }

            // HMCL/PCL patch metadata stores the effective mainClass, libraries,
            // and arguments inside prioritized patch objects rather than only at
            // the top level. Reading all fragments is sufficient for identity
            // detection and avoids treating a patches-only instance as unusable.
            ReadMainClass(patch, jsonPath, builder);
            ReadLibraries(patch, jsonPath, builder);
            ReadFmlMinecraftVersionArgument(patch, builder);
        }
    }

    private static void ReadLibraries(JsonElement root, string jsonPath, MetadataBuilder builder)
    {
        if (!root.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var library in libraries.EnumerateArray())
        {
            var coordinate = library.ValueKind switch
            {
                JsonValueKind.String => library.GetString(),
                JsonValueKind.Object => ReadString(library, "name"),
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(coordinate))
            {
                continue;
            }

            var segments = coordinate.Split(':');
            var group = segments.Length > 0 ? segments[0] : string.Empty;
            var artifact = segments.Length > 1 ? segments[1] : string.Empty;
            var version = segments.Length > 2 ? segments[2] : null;
            var kind = LoaderKindFromCoordinate(group, artifact);
            if (kind is not Pcl2ModLoaderKind.Unknown)
            {
                builder.AddLoader(
                    kind,
                    version,
                    $"Library '{coordinate}' in {Path.GetFileName(jsonPath)}");
            }

            if (coordinate.StartsWith("net.minecraft:client:", StringComparison.OrdinalIgnoreCase) ||
                coordinate.StartsWith("net.fabricmc:intermediary:", StringComparison.OrdinalIgnoreCase) ||
                coordinate.StartsWith("org.quiltmc:hashed:", StringComparison.OrdinalIgnoreCase))
            {
                builder.ConsiderMinecraftVersion(
                    version,
                    MinecraftVersionPriority.GameLibrary);
            }

            if (kind is Pcl2ModLoaderKind.Forge or Pcl2ModLoaderKind.NeoForge &&
                TryReadForgeMinecraftVersion(version, out var forgeMinecraftVersion))
            {
                builder.ConsiderMinecraftVersion(
                    forgeMinecraftVersion,
                    MinecraftVersionPriority.LoaderCoordinate);
            }
        }
    }

    private static void ReadFmlMinecraftVersionArgument(
        JsonElement root,
        MetadataBuilder builder)
    {
        var gameArguments = new List<string>();
        if (root.TryGetProperty("arguments", out var arguments) &&
            arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("game", out var game) &&
            game.ValueKind == JsonValueKind.Array)
        {
            foreach (var argument in game.EnumerateArray())
            {
                if (argument.ValueKind == JsonValueKind.String)
                {
                    gameArguments.Add(argument.GetString() ?? string.Empty);
                    continue;
                }

                if (argument.ValueKind != JsonValueKind.Object ||
                    !argument.TryGetProperty("value", out var conditionalValue))
                {
                    continue;
                }

                if (conditionalValue.ValueKind == JsonValueKind.String)
                {
                    gameArguments.Add(conditionalValue.GetString() ?? string.Empty);
                }
                else if (conditionalValue.ValueKind == JsonValueKind.Array)
                {
                    gameArguments.AddRange(conditionalValue.EnumerateArray()
                        .Where(value => value.ValueKind == JsonValueKind.String)
                        .Select(value => value.GetString() ?? string.Empty));
                }
            }
        }

        var legacyArguments = ReadString(root, "minecraftArguments");
        if (!string.IsNullOrWhiteSpace(legacyArguments))
        {
            gameArguments.AddRange(legacyArguments.Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries));
        }

        const string fmlVersionSwitch = "--fml.mcVersion";
        for (var index = 0; index < gameArguments.Count; index++)
        {
            var argument = gameArguments[index];
            if (argument.Equals(fmlVersionSwitch, StringComparison.OrdinalIgnoreCase) &&
                index + 1 < gameArguments.Count)
            {
                builder.ConsiderMinecraftVersion(
                    gameArguments[index + 1],
                    MinecraftVersionPriority.FmlArgument);
                continue;
            }

            var prefix = fmlVersionSwitch + "=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                builder.ConsiderMinecraftVersion(
                    argument[prefix.Length..],
                    MinecraftVersionPriority.FmlArgument);
            }
        }
    }

    private static void ReadMainClass(JsonElement root, string jsonPath, MetadataBuilder builder)
    {
        var mainClass = ReadString(root, "mainClass");
        if (string.IsNullOrWhiteSpace(mainClass))
        {
            return;
        }

        builder.SawMainClass = true;

        var kind = LoaderKindFromText(mainClass);
        if (kind is not Pcl2ModLoaderKind.Unknown and not Pcl2ModLoaderKind.Vanilla)
        {
            builder.AddLoader(kind, null, $"Main class '{mainClass}' in {Path.GetFileName(jsonPath)}");
        }
    }

    private static Pcl2ModpackIdentity? ReadExplicitIdentity(JsonElement root, string jsonPath)
    {
        foreach (var propertyName in new[] { "modpack", "pack", "manifest" })
        {
            if (!root.TryGetProperty(propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return new Pcl2ModpackIdentity(
                    value.GetString()!,
                    null,
                    Pcl2IdentityConfidence.High,
                    Pcl2IdentitySource.InstanceJson,
                    $"Explicit '{propertyName}' field in {Path.GetFileName(jsonPath)}");
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                var name = ReadString(value, "name") ?? ReadString(value, "title");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return new Pcl2ModpackIdentity(
                        name,
                        ReadString(value, "version") ?? ReadString(value, "versionName"),
                        Pcl2IdentityConfidence.High,
                        Pcl2IdentitySource.InstanceJson,
                        $"Explicit '{propertyName}' object in {Path.GetFileName(jsonPath)}");
                }
            }
        }

        var modpackName = ReadString(root, "modpackName");
        return string.IsNullOrWhiteSpace(modpackName)
            ? null
            : new Pcl2ModpackIdentity(
                modpackName,
                ReadString(root, "modpackVersion"),
                Pcl2IdentityConfidence.High,
                Pcl2IdentitySource.InstanceJson,
                $"Explicit modpack fields in {Path.GetFileName(jsonPath)}");
    }

    private static void DetectManifest(
        Pcl2ReadPathGuard access,
        NormalizedRelativePath instanceRoot,
        string instanceId,
        List<Pcl2Diagnostic> diagnostics,
        MetadataBuilder builder,
        CancellationToken cancellationToken)
    {
        var manifestRelative = Pcl2ReadPathGuard.Combine(instanceRoot, "manifest.json");
        var manifestPath = access.GetMinecraftAbsolutePath(manifestRelative);

        try
        {
            var snapshot = access.ReadMinecraftFile(
                manifestRelative,
                4 * 1024 * 1024,
                cancellationToken);
            if (!snapshot.Exists)
            {
                return;
            }

            using var manifest = JsonDocument.Parse(snapshot.CopyBytes(), JsonOptions);
            if (manifest.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Pcl2Diagnostic(
                    Pcl2DiagnosticCode.ManifestInvalid,
                    Pcl2DiagnosticSeverity.Warning,
                    $"The optional manifest root must be an object, not {manifest.RootElement.ValueKind}; it was ignored.",
                    manifestPath,
                    instanceId));
                return;
            }

            var name = ReadString(manifest.RootElement, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            builder.ConsiderIdentity(new Pcl2ModpackIdentity(
                name,
                ReadString(manifest.RootElement, "version") ??
                    ReadString(manifest.RootElement, "versionName"),
                Pcl2IdentityConfidence.High,
                Pcl2IdentitySource.Manifest,
                $"Manifest {Path.GetFileName(manifestPath)}"),
                replaceEqualConfidence: true);
        }
        catch (CapabilityLimitExceededException)
        {
            throw;
        }
        catch (Exception exception) when (exception is CapabilityBoundaryException or JsonException)
        {
            diagnostics.Add(new Pcl2Diagnostic(
                Pcl2DiagnosticCode.ManifestInvalid,
                Pcl2DiagnosticSeverity.Warning,
                $"The optional manifest could not be read and was ignored: {DiagnosticText.EscapeTechnicalValue(exception.Message)}",
                manifestPath,
                instanceId));
        }
    }

    private static void DetectClientNote(
        Pcl2ReadPathGuard access,
        NormalizedRelativePath instanceRoot,
        string instanceId,
        List<Pcl2Diagnostic> diagnostics,
        MetadataBuilder builder,
        CancellationToken cancellationToken)
    {
        FileSystemEntrySnapshot[] candidates;
        try
        {
            candidates = access.EnumerateMinecraft(instanceRoot, 256, cancellationToken)
                .Where(entry => !entry.IsDirectory)
                .Where(entry =>
                    entry.RelativePath.Segments[^1].Contains("client-note", StringComparison.OrdinalIgnoreCase) ||
                    entry.RelativePath.Segments[^1].Contains("客户端说明", StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.RelativePath.Value, StringComparer.Ordinal)
                .Take(32)
                .ToArray();
        }
        catch (CapabilityLimitExceededException)
        {
            throw;
        }
        catch (CapabilityBoundaryException)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            string[] lines;
            try
            {
                var snapshot = access.ReadMinecraftFile(
                    candidate.RelativePath,
                    1024 * 1024,
                    cancellationToken);
                lines = global::System.Text.Encoding.UTF8.GetString(snapshot.CopyBytes())
                    .Split(["\r\n", "\n"], StringSplitOptions.None);
            }
            catch (CapabilityLimitExceededException)
            {
                throw;
            }
            catch (CapabilityBoundaryException)
            {
                continue;
            }

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith('-'))
                {
                    continue;
                }

                var separator = trimmed.IndexOf(':', StringComparison.Ordinal);
                if (separator <= 1 || separator == trimmed.Length - 1)
                {
                    continue;
                }

                var name = trimmed[1..separator].Trim();
                var version = trimmed[(separator + 1)..].Trim();
                if (name.Equals("Minecraft", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Java", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("作者", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("构建日期", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("补丁", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("组件", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                builder.ConsiderIdentity(new Pcl2ModpackIdentity(
                    name,
                    version,
                    Pcl2IdentityConfidence.Medium,
                    Pcl2IdentitySource.ClientNote,
                    $"Pack declaration in {candidate.RelativePath.Segments[^1]}"));
                return;
            }
        }
    }

    private static Pcl2ModLoaderKind LoaderKindFromCoordinate(string group, string artifact)
    {
        if (group.Equals("net.neoforged", StringComparison.OrdinalIgnoreCase) &&
            (artifact.Equals("neoforge", StringComparison.OrdinalIgnoreCase) ||
             artifact.Equals("forge", StringComparison.OrdinalIgnoreCase) ||
             artifact.Equals("fmlloader", StringComparison.OrdinalIgnoreCase)))
        {
            return Pcl2ModLoaderKind.NeoForge;
        }

        if (group.Equals("net.minecraftforge", StringComparison.OrdinalIgnoreCase) &&
            (artifact.Equals("forge", StringComparison.OrdinalIgnoreCase) ||
             artifact.Equals("fmlloader", StringComparison.OrdinalIgnoreCase)))
        {
            return Pcl2ModLoaderKind.Forge;
        }

        if (group.Equals("net.fabricmc", StringComparison.OrdinalIgnoreCase) &&
            artifact.Equals("fabric-loader", StringComparison.OrdinalIgnoreCase))
        {
            return Pcl2ModLoaderKind.Fabric;
        }

        if (group.Equals("org.quiltmc", StringComparison.OrdinalIgnoreCase) &&
            artifact.Equals("quilt-loader", StringComparison.OrdinalIgnoreCase))
        {
            return Pcl2ModLoaderKind.Quilt;
        }

        if (group.Equals("com.mumfrey", StringComparison.OrdinalIgnoreCase) &&
            artifact.Contains("liteloader", StringComparison.OrdinalIgnoreCase))
        {
            return Pcl2ModLoaderKind.LiteLoader;
        }

        return group.Contains("optifine", StringComparison.OrdinalIgnoreCase) ||
               artifact.Contains("optifine", StringComparison.OrdinalIgnoreCase)
            ? Pcl2ModLoaderKind.OptiFine
            : Pcl2ModLoaderKind.Unknown;
    }

    private static Pcl2ModLoaderKind LoaderKindFromText(string text)
    {
        if (text.Contains("neoforge", StringComparison.OrdinalIgnoreCase))
        {
            return Pcl2ModLoaderKind.NeoForge;
        }

        if (text.Contains("fabric", StringComparison.OrdinalIgnoreCase))
        {
            return Pcl2ModLoaderKind.Fabric;
        }

        if (text.Contains("quilt", StringComparison.OrdinalIgnoreCase))
        {
            return Pcl2ModLoaderKind.Quilt;
        }

        if (text.Contains("liteloader", StringComparison.OrdinalIgnoreCase))
        {
            return Pcl2ModLoaderKind.LiteLoader;
        }

        if (text.Contains("optifine", StringComparison.OrdinalIgnoreCase))
        {
            return Pcl2ModLoaderKind.OptiFine;
        }

        return text.Contains("forge", StringComparison.OrdinalIgnoreCase)
            ? Pcl2ModLoaderKind.Forge
            : Pcl2ModLoaderKind.Unknown;
    }

    private static bool TryBuildParentJsonPath(
        string? inheritsFrom,
        out NormalizedRelativePath parentJsonPath)
    {
        parentJsonPath = null!;
        if (string.IsNullOrWhiteSpace(inheritsFrom) ||
            inheritsFrom.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        try
        {
            if (!string.Equals(Path.GetFileName(inheritsFrom), inheritsFrom, StringComparison.Ordinal))
            {
                return false;
            }

            if (NormalizedRelativePath.TryCreate(
                    Path.Combine("versions", inheritsFrom, inheritsFrom + ".json"),
                    out var parsed,
                    out _) &&
                parsed is not null)
            {
                parentJsonPath = parsed;
                return true;
            }

            return false;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryReadForgeMinecraftVersion(string? forgeVersion, out string minecraftVersion)
    {
        minecraftVersion = string.Empty;
        if (string.IsNullOrWhiteSpace(forgeVersion))
        {
            return false;
        }

        var separator = forgeVersion.IndexOf('-', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        var candidate = forgeVersion[..separator];
        if (!IsMinecraftVersion(candidate))
        {
            return false;
        }

        minecraftVersion = candidate;
        return true;
    }

    private static bool IsMinecraftVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return Regex.IsMatch(
            trimmed,
            @"^(?:\d+\.\d+(?:\.\d+)?(?:-(?:pre|rc)\d+|-snapshot-\d+)?|\d{2}w\d{2}[a-z∞]|[ab]\d+\.\d+(?:\.\d+)?(?:_\d+)?|rd-\d+|inf-\d+|\d+\.\d+(?:\.\d+)?_(?:experimental|deep_dark|combat)[a-z0-9_.-]*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private enum MinecraftVersionPriority
    {
        None = 0,
        Id = 10,
        JarWithoutInheritance = 30,
        ExplicitMinecraftVersion = 40,
        LoaderCoordinate = 50,
        GameLibrary = 60,
        Inheritance = 70,
        JarWithInheritance = 80,
        FmlArgument = 90,
        GamePatch = 100,
        ClientVersion = 110,
    }

    private sealed class MetadataBuilder(string instanceName)
    {
        private MinecraftVersionPriority _minecraftVersionPriority;

        public string DisplayName { get; set; } = instanceName;

        public string? MinecraftVersion { get; set; }

        public bool ParsedAnyJson { get; set; }

        public bool SawMainClass { get; set; }

        public bool HasFatalMetadataError { get; set; }

        public Dictionary<Pcl2ModLoaderKind, Pcl2ModLoader> Loaders { get; } = [];

        public Pcl2ModpackIdentity Identity { get; private set; } = new(
            instanceName,
            null,
            Pcl2IdentityConfidence.Low,
            Pcl2IdentitySource.DirectoryName,
            "Fallback identity from the PCL version directory name.");

        public void AddLoader(Pcl2ModLoaderKind kind, string? version, string evidence)
        {
            if (!Loaders.TryGetValue(kind, out var existing) ||
                (existing.Version is null && version is not null))
            {
                Loaders[kind] = new Pcl2ModLoader(kind, version, evidence);
            }
        }

        public void ConsiderMinecraftVersion(
            string? candidate,
            MinecraftVersionPriority priority)
        {
            if (priority <= _minecraftVersionPriority || !IsMinecraftVersion(candidate))
            {
                return;
            }

            MinecraftVersion = candidate!.Trim();
            _minecraftVersionPriority = priority;
        }

        public void ConsiderIdentity(
            Pcl2ModpackIdentity candidate,
            bool replaceEqualConfidence = false)
        {
            if (candidate.Confidence > Identity.Confidence ||
                (replaceEqualConfidence && candidate.Confidence == Identity.Confidence))
            {
                Identity = candidate;
            }
        }
    }
}
