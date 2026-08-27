namespace BlockFerry.Core.Transactions;

internal interface IFaultInjector
{
    void Hit(MigrationFaultPoint point);
}

internal sealed class NoFaultInjector : IFaultInjector
{
    public void Hit(MigrationFaultPoint point)
    {
    }
}

internal enum MigrationFaultPoint
{
    AuthorityValidated,
    MutexAcquired,
    ProcessGuardStarted,
    TargetOpened,
    InputsReread,
    StorePrepared,
    BackupIntentFlushed,
    BackupVerified,
    DirectoryIntentFlushed,
    DirectoryNamespaceCreated,
    DirectoryCreatedDurableBeforePersistence,
    DirectoryCreated,
    StageIntentFlushed,
    StageVerified,
    CommitIntentFlushed,
    CommitVerified,
    FinalRereadVerified,
    CleanupIntentFlushed,
    CleanupVerified,
    CommittedFlushed,
    RollbackIntentFlushed,
    RollbackActionCompleted,
    RollbackVerified,
    RolledBackFlushed,
    RecoveryRequiredFlushed,
}
