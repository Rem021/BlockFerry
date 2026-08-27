using BlockFerry.Core.System;
using BlockFerry.Core.Mods;
using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlockFerry.Core.Content;

internal sealed class ExtremeSoundMufflerAdapter : IContentAdapter
{
    private const string AdapterId = "esm";
    private const string ModId = "extremesoundmuffler";
    private const int RootEnumerationLimit = 2_049;
    private static readonly ContentRelativePath RootPath = CreatePath(string.Empty);
    private static readonly ContentRelativePath DataPath = CreatePath(@"ESM\soundsMuffled.dat");
    private readonly ConditionalWeakTable<ContentCatalog, EsmPreparedSession> preparedCatalogs = new();

    public string Id => AdapterId;

    public ContentProbeResult Probe(
        ContentProbeContext context,
        CancellationToken cancellationToken)
    {
        if (!TryPrepare(context, cancellationToken, out var prepared, out var rejection))
        {
            return ContentProbeResult.Create(false, rejection, [Diagnostic(rejection)]);
        }

        return ContentProbeResult.Create(true, null, prepared!.Diagnostics);
    }

    public ContentCatalog BuildCatalog(
        ContentProbeContext context,
        CancellationToken cancellationToken)
    {
        if (!TryPrepare(context, cancellationToken, out var prepared, out var rejection))
        {
            return ContentCatalog.Create(AdapterId, [], [Diagnostic(rejection)]);
        }

        var catalog = ContentCatalog.Create(
            AdapterId,
            prepared!.Items.Select(MapCatalogItem),
            prepared.Diagnostics);
        preparedCatalogs.Add(catalog, prepared);
        return catalog;
    }

    public ContentAdapterPlan Plan(
        ContentProbeContext context,
        ContentCatalog catalog,
        ValidatedContentSelection selection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(selection);
        if (!string.Equals(catalog.AdapterId, AdapterId, StringComparison.Ordinal) ||
            !selection.IsBoundTo(catalog) ||
            !preparedCatalogs.TryGetValue(catalog, out var prepared))
        {
            return RejectedPlan(ContentDiagnosticCode.CapabilityRejected);
        }

        try
        {
            context.ThrowIfUnavailable();
            if (!prepared.IsBoundTo(context) ||
                CompatibilityRejection(context) is not null ||
                !TryPrepare(context, cancellationToken, out var current, out _) ||
                !prepared.SameEvidence(current!))
            {
                return RejectedPlan(ContentDiagnosticCode.StaleContext);
            }

            var items = catalog.Items
                .Select(item => MapPlanItem(item, selection))
                .ToList();
            var actionable = items.Where(IsActionable).ToList();
            if (actionable.Count == 0)
            {
                return ContentAdapterPlan.Create(AdapterId, items, [], prepared.Diagnostics);
            }

            if (!TryBuildOutput(
                    prepared.SourceDocument,
                    prepared.TargetDocument,
                    actionable,
                    out _,
                    out var buildRejection))
            {
                return RejectedPlan(buildRejection ?? ContentDiagnosticCode.UnsupportedSchema);
            }

            var change = PlannedFileChange.Create(
                AdapterId,
                DataPath,
                prepared.SourceSnapshot,
                prepared.TargetSnapshot,
                actionable);
            return ContentAdapterPlan.Create(AdapterId, items, [change], prepared.Diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            return RejectedPlan(ContentDiagnosticCode.StaleContext);
        }
        catch (CapabilityLimitExceededException)
        {
            return RejectedPlan(ContentDiagnosticCode.LimitExceeded);
        }
        catch (Exception exception) when (
            exception is CapabilityBoundaryException or ArgumentException or InvalidOperationException)
        {
            return RejectedPlan(ContentDiagnosticCode.CapabilityRejected);
        }
    }

    public ContentStageResult Stage(
        ContentAdapterPlan plan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(plan.AdapterId, AdapterId, StringComparison.Ordinal) ||
            plan.FileChanges.Count > 1)
        {
            throw new ArgumentException("The ESM plan is not adapter-bound.", nameof(plan));
        }

        if (plan.FileChanges.Count == 0)
        {
            return ContentStageResult.Create(AdapterId, []);
        }

        var change = plan.FileChanges[0];
        if (!change.RelativePath.Equals(DataPath) ||
            !EsmMuteDocument.TryParse(change.SourceSnapshot.Bytes, out var source, out _) ||
            change.TargetSnapshot.Exists &&
            !EsmMuteDocument.TryParse(change.TargetSnapshot.Bytes, out _, out _) ||
            !TryBuildOutput(
                source!,
                ParseTarget(change.TargetSnapshot),
                change.Items,
                out var output,
                out _))
        {
            throw new InvalidOperationException("The ESM stage no longer matches its immutable plan.");
        }

        return ContentStageResult.Create(
            AdapterId,
            [StagedFileMutation.Create(change, output!)]);
    }

    public ContentVerificationResult Verify(
        ContentStageResult staged,
        IReadOnlyList<ContentFileSnapshot> pathBoundRereads,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(pathBoundRereads);
        ContentDiagnostic? bindingRejection = null;
        IReadOnlyList<ContentFileSnapshot> bound = [];
        if (!string.Equals(staged.AdapterId, AdapterId, StringComparison.Ordinal) ||
            !ContentPlanCoordinator.TryBindVerificationRereads(
                staged,
                pathBoundRereads,
                out bound,
                out bindingRejection))
        {
            return ContentVerificationResult.Create(
                false,
                bindingRejection is null
                    ? [Diagnostic(ContentDiagnosticCode.CapabilityRejected)]
                    : [bindingRejection]);
        }

        for (var index = 0; index < staged.Mutations.Count; index++)
        {
            var expected = staged.Mutations[index].AfterBytes;
            var actual = bound[index];
            ContentDiagnosticCode? rejection = null;
            EsmMuteDocument? actualDocument = null;
            var expectedValid = EsmMuteDocument.TryParse(
                expected,
                out var expectedDocument,
                out rejection);
            var actualValid = expectedValid && EsmMuteDocument.TryParse(
                actual.Bytes,
                out actualDocument,
                out rejection);
            if (!actual.Exists ||
                actual.Length != expected.Length ||
                !string.Equals(actual.Sha256, expected.Sha256, StringComparison.Ordinal) ||
                !expectedValid ||
                !actualValid ||
                !expectedDocument!.SemanticAndSpellingEquals(actualDocument!))
            {
                return ContentVerificationResult.Create(
                    false,
                    [Diagnostic(rejection ?? ContentDiagnosticCode.CapabilityRejected)]);
            }
        }

        return ContentVerificationResult.Create(true, []);
    }

    public IReadOnlySet<ContentRelativePath> RegenerateAllowedPaths(
        ContentProbeContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        return TryPrepare(context, cancellationToken, out _, out _)
            ? new ReadOnlySet<ContentRelativePath>([DataPath])
            : new ReadOnlySet<ContentRelativePath>([]);
    }

    public IReadOnlySet<ContentRelativePath> RegenerateRecoveryAllowedPaths(
        RecoveryCatalogContext context,
        IReadOnlySet<ContentRelativePath> storedCandidatePaths,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(storedCandidatePaths);
        context.ThrowIfUnavailable();
        return RecoveryCompatibilityRejection(context) is null
            ? new ReadOnlySet<ContentRelativePath>([DataPath])
            : new ReadOnlySet<ContentRelativePath>([]);
    }

    private static bool TryPrepare(
        ContentProbeContext context,
        CancellationToken cancellationToken,
        out EsmPreparedSession? prepared,
        out ContentDiagnosticCode rejection)
    {
        prepared = null;
        rejection = ContentDiagnosticCode.CapabilityRejected;
        if (context is null)
        {
            return false;
        }

        if (CompatibilityRejection(context) is { } compatibilityRejection)
        {
            rejection = compatibilityRejection;
            return false;
        }

        try
        {
            context.ThrowIfUnavailable();
            if (!ContainsExactEsmDirectory(context.Source, cancellationToken))
            {
                rejection = ContentDiagnosticCode.MissingSourceData;
                return false;
            }

            var sourceSnapshot = context.Source.Read(
                DataPath,
                new ContentReadLimits(EsmMuteDocument.MaximumFileBytes),
                cancellationToken);
            if (!sourceSnapshot.Exists)
            {
                rejection = ContentDiagnosticCode.MissingSourceData;
                return false;
            }

            if (!EsmMuteDocument.TryParse(sourceSnapshot.Bytes, out var sourceDocument, out var sourceRejection))
            {
                rejection = sourceRejection ?? ContentDiagnosticCode.UnsupportedSchema;
                return false;
            }

            var targetDirectoryExists = ContainsExactEsmDirectory(context.Target, cancellationToken);
            var targetSnapshot = targetDirectoryExists
                ? context.Target.Read(
                    DataPath,
                    new ContentReadLimits(EsmMuteDocument.MaximumFileBytes),
                    cancellationToken)
                : MissingSnapshot();
            EsmMuteDocument targetDocument;
            if (!targetSnapshot.Exists)
            {
                targetDocument = EsmMuteDocument.Empty;
            }
            else if (!EsmMuteDocument.TryParse(
                         targetSnapshot.Bytes,
                         out var parsedTarget,
                         out var targetRejection))
            {
                rejection = targetRejection ?? ContentDiagnosticCode.UnsupportedSchema;
                return false;
            }
            else
            {
                targetDocument = parsedTarget!;
            }

            var diagnostics = targetSnapshot.Exists
                ? Array.Empty<ContentDiagnostic>()
                : [Information(ContentDiagnosticCode.MissingTargetData)];
            prepared = new EsmPreparedSession(
                context,
                targetDirectoryExists,
                sourceSnapshot,
                targetSnapshot,
                sourceDocument!,
                targetDocument,
                BuildPreparedItems(sourceDocument!, targetDocument),
                diagnostics);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CapabilityLimitExceededException)
        {
            rejection = ContentDiagnosticCode.LimitExceeded;
        }
        catch (ObjectDisposedException)
        {
            rejection = ContentDiagnosticCode.StaleContext;
        }
        catch (SemanticAliasException)
        {
            rejection = ContentDiagnosticCode.SemanticAliasCollision;
        }
        catch (Exception exception) when (
            exception is CapabilityBoundaryException or ArgumentException or InvalidOperationException)
        {
            rejection = ContentDiagnosticCode.CapabilityRejected;
        }

        return false;
    }

    private static List<EsmPreparedItem> BuildPreparedItems(
        EsmMuteDocument source,
        EsmMuteDocument target)
    {
        var canonicalKeys = source.Entries.Keys
            .Concat(target.Entries.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var items = new List<EsmPreparedItem>(canonicalKeys.Length);
        var ids = new HashSet<ContentItemId>();
        foreach (var canonical in canonicalKeys)
        {
            source.Entries.TryGetValue(canonical, out var sourceEntry);
            target.Entries.TryGetValue(canonical, out var targetEntry);
            var id = CreateItemId(canonical);
            if (!ids.Add(id))
            {
                throw new InvalidOperationException("An ESM item identifier collided.");
            }

            var disposition = sourceEntry is null
                ? PlannedContentDisposition.Same
                : targetEntry is null
                    ? PlannedContentDisposition.Add
                    : sourceEntry.Value == targetEntry.Value
                        ? PlannedContentDisposition.Same
                        : PlannedContentDisposition.Conflict;
            items.Add(new EsmPreparedItem(
                id,
                canonical,
                sourceEntry,
                targetEntry,
                disposition));
        }

        return items;
    }

    private static ContentCatalogItem MapCatalogItem(EsmPreparedItem item)
    {
        var description = item.Source is null
            ? $"仅目标静音 · {FormatValue(item.Target!.Value)} · 保留"
            : item.Target is null
                ? $"来源音量 {FormatValue(item.Source.Value)} · 默认跳过"
                : item.Disposition == PlannedContentDisposition.Same
                    ? $"两边音量一致 · {FormatValue(item.Source.Value)}"
                    : $"来源 {FormatValue(item.Source.Value)} · 目标 {FormatValue(item.Target.Value)} · 默认保留目标";
        return ContentCatalogItem.Create(
            item.Id,
            DisplayName(item.CanonicalKey),
            description,
            item.Disposition,
            isSelectable: item.Disposition is PlannedContentDisposition.Add or PlannedContentDisposition.Conflict,
            isSelectedByDefault: false,
            item.Disposition == PlannedContentDisposition.Conflict
                ? ConflictResolution.KeepTarget
                : ConflictResolution.Skip,
            null);
    }

    private static ContentPlanItem MapPlanItem(
        ContentCatalogItem item,
        ValidatedContentSelection selection)
    {
        if (item.Disposition == PlannedContentDisposition.Conflict)
        {
            if (!selection.ConflictResolutions.TryGetValue(item.Id, out var resolution))
            {
                throw new InvalidOperationException("An ESM conflict has no validated resolution.");
            }

            return ContentPlanItem.Create(
                item.Id,
                PlannedContentDisposition.Conflict,
                resolution,
                resolution == ConflictResolution.UseSource
                    ? "将采用来源静音值"
                    : resolution == ConflictResolution.Skip
                        ? "跳过冲突并保留目标"
                        : "将保留目标静音值");
        }

        if (item.Disposition == PlannedContentDisposition.Add)
        {
            var selected = selection.SelectedItems.Contains(item.Id);
            return ContentPlanItem.Create(
                item.Id,
                selected ? PlannedContentDisposition.Add : PlannedContentDisposition.Unselected,
                ConflictResolution.Skip,
                selected ? "将新增静音设置" : "默认跳过");
        }

        return ContentPlanItem.Create(
            item.Id,
            PlannedContentDisposition.Same,
            ConflictResolution.Skip,
            "保持目标静音设置");
    }

    private static bool TryBuildOutput(
        EsmMuteDocument source,
        EsmMuteDocument target,
        IReadOnlyList<ContentPlanItem> actionable,
        out byte[]? bytes,
        out ContentDiagnosticCode? rejection)
    {
        bytes = null;
        rejection = null;
        var selectedIds = actionable.ToDictionary(item => item.Id);
        var sourceById = source.Entries.Values.ToDictionary(
            entry => CreateItemId(entry.CanonicalKey));
        var output = target.Entries.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        foreach (var (id, planned) in selectedIds)
        {
            if (!sourceById.TryGetValue(id, out var sourceEntry) ||
                !IsActionable(planned))
            {
                rejection = ContentDiagnosticCode.CapabilityRejected;
                return false;
            }

            var targetExists = target.Entries.TryGetValue(
                sourceEntry.CanonicalKey,
                out var targetEntry);
            var relationIsValid = planned.Disposition == PlannedContentDisposition.Add
                ? !targetExists
                : planned.Disposition == PlannedContentDisposition.Conflict &&
                  planned.Resolution == ConflictResolution.UseSource &&
                  targetExists &&
                  targetEntry!.Value != sourceEntry.Value;
            if (!relationIsValid)
            {
                rejection = ContentDiagnosticCode.CapabilityRejected;
                return false;
            }

            output[sourceEntry.CanonicalKey] = sourceEntry;
        }

        if (!EsmMuteDocument.TryEncode(output.Values, out bytes))
        {
            rejection = ContentDiagnosticCode.LimitExceeded;
            return false;
        }

        return true;
    }

    private static EsmMuteDocument ParseTarget(ContentFileSnapshot snapshot)
    {
        if (!snapshot.Exists)
        {
            return EsmMuteDocument.Empty;
        }

        if (!EsmMuteDocument.TryParse(snapshot.Bytes, out var parsed, out _))
        {
            throw new InvalidOperationException("The retained target ESM snapshot is invalid.");
        }

        return parsed!;
    }

    private static bool ContainsExactEsmDirectory(
        IReadOnlyInstanceAccess access,
        CancellationToken cancellationToken)
    {
        var found = false;
        foreach (var entry in access.Enumerate(
                     RootPath,
                     new ContentEnumerationLimits(RootEnumerationLimit),
                     cancellationToken))
        {
            if (!string.Equals(entry.RelativePath.Value, "ESM", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!entry.IsDirectory ||
                !string.Equals(entry.RelativePath.Value, "ESM", StringComparison.Ordinal))
            {
                throw new SemanticAliasException();
            }

            found = true;
        }

        return found;
    }

    private static ContentDiagnosticCode? CompatibilityRejection(ContentProbeContext context)
    {
        var compatibility = context.Compatibility;
        if (!ModDataCompatibilityPolicy.IsSupportedMinecraftPair(
                compatibility.SourceMinecraftVersion,
                compatibility.TargetMinecraftVersion) ||
            !ModDataCompatibilityPolicy.IsSupportedMinecraftPair(
                context.Source.Identity.MinecraftVersion,
                context.Target.Identity.MinecraftVersion))
        {
            return ContentDiagnosticCode.UnsupportedMinecraftVersion;
        }

        if (compatibility.DetectedUnsupportedModIds.Contains(ModId) ||
            !compatibility.SourceModVersions.TryGetValue(ModId, out var sourceVersion) ||
            !compatibility.TargetModVersions.TryGetValue(ModId, out var targetVersion) ||
            !ModDataCompatibilityPolicy.AreModVersionsCompatible(
                ModId,
                sourceVersion,
                targetVersion))
        {
            return ContentDiagnosticCode.UnsupportedModVersion;
        }

        return null;
    }

    private static ContentDiagnosticCode? RecoveryCompatibilityRejection(
        RecoveryCatalogContext context)
    {
        if (!ModDataCompatibilityPolicy.IsSupportedMinecraftTarget(
                context.TargetMinecraftVersion))
        {
            return ContentDiagnosticCode.UnsupportedMinecraftVersion;
        }

        if (context.UnsupportedModIds.Contains(ModId) ||
            !context.TargetModVersions.TryGetValue(ModId, out var targetVersion) ||
            !ModDataCompatibilityPolicy.IsSupportedTargetModVersion(ModId, targetVersion))
        {
            return ContentDiagnosticCode.UnsupportedModVersion;
        }

        return null;
    }

    private static ContentFileSnapshot MissingSnapshot() =>
        ContentFileSnapshot.Create(
            DataPath,
            false,
            [],
            DateTimeOffset.UnixEpoch,
            0,
            null);

    private static bool IsActionable(ContentPlanItem item) =>
        item.Disposition is PlannedContentDisposition.Add or PlannedContentDisposition.Update ||
        item.Disposition == PlannedContentDisposition.Conflict &&
        item.Resolution == ConflictResolution.UseSource;

    private static ContentItemId CreateItemId(string canonical)
    {
        var payload = Encoding.UTF8.GetBytes("blockferry.content.esm.item.v1\0" + canonical);
        try
        {
            var technicalKey = Convert.ToHexString(SHA256.HashData(payload));
            if (!ContentItemId.TryCreate(AdapterId, technicalKey, out var id))
            {
                throw new InvalidOperationException("An ESM canonical key could not become an item ID.");
            }

            return id;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static string DisplayName(string canonical) =>
        canonical.Length <= 500 ? canonical : canonical[..499] + "…";

    private static string FormatValue(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static ContentAdapterPlan RejectedPlan(ContentDiagnosticCode code) =>
        ContentAdapterPlan.Create(AdapterId, [], [], [Diagnostic(code)]);

    private static ContentDiagnostic Diagnostic(ContentDiagnosticCode code) =>
        ContentDiagnostic.Create(
            code,
            ContentDiagnosticSeverity.Error,
            AdapterId);

    private static ContentDiagnostic Information(ContentDiagnosticCode code) =>
        ContentDiagnostic.Create(
            code,
            ContentDiagnosticSeverity.Information,
            AdapterId);

    private static ContentRelativePath CreatePath(string value)
    {
        if (!ContentRelativePath.TryCreate(value, out var path, out _))
        {
            throw new InvalidOperationException("The fixed ESM path is invalid.");
        }

        return path!;
    }

    private sealed class SemanticAliasException : Exception;
}

internal sealed class EsmPreparedSession
{
    private readonly IReadOnlyInstanceAccess sourceAccess;
    private readonly IReadOnlyInstanceAccess targetAccess;

    internal EsmPreparedSession(
        ContentProbeContext context,
        bool targetDirectoryExists,
        ContentFileSnapshot sourceSnapshot,
        ContentFileSnapshot targetSnapshot,
        EsmMuteDocument sourceDocument,
        EsmMuteDocument targetDocument,
        IReadOnlyList<EsmPreparedItem> items,
        IReadOnlyList<ContentDiagnostic> diagnostics)
    {
        Generation = context.Generation;
        sourceAccess = context.Source;
        targetAccess = context.Target;
        SourceIdentity = context.Source.Identity;
        TargetIdentity = context.Target.Identity;
        TargetDirectoryExists = targetDirectoryExists;
        SourceSnapshot = sourceSnapshot;
        TargetSnapshot = targetSnapshot;
        SourceDocument = sourceDocument;
        TargetDocument = targetDocument;
        Items = items;
        Diagnostics = diagnostics;
    }

    internal long Generation { get; }

    internal ContentInstanceIdentity SourceIdentity { get; }

    internal ContentInstanceIdentity TargetIdentity { get; }

    internal bool TargetDirectoryExists { get; }

    internal ContentFileSnapshot SourceSnapshot { get; }

    internal ContentFileSnapshot TargetSnapshot { get; }

    internal EsmMuteDocument SourceDocument { get; }

    internal EsmMuteDocument TargetDocument { get; }

    internal IReadOnlyList<EsmPreparedItem> Items { get; }

    internal IReadOnlyList<ContentDiagnostic> Diagnostics { get; }

    internal bool IsBoundTo(ContentProbeContext context) =>
        context.Generation == Generation &&
        ReferenceEquals(context.Source, sourceAccess) &&
        ReferenceEquals(context.Target, targetAccess) &&
        context.Source.Identity == SourceIdentity &&
        context.Target.Identity == TargetIdentity;

    internal bool SameEvidence(EsmPreparedSession other) =>
        other is not null &&
        Generation == other.Generation &&
        SourceIdentity == other.SourceIdentity &&
        TargetIdentity == other.TargetIdentity &&
        TargetDirectoryExists == other.TargetDirectoryExists &&
        SameSnapshot(SourceSnapshot, other.SourceSnapshot) &&
        SameSnapshot(TargetSnapshot, other.TargetSnapshot);

    private static bool SameSnapshot(ContentFileSnapshot expected, ContentFileSnapshot actual) =>
        expected.RelativePath.Equals(actual.RelativePath) &&
        expected.Exists == actual.Exists &&
        expected.Length == actual.Length &&
        string.Equals(expected.Sha256, actual.Sha256, StringComparison.Ordinal) &&
        expected.LastWriteTimeUtc == actual.LastWriteTimeUtc &&
        expected.WindowsFileAttributes == actual.WindowsFileAttributes &&
        expected.Identity == actual.Identity;
}

internal sealed record EsmPreparedItem(
    ContentItemId Id,
    string CanonicalKey,
    EsmMuteEntry? Source,
    EsmMuteEntry? Target,
    PlannedContentDisposition Disposition);

internal sealed record EsmMuteEntry(
    string RawKey,
    string CanonicalKey,
    double Value);

internal sealed class EsmMuteDocument
{
    internal const int MaximumFileBytes = 4 * 1024 * 1024;
    private const int MaximumProperties = 250_000;
    private const long MaximumTokens = 1_000_000;
    private const int MaximumStringUtf8Bytes = 32 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private EsmMuteDocument(IReadOnlyDictionary<string, EsmMuteEntry> entries)
    {
        Entries = entries;
    }

    internal static EsmMuteDocument Empty { get; } = new(
        new Dictionary<string, EsmMuteEntry>(StringComparer.Ordinal));

    internal IReadOnlyDictionary<string, EsmMuteEntry> Entries { get; }

    internal static bool TryParse(
        ImmutableByteBuffer bytes,
        out EsmMuteDocument? document,
        out ContentDiagnosticCode? rejection)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        document = null;
        rejection = null;
        if (bytes.Length > MaximumFileBytes)
        {
            rejection = ContentDiagnosticCode.LimitExceeded;
            return false;
        }

        var raw = bytes.CopyBytes();
        try
        {
            _ = StrictUtf8.GetCharCount(raw);
        }
        catch (DecoderFallbackException)
        {
            rejection = ContentDiagnosticCode.MalformedUtf8;
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(
                raw,
                new JsonReaderOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 128,
                });
            long tokenCount = 0;
            if (!ReadNext(ref reader, ref tokenCount) || reader.TokenType != JsonTokenType.StartObject)
            {
                throw new EsmSchemaException();
            }

            var rawKeys = new HashSet<string>(StringComparer.Ordinal);
            var entries = new Dictionary<string, EsmMuteEntry>(StringComparer.Ordinal);
            while (ReadNext(ref reader, ref tokenCount))
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    if (ReadNext(ref reader, ref tokenCount))
                    {
                        throw new JsonException("Trailing JSON is not allowed.");
                    }

                    document = new EsmMuteDocument(entries);
                    return true;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException("An ESM property was expected.");
                }

                if (rawKeys.Count == MaximumProperties)
                {
                    throw new EsmLimitException();
                }

                var rawKey = ReadBoundedString(ref reader);
                if (!rawKeys.Add(rawKey))
                {
                    throw new DuplicatePropertyException();
                }

                if (!ResourceLocationValidator.TryParse1211(rawKey, out var location))
                {
                    throw new EsmSchemaException();
                }

                if (!ReadNext(ref reader, ref tokenCount) || reader.TokenType != JsonTokenType.Number)
                {
                    throw new EsmSchemaException();
                }

                if (reader.ValueSpan.Length > MaximumStringUtf8Bytes ||
                    !Utf8Parser.TryParse(reader.ValueSpan, out double value, out var consumed) ||
                    consumed != reader.ValueSpan.Length ||
                    !double.IsFinite(value) ||
                    value < 0 ||
                    value > 0.9)
                {
                    throw new EsmSchemaException();
                }

                if (value == 0)
                {
                    value = 0;
                }

                var entry = new EsmMuteEntry(rawKey, location.CanonicalValue, value);
                if (!entries.TryAdd(location.CanonicalValue, entry))
                {
                    throw new SemanticAliasException();
                }
            }

            throw new JsonException("The ESM object is incomplete.");
        }
        catch (DuplicatePropertyException)
        {
            rejection = ContentDiagnosticCode.DuplicateJsonProperty;
        }
        catch (SemanticAliasException)
        {
            rejection = ContentDiagnosticCode.SemanticAliasCollision;
        }
        catch (EsmLimitException)
        {
            rejection = ContentDiagnosticCode.LimitExceeded;
        }
        catch (EsmSchemaException)
        {
            rejection = ContentDiagnosticCode.UnsupportedSchema;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or OverflowException)
        {
            rejection = ContentDiagnosticCode.MalformedJson;
        }

        return false;
    }

    internal static bool TryEncode(
        IEnumerable<EsmMuteEntry> entries,
        out byte[]? bytes)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var ordered = entries.OrderBy(entry => entry.CanonicalKey, StringComparer.Ordinal).ToArray();
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
                   output,
                   new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            writer.WriteStartObject();
            foreach (var entry in ordered)
            {
                writer.WritePropertyName(entry.RawKey);
                writer.WriteRawValue(
                    entry.Value.ToString("R", CultureInfo.InvariantCulture),
                    skipInputValidation: false);
            }

            writer.WriteEndObject();
        }

        if (output.WrittenCount > MaximumFileBytes)
        {
            bytes = null;
            return false;
        }

        bytes = output.WrittenSpan.ToArray();
        return true;
    }

    internal bool SemanticAndSpellingEquals(EsmMuteDocument other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Entries.Count != other.Entries.Count)
        {
            return false;
        }

        foreach (var (canonical, entry) in Entries)
        {
            if (!other.Entries.TryGetValue(canonical, out var candidate) ||
                !string.Equals(entry.RawKey, candidate.RawKey, StringComparison.Ordinal) ||
                entry.Value != candidate.Value)
            {
                return false;
            }
        }

        return true;
    }

    private static string ReadBoundedString(ref Utf8JsonReader reader)
    {
        var rawLength = reader.ValueSpan.Length;
        if ((!reader.ValueIsEscaped && rawLength > MaximumStringUtf8Bytes) ||
            rawLength > checked(MaximumStringUtf8Bytes * 6))
        {
            throw new EsmLimitException();
        }

        var value = reader.GetString() ?? throw new JsonException("A string was expected.");
        if (StrictUtf8.GetByteCount(value) > MaximumStringUtf8Bytes)
        {
            throw new EsmLimitException();
        }

        return value;
    }

    private static bool ReadNext(ref Utf8JsonReader reader, ref long tokenCount)
    {
        if (!reader.Read())
        {
            return false;
        }

        tokenCount++;
        if (tokenCount > MaximumTokens || reader.CurrentDepth > 64)
        {
            throw new EsmLimitException();
        }

        return true;
    }

    private sealed class DuplicatePropertyException : Exception;

    private sealed class SemanticAliasException : Exception;

    private sealed class EsmLimitException : Exception;

    private sealed class EsmSchemaException : Exception;
}
