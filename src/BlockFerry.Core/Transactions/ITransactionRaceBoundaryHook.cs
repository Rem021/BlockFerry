namespace BlockFerry.Core.Transactions;

internal enum TransactionRaceBoundary
{
    DirectoryNamespaceCreated,
    RestoreBackupAfterComparison,
    RestoreDisplacedAfterComparison,
    DeleteCreatedAfterComparison,
    RestoreDisplacedBeforeMetadataApplication,
    RestoreBackupBeforeMetadataApplication,
    RestoreDisplacedCaptureBeforeDelete,
    RestoreBackupCaptureBeforeDelete,
    CompensationCaptureBeforeDelete,
    RecoveryStageReady,
    RecoveryStageBeforeDelete,
    AuthenticatedDeleteAfterComparison,
    UndoEligibilityPathRetained,
    NormalReplaceBeforeMetadataAuthentication,
}

internal interface ITransactionRaceBoundaryHook
{
    void Hit(TransactionRaceBoundary boundary, string finalPath);
}

internal sealed class NullTransactionRaceBoundaryHook : ITransactionRaceBoundaryHook
{
    internal static NullTransactionRaceBoundaryHook Instance { get; } = new();

    private NullTransactionRaceBoundaryHook()
    {
    }

    public void Hit(TransactionRaceBoundary boundary, string finalPath)
    {
    }
}
