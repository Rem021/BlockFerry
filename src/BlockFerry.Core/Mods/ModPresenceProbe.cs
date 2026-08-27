using System.Text;
using System.Text.Json;
using BlockFerry.Core.Content;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Mods;

internal sealed class ModPresenceProbe
{
    private const int MaximumRequiredModIds = 64;
    private const int MaximumJsonDepth = 64;
    private const long MaximumJsonTokens = 1_000_000;
    private const int MaximumJsonStringBytes = 32 * 1024;
    private const int MaximumJsonContainerItems = 250_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IReadOnlySet<string> declarationEntryNames =
        new ReadOnlySet<string>(
            [
                "fabric.mod.json",
                "quilt.mod.json",
                "META-INF/mods.toml",
                "META-INF/neoforge.mods.toml",
            ],
            StringComparer.Ordinal);
    private readonly IReadOnlySet<string> manifestEntryName =
        new ReadOnlySet<string>(["META-INF/MANIFEST.MF"], StringComparer.Ordinal);

    internal ModPresenceResult Probe(
        IReadOnlyInstanceAccess instance,
        IReadOnlySet<string> requiredModIds,
        ModProbeLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(requiredModIds);
        ArgumentNullException.ThrowIfNull(limits);
        var diagnostics = new List<ContentDiagnostic>();
        if (!LimitsAreValid(limits))
        {
            diagnostics.Add(Diagnostic(ContentDiagnosticCode.LimitExceeded));
            return ModPresenceResult.Create([], diagnostics);
        }

        List<string> required;
        try
        {
            required = CopyRequiredIds(requiredModIds);
        }
        catch (ArgumentException)
        {
            diagnostics.Add(Diagnostic(ContentDiagnosticCode.CapabilityRejected));
            return ModPresenceResult.Create([], diagnostics);
        }

        AssertContentPath("mods", out var modsPath);
        IReadOnlyList<ContentDirectoryEntry> rawEntries;
        try
        {
            rawEntries = instance.Enumerate(
                modsPath,
                new ContentEnumerationLimits(limits.MaximumJarFiles),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsCapabilityFailure(exception))
        {
            diagnostics.Add(Diagnostic(
                exception is CapabilityLimitExceededException
                    ? ContentDiagnosticCode.LimitExceeded
                    : ContentDiagnosticCode.MissingSourceData));
            return ModPresenceResult.Create([], diagnostics);
        }

        var jarPaths = new List<ContentRelativePath>();
        foreach (var entry in rawEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDirectory ||
                !entry.RelativePath.Value.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                !IsDirectModsChild(entry.RelativePath))
            {
                continue;
            }

            jarPaths.Add(entry.RelativePath);
        }

        jarPaths.Sort((left, right) =>
            StringComparer.Ordinal.Compare(left.Value, right.Value));
        var requiredSet = new HashSet<string>(required, StringComparer.Ordinal);
        var evidence = new List<ModPresenceEvidence>();
        long returnedBytes = 0;
        foreach (var jarPath in jarPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadDeclarations(
                    instance,
                    jarPath,
                    requiredSet,
                    limits,
                    ref returnedBytes,
                    cancellationToken,
                    out var found,
                    out var rejection))
            {
                diagnostics.Add(Diagnostic(rejection));
                continue;
            }

            if (found is not null)
            {
                evidence.Add(found);
            }
        }

        if (diagnostics.Any(item => item.Code == ContentDiagnosticCode.LimitExceeded))
        {
            return ModPresenceResult.Create([], diagnostics);
        }

        var duplicateIds = evidence
            .GroupBy(item => item.ModId, StringComparer.Ordinal)
            .Where(group => group.Skip(1).Any())
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (duplicateIds.Count > 0)
        {
            evidence.RemoveAll(item => duplicateIds.Contains(item.ModId));
            diagnostics.AddRange(duplicateIds.Order(StringComparer.Ordinal).Select(modId =>
                Diagnostic(ContentDiagnosticCode.UnsupportedModVersion, modId)));
        }

        foreach (var emi in evidence.Where(item => item.ModId == "emi").ToArray())
        {
            diagnostics.Add(Diagnostic(ContentDiagnosticCode.UnsupportedEmiState, emi.ModId));
        }

        return ModPresenceResult.Create(evidence, diagnostics);
    }

    private bool TryReadDeclarations(
        IReadOnlyInstanceAccess instance,
        ContentRelativePath jarPath,
        HashSet<string> requiredModIds,
        ModProbeLimits limits,
        ref long returnedBytes,
        CancellationToken cancellationToken,
        out ModPresenceEvidence? evidence,
        out ContentDiagnosticCode rejection)
    {
        evidence = null;
        rejection = ContentDiagnosticCode.UnsupportedSchema;
        var remaining = limits.MaximumTotalBytes - returnedBytes;
        if (remaining <= 0)
        {
            rejection = ContentDiagnosticCode.LimitExceeded;
            return false;
        }

        IReadOnlyDictionary<string, ContentFileSnapshot> declarations;
        try
        {
            declarations = instance.ReadZipEntries(
                jarPath,
                declarationEntryNames,
                ZipLimits(limits, remaining),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsCapabilityFailure(exception))
        {
            rejection = exception is CapabilityLimitExceededException
                ? ContentDiagnosticCode.LimitExceeded
                : ContentDiagnosticCode.UnsupportedSchema;
            return false;
        }

        returnedBytes = checked(returnedBytes + declarations.Values.Sum(value => value.Length));
        if (returnedBytes > limits.MaximumTotalBytes)
        {
            rejection = ContentDiagnosticCode.LimitExceeded;
            return false;
        }

        if (declarations.Count != 1)
        {
            rejection = ContentDiagnosticCode.UnsupportedSchema;
            return false;
        }

        var declaration = declarations.Single();
        string? modId;
        string? version;
        ModDeclarationKind kind;
        var requiresManifest = false;
        if (string.Equals(declaration.Key, "fabric.mod.json", StringComparison.Ordinal))
        {
            kind = ModDeclarationKind.FabricJson;
            if (!TryParseJsonDeclaration(
                    declaration.Value.Bytes,
                    quilt: false,
                    out modId,
                    out version))
            {
                return false;
            }
        }
        else if (string.Equals(declaration.Key, "quilt.mod.json", StringComparison.Ordinal))
        {
            kind = ModDeclarationKind.QuiltJson;
            if (!TryParseJsonDeclaration(
                    declaration.Value.Bytes,
                    quilt: true,
                    out modId,
                    out version))
            {
                return false;
            }
        }
        else
        {
            kind = string.Equals(
                declaration.Key,
                "META-INF/neoforge.mods.toml",
                StringComparison.Ordinal)
                ? ModDeclarationKind.NeoForgeToml
                : ModDeclarationKind.ForgeToml;
            if (!StrictModTomlParser.TryParse(
                    declaration.Value.Bytes,
                    out var toml))
            {
                return false;
            }

            modId = toml!.ModId;
            version = toml.Version;
            requiresManifest = toml.RequiresManifestVersion;
        }

        if (requiresManifest)
        {
            remaining = limits.MaximumTotalBytes - returnedBytes;
            if (remaining <= 0)
            {
                rejection = ContentDiagnosticCode.LimitExceeded;
                return false;
            }

            IReadOnlyDictionary<string, ContentFileSnapshot> manifest;
            try
            {
                manifest = instance.ReadZipEntries(
                    jarPath,
                    manifestEntryName,
                    ZipLimits(limits, remaining),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsCapabilityFailure(exception))
            {
                rejection = exception is CapabilityLimitExceededException
                    ? ContentDiagnosticCode.LimitExceeded
                    : ContentDiagnosticCode.UnsupportedSchema;
                return false;
            }

            returnedBytes = checked(returnedBytes + manifest.Values.Sum(value => value.Length));
            if (returnedBytes > limits.MaximumTotalBytes ||
                manifest.Count != 1 ||
                !manifest.TryGetValue("META-INF/MANIFEST.MF", out var manifestBytes) ||
                !ManifestVersionReader.TryReadImplementationVersion(
                    manifestBytes.Bytes,
                    out version))
            {
                rejection = returnedBytes > limits.MaximumTotalBytes
                    ? ContentDiagnosticCode.LimitExceeded
                    : ContentDiagnosticCode.UnsupportedSchema;
                return false;
            }
        }

        if (!ContentValueValidation.IsTechnicalId(modId) ||
            !ContentValueValidation.IsOptionalTechnicalValue(version))
        {
            return false;
        }

        if (modId is not null && requiredModIds.Contains(modId))
        {
            evidence = new ModPresenceEvidence(modId, version, jarPath, kind);
        }

        rejection = default;
        return true;
    }

    private static bool TryParseJsonDeclaration(
        ImmutableByteBuffer bytes,
        bool quilt,
        out string? modId,
        out string? version)
    {
        modId = null;
        version = null;
        var utf8 = bytes.CopyBytes();
        if (!TryPreflightJson(utf8))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(
                utf8,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumJsonDepth,
                });
            var declaration = document.RootElement;
            if (declaration.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (quilt)
            {
                if (!declaration.TryGetProperty("quilt_loader", out declaration) ||
                    declaration.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }
            }

            if (!declaration.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.String ||
                !declaration.TryGetProperty("version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            modId = idElement.GetString();
            version = versionElement.GetString();
            return ContentValueValidation.IsTechnicalId(modId) &&
                   ContentValueValidation.IsOptionalTechnicalValue(version);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryPreflightJson(byte[] utf8)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(utf8);
            var reader = new Utf8JsonReader(
                utf8,
                new JsonReaderOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumJsonDepth,
                });
            var containers = new Stack<JsonContainer>();
            long tokens = 0;
            while (reader.Read())
            {
                if (++tokens > MaximumJsonTokens || reader.CurrentDepth > MaximumJsonDepth)
                {
                    return false;
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        if (!CountArrayValue(containers))
                        {
                            return false;
                        }

                        containers.Push(JsonContainer.Object());
                        break;
                    case JsonTokenType.StartArray:
                        if (!CountArrayValue(containers))
                        {
                            return false;
                        }

                        containers.Push(JsonContainer.Array());
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (containers.Count == 0)
                        {
                            return false;
                        }

                        containers.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        if (containers.Count == 0 || !containers.Peek().IsObject)
                        {
                            return false;
                        }

                        var property = reader.GetString();
                        if (property is null ||
                            StrictUtf8.GetByteCount(property) > MaximumJsonStringBytes ||
                            !containers.Peek().AddProperty(property))
                        {
                            return false;
                        }

                        break;
                    case JsonTokenType.String:
                        if (!CountArrayValue(containers) ||
                            StrictUtf8.GetByteCount(reader.GetString()!) > MaximumJsonStringBytes)
                        {
                            return false;
                        }

                        break;
                    case JsonTokenType.Number:
                    case JsonTokenType.True:
                    case JsonTokenType.False:
                    case JsonTokenType.Null:
                        if (!CountArrayValue(containers))
                        {
                            return false;
                        }

                        break;
                }
            }

            return tokens > 0 && containers.Count == 0;
        }
        catch (Exception exception) when (
            exception is JsonException or DecoderFallbackException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool CountArrayValue(Stack<JsonContainer> containers) =>
        containers.Count == 0 ||
        containers.Peek().IsObject ||
        containers.Peek().IncrementArrayCount();

    private static ContentZipReadLimits ZipLimits(ModProbeLimits limits, long remainingBytes)
    {
        var totalBytes = Math.Min(remainingBytes, limits.MaximumEntryBytes * 4L);
        var entryBytes = checked((int)Math.Min(limits.MaximumEntryBytes, totalBytes));
        return new ContentZipReadLimits(
            limits.MaximumZipEntries,
            entryBytes,
            totalBytes,
            limits.MaximumArchiveBytes,
            limits.MaximumCentralDirectoryBytes);
    }

    private static List<string> CopyRequiredIds(IReadOnlySet<string> requiredModIds)
    {
        var copy = new List<string>(Math.Min(requiredModIds.Count, MaximumRequiredModIds));
        using var enumerator = requiredModIds.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (copy.Count == MaximumRequiredModIds)
            {
                throw new ArgumentException("Too many required mod IDs.", nameof(requiredModIds));
            }

            var value = enumerator.Current;
            if (!ContentValueValidation.IsTechnicalId(value) || copy.Contains(value, StringComparer.Ordinal))
            {
                throw new ArgumentException("Required mod IDs must be valid and unique.", nameof(requiredModIds));
            }

            copy.Add(value);
        }

        return copy;
    }

    private static bool LimitsAreValid(ModProbeLimits limits) =>
        limits.MaximumJarFiles is > 0 and <= 2_048 &&
        limits.MaximumZipEntries is > 0 and <= 65_536 &&
        limits.MaximumEntryBytes is > 0 and <= 2 * 1024 * 1024 &&
        limits.MaximumTotalBytes is > 0 and <= 32L * 1024 * 1024 &&
        limits.MaximumArchiveBytes is > 0 and <= 256L * 1024 * 1024 &&
        limits.MaximumCentralDirectoryBytes is > 0 and <= 32L * 1024 * 1024 &&
        limits.MaximumEntryBytes <= limits.MaximumTotalBytes &&
        limits.MaximumCentralDirectoryBytes <= limits.MaximumArchiveBytes;

    private static bool IsDirectModsChild(ContentRelativePath path)
    {
        const string prefix = "mods\\";
        return path.Value.StartsWith(prefix, StringComparison.Ordinal) &&
               !path.Value.AsSpan(prefix.Length).Contains('\\');
    }

    private static bool IsCapabilityFailure(Exception exception) =>
        exception is CapabilityBoundaryException or
            ArgumentException or
            InvalidOperationException or
            OverflowException or
            ObjectDisposedException;

    private static ContentDiagnostic Diagnostic(
        ContentDiagnosticCode code,
        string? modId = null)
    {
        ContentItemId? itemId = null;
        if (modId is not null && ContentItemId.TryCreate("mods", modId, out var created))
        {
            itemId = created;
        }

        return ContentDiagnostic.Create(
            code,
            ContentDiagnosticSeverity.Error,
            "mods",
            itemId);
    }

    private static void AssertContentPath(string value, out ContentRelativePath path)
    {
        if (!ContentRelativePath.TryCreate(value, out var created, out _))
        {
            throw new InvalidOperationException("A fixed mod-probe path is invalid.");
        }

        path = created!;
    }

    private sealed class JsonContainer
    {
        private readonly HashSet<string>? properties;
        private int count;

        private JsonContainer(bool isObject)
        {
            IsObject = isObject;
            properties = isObject ? new HashSet<string>(StringComparer.Ordinal) : null;
        }

        internal bool IsObject { get; }

        internal static JsonContainer Object() => new(true);

        internal static JsonContainer Array() => new(false);

        internal bool AddProperty(string property) =>
            ++count <= MaximumJsonContainerItems && properties!.Add(property);

        internal bool IncrementArrayCount() => ++count <= MaximumJsonContainerItems;
    }
}
