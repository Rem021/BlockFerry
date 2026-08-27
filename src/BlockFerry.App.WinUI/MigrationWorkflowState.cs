using BlockFerry.Core.Content;
using BlockFerry.Core.Pcl2;
using BlockFerry.Core.Transactions;
using BlockFerry.App.WinUI.Selection;

namespace BlockFerry.App.WinUI;

internal enum MigrationWorkflowPhase
{
    CheckingRecovery,
    RecoveryRequired,
    AwaitingDiscovery,
    Discovering,
    Selecting,
    Reviewing,
    Executing,
    RollingBack,
    Succeeded,
    Blocked,
    Demo,
}

internal sealed record MigrationWorkflowState(
    MigrationWorkflowPhase Phase,
    long Generation,
    MigrationViewState ViewState,
    IReadOnlyList<Pcl2Instance> Instances,
    string? SourceInstanceId,
    string? TargetInstanceId,
    IReadOnlyList<ContentCatalog> Catalogs,
    IReadOnlyList<ContentPlanItem> ReviewItems,
    string StatusText,
    MigrationProgress? Progress,
    PendingRecovery? PendingRecovery,
    TransactionId? CommittedTransactionId,
    MigrationExecutionStatus? LastExecutionStatus,
    int PlannedFileCount,
    int PlannedItemCount,
    bool CanExecute,
    bool CanUndo)
{
    internal ContentCompatibilityDisplayEvidence? Compatibility { get; init; }

    internal bool HasDeferredJeiSync { get; init; }

    internal static MigrationWorkflowState Initial { get; } = new(
        MigrationWorkflowPhase.CheckingRecovery,
        0,
        MigrationViewState.AwaitingDiscovery,
        Array.Empty<Pcl2Instance>(),
        null,
        null,
        Array.Empty<ContentCatalog>(),
        Array.Empty<ContentPlanItem>(),
        "正在检查上次同步是否完整…",
        null,
        null,
        null,
        null,
        0,
        0,
        CanExecute: false,
        CanUndo: false);

    internal bool IsMutationInProgress =>
        Phase is MigrationWorkflowPhase.Executing or MigrationWorkflowPhase.RollingBack;
}
