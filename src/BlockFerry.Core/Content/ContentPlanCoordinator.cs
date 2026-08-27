using System.Text;

namespace BlockFerry.Core.Content;

public static class ContentPlanCoordinator
{
    public static bool TryCreateMigrationPlan(
        long discoveryGeneration,
        string sourceInstanceId,
        string targetInstanceId,
        IEnumerable<ContentAdapterPlan> adapterPlans,
        out MigrationContentPlan? plan,
        out ContentDiagnostic? rejection)
    {
        plan = null;
        rejection = null;
        try
        {
            plan = CreateValidated(
                discoveryGeneration,
                sourceInstanceId,
                targetInstanceId,
                adapterPlans);
            return true;
        }
        catch (ContentPlanValidationException exception)
        {
            rejection = ContentDiagnostic.Create(
                exception.Code,
                ContentDiagnosticSeverity.Error,
                exception.AdapterId);
            return false;
        }
        catch (ArgumentException)
        {
            rejection = ContentDiagnostic.Create(
                ContentDiagnosticCode.CapabilityRejected,
                ContentDiagnosticSeverity.Error,
                "content");
            return false;
        }
        catch (OverflowException)
        {
            rejection = ContentDiagnostic.Create(
                ContentDiagnosticCode.LimitExceeded,
                ContentDiagnosticSeverity.Error,
                "content");
            return false;
        }
    }

    public static bool TryBindVerificationRereads(
        ContentStageResult staged,
        IEnumerable<ContentFileSnapshot> rereads,
        out IReadOnlyList<ContentFileSnapshot> boundRereads,
        out ContentDiagnostic? rejection)
    {
        boundRereads = Array.Empty<ContentFileSnapshot>();
        rejection = null;
        var adapterId = staged?.AdapterId ?? "content";
        if (staged is null || rereads is null)
        {
            rejection = Reject(adapterId, ContentDiagnosticCode.CapabilityRejected);
            return false;
        }

        List<ContentFileSnapshot> copy;
        try
        {
            copy = ContentEnumerable.CopyBounded(
                rereads,
                ContentContractLimits.MaximumFileChanges,
                nameof(rereads));
        }
        catch (ArgumentException)
        {
            rejection = Reject(adapterId, ContentDiagnosticCode.LimitExceeded);
            return false;
        }

        if (copy.Any(snapshot => snapshot is null))
        {
            rejection = Reject(adapterId, ContentDiagnosticCode.CapabilityRejected);
            return false;
        }

        try
        {
            var expectedByPath = new Dictionary<string, StagedFileMutation>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var mutation in staged.Mutations)
            {
                var pathKey = mutation.Change.RelativePath.Value.Normalize(NormalizationForm.FormC);
                if (!expectedByPath.TryAdd(pathKey, mutation))
                {
                    rejection = Reject(adapterId, ContentDiagnosticCode.PathConflict);
                    return false;
                }
            }

            var rereadByPath = new Dictionary<string, ContentFileSnapshot>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var snapshot in copy)
            {
                var pathKey = snapshot.RelativePath.Value.Normalize(NormalizationForm.FormC);
                if (!rereadByPath.TryAdd(pathKey, snapshot) ||
                    !expectedByPath.TryGetValue(pathKey, out var expected) ||
                    !snapshot.RelativePath.Equals(expected.Change.RelativePath))
                {
                    rejection = Reject(adapterId, ContentDiagnosticCode.PathConflict);
                    return false;
                }
            }

            if (rereadByPath.Count != expectedByPath.Count)
            {
                rejection = Reject(adapterId, ContentDiagnosticCode.CapabilityRejected);
                return false;
            }

            var ordered = new ContentFileSnapshot[staged.Mutations.Count];
            for (var index = 0; index < staged.Mutations.Count; index++)
            {
                var path = staged.Mutations[index].Change.RelativePath;
                var pathKey = path.Value.Normalize(NormalizationForm.FormC);
                if (!rereadByPath.TryGetValue(pathKey, out var snapshot) ||
                    !snapshot.RelativePath.Equals(path))
                {
                    rejection = Reject(adapterId, ContentDiagnosticCode.CapabilityRejected);
                    return false;
                }

                ordered[index] = snapshot;
            }

            boundRereads = Array.AsReadOnly(ordered);
            return true;
        }
        catch (ArgumentException)
        {
            rejection = Reject(adapterId, ContentDiagnosticCode.InvalidRelativePath);
            return false;
        }
    }

    internal static MigrationContentPlan CreateValidated(
        long discoveryGeneration,
        string sourceInstanceId,
        string targetInstanceId,
        IEnumerable<ContentAdapterPlan> adapterPlans)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(discoveryGeneration);

        ContentValueValidation.RequireTechnicalId(sourceInstanceId, nameof(sourceInstanceId));
        ContentValueValidation.RequireTechnicalId(targetInstanceId, nameof(targetInstanceId));
        if (string.Equals(sourceInstanceId, targetInstanceId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Source and target instance IDs must differ.", nameof(targetInstanceId));
        }

        var copy = ContentEnumerable.CopyBounded(
            adapterPlans,
            ContentContractLimits.MaximumAdapters,
            nameof(adapterPlans));
        if (copy.Any(adapter => adapter is null))
        {
            throw new ArgumentException("Adapter plans cannot contain null.", nameof(adapterPlans));
        }

        copy.Sort((left, right) => StringComparer.Ordinal.Compare(left.AdapterId, right.AdapterId));
        if (copy.Select(adapter => adapter.AdapterId).Distinct(StringComparer.Ordinal).Count() != copy.Count)
        {
            throw new ContentPlanValidationException("content", ContentDiagnosticCode.PathConflict);
        }

        var items = new List<ContentPlanItem>();
        var changes = new List<PlannedFileChange>();
        var diagnostics = new List<ContentDiagnostic>();
        var itemIds = new HashSet<ContentItemId>();
        var pathKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in copy)
        {
            AddBounded(items, adapter.Items, ContentContractLimits.MaximumCatalogItems, adapter.AdapterId);
            AddBounded(changes, adapter.FileChanges, ContentContractLimits.MaximumFileChanges, adapter.AdapterId);
            AddBounded(diagnostics, adapter.Diagnostics, ContentContractLimits.MaximumDiagnostics, adapter.AdapterId);
            foreach (var item in adapter.Items)
            {
                if (!itemIds.Add(item.Id))
                {
                    throw new ContentPlanValidationException(adapter.AdapterId, ContentDiagnosticCode.PathConflict);
                }
            }

            foreach (var change in adapter.FileChanges)
            {
                if (!change.Items.Any(IsActionable))
                {
                    throw new ContentPlanValidationException(
                        adapter.AdapterId,
                        ContentDiagnosticCode.CapabilityRejected);
                }

                var collisionKey = change.RelativePath.Value.Normalize(NormalizationForm.FormC);
                if (!pathKeys.Add(collisionKey))
                {
                    throw new ContentPlanValidationException(adapter.AdapterId, ContentDiagnosticCode.PathConflict);
                }
            }
        }

        return new MigrationContentPlan(
            discoveryGeneration,
            sourceInstanceId,
            targetInstanceId,
            Array.AsReadOnly(copy.ToArray()),
            Array.AsReadOnly(items.ToArray()),
            Array.AsReadOnly(changes.ToArray()),
            Array.AsReadOnly(diagnostics.ToArray()));
    }

    private static bool IsActionable(ContentPlanItem item) =>
        item.Disposition is PlannedContentDisposition.Add or PlannedContentDisposition.Update ||
        item.Disposition == PlannedContentDisposition.Conflict &&
        item.Resolution == ConflictResolution.UseSource;

    private static ContentDiagnostic Reject(string adapterId, ContentDiagnosticCode code) =>
        ContentDiagnostic.Create(code, ContentDiagnosticSeverity.Error, adapterId);

    private static void AddBounded<T>(
        List<T> target,
        IReadOnlyList<T> source,
        int maximum,
        string adapterId)
    {
        if (source.Count > maximum - target.Count)
        {
            throw new ContentPlanValidationException(adapterId, ContentDiagnosticCode.LimitExceeded);
        }

        target.AddRange(source);
    }

    private sealed class ContentPlanValidationException(
        string adapterId,
        ContentDiagnosticCode code) : Exception
    {
        internal string AdapterId { get; } = adapterId;

        internal ContentDiagnosticCode Code { get; } = code;
    }
}
