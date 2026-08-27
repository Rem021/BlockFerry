using System.Collections.ObjectModel;

namespace BlockFerry.Core.Transactions;

public enum MigrationExecutionStatus
{
    Succeeded,
    RejectedStale,
    Blocked,
    CancelledBeforeMutation,
    RolledBack,
    RecoveryRequired,
}

public enum MigrationProgressStage
{
    Revalidating,
    CheckingRunningGames,
    PreparingBackup,
    BackingUp,
    Staging,
    Committing,
    Verifying,
    RollingBack,
    CleaningUp,
    Completed,
    Blocked,
}

public sealed record MigrationProgress(
    MigrationProgressStage Stage,
    int CompletedSteps,
    int TotalSteps,
    string Message);

public sealed class MigrationExecutionResult
{
    private MigrationExecutionResult(
        MigrationExecutionStatus status,
        TransactionId? transactionId,
        int committedFileCount,
        IReadOnlyList<string> diagnostics)
    {
        Status = status;
        TransactionId = transactionId;
        CommittedFileCount = committedFileCount;
        Diagnostics = diagnostics;
    }

    public MigrationExecutionStatus Status { get; }

    public TransactionId? TransactionId { get; }

    public int CommittedFileCount { get; }

    public IReadOnlyList<string> Diagnostics { get; }

    public string Message => Diagnostics.Count == 0 ? string.Empty : Diagnostics[0];

    public bool IsSuccess => Status == MigrationExecutionStatus.Succeeded;

    internal static MigrationExecutionResult Create(
        MigrationExecutionStatus status,
        TransactionId? transactionId,
        int committedFileCount,
        params string[] diagnostics) =>
        new(
            status,
            transactionId,
            committedFileCount,
            new ReadOnlyCollection<string>(diagnostics
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Take(32)
                .ToArray()));
}
