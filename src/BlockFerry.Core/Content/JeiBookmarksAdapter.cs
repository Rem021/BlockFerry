using BlockFerry.Core.System;
using BlockFerry.Core.Mods;
using System.Runtime.CompilerServices;

namespace BlockFerry.Core.Content;

internal sealed class JeiBookmarksAdapter : IContentAdapter
{
    private const string AdapterId = "jei";
    private const int MaximumScopes = 1_024;
    private const int EnumerationProbeLimit = 2_049;
    private static readonly ContentRelativePath Root = CreatePath(string.Empty);
    private static readonly ContentRelativePath ConfigBase = CreatePath("config");
    private static readonly ContentRelativePath JeiBase = CreatePath(@"config\jei");
    private static readonly ContentRelativePath WorldBase = CreatePath(@"config\jei\world");
    private static readonly ContentRelativePath LocalBase = CreatePath(@"config\jei\world\local");
    private static readonly ContentRelativePath ServerBase = CreatePath(@"config\jei\world\server");
    private readonly ConditionalWeakTable<ContentCatalog, JeiBookmarkPreparedSession> preparedCatalogs = new();
    private readonly ConditionalWeakTable<ContentAdapterPlan, DeferredJeiPlanMetadata> deferredPlans = new();
    private readonly IJeiServerScopeHintProvider serverScopeHints;

    internal JeiBookmarksAdapter(IJeiServerScopeHintProvider? serverScopeHints = null)
    {
        this.serverScopeHints = serverScopeHints ?? NoJeiServerScopeHintProvider.Instance;
    }

    public string Id => AdapterId;

    public ContentProbeResult Probe(
        ContentProbeContext context,
        CancellationToken cancellationToken)
    {
        if (!TryPrepare(context, cancellationToken, out var prepared, out var rejection))
        {
            return ContentProbeResult.Create(false, rejection, [Diagnostic(rejection)]);
        }

        var supported = prepared!.Items.Any(item => item.IsSupported);
        ContentDiagnosticCode? disabled = supported
            ? null
            : prepared.Items.Count > 0
                ? prepared.Items[0].DisabledReason ?? ContentDiagnosticCode.UnsupportedSchema
                : ContentDiagnosticCode.MissingSourceData;
        return ContentProbeResult.Create(
            supported,
            disabled,
            prepared.Diagnostics);
    }

    public ContentCatalog BuildCatalog(
        ContentProbeContext context,
        CancellationToken cancellationToken)
    {
        if (!TryPrepare(context, cancellationToken, out var prepared, out var rejection))
        {
            return ContentCatalog.Create(AdapterId, [], [Diagnostic(rejection)]);
        }

        var localIndex = 0;
        var serverIndex = 0;
        var items = new List<ContentCatalogItem>(prepared!.Items.Count);
        foreach (var item in prepared.Items)
        {
            var index = item.ScopeKind == JeiBookmarkScopeKind.Local
                ? ++localIndex
                : ++serverIndex;
            items.Add(MapCatalogItem(item, index));
        }

        var catalog = ContentCatalog.Create(AdapterId, items, prepared.Diagnostics);
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
            if (!prepared.IsBoundTo(context) || CompatibilityRejection(context) is not null)
            {
                return RejectedPlan(ContentDiagnosticCode.StaleContext);
            }

            var actionableIds = selection.SelectedItems;
            HashSet<(JeiBookmarkScopeKind Kind, string Scope)>? currentTargetScopes = null;
            if (prepared.Items.Any(item => actionableIds.Contains(item.Id)))
            {
                var currentSourceScopeDescriptors = EnumerateBothKinds(
                    context.Source,
                    cancellationToken);
                var currentTargetScopeDescriptors = EnumerateBothKinds(
                    context.Target,
                    cancellationToken);
                EnsureNoAliases(currentSourceScopeDescriptors);
                EnsureNoAliases(currentTargetScopeDescriptors);
                EnsureNoCrossRootAliases(
                    currentSourceScopeDescriptors,
                    currentTargetScopeDescriptors);
                if (!prepared.HasSameScopeTopology(
                        SnapshotScopeTopology(currentSourceScopeDescriptors),
                        SnapshotScopeTopology(currentTargetScopeDescriptors)))
                {
                    return RejectedPlan(ContentDiagnosticCode.StaleContext);
                }

                currentTargetScopes = currentTargetScopeDescriptors
                    .Select(value => (value.Kind, value.RawScope))
                    .ToHashSet(ScopeKeyComparer.Instance);
            }

            foreach (var item in prepared.Items.Where(item => actionableIds.Contains(item.Id)))
            {
                var targetScopeExists = currentTargetScopes is not null &&
                                         currentTargetScopes.Contains((item.ScopeKind, item.TargetRawScope));
                if (!item.IsSupported ||
                    targetScopeExists != item.TargetScopeExists ||
                    (!targetScopeExists && item.Disposition != PlannedContentDisposition.Add) ||
                    !TryRereadAndMatch(context, item, cancellationToken))
                {
                    return RejectedPlan(ContentDiagnosticCode.StaleContext);
                }
            }

            var planItems = catalog.Items
                .Select(item => MapPlanItem(item, selection))
                .ToList();
            var itemById = planItems.ToDictionary(item => item.Id);
            var changes = new List<PlannedFileChange>();
            foreach (var preparedItem in prepared.Items)
            {
                var planItem = itemById[preparedItem.Id];
                if (!IsActionable(planItem))
                {
                    continue;
                }

                if (!preparedItem.IsSupported)
                {
                    return RejectedPlan(ContentDiagnosticCode.CapabilityRejected);
                }

                changes.Add(PlannedFileChange.CreateMapped(
                    AdapterId,
                    preparedItem.RelativePath,
                    preparedItem.SourceSnapshot!,
                    preparedItem.TargetSnapshot!,
                    [planItem]));
            }

            var plan = ContentAdapterPlan.Create(AdapterId, planItems, changes, prepared.Diagnostics);
            var deferredSeeds = prepared.Items
                .Where(item =>
                    actionableIds.Contains(item.Id) &&
                    item.ScopeKind == JeiBookmarkScopeKind.Server &&
                    item.Disposition == PlannedContentDisposition.Add &&
                    !item.TargetScopeExists &&
                    !item.TargetScopeConfirmed &&
                    string.Equals(item.RawScope, item.TargetRawScope, StringComparison.Ordinal) &&
                    item.SourceSnapshot is not null)
                .Select(item => new DeferredJeiSeed(
                    item.SourceRelativePath,
                    item.RelativePath,
                    item.SourceSnapshot!.Sha256))
                .ToArray();
            if (deferredSeeds.Length > 0)
            {
                deferredPlans.Add(
                    plan,
                    new DeferredJeiPlanMetadata(Array.AsReadOnly(deferredSeeds)));
            }

            return plan;
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
        catch (SemanticAliasException)
        {
            return RejectedPlan(ContentDiagnosticCode.StaleContext);
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
        if (!string.Equals(plan.AdapterId, AdapterId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The JEI plan is not adapter-bound.", nameof(plan));
        }

        var staged = new List<StagedFileMutation>(plan.FileChanges.Count);
        foreach (var change in plan.FileChanges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (change.Items.Count != 1 ||
                !IsActionable(change.Items[0]) ||
                !change.SourceSnapshot.Exists ||
                !JeiBookmarkDocument.TryValidate(change.SourceSnapshot.Bytes, out _))
            {
                throw new InvalidOperationException("The JEI stage no longer matches its immutable plan.");
            }

            staged.Add(StagedFileMutation.Create(change, change.SourceSnapshot.Bytes.CopyBytes()));
        }

        return ContentStageResult.Create(AdapterId, staged);
    }

    internal IReadOnlyList<DeferredJeiSeed> GetDeferredSeeds(ContentAdapterPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(plan.AdapterId, AdapterId, StringComparison.Ordinal) ||
            !deferredPlans.TryGetValue(plan, out var metadata))
        {
            return Array.Empty<DeferredJeiSeed>();
        }

        return metadata.Seeds;
    }

    internal DeferredJeiResolution ResolveDeferred(
        ContentCatalog catalog,
        DeferredJeiSeed seed)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(seed);
        if (!string.Equals(catalog.AdapterId, AdapterId, StringComparison.Ordinal) ||
            !preparedCatalogs.TryGetValue(catalog, out var prepared))
        {
            return new DeferredJeiResolution(DeferredJeiResolutionKind.Rejected);
        }

        var matches = prepared.Items
            .Where(item =>
                item.ScopeKind == JeiBookmarkScopeKind.Server &&
                item.SourceRelativePath.Equals(seed.SourceRelativePath) &&
                item.SourceSnapshot is not null &&
                string.Equals(
                    item.SourceSnapshot.Sha256,
                    seed.SourceSha256,
                    StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
        {
            return new DeferredJeiResolution(DeferredJeiResolutionKind.Rejected);
        }

        var item = matches[0];
        var mapsToProvisionalPath = item.RelativePath.Equals(seed.ProvisionalTargetRelativePath);
        var alternativeServerScopes = prepared.TargetScopes.Count(scope =>
            scope.Kind == JeiBookmarkScopeKind.Server &&
            !string.Equals(
                scope.Scope,
                item.RawScope,
                StringComparison.OrdinalIgnoreCase));
        if (mapsToProvisionalPath && alternativeServerScopes == 0)
        {
            return new DeferredJeiResolution(DeferredJeiResolutionKind.PendingTargetScope);
        }

        if (item.DisabledReason is not null)
        {
            return new DeferredJeiResolution(
                alternativeServerScopes == 0
                    ? DeferredJeiResolutionKind.PendingTargetScope
                    : DeferredJeiResolutionKind.Conflict);
        }

        return item.Disposition switch
        {
            PlannedContentDisposition.Add when !mapsToProvisionalPath =>
                new DeferredJeiResolution(DeferredJeiResolutionKind.Ready, item.Id),
            PlannedContentDisposition.Same when !mapsToProvisionalPath =>
                new DeferredJeiResolution(DeferredJeiResolutionKind.Complete, item.Id),
            PlannedContentDisposition.Conflict when
                !mapsToProvisionalPath &&
                item.TargetSnapshot is not null &&
                JeiBookmarkDocument.IsEmpty(item.TargetSnapshot.Bytes) =>
                new DeferredJeiResolution(DeferredJeiResolutionKind.ReadyReplaceEmpty, item.Id),
            PlannedContentDisposition.Conflict when !mapsToProvisionalPath =>
                new DeferredJeiResolution(DeferredJeiResolutionKind.Conflict, item.Id),
            _ => new DeferredJeiResolution(DeferredJeiResolutionKind.Rejected),
        };
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
            var equivalent = false;
            var validSchema = JeiBookmarkDocument.TryValidate(actual.Bytes, out rejection);
            var comparable = validSchema && JeiBookmarkDocument.TryCompare(
                expected,
                actual.Bytes,
                out equivalent,
                out rejection);
            if (!actual.Exists ||
                actual.Length != expected.Length ||
                !string.Equals(actual.Sha256, expected.Sha256, StringComparison.Ordinal) ||
                !validSchema ||
                !comparable ||
                !equivalent)
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
        if (!TryPrepare(context, cancellationToken, out var prepared, out _))
        {
            return new ReadOnlySet<ContentRelativePath>([]);
        }

        return new ReadOnlySet<ContentRelativePath>(
            prepared!.Items
                .Where(item => item.IsSupported)
                .Select(item => item.RelativePath));
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
        if (RecoveryCompatibilityRejection(context) is not null)
        {
            return new ReadOnlySet<ContentRelativePath>([]);
        }

        var scopes = new HashSet<(JeiBookmarkScopeKind Kind, string Scope)>(ScopeKeyComparer.Instance);
        var allowed = new List<ContentRelativePath>();
        foreach (var path in storedCandidatePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segments = path?.Value.Split('\\');
            if (path is null ||
                segments is null ||
                segments.Length != 6 ||
                !string.Equals(segments[0], "config", StringComparison.Ordinal) ||
                !string.Equals(segments[1], "jei", StringComparison.Ordinal) ||
                !string.Equals(segments[2], "world", StringComparison.Ordinal) ||
                !string.Equals(segments[5], "bookmarks.json", StringComparison.Ordinal))
            {
                continue;
            }

            var kind = segments[3] switch
            {
                "local" => JeiBookmarkScopeKind.Local,
                "server" => JeiBookmarkScopeKind.Server,
                _ => (JeiBookmarkScopeKind?)null,
            };
            var rawScope = segments[4];
            if (kind is null ||
                string.IsNullOrEmpty(rawScope) ||
                !scopes.Add((kind.Value, rawScope)))
            {
                continue;
            }

            allowed.Add(path);
        }

        return new ReadOnlySet<ContentRelativePath>(allowed);
    }

    private bool TryPrepare(
        ContentProbeContext context,
        CancellationToken cancellationToken,
        out JeiBookmarkPreparedSession? prepared,
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
            var sourceScopes = EnumerateBothKinds(context.Source, cancellationToken);
            var targetScopes = EnumerateBothKinds(context.Target, cancellationToken);
            EnsureNoAliases(sourceScopes);
            EnsureNoAliases(targetScopes);
            EnsureNoCrossRootAliases(sourceScopes, targetScopes);
            var serverScopeMap = BuildServerScopeMap(sourceScopes, targetScopes);
            var confirmedServerScopeHints = BuildConfirmedServerScopeHints(
                context.Source,
                sourceScopes,
                targetScopes,
                cancellationToken);

            var items = new List<JeiBookmarkPreparedItem>();
            var diagnostics = new List<ContentDiagnostic>();
            foreach (var sourceScope in sourceScopes
                         .OrderBy(value => value.Kind)
                         .ThenBy(value => value.RawScope, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = context.CreateGenerationBoundOpaqueId(
                    AdapterId,
                    ScopeKindId(sourceScope.Kind),
                    sourceScope.RawScope.AsSpan());
                var sourcePath = CreateBookmarkPath(
                    sourceScope.Kind,
                    sourceScope.RawScope,
                    "bookmarks.json");
                ContentFileSnapshot? sourceSnapshot;
                try
                {
                    sourceSnapshot = context.Source.Read(
                        sourcePath,
                        new ContentReadLimits(JeiBookmarkDocument.MaximumFileBytes),
                        cancellationToken);
                }
                catch (CapabilityLimitExceededException)
                {
                    items.Add(UnsupportedItem(
                        id,
                        sourceScope,
                        sourcePath,
                        ContentDiagnosticCode.LimitExceeded,
                        isLegacy: false));
                    diagnostics.Add(Diagnostic(ContentDiagnosticCode.LimitExceeded, id));
                    continue;
                }

                if (!sourceSnapshot.Exists)
                {
                    var hasLegacy = LegacyExists(context.Source, sourceScope, cancellationToken);
                    if (!hasLegacy)
                    {
                        continue;
                    }

                    items.Add(UnsupportedItem(
                        id,
                        sourceScope,
                        sourcePath,
                        ContentDiagnosticCode.UnsupportedSchema,
                        isLegacy: true));
                    diagnostics.Add(Diagnostic(ContentDiagnosticCode.UnsupportedSchema, id));
                    continue;
                }

                if (!JeiBookmarkDocument.TryValidate(sourceSnapshot.Bytes, out var sourceRejection))
                {
                    var code = sourceRejection ?? ContentDiagnosticCode.UnsupportedSchema;
                    items.Add(UnsupportedItem(id, sourceScope, sourcePath, code, isLegacy: false));
                    diagnostics.Add(Diagnostic(code, id));
                    continue;
                }

                var targetScopeResolution = ResolveTargetScope(
                    sourceScope,
                    sourceScopes,
                    targetScopes,
                    serverScopeMap,
                    confirmedServerScopeHints,
                    context.Target,
                    sourceSnapshot,
                    cancellationToken);
                if (targetScopeResolution is null)
                {
                    items.Add(new JeiBookmarkPreparedItem(
                        id,
                        sourceScope.Kind,
                        sourceScope.RawScope,
                        sourceScope.RawScope,
                        sourcePath,
                        sourcePath,
                        false,
                        false,
                        sourceSnapshot,
                        null,
                        PlannedContentDisposition.Unsupported,
                        ContentDiagnosticCode.MissingTargetData,
                        isLegacy: false));
                    diagnostics.Add(Diagnostic(ContentDiagnosticCode.MissingTargetData, id));
                    continue;
                }

                var targetRawScope = targetScopeResolution.RawScope;

                var targetPath = CreateBookmarkPath(
                    sourceScope.Kind,
                    targetRawScope,
                    "bookmarks.json");
                var targetScopeExists = targetScopes.Any(value =>
                    ScopeKeyComparer.Instance.Equals(
                        (value.Kind, value.RawScope),
                        (sourceScope.Kind, targetRawScope)));
                ContentFileSnapshot? targetSnapshot;
                try
                {
                    targetSnapshot = context.Target.Read(
                        targetPath,
                        new ContentReadLimits(JeiBookmarkDocument.MaximumFileBytes),
                        cancellationToken);
                }
                catch (CapabilityLimitExceededException)
                {
                    items.Add(UnsupportedItem(
                        id,
                        sourceScope,
                        sourcePath,
                        ContentDiagnosticCode.LimitExceeded,
                        isLegacy: false));
                    diagnostics.Add(Diagnostic(ContentDiagnosticCode.LimitExceeded, id));
                    continue;
                }

                if (!targetSnapshot.Exists)
                {
                    items.Add(new JeiBookmarkPreparedItem(
                        id,
                        sourceScope.Kind,
                        sourceScope.RawScope,
                        targetRawScope,
                        sourcePath,
                        targetPath,
                        targetScopeExists,
                        targetScopeResolution.IsConfirmed,
                        sourceSnapshot,
                        targetSnapshot,
                        PlannedContentDisposition.Add,
                        null,
                        isLegacy: false));
                    diagnostics.Add(Information(ContentDiagnosticCode.MissingTargetData, id));
                    continue;
                }

                if (!JeiBookmarkDocument.TryValidate(targetSnapshot.Bytes, out var targetRejection))
                {
                    var code = targetRejection ?? ContentDiagnosticCode.UnsupportedSchema;
                    items.Add(UnsupportedItem(id, sourceScope, sourcePath, code, isLegacy: false));
                    diagnostics.Add(Diagnostic(code, id));
                    continue;
                }

                if (!JeiBookmarkDocument.TryCompare(
                        sourceSnapshot.Bytes,
                        targetSnapshot.Bytes,
                        out var equivalent,
                        out var comparisonRejection))
                {
                    var code = comparisonRejection ?? ContentDiagnosticCode.UnsupportedSchema;
                    items.Add(UnsupportedItem(id, sourceScope, sourcePath, code, isLegacy: false));
                    diagnostics.Add(Diagnostic(code, id));
                    continue;
                }

                items.Add(new JeiBookmarkPreparedItem(
                    id,
                    sourceScope.Kind,
                    sourceScope.RawScope,
                    targetRawScope,
                    sourcePath,
                    targetPath,
                    targetScopeExists,
                    targetScopeResolution.IsConfirmed,
                    sourceSnapshot,
                    targetSnapshot,
                    equivalent
                        ? PlannedContentDisposition.Same
                        : PlannedContentDisposition.Conflict,
                    null,
                    isLegacy: false));
            }

            if (items.Count == 0)
            {
                diagnostics.Add(Diagnostic(ContentDiagnosticCode.MissingSourceData));
            }

            prepared = new JeiBookmarkPreparedSession(
                context,
                items,
                diagnostics,
                SnapshotScopeTopology(sourceScopes),
                SnapshotScopeTopology(targetScopes));
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

    private static bool TryRereadAndMatch(
        ContentProbeContext context,
        JeiBookmarkPreparedItem item,
        CancellationToken cancellationToken)
    {
        if (!item.IsSupported)
        {
            return false;
        }

        var source = context.Source.Read(
            item.SourceRelativePath,
            new ContentReadLimits(JeiBookmarkDocument.MaximumFileBytes),
            cancellationToken);
        var target = context.Target.Read(
            item.RelativePath,
            new ContentReadLimits(JeiBookmarkDocument.MaximumFileBytes),
            cancellationToken);
        return SameSnapshot(item.SourceSnapshot!, source) &&
               SameSnapshot(item.TargetSnapshot!, target) &&
               JeiBookmarkDocument.TryValidate(source.Bytes, out _) &&
               (!target.Exists || JeiBookmarkDocument.TryValidate(target.Bytes, out _));
    }

    private static ContentFileSnapshot MissingSnapshot(ContentRelativePath path) =>
        ContentFileSnapshot.Create(
            path,
            false,
            [],
            DateTimeOffset.UnixEpoch,
            0,
            null);

    private static ContentCatalogItem MapCatalogItem(JeiBookmarkPreparedItem item, int index)
    {
        var displayName = item.ScopeKind == JeiBookmarkScopeKind.Local
            ? $"单人收藏 {index}"
            : $"多人收藏 {index}";
        var description = item.IsLegacy
            ? "旧版收藏暂不支持"
            : item.DisabledReason == ContentDiagnosticCode.MissingTargetData &&
              item.ScopeKind == JeiBookmarkScopeKind.Server
                ? "目标中没有可唯一对应的收藏作用域"
            : item.DisabledReason is not null
                ? "收藏格式或对应关系暂不支持"
                : item.Disposition switch
                {
                    PlannedContentDisposition.Same => "整份收藏与目标一致",
                    PlannedContentDisposition.Add when
                        item.ScopeKind == JeiBookmarkScopeKind.Server && !item.TargetScopeExists =>
                        "新增服务器收藏作用域 · 默认跳过",
                    PlannedContentDisposition.Add => "目标中暂无收藏 · 默认跳过",
                    PlannedContentDisposition.Conflict => "整份收藏不同 · 默认保留目标",
                    _ => "收藏暂不支持",
                };
        return ContentCatalogItem.Create(
            item.Id,
            displayName,
            description,
            item.Disposition,
            isSelectable: item.Disposition is PlannedContentDisposition.Add or PlannedContentDisposition.Conflict,
            isSelectedByDefault: false,
            item.Disposition == PlannedContentDisposition.Conflict
                ? ConflictResolution.KeepTarget
                : ConflictResolution.Skip,
            item.DisabledReason);
    }

    private static ContentPlanItem MapPlanItem(
        ContentCatalogItem item,
        ValidatedContentSelection selection)
    {
        if (item.Disposition == PlannedContentDisposition.Conflict)
        {
            if (!selection.ConflictResolutions.TryGetValue(item.Id, out var resolution))
            {
                throw new InvalidOperationException("A JEI conflict has no validated resolution.");
            }

            return ContentPlanItem.Create(
                item.Id,
                PlannedContentDisposition.Conflict,
                resolution,
                resolution == ConflictResolution.UseSource
                    ? "将采用来源整份收藏"
                    : "将保留目标整份收藏");
        }

        if (item.Disposition == PlannedContentDisposition.Add)
        {
            var selected = selection.SelectedItems.Contains(item.Id);
            return ContentPlanItem.Create(
                item.Id,
                selected ? PlannedContentDisposition.Add : PlannedContentDisposition.Unselected,
                ConflictResolution.Skip,
                selected ? "将新增整份收藏" : "默认跳过，保持目标现状");
        }

        return ContentPlanItem.Create(
            item.Id,
            item.Disposition,
            ConflictResolution.Skip,
            item.Disposition == PlannedContentDisposition.Same
                ? "收藏内容一致，无需写入"
                : "暂不支持，不会写入");
    }

    private static bool LegacyExists(
        IReadOnlyInstanceAccess access,
        ScopeDescriptor scope,
        CancellationToken cancellationToken)
    {
        var legacyPath = CreateBookmarkPath(scope.Kind, scope.RawScope, "bookmarks.ini");
        try
        {
            return access.Read(legacyPath, new ContentReadLimits(1), cancellationToken).Exists;
        }
        catch (CapabilityLimitExceededException)
        {
            return true;
        }
    }

    private static TargetScopeResolution? ResolveTargetScope(
        ScopeDescriptor sourceScope,
        IReadOnlyList<ScopeDescriptor> sourceScopes,
        IReadOnlyList<ScopeDescriptor> targetScopes,
        Dictionary<(JeiBookmarkScopeKind Kind, string Scope), string> serverScopeMap,
        Dictionary<(JeiBookmarkScopeKind Kind, string Scope), string> confirmedServerScopeHints,
        IReadOnlyInstanceAccess target,
        ContentFileSnapshot sourceSnapshot,
        CancellationToken cancellationToken)
    {
        var sameKind = targetScopes
            .Where(value => value.Kind == sourceScope.Kind)
            .ToList();
        var exact = sameKind.SingleOrDefault(value => ScopeKeyComparer.Instance.Equals(
            (value.Kind, value.RawScope),
            (sourceScope.Kind, sourceScope.RawScope)));
        if (sourceScope.Kind == JeiBookmarkScopeKind.Local)
        {
            return exact is null ? null : new TargetScopeResolution(exact.RawScope, true);
        }

        if (serverScopeMap.TryGetValue(
                (sourceScope.Kind, sourceScope.RawScope),
                out var globallyMappedTarget))
        {
            return new TargetScopeResolution(globallyMappedTarget, true);
        }

        if (confirmedServerScopeHints.TryGetValue(
                (sourceScope.Kind, sourceScope.RawScope),
                out var confirmedTarget))
        {
            return new TargetScopeResolution(confirmedTarget, true);
        }

        var sourceServerScopes = sourceScopes
            .Where(value => value.Kind == JeiBookmarkScopeKind.Server)
            .ToList();
        var targetServerScopes = sameKind;
        if (targetServerScopes.Count == 0)
        {
            return new TargetScopeResolution(sourceScope.RawScope, false);
        }

        var alternatives = targetServerScopes
            .Where(targetScope => exact is null ||
                                  !ScopeKeyComparer.Instance.Equals(
                                      (targetScope.Kind, targetScope.RawScope),
                                      (exact.Kind, exact.RawScope)))
            .ToList();
        if (sourceServerScopes.Count == 1 && exact is not null && alternatives.Count == 1)
        {
            var exactSnapshot = target.Read(
                CreateBookmarkPath(exact.Kind, exact.RawScope, "bookmarks.json"),
                new ContentReadLimits(JeiBookmarkDocument.MaximumFileBytes),
                cancellationToken);
            var alternativePath = CreateBookmarkPath(
                alternatives[0].Kind,
                alternatives[0].RawScope,
                "bookmarks.json");
            var alternativeSnapshot = target.Read(
                alternativePath,
                new ContentReadLimits(JeiBookmarkDocument.MaximumFileBytes),
                cancellationToken);
            if (exactSnapshot.Exists &&
                JeiBookmarkDocument.TryValidate(exactSnapshot.Bytes, out _) &&
                JeiBookmarkDocument.TryCompare(
                    sourceSnapshot.Bytes,
                    exactSnapshot.Bytes,
                    out var equivalent,
                    out _) &&
                equivalent &&
                (!alternativeSnapshot.Exists ||
                 JeiBookmarkDocument.TryValidate(alternativeSnapshot.Bytes, out _)))
            {
                return new TargetScopeResolution(alternatives[0].RawScope, true);
            }
        }

        return null;
    }

    private Dictionary<(JeiBookmarkScopeKind Kind, string Scope), string>
        BuildConfirmedServerScopeHints(
            IReadOnlyInstanceAccess source,
            IReadOnlyList<ScopeDescriptor> sourceScopes,
            IReadOnlyList<ScopeDescriptor> targetScopes,
            CancellationToken cancellationToken)
    {
        var result = new Dictionary<(JeiBookmarkScopeKind Kind, string Scope), string>(
            ScopeKeyComparer.Instance);
        var sourceServers = sourceScopes
            .Where(value => value.Kind == JeiBookmarkScopeKind.Server)
            .ToArray();
        if (sourceServers.Length != 1 ||
            targetScopes.Any(value => value.Kind == JeiBookmarkScopeKind.Server))
        {
            return result;
        }

        var sourceScope = sourceServers[0];
        var targetScope = serverScopeHints.TryResolveTargetScope(
            source,
            sourceScope.RawScope,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(targetScope))
        {
            return result;
        }

        try
        {
            _ = CreateBookmarkPath(
                JeiBookmarkScopeKind.Server,
                targetScope,
                "bookmarks.json");
        }
        catch (CapabilityBoundaryException)
        {
            return result;
        }

        result.Add((JeiBookmarkScopeKind.Server, sourceScope.RawScope), targetScope);
        return result;
    }

    private static Dictionary<(JeiBookmarkScopeKind Kind, string Scope), string>
        BuildServerScopeMap(
            IReadOnlyList<ScopeDescriptor> sourceScopes,
            IReadOnlyList<ScopeDescriptor> targetScopes)
    {
        var result = new Dictionary<(JeiBookmarkScopeKind Kind, string Scope), string>(
            ScopeKeyComparer.Instance);
        var sourceServers = sourceScopes
            .Where(value => value.Kind == JeiBookmarkScopeKind.Server)
            .ToList();
        var targetServers = targetScopes
            .Where(value => value.Kind == JeiBookmarkScopeKind.Server)
            .ToList();

        var unmatchedSources = new List<ScopeDescriptor>();
        foreach (var source in sourceServers)
        {
            var exact = targetServers.SingleOrDefault(targetScope =>
                ScopeKeyComparer.Instance.Equals(
                    (source.Kind, source.RawScope),
                    (targetScope.Kind, targetScope.RawScope)));
            if (exact is null)
            {
                unmatchedSources.Add(source);
                continue;
            }

            result.Add((source.Kind, source.RawScope), exact.RawScope);
        }

        var unmatchedTargets = targetServers
            .Where(targetScope => !sourceServers.Any(source =>
                ScopeKeyComparer.Instance.Equals(
                    (source.Kind, source.RawScope),
                    (targetScope.Kind, targetScope.RawScope))))
            .ToList();

        if (unmatchedTargets.Count > unmatchedSources.Count)
        {
            result.Clear();
        }
        else if (unmatchedSources.Count == 1 && unmatchedTargets.Count == 1)
        {
            result.Add(
                (unmatchedSources[0].Kind, unmatchedSources[0].RawScope),
                unmatchedTargets[0].RawScope);
        }

        return result;
    }

    private static List<ScopeDescriptor> EnumerateBothKinds(
        IReadOnlyInstanceAccess access,
        CancellationToken cancellationToken)
    {
        var scopes = new List<ScopeDescriptor>();
        if (!ContainsExactDirectory(access, Root, "config", cancellationToken) ||
            !ContainsExactDirectory(access, ConfigBase, "jei", cancellationToken) ||
            !ContainsExactDirectory(access, JeiBase, "world", cancellationToken))
        {
            return scopes;
        }

        if (ContainsExactDirectory(access, WorldBase, "local", cancellationToken))
        {
            AddScopes(scopes, JeiBookmarkScopeKind.Local, EnumerateScopes(access, LocalBase, cancellationToken));
        }

        if (ContainsExactDirectory(access, WorldBase, "server", cancellationToken))
        {
            AddScopes(scopes, JeiBookmarkScopeKind.Server, EnumerateScopes(access, ServerBase, cancellationToken));
        }

        if (scopes.Count > MaximumScopes)
        {
            throw new CapabilityLimitExceededException("The bounded JEI scope count was exceeded.");
        }

        return scopes;
    }

    private static bool ContainsExactDirectory(
        IReadOnlyInstanceAccess access,
        ContentRelativePath parent,
        string childName,
        CancellationToken cancellationToken)
    {
        var exact = false;
        foreach (var entry in access.Enumerate(
                     parent,
                     new ContentEnumerationLimits(EnumerationProbeLimit),
                     cancellationToken))
        {
            var leaf = entry.RelativePath.Value[(parent.Value.Length == 0 ? 0 : parent.Value.Length + 1)..];
            if (!string.Equals(leaf, childName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!entry.IsDirectory || !string.Equals(leaf, childName, StringComparison.Ordinal))
            {
                throw new SemanticAliasException();
            }

            exact = true;
        }

        return exact;
    }

    private static List<string> EnumerateScopes(
        IReadOnlyInstanceAccess access,
        ContentRelativePath basePath,
        CancellationToken cancellationToken)
    {
        var values = new List<string>();
        foreach (var entry in access.Enumerate(
                     basePath,
                     new ContentEnumerationLimits(EnumerationProbeLimit),
                     cancellationToken))
        {
            if (!entry.IsDirectory)
            {
                continue;
            }

            var prefix = basePath.Value + "\\";
            if (!entry.RelativePath.Value.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new CapabilityBoundaryException("The JEI scope enumeration was relabeled.");
            }

            var rawScope = entry.RelativePath.Value[prefix.Length..];
            if (rawScope.Length == 0 || rawScope.Contains('\\', StringComparison.Ordinal))
            {
                throw new CapabilityBoundaryException("The JEI scope was not an immediate child.");
            }

            values.Add(rawScope);
        }

        if (values.Count > MaximumScopes)
        {
            throw new CapabilityLimitExceededException("The bounded JEI scope count was exceeded.");
        }

        values.Sort(StringComparer.Ordinal);
        return values;
    }

    private static void AddScopes(
        List<ScopeDescriptor> target,
        JeiBookmarkScopeKind kind,
        IReadOnlyList<string> rawScopes)
    {
        foreach (var rawScope in rawScopes)
        {
            target.Add(new ScopeDescriptor(kind, rawScope));
        }
    }

    private static void EnsureNoAliases(IReadOnlyList<ScopeDescriptor> scopes)
    {
        var keys = new HashSet<(JeiBookmarkScopeKind Kind, string Scope)>(ScopeKeyComparer.Instance);
        foreach (var scope in scopes)
        {
            if (!keys.Add((scope.Kind, scope.RawScope)))
            {
                throw new SemanticAliasException();
            }
        }
    }

    private static void EnsureNoCrossRootAliases(
        IReadOnlyList<ScopeDescriptor> source,
        IReadOnlyList<ScopeDescriptor> target)
    {
        var sourceAliases = source.ToDictionary(
            value => (value.Kind, value.RawScope),
            value => value.RawScope,
            ScopeKeyComparer.Instance);
        foreach (var candidate in target)
        {
            if (sourceAliases.TryGetValue((candidate.Kind, candidate.RawScope), out var raw) &&
                !string.Equals(raw, candidate.RawScope, StringComparison.Ordinal))
            {
                throw new SemanticAliasException();
            }
        }
    }

    private static JeiBookmarkScopeTopologyEntry[] SnapshotScopeTopology(
        IEnumerable<ScopeDescriptor> scopes) =>
        scopes
            .Select(scope => new JeiBookmarkScopeTopologyEntry(scope.Kind, scope.RawScope))
            .OrderBy(scope => scope.Kind)
            .ThenBy(scope => scope.Scope, StringComparer.Ordinal)
            .ToArray();

    private static JeiBookmarkPreparedItem UnsupportedItem(
        ContentItemId id,
        ScopeDescriptor scope,
        ContentRelativePath path,
        ContentDiagnosticCode reason,
        bool isLegacy) =>
        new(
            id,
            scope.Kind,
            scope.RawScope,
            scope.RawScope,
            path,
            path,
            false,
            false,
            null,
            null,
            PlannedContentDisposition.Unsupported,
            reason,
            isLegacy);

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

        if (compatibility.DetectedUnsupportedModIds.Contains(AdapterId) ||
            !compatibility.SourceModVersions.TryGetValue(AdapterId, out var sourceJei) ||
            !compatibility.TargetModVersions.TryGetValue(AdapterId, out var targetJei) ||
            !ModDataCompatibilityPolicy.AreModVersionsCompatible(
                AdapterId,
                sourceJei,
                targetJei))
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

        if (context.UnsupportedModIds.Contains(AdapterId) ||
            !context.TargetModVersions.TryGetValue(AdapterId, out var targetJei) ||
            !ModDataCompatibilityPolicy.IsSupportedTargetModVersion(AdapterId, targetJei))
        {
            return ContentDiagnosticCode.UnsupportedModVersion;
        }

        return null;
    }

    private static bool SameSnapshot(ContentFileSnapshot expected, ContentFileSnapshot actual) =>
        expected.RelativePath.Equals(actual.RelativePath) &&
        expected.Exists == actual.Exists &&
        expected.Length == actual.Length &&
        string.Equals(expected.Sha256, actual.Sha256, StringComparison.Ordinal) &&
        expected.LastWriteTimeUtc == actual.LastWriteTimeUtc &&
        expected.WindowsFileAttributes == actual.WindowsFileAttributes &&
        expected.Identity == actual.Identity;

    private static bool IsActionable(ContentPlanItem item) =>
        item.Disposition is PlannedContentDisposition.Add or PlannedContentDisposition.Update ||
        item.Disposition == PlannedContentDisposition.Conflict &&
        item.Resolution == ConflictResolution.UseSource;

    private static string ScopeKindId(JeiBookmarkScopeKind kind) => kind switch
    {
        JeiBookmarkScopeKind.Local => "local",
        JeiBookmarkScopeKind.Server => "server",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static ContentRelativePath CreateBookmarkPath(
        JeiBookmarkScopeKind kind,
        string rawScope,
        string fileName) =>
        CreatePath($@"config\jei\world\{ScopeKindId(kind)}\{rawScope}\{fileName}");

    private static ContentRelativePath CreatePath(string value)
    {
        if (!ContentRelativePath.TryCreate(value, out var path, out _))
        {
            throw new CapabilityBoundaryException("A JEI relative path was rejected.");
        }

        return path!;
    }

    private static ContentAdapterPlan RejectedPlan(ContentDiagnosticCode code) =>
        ContentAdapterPlan.Create(AdapterId, [], [], [Diagnostic(code)]);

    private static ContentDiagnostic Diagnostic(
        ContentDiagnosticCode code,
        ContentItemId? itemId = null) =>
        ContentDiagnostic.Create(
            code,
            ContentDiagnosticSeverity.Error,
            AdapterId,
            itemId);

    private static ContentDiagnostic Information(
        ContentDiagnosticCode code,
        ContentItemId itemId) =>
        ContentDiagnostic.Create(
            code,
            ContentDiagnosticSeverity.Information,
            AdapterId,
            itemId);

    private sealed record ScopeDescriptor(JeiBookmarkScopeKind Kind, string RawScope);

    private sealed record TargetScopeResolution(string RawScope, bool IsConfirmed);

    private sealed class ScopeKeyComparer : IEqualityComparer<(JeiBookmarkScopeKind Kind, string Scope)>
    {
        internal static ScopeKeyComparer Instance { get; } = new();

        public bool Equals(
            (JeiBookmarkScopeKind Kind, string Scope) x,
            (JeiBookmarkScopeKind Kind, string Scope) y) =>
            x.Kind == y.Kind &&
            string.Equals(
                x.Scope.Normalize(global::System.Text.NormalizationForm.FormC),
                y.Scope.Normalize(global::System.Text.NormalizationForm.FormC),
                StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((JeiBookmarkScopeKind Kind, string Scope) obj) =>
            HashCode.Combine(
                obj.Kind,
                StringComparer.OrdinalIgnoreCase.GetHashCode(
                    obj.Scope.Normalize(global::System.Text.NormalizationForm.FormC)));
    }

    private sealed class SemanticAliasException : Exception;
}
