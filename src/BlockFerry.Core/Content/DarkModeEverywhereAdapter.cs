using System.Runtime.CompilerServices;
using BlockFerry.Core.Mods;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Content;

internal sealed class DarkModeEverywhereAdapter : IContentAdapter
{
    private const string AdapterId = "appearance";
    private const string ModId = "darkmodeeverywhere";
    private static readonly ContentRelativePath ConfigPath = CreatePath();
    private readonly ConditionalWeakTable<ContentCatalog, PreparedSession> preparedCatalogs = new();

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

        var item = ContentCatalogItem.Create(
            CreateItemId(),
            "深色模式",
            prepared!.Disposition switch
            {
                PlannedContentDisposition.Same => "Dark Mode Everywhere 当前模式已一致",
                PlannedContentDisposition.Add => "目标尚未初始化 · 可创建配置 · 默认跳过",
                _ => "Dark Mode Everywhere 当前模式",
            },
            prepared.Disposition,
            isSelectable: prepared.Disposition is PlannedContentDisposition.Add or
                PlannedContentDisposition.Update,
            isSelectedByDefault: false,
            ConflictResolution.Skip,
            null);
        var catalog = ContentCatalog.Create(AdapterId, [item], prepared.Diagnostics);
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

            var currentSource = context.Source.Read(
                ConfigPath,
                new ContentReadLimits(DarkModeEverywhereDocument.MaximumFileBytes),
                cancellationToken);
            var currentTarget = context.Target.Read(
                ConfigPath,
                new ContentReadLimits(DarkModeEverywhereDocument.MaximumFileBytes),
                cancellationToken);
            if (!SameSnapshot(prepared.SourceSnapshot, currentSource) ||
                !SameSnapshot(prepared.TargetSnapshot, currentTarget) ||
                !TryResolve(currentSource, currentTarget, out var mappedIndex, out _) ||
                mappedIndex != prepared.MappedTargetIndex)
            {
                return RejectedPlan(ContentDiagnosticCode.StaleContext);
            }

            var selected = selection.SelectedItems.Contains(catalog.Items.Single().Id);
            var planItem = ContentPlanItem.Create(
                catalog.Items.Single().Id,
                prepared.Disposition is PlannedContentDisposition.Add or
                    PlannedContentDisposition.Update
                    ? selected
                        ? prepared.Disposition
                        : PlannedContentDisposition.Unselected
                    : PlannedContentDisposition.Same,
                ConflictResolution.Skip,
                (prepared.Disposition is PlannedContentDisposition.Add or
                    PlannedContentDisposition.Update) && selected
                    ? prepared.Disposition == PlannedContentDisposition.Add
                        ? "将创建配置并采用来源深色模式"
                        : "将采用来源深色模式"
                    : prepared.Disposition == PlannedContentDisposition.Same
                        ? "深色模式已经一致"
                        : "未选择，保留目标模式");
            var changes = selected &&
                (prepared.Disposition is PlannedContentDisposition.Add or PlannedContentDisposition.Update)
                ? new[]
                {
                    PlannedFileChange.Create(
                        AdapterId,
                        ConfigPath,
                        prepared.SourceSnapshot,
                        prepared.TargetSnapshot,
                        [planItem]),
                }
                : [];
            return ContentAdapterPlan.Create(AdapterId, [planItem], changes, prepared.Diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CapabilityLimitExceededException)
        {
            return RejectedPlan(ContentDiagnosticCode.LimitExceeded);
        }
        catch (ObjectDisposedException)
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
        if (!string.Equals(plan.AdapterId, AdapterId, StringComparison.Ordinal) ||
            plan.FileChanges.Count > 1)
        {
            throw new ArgumentException("The appearance plan is not adapter-bound.", nameof(plan));
        }

        if (plan.FileChanges.Count == 0)
        {
            return ContentStageResult.Create(AdapterId, []);
        }

        var change = plan.FileChanges.Single();
        var disposition = change.Items[0].Disposition;
        var mappedIndex = -1;
        ContentDiagnosticCode? stageRejection = null;
        if (!change.RelativePath.Equals(ConfigPath) ||
            change.Items.Count != 1 ||
            disposition is not (PlannedContentDisposition.Add or PlannedContentDisposition.Update) ||
            !TryResolve(change.SourceSnapshot, change.TargetSnapshot, out mappedIndex, out stageRejection))
        {
            throw new InvalidOperationException(
                $"The appearance stage no longer matches its immutable plan ({stageRejection}).");
        }

        ImmutableByteBuffer after;
        if (disposition == PlannedContentDisposition.Add)
        {
            if (change.TargetSnapshot.Exists)
            {
                throw new InvalidOperationException(
                    "The appearance add stage no longer targets a missing file.");
            }

            after = change.SourceSnapshot.Bytes;
        }
        else
        {
            if (!change.TargetSnapshot.Exists ||
                !DarkModeEverywhereDocument.TryParse(
                    change.TargetSnapshot.Bytes,
                    out var targetDocument,
                    out stageRejection) ||
                targetDocument!.SelectedShaderIndex == mappedIndex)
            {
                throw new InvalidOperationException(
                    $"The appearance update stage no longer matches its immutable plan ({stageRejection}).");
            }

            after = targetDocument.ReplaceSelectedShaderIndex(mappedIndex);
        }

        return ContentStageResult.Create(
            AdapterId,
            [StagedFileMutation.Create(change, after.CopyBytes())]);
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
                [bindingRejection ?? Diagnostic(ContentDiagnosticCode.CapabilityRejected)]);
        }

        for (var index = 0; index < staged.Mutations.Count; index++)
        {
            var expected = staged.Mutations[index].AfterBytes;
            var actual = bound[index];
            ContentDiagnosticCode? schemaRejection = null;
            if (!actual.Exists ||
                actual.Length != expected.Length ||
                !string.Equals(actual.Sha256, expected.Sha256, StringComparison.Ordinal) ||
                !DarkModeEverywhereDocument.TryParse(actual.Bytes, out _, out schemaRejection))
            {
                return ContentVerificationResult.Create(
                    false,
                    [Diagnostic(schemaRejection ?? ContentDiagnosticCode.CapabilityRejected)]);
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
            ? new ReadOnlySet<ContentRelativePath>([ConfigPath])
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
        return storedCandidatePaths.Contains(ConfigPath) &&
               ModDataCompatibilityPolicy.IsSupportedMinecraftTarget(context.TargetMinecraftVersion) &&
               !context.UnsupportedModIds.Contains(ModId) &&
               context.TargetModVersions.TryGetValue(ModId, out var targetVersion) &&
               ModDataCompatibilityPolicy.IsSupportedTargetModVersion(ModId, targetVersion)
            ? new ReadOnlySet<ContentRelativePath>([ConfigPath])
            : new ReadOnlySet<ContentRelativePath>([]);
    }

    private static bool TryPrepare(
        ContentProbeContext context,
        CancellationToken cancellationToken,
        out PreparedSession? prepared,
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
            var source = context.Source.Read(
                ConfigPath,
                new ContentReadLimits(DarkModeEverywhereDocument.MaximumFileBytes),
                cancellationToken);
            var target = context.Target.Read(
                ConfigPath,
                new ContentReadLimits(DarkModeEverywhereDocument.MaximumFileBytes),
                cancellationToken);
            if (!source.Exists)
            {
                rejection = ContentDiagnosticCode.MissingSourceData;
                return false;
            }

            if (!DarkModeEverywhereDocument.TryParse(
                    source.Bytes,
                    out var sourceDocument,
                    out var sourceRejection))
            {
                rejection = sourceRejection ?? ContentDiagnosticCode.UnsupportedSchema;
                return false;
            }

            if (!target.Exists)
            {
                prepared = new PreparedSession(
                    context,
                    source,
                    target,
                    sourceDocument!.SelectedShaderIndex,
                    PlannedContentDisposition.Add,
                    []);
                return true;
            }

            if (!TryResolve(source, target, out var mappedTargetIndex, out var schemaRejection))
            {
                rejection = schemaRejection ?? ContentDiagnosticCode.UnsupportedSchema;
                return false;
            }

            if (!DarkModeEverywhereDocument.TryParse(
                    target.Bytes,
                    out var targetDocument,
                    out schemaRejection))
            {
                rejection = schemaRejection ?? ContentDiagnosticCode.UnsupportedSchema;
                return false;
            }

            prepared = new PreparedSession(
                context,
                source,
                target,
                mappedTargetIndex,
                targetDocument!.SelectedShaderIndex == mappedTargetIndex
                    ? PlannedContentDisposition.Same
                    : PlannedContentDisposition.Update,
                []);
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
        catch (Exception exception) when (
            exception is CapabilityBoundaryException or ArgumentException or InvalidOperationException)
        {
            rejection = ContentDiagnosticCode.CapabilityRejected;
        }

        return false;
    }

    private static bool TryResolve(
        ContentFileSnapshot source,
        ContentFileSnapshot target,
        out int mappedTargetIndex,
        out ContentDiagnosticCode? rejection)
    {
        mappedTargetIndex = -1;
        rejection = null;
        if (!source.Exists ||
            !DarkModeEverywhereDocument.TryParse(
                source.Bytes,
                out var sourceDocument,
                out rejection))
        {
            return false;
        }

        if (!target.Exists)
        {
            mappedTargetIndex = sourceDocument!.SelectedShaderIndex;
            return true;
        }

        return DarkModeEverywhereDocument.TryParse(
                   target.Bytes,
                   out var targetDocument,
                   out rejection) &&
               sourceDocument!.TryMapSelectedShader(targetDocument!, out mappedTargetIndex, out rejection);
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

    private static bool SameSnapshot(ContentFileSnapshot expected, ContentFileSnapshot actual) =>
        expected.RelativePath.Equals(actual.RelativePath) &&
        expected.Exists == actual.Exists &&
        expected.Length == actual.Length &&
        string.Equals(expected.Sha256, actual.Sha256, StringComparison.Ordinal) &&
        expected.LastWriteTimeUtc == actual.LastWriteTimeUtc &&
        expected.WindowsFileAttributes == actual.WindowsFileAttributes &&
        expected.Identity == actual.Identity;

    private static ContentItemId CreateItemId()
    {
        if (!ContentItemId.TryCreate(AdapterId, "dark-mode", out var id))
        {
            throw new InvalidOperationException("The fixed appearance item ID is invalid.");
        }

        return id;
    }

    private static ContentRelativePath CreatePath()
    {
        if (!ContentRelativePath.TryCreate(
                @"config\darkmodeeverywhereshaders.json",
                out var path,
                out _))
        {
            throw new InvalidOperationException("The fixed appearance path is invalid.");
        }

        return path!;
    }

    private static ContentAdapterPlan RejectedPlan(ContentDiagnosticCode code) =>
        ContentAdapterPlan.Create(AdapterId, [], [], [Diagnostic(code)]);

    private static ContentDiagnostic Diagnostic(ContentDiagnosticCode code) =>
        ContentDiagnostic.Create(code, ContentDiagnosticSeverity.Error, AdapterId);

    private sealed class PreparedSession
    {
        private readonly IReadOnlyInstanceAccess sourceAccess;
        private readonly IReadOnlyInstanceAccess targetAccess;

        internal PreparedSession(
            ContentProbeContext context,
            ContentFileSnapshot sourceSnapshot,
            ContentFileSnapshot targetSnapshot,
            int mappedTargetIndex,
            PlannedContentDisposition disposition,
            IReadOnlyList<ContentDiagnostic> diagnostics)
        {
            Generation = context.Generation;
            sourceAccess = context.Source;
            targetAccess = context.Target;
            SourceIdentity = context.Source.Identity;
            TargetIdentity = context.Target.Identity;
            SourceSnapshot = sourceSnapshot;
            TargetSnapshot = targetSnapshot;
            MappedTargetIndex = mappedTargetIndex;
            Disposition = disposition;
            Diagnostics = diagnostics;
        }

        internal long Generation { get; }

        internal ContentInstanceIdentity SourceIdentity { get; }

        internal ContentInstanceIdentity TargetIdentity { get; }

        internal ContentFileSnapshot SourceSnapshot { get; }

        internal ContentFileSnapshot TargetSnapshot { get; }

        internal int MappedTargetIndex { get; }

        internal PlannedContentDisposition Disposition { get; }

        internal IReadOnlyList<ContentDiagnostic> Diagnostics { get; }

        internal bool IsBoundTo(ContentProbeContext context) =>
            context.Generation == Generation &&
            ReferenceEquals(context.Source, sourceAccess) &&
            ReferenceEquals(context.Target, targetAccess) &&
            context.Source.Identity == SourceIdentity &&
            context.Target.Identity == TargetIdentity;
    }
}
