using BlockFerry.Core.Options;
using BlockFerry.Core.Pcl2;
using BlockFerry.Core.System;
using BlockFerry.Core.Mods;
using System.Runtime.CompilerServices;

namespace BlockFerry.Core.Content;

internal sealed class VanillaOptionsAdapter : IContentAdapter
{
    private const string AdapterId = "vanilla";
    private static readonly ContentRelativePath OptionsPath = CreateOptionsPath();
    private static readonly ContentRelativePath FancyMenuScaleMarkerPath =
        CreatePath(@"fancymenu_data\default_scale_set.fm");
    private static readonly byte[] FancyMenuScaleMarkerBytes =
        "You're not supposed to be here! Shoo!"u8.ToArray();
    private readonly Pcl2OptionsMigrationPreviewer previewer;
    private readonly ConditionalWeakTable<ContentCatalog, Pcl2ContentOptionsSelectionSession>
        preparedCatalogs = new();

    internal VanillaOptionsAdapter(Pcl2OptionsMigrationPreviewer previewer)
    {
        ArgumentNullException.ThrowIfNull(previewer);
        this.previewer = previewer;
    }

    public string Id => AdapterId;

    public ContentProbeResult Probe(
        ContentProbeContext context,
        CancellationToken cancellationToken)
    {
        if (!TryPrepare(
                context,
                cancellationToken,
                out var prepared,
                out var diagnostics,
                out var rejection))
        {
            return ContentProbeResult.Create(false, rejection, diagnostics);
        }

        try
        {
            _ = MapCatalog(prepared!.Catalog, diagnostics);
        }
        catch (ArgumentException)
        {
            return ContentProbeResult.Create(
                false,
                ContentDiagnosticCode.UnsupportedSchema,
                [Diagnostic(ContentDiagnosticCode.UnsupportedSchema)]);
        }

        return ContentProbeResult.Create(true, null, diagnostics);
    }

    public ContentCatalog BuildCatalog(
        ContentProbeContext context,
        CancellationToken cancellationToken)
    {
        if (!TryPrepare(
                context,
                cancellationToken,
                out var prepared,
                out var diagnostics,
                out _))
        {
            return ContentCatalog.Create(AdapterId, [], diagnostics);
        }

        try
        {
            var catalog = MapCatalog(prepared!.Catalog, diagnostics);
            preparedCatalogs.Add(catalog, prepared);
            return catalog;
        }
        catch (ArgumentException)
        {
            return ContentCatalog.Create(
                AdapterId,
                [],
                [Diagnostic(ContentDiagnosticCode.UnsupportedSchema)]);
        }
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
            !selection.IsBoundTo(catalog))
        {
            return RejectedPlan(ContentDiagnosticCode.CapabilityRejected);
        }

        if (!preparedCatalogs.TryGetValue(catalog, out var prepared))
        {
            return RejectedPlan(ContentDiagnosticCode.CapabilityRejected);
        }

        if (!TryReadCurrentSnapshots(
                context,
                cancellationToken,
                out var currentSource,
                out var currentTarget,
                out var diagnostics))
        {
            return ContentAdapterPlan.Create(AdapterId, [], [], diagnostics);
        }

        var selectedKeys = selection.SelectedItems
            .Select(item => item.TechnicalKey)
            .ToHashSet(StringComparer.Ordinal);
        var preview = previewer.PreviewSelected(
            prepared,
            context,
            currentSource!,
            currentTarget!,
            selectedKeys,
            cancellationToken);
        if (preview.IsStale || preview.Result is null)
        {
            return RejectedPlan(ContentDiagnosticCode.StaleContext);
        }

        var activateRequiredChanges = selection.SelectedItems.Count > 0;
        var items = catalog.Items
            .Select(item => MapPlanItem(
                item,
                selection.SelectedItems.Contains(item.Id) ||
                activateRequiredChanges &&
                !item.IsSelectable &&
                item.Disposition is PlannedContentDisposition.Add or PlannedContentDisposition.Update))
            .ToList();
        var actionable = items
            .Where(item => item.Disposition is
                PlannedContentDisposition.Add or PlannedContentDisposition.Update)
            .ToList();
        var plannedKeys = preview.Result.PlannedChanges
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (!plannedKeys.SetEquals(actionable.Select(item => item.Id.TechnicalKey)))
        {
            return RejectedPlan(ContentDiagnosticCode.StaleContext);
        }

        var changes = new List<PlannedFileChange>();
        if (preview.Result.Changed)
        {
            try
            {
                _ = Pcl2OptionsMigrationPreviewer.EncodeSelectedContent(
                    preview.Result.Content,
                    prepared.SourceSnapshot,
                    prepared.TargetSnapshot);
            }
            catch (CapabilityLimitExceededException)
            {
                return RejectedPlan(ContentDiagnosticCode.LimitExceeded);
            }

            if (actionable.Count == 0)
            {
                return RejectedPlan(ContentDiagnosticCode.CapabilityRejected);
            }

            changes.Add(PlannedFileChange.Create(
                AdapterId,
                OptionsPath,
                prepared.SourceSnapshot,
                prepared.TargetSnapshot,
                actionable));
        }

        if (selectedKeys.Contains("guiScale"))
        {
            var markerDecision = PrepareFancyMenuScaleMarker(
                context,
                cancellationToken,
                out var markerSource,
                out var markerTarget,
                out var markerRejection);
            if (markerRejection is not null)
            {
                return RejectedPlan(markerRejection.Value);
            }

            if (markerDecision)
            {
                var guiScaleItem = actionable.SingleOrDefault(item =>
                    string.Equals(item.Id.TechnicalKey, "guiScale", StringComparison.Ordinal));
                if (guiScaleItem is null || markerSource is null || markerTarget is null)
                {
                    return RejectedPlan(ContentDiagnosticCode.CapabilityRejected);
                }

                changes.Add(PlannedFileChange.Create(
                    AdapterId,
                    FancyMenuScaleMarkerPath,
                    markerSource,
                    markerTarget,
                    [guiScaleItem]));
            }
        }

        return ContentAdapterPlan.Create(AdapterId, items, changes, diagnostics);
    }

    public ContentStageResult Stage(
        ContentAdapterPlan plan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(plan.AdapterId, AdapterId, StringComparison.Ordinal) ||
            plan.FileChanges.Count > 2)
        {
            throw new ArgumentException("The vanilla plan is not adapter-bound.", nameof(plan));
        }

        if (plan.FileChanges.Count == 0)
        {
            return ContentStageResult.Create(AdapterId, []);
        }

        var optionsChange = plan.FileChanges.SingleOrDefault(change =>
            change.RelativePath.Equals(OptionsPath));
        var markerChange = plan.FileChanges.SingleOrDefault(change =>
            change.RelativePath.Equals(FancyMenuScaleMarkerPath));
        if (optionsChange is null ||
            plan.FileChanges.Any(change =>
                !change.RelativePath.Equals(OptionsPath) &&
                !change.RelativePath.Equals(FancyMenuScaleMarkerPath)))
        {
            throw new ArgumentException("The vanilla plan contains an unexpected path.", nameof(plan));
        }

        var selectedKeys = optionsChange.Items
            .Where(item => item.Disposition is
                PlannedContentDisposition.Add or PlannedContentDisposition.Update)
            .Select(item => item.Id.TechnicalKey)
            .ToHashSet(StringComparer.Ordinal);
        var preview = previewer.PreviewSelectedSnapshots(
            optionsChange.SourceSnapshot,
            optionsChange.TargetSnapshot,
            selectedKeys,
            cancellationToken);
        if (!preview.Changed ||
            !preview.PlannedChanges.Select(item => item.Key).ToHashSet(StringComparer.Ordinal)
                .SetEquals(selectedKeys))
        {
            throw new InvalidOperationException("The vanilla stage no longer matches its immutable plan.");
        }

        var bytes = Pcl2OptionsMigrationPreviewer.EncodeSelectedContent(
            preview.Content,
            optionsChange.SourceSnapshot,
            optionsChange.TargetSnapshot);
        var mutations = new List<StagedFileMutation>
        {
            StagedFileMutation.Create(optionsChange, bytes),
        };
        if (markerChange is not null)
        {
            if (markerChange.TargetSnapshot.Exists ||
                !markerChange.SourceSnapshot.Exists ||
                !markerChange.SourceSnapshot.Bytes.CopyBytes().SequenceEqual(FancyMenuScaleMarkerBytes) ||
                markerChange.Items.Count != 1 ||
                !string.Equals(
                    markerChange.Items[0].Id.TechnicalKey,
                    "guiScale",
                    StringComparison.Ordinal) ||
                markerChange.Items[0].Disposition is not
                    (PlannedContentDisposition.Add or PlannedContentDisposition.Update))
            {
                throw new InvalidOperationException(
                    "The FancyMenu marker no longer matches its immutable vanilla plan.");
            }

            mutations.Add(StagedFileMutation.Create(
                markerChange,
                markerChange.SourceSnapshot.Bytes.CopyBytes()));
        }

        return ContentStageResult.Create(AdapterId, mutations);
    }

    public ContentVerificationResult Verify(
        ContentStageResult staged,
        IReadOnlyList<ContentFileSnapshot> pathBoundRereads,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(pathBoundRereads);
        ContentDiagnostic? rejection = null;
        IReadOnlyList<ContentFileSnapshot> bound = [];
        if (!string.Equals(staged.AdapterId, AdapterId, StringComparison.Ordinal) ||
            !ContentPlanCoordinator.TryBindVerificationRereads(
                staged,
                pathBoundRereads,
                out bound,
                out rejection))
        {
            return ContentVerificationResult.Create(
                false,
                rejection is null ? [Diagnostic(ContentDiagnosticCode.CapabilityRejected)] : [rejection]);
        }

        for (var index = 0; index < staged.Mutations.Count; index++)
        {
            var expected = staged.Mutations[index].AfterBytes;
            var actual = bound[index];
            if (!actual.Exists ||
                actual.Length != expected.Length ||
                !string.Equals(actual.Sha256, expected.Sha256, StringComparison.Ordinal))
            {
                return ContentVerificationResult.Create(
                    false,
                    [Diagnostic(ContentDiagnosticCode.CapabilityRejected)]);
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
        context.ThrowIfUnavailable();
        if (!VersionsAreCompatible(context))
        {
            return new ReadOnlySet<ContentRelativePath>([]);
        }

        var allowed = new List<ContentRelativePath> { OptionsPath };
        if (HasSupportedFancyMenuPair(context))
        {
            allowed.Add(FancyMenuScaleMarkerPath);
        }

        return new ReadOnlySet<ContentRelativePath>(allowed);
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
        var allowed = new List<ContentRelativePath> { OptionsPath };
        if (storedCandidatePaths.Contains(FancyMenuScaleMarkerPath) &&
            context.TargetModVersions.TryGetValue("fancymenu", out var targetFancyMenu) &&
            !context.UnsupportedModIds.Contains("fancymenu") &&
            ModDataCompatibilityPolicy.IsSupportedTargetModVersion(
                "fancymenu",
                targetFancyMenu))
        {
            allowed.Add(FancyMenuScaleMarkerPath);
        }

        return new ReadOnlySet<ContentRelativePath>(allowed);
    }

    private bool TryPrepare(
        ContentProbeContext context,
        CancellationToken cancellationToken,
        out Pcl2ContentOptionsSelectionSession? prepared,
        out IReadOnlyList<ContentDiagnostic> diagnostics,
        out ContentDiagnosticCode rejection)
    {
        prepared = null;
        var collected = new List<ContentDiagnostic>();
        diagnostics = collected.AsReadOnly();
        rejection = ContentDiagnosticCode.CapabilityRejected;
        if (context is null)
        {
            collected.Add(Diagnostic(rejection));
            return false;
        }

        if (!VersionsAreCompatible(context))
        {
            rejection = ContentDiagnosticCode.UnsupportedMinecraftVersion;
            collected.Add(Diagnostic(rejection));
            return false;
        }

        try
        {
            context.ThrowIfUnavailable();
            var source = context.Source.Read(
                OptionsPath,
                new ContentReadLimits(Pcl2OptionsMigrationPreviewer.MaximumOptionsFileBytes),
                cancellationToken);
            var target = context.Target.Read(
                OptionsPath,
                new ContentReadLimits(Pcl2OptionsMigrationPreviewer.MaximumOptionsFileBytes),
                cancellationToken);
            if (!source.Exists)
            {
                rejection = ContentDiagnosticCode.MissingSourceData;
                collected.Add(Diagnostic(rejection));
                return false;
            }

            if (!target.Exists)
            {
                collected.Add(ContentDiagnostic.Create(
                    ContentDiagnosticCode.MissingTargetData,
                    ContentDiagnosticSeverity.Information,
                    AdapterId));
            }

            prepared = previewer.PrepareSelection(
                context,
                source,
                target,
                cancellationToken);
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
        catch (OptionsSchemaVersionException)
        {
            rejection = ContentDiagnosticCode.UnsupportedSchema;
        }
        catch (Exception exception) when (
            exception is CapabilityBoundaryException or ArgumentException or InvalidOperationException)
        {
            rejection = ContentDiagnosticCode.CapabilityRejected;
        }

        collected.Add(Diagnostic(rejection));
        return false;
    }

    private static bool TryReadCurrentSnapshots(
        ContentProbeContext context,
        CancellationToken cancellationToken,
        out ContentFileSnapshot? source,
        out ContentFileSnapshot? target,
        out IReadOnlyList<ContentDiagnostic> diagnostics)
    {
        source = null;
        target = null;
        var collected = new List<ContentDiagnostic>();
        diagnostics = collected.AsReadOnly();
        if (context is null)
        {
            collected.Add(Diagnostic(ContentDiagnosticCode.CapabilityRejected));
            return false;
        }

        if (!VersionsAreCompatible(context))
        {
            collected.Add(Diagnostic(ContentDiagnosticCode.UnsupportedMinecraftVersion));
            return false;
        }

        try
        {
            context.ThrowIfUnavailable();
            source = context.Source.Read(
                OptionsPath,
                new ContentReadLimits(Pcl2OptionsMigrationPreviewer.MaximumOptionsFileBytes),
                cancellationToken);
            target = context.Target.Read(
                OptionsPath,
                new ContentReadLimits(Pcl2OptionsMigrationPreviewer.MaximumOptionsFileBytes),
                cancellationToken);
            if (!source.Exists)
            {
                collected.Add(Diagnostic(ContentDiagnosticCode.MissingSourceData));
                return false;
            }

            if (!target.Exists)
            {
                collected.Add(ContentDiagnostic.Create(
                    ContentDiagnosticCode.MissingTargetData,
                    ContentDiagnosticSeverity.Information,
                    AdapterId));
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CapabilityLimitExceededException)
        {
            collected.Add(Diagnostic(ContentDiagnosticCode.LimitExceeded));
        }
        catch (ObjectDisposedException)
        {
            collected.Add(Diagnostic(ContentDiagnosticCode.StaleContext));
        }
        catch (Exception exception) when (
            exception is CapabilityBoundaryException or ArgumentException or InvalidOperationException)
        {
            collected.Add(Diagnostic(ContentDiagnosticCode.CapabilityRejected));
        }

        return false;
    }

    private static ContentCatalog MapCatalog(
        OptionsSelectionCatalog source,
        IReadOnlyList<ContentDiagnostic> diagnostics)
    {
        var items = new List<ContentCatalogItem>();
        foreach (var descriptor in source.SelectableDifferences)
        {
            var id = CreateItemId(descriptor.Key);
            items.Add(ContentCatalogItem.Create(
                id,
                descriptor.DisplayName,
                CategoryLabel(descriptor.Category) + " · " + descriptor.DisplayKey,
                descriptor.TargetValue is null
                    ? PlannedContentDisposition.Add
                    : PlannedContentDisposition.Update,
                isSelectable: true,
                isSelectedByDefault: false,
                ConflictResolution.Skip,
                null));
        }

        foreach (var item in source.RequiredChanges)
        {
            var id = CreateItemId(item.Key);
            items.Add(ContentCatalogItem.Create(
                id,
                "设置格式版本",
                "目标尚未初始化 · 同步时自动补全",
                PlannedContentDisposition.Add,
                isSelectable: false,
                isSelectedByDefault: false,
                ConflictResolution.Skip,
                null));
        }

        foreach (var item in source.ProtectedDifferences)
        {
            var id = CreateItemId(item.Key);
            items.Add(ContentCatalogItem.Create(
                id,
                "受保护设置",
                item.Key,
                PlannedContentDisposition.Protected,
                isSelectable: false,
                isSelectedByDefault: false,
                ConflictResolution.Skip,
                ContentDiagnosticCode.CapabilityRejected));
        }

        foreach (var item in source.TargetOnlyItems)
        {
            var id = CreateItemId(item.Key);
            items.Add(ContentCatalogItem.Create(
                id,
                "仅目标设置",
                item.Key,
                PlannedContentDisposition.Same,
                isSelectable: false,
                isSelectedByDefault: false,
                ConflictResolution.Skip,
                null));
        }

        return ContentCatalog.Create(
            AdapterId,
            items.OrderBy(item => item.Id.TechnicalKey, StringComparer.Ordinal),
            diagnostics);
    }

    private static ContentPlanItem MapPlanItem(
        ContentCatalogItem item,
        bool selected)
    {
        var disposition = item.Disposition switch
        {
            PlannedContentDisposition.Add or PlannedContentDisposition.Update when selected =>
                item.Disposition,
            PlannedContentDisposition.Add or PlannedContentDisposition.Update =>
                PlannedContentDisposition.Unselected,
            _ => item.Disposition,
        };
        var summary = disposition switch
        {
            PlannedContentDisposition.Add => "将新增到目标",
            PlannedContentDisposition.Update => "将采用来源值",
            PlannedContentDisposition.Unselected => "未选择，保留目标",
            PlannedContentDisposition.Protected => "受目标整合包保护",
            _ => "保持目标现状",
        };
        return ContentPlanItem.Create(item.Id, disposition, ConflictResolution.Skip, summary);
    }

    private static bool PrepareFancyMenuScaleMarker(
        ContentProbeContext context,
        CancellationToken cancellationToken,
        out ContentFileSnapshot? source,
        out ContentFileSnapshot? target,
        out ContentDiagnosticCode? rejection)
    {
        source = null;
        target = null;
        rejection = null;
        if (!context.Compatibility.TargetModVersions.ContainsKey("fancymenu"))
        {
            return false;
        }

        if (!HasSupportedFancyMenuPair(context))
        {
            rejection = ContentDiagnosticCode.UnsupportedModVersion;
            return false;
        }

        try
        {
            context.ThrowIfUnavailable();
            target = context.Target.Read(
                FancyMenuScaleMarkerPath,
                new ContentReadLimits(64),
                cancellationToken);
            if (target.Exists)
            {
                return false;
            }

            source = context.Source.Read(
                FancyMenuScaleMarkerPath,
                new ContentReadLimits(64),
                cancellationToken);
            if (!source.Exists ||
                !source.Bytes.CopyBytes().SequenceEqual(FancyMenuScaleMarkerBytes))
            {
                rejection = ContentDiagnosticCode.UnsupportedSchema;
                return false;
            }

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

    private static bool HasSupportedFancyMenuPair(ContentProbeContext context)
    {
        var compatibility = context.Compatibility;
        return !compatibility.DetectedUnsupportedModIds.Contains("fancymenu") &&
               compatibility.SourceModVersions.TryGetValue("fancymenu", out var sourceFancyMenu) &&
               compatibility.TargetModVersions.TryGetValue("fancymenu", out var targetFancyMenu) &&
               ModDataCompatibilityPolicy.AreModVersionsCompatible(
                   "fancymenu",
                   sourceFancyMenu,
                   targetFancyMenu);
    }

    private static bool VersionsAreCompatible(ContentProbeContext context)
    {
        var compatibility = context.Compatibility;
        return compatibility.SourceMinecraftVersion is { Length: > 0 } sourceVersion &&
               string.Equals(sourceVersion, compatibility.TargetMinecraftVersion, StringComparison.Ordinal) &&
               string.Equals(
                   sourceVersion,
                   context.Source.Identity.MinecraftVersion,
                   StringComparison.Ordinal) &&
               string.Equals(
                   compatibility.TargetMinecraftVersion,
                   context.Target.Identity.MinecraftVersion,
                   StringComparison.Ordinal);
    }

    private static ContentAdapterPlan RejectedPlan(ContentDiagnosticCode code) =>
        ContentAdapterPlan.Create(AdapterId, [], [], [Diagnostic(code)]);

    private static ContentDiagnostic Diagnostic(ContentDiagnosticCode code) =>
        ContentDiagnostic.Create(
            code,
            ContentDiagnosticSeverity.Error,
            AdapterId);

    private static ContentItemId CreateItemId(string key)
    {
        if (!ContentItemId.TryCreate(AdapterId, key, out var id))
        {
            throw new ArgumentException("The options key cannot become a bounded content item ID.", nameof(key));
        }

        return id;
    }

    private static string CategoryLabel(OptionSettingCategory category) => category switch
    {
        OptionSettingCategory.LanguageAndInterface => "语言与界面",
        OptionSettingCategory.Controls => "按键与控制",
        OptionSettingCategory.SoundAndDisplay => "声音与显示",
        _ => "其他玩家设置",
    };

    private static ContentRelativePath CreateOptionsPath()
    {
        if (!ContentRelativePath.TryCreate("options.txt", out var path, out _))
        {
            throw new InvalidOperationException("The fixed options.txt path is invalid.");
        }

        return path!;
    }

    private static ContentRelativePath CreatePath(string value)
    {
        if (!ContentRelativePath.TryCreate(value, out var path, out _))
        {
            throw new InvalidOperationException("A fixed vanilla support path is invalid.");
        }

        return path!;
    }
}
