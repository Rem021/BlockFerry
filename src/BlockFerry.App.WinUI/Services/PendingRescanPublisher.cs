using BlockFerry.Core.Transactions;

namespace BlockFerry.App.WinUI.Services;

internal sealed class PendingRescanPublisher
{
    private readonly Func<CancellationToken, IReadOnlyList<PendingRecovery>> findPending;
    private readonly Func<MigrationWorkflowState> stateProvider;
    private readonly Action<MigrationWorkflowState> publish;
    private readonly Action<bool> setRecoveryCheckPassed;
    private readonly Func<Exception, bool> isRecoverable;
    private readonly CancellationToken lifetimeToken;

    internal PendingRescanPublisher(
        Func<CancellationToken, IReadOnlyList<PendingRecovery>> findPending,
        Func<MigrationWorkflowState> stateProvider,
        Action<MigrationWorkflowState> publish,
        Action<bool> setRecoveryCheckPassed,
        Func<Exception, bool> isRecoverable,
        CancellationToken lifetimeToken)
    {
        this.findPending = findPending ?? throw new ArgumentNullException(nameof(findPending));
        this.stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
        this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
        this.setRecoveryCheckPassed = setRecoveryCheckPassed ??
            throw new ArgumentNullException(nameof(setRecoveryCheckPassed));
        this.isRecoverable = isRecoverable ?? throw new ArgumentNullException(nameof(isRecoverable));
        this.lifetimeToken = lifetimeToken;
    }

    internal Task<bool> PublishRequestBoundAsync(
        MigrationWorkflowState whenNone,
        string pendingStatusText,
        CancellationToken requestToken,
        TransactionId? attentionTransactionId = null,
        MigrationRecoveryStatus? attentionStatus = null) =>
        PublishCoreAsync(
            whenNone,
            pendingStatusText,
            attentionTransactionId,
            attentionStatus,
            requestToken);

    internal async Task<bool> PublishAfterOutcomeAsync(
        MigrationWorkflowState whenNone,
        string pendingStatusText,
        CancellationToken requestToken,
        TransactionId? attentionTransactionId = null,
        MigrationRecoveryStatus? attentionStatus = null)
    {
        var noPending = await PublishCoreAsync(
            whenNone,
            pendingStatusText,
            attentionTransactionId,
            attentionStatus,
            lifetimeToken);
        requestToken.ThrowIfCancellationRequested();
        return noPending;
    }

    private async Task<bool> PublishCoreAsync(
        MigrationWorkflowState whenNone,
        string pendingStatusText,
        TransactionId? attentionTransactionId,
        MigrationRecoveryStatus? attentionStatus,
        CancellationToken scanToken)
    {
        ArgumentNullException.ThrowIfNull(whenNone);
        ArgumentException.ThrowIfNullOrWhiteSpace(pendingStatusText);
        setRecoveryCheckPassed(false);
        publish(stateProvider() with
        {
            Phase = MigrationWorkflowPhase.CheckingRecovery,
            PendingRecovery = null,
            StatusText = "正在重新检查所有未完成的同步…",
            CanExecute = false,
            CanUndo = false,
        });

        IReadOnlyList<PendingRecovery> pending;
        try
        {
            pending = await Task.Run(
                () => findPending(scanToken),
                scanToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (isRecoverable(exception))
        {
            publish(whenNone with
            {
                Phase = MigrationWorkflowPhase.Blocked,
                PendingRecovery = null,
                StatusText = "无法确认上次同步是否完整；为保护实例，暂不开始新的同步。",
                CanExecute = false,
                CanUndo = false,
            });
            return false;
        }

        setRecoveryCheckPassed(true);
        var first = pending.Count == 0 ? null : pending[0];
        if (first is null)
        {
            publish(whenNone with
            {
                Phase = MigrationWorkflowPolicy.ResolvePendingRescanPhase(0, whenNone.Phase),
                PendingRecovery = null,
            });
            return true;
        }

        if (attentionTransactionId == first.TransactionId && attentionStatus is { } status)
        {
            first = first with
            {
                TargetPathAvailable = status == MigrationRecoveryStatus.TargetReselectionRequired
                    ? false
                    : first.TargetPathAvailable,
                AttentionStatus = status,
            };
        }

        var statusText = first.AttentionStatus == MigrationRecoveryStatus.AuthenticationFailed
            ? "上次同步记录无法验证；不会猜测写入，请先导出诊断。"
            : !first.TargetPathAvailable
                ? "上次同步的目标位置未通过当前发现证明；请选择同一个实例的新位置。"
                : pendingStatusText;
        publish(whenNone with
        {
            Phase = MigrationWorkflowPolicy.ResolvePendingRescanPhase(pending.Count, whenNone.Phase),
            PendingRecovery = first,
            StatusText = statusText,
            CanExecute = false,
            CanUndo = false,
        });
        return false;
    }
}
