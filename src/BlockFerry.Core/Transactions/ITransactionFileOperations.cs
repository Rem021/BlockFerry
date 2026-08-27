using BlockFerry.Core.Content;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Transactions;

internal interface ITransactionFileOperations
{
    TransactionRootLease OpenTargetRoot(
        MigrationTransactionCoordinator.ExecutionAuthority authority,
        CancellationToken cancellationToken);

    TransactionRootLease OpenRecoveryTargetRoot(
        RecoveryExecutionAuthority authority,
        CancellationToken cancellationToken);

    IReadOnlyList<NormalizedRelativePath> FindMissingParentDirectories(
        TransactionRootLease target,
        NormalizedRelativePath filePath,
        CancellationToken cancellationToken);

    CreatedDirectory? TryOpenDirectory(
        TransactionRootLease target,
        NormalizedRelativePath directory,
        string opaqueObjectId,
        CancellationToken cancellationToken);

    CreatedDirectory CreateDirectory(
        TransactionRootLease target,
        NormalizedRelativePath directory,
        JournalMutationPermit directoryIntent,
        CancellationToken cancellationToken);

    void PersistCreatedDirectory(
        TransactionRootLease target,
        CreatedDirectory created,
        CancellationToken cancellationToken);

    BackupObject BackupExisting(
        TransactionRootLease target,
        PlannedFileChange change,
        JournalMutationPermit backupIntent,
        CancellationToken cancellationToken);

    StagedObject Stage(
        TransactionRootLease target,
        StagedFileMutation mutation,
        JournalMutationPermit stageIntent,
        CancellationToken cancellationToken);

    ReplaceOutcome ReplaceExisting(
        TransactionRootLease target,
        StagedObject staged,
        ExpectedTargetObject expected,
        JournalMutationPermit commitIntent,
        CancellationToken cancellationToken);

    CommittedObject CreateMissing(
        TransactionRootLease target,
        StagedObject staged,
        JournalMutationPermit commitIntent,
        CancellationToken cancellationToken);

    VerifiedObject Reread(
        TransactionRootLease target,
        NormalizedRelativePath path,
        CancellationToken cancellationToken);

    VerifiedObject? TryOpenTemporary(
        TransactionRootLease target,
        NormalizedRelativePath finalPath,
        TransactionId transactionId,
        string opaqueObjectId,
        string suffix,
        CancellationToken cancellationToken);

    void RestoreDisplaced(
        TransactionRootLease target,
        DisplacedObject displaced,
        JournalMutationPermit rollbackIntent,
        CancellationToken cancellationToken);

    void DeleteCreatedFile(
        TransactionRootLease target,
        CommittedObject created,
        JournalMutationPermit rollbackIntent,
        CancellationToken cancellationToken);

    void RemoveCreatedDirectory(
        TransactionRootLease target,
        CreatedDirectory created,
        JournalMutationPermit rollbackIntent,
        CancellationToken cancellationToken);

    void DeleteStagedOrDisplaced(
        TransactionRootLease target,
        VerifiedTransactionObject temporary,
        JournalMutationPermit cleanupIntent,
        CancellationToken cancellationToken);

    void CleanupDisplacedAfterCommit(
        TransactionRootLease target,
        DisplacedObject displaced,
        MigrationTransactionCoordinator.PostCommitCleanupAuthority cleanupAuthority,
        CancellationToken cancellationToken);

    void RestoreBackup(
        TransactionRootLease target,
        NormalizedRelativePath path,
        BackupPayload backup,
        VerifiedObject current,
        JournalMutationPermit rollbackIntent,
        CancellationToken cancellationToken);

    VerifiedObject RestoreMissingBackup(
        TransactionRootLease target,
        NormalizedRelativePath path,
        BackupPayload backup,
        string opaqueObjectId,
        JournalMutationPermit rollbackIntent,
        CancellationToken cancellationToken);
}
