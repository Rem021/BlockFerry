using BlockFerry.Core.Transactions;

namespace BlockFerry.App.WinUI.Services;

internal readonly record struct UndoWorkflowDisposition(
    MigrationWorkflowPhase Phase,
    bool KeepCommittedTransaction,
    bool CanRetryUndo);

internal static class MigrationWorkflowPolicy
{
    internal static bool CanDiscover(
        bool recoveryCheckPassed,
        MigrationWorkflowPhase phase) =>
        recoveryCheckPassed &&
        phase is not (
            MigrationWorkflowPhase.CheckingRecovery or
            MigrationWorkflowPhase.RecoveryRequired or
            MigrationWorkflowPhase.Discovering or
            MigrationWorkflowPhase.Executing or
            MigrationWorkflowPhase.RollingBack);

    internal static bool CanApplyMutationProgress(
        long currentOperation,
        long callbackOperation,
        MigrationWorkflowPhase phase) =>
        currentOperation == callbackOperation &&
        phase is MigrationWorkflowPhase.Executing or MigrationWorkflowPhase.RollingBack;

    internal static bool CanReturnToSelection(
        MigrationWorkflowPhase phase,
        bool hasCatalogs,
        bool isMutationInProgress) =>
        !isMutationInProgress &&
        (phase == MigrationWorkflowPhase.Reviewing ||
         phase == MigrationWorkflowPhase.Blocked && hasCatalogs);

    internal static bool CanStartAnotherSync(
        MigrationWorkflowPhase phase,
        MigrationExecutionStatus? lastExecutionStatus,
        bool hasDeferredJeiSync,
        bool isMutationInProgress,
        bool hasPair) =>
        phase == MigrationWorkflowPhase.Succeeded &&
        lastExecutionStatus == MigrationExecutionStatus.Succeeded &&
        !hasDeferredJeiSync &&
        !isMutationInProgress &&
        hasPair;

    internal static bool CanRecover(
        MigrationRecoveryStatus? attentionStatus,
        bool targetPathAvailable,
        bool hasVerifiedReselection) =>
        attentionStatus != MigrationRecoveryStatus.AuthenticationFailed &&
        (targetPathAvailable || hasVerifiedReselection);

    internal static MigrationWorkflowPhase ResolvePendingRescanPhase(
        int pendingCount,
        MigrationWorkflowPhase whenNone)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pendingCount);
        return pendingCount == 0
            ? whenNone
            : MigrationWorkflowPhase.RecoveryRequired;
    }

    internal static UndoWorkflowDisposition ResolveUndoResult(
        MigrationRecoveryStatus status) => status switch
        {
            MigrationRecoveryStatus.Recovered => new(
                MigrationWorkflowPhase.Succeeded,
                KeepCommittedTransaction: false,
                CanRetryUndo: false),
            MigrationRecoveryStatus.Blocked => new(
                MigrationWorkflowPhase.Succeeded,
                KeepCommittedTransaction: true,
                CanRetryUndo: true),
            MigrationRecoveryStatus.RecoveryRequired => new(
                MigrationWorkflowPhase.RecoveryRequired,
                KeepCommittedTransaction: false,
                CanRetryUndo: false),
            _ => new(
                MigrationWorkflowPhase.Blocked,
                KeepCommittedTransaction: false,
                CanRetryUndo: false),
        };
}
