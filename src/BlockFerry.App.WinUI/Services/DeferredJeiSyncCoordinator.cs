using BlockFerry.Core.Content;
using BlockFerry.Core.Discovery;
using BlockFerry.Core.Transactions;

namespace BlockFerry.App.WinUI.Services;

internal enum DeferredJeiAttemptStatus
{
    PendingTargetScope,
    CompletedAlready,
    Succeeded,
    Conflict,
    RejectedStale,
    Blocked,
    RecoveryRequired,
}

internal sealed record DeferredJeiAttemptResult(
    DeferredJeiAttemptStatus Status,
    TransactionId? TransactionId = null,
    int CommittedFileCount = 0,
    string? Message = null);

internal sealed class DeferredJeiSyncCoordinator(
    JeiBookmarksAdapter adapter,
    AcceptedMigrationPlanFactory acceptedPlanFactory,
    MigrationTransactionCoordinator transactionCoordinator,
    IDeferredJeiSyncStore store)
{
    private readonly JeiBookmarksAdapter adapter =
        adapter ?? throw new ArgumentNullException(nameof(adapter));
    private readonly AcceptedMigrationPlanFactory acceptedPlanFactory =
        acceptedPlanFactory ?? throw new ArgumentNullException(nameof(acceptedPlanFactory));
    private readonly MigrationTransactionCoordinator transactionCoordinator =
        transactionCoordinator ?? throw new ArgumentNullException(nameof(transactionCoordinator));
    private readonly IDeferredJeiSyncStore store =
        store ?? throw new ArgumentNullException(nameof(store));

    internal IReadOnlyList<DeferredJeiSyncRecord> Load(
        CancellationToken cancellationToken = default) =>
        store.Load(cancellationToken);

    internal DeferredJeiSyncRecord? CreatePendingRecord(
        AcceptedMigrationPlan plan,
        TransactionId transactionId,
        DateTimeOffset createdUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var seeds = plan.ContentPlan.AdapterPlans
            .Where(candidate => string.Equals(candidate.AdapterId, adapter.Id, StringComparison.Ordinal))
            .SelectMany(adapter.GetDeferredSeeds)
            .DistinctBy(seed => seed.SourceRelativePath.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return seeds.Length == 0
            ? null
            : new DeferredJeiSyncRecord(
                plan.SourceInstanceId,
                plan.TargetInstanceId,
                transactionId,
                createdUtc,
                Array.AsReadOnly(seeds));
    }

    internal bool Persist(
        DeferredJeiSyncRecord record,
        CancellationToken cancellationToken = default) =>
        store.Upsert(record, cancellationToken);

    internal bool Remove(
        DeferredJeiSyncRecord record,
        CancellationToken cancellationToken = default) =>
        store.Remove(record.OriginalTransactionId, cancellationToken);

    internal async Task<DeferredJeiAttemptResult> AttemptAsync(
        DeferredJeiSyncRecord record,
        DiscoverySession session,
        string sourceId,
        string targetId,
        ContentAccessLease lease,
        ContentProbeContext context,
        Action? beforeExecution,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(record.SourceInstanceId, sourceId, StringComparison.Ordinal) ||
            !string.Equals(record.TargetInstanceId, targetId, StringComparison.Ordinal) ||
            !lease.IsBoundTo(session, sourceId, targetId) ||
            !context.IsOwnedBy(lease))
        {
            return new DeferredJeiAttemptResult(DeferredJeiAttemptStatus.RejectedStale);
        }

        ContentCatalog catalog;
        try
        {
            catalog = adapter.BuildCatalog(context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return new DeferredJeiAttemptResult(DeferredJeiAttemptStatus.RejectedStale);
        }

        var resolutions = record.Seeds
            .Select(seed => adapter.ResolveDeferred(catalog, seed))
            .ToArray();
        if (resolutions.Any(result => result.Kind == DeferredJeiResolutionKind.Conflict))
        {
            return new DeferredJeiAttemptResult(
                DeferredJeiAttemptStatus.Conflict,
                Message: "目标服务器已有不同的 JEI 收藏；已保留目标并停止自动覆盖。");
        }

        if (resolutions.Any(result => result.Kind == DeferredJeiResolutionKind.Rejected))
        {
            return new DeferredJeiAttemptResult(
                DeferredJeiAttemptStatus.RejectedStale,
                Message: "JEI 来源或实例证据已变化，需要重新检查同步内容。");
        }

        if (resolutions.Any(result => result.Kind == DeferredJeiResolutionKind.PendingTargetScope))
        {
            return new DeferredJeiAttemptResult(DeferredJeiAttemptStatus.PendingTargetScope);
        }

        var readyIds = resolutions
            .Where(result =>
                result.Kind is DeferredJeiResolutionKind.Ready or
                    DeferredJeiResolutionKind.ReadyReplaceEmpty &&
                result.ItemId is not null)
            .Select(result => result.ItemId!.Value)
            .ToArray();
        if (readyIds.Length == 0)
        {
            _ = store.Remove(record.OriginalTransactionId, cancellationToken);
            return new DeferredJeiAttemptResult(DeferredJeiAttemptStatus.CompletedAlready);
        }

        var replaceEmptyIds = resolutions
            .Where(result =>
                result.Kind == DeferredJeiResolutionKind.ReadyReplaceEmpty &&
                result.ItemId is not null)
            .Select(result => result.ItemId!.Value)
            .ToHashSet();
        var selection = ContentSelection.Create(
            readyIds,
            replaceEmptyIds.Select(id =>
                new KeyValuePair<ContentItemId, ConflictResolution>(
                    id,
                    ConflictResolution.UseSource)));
        if (!ContentSelectionValidator.TryValidateExplicit(
                catalog,
                selection,
                out var validated,
                out _) ||
            validated is null)
        {
            return new DeferredJeiAttemptResult(DeferredJeiAttemptStatus.RejectedStale);
        }

        var adapterPlan = adapter.Plan(context, catalog, validated, cancellationToken);
        if (adapterPlan.FileChanges.Count != readyIds.Length ||
            adapter.GetDeferredSeeds(adapterPlan).Count != 0 ||
            !ContentPlanCoordinator.TryCreateMigrationPlan(
                session.Generation,
                sourceId,
                targetId,
                [adapterPlan],
                out var contentPlan,
                out _) ||
            contentPlan is null)
        {
            return new DeferredJeiAttemptResult(DeferredJeiAttemptStatus.RejectedStale);
        }

        var accepted = acceptedPlanFactory.Create(
            session,
            sourceId,
            targetId,
            lease,
            context,
            contentPlan,
            cancellationToken);
        if (!accepted.IsAccepted || accepted.Plan is null)
        {
            return new DeferredJeiAttemptResult(DeferredJeiAttemptStatus.RejectedStale);
        }

        beforeExecution?.Invoke();
        var execution = await transactionCoordinator.ExecuteAsync(
            accepted.Plan,
            session,
            sourceId,
            targetId,
            lease,
            context,
            progress,
            cancellationToken);
        if (execution.IsSuccess && execution.TransactionId is { } committed)
        {
            _ = store.Remove(record.OriginalTransactionId, cancellationToken);
            return new DeferredJeiAttemptResult(
                DeferredJeiAttemptStatus.Succeeded,
                committed,
                execution.CommittedFileCount,
                execution.Message);
        }

        return execution.Status switch
        {
            MigrationExecutionStatus.RecoveryRequired => new DeferredJeiAttemptResult(
                DeferredJeiAttemptStatus.RecoveryRequired,
                execution.TransactionId,
                Message: execution.Message),
            MigrationExecutionStatus.Blocked or MigrationExecutionStatus.CancelledBeforeMutation =>
                new DeferredJeiAttemptResult(
                    DeferredJeiAttemptStatus.Blocked,
                    execution.TransactionId,
                    Message: execution.Message),
            _ => new DeferredJeiAttemptResult(
                DeferredJeiAttemptStatus.RejectedStale,
                execution.TransactionId,
                Message: execution.Message),
        };
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            InvalidOperationException or
            ObjectDisposedException;
}
