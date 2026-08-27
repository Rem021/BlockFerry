using BlockFerry.Core.Transactions;

namespace BlockFerry.App.WinUI.Services;

internal sealed class UndoEligibilityRefreshGate(
    Func<TransactionId, CancellationToken, Task<bool>> query)
{
    private readonly Func<TransactionId, CancellationToken, Task<bool>> query =
        query ?? throw new ArgumentNullException(nameof(query));

    internal async Task<bool> EvaluateAsync(
        MigrationWorkflowState snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Phase != MigrationWorkflowPhase.Succeeded ||
            snapshot.IsMutationInProgress ||
            snapshot.CommittedTransactionId is not { } transactionId ||
            snapshot.LastExecutionStatus is not (MigrationExecutionStatus.Succeeded or
                MigrationExecutionStatus.Blocked))
        {
            return false;
        }

        try
        {
            return await query(transactionId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
