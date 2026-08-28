using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using BlockFerry.Core.Content;
using BlockFerry.Core.Discovery;
using BlockFerry.Core.Mods;
using BlockFerry.Core.Options;
using BlockFerry.Core.Pcl2;
using BlockFerry.Core.Processes;
using BlockFerry.Core.System;
using BlockFerry.Core.Transactions;
using BlockFerry.TestSupport;
using Microsoft.Win32.SafeHandles;

var requestedCase = ReadCase(args);
if (requestedCase is not ("preflight" or "journal" or "file-ops" or "coordinator" or "recovery" or "all"))
{
    throw new InvalidOperationException($"Unknown or not-yet-implemented transaction case: {requestedCase}");
}

if (requestedCase is "preflight" or "all")
{
    WriteAllowlistRejectsEveryUnsafeShape();
    PublicPairCannotAuthorizeAcceptedPlan();
    AcceptanceRequiresOriginalSessionIdsLeaseAndContext();
    AcceptanceRevalidatesBeforeStaging();
    AcceptanceRegeneratesAllowlistFromLiveContext();
    MinecraftCommandLineEvidenceIsBoundedAndRedacted();
    ProcessGuardDetectsLateMinecraft();
    PhysicalTargetMutexIsExclusive();
    TargetRootAccessIsLeastPrivilege();
    Console.WriteLine("PASS: preflight");
}

if (requestedCase is "journal" or "all")
{
    BootstrapFailureTouchesNoTarget();
    CrossTransactionReplayFails();
    EveryJournalTruncationFailsClosed();
    IntentPermitRequiresDurableAppend();
    CurrentUserProtectedStoreRoundTrips();
    LegacyOwnedAppRootIsHardenedWithoutChangingTheme();
    StoredPlanRejectsNormalizedCollisions();
    JournalStructuralTamperingFailsClosed();
    Console.WriteLine("PASS: journal");
}

if (requestedCase is "file-ops" or "all")
{
    PublicPairCannotOpenTargetRoot();
    ExistingTargetRoundTripsThroughBackupReplaceAndRollback();
    MissingTargetCreatesAndRollsBackWithoutOverwrite();
    ProvisionalDirectoryHandlesCloseBothCrashGaps();
    ParentDirectoriesCreateAndCleanUpInReverse();
    UnsupportedTargetMetadataFailsBeforeMutation();
    Console.WriteLine("PASS: file-ops");
}

if (requestedCase is "coordinator" or "all")
{
    CoordinatorPostCommitCleanupBoundaries();
    VanillaFancyMenuMarkerTransactionCommitsAndRecovers();
    AppearanceAdapterCommitsSemanticUpdate();
    AppearanceAdapterSeedsMissingTargetAndUndoRemovesIt();
    JeiMappedSourcePathCommitsToTargetScope();
    JeiAdapterSeedsMissingTargetScopeAndUndoRemovesIt();
    CoordinatorFaultRollsBackExactTarget();
    NormalReplaceRacesFailBeforeMetadataMutation();
    ImmediateRestoreDisplacedPreservesRacedTargets();
    ImmediateRestoreDisplacedPreservesRacedCaptureBeforeDelete();
    ImmediateRestoreDisplacedPreservesPreMetadataRace();
    ImmediateRestoreDisplacedPreservesMetadataOnlyPreApplicationRace();
    CompensationDeletePreservesRacedCapture();
    ImmediateDeleteCreatedPreservesRacedTargets();
    DeleteCreatedExclusiveBoundaryBlocksWriter();
    DeleteCreatedExclusiveAcquisitionPreservesObject();
    CoordinatorKeepsSuccessAfterDurableCommitMarker();
    CoordinatorRejectsStaleAuthorityBeforeRuntime();
    CoordinatorRegeneratesAllowlistsAtExecution();
    ExecutionFailureMessagesStaySpecific();
    PreNamespaceFailureAndCancellationLeaveNoHiddenPendingTransaction();
    Console.WriteLine("PASS: coordinator");
}

static void CoordinatorPostCommitCleanupBoundaries()
{
    var failures = new List<string>();
    foreach (var (name, action) in new (string Name, Action Action)[]
             {
                 ("ordinary-success", CoordinatorCommitsOnlyAfterFinalVerification),
                 ("injected-cleanup-failure", PostCommitCleanupFailureKeepsVerifiedSuccessAndOriginal),
             })
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            failures.Add($"{name}: {exception.Message}");
        }
    }

    Assert(failures.Count == 0,
        "Post-commit cleanup boundaries failed:\n" + string.Join("\n", failures));
}

static void TargetRootAccessIsLeastPrivilege()
{
    const uint fileAddFile = 0x00000002;
    const uint fileAddSubdirectory = 0x00000004;
    const uint fileDeleteChild = 0x00000040;
    const uint writeDac = 0x00040000;
    const uint writeOwner = 0x00080000;
    var property = typeof(WindowsTransactionFileOperations).GetProperty(
        "TargetRootAccessContract",
        BindingFlags.Static | BindingFlags.NonPublic);
    var value = property?.GetValue(null);
    Assert(value is uint access &&
           (access & fileAddFile) != 0 &&
           (access & fileAddSubdirectory) != 0 &&
           (access & (fileDeleteChild | writeDac | writeOwner)) == 0,
        "The retained target root must request create/traverse rights without unnecessary delete-child or security-owner privileges.");
}

static void ExecutionFailureMessagesStaySpecific()
{
    var method = typeof(MigrationTransactionCoordinator).GetMethod(
        "SafeFailureMessage",
        BindingFlags.Static | BindingFlags.NonPublic) ??
        throw new InvalidOperationException("The safe execution failure presenter was unavailable.");
    var permission = (string?)method.Invoke(null, [new UnauthorizedAccessException()]);
    var mutex = (string?)method.Invoke(null, [new TargetMutexBusyException()]);
    Assert(permission == "目标实例权限不足；请以管理员身份重新打开 BlockFerry。" &&
           mutex == "目标实例正在被另一个 BlockFerry 操作使用。",
        "Permission and target-busy failures must not be presented as content drift.");
}

static void PreNamespaceFailureAndCancellationLeaveNoHiddenPendingTransaction()
{
    var failures = new List<string>();
    foreach (var cancel in new[] { false, true })
    {
        try
        {
            using var fixture = TransactionAccessFixture.Create();
            using var session = fixture.SessionFactory.Create(cancel ? 38 : 37, fixture.Discovery);
            using var lease = fixture.OpenLease(session);
            var context = lease.CreateProbeContext(fixture.CreateCompatibility());
            var adapter = fixture.CreateVanillaAdapter();
            var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
            var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
            using var cancellation = new CancellationTokenSource();
            IFaultInjector fault = cancel
                ? new CancellingFaultInjector(MigrationFaultPoint.StorePrepared, cancellation)
                : new ScriptedFaultInjector(MigrationFaultPoint.StorePrepared);
            var coordinator = CreateFixtureCoordinator(
                fixture,
                adapter,
                runtimeFactory,
                fault,
                out _);
            var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
            var before = File.ReadAllBytes(targetFile);
            var result = coordinator.ExecuteAsync(
                    acceptedPlan,
                    session,
                    fixture.Source.Id,
                    fixture.Target.Id,
                    lease,
                    context,
                    cancellationToken: cancellation.Token)
                .GetAwaiter()
                .GetResult();
            var pending = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _)
                .FindPending();
            using var reopened = runtimeFactory.Open(
                runtimeFactory.Storages.Single().TransactionId,
                CancellationToken.None);
            var journal = reopened.Journal.ReadAndVerify(reopened.TransactionId, CancellationToken.None);
            Assert(pending.Count == 0 &&
                   journal.TerminalKind == TransactionRecordKind.RolledBack &&
                   File.ReadAllBytes(targetFile).SequenceEqual(before) &&
                   result.Status == (cancel
                       ? MigrationExecutionStatus.CancelledBeforeMutation
                       : MigrationExecutionStatus.RejectedStale),
                "A returned pre-namespace failure or cancellation must durably retire its store without touching the target.");
        }
        catch (Exception exception)
        {
            failures.Add($"{(cancel ? "cancel" : "failure")}: {exception.Message}");
        }
    }

    Assert(failures.Count == 0,
        "Pre-namespace store retirement failed:\n" + string.Join("\n", failures));
}

static void CoordinatorKeepsSuccessAfterDurableCommitMarker()
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(35, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ScriptedFaultInjector(MigrationFaultPoint.CommittedFlushed),
        out _);
    var expected = acceptedPlan.AdapterStages[adapter.Id].Mutations.Single().AfterBytes.CopyBytes();
    var observedProgress = new List<MigrationProgress>();
    try
    {
        var result = coordinator.ExecuteAsync(
                acceptedPlan,
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                lease,
                context,
                progress: new InlineProgress<MigrationProgress>(observedProgress.Add))
            .GetAwaiter()
            .GetResult();
        Assert(result.Status == MigrationExecutionStatus.Succeeded &&
               File.ReadAllBytes(Path.Combine(fixture.TargetRootPath, "options.txt")).SequenceEqual(expected),
            "Once authenticated Committed is durable, a later notification failure must not roll back or report recovery-required.");
        AssertTruthfulSuccessfulProgress(
            observedProgress,
            expectedMutationCount: 1,
            "durable-commit-exception");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(expected);
    }
}

static void PostCommitCleanupFailureKeepsVerifiedSuccessAndOriginal()
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(36, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    var hook = new ThrowingTransactionRaceBoundaryHook(
        TransactionRaceBoundary.AuthenticatedDeleteAfterComparison);
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(
        fixture.AuditedCapability,
        hook);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ScriptedFaultInjector(),
        out _);
    var mutation = acceptedPlan.AdapterStages[adapter.Id].Mutations.Single();
    var objectId = MigrationTransactionCoordinator.ComputeOpaqueObjectId(
        "file",
        adapter.Id + "\0" + mutation.Change.RelativePath.Value);
    var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
    var before = File.ReadAllBytes(targetFile);
    var after = mutation.AfterBytes.CopyBytes();
    try
    {
        var result = coordinator.ExecuteAsync(
                acceptedPlan,
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                lease,
                context)
            .GetAwaiter()
            .GetResult();
        var displacedPaths = Directory.GetFiles(
            fixture.TargetRootPath,
            ".bf-*",
            SearchOption.TopDirectoryOnly);
        using var reopened = runtimeFactory.Open(
            runtimeFactory.Storages.Single().TransactionId,
            CancellationToken.None);
        var journal = reopened.Journal.ReadAndVerify(reopened.TransactionId, CancellationToken.None);
        using var backup = new BackupStore(reopened, runtimeFactory.ProtectedData)
            .Read(objectId, CancellationToken.None);
        Assert(result.Status == MigrationExecutionStatus.Succeeded &&
               hook.HitCount == 1 &&
               journal.TerminalKind == TransactionRecordKind.Committed &&
               File.ReadAllBytes(targetFile).SequenceEqual(after) &&
               displacedPaths.Length == 1 &&
               File.ReadAllBytes(displacedPaths[0]).SequenceEqual(before) &&
               backup.Bytes.SequenceEqual(before),
            $"A post-terminal cleanup failure must keep verified success, the target, backup, and opaque displaced original; " +
            $"status={result.Status}; committed={result.CommittedFileCount}; transaction={result.TransactionId is not null}; " +
            $"hook={hook.HitCount}; terminal={journal.TerminalKind}; displaced={displacedPaths.Length}; " +
            $"journal={string.Join(',', journal.Records.Select(record => record.Kind))}; " +
            $"diagnostics={string.Join(" | ", result.Diagnostics)}.");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(before);
        CryptographicOperations.ZeroMemory(after);
    }
}

if (requestedCase is "recovery" or "all")
{
    RecoveryHandlesEveryCoordinatorCrashBoundary();
    RecoveryHandlesBothDirectoryCrashBoundaries();
    RecoveryResolvesEveryRollbackCrashBoundary();
    RecoveryHandlesMissingTargetsWithoutOverwritingUsers();
    RecoveryIgnoresLastAccessOnlyChanges();
    RecoveryRemovesOnlyIdentityMatchingCreatedDirectories();
    PendingDirectoryIntentRejectsForeignDirectory();
    RecoveryAuthorizationRejectsBeforeMutation();
    RecoveryRollsBackHardCrashAndIsIdempotent();
    RecoveryRestoreBackupPreservesRacedTargets();
    RecoveryRestoreBackupPreservesRacedCaptureBeforeDelete();
    RecoveryRestoreBackupPreservesPreMetadataRace();
    RecoveryRestoreBackupPreservesMetadataOnlyPreApplicationRace();
    RecoveryStageCleanupPreservesRacedStage();
    RecoveryRequiredMarkerRemainsRetryable();
    AuthenticatedUndoRestoresOriginalAndRejectsChangedState();
    AuthenticatedUndoEligibilityIsReadOnlyAndFresh();
    MultiFileUndoEligibilityRetainsOneFreshAfterState();
    RecoveryRequiresTheOriginalPhysicalTargetOrCorrectReselection();
    RecoveryAndUndoDetectMetadataOnlyChanges();
    RecoveryIsBlockedByMutexAndLateJava();
    DiagnosticExportIsBoundedAndRedacted();
    UndoCrashContinuesToVerifiedRecovery();
    MultiFileUndoCrashResumesEveryPath();
    RecoveryRejectsTamperedAuthenticationWithoutWriting();
    Console.WriteLine("PASS: recovery");
}

static void RecoveryResolvesEveryRollbackCrashBoundary()
{
    foreach (var boundary in new[]
             {
                 MigrationFaultPoint.RollbackIntentFlushed,
                 MigrationFaultPoint.RollbackActionCompleted,
                 MigrationFaultPoint.RollbackVerified,
             })
    {
        using var fixture = TransactionAccessFixture.Create();
        using var session = fixture.SessionFactory.Create(300 + (int)boundary, fixture.Discovery);
        using var lease = fixture.OpenLease(session);
        var context = lease.CreateProbeContext(fixture.CreateCompatibility());
        var adapter = fixture.CreateVanillaAdapter();
        var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
        var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
        var coordinator = CreateFixtureCoordinator(
            fixture,
            adapter,
            runtimeFactory,
            new RollbackCrashFaultInjector(boundary),
            out _);
        var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
        var before = File.ReadAllBytes(targetFile);
        AssertThrows<SimulatedProcessCrashException>(
            () => coordinator.ExecuteAsync(
                    acceptedPlan,
                    session,
                    fixture.Source.Id,
                    fixture.Target.Id,
                    lease,
                    context)
                .GetAwaiter()
                .GetResult(),
            $"The {boundary} fixture must terminate during automatic rollback.");
        var recovery = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _);
        var result = recovery.RecoverAsync(runtimeFactory.Storages.Single().TransactionId)
            .GetAwaiter()
            .GetResult();
        Assert(result.Status == MigrationRecoveryStatus.Recovered &&
               File.ReadAllBytes(targetFile).SequenceEqual(before),
            $"Recovery must resolve an interrupted {boundary} rollback exactly; actual={result.Status}.");
    }
}

static void RecoveryRemovesOnlyIdentityMatchingCreatedDirectories()
{
    using var fixture = TransactionAccessFixture.Create();
    var nested = NormalizeRequired("config\\jei\\world\\fixture\\bookmarks.ini");
    var adapter = new NestedDirectoryFixtureAdapter(nested.Value);
    var transactionId = new TransactionId(Guid.NewGuid());
    var locator = RecoveryLocator.Create(
        transactionId,
        fixture.Target.Id,
        fixture.TargetRootPath,
        fixture.Sandbox.GetRootProof(fixture.TargetRootPath).PhysicalIdentity);
    var emptySha = Convert.ToHexString(SHA256.HashData([]));
    var plan = StoredMigrationPlan.Create(
        transactionId,
        new string('D', 64),
        [StoredPlanPath.Create(
            adapter.Id,
            nested,
            ConflictResolution.UseSource,
            beforeExists: false,
            emptySha,
            afterExists: true,
            Convert.ToHexString(SHA256.HashData("future"u8))) ]);
    var stores = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    using (var store = stores.Create(locator, plan, CancellationToken.None))
    {
        var backups = new BackupStore(store, stores.ProtectedData);
        var operations = new WindowsTransactionFileOperations(fixture.AuditedCapability, backups);
        using var authority = new RecoveryExecutionAuthority(
            locator,
            new BlockFerry.Core.Content.ReadOnlySet<NormalizedRelativePath>(
                [nested],
                NormalizedRelativePathComparer.Instance));
        using var target = operations.OpenRecoveryTargetRoot(authority, CancellationToken.None);
        foreach (var directory in operations.FindMissingParentDirectories(
                     target,
                     nested,
                     CancellationToken.None))
        {
            var objectId = MigrationTransactionCoordinator.ComputeOpaqueObjectId(
                "directory",
                directory.Value);
            var permit = store.Journal.AppendIntent(
                TransactionIntent.Create(
                    TransactionRecordKind.DirectoryIntent,
                    objectId,
                    directory,
                    emptySha),
                CancellationToken.None);
            using var created = operations.CreateDirectory(
                target,
                directory,
                permit,
                CancellationToken.None);
            store.Journal.AppendVerified(
                permit,
                TransactionVerification.Create(
                    TransactionRecordKind.DirectoryCreated,
                    objectId,
                    directory,
                    MigrationTransactionCoordinator.IdentityDigest(created.Identity)),
                CancellationToken.None);
            operations.PersistCreatedDirectory(target, created, CancellationToken.None);
        }
    }

    Assert(Directory.Exists(Path.Combine(fixture.TargetRootPath, "config", "jei", "world", "fixture")),
        "The directory-only crash fixture must leave nested transaction-created directories.");
    var recovery = CreateFixtureRecovery(
        stores,
        fixture.AuditedCapability,
        out _,
        adapters: new Dictionary<string, IContentAdapter>(StringComparer.Ordinal)
        {
            [adapter.Id] = adapter,
        });
    var result = recovery.RecoverAsync(transactionId).GetAwaiter().GetResult();
    Assert(result.Status == MigrationRecoveryStatus.Recovered &&
           !Directory.Exists(Path.Combine(fixture.TargetRootPath, "config")),
        "Recovery must remove authenticated transaction-created directories in reverse order.");
}

static void PendingDirectoryIntentRejectsForeignDirectory()
{
    using var fixture = TransactionAccessFixture.Create();
    var nested = NormalizeRequired("config\\fixture\\settings.ini");
    var directory = NormalizeRequired("config");
    var adapter = new NestedDirectoryFixtureAdapter(nested.Value);
    var transactionId = new TransactionId(Guid.NewGuid());
    var locator = RecoveryLocator.Create(
        transactionId,
        fixture.Target.Id,
        fixture.TargetRootPath,
        fixture.Sandbox.GetRootProof(fixture.TargetRootPath).PhysicalIdentity);
    var emptySha = Convert.ToHexString(SHA256.HashData([]));
    var plan = StoredMigrationPlan.Create(
        transactionId,
        new string('E', 64),
        [StoredPlanPath.Create(
            adapter.Id,
            nested,
            ConflictResolution.UseSource,
            beforeExists: false,
            emptySha,
            afterExists: true,
            Convert.ToHexString(SHA256.HashData("future"u8))) ]);
    var stores = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    using (var store = stores.Create(locator, plan, CancellationToken.None))
    {
        var objectId = MigrationTransactionCoordinator.ComputeOpaqueObjectId(
            "directory",
            directory.Value);
        _ = store.Journal.AppendIntent(
            TransactionIntent.Create(
                TransactionRecordKind.DirectoryIntent,
                objectId,
                directory,
                emptySha),
            CancellationToken.None);
    }

    var foreignPath = Path.Combine(fixture.TargetRootPath, "config");
    Directory.CreateDirectory(foreignPath);
    var recovery = CreateFixtureRecovery(
        stores,
        fixture.AuditedCapability,
        out _,
        adapters: new Dictionary<string, IContentAdapter>(StringComparer.Ordinal)
        {
            [adapter.Id] = adapter,
        });
    var result = recovery.RecoverAsync(transactionId).GetAwaiter().GetResult();
    using var reopened = stores.Open(transactionId, CancellationToken.None);
    var journal = reopened.Journal.ReadAndVerify(transactionId, CancellationToken.None);
    Assert(result.Status == MigrationRecoveryStatus.CurrentStateChanged &&
           Directory.Exists(foreignPath) &&
           !journal.IsTerminal &&
           journal.Records[^1].Kind == TransactionRecordKind.DirectoryIntent,
        "Pending DirectoryIntent recovery must preserve a foreign directory and remain fail-closed.");
}

static void RecoveryRequiredMarkerRemainsRetryable()
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(45, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ProcessCrashFaultInjector(MigrationFaultPoint.CommitVerified),
        out _);
    var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
    var before = File.ReadAllBytes(targetFile);
    AssertThrows<SimulatedProcessCrashException>(
        () => coordinator.ExecuteAsync(
                acceptedPlan,
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                lease,
                context)
            .GetAwaiter()
            .GetResult(),
        "The retry fixture must leave a recoverable committed object.");
    var storage = runtimeFactory.Storages.Single();
    storage.FailNextAppend = true;
    var recovery = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _);
    var first = recovery.RecoverAsync(storage.TransactionId).GetAwaiter().GetResult();
    Assert(first.Status == MigrationRecoveryStatus.RecoveryRequired,
        "A transient durable-journal failure must keep recovery pending.");
    using (var reopened = runtimeFactory.Open(storage.TransactionId, CancellationToken.None))
    {
        var journal = reopened.Journal.ReadAndVerify(storage.TransactionId, CancellationToken.None);
        Assert(!journal.IsTerminal &&
               journal.Records[^1].Kind == TransactionRecordKind.RecoveryRequired,
            "RecoveryRequired must be an authenticated retryable marker, not a false terminal state.");
    }

    var second = recovery.RecoverAsync(storage.TransactionId).GetAwaiter().GetResult();
    Assert(second.Status == MigrationRecoveryStatus.Recovered &&
           File.ReadAllBytes(targetFile).SequenceEqual(before),
        "A retry after the transient failure must still restore the exact before-state.");
}

static void RecoveryHandlesMissingTargetsWithoutOverwritingUsers()
{
    foreach (var boundary in new[]
             {
                 MigrationFaultPoint.StageVerified,
                 MigrationFaultPoint.CommitIntentFlushed,
                 MigrationFaultPoint.CommitVerified,
             })
    {
        using var fixture = TransactionAccessFixture.Create();
        var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
        File.Delete(targetFile);
        using var session = fixture.SessionFactory.Create(200 + (int)boundary, fixture.Discovery);
        using var lease = fixture.OpenLease(session);
        var context = lease.CreateProbeContext(fixture.CreateCompatibility());
        var adapter = fixture.CreateVanillaAdapter();
        var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
        var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
        var coordinator = CreateFixtureCoordinator(
            fixture,
            adapter,
            runtimeFactory,
            new ProcessCrashFaultInjector(boundary),
            out _);
        AssertThrows<SimulatedProcessCrashException>(
            () => coordinator.ExecuteAsync(
                    acceptedPlan,
                    session,
                    fixture.Source.Id,
                    fixture.Target.Id,
                    lease,
                    context)
                .GetAwaiter()
                .GetResult(),
            $"The missing-target {boundary} fixture must terminate abruptly.");

        var afterDifference = DescribeCommittedAfterDifference(runtimeFactory, fixture);
        var recovery = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _);
        var result = recovery.RecoverAsync(runtimeFactory.Storages.Single().TransactionId)
            .GetAwaiter()
            .GetResult();
        Assert(result.Status == MigrationRecoveryStatus.Recovered && !File.Exists(targetFile),
            $"Recovery after missing-target {boundary} must return the path to missing; " +
            $"actual={result.Status}, exists={File.Exists(targetFile)}, message={result.Message}, " +
            $"after-difference={afterDifference}");
    }

    using (var fixture = TransactionAccessFixture.Create())
    {
        var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
        File.Delete(targetFile);
        using var session = fixture.SessionFactory.Create(230, fixture.Discovery);
        using var lease = fixture.OpenLease(session);
        var context = lease.CreateProbeContext(fixture.CreateCompatibility());
        var adapter = fixture.CreateVanillaAdapter();
        var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
        var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
        var coordinator = CreateFixtureCoordinator(
            fixture,
            adapter,
            runtimeFactory,
            new ProcessCrashFaultInjector(MigrationFaultPoint.CommitIntentFlushed),
            out _);
        AssertThrows<SimulatedProcessCrashException>(
            () => coordinator.ExecuteAsync(
                    acceptedPlan,
                    session,
                    fixture.Source.Id,
                    fixture.Target.Id,
                    lease,
                    context)
                .GetAwaiter()
                .GetResult(),
            "The missing-target race fixture must stop after durable commit intent.");
        var userBytes = "user_created_after_crash:true\n"u8.ToArray();
        File.WriteAllBytes(targetFile, userBytes);
        var recovery = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _);
        var result = recovery.RecoverAsync(runtimeFactory.Storages.Single().TransactionId)
            .GetAwaiter()
            .GetResult();
        Assert(result.Status == MigrationRecoveryStatus.Recovered &&
               File.ReadAllBytes(targetFile).SequenceEqual(userBytes),
            "Recovery must never delete a user-created file when the transaction did not create the final name.");
    }
}

static string DescribeCommittedAfterDifference(
    FixtureMigrationTransactionRuntimeFactory runtimeFactory,
    TransactionAccessFixture fixture)
{
    var transactionId = runtimeFactory.Storages.Single().TransactionId;
    using var store = runtimeFactory.Open(transactionId, CancellationToken.None);
    var planPath = store.Plan.Paths.Single();
    var objectId = MigrationTransactionCoordinator.ComputeOpaqueObjectId(
        "file",
        planPath.AdapterId + "\0" + planPath.RelativePath.Value);
    var backups = new BackupStore(store, runtimeFactory.ProtectedData);
    using var expectedAfter = backups.Read(
        MigrationTransactionCoordinator.AfterObjectId(objectId),
        CancellationToken.None);
    var operations = new WindowsTransactionFileOperations(fixture.AuditedCapability, backups);
    using var authority = new RecoveryExecutionAuthority(
        store.Locator,
        new BlockFerry.Core.Content.ReadOnlySet<NormalizedRelativePath>(
            [planPath.RelativePath],
            NormalizedRelativePathComparer.Instance));
    using var target = operations.OpenRecoveryTargetRoot(authority, CancellationToken.None);
    VerifiedObject current;
    try
    {
        current = operations.Reread(target, planPath.RelativePath, CancellationToken.None);
    }
    catch (FileNotFoundException)
    {
        return "target-missing";
    }

    using (current)
    {
        var actual = current.Metadata;
        var expected = expectedAfter.Metadata;
        return $"identity={actual.Identity == expected.Identity}, " +
               $"length={actual.Length == expected.Length}, " +
               $"content={string.Equals(actual.Sha256, expected.Sha256, StringComparison.Ordinal)}, " +
               $"creation={actual.CreationTimeUtc == expected.CreationTimeUtc}, " +
               $"access={actual.LastAccessTimeUtc == expected.LastAccessTimeUtc}, " +
               $"write={actual.LastWriteTimeUtc == expected.LastWriteTimeUtc}, " +
               $"attributes={actual.Attributes == expected.Attributes}, " +
               $"links={actual.LinkCount == expected.LinkCount}, " +
               $"security={FileMetadataSnapshot.SecurityDescriptorsSemanticallyEqual(actual.SecurityDescriptor, expected.SecurityDescriptor)}, " +
               $"security-detail={FileMetadataSnapshot.DescribeSecurityDescriptorDifference(actual.SecurityDescriptor, expected.SecurityDescriptor)}, " +
               $"streams={actual.StreamNames.SequenceEqual(expected.StreamNames, StringComparer.Ordinal)}";
    }
}

static void RecoveryIgnoresLastAccessOnlyChanges()
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(232, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ProcessCrashFaultInjector(MigrationFaultPoint.CommitVerified),
        out _);
    var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
    var before = File.ReadAllBytes(targetFile);
    AssertThrows<SimulatedProcessCrashException>(
        () => coordinator.ExecuteAsync(
                acceptedPlan,
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                lease,
                context)
            .GetAwaiter()
            .GetResult(),
        "The last-access fixture must leave a recoverable committed object.");

    var changedLastAccess = new DateTime(2037, 4, 5, 6, 7, 8, DateTimeKind.Utc);
    File.SetLastAccessTimeUtc(targetFile, changedLastAccess);
    Assert(File.GetLastAccessTimeUtc(targetFile) == changedLastAccess,
        "The fixture must prove that only the volatile last-access timestamp changed.");

    var recovery = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _);
    var result = recovery.RecoverAsync(runtimeFactory.Storages.Single().TransactionId)
        .GetAwaiter()
        .GetResult();
    Assert(result.Status == MigrationRecoveryStatus.Recovered &&
           File.ReadAllBytes(targetFile).SequenceEqual(before),
        "A read-only last-access timestamp change must not be mistaken for a user content change.");
}

static void RecoveryRequiresTheOriginalPhysicalTargetOrCorrectReselection()
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(240, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ProcessCrashFaultInjector(MigrationFaultPoint.CommitVerified),
        out _);
    var before = File.ReadAllBytes(Path.Combine(fixture.TargetRootPath, "options.txt"));
    AssertThrows<SimulatedProcessCrashException>(
        () => coordinator.ExecuteAsync(
                acceptedPlan,
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                lease,
                context)
            .GetAwaiter()
            .GetResult(),
        "The reselection fixture must leave an unfinished commit.");

    var movedTarget = fixture.Sandbox.AllocateGuidPath();
    fixture.Sandbox.MoveDirectory(fixture.TargetRootPath, movedTarget);
    var unavailable = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _)
        .RecoverAsync(runtimeFactory.Storages.Single().TransactionId)
        .GetAwaiter()
        .GetResult();
    Assert(unavailable.Status == MigrationRecoveryStatus.TargetReselectionRequired,
        "A missing recorded path must request reselection instead of reporting a generic recovery failure.");

    Directory.CreateDirectory(Path.Combine(fixture.TargetRootPath, "PCL"));
    File.WriteAllBytes(
        Path.Combine(fixture.TargetRootPath, "PCL", "Setup.ini"),
        "VersionArgumentIndieV2:true\r\n"u8.ToArray());
    File.WriteAllBytes(
        Path.Combine(fixture.TargetRootPath, "Target.json"),
        "{\"id\":\"target\",\"minecraftVersion\":\"1.21.1\",\"mainClass\":\"net.minecraft.client.main.Main\"}"u8.ToArray());
    File.WriteAllBytes(Path.Combine(fixture.TargetRootPath, "options.txt"), "impostor=true\n"u8.ToArray());
    var sameTextWrongIdentity = CreateFixtureRecovery(
            runtimeFactory,
            fixture.AuditedCapability,
            out _)
        .RecoverAsync(runtimeFactory.Storages.Single().TransactionId)
        .GetAwaiter()
        .GetResult();
    Assert(sameTextWrongIdentity.Status == MigrationRecoveryStatus.TargetReselectionRequired &&
           File.ReadAllBytes(Path.Combine(fixture.TargetRootPath, "options.txt"))
               .SequenceEqual("impostor=true\n"u8.ToArray()),
        "The recorded textual path must not authorize a replacement directory with a different full physical identity.");
    Directory.Delete(fixture.TargetRootPath, recursive: true);

    var proof = fixture.Sandbox.AuthorizeExistingDirectory(movedTarget);
    var movedCapability = new AuditedFileSystemCapability([proof]);
    var movedSnapshot = new VerifiedDirectorySnapshot(
        movedTarget,
        proof.PhysicalIdentity,
        IsLocalVolume: true,
        IsNetworkRedirected: false,
        IsReparseFree: true);
    var movedInstance = fixture.Target with
    {
        InstanceRoot = movedTarget,
        GameRoot = movedTarget,
        SetupPath = Path.Combine(movedTarget, "PCL", "Setup.ini"),
    };
    var choice = new DiscoveredInstanceChoice(movedInstance, movedSnapshot, "recovery-fixture");
    var recovery = CreateFixtureRecovery(runtimeFactory, movedCapability, out _);
    var wrong = recovery.RecoverAsync(
            runtimeFactory.Storages.Single().TransactionId,
            new VerifiedRecoverySelection(
                choice,
                new PhysicalDirectoryIdentity(999, 998, 997)))
        .GetAwaiter()
        .GetResult();
    Assert(wrong.Status == MigrationRecoveryStatus.TargetReselectionRequired,
        "A reselection with the wrong recorded identity must perform zero writes.");
    var movedSameIdentity = recovery.RecoverAsync(
            runtimeFactory.Storages.Single().TransactionId,
            new VerifiedRecoverySelection(choice, proof.PhysicalIdentity))
        .GetAwaiter()
        .GetResult();
    Assert(movedSameIdentity.Status == MigrationRecoveryStatus.TargetReselectionRequired,
        "A same-volume, same-identity folder outside its current exact PCL instance location must not authorize recovery.");

    fixture.Sandbox.MoveDirectory(movedTarget, fixture.TargetRootPath);
    var restored = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _)
        .RecoverAsync(runtimeFactory.Storages.Single().TransactionId)
        .GetAwaiter()
        .GetResult();
    Assert(restored.Status == MigrationRecoveryStatus.Recovered &&
           File.ReadAllBytes(Path.Combine(fixture.TargetRootPath, "options.txt")).SequenceEqual(before),
        "Recovery must succeed after fresh discovery proves the exact recorded PCL instance and full root identity again.");
}

static void RecoveryAndUndoDetectMetadataOnlyChanges()
{
    using (var fixture = TransactionAccessFixture.Create())
    using (var session = fixture.SessionFactory.Create(250, fixture.Discovery))
    using (var lease = fixture.OpenLease(session))
    {
        var context = lease.CreateProbeContext(fixture.CreateCompatibility());
        var adapter = fixture.CreateVanillaAdapter();
        var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
        var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
        var coordinator = CreateFixtureCoordinator(
            fixture,
            adapter,
            runtimeFactory,
            new ScriptedFaultInjector(),
            out _);
        var committed = coordinator.ExecuteAsync(
                acceptedPlan,
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                lease,
                context)
            .GetAwaiter()
            .GetResult();
        var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
        var content = File.ReadAllBytes(targetFile);
        var changedTime = File.GetLastWriteTimeUtc(targetFile).AddMinutes(-3);
        File.SetLastWriteTimeUtc(targetFile, changedTime);
        var recovery = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _);
        var result = recovery.UndoAsync(committed.TransactionId!.Value).GetAwaiter().GetResult();
        Assert(result.Status == MigrationRecoveryStatus.CurrentStateChanged &&
               File.ReadAllBytes(targetFile).SequenceEqual(content) &&
               File.GetLastWriteTimeUtc(targetFile) == changedTime,
            "Metadata-only changes after commit must block undo without overwriting them.");
    }

    using (var fixture = TransactionAccessFixture.Create())
    using (var session = fixture.SessionFactory.Create(251, fixture.Discovery))
    using (var lease = fixture.OpenLease(session))
    {
        var context = lease.CreateProbeContext(fixture.CreateCompatibility());
        var adapter = fixture.CreateVanillaAdapter();
        var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
        var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
        var coordinator = CreateFixtureCoordinator(
            fixture,
            adapter,
            runtimeFactory,
            new ProcessCrashFaultInjector(MigrationFaultPoint.CommitVerified),
            out _);
        AssertThrows<SimulatedProcessCrashException>(
            () => coordinator.ExecuteAsync(
                    acceptedPlan,
                    session,
                    fixture.Source.Id,
                    fixture.Target.Id,
                    lease,
                    context)
                .GetAwaiter()
                .GetResult(),
            "The metadata recovery fixture must stop after verified commit.");
        var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
        var content = File.ReadAllBytes(targetFile);
        var changedTime = File.GetLastWriteTimeUtc(targetFile).AddMinutes(-4);
        File.SetLastWriteTimeUtc(targetFile, changedTime);
        var recovery = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _);
        var result = recovery.RecoverAsync(runtimeFactory.Storages.Single().TransactionId)
            .GetAwaiter()
            .GetResult();
        Assert(result.Status == MigrationRecoveryStatus.CurrentStateChanged &&
               File.ReadAllBytes(targetFile).SequenceEqual(content) &&
               File.GetLastWriteTimeUtc(targetFile) == changedTime,
            "Metadata-only changes after an interrupted commit must block recovery writes.");
    }
}

static void RecoveryIsBlockedByMutexAndLateJava()
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(260, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ProcessCrashFaultInjector(MigrationFaultPoint.CommitVerified),
        out _);
    AssertThrows<SimulatedProcessCrashException>(
        () => coordinator.ExecuteAsync(
                acceptedPlan,
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                lease,
                context)
            .GetAwaiter()
            .GetResult(),
        "The concurrency fixture must leave a recoverable transaction.");
    var transactionId = runtimeFactory.Storages.Single().TransactionId;
    var mutexFactory = new TargetMutexFactory();
    using (var store = runtimeFactory.Open(transactionId, CancellationToken.None))
    using (var authority = new RecoveryExecutionAuthority(
               store.Locator,
               new BlockFerry.Core.Content.ReadOnlySet<NormalizedRelativePath>(
                   store.Plan.Paths.Select(path => path.RelativePath),
                   NormalizedRelativePathComparer.Instance)))
    using (var owner = mutexFactory.Acquire(authority))
    {
        var blocked = CreateFixtureRecovery(
                runtimeFactory,
                fixture.AuditedCapability,
                out _,
                mutexFactory)
            .RecoverAsync(transactionId)
            .GetAwaiter()
            .GetResult();
        Assert(blocked.Status == MigrationRecoveryStatus.Blocked,
            "A second owner must not enter recovery for the same physical target.");
    }

    var targetAfterCrash = File.ReadAllBytes(Path.Combine(fixture.TargetRootPath, "options.txt"));
    var recovery = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out var inventory);
    var lateJava = new InlineProgress<MigrationProgress>(_ =>
        inventory.Set(ProcessInventoryEntry.Unreadable(991, "javaw")));
    var processBlocked = recovery.RecoverAsync(transactionId, progress: lateJava)
        .GetAwaiter()
        .GetResult();
    Assert(processBlocked.Status == MigrationRecoveryStatus.Blocked &&
           File.ReadAllBytes(Path.Combine(fixture.TargetRootPath, "options.txt")).SequenceEqual(targetAfterCrash),
        "A Java process appearing after the initial check must block recovery before its first mutation.");
    inventory.Set();
    Assert(recovery.RecoverAsync(transactionId).GetAwaiter().GetResult().Status ==
           MigrationRecoveryStatus.Recovered,
        "Recovery must remain retryable after the late-process block clears.");

    using var undoFixture = TransactionAccessFixture.Create();
    using var undoSession = undoFixture.SessionFactory.Create(261, undoFixture.Discovery);
    using var undoLease = undoFixture.OpenLease(undoSession);
    var undoContext = undoLease.CreateProbeContext(undoFixture.CreateCompatibility());
    var undoAdapter = undoFixture.CreateVanillaAdapter();
    var undoPlan = AcceptVanillaPlan(
        undoFixture,
        undoSession,
        undoLease,
        undoContext,
        undoAdapter);
    var undoRuntimeFactory = new FixtureMigrationTransactionRuntimeFactory(undoFixture.AuditedCapability);
    var undoCoordinator = CreateFixtureCoordinator(
        undoFixture,
        undoAdapter,
        undoRuntimeFactory,
        new ScriptedFaultInjector(),
        out _);
    var committed = undoCoordinator.ExecuteAsync(
            undoPlan,
            undoSession,
            undoFixture.Source.Id,
            undoFixture.Target.Id,
            undoLease,
            undoContext)
        .GetAwaiter()
        .GetResult();
    var committedBytes = File.ReadAllBytes(Path.Combine(undoFixture.TargetRootPath, "options.txt"));
    var undoMutexFactory = new TargetMutexFactory();
    using (var undoStore = undoRuntimeFactory.Open(committed.TransactionId!.Value, CancellationToken.None))
    using (var undoAuthority = new RecoveryExecutionAuthority(
               undoStore.Locator,
               new BlockFerry.Core.Content.ReadOnlySet<NormalizedRelativePath>(
                   undoStore.Plan.Paths.Select(path => path.RelativePath),
                   NormalizedRelativePathComparer.Instance)))
    using (var undoOwner = undoMutexFactory.Acquire(undoAuthority))
    {
        var mutexBlockedUndo = CreateFixtureRecovery(
                undoRuntimeFactory,
                undoFixture.AuditedCapability,
                out _,
                undoMutexFactory)
            .UndoAsync(committed.TransactionId.Value)
            .GetAwaiter()
            .GetResult();
        Assert(mutexBlockedUndo.Status == MigrationRecoveryStatus.Blocked &&
               undoRuntimeFactory.Storages.Count == 1,
            "A second target mutex owner must block undo before it creates a transaction.");
    }

    var undoRecovery = CreateFixtureRecovery(
        undoRuntimeFactory,
        undoFixture.AuditedCapability,
        out var undoInventory);
    var lateUndoJava = new InlineProgress<MigrationProgress>(_ =>
        undoInventory.Set(ProcessInventoryEntry.Unreadable(992, "java")));
    var undoBlocked = undoRecovery.UndoAsync(
            committed.TransactionId!.Value,
            lateUndoJava)
        .GetAwaiter()
        .GetResult();
    Assert(undoBlocked.Status == MigrationRecoveryStatus.Blocked &&
           undoRuntimeFactory.Storages.Count == 1 &&
           File.ReadAllBytes(Path.Combine(undoFixture.TargetRootPath, "options.txt"))
               .SequenceEqual(committedBytes),
        "A Java process appearing after the undo guard starts must block before an undo transaction is created; " +
        $"actual={undoBlocked.Status}, stores={undoRuntimeFactory.Storages.Count}.");
    undoInventory.Set();
    Assert(undoRecovery.UndoAsync(committed.TransactionId.Value).GetAwaiter().GetResult().Status ==
           MigrationRecoveryStatus.Recovered,
        "Undo must remain retryable after the late-process block clears.");
}

static void DiagnosticExportIsBoundedAndRedacted()
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(270, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ScriptedFaultInjector(),
        out _);
    var committed = coordinator.ExecuteAsync(
            acceptedPlan,
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            lease,
            context)
        .GetAwaiter()
        .GetResult();
    var recovery = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _);
    var diagnostic = recovery.ExportRedactedDiagnostic(committed.TransactionId!.Value);
    var bytes = diagnostic.CopyBytes();
    try
    {
        Assert(bytes.Length <= 64 * 1024,
            "The diagnostic artifact must remain within its fixed in-memory bound.");
        using var json = JsonDocument.Parse(bytes);
        var names = json.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet();
        Assert(names.SetEquals(
                   ["schema", "transactionId", "targetInstanceId", "state", "recordCount", "recordKinds", "plannedPathCount"]) &&
               !Encoding.UTF8.GetString(bytes).Contains(fixture.TargetRootPath, StringComparison.OrdinalIgnoreCase) &&
               !Encoding.UTF8.GetString(bytes).Contains("options.txt", StringComparison.OrdinalIgnoreCase),
            "Diagnostic export must contain only whitelisted aggregate fields and no filesystem paths.");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(bytes);
    }
}

static void UndoCrashContinuesToVerifiedRecovery()
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(280, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ScriptedFaultInjector(),
        out _);
    var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
    var targetBefore = File.ReadAllBytes(targetFile);
    var committed = coordinator.ExecuteAsync(
            acceptedPlan,
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            lease,
            context)
        .GetAwaiter()
        .GetResult();
    var recovery = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _);
    var progressReports = 0;
    var crashProgress = new InlineProgress<MigrationProgress>(_ =>
    {
        if (Interlocked.Increment(ref progressReports) == 2)
        {
            throw new SimulatedProcessCrashException();
        }
    });
    AssertThrows<SimulatedProcessCrashException>(
        () => recovery.UndoAsync(committed.TransactionId!.Value, crashProgress)
            .GetAwaiter()
            .GetResult(),
        "The undo fixture must crash after its first verified restored file.");
    Assert(runtimeFactory.Storages.Count == 2 &&
           File.ReadAllBytes(targetFile).SequenceEqual(targetBefore),
        "The interrupted undo must have restored the exact original file before termination.");
    var undoId = runtimeFactory.Storages[1].TransactionId;
    var resumed = recovery.RecoverAsync(undoId).GetAwaiter().GetResult();
    Assert(resumed.Status == MigrationRecoveryStatus.Recovered &&
           File.ReadAllBytes(targetFile).SequenceEqual(targetBefore),
        "Startup recovery must make an interrupted undo durable and idempotent.");
}

static void MultiFileUndoCrashResumesEveryPath()
{
    using var fixture = TransactionAccessFixture.Create();
    var firstPath = NormalizeRequired("options.txt");
    var secondPath = NormalizeRequired("config\\fixture-second.txt");
    Directory.CreateDirectory(Path.Combine(fixture.TargetRootPath, "config"));
    var beforeByPath = new Dictionary<NormalizedRelativePath, byte[]?>(NormalizedRelativePathComparer.Instance)
    {
        [firstPath] = "first:before\n"u8.ToArray(),
        [secondPath] = null,
    };
    var afterByPath = new Dictionary<NormalizedRelativePath, byte[]>(NormalizedRelativePathComparer.Instance)
    {
        [firstPath] = "first:after\n"u8.ToArray(),
        [secondPath] = "second:after\n"u8.ToArray(),
    };
    foreach (var pair in afterByPath)
    {
        File.WriteAllBytes(
            Path.Combine(fixture.TargetRootPath, pair.Key.Value),
            pair.Value);
    }

    var committedId = new TransactionId(Guid.NewGuid());
    var digest = new string('A', 64);
    var locator = RecoveryLocator.Create(
        committedId,
        fixture.Target.Id,
        fixture.TargetRootPath,
        fixture.Sandbox.GetRootProof(fixture.TargetRootPath).PhysicalIdentity);
    var storedPaths = new[]
    {
        StoredPlanPath.Create(
            "fixture-a",
            firstPath,
            ConflictResolution.UseSource,
            beforeExists: true,
            Convert.ToHexString(SHA256.HashData(beforeByPath[firstPath]!)),
            afterExists: true,
            Convert.ToHexString(SHA256.HashData(afterByPath[firstPath]))),
        StoredPlanPath.Create(
            "fixture-b",
            secondPath,
            ConflictResolution.UseSource,
            beforeExists: false,
            Convert.ToHexString(SHA256.HashData([])),
            afterExists: true,
            Convert.ToHexString(SHA256.HashData(afterByPath[secondPath]))),
    };
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    using (var store = runtimeFactory.Create(
               locator,
               StoredMigrationPlan.Create(committedId, digest, storedPaths),
               CancellationToken.None))
    {
        var backups = new BackupStore(store, runtimeFactory.ProtectedData);
        var operations = new WindowsTransactionFileOperations(fixture.AuditedCapability, backups);
        using var authority = new RecoveryExecutionAuthority(
            locator,
            new BlockFerry.Core.Content.ReadOnlySet<NormalizedRelativePath>(
                [firstPath, secondPath],
                NormalizedRelativePathComparer.Instance));
        using var target = operations.OpenRecoveryTargetRoot(authority, CancellationToken.None);
        foreach (var planPath in store.Plan.Paths)
        {
            var objectId = MigrationTransactionCoordinator.ComputeOpaqueObjectId(
                "file",
                planPath.AdapterId + "\0" + planPath.RelativePath.Value);
            using var current = operations.Reread(target, planPath.RelativePath, CancellationToken.None);
            backups.WriteVerified(
                MigrationTransactionCoordinator.AfterObjectId(objectId),
                afterByPath[planPath.RelativePath],
                current.Metadata,
                CancellationToken.None);
            if (!planPath.BeforeExists)
            {
                continue;
            }

            var beforeBytes = beforeByPath[planPath.RelativePath] ??
                throw new InvalidOperationException("The existing-path fixture had no before bytes.");
            var beforeMetadata = current.Metadata.WithContentIdentity(
                current.Metadata.Identity,
                beforeBytes.Length,
                Convert.ToHexString(SHA256.HashData(beforeBytes)));
            backups.WriteVerified(objectId, beforeBytes, beforeMetadata, CancellationToken.None);
            var backupPermit = store.Journal.AppendIntent(
                TransactionIntent.Create(
                    TransactionRecordKind.BackupIntent,
                    objectId,
                    planPath.RelativePath,
                    planPath.ExpectedBeforeSha256),
                CancellationToken.None);
            backupPermit.Consume(
                committedId,
                TransactionRecordKind.BackupIntent,
                objectId,
                planPath.RelativePath);
            store.Journal.AppendVerified(
                backupPermit,
                TransactionVerification.Create(
                    TransactionRecordKind.BackupVerified,
                    objectId,
                    planPath.RelativePath,
                    planPath.ExpectedBeforeSha256),
                CancellationToken.None);
        }

        foreach (var planPath in store.Plan.Paths)
        {
            var objectId = MigrationTransactionCoordinator.ComputeOpaqueObjectId(
                "file",
                planPath.AdapterId + "\0" + planPath.RelativePath.Value);
            var commitPermit = store.Journal.AppendIntent(
                TransactionIntent.Create(
                    TransactionRecordKind.CommitIntent,
                    objectId,
                    planPath.RelativePath,
                    planPath.ExpectedBeforeSha256),
                CancellationToken.None);
            commitPermit.Consume(
                committedId,
                TransactionRecordKind.CommitIntent,
                objectId,
                planPath.RelativePath);
            store.Journal.AppendVerified(
                commitPermit,
                TransactionVerification.Create(
                    TransactionRecordKind.CommitVerified,
                    objectId,
                    planPath.RelativePath,
                    planPath.ExpectedAfterSha256),
                CancellationToken.None);
        }

        store.Journal.AppendTerminal(
            TransactionRecordKind.Committed,
            digest,
            CancellationToken.None);
    }

    var recoveryAdapters = new Dictionary<string, IContentAdapter>(StringComparer.Ordinal)
    {
        ["fixture-a"] = new NestedDirectoryFixtureAdapter(firstPath.Value, "fixture-a"),
        ["fixture-b"] = new NestedDirectoryFixtureAdapter(secondPath.Value, "fixture-b"),
    };
    var recovery = CreateFixtureRecovery(
        runtimeFactory,
        fixture.AuditedCapability,
        out _,
        adapters: recoveryAdapters);
    var progressReports = 0;
    var crashProgress = new InlineProgress<MigrationProgress>(_ =>
    {
        if (Interlocked.Increment(ref progressReports) == 2)
        {
            throw new SimulatedProcessCrashException();
        }
    });
    AssertThrows<SimulatedProcessCrashException>(
        () => recovery.UndoAsync(committedId, crashProgress).GetAwaiter().GetResult(),
        "The multi-file undo fixture must terminate after exactly one restored path.");
    Assert(runtimeFactory.Storages.Count == 2,
        "An interrupted multi-file undo must retain its authenticated undo transaction.");
    var beforeCount = beforeByPath.Count(pair =>
        pair.Value is null
            ? !File.Exists(Path.Combine(fixture.TargetRootPath, pair.Key.Value))
            : File.Exists(Path.Combine(fixture.TargetRootPath, pair.Key.Value)) &&
              File.ReadAllBytes(Path.Combine(fixture.TargetRootPath, pair.Key.Value)).SequenceEqual(pair.Value));
    Assert(beforeCount == 1,
        "The crash fixture must prove it stopped after one of two files was restored.");

    var undoId = runtimeFactory.Storages[1].TransactionId;
    var resumed = recovery.RecoverAsync(undoId).GetAwaiter().GetResult();
    Assert(resumed.Status == MigrationRecoveryStatus.Recovered &&
           beforeByPath.All(pair =>
               pair.Value is null
                   ? !File.Exists(Path.Combine(fixture.TargetRootPath, pair.Key.Value))
                   : File.Exists(Path.Combine(fixture.TargetRootPath, pair.Key.Value)) &&
                     File.ReadAllBytes(Path.Combine(fixture.TargetRootPath, pair.Key.Value)).SequenceEqual(pair.Value)),
        "Startup recovery must finish every path of an interrupted multi-file undo without a partial terminal state.");
}

static void RecoveryHandlesEveryCoordinatorCrashBoundary()
{
    MigrationFaultPoint[] recoverableBoundaries =
    [
        MigrationFaultPoint.StorePrepared,
        MigrationFaultPoint.TargetOpened,
        MigrationFaultPoint.InputsReread,
        MigrationFaultPoint.BackupIntentFlushed,
        MigrationFaultPoint.BackupVerified,
        MigrationFaultPoint.StageIntentFlushed,
        MigrationFaultPoint.StageVerified,
        MigrationFaultPoint.CommitIntentFlushed,
        MigrationFaultPoint.CommitVerified,
        MigrationFaultPoint.FinalRereadVerified,
    ];

    foreach (var boundary in recoverableBoundaries)
    {
        using var fixture = TransactionAccessFixture.Create();
        using var session = fixture.SessionFactory.Create(100 + (int)boundary, fixture.Discovery);
        using var lease = fixture.OpenLease(session);
        var context = lease.CreateProbeContext(fixture.CreateCompatibility());
        var adapter = fixture.CreateVanillaAdapter();
        var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
        var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
        var coordinator = CreateFixtureCoordinator(
            fixture,
            adapter,
            runtimeFactory,
            new ProcessCrashFaultInjector(boundary),
            out _);
        var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
        var targetBefore = File.ReadAllBytes(targetFile);
        AssertThrows<SimulatedProcessCrashException>(
            () => coordinator.ExecuteAsync(
                    acceptedPlan,
                    session,
                    fixture.Source.Id,
                    fixture.Target.Id,
                    lease,
                    context)
                .GetAwaiter()
                .GetResult(),
            $"The {boundary} fixture must simulate process termination.");
        Assert(runtimeFactory.Storages.Count == 1,
            $"The {boundary} fixture must leave one authenticated store.");

        var recovery = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _);
        var result = recovery.RecoverAsync(runtimeFactory.Storages[0].TransactionId)
            .GetAwaiter()
            .GetResult();
        Assert(result.Status == MigrationRecoveryStatus.Recovered &&
               File.ReadAllBytes(targetFile).SequenceEqual(targetBefore),
            $"Recovery after {boundary} must converge to the exact before-state; actual={result.Status}.");
    }
}

static void RecoveryHandlesBothDirectoryCrashBoundaries()
{
    foreach (var boundary in new[]
             {
                 MigrationFaultPoint.DirectoryNamespaceCreated,
                 MigrationFaultPoint.DirectoryCreatedDurableBeforePersistence,
             })
    {
        using var fixture = TransactionAccessFixture.Create();
        var sourcePath = Path.Combine(
            fixture.SourceRootPath,
            "config",
            "fixture",
            "settings.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        File.WriteAllBytes(sourcePath, "fixture=true\n"u8.ToArray());
        var targetConfigPath = Path.Combine(fixture.TargetRootPath, "config");
        Directory.CreateDirectory(Path.Combine(targetConfigPath, "fixture"));
        using var session = fixture.SessionFactory.Create(380 + (int)boundary, fixture.Discovery);
        using var lease = fixture.OpenLease(session);
        var context = lease.CreateProbeContext(fixture.CreateCompatibility());
        var vanilla = fixture.CreateVanillaAdapter();
        var baseline = AcceptVanillaPlan(fixture, session, lease, context, vanilla);
        var adapter = new NestedDirectoryFixtureAdapter("config\\fixture\\settings.ini");
        var acceptedPlan = CreateNestedAcceptedPlan(
            session,
            lease,
            context,
            baseline,
            adapter);
        var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
        var coordinator = CreateFixtureCoordinator(
            fixture,
            adapter,
            runtimeFactory,
            new DirectoryCrashFaultInjector(boundary, targetConfigPath),
            out _);
        AssertThrows<SimulatedProcessCrashException>(
            () => coordinator.ExecuteAsync(
                    acceptedPlan,
                    session,
                    fixture.Source.Id,
                    fixture.Target.Id,
                    lease,
                    context)
                .GetAwaiter()
                .GetResult(),
            $"The {boundary} directory fixture must simulate process termination.");
        Assert(!Directory.Exists(Path.Combine(fixture.TargetRootPath, "config")),
            $"The {boundary} process-style close must remove the provisional directory.");
        using (var reopened = runtimeFactory.Open(
                   runtimeFactory.Storages.Single().TransactionId,
                   CancellationToken.None))
        {
            var journal = reopened.Journal.ReadAndVerify(reopened.TransactionId, CancellationToken.None);
            var expectedKind = boundary == MigrationFaultPoint.DirectoryNamespaceCreated
                ? TransactionRecordKind.DirectoryIntent
                : TransactionRecordKind.DirectoryCreated;
            Assert(journal.Records[^1].Kind == expectedKind,
                $"The {boundary} crash must retain its exact authenticated journal boundary.");
        }

        var recovery = CreateFixtureRecovery(
            runtimeFactory,
            fixture.AuditedCapability,
            out _,
            adapters: new Dictionary<string, IContentAdapter>(StringComparer.Ordinal)
            {
                [adapter.Id] = adapter,
            });
        var result = recovery.RecoverAsync(runtimeFactory.Storages.Single().TransactionId)
            .GetAwaiter()
            .GetResult();
        Assert(result.Status == MigrationRecoveryStatus.Recovered &&
               !Directory.Exists(Path.Combine(fixture.TargetRootPath, "config")),
            $"Recovery after {boundary} must accept the already-cleaned absent directory; " +
            $"actual={result.Status}, exists={Directory.Exists(Path.Combine(fixture.TargetRootPath, "config"))}.");
    }
}

static void RecoveryRollsBackHardCrashAndIsIdempotent()
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(41, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ProcessCrashFaultInjector(MigrationFaultPoint.CommitVerified),
        out _);
    var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
    var targetBefore = File.ReadAllBytes(targetFile);

    AssertThrows<SimulatedProcessCrashException>(
        () => coordinator.ExecuteAsync(
                acceptedPlan,
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                lease,
                context,
                cancellationToken: CancellationToken.None)
            .GetAwaiter()
            .GetResult(),
        "The crash fixture must escape the in-process rollback handler.");
    Assert(!File.ReadAllBytes(targetFile).SequenceEqual(targetBefore),
        "The hard-crash fixture must leave an actually committed after-state to recover.");

    fixture.MoveSourceRoot();
    var recovery = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _);
    var pending = recovery.FindPending();
    Assert(pending.Count == 1 && pending[0].TargetPathAvailable,
        "Startup recovery must find the authenticated unfinished transaction.");
    var first = recovery.RecoverAsync(pending[0].TransactionId).GetAwaiter().GetResult();
    Assert(first.Status == MigrationRecoveryStatus.Recovered &&
           first.RestoredFileCount == 1 &&
           File.ReadAllBytes(targetFile).SequenceEqual(targetBefore),
        "Recovery must restore the exact before-state after a hard crash even after the original source disappears.");
    var second = recovery.RecoverAsync(pending[0].TransactionId).GetAwaiter().GetResult();
    Assert(second.Status == MigrationRecoveryStatus.AlreadyTerminal &&
           File.ReadAllBytes(targetFile).SequenceEqual(targetBefore),
        "Repeated recovery must be idempotent and keep the verified rollback state.");
}

static void RecoveryRejectsMissingCurrentDiscoveryProofBeforeMutation()
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(39, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ProcessCrashFaultInjector(MigrationFaultPoint.CommitVerified),
        out _);
    var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
    AssertThrows<SimulatedProcessCrashException>(
        () => coordinator.ExecuteAsync(
                acceptedPlan,
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                lease,
                context)
            .GetAwaiter()
            .GetResult(),
        "The current-discovery fixture must leave a committed after-state pending recovery.");
    var after = File.ReadAllBytes(targetFile);
    File.Delete(Path.Combine(fixture.TargetRootPath, "PCL", "Setup.ini"));
    var mutationCount = fixture.AuditedCapability.AuditLog.Count(entry => entry.IsMutation);

    var result = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _)
        .RecoverAsync(runtimeFactory.Storages.Single().TransactionId)
        .GetAwaiter()
        .GetResult();

    Assert(result.Status == MigrationRecoveryStatus.TargetReselectionRequired &&
           File.ReadAllBytes(targetFile).SequenceEqual(after) &&
           fixture.AuditedCapability.AuditLog.Count(entry => entry.IsMutation) == mutationCount,
        "Recovery must rediscover the recorded target and reject before mutation when its live PCL proof disappeared.");
}

static void RecoveryAuthorizationRejectsBeforeMutation()
{
    var failures = new List<string>();
    foreach (var (name, action) in new (string Name, Action Action)[]
             {
                 ("current-discovery", RecoveryRejectsMissingCurrentDiscoveryProofBeforeMutation),
                 ("current-adapter-catalog", RecoveryRejectsRemovedAdapterPathBeforeMutation),
                 ("known-adapter-subset", () => RecoveryRejectsInjectedStoredPathBeforeMutation(
                     "vanilla",
                     "config\\not-options.dat")),
                 ("current-adapter-compatibility", () => RecoveryRejectsInjectedStoredPathBeforeMutation(
                     "esm",
                     "ESM\\soundsMuffled.dat")),
                 ("malformed-protected-path", RecoveryRejectsMalformedProtectedPathBeforeMutation),
             })
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            failures.Add($"{name}: {exception.Message}");
        }
    }

    Assert(failures.Count == 0,
        "Recovery authorization boundaries failed:\n" + string.Join("\n", failures));
}

static void RecoveryRejectsRemovedAdapterPathBeforeMutation()
    => RecoveryRejectsInjectedStoredPathBeforeMutation(
        "removed-adapter",
        "removed-adapter.dat");

static void RecoveryRejectsInjectedStoredPathBeforeMutation(
    string injectedAdapterId,
    string injectedRelativePath)
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(40, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ProcessCrashFaultInjector(MigrationFaultPoint.CommitVerified),
        out _);
    var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
    AssertThrows<SimulatedProcessCrashException>(
        () => coordinator.ExecuteAsync(
                acceptedPlan,
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                lease,
                context)
            .GetAwaiter()
            .GetResult(),
        "The removed-adapter fixture must leave a committed after-state pending recovery.");
    var after = File.ReadAllBytes(targetFile);
    AddStoredPlanPathForTest(
        runtimeFactory,
        StoredPlanPath.Create(
            injectedAdapterId,
            NormalizeRequired(injectedRelativePath),
            ConflictResolution.UseSource));
    var mutationCount = fixture.AuditedCapability.AuditLog.Count(entry => entry.IsMutation);

    var result = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _)
        .RecoverAsync(runtimeFactory.Storages.Single().TransactionId)
        .GetAwaiter()
        .GetResult();

    Assert(result.Status is MigrationRecoveryStatus.AuthenticationFailed or MigrationRecoveryStatus.CurrentStateChanged &&
           File.ReadAllBytes(targetFile).SequenceEqual(after) &&
           fixture.AuditedCapability.AuditLog.Count(entry => entry.IsMutation) == mutationCount,
        $"Injected recovery path '{injectedAdapterId}' must reject before the first target mutation.");
}

static void RecoveryRejectsMalformedProtectedPathBeforeMutation()
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(42, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ProcessCrashFaultInjector(MigrationFaultPoint.CommitVerified),
        out _);
    var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
    AssertThrows<SimulatedProcessCrashException>(
        () => coordinator.ExecuteAsync(
                acceptedPlan,
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                lease,
                context)
            .GetAwaiter()
            .GetResult(),
        "The malformed protected-path fixture must leave a pending committed after-state.");
    var after = File.ReadAllBytes(targetFile);
    ReplaceStoredPlanPathTextForTest(runtimeFactory, "options.txt", "..\\evil.txt");
    var mutationCount = fixture.AuditedCapability.AuditLog.Count(entry => entry.IsMutation);

    var result = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _)
        .RecoverAsync(runtimeFactory.Storages.Single().TransactionId)
        .GetAwaiter()
        .GetResult();
    Assert(result.Status == MigrationRecoveryStatus.AuthenticationFailed &&
           File.ReadAllBytes(targetFile).SequenceEqual(after) &&
           fixture.AuditedCapability.AuditLog.Count(entry => entry.IsMutation) == mutationCount,
        "An authenticated but malformed protected path must fail while decoding the plan and before target mutation.");
}

static void RecoveryRestoreBackupPreservesRacedTargets()
{
    foreach (var mutation in Enum.GetValues<FixtureRaceMutation>())
    {
        using var fixture = TransactionAccessFixture.Create();
        using var session = fixture.SessionFactory.Create(420 + (int)mutation, fixture.Discovery);
        using var lease = fixture.OpenLease(session);
        var context = lease.CreateProbeContext(fixture.CreateCompatibility());
        var adapter = fixture.CreateVanillaAdapter();
        var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
        var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
        var coordinator = CreateFixtureCoordinator(
            fixture,
            adapter,
            runtimeFactory,
            new ProcessCrashFaultInjector(MigrationFaultPoint.CommitVerified),
            out _);
        var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
        AssertThrows<SimulatedProcessCrashException>(
            () => coordinator.ExecuteAsync(
                    acceptedPlan,
                    session,
                    fixture.Source.Id,
                    fixture.Target.Id,
                    lease,
                    context)
                .GetAwaiter()
                .GetResult(),
            "The recovery CAS fixture must leave an authenticated committed after-state.");
        var displacedPath = Directory.GetFiles(
            fixture.TargetRootPath,
            ".bf-*.displaced",
            SearchOption.TopDirectoryOnly).Single();
        File.Delete(displacedPath);
        var raceBytes = Encoding.UTF8.GetBytes($"recovery-race:{mutation}\n");
        var hook = new FixtureTransactionRaceBoundaryHook(
            TransactionRaceBoundary.RestoreBackupAfterComparison,
            mutation,
            raceBytes);
        var recovery = CreateFixtureRecovery(
            runtimeFactory,
            fixture.AuditedCapability,
            out _,
            raceBoundaryHook: hook);

        var result = recovery.RecoverAsync(runtimeFactory.Storages.Single().TransactionId)
            .GetAwaiter()
            .GetResult();

        Assert(result.Status == MigrationRecoveryStatus.RecoveryRequired,
            $"Recovery RestoreBackup must fail recovery-required after {mutation}; actual={result.Status}.");
        Assert(hook.HitCount == 1 && File.ReadAllBytes(targetFile).SequenceEqual(raceBytes),
            $"Recovery RestoreBackup must preserve the exact {mutation} object at the final path.");
        using var reopened = runtimeFactory.Open(runtimeFactory.Storages.Single().TransactionId, CancellationToken.None);
        var journal = reopened.Journal.ReadAndVerify(reopened.TransactionId, CancellationToken.None);
        Assert(!journal.IsTerminal && journal.Records[^1].Kind == TransactionRecordKind.RollbackIntent,
            "A recovery CAS mismatch must keep the authenticated pending rollback intent without an illegal terminal record.");
    }
}

static void RecoveryRestoreBackupPreservesRacedCaptureBeforeDelete()
{
    foreach (var mutation in Enum.GetValues<FixtureRaceMutation>())
    {
        using var fixture = TransactionAccessFixture.Create();
        using var session = fixture.SessionFactory.Create(430 + (int)mutation, fixture.Discovery);
        using var lease = fixture.OpenLease(session);
        var context = lease.CreateProbeContext(fixture.CreateCompatibility());
        var adapter = fixture.CreateVanillaAdapter();
        var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
        var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
        var coordinator = CreateFixtureCoordinator(
            fixture,
            adapter,
            runtimeFactory,
            new ProcessCrashFaultInjector(MigrationFaultPoint.CommitVerified),
            out _);
        AssertThrows<SimulatedProcessCrashException>(
            () => coordinator.ExecuteAsync(
                    acceptedPlan,
                    session,
                    fixture.Source.Id,
                    fixture.Target.Id,
                    lease,
                    context)
                .GetAwaiter()
                .GetResult(),
            "The recovery captured-delete fixture must leave a committed after-state.");
        File.Delete(Directory.GetFiles(
            fixture.TargetRootPath,
            ".bf-*.displaced",
            SearchOption.TopDirectoryOnly).Single());
        var raceBytes = Encoding.UTF8.GetBytes($"recovery-capture-delete-race:{mutation}\n");
        var hook = new FixtureTransactionRaceBoundaryHook(
            TransactionRaceBoundary.RestoreBackupCaptureBeforeDelete,
            mutation,
            raceBytes);
        var recovery = CreateFixtureRecovery(
            runtimeFactory,
            fixture.AuditedCapability,
            out _,
            raceBoundaryHook: hook);

        var result = recovery.RecoverAsync(runtimeFactory.Storages.Single().TransactionId)
            .GetAwaiter()
            .GetResult();

        Assert(result.Status == MigrationRecoveryStatus.RecoveryRequired,
            $"Recovery must remain pending after a {mutation} race on its displaced capture.");
        Assert(hook.AffectedPath is not null &&
               File.Exists(hook.AffectedPath) &&
               File.ReadAllBytes(hook.AffectedPath).SequenceEqual(raceBytes),
            $"Recovery must preserve the exact {mutation} object introduced at its displaced capture.");
        AssertPendingRollbackIntent(runtimeFactory);
    }
}

static void RecoveryRestoreBackupPreservesPreMetadataRace()
{
    foreach (var mutation in Enum.GetValues<FixtureRaceMutation>())
    {
        using var fixture = TransactionAccessFixture.Create();
        using var session = fixture.SessionFactory.Create(440 + (int)mutation, fixture.Discovery);
        using var lease = fixture.OpenLease(session);
        var context = lease.CreateProbeContext(fixture.CreateCompatibility());
        var adapter = fixture.CreateVanillaAdapter();
        var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
        var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
        var coordinator = CreateFixtureCoordinator(
            fixture,
            adapter,
            runtimeFactory,
            new ProcessCrashFaultInjector(MigrationFaultPoint.CommitVerified),
            out _);
        AssertThrows<SimulatedProcessCrashException>(
            () => coordinator.ExecuteAsync(
                    acceptedPlan,
                    session,
                    fixture.Source.Id,
                    fixture.Target.Id,
                    lease,
                    context)
                .GetAwaiter()
                .GetResult(),
            "The recovery pre-metadata fixture must leave a committed after-state.");
        File.Delete(Directory.GetFiles(
            fixture.TargetRootPath,
            ".bf-*.displaced",
            SearchOption.TopDirectoryOnly).Single());
        var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
        var raceBytes = Encoding.UTF8.GetBytes($"recovery-pre-metadata-race:{mutation}\n");
        var hook = new FixtureTransactionRaceBoundaryHook(
            TransactionRaceBoundary.RestoreBackupBeforeMetadataApplication,
            mutation,
            raceBytes);
        var recovery = CreateFixtureRecovery(
            runtimeFactory,
            fixture.AuditedCapability,
            out _,
            raceBoundaryHook: hook);

        var result = recovery.RecoverAsync(runtimeFactory.Storages.Single().TransactionId)
            .GetAwaiter()
            .GetResult();

        Assert(result.Status == MigrationRecoveryStatus.RecoveryRequired &&
               File.ReadAllBytes(targetFile).SequenceEqual(raceBytes) &&
               File.GetLastWriteTimeUtc(targetFile) == hook.AffectedLastWriteTimeUtc,
            $"Recovery must preserve the exact {mutation} object and metadata introduced before metadata restoration.");
        AssertPendingRollbackIntent(runtimeFactory);
    }
}

static void RecoveryRestoreBackupPreservesMetadataOnlyPreApplicationRace()
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(445, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ProcessCrashFaultInjector(MigrationFaultPoint.CommitVerified),
        out _);
    AssertThrows<SimulatedProcessCrashException>(
        () => coordinator.ExecuteAsync(
                acceptedPlan,
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                lease,
                context)
            .GetAwaiter()
            .GetResult(),
        "The recovery metadata-only fixture must leave a committed after-state.");
    File.Delete(Directory.GetFiles(
        fixture.TargetRootPath,
        ".bf-*.displaced",
        SearchOption.TopDirectoryOnly).Single());
    var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
    var hook = new FixtureMetadataOnlyRaceBoundaryHook(
        TransactionRaceBoundary.RestoreBackupBeforeMetadataApplication);
    var recovery = CreateFixtureRecovery(
        runtimeFactory,
        fixture.AuditedCapability,
        out _,
        raceBoundaryHook: hook);

    var result = recovery.RecoverAsync(runtimeFactory.Storages.Single().TransactionId)
        .GetAwaiter()
        .GetResult();

    Assert(result.Status == MigrationRecoveryStatus.RecoveryRequired &&
           hook.HitCount == 1 &&
           File.GetAttributes(targetFile) == hook.ChangedAttributes,
        "Recovery must preserve a metadata-only pre-application change without overwriting it.");
    AssertPendingRollbackIntent(runtimeFactory);
}

static void RecoveryStageCleanupPreservesRacedStage()
{
    foreach (var mutation in Enum.GetValues<FixtureRaceMutation>())
    {
        using var fixture = TransactionAccessFixture.Create();
        using var session = fixture.SessionFactory.Create(450 + (int)mutation, fixture.Discovery);
        using var lease = fixture.OpenLease(session);
        var context = lease.CreateProbeContext(fixture.CreateCompatibility());
        var adapter = fixture.CreateVanillaAdapter();
        var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
        var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
        var coordinator = CreateFixtureCoordinator(
            fixture,
            adapter,
            runtimeFactory,
            new ProcessCrashFaultInjector(MigrationFaultPoint.CommitVerified),
            out _);
        AssertThrows<SimulatedProcessCrashException>(
            () => coordinator.ExecuteAsync(
                    acceptedPlan,
                    session,
                    fixture.Source.Id,
                    fixture.Target.Id,
                    lease,
                    context)
                .GetAwaiter()
                .GetResult(),
            "The recovery stage-cleanup fixture must leave a committed after-state.");
        File.Delete(Directory.GetFiles(
            fixture.TargetRootPath,
            ".bf-*.displaced",
            SearchOption.TopDirectoryOnly).Single());
        var raceBytes = Encoding.UTF8.GetBytes($"recovery-stage-cleanup-race:{mutation}\n");
        using var hook = new ScriptedTransactionRaceBoundaryHook(
            new FixtureRaceStep(
                TransactionRaceBoundary.RecoveryStageReady,
                ThrowAfter: true),
            new FixtureRaceStep(
                TransactionRaceBoundary.RecoveryStageBeforeDelete,
                mutation,
                raceBytes));
        var recovery = CreateFixtureRecovery(
            runtimeFactory,
            fixture.AuditedCapability,
            out _,
            raceBoundaryHook: hook);

        var result = recovery.RecoverAsync(runtimeFactory.Storages.Single().TransactionId)
            .GetAwaiter()
            .GetResult();
        var cleanupStep = hook.Results.Single(result =>
            result.Boundary == TransactionRaceBoundary.RecoveryStageBeforeDelete);

        Assert(result.Status == MigrationRecoveryStatus.RecoveryRequired &&
               cleanupStep.AffectedPath is not null &&
               File.Exists(cleanupStep.AffectedPath) &&
               File.ReadAllBytes(cleanupStep.AffectedPath).SequenceEqual(raceBytes),
            $"Recovery failure cleanup must preserve the exact {mutation} object at the authenticated stage path.");
        AssertPendingRollbackIntent(runtimeFactory);
    }
}

static void AuthenticatedUndoRestoresOriginalAndRejectsChangedState()
{
    using (var fixture = TransactionAccessFixture.Create())
    using (var session = fixture.SessionFactory.Create(42, fixture.Discovery))
    using (var lease = fixture.OpenLease(session))
    {
        var context = lease.CreateProbeContext(fixture.CreateCompatibility());
        var adapter = fixture.CreateVanillaAdapter();
        var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
        var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
        var coordinator = CreateFixtureCoordinator(
            fixture,
            adapter,
            runtimeFactory,
            new ScriptedFaultInjector(),
            out _);
        var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
        var targetBefore = File.ReadAllBytes(targetFile);
        var committed = coordinator.ExecuteAsync(
                acceptedPlan,
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                lease,
                context)
            .GetAwaiter()
            .GetResult();
        Assert(committed.IsSuccess && committed.TransactionId is not null,
            "Undo fixture setup must produce one authenticated committed transaction.");

        var recovery = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _);
        var committedId = committed.TransactionId ??
            throw new InvalidOperationException("The committed fixture transaction had no ID.");
        var undone = recovery.UndoAsync(committedId).GetAwaiter().GetResult();
        Assert(undone.Status == MigrationRecoveryStatus.Recovered &&
               undone.RestoredFileCount == 1 &&
               File.ReadAllBytes(targetFile).SequenceEqual(targetBefore),
            "Authenticated undo must restore the exact pre-migration target bytes.");
    }

    using (var fixture = TransactionAccessFixture.Create())
    using (var session = fixture.SessionFactory.Create(43, fixture.Discovery))
    using (var lease = fixture.OpenLease(session))
    {
        var context = lease.CreateProbeContext(fixture.CreateCompatibility());
        var adapter = fixture.CreateVanillaAdapter();
        var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
        var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
        var coordinator = CreateFixtureCoordinator(
            fixture,
            adapter,
            runtimeFactory,
            new ScriptedFaultInjector(),
            out _);
        var committed = coordinator.ExecuteAsync(
                acceptedPlan,
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                lease,
                context)
            .GetAwaiter()
            .GetResult();
        var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
        File.AppendAllText(targetFile, "future_user_change:true\n", Encoding.UTF8);
        var changed = File.ReadAllBytes(targetFile);
        var recovery = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _);
        var refused = recovery.UndoAsync(committed.TransactionId!.Value).GetAwaiter().GetResult();
        Assert(refused.Status == MigrationRecoveryStatus.CurrentStateChanged &&
               File.ReadAllBytes(targetFile).SequenceEqual(changed),
            "Undo must refuse to overwrite a target changed after BlockFerry committed it.");
    }
}

static void AuthenticatedUndoEligibilityIsReadOnlyAndFresh()
{
    foreach (var (name, mutation, expected) in new (string Name, Action<string>? Mutation, bool Expected)[]
             {
                 ("matching", null, true),
                 ("content-mismatch", path => File.AppendAllText(
                     path,
                     "future_user_change:true\n",
                     Encoding.UTF8), false),
                 ("metadata-mismatch", path => File.SetLastWriteTimeUtc(
                     path,
                     new DateTime(2024, 1, 2, 3, 4, 6, DateTimeKind.Utc)), false),
                 ("identity-mismatch", ReplaceWithSameContent, false),
             })
    {
        RunSingleFileUndoEligibilityCase(name, mutation, expected, denyRead: false);
    }

    RunSingleFileUndoEligibilityCase(
        "read-failure",
        mutation: null,
        expected: false,
        denyRead: true);
}

static void RunSingleFileUndoEligibilityCase(
    string name,
    Action<string>? mutation,
    bool expected,
    bool denyRead)
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(600 + name.Length, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ScriptedFaultInjector(),
        out _);
    var committed = coordinator.ExecuteAsync(
            acceptedPlan,
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            lease,
            context)
        .GetAwaiter()
        .GetResult();
    Assert(committed.IsSuccess && committed.TransactionId is not null,
        $"Undo eligibility/{name}: fixture setup must produce one authenticated committed transaction.");
    var committedId = committed.TransactionId ??
        throw new InvalidOperationException("The authenticated eligibility fixture had no transaction ID.");
    var targetPath = Path.Combine(fixture.TargetRootPath, "options.txt");
    mutation?.Invoke(targetPath);

    var mutexFactory = new TargetMutexFactory();
    var recovery = CreateFixtureRecovery(
        runtimeFactory,
        fixture.AuditedCapability,
        out var inventory,
        mutexFactory);
    inventory.Set(ProcessInventoryEntry.Unreadable(800 + name.Length, "javaw"));
    var inventoryCaptureCount = inventory.CaptureCount;
    var mutationCount = fixture.AuditedCapability.AuditLog.Count(entry => entry.IsMutation);
    var storeCount = runtimeFactory.Storages.Count;
    var runtimeCreateCount = runtimeFactory.CreateCount;
    int journalRecordCount;
    using (var reopened = runtimeFactory.Open(committedId, CancellationToken.None))
    {
        journalRecordCount = reopened.Journal.ReadAndVerify(committedId, CancellationToken.None).Records.Count;
    }

    var mutexName = TargetMutexFactory.ComputeName(
        fixture.Sandbox.GetRootProof(fixture.TargetRootPath).PhysicalIdentity);
    using var heldMutex = new Mutex(initiallyOwned: true, mutexName, out _);
    using var readOnlyRootBlocker = File.OpenHandle(
        fixture.TargetRootPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        (FileOptions)0x02000000);
    SafeFileHandle? readBlocker = null;
    SafeFileHandle? writeAuthorityBlocker = null;
    try
    {
        if (denyRead)
        {
            readBlocker = File.OpenHandle(
                targetPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        else
        {
            writeAuthorityBlocker = File.OpenHandle(
                targetPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
        }

        var eligible = recovery.IsUndoEligibleAsync(committedId).GetAwaiter().GetResult();
        int afterJournalRecordCount;
        using (var reopened = runtimeFactory.Open(committedId, CancellationToken.None))
        {
            afterJournalRecordCount = reopened.Journal.ReadAndVerify(committedId, CancellationToken.None).Records.Count;
        }

        Assert(eligible == expected,
            $"Undo eligibility/{name}: the real authenticated query returned {eligible}, expected {expected}.");
        Assert(fixture.AuditedCapability.AuditLog.Count(entry => entry.IsMutation) == mutationCount &&
               runtimeFactory.Storages.Count == storeCount &&
               runtimeFactory.CreateCount == runtimeCreateCount &&
               inventory.CaptureCount == inventoryCaptureCount &&
               afterJournalRecordCount == journalRecordCount,
            $"Undo eligibility/{name}: the real query must create no mutation, transaction, process guard, write authority, or journal permit while a target mutex and read-only root/file blockers are held.");
    }
    finally
    {
        writeAuthorityBlocker?.Dispose();
        readBlocker?.Dispose();
        heldMutex.ReleaseMutex();
    }
}

static void ReplaceWithSameContent(string path)
{
    var bytes = File.ReadAllBytes(path);
    var replacement = path + ".identity-replacement";
    try
    {
        File.WriteAllBytes(replacement, bytes);
        File.Move(replacement, path, overwrite: true);
    }
    finally
    {
        CryptographicOperations.ZeroMemory(bytes);
        if (File.Exists(replacement))
        {
            File.Delete(replacement);
        }
    }
}

static void MultiFileUndoEligibilityRetainsOneFreshAfterState()
{
    using var fixture = TransactionAccessFixture.Create();
    var relativePaths = new[]
    {
        "eligibility\\a.txt",
        "eligibility\\b.txt",
    };
    Directory.CreateDirectory(Path.Combine(fixture.SourceRootPath, "eligibility"));
    Directory.CreateDirectory(Path.Combine(fixture.TargetRootPath, "eligibility"));
    File.WriteAllBytes(
        Path.Combine(fixture.SourceRootPath, relativePaths[0]),
        "eligibility-a-after\n"u8.ToArray());
    File.WriteAllBytes(
        Path.Combine(fixture.SourceRootPath, relativePaths[1]),
        "eligibility-b-after\n"u8.ToArray());

    using var session = fixture.SessionFactory.Create(650, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var baselineAdapter = fixture.CreateVanillaAdapter();
    var baseline = AcceptVanillaPlan(fixture, session, lease, context, baselineAdapter);
    var adapter = new MultiPathEligibilityFixtureAdapter(relativePaths);
    var acceptedPlan = CreateMultiPathAcceptedPlan(
        session,
        lease,
        context,
        baseline,
        adapter);
    var churn = new UndoEligibilityChurnHook(
        fixture.TargetRootPath,
        Path.Combine(fixture.TargetRootPath, relativePaths[0]),
        "mixed-time-write\n"u8.ToArray());
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(
        fixture.AuditedCapability,
        churn);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ScriptedFaultInjector(),
        out _);
    var committed = coordinator.ExecuteAsync(
            acceptedPlan,
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            lease,
            context)
        .GetAwaiter()
        .GetResult();
    Assert(committed.IsSuccess && committed.TransactionId is not null,
        "Undo eligibility/two-file churn: fixture setup must commit both generated paths.");
    var committedId = committed.TransactionId ??
        throw new InvalidOperationException("The two-file eligibility fixture had no transaction ID.");

    var adapters = new Dictionary<string, IContentAdapter>(StringComparer.Ordinal)
    {
        [adapter.Id] = adapter,
    };
    var recovery = CreateFixtureRecovery(
        runtimeFactory,
        fixture.AuditedCapability,
        out _,
        raceBoundaryHook: churn,
        adapters: adapters);
    var eligible = recovery.IsUndoEligibleAsync(committedId).GetAwaiter().GetResult();
    Assert(eligible &&
           churn.EligibilityRetainedHitCount == 2 &&
           churn.MutationBlocked &&
           churn.DeletionBlocked &&
           churn.RootDeletionBlocked &&
           !churn.MutationSucceeded &&
           !churn.DeletionSucceeded &&
           !churn.RootDeletionSucceeded,
        "Undo eligibility/two-file churn: the retained root and first verified object must exclude writers and deleters while the second path is checked, or the query must fail closed.");

    using var afterProof = File.OpenHandle(
        churn.FirstPath,
        FileMode.Open,
        FileAccess.Write,
        FileShare.ReadWrite | FileShare.Delete);
    afterProof.Dispose();
    var afterProofMove = churn.FirstPath + ".after-proof-move";
    File.Move(churn.FirstPath, afterProofMove);
    File.Move(afterProofMove, churn.FirstPath);
}

static void RecoveryRejectsTamperedAuthenticationWithoutWriting()
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(44, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ProcessCrashFaultInjector(MigrationFaultPoint.CommitVerified),
        out _);
    AssertThrows<SimulatedProcessCrashException>(
        () => coordinator.ExecuteAsync(
                acceptedPlan,
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                lease,
                context)
            .GetAwaiter()
            .GetResult(),
        "The authentication fixture must leave one unfinished transaction.");
    var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
    var targetAfterCrash = File.ReadAllBytes(targetFile);
    var storage = runtimeFactory.Storages.Single();
    var journal = storage.ReadForTest("journal.log");
    journal[^1] ^= 0x5A;
    storage.OverwriteForTest("journal.log", journal);

    var recovery = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _);
    var result = recovery.RecoverAsync(storage.TransactionId).GetAwaiter().GetResult();
    Assert(result.Status == MigrationRecoveryStatus.AuthenticationFailed &&
           File.ReadAllBytes(targetFile).SequenceEqual(targetAfterCrash),
        "A corrupt journal must fail authentication before any target write.");
    var pending = recovery.FindPending();
    Assert(pending.Count == 1 &&
           pending[0].AttentionStatus == MigrationRecoveryStatus.AuthenticationFailed,
        "Startup enumeration must surface a corrupt transaction as an authentication alert instead of crashing.");
    var diagnostic = recovery.ExportRedactedDiagnostic(storage.TransactionId);
    var diagnosticBytes = diagnostic.CopyBytes();
    try
    {
        using var document = JsonDocument.Parse(diagnosticBytes);
        Assert(document.RootElement.GetProperty("state").GetString() == "AuthenticationFailed" &&
               !Encoding.UTF8.GetString(diagnosticBytes)
                   .Contains(fixture.TargetRootPath, StringComparison.OrdinalIgnoreCase),
            "Authentication failure must still permit a bounded path-free diagnostic export.");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(diagnosticBytes);
    }
}

static void CoordinatorCommitsOnlyAfterFinalVerification()
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(31, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    var faultInjector = new ScriptedFaultInjector();
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        faultInjector,
        out _);
    var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
    var sourceBefore = fixture.Sandbox.SnapshotTree(fixture.SourceRootPath);
    var expected = acceptedPlan.AdapterStages[adapter.Id].Mutations.Single().AfterBytes.CopyBytes();
    var observedProgress = new List<MigrationProgress>();
    try
    {
        var result = coordinator.ExecuteAsync(
                acceptedPlan,
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                lease,
                context,
                progress: new InlineProgress<MigrationProgress>(observedProgress.Add),
                cancellationToken: CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        using var reopened = AuthenticatedTransactionStore.Open(
            runtimeFactory.Storages.Single(),
            runtimeFactory.ProtectedData,
            CancellationToken.None);
        var journal = reopened.Journal.ReadAndVerify(
            reopened.TransactionId,
            CancellationToken.None);
        Assert(result.IsSuccess &&
               result.TransactionId is not null &&
               result.CommittedFileCount == 1,
            $"A verified fixture transaction must return one committed file and an authenticated ID; " +
            $"status={result.Status}; committed={result.CommittedFileCount}; transaction={result.TransactionId is not null}; " +
            $"journal={string.Join(',', journal.Records.Select(record => record.Kind))}; " +
            $"diagnostics={string.Join(" | ", result.Diagnostics)}.");
        Assert(File.ReadAllBytes(targetFile).SequenceEqual(expected),
            "Coordinator success must leave the exact staged bytes at the target final name.");
        Assert(fixture.Sandbox.SnapshotTree(fixture.SourceRootPath) == sourceBefore,
            "Coordinator success must not mutate the source tree.");
        Assert(runtimeFactory.Storages.Count == 1,
            "A successful transaction must create exactly one authenticated transaction store.");
        Assert(Directory.GetFiles(
                   fixture.TargetRootPath,
                   ".bf-*",
                   SearchOption.TopDirectoryOnly).Length == 0,
            "Ordinary successful commit must remove its authenticated opaque displaced original.");
        Assert(journal.TerminalKind == TransactionRecordKind.Committed,
            "Success must follow a durable authenticated Committed record.");
        var observed = faultInjector.Observed;
        Assert(observed.Contains(MigrationFaultPoint.FinalRereadVerified) &&
               observed[^1] == MigrationFaultPoint.CommittedFlushed,
            "Committed must be the final accepted coordinator fault point after full reread verification.");
        AssertTruthfulSuccessfulProgress(
            observedProgress,
            acceptedPlan.AdapterStages.Values.Sum(stage => stage.Mutations.Count),
            "existing-target");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(expected);
    }
}

static void AppearanceAdapterCommitsSemanticUpdate()
{
    const string sourceText =
        "{\"shaders\":[null,{\"id\":\"light\"},{\"id\":\"dark\"}],\"version\":2,\"selectedShaderIndex\":2}";
    const string targetText =
        "{\r\n  \"version\": 2,\r\n  \"shaders\": [null, {\"id\":\"dark\"}, {\"id\":\"light\"}],\r\n  \"selectedShaderIndex\": 0\r\n}\r\n";
    const string expectedText =
        "{\r\n  \"version\": 2,\r\n  \"shaders\": [null, {\"id\":\"dark\"}, {\"id\":\"light\"}],\r\n  \"selectedShaderIndex\": 1\r\n}\r\n";
    using var fixture = TransactionAccessFixture.Create();
    var relativePath = Path.Combine("config", "darkmodeeverywhereshaders.json");
    fixture.WriteSourceBytes(relativePath, Encoding.UTF8.GetBytes(sourceText));
    fixture.WriteTargetBytes(relativePath, Encoding.UTF8.GetBytes(targetText));
    AddFabricModFixture(fixture, true, "darkmodeeverywhere", "1.21.1-1.4.0");
    AddFabricModFixture(fixture, false, "darkmodeeverywhere", "1.21.1-1.4.0");
    using var session = fixture.SessionFactory.Create(311, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var compatibility = AdapterCompatibilityEvidence.Create(
        fixture.Source.MinecraftVersion,
        fixture.Target.MinecraftVersion,
        [new KeyValuePair<string, string>("darkmodeeverywhere", "1.21.1-1.4.0")],
        [new KeyValuePair<string, string>("darkmodeeverywhere", "1.21.1-1.4.0")],
        []);
    var context = lease.CreateProbeContext(compatibility);
    var adapter = new DarkModeEverywhereAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var selectionRequest = ContentSelection.Create([catalog.Items.Single().Id], []);
    Assert(ContentSelectionValidator.TryValidateExplicit(
            catalog,
            selectionRequest,
            out var selection,
            out _),
        "The appearance transaction selection must validate.");
    var adapterPlan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    var contentPlan = MigrationContentPlan.Create(
        session.Generation,
        fixture.Source.Id,
        fixture.Target.Id,
        [adapterPlan]);
    var adapters = new Dictionary<string, IContentAdapter>(StringComparer.Ordinal)
    {
        [adapter.Id] = adapter,
    };
    var accepted = new AcceptedMigrationPlanFactory(fixture.SessionFactory, adapters).Create(
        session,
        fixture.Source.Id,
        fixture.Target.Id,
        lease,
        context,
        contentPlan);
    Assert(accepted.IsAccepted && accepted.Plan is not null,
        "The appearance transaction plan must be accepted.");
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ScriptedFaultInjector(),
        out _);
    var result = coordinator.ExecuteAsync(
            accepted.Plan!,
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            lease,
            context)
        .GetAwaiter()
        .GetResult();

    Assert(result.IsSuccess && result.CommittedFileCount == 1 &&
           File.ReadAllText(Path.Combine(fixture.TargetRootPath, relativePath)) == expectedText,
        "The transaction must commit only the mapped dark-mode index while preserving target JSON formatting.");
    var undone = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _)
        .UndoAsync(result.TransactionId!.Value)
        .GetAwaiter()
        .GetResult();
    Assert(undone.Status == MigrationRecoveryStatus.Recovered &&
           File.ReadAllText(Path.Combine(fixture.TargetRootPath, relativePath)) == targetText,
        "Appearance undo must be authorized by the current adapter catalog and restore the exact target JSON.");
}

static void AppearanceAdapterSeedsMissingTargetAndUndoRemovesIt()
{
    var sourceBytes =
        "{\n  \"shaders\": [null, {\"id\":\"dark\"}],\n  \"version\": 2,\n  \"selectedShaderIndex\": 1\n}\n"u8.ToArray();
    using var fixture = TransactionAccessFixture.Create();
    var relativePath = Path.Combine("config", "darkmodeeverywhereshaders.json");
    fixture.WriteSourceBytes(relativePath, sourceBytes);
    AddFabricModFixture(fixture, true, "darkmodeeverywhere", "1.21.1-1.4.0");
    AddFabricModFixture(fixture, false, "darkmodeeverywhere", "1.21.1-1.4.0");
    using var session = fixture.SessionFactory.Create(313, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var compatibility = AdapterCompatibilityEvidence.Create(
        fixture.Source.MinecraftVersion,
        fixture.Target.MinecraftVersion,
        [new KeyValuePair<string, string>("darkmodeeverywhere", "1.21.1-1.4.0")],
        [new KeyValuePair<string, string>("darkmodeeverywhere", "1.21.1-1.4.0")],
        []);
    var context = lease.CreateProbeContext(compatibility);
    var adapter = new DarkModeEverywhereAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var item = catalog.Items.Single();
    Assert(item.Disposition == PlannedContentDisposition.Add,
        "The missing-target appearance item must be an explicit add.");
    Assert(ContentSelectionValidator.TryValidateExplicit(
            catalog,
            ContentSelection.Create([item.Id], []),
            out var selection,
            out _),
        "The missing-target appearance transaction selection must validate.");
    var adapterPlan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    var contentPlan = MigrationContentPlan.Create(
        session.Generation,
        fixture.Source.Id,
        fixture.Target.Id,
        [adapterPlan]);
    var accepted = new AcceptedMigrationPlanFactory(
            fixture.SessionFactory,
            new Dictionary<string, IContentAdapter>(StringComparer.Ordinal)
            {
                [adapter.Id] = adapter,
            })
        .Create(
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            lease,
            context,
            contentPlan);
    Assert(accepted.IsAccepted && accepted.Plan is not null,
        "The missing-target appearance transaction plan must be accepted.");
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ScriptedFaultInjector(),
        out _);
    var observedProgress = new List<MigrationProgress>();
    var result = coordinator.ExecuteAsync(
            accepted.Plan!,
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            lease,
            context,
            progress: new InlineProgress<MigrationProgress>(observedProgress.Add))
        .GetAwaiter()
        .GetResult();
    var targetPath = Path.Combine(fixture.TargetRootPath, relativePath);
    Assert(result.IsSuccess &&
           result.CommittedFileCount == 1 &&
           File.ReadAllBytes(targetPath).SequenceEqual(sourceBytes),
        "The transaction must create the validated DME config before target first launch.");
    AssertTruthfulSuccessfulProgress(
        observedProgress,
        accepted.Plan!.AdapterStages.Values.Sum(stage => stage.Mutations.Count),
        "missing-target");

    var undone = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _)
        .UndoAsync(result.TransactionId!.Value)
        .GetAwaiter()
        .GetResult();
    Assert(undone.Status == MigrationRecoveryStatus.Recovered && !File.Exists(targetPath),
        "Appearance undo must remove the config created for an uninitialized target.");
}

static void VanillaFancyMenuMarkerTransactionCommitsAndRecovers()
{
    var markerBytes = "You're not supposed to be here! Shoo!"u8.ToArray();
    foreach (var simulateCrash in new[] { false, true })
    {
        using var fixture = TransactionAccessFixture.Create();
        var sourceOptions = "version:3955\nguiScale:3\nlang:en_us\n"u8.ToArray();
        var targetOptions = "version:3955\nguiScale:2\nlang:zh_cn\n"u8.ToArray();
        fixture.WriteSourceBytes("options.txt", sourceOptions);
        fixture.WriteTargetBytes("options.txt", targetOptions);
        var markerRelative = Path.Combine("fancymenu_data", "default_scale_set.fm");
        fixture.WriteSourceBytes(markerRelative, markerBytes);
        AddFabricModFixture(fixture, true, "fancymenu", "3.9.9");
        AddFabricModFixture(fixture, false, "fancymenu", "3.9.9");

        using var session = fixture.SessionFactory.Create(simulateCrash ? 318 : 317, fixture.Discovery);
        using var lease = fixture.OpenLease(session);
        var compatibility = AdapterCompatibilityEvidence.Create(
            fixture.Source.MinecraftVersion,
            fixture.Target.MinecraftVersion,
            [new KeyValuePair<string, string>("fancymenu", "3.9.9")],
            [new KeyValuePair<string, string>("fancymenu", "3.9.9")],
            []);
        var context = lease.CreateProbeContext(compatibility);
        var adapter = fixture.CreateVanillaAdapter();
        var catalog = adapter.BuildCatalog(context, CancellationToken.None);
        var guiScale = catalog.Items.Single(item =>
            string.Equals(item.Id.TechnicalKey, "guiScale", StringComparison.Ordinal));
        Assert(ContentSelectionValidator.TryValidateExplicit(
                catalog,
                ContentSelection.Create([guiScale.Id], []),
                out var selection,
                out _),
            "The FancyMenu transaction selection must validate.");
        var adapterPlan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
        var contentPlan = MigrationContentPlan.Create(
            session.Generation,
            fixture.Source.Id,
            fixture.Target.Id,
            [adapterPlan]);
        var adapters = new Dictionary<string, IContentAdapter>(StringComparer.Ordinal)
        {
            [adapter.Id] = adapter,
        };
        var accepted = new AcceptedMigrationPlanFactory(fixture.SessionFactory, adapters).Create(
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            lease,
            context,
            contentPlan);
        Assert(accepted.IsAccepted && accepted.Plan is not null &&
               accepted.Plan.AdapterStages[adapter.Id].Mutations.Count == 2 &&
               accepted.Plan.WriteAllowlist.SetEquals(
                   [NormalizeRequired("options.txt"), NormalizeRequired(markerRelative)]),
            "The FancyMenu accepted plan must authorize exactly options.txt and the first-launch marker.");

        var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
        IFaultInjector fault = simulateCrash
            ? new ProcessCrashFaultInjector(MigrationFaultPoint.CommitVerified)
            : new ScriptedFaultInjector();
        var coordinator = CreateFixtureCoordinator(
            fixture,
            adapter,
            runtimeFactory,
            fault,
            out _);
        var targetOptionsPath = Path.Combine(fixture.TargetRootPath, "options.txt");
        var targetMarkerPath = Path.Combine(fixture.TargetRootPath, markerRelative);

        if (simulateCrash)
        {
            AssertThrows<SimulatedProcessCrashException>(
                () => coordinator.ExecuteAsync(
                        accepted.Plan!,
                        session,
                        fixture.Source.Id,
                        fixture.Target.Id,
                        lease,
                        context)
                    .GetAwaiter()
                    .GetResult(),
                "The two-path FancyMenu fixture must leave an interrupted commit for startup recovery.");
            var recovery = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _);
            var pending = recovery.FindPending();
            Assert(pending.Count == 1 &&
                   recovery.RecoverAsync(pending[0].TransactionId).GetAwaiter().GetResult().Status ==
                   MigrationRecoveryStatus.Recovered,
                "Startup recovery must authorize and recover the interrupted FancyMenu marker transaction.");
        }
        else
        {
            var committed = coordinator.ExecuteAsync(
                    accepted.Plan!,
                    session,
                    fixture.Source.Id,
                    fixture.Target.Id,
                    lease,
                    context)
                .GetAwaiter()
                .GetResult();
            Assert(committed.IsSuccess && committed.CommittedFileCount == 2 &&
                   File.Exists(targetMarkerPath) &&
                   File.ReadAllText(targetOptionsPath).Contains("guiScale:3", StringComparison.Ordinal),
                "The FancyMenu transaction must commit the GUI scale and marker together.");
            var undone = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _)
                .UndoAsync(committed.TransactionId!.Value)
                .GetAwaiter()
                .GetResult();
            Assert(undone.Status == MigrationRecoveryStatus.Recovered,
                "FancyMenu undo must be authorized by the current two-path adapter allowlist.");
        }

        var optionsRestored = File.ReadAllBytes(targetOptionsPath).SequenceEqual(targetOptions);
        var markerExists = File.Exists(targetMarkerPath);
        var markerDirectoryExists = Directory.Exists(Path.GetDirectoryName(targetMarkerPath)!);
        Assert(optionsRestored && !markerExists && (!simulateCrash || !markerDirectoryExists),
            $"Commit undo or startup recovery must restore options exactly and remove the marker; " +
            $"crash={simulateCrash}, options={optionsRestored}, marker={markerExists}, directory={markerDirectoryExists}.");
    }
}

static void JeiMappedSourcePathCommitsToTargetScope()
{
    var bookmarkBytes = "[{\"version\":2},{\"type\":\"item\",\"value\":\"fixture\"}]"u8.ToArray();
    using var fixture = TransactionAccessFixture.Create();
    var sourceRelative = Path.Combine(
        "config", "jei", "world", "server", "source-runtime", "bookmarks.json");
    var targetDirectoryRelative = Path.Combine(
        "config", "jei", "world", "server", "target-runtime");
    fixture.WriteSourceBytes(sourceRelative, bookmarkBytes);
    fixture.Sandbox.CreateDirectory(
        Path.GetRelativePath(
            fixture.Sandbox.RootPath,
            Path.Combine(fixture.TargetRootPath, targetDirectoryRelative)));
    using var session = fixture.SessionFactory.Create(312, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var compatibility = AdapterCompatibilityEvidence.Create(
        fixture.Source.MinecraftVersion,
        fixture.Target.MinecraftVersion,
        [new KeyValuePair<string, string>("jei", "19.44.0.401")],
        [new KeyValuePair<string, string>("jei", "19.44.0.401")],
        []);
    var context = lease.CreateProbeContext(compatibility);
    var adapter = new JeiBookmarksAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var request = ContentSelection.Create([catalog.Items.Single().Id], []);
    Assert(ContentSelectionValidator.TryValidateExplicit(
            catalog,
            request,
            out var selection,
            out _),
        "The mapped JEI transaction selection must validate.");
    var adapterPlan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    var mappedChange = adapterPlan.FileChanges.Single();
    Assert(!mappedChange.SourceRelativePath.Equals(mappedChange.RelativePath),
        "The JEI mapping fixture must keep distinct source-read and target-write paths.");
    var contentPlan = MigrationContentPlan.Create(
        session.Generation,
        fixture.Source.Id,
        fixture.Target.Id,
        [adapterPlan]);
    var adapters = new Dictionary<string, IContentAdapter>(StringComparer.Ordinal)
    {
        [adapter.Id] = adapter,
    };
    var accepted = new AcceptedMigrationPlanFactory(fixture.SessionFactory, adapters).Create(
        session,
        fixture.Source.Id,
        fixture.Target.Id,
        lease,
        context,
        contentPlan);
    Assert(accepted.IsAccepted && accepted.Plan is not null,
        "The mapped JEI transaction plan must be accepted.");
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ScriptedFaultInjector(),
        out _);
    var result = coordinator.ExecuteAsync(
            accepted.Plan!,
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            lease,
            context)
        .GetAwaiter()
        .GetResult();
    var targetFile = Path.Combine(fixture.TargetRootPath, targetDirectoryRelative, "bookmarks.json");

    Assert(result.IsSuccess && result.CommittedFileCount == 1 &&
           File.ReadAllBytes(targetFile).SequenceEqual(bookmarkBytes) &&
           !File.Exists(Path.Combine(
               fixture.TargetRootPath,
               "config", "jei", "world", "server", "source-runtime", "bookmarks.json")),
        "The transaction must reread the mapped source and write only the target runtime scope.");
}

static void JeiAdapterSeedsMissingTargetScopeAndUndoRemovesIt()
{
    var bookmarkBytes = "[{\"version\":2},{\"type\":\"item\",\"value\":\"fixture\"}]"u8.ToArray();
    using var fixture = TransactionAccessFixture.Create();
    var relativePath = Path.Combine(
        "config", "jei", "world", "server", "source-runtime", "bookmarks.json");
    fixture.WriteSourceBytes(relativePath, bookmarkBytes);
    AddFabricModFixture(fixture, true, "jei", "19.44.0.401");
    AddFabricModFixture(fixture, false, "jei", "19.44.0.401");
    using var session = fixture.SessionFactory.Create(314, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var compatibility = AdapterCompatibilityEvidence.Create(
        fixture.Source.MinecraftVersion,
        fixture.Target.MinecraftVersion,
        [new KeyValuePair<string, string>("jei", "19.44.0.401")],
        [new KeyValuePair<string, string>("jei", "19.44.0.401")],
        []);
    var context = lease.CreateProbeContext(compatibility);
    var adapter = new JeiBookmarksAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var item = catalog.Items.Single();
    Assert(item.Disposition == PlannedContentDisposition.Add,
        "The missing-target JEI item must be an explicit add.");
    Assert(ContentSelectionValidator.TryValidateExplicit(
            catalog,
            ContentSelection.Create([item.Id], []),
            out var selection,
            out _),
        "The missing-target JEI transaction selection must validate.");
    var adapterPlan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    var deferredSeed = adapter.GetDeferredSeeds(adapterPlan).Single();
    var contentPlan = MigrationContentPlan.Create(
        session.Generation,
        fixture.Source.Id,
        fixture.Target.Id,
        [adapterPlan]);
    var accepted = new AcceptedMigrationPlanFactory(
            fixture.SessionFactory,
            new Dictionary<string, IContentAdapter>(StringComparer.Ordinal)
            {
                [adapter.Id] = adapter,
            })
        .Create(
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            lease,
            context,
            contentPlan);
    Assert(accepted.IsAccepted && accepted.Plan is not null,
        "The missing-target JEI transaction plan must be accepted.");
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ScriptedFaultInjector(),
        out _);
    var result = coordinator.ExecuteAsync(
            accepted.Plan!,
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            lease,
            context)
        .GetAwaiter()
        .GetResult();
    var targetPath = Path.Combine(fixture.TargetRootPath, relativePath);
    Assert(result.IsSuccess &&
           result.CommittedFileCount == 1 &&
           File.ReadAllBytes(targetPath).SequenceEqual(bookmarkBytes),
        "The transaction must create the JEI bookmark scope before joining the target server.");

    var actualRelative = Path.Combine(
        "config", "jei", "world", "server", "actual-target-runtime", "bookmarks.json");
    var emptyBookmarks = "[{\"version\":2}]"u8.ToArray();
    fixture.WriteTargetBytes(actualRelative, emptyBookmarks);
    var followUpCatalog = adapter.BuildCatalog(context, CancellationToken.None);
    var deferredResolution = adapter.ResolveDeferred(followUpCatalog, deferredSeed);
    Assert(deferredResolution.Kind == DeferredJeiResolutionKind.ReadyReplaceEmpty &&
           deferredResolution.ItemId is not null,
        "The deferred JEI follow-up must recognize the unique runtime scope with only default-empty bookmarks.");
    var followUpId = deferredResolution.ItemId!.Value;
    Assert(ContentSelectionValidator.TryValidateExplicit(
            followUpCatalog,
            ContentSelection.Create(
                [followUpId],
                [new KeyValuePair<ContentItemId, ConflictResolution>(
                    followUpId,
                    ConflictResolution.UseSource)]),
            out var followUpSelection,
            out _),
        "The default-empty JEI runtime scope must validate as an explicit protected replacement.");
    var followUpAdapterPlan = adapter.Plan(
        context,
        followUpCatalog,
        followUpSelection!,
        CancellationToken.None);
    var followUpContentPlan = MigrationContentPlan.Create(
        session.Generation,
        fixture.Source.Id,
        fixture.Target.Id,
        [followUpAdapterPlan]);
    var followUpAccepted = new AcceptedMigrationPlanFactory(
            fixture.SessionFactory,
            new Dictionary<string, IContentAdapter>(StringComparer.Ordinal)
            {
                [adapter.Id] = adapter,
            })
        .Create(
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            lease,
            context,
            followUpContentPlan);
    Assert(followUpAccepted.IsAccepted && followUpAccepted.Plan is not null,
        "The deferred JEI follow-up transaction must be accepted after runtime scope discovery.");
    var followUpResult = coordinator.ExecuteAsync(
            followUpAccepted.Plan!,
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            lease,
            context)
        .GetAwaiter()
        .GetResult();
    var actualTargetPath = Path.Combine(fixture.TargetRootPath, actualRelative);
    Assert(followUpResult.IsSuccess &&
           followUpResult.CommittedFileCount == 1 &&
           File.ReadAllBytes(actualTargetPath).SequenceEqual(bookmarkBytes),
        "The deferred JEI transaction must copy bookmarks into the runtime-generated target scope.");

    var recovery = CreateFixtureRecovery(runtimeFactory, fixture.AuditedCapability, out _);
    var followUpUndone = recovery
        .UndoAsync(followUpResult.TransactionId!.Value)
        .GetAwaiter()
        .GetResult();
    Assert(followUpUndone.Status == MigrationRecoveryStatus.Recovered &&
           File.ReadAllBytes(actualTargetPath).SequenceEqual(emptyBookmarks),
        "Deferred JEI undo must restore the target's original empty bookmark document.");
    var undone = recovery
        .UndoAsync(result.TransactionId!.Value)
        .GetAwaiter()
        .GetResult();
    Assert(undone.Status == MigrationRecoveryStatus.Recovered && !File.Exists(targetPath),
        "JEI undo must remove the bookmark file created for an uninitialized target.");
}

static void CoordinatorFaultRollsBackExactTarget()
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(32, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ScriptedFaultInjector(MigrationFaultPoint.CommitVerified),
        out _);
    var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
    var targetBefore = File.ReadAllBytes(targetFile);
    var sourceBefore = fixture.Sandbox.SnapshotTree(fixture.SourceRootPath);

    var result = coordinator.ExecuteAsync(
            acceptedPlan,
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            lease,
            context,
            cancellationToken: CancellationToken.None)
        .GetAwaiter()
        .GetResult();
    Assert(result.Status == MigrationExecutionStatus.RolledBack,
        "A post-commit injected failure must report a verified rollback, never success.");
    Assert(File.ReadAllBytes(targetFile).SequenceEqual(targetBefore),
        "Injected post-commit failure must restore the exact original target bytes.");
    Assert(fixture.Sandbox.SnapshotTree(fixture.SourceRootPath) == sourceBefore,
        "Injected rollback must leave the source tree unchanged.");
    using var reopened = AuthenticatedTransactionStore.Open(
        runtimeFactory.Storages.Single(),
        runtimeFactory.ProtectedData,
        CancellationToken.None);
    Assert(reopened.Journal.ReadAndVerify(
               reopened.TransactionId,
               CancellationToken.None).TerminalKind == TransactionRecordKind.RolledBack,
        "A successful automatic rollback must be durably authenticated as RolledBack.");
}

static void ImmediateRestoreDisplacedPreservesRacedTargets()
{
    foreach (var mutation in Enum.GetValues<FixtureRaceMutation>())
    {
        using var fixture = TransactionAccessFixture.Create();
        using var session = fixture.SessionFactory.Create(320 + (int)mutation, fixture.Discovery);
        using var lease = fixture.OpenLease(session);
        var context = lease.CreateProbeContext(fixture.CreateCompatibility());
        var adapter = fixture.CreateVanillaAdapter();
        var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
        var raceBytes = Encoding.UTF8.GetBytes($"restore-displaced-race:{mutation}\n");
        var hook = new FixtureTransactionRaceBoundaryHook(
            TransactionRaceBoundary.RestoreDisplacedAfterComparison,
            mutation,
            raceBytes);
        var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(
            fixture.AuditedCapability,
            hook);
        var coordinator = CreateFixtureCoordinator(
            fixture,
            adapter,
            runtimeFactory,
            new ScriptedFaultInjector(MigrationFaultPoint.CommitVerified),
            out _);
        var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");

        var result = coordinator.ExecuteAsync(
                acceptedPlan,
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                lease,
                context)
            .GetAwaiter()
            .GetResult();

        Assert(result.Status == MigrationExecutionStatus.RecoveryRequired,
            $"Immediate RestoreDisplaced must fail recovery-required after {mutation}; actual={result.Status}.");
        var finalBytes = File.ReadAllBytes(targetFile);
        Assert(hook.HitCount == 1 && finalBytes.SequenceEqual(raceBytes),
            $"Immediate RestoreDisplaced must preserve the exact {mutation} object at the final path; " +
            $"actual={Convert.ToHexString(finalBytes)}.");
        using var reopened = runtimeFactory.Open(runtimeFactory.Storages.Single().TransactionId, CancellationToken.None);
        var journal = reopened.Journal.ReadAndVerify(reopened.TransactionId, CancellationToken.None);
        Assert(!journal.IsTerminal && journal.Records[^1].Kind == TransactionRecordKind.RollbackIntent,
            "An immediate restore CAS mismatch must keep the authenticated pending rollback intent without an illegal terminal record.");
    }
}

static void ImmediateRestoreDisplacedPreservesRacedCaptureBeforeDelete()
{
    foreach (var mutation in Enum.GetValues<FixtureRaceMutation>())
    {
        using var fixture = TransactionAccessFixture.Create();
        using var session = fixture.SessionFactory.Create(325 + (int)mutation, fixture.Discovery);
        using var lease = fixture.OpenLease(session);
        var context = lease.CreateProbeContext(fixture.CreateCompatibility());
        var adapter = fixture.CreateVanillaAdapter();
        var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
        var raceBytes = Encoding.UTF8.GetBytes($"restore-displaced-capture-delete-race:{mutation}\n");
        var hook = new FixtureTransactionRaceBoundaryHook(
            TransactionRaceBoundary.RestoreDisplacedCaptureBeforeDelete,
            mutation,
            raceBytes);
        var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(
            fixture.AuditedCapability,
            hook);
        var coordinator = CreateFixtureCoordinator(
            fixture,
            adapter,
            runtimeFactory,
            new ScriptedFaultInjector(MigrationFaultPoint.CommitVerified),
            out _);

        var result = coordinator.ExecuteAsync(
                acceptedPlan,
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                lease,
                context)
            .GetAwaiter()
            .GetResult();

        Assert(result.Status == MigrationExecutionStatus.RecoveryRequired,
            $"Immediate rollback must remain pending after a {mutation} race on its displaced capture.");
        Assert(hook.AffectedPath is not null &&
               File.Exists(hook.AffectedPath) &&
               File.ReadAllBytes(hook.AffectedPath).SequenceEqual(raceBytes),
            $"Immediate rollback must preserve the exact {mutation} object introduced at its displaced capture; " +
            $"path={hook.AffectedPath}; exists={hook.AffectedPath is not null && File.Exists(hook.AffectedPath)}; " +
            $"actual={(hook.AffectedPath is not null && File.Exists(hook.AffectedPath) ? Convert.ToHexString(File.ReadAllBytes(hook.AffectedPath)) : "missing")}; " +
            $"expected={Convert.ToHexString(raceBytes)}; mutation-verified={hook.MutationVerified}.");
        AssertPendingRollbackIntent(runtimeFactory);
    }
}

static void ImmediateRestoreDisplacedPreservesPreMetadataRace()
{
    foreach (var mutation in Enum.GetValues<FixtureRaceMutation>())
    {
        using var fixture = TransactionAccessFixture.Create();
        using var session = fixture.SessionFactory.Create(327 + (int)mutation, fixture.Discovery);
        using var lease = fixture.OpenLease(session);
        var context = lease.CreateProbeContext(fixture.CreateCompatibility());
        var adapter = fixture.CreateVanillaAdapter();
        var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
        var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
        var raceBytes = Encoding.UTF8.GetBytes($"restore-displaced-pre-metadata-race:{mutation}\n");
        var hook = new FixtureTransactionRaceBoundaryHook(
            TransactionRaceBoundary.RestoreDisplacedBeforeMetadataApplication,
            mutation,
            raceBytes);
        var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(
            fixture.AuditedCapability,
            hook);
        var coordinator = CreateFixtureCoordinator(
            fixture,
            adapter,
            runtimeFactory,
            new ScriptedFaultInjector(MigrationFaultPoint.CommitVerified),
            out _);

        var result = coordinator.ExecuteAsync(
                acceptedPlan,
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                lease,
                context)
            .GetAwaiter()
            .GetResult();

        Assert(result.Status == MigrationExecutionStatus.RecoveryRequired &&
               File.ReadAllBytes(targetFile).SequenceEqual(raceBytes) &&
               File.GetLastWriteTimeUtc(targetFile) == hook.AffectedLastWriteTimeUtc,
            $"Immediate rollback must preserve the exact {mutation} object and metadata introduced before metadata restoration.");
        AssertPendingRollbackIntent(runtimeFactory);
    }
}

static void ImmediateRestoreDisplacedPreservesMetadataOnlyPreApplicationRace()
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(328, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    var hook = new FixtureMetadataOnlyRaceBoundaryHook(
        TransactionRaceBoundary.RestoreDisplacedBeforeMetadataApplication);
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(
        fixture.AuditedCapability,
        hook);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ScriptedFaultInjector(MigrationFaultPoint.CommitVerified),
        out _);
    var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
    var targetBefore = File.ReadAllBytes(targetFile);

    var result = coordinator.ExecuteAsync(
            acceptedPlan,
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            lease,
            context)
        .GetAwaiter()
        .GetResult();

    Assert(result.Status == MigrationExecutionStatus.RecoveryRequired &&
           hook.HitCount == 1 &&
           File.ReadAllBytes(targetFile).SequenceEqual(targetBefore) &&
           File.GetAttributes(targetFile) == hook.ChangedAttributes,
        "Immediate rollback must preserve a metadata-only pre-application change without overwriting it.");
    AssertPendingRollbackIntent(runtimeFactory);
}

static void CompensationDeletePreservesRacedCapture()
{
    foreach (var mutation in Enum.GetValues<FixtureRaceMutation>())
    {
        using var fixture = TransactionAccessFixture.Create();
        using var session = fixture.SessionFactory.Create(329 + (int)mutation, fixture.Discovery);
        using var lease = fixture.OpenLease(session);
        var context = lease.CreateProbeContext(fixture.CreateCompatibility());
        var adapter = fixture.CreateVanillaAdapter();
        var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
        var finalRaceBytes = Encoding.UTF8.GetBytes($"compensation-final-race:{mutation}\n");
        var captureRaceBytes = Encoding.UTF8.GetBytes($"compensation-capture-race:{mutation}\n");
        using var hook = new ScriptedTransactionRaceBoundaryHook(
            new FixtureRaceStep(
                TransactionRaceBoundary.RestoreDisplacedAfterComparison,
                mutation,
                finalRaceBytes),
            new FixtureRaceStep(
                TransactionRaceBoundary.CompensationCaptureBeforeDelete,
                mutation,
                captureRaceBytes));
        var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(
            fixture.AuditedCapability,
            hook);
        var coordinator = CreateFixtureCoordinator(
            fixture,
            adapter,
            runtimeFactory,
            new ScriptedFaultInjector(MigrationFaultPoint.CommitVerified),
            out _);
        var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");

        var result = coordinator.ExecuteAsync(
                acceptedPlan,
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                lease,
                context)
            .GetAwaiter()
            .GetResult();
        var captureStep = hook.Results.Single(item =>
            item.Boundary == TransactionRaceBoundary.CompensationCaptureBeforeDelete);

        Assert(result.Status == MigrationExecutionStatus.RecoveryRequired &&
               File.ReadAllBytes(targetFile).SequenceEqual(finalRaceBytes) &&
               captureStep.AffectedPath is not null &&
               File.Exists(captureStep.AffectedPath) &&
               File.ReadAllBytes(captureStep.AffectedPath).SequenceEqual(captureRaceBytes),
            $"Compensation must preserve both the final {mutation} object and the changed transaction capture.");
        AssertPendingRollbackIntent(runtimeFactory);
    }
}

static void ImmediateDeleteCreatedPreservesRacedTargets()
{
    foreach (var mutation in Enum.GetValues<FixtureRaceMutation>())
    {
        using var fixture = TransactionAccessFixture.Create();
        var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
        File.Delete(targetFile);
        using var session = fixture.SessionFactory.Create(330 + (int)mutation, fixture.Discovery);
        using var lease = fixture.OpenLease(session);
        var context = lease.CreateProbeContext(fixture.CreateCompatibility());
        var adapter = fixture.CreateVanillaAdapter();
        var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
        var raceBytes = Encoding.UTF8.GetBytes($"delete-created-race:{mutation}\n");
        var hook = new FixtureTransactionRaceBoundaryHook(
            TransactionRaceBoundary.DeleteCreatedAfterComparison,
            mutation,
            raceBytes);
        var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(
            fixture.AuditedCapability,
            hook);
        var coordinator = CreateFixtureCoordinator(
            fixture,
            adapter,
            runtimeFactory,
            new ScriptedFaultInjector(MigrationFaultPoint.CommitVerified),
            out _);

        var result = coordinator.ExecuteAsync(
                acceptedPlan,
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                lease,
                context)
            .GetAwaiter()
            .GetResult();

        Assert(result.Status == MigrationExecutionStatus.RecoveryRequired,
            $"Delete-created rollback must fail recovery-required after {mutation}; actual={result.Status}.");
        Assert(hook.HitCount == 1 && File.ReadAllBytes(targetFile).SequenceEqual(raceBytes),
            $"Delete-created rollback must preserve the exact {mutation} object at the final path.");
        using var reopened = runtimeFactory.Open(runtimeFactory.Storages.Single().TransactionId, CancellationToken.None);
        var journal = reopened.Journal.ReadAndVerify(reopened.TransactionId, CancellationToken.None);
        Assert(!journal.IsTerminal && journal.Records[^1].Kind == TransactionRecordKind.RollbackIntent,
            "A delete-created CAS mismatch must keep the authenticated pending rollback intent without an illegal terminal record.");
    }
}

static void DeleteCreatedExclusiveBoundaryBlocksWriter()
{
    using var fixture = TransactionAccessFixture.Create();
    var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
    File.Delete(targetFile);
    using var session = fixture.SessionFactory.Create(335, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    var blockedBytes = Encoding.UTF8.GetBytes("exclusive-delete-write-must-be-blocked\n");
    using var hook = new ScriptedTransactionRaceBoundaryHook(
        new FixtureRaceStep(
            TransactionRaceBoundary.AuthenticatedDeleteAfterComparison,
            FixtureRaceMutation.ContentWrite,
            blockedBytes,
            ExpectMutationBlocked: true));
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability, hook);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ScriptedFaultInjector(MigrationFaultPoint.CommitVerified),
        out _);

    var result = coordinator.ExecuteAsync(
            acceptedPlan,
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            lease,
            context)
        .GetAwaiter()
        .GetResult();

    Assert(result.Status == MigrationExecutionStatus.RolledBack &&
           !File.Exists(targetFile) &&
           hook.Results.Single().MutationBlocked,
        "Authenticated delete must exclude a writer from final comparison through disposition and close.");
}

static void DeleteCreatedExclusiveAcquisitionPreservesObject()
{
    using var fixture = TransactionAccessFixture.Create();
    var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
    File.Delete(targetFile);
    using var session = fixture.SessionFactory.Create(336, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    using var hook = new ScriptedTransactionRaceBoundaryHook(
        new FixtureRaceStep(
            TransactionRaceBoundary.DeleteCreatedAfterComparison,
            HoldWriter: true));
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability, hook);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ScriptedFaultInjector(MigrationFaultPoint.CommitVerified),
        out _);

    var result = coordinator.ExecuteAsync(
            acceptedPlan,
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            lease,
            context)
        .GetAwaiter()
        .GetResult();

    Assert(result.Status == MigrationExecutionStatus.RecoveryRequired &&
           File.Exists(targetFile),
        "Failure to acquire authenticated-delete exclusivity must preserve the transaction-created object.");
    AssertPendingRollbackIntent(runtimeFactory);
}

static void CoordinatorRejectsStaleAuthorityBeforeRuntime()
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(33, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ScriptedFaultInjector(),
        out _);
    fixture.MoveSourceRoot();

    var result = coordinator.ExecuteAsync(
            acceptedPlan,
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            lease,
            context,
            cancellationToken: CancellationToken.None)
        .GetAwaiter()
        .GetResult();
    Assert(result.Status == MigrationExecutionStatus.RejectedStale && runtimeFactory.CreateCount == 0,
        "Execution-time discovery drift must be rejected before store, process, mutex writer, or target mutation setup.");
}

static void CoordinatorRegeneratesAllowlistsAtExecution()
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(34, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var inner = fixture.CreateVanillaAdapter();
    var adapter = new CountingAdapter(inner);
    var contentPlan = fixture.CreateVanillaPlan(inner, context, session.Generation);
    var accepted = new AcceptedMigrationPlanFactory(
        fixture.SessionFactory,
        new Dictionary<string, IContentAdapter>(StringComparer.Ordinal)
        {
            [adapter.Id] = adapter,
        }).Create(
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            lease,
            context,
            contentPlan);
    Assert(accepted.IsAccepted && accepted.Plan is not null,
        "The mutable allowlist fixture must accept its initial live set.");
    adapter.ReturnEmptyAllowlist = true;
    var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(fixture.AuditedCapability);
    var coordinator = CreateFixtureCoordinator(
        fixture,
        adapter,
        runtimeFactory,
        new ScriptedFaultInjector(),
        out _);

    var result = coordinator.ExecuteAsync(
            accepted.Plan!,
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            lease,
            context,
            cancellationToken: CancellationToken.None)
        .GetAwaiter()
        .GetResult();
    Assert(result.Status == MigrationExecutionStatus.RejectedStale &&
           runtimeFactory.CreateCount == 0 &&
           adapter.RegenerateCount == 2,
        "Execution must regenerate every allowlist and reject changes before runtime construction.");
}

static void PublicPairCannotOpenTargetRoot()
{
    var openMethods = typeof(ITransactionFileOperations)
        .GetMethods()
        .Where(method => method.Name == "OpenTargetRoot")
        .ToArray();
    Assert(openMethods.Length == 1 &&
           openMethods[0].GetParameters()[0].ParameterType ==
           typeof(MigrationTransactionCoordinator.ExecutionAuthority),
        "The target writer must have one opaque-authority-only root opener.");
    Assert(openMethods[0].GetParameters().All(parameter =>
            parameter.ParameterType != typeof(DiscoveredInstancePair) &&
            parameter.ParameterType != typeof(DiscoveredInstanceChoice) &&
            parameter.ParameterType != typeof(AcceptedMigrationPlan)),
        "Public discovery evidence or an accepted plan alone must not open the target writer.");

    var adapterTypes = typeof(IContentAdapter).Assembly.GetTypes()
        .Where(type => !type.IsAbstract && typeof(IContentAdapter).IsAssignableFrom(type));
    foreach (var adapterType in adapterTypes)
    {
        var exposedTypes = adapterType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType))
            .Concat(adapterType.GetProperties().Select(property => property.PropertyType))
            .Concat(adapterType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType)));
        Assert(exposedTypes.All(type => type != typeof(ITransactionFileOperations)),
            $"Content adapter {adapterType.Name} must remain independent from transaction writers.");
    }
}

static void ExistingTargetRoundTripsThroughBackupReplaceAndRollback()
{
    using var fixture = TransactionAccessFixture.Create();
    var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
    FixtureSecurityDescriptor.SetDistinctProtectedDacl(targetFile);
    using var session = fixture.SessionFactory.Create(21, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var contentPlan = fixture.CreateVanillaPlan(adapter, context, session.Generation);
    var accepted = new AcceptedMigrationPlanFactory(
        fixture.SessionFactory,
        new Dictionary<string, IContentAdapter>(StringComparer.Ordinal)
        {
            [adapter.Id] = adapter,
        }).Create(
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            lease,
            context,
            contentPlan);
    Assert(accepted.IsAccepted && accepted.Plan is not null,
        "The file-operations fixture must produce an accepted migration plan.");
    var acceptedPlan = accepted.Plan ??
        throw new InvalidOperationException("The accepted file-operations plan was unavailable.");
    var currentPair = fixture.SessionFactory.Revalidate(
        session,
        fixture.Source.Id,
        fixture.Target.Id).Pair ??
        throw new InvalidOperationException("The file-operations fixture pair did not revalidate.");
    var authority = CreateAuthorityFromAcceptedPlan(
        acceptedPlan,
        currentPair,
        acceptedPlan.RegeneratedAdapterAllowlists);
    var mutation = acceptedPlan.AdapterStages[adapter.Id].Mutations.Single();
    var originalTargetBytes = File.ReadAllBytes(targetFile);
    var sourceTreeBefore = fixture.Sandbox.SnapshotTree(fixture.SourceRootPath);

    var transactionId = new TransactionId(Guid.NewGuid());
    var storage = new MemoryTransactionStorageDirectory(transactionId);
    using var store = AuthenticatedTransactionStore.Bootstrap(
        storage,
        new FixtureProtectedData(),
        RecoveryLocator.Create(
            transactionId,
            fixture.Target.Id,
            fixture.TargetRootPath,
            currentPair.Target.GameRoot.Identity),
        StoredMigrationPlan.Create(
            transactionId,
            acceptedPlan.IntegrityDigest,
            acceptedPlan.WriteAllowlist.Select(path =>
                StoredPlanPath.Create(adapter.Id, path, ConflictResolution.UseSource))),
        CancellationToken.None);
    var backupStore = new BackupStore(store, new FixtureProtectedData());
    var operations = new WindowsTransactionFileOperations(fixture.AuditedCapability, backupStore);
    using var target = operations.OpenTargetRoot(authority, CancellationToken.None);
    const string objectId = "object-options";
    var path = acceptedPlan.WriteAllowlist.Single();

    var backupPermit = store.Journal.AppendIntent(
        TransactionIntent.Create(
            TransactionRecordKind.BackupIntent,
            objectId,
            path,
            mutation.Change.TargetSnapshot.Sha256),
        CancellationToken.None);
    var backup = operations.BackupExisting(
        target,
        mutation.Change,
        backupPermit,
        CancellationToken.None);
    var originalTargetState = FixtureSupportedFileStateCapture.Capture(targetFile);
    store.Journal.AppendVerified(
        backupPermit,
        TransactionVerification.Create(
            TransactionRecordKind.BackupVerified,
            objectId,
            path,
            backup.Metadata.Sha256),
        CancellationToken.None);

    var stagePermit = store.Journal.AppendIntent(
        TransactionIntent.Create(
            TransactionRecordKind.StageIntent,
            objectId,
            path,
            backup.Metadata.Sha256),
        CancellationToken.None);
    using var staged = operations.Stage(target, mutation, stagePermit, CancellationToken.None);
    Assert(!FixtureSecurityDescriptor.DaclAndProtectionEqual(
            backup.Metadata.SecurityDescriptor,
            staged.Metadata.SecurityDescriptor),
        "The custom-DACL fixture must prove the authenticated target DACL/protection differs from the inherited stage DACL/protection before replacement.");
    store.Journal.AppendVerified(
        stagePermit,
        TransactionVerification.Create(
            TransactionRecordKind.StageVerified,
            objectId,
            path,
            staged.Metadata.Sha256),
        CancellationToken.None);

    var commitPermit = store.Journal.AppendIntent(
        TransactionIntent.Create(
            TransactionRecordKind.CommitIntent,
            objectId,
            path,
            backup.Metadata.Sha256),
        CancellationToken.None);
    using var outcome = operations.ReplaceExisting(
        target,
        staged,
        backup.ExpectedTarget,
        commitPermit,
        CancellationToken.None);
    Assert(outcome.DisplacedMatchesExpected,
        "The actual displaced object must match the authenticated backup before commit can continue.");
    store.Journal.AppendVerified(
        commitPermit,
        TransactionVerification.Create(
            TransactionRecordKind.CommitVerified,
            objectId,
            path,
            outcome.Replacement.Metadata.Sha256),
        CancellationToken.None);
    var committedState = FixtureSupportedFileStateCapture.Capture(targetFile);
    var expectedAfterBytes = mutation.AfterBytes.CopyBytes();
    Assert(committedState.Bytes.SequenceEqual(expectedAfterBytes) &&
           FixtureSupportedFileStateCapture.HasIdentity(committedState, staged.Metadata.Identity) &&
           FixtureSupportedFileStateCapture.MetadataEquals(originalTargetState, committedState) &&
           !committedState.Bytes.SequenceEqual(originalTargetBytes) &&
           File.Exists(outcome.Displaced.RetainedPath),
        "The custom-DACL target must contain staged bytes with the complete authenticated target metadata while the displaced original remains retained; " +
        $"content={committedState.Bytes.SequenceEqual(expectedAfterBytes)}; " +
        $"identity={FixtureSupportedFileStateCapture.HasIdentity(committedState, staged.Metadata.Identity)}; " +
        $"metadata={FixtureSupportedFileStateCapture.MetadataEquals(originalTargetState, committedState)}; " +
        FixtureSupportedFileStateCapture.DescribeDifference(originalTargetState, committedState));
    AssertThrows<IOException>(
        () =>
        {
            using var denied = new FileStream(
                targetFile,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
        },
        "The retained committed handle must deny external writes during the commit window.");
    var retainedHandleWrite = FixtureHandleAccessProbe.TrySetLastWriteTime(
        outcome.Replacement.Handle,
        outcome.Replacement.Metadata.LastWriteTimeUtc);
    Assert(!retainedHandleWrite.Succeeded && retainedHandleWrite.Error == 5,
        "The retained committed handle itself must deny supported metadata writes with access denied; " +
        $"succeeded={retainedHandleWrite.Succeeded}; Windows error={retainedHandleWrite.Error}.");

    var rollbackPermit = store.Journal.AppendIntent(
        TransactionIntent.Create(
            TransactionRecordKind.RollbackIntent,
            objectId,
            path,
            outcome.Replacement.Metadata.Sha256),
        CancellationToken.None);
    operations.RestoreDisplaced(target, outcome.Displaced, rollbackPermit, CancellationToken.None);
    store.Journal.AppendVerified(
        rollbackPermit,
        TransactionVerification.Create(
            TransactionRecordKind.RollbackVerified,
            objectId,
            path,
            backup.Metadata.Sha256),
        CancellationToken.None);

    var rolledBackState = FixtureSupportedFileStateCapture.Capture(targetFile);
    Assert(FixtureSupportedFileStateCapture.SemanticallyEquals(originalTargetState, rolledBackState),
        "Rollback must restore the exact original target identity, bytes, and complete supported metadata.");
    Assert(fixture.Sandbox.SnapshotTree(fixture.SourceRootPath) == sourceTreeBefore,
        "Backup, replace, and rollback must leave the synthetic source tree unchanged.");
}

static void NormalReplaceRacesFailBeforeMetadataMutation()
{
    var metadataFailures = new List<string>();
    foreach (var mutation in Enum.GetValues<FixtureRaceMutation>())
    {
        using var fixture = TransactionAccessFixture.Create();
        using var session = fixture.SessionFactory.Create(335 + (int)mutation, fixture.Discovery);
        using var lease = fixture.OpenLease(session);
        var context = lease.CreateProbeContext(fixture.CreateCompatibility());
        var adapter = fixture.CreateVanillaAdapter();
        var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
        var raceBytes = Encoding.UTF8.GetBytes($"normal-replace-pre-metadata-race:{mutation}\n");
        var hook = new FixtureTransactionRaceBoundaryHook(
            TransactionRaceBoundary.NormalReplaceBeforeMetadataAuthentication,
            mutation,
            raceBytes);
        var runtimeFactory = new FixtureMigrationTransactionRuntimeFactory(
            fixture.AuditedCapability,
            hook);
        var coordinator = CreateFixtureCoordinator(
            fixture,
            adapter,
            runtimeFactory,
            new ScriptedFaultInjector(),
            out _);
        var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");

        var result = coordinator.ExecuteAsync(
                acceptedPlan,
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                lease,
                context)
            .GetAwaiter()
            .GetResult();

        Assert(result.Status == MigrationExecutionStatus.RecoveryRequired,
            $"A normal replacement {mutation} race must fail closed and remain recoverable; actual={result.Status}.");
        var finalState = FixtureSupportedFileStateCapture.Capture(targetFile);
        Assert(hook.HitCount == 1 &&
               hook.MutationVerified &&
               hook.AffectedState is not null &&
               finalState.Bytes.SequenceEqual(raceBytes),
            $"The normal replacement hook must install and preserve the exact {mutation} race object; " +
            $"actual={Convert.ToHexString(finalState.Bytes)}; expected={Convert.ToHexString(raceBytes)}.");
        if (!FixtureSupportedFileStateCapture.SemanticallyEquals(hook.AffectedState!, finalState))
        {
            metadataFailures.Add(
                $"{mutation}: {FixtureSupportedFileStateCapture.DescribeDifference(hook.AffectedState!, finalState)}");
        }
        using var reopened = runtimeFactory.Open(
            runtimeFactory.Storages.Single().TransactionId,
            CancellationToken.None);
        var journal = reopened.Journal.ReadAndVerify(reopened.TransactionId, CancellationToken.None);
        Assert(!journal.IsTerminal &&
               journal.Records[^1].Kind == TransactionRecordKind.CommitIntent,
            $"A normal replacement {mutation} race must leave a legal authenticated nonterminal journal; " +
            $"actual={journal.Records[^1].Kind}.");
    }

    Assert(metadataFailures.Count == 0,
        "Normal replacement must authenticate every race object before copying metadata:\n" +
        string.Join("\n", metadataFailures));
}

static void MissingTargetCreatesAndRollsBackWithoutOverwrite()
{
    using var fixture = TransactionAccessFixture.Create();
    var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
    File.Delete(targetFile);
    using var session = fixture.SessionFactory.Create(22, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    var mutation = acceptedPlan.AdapterStages[adapter.Id].Mutations.Single();
    Assert(!mutation.Change.TargetSnapshot.Exists, "The create-new fixture target must be absent at planning.");
    var currentPair = fixture.SessionFactory.Revalidate(
        session,
        fixture.Source.Id,
        fixture.Target.Id).Pair!;
    var authority = CreateAuthorityFromAcceptedPlan(
        acceptedPlan,
        currentPair,
        acceptedPlan.RegeneratedAdapterAllowlists);
    var transactionId = new TransactionId(Guid.NewGuid());
    var storage = new MemoryTransactionStorageDirectory(transactionId);
    using var store = AuthenticatedTransactionStore.Bootstrap(
        storage,
        new FixtureProtectedData(),
        RecoveryLocator.Create(
            transactionId,
            fixture.Target.Id,
            fixture.TargetRootPath,
            currentPair.Target.GameRoot.Identity),
        StoredMigrationPlan.Create(
            transactionId,
            acceptedPlan.IntegrityDigest,
            [StoredPlanPath.Create(adapter.Id, acceptedPlan.WriteAllowlist.Single(), ConflictResolution.UseSource)]),
        CancellationToken.None);
    var backups = new BackupStore(store, new FixtureProtectedData());
    var operations = new WindowsTransactionFileOperations(fixture.AuditedCapability, backups);
    using var target = operations.OpenTargetRoot(authority, CancellationToken.None);
    var path = acceptedPlan.WriteAllowlist.Single();
    const string objectId = "object-create";
    var stagePermit = store.Journal.AppendIntent(
        TransactionIntent.Create(
            TransactionRecordKind.StageIntent,
            objectId,
            path,
            mutation.Change.TargetSnapshot.Sha256),
        CancellationToken.None);
    using var staged = operations.Stage(target, mutation, stagePermit, CancellationToken.None);
    store.Journal.AppendVerified(
        stagePermit,
        TransactionVerification.Create(
            TransactionRecordKind.StageVerified,
            objectId,
            path,
            staged.Metadata.Sha256),
        CancellationToken.None);
    var commitPermit = store.Journal.AppendIntent(
        TransactionIntent.Create(
            TransactionRecordKind.CommitIntent,
            objectId,
            path,
            mutation.Change.TargetSnapshot.Sha256),
        CancellationToken.None);
    using var created = operations.CreateMissing(target, staged, commitPermit, CancellationToken.None);
    store.Journal.AppendVerified(
        commitPermit,
        TransactionVerification.Create(
            TransactionRecordKind.CommitVerified,
            objectId,
            path,
            created.Metadata.Sha256),
        CancellationToken.None);
    Assert(File.Exists(targetFile), "Create-new must materialize the staged file at the exact final name.");
    AssertThrowsAny<IOException, UnauthorizedAccessException>(
        () => File.Delete(targetFile),
        "The retained create-new final handle must deny external deletion during the commit window.");
    var rollbackPermit = store.Journal.AppendIntent(
        TransactionIntent.Create(
            TransactionRecordKind.RollbackIntent,
            objectId,
            path,
            created.Metadata.Sha256),
        CancellationToken.None);
    operations.DeleteCreatedFile(target, created, rollbackPermit, CancellationToken.None);
    store.Journal.AppendVerified(
        rollbackPermit,
        TransactionVerification.Create(
            TransactionRecordKind.RollbackVerified,
            objectId,
            path,
            Convert.ToHexString(SHA256.HashData([]))),
        CancellationToken.None);
    Assert(!File.Exists(targetFile), "Rollback must remove only the identity-matching create-new file.");
}

static void ParentDirectoriesCreateAndCleanUpInReverse()
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(23, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    var nestedPath = NormalizeRequired("config\\jei\\world\\fixture\\bookmarks.ini");
    var nestedSet = new BlockFerry.Core.Content.ReadOnlySet<NormalizedRelativePath>(
        [nestedPath],
        NormalizedRelativePathComparer.Instance);
    var nestedPlan = new AcceptedMigrationPlan(
        acceptedPlan.AcceptedFingerprint,
        acceptedPlan.ContentPlan,
        acceptedPlan.AdapterStages,
        new Dictionary<string, IReadOnlySet<NormalizedRelativePath>>(StringComparer.Ordinal)
        {
            [adapter.Id] = nestedSet,
        },
        nestedSet,
        acceptedPlan.IntegrityDigest,
        session,
        lease,
        context);
    var pair = fixture.SessionFactory.Revalidate(
        session,
        fixture.Source.Id,
        fixture.Target.Id).Pair!;
    var authority = CreateAuthorityFromAcceptedPlan(
        nestedPlan,
        pair,
        nestedPlan.RegeneratedAdapterAllowlists);
    var transactionId = new TransactionId(Guid.NewGuid());
    var storage = new MemoryTransactionStorageDirectory(transactionId);
    using var store = AuthenticatedTransactionStore.Bootstrap(
        storage,
        new FixtureProtectedData(),
        RecoveryLocator.Create(
            transactionId,
            fixture.Target.Id,
            fixture.TargetRootPath,
            pair.Target.GameRoot.Identity),
        StoredMigrationPlan.Create(
            transactionId,
            nestedPlan.IntegrityDigest,
            [StoredPlanPath.Create(adapter.Id, nestedPath, ConflictResolution.UseSource)]),
        CancellationToken.None);
    var backupStore = new BackupStore(store, new FixtureProtectedData());
    var operations = new WindowsTransactionFileOperations(fixture.AuditedCapability, backupStore);
    using var target = operations.OpenTargetRoot(authority, CancellationToken.None);
    var missing = operations.FindMissingParentDirectories(target, nestedPath, CancellationToken.None);
    Assert(missing.Select(path => path.Value).SequenceEqual(
            ["config", "config\\jei", "config\\jei\\world", "config\\jei\\world\\fixture"],
            StringComparer.Ordinal),
        "Missing parent discovery must return every absent segment in root-to-leaf order.");
    var created = new List<CreatedDirectory>();
    foreach (var directory in missing)
    {
        var objectId = "dir-" + created.Count;
        var permit = store.Journal.AppendIntent(
            TransactionIntent.Create(
                TransactionRecordKind.DirectoryIntent,
                objectId,
                directory,
                Convert.ToHexString(SHA256.HashData([]))),
            CancellationToken.None);
        var item = operations.CreateDirectory(target, directory, permit, CancellationToken.None);
        store.Journal.AppendVerified(
            permit,
            TransactionVerification.Create(
                TransactionRecordKind.DirectoryCreated,
                objectId,
                directory,
                Convert.ToHexString(SHA256.HashData([]))),
            CancellationToken.None);
        operations.PersistCreatedDirectory(target, item, CancellationToken.None);
        created.Add(item);
    }

    foreach (var directory in created.AsEnumerable().Reverse())
    {
        var permit = store.Journal.AppendIntent(
            TransactionIntent.Create(
                TransactionRecordKind.RollbackIntent,
                directory.OpaqueObjectId,
                directory.RelativePath,
                Convert.ToHexString(SHA256.HashData([]))),
            CancellationToken.None);
        operations.RemoveCreatedDirectory(target, directory, permit, CancellationToken.None);
        store.Journal.AppendVerified(
            permit,
            TransactionVerification.Create(
                TransactionRecordKind.RollbackVerified,
                directory.OpaqueObjectId,
                directory.RelativePath,
                Convert.ToHexString(SHA256.HashData([]))),
            CancellationToken.None);
    }

    Assert(!Directory.Exists(Path.Combine(fixture.TargetRootPath, "config")),
        "Reverse rollback must remove each transaction-created empty parent directory.");
}

static void ProvisionalDirectoryHandlesCloseBothCrashGaps()
{
    var failures = new List<string>();
    foreach (var boundary in new[]
             {
                 DirectoryCrashBoundary.NamespaceCreated,
                 DirectoryCrashBoundary.CreatedRecordDurableBeforePersistence,
             })
    {
        try
        {
            ProvisionalDirectoryHandleClosesCrashGap(boundary);
        }
        catch (Exception exception)
        {
            failures.Add($"{boundary}: {exception.Message}");
        }
    }

    Assert(failures.Count == 0,
        "Provisional directory crash boundaries failed:\n" + string.Join("\n", failures));
}

static void ProvisionalDirectoryHandleClosesCrashGap(DirectoryCrashBoundary boundary)
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(22 + (int)boundary, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var acceptedPlan = AcceptVanillaPlan(fixture, session, lease, context, adapter);
    var nestedPath = NormalizeRequired("config\\fixture\\settings.ini");
    var nestedSet = new BlockFerry.Core.Content.ReadOnlySet<NormalizedRelativePath>(
        [nestedPath],
        NormalizedRelativePathComparer.Instance);
    var nestedPlan = new AcceptedMigrationPlan(
        acceptedPlan.AcceptedFingerprint,
        acceptedPlan.ContentPlan,
        acceptedPlan.AdapterStages,
        new Dictionary<string, IReadOnlySet<NormalizedRelativePath>>(StringComparer.Ordinal)
        {
            [adapter.Id] = nestedSet,
        },
        nestedSet,
        acceptedPlan.IntegrityDigest,
        session,
        lease,
        context);
    var pair = fixture.SessionFactory.Revalidate(
        session,
        fixture.Source.Id,
        fixture.Target.Id).Pair!;
    var authority = CreateAuthorityFromAcceptedPlan(
        nestedPlan,
        pair,
        nestedPlan.RegeneratedAdapterAllowlists);
    var transactionId = new TransactionId(Guid.NewGuid());
    using var store = AuthenticatedTransactionStore.Bootstrap(
        new MemoryTransactionStorageDirectory(transactionId),
        new FixtureProtectedData(),
        RecoveryLocator.Create(
            transactionId,
            fixture.Target.Id,
            fixture.TargetRootPath,
            pair.Target.GameRoot.Identity),
        StoredMigrationPlan.Create(
            transactionId,
            nestedPlan.IntegrityDigest,
            [StoredPlanPath.Create(adapter.Id, nestedPath, ConflictResolution.UseSource)]),
        CancellationToken.None);
    var operations = new WindowsTransactionFileOperations(
        fixture.AuditedCapability,
        new BackupStore(store, new FixtureProtectedData()));
    using var target = operations.OpenTargetRoot(authority, CancellationToken.None);
    var directory = NormalizeRequired("config");
    var objectId = MigrationTransactionCoordinator.ComputeOpaqueObjectId("directory", directory.Value);
    var permit = store.Journal.AppendIntent(
        TransactionIntent.Create(
            TransactionRecordKind.DirectoryIntent,
            objectId,
            directory,
            Convert.ToHexString(SHA256.HashData([]))),
        CancellationToken.None);
    var created = operations.CreateDirectory(target, directory, permit, CancellationToken.None);
    if (boundary == DirectoryCrashBoundary.CreatedRecordDurableBeforePersistence)
    {
        store.Journal.AppendVerified(
            permit,
            TransactionVerification.Create(
                TransactionRecordKind.DirectoryCreated,
                objectId,
                directory,
                MigrationTransactionCoordinator.IdentityDigest(created.Identity)),
            CancellationToken.None);
    }

    created.Dispose();
    var expectedRecord = boundary == DirectoryCrashBoundary.NamespaceCreated
        ? TransactionRecordKind.DirectoryIntent
        : TransactionRecordKind.DirectoryCreated;
    var journal = store.Journal.ReadAndVerify(transactionId, CancellationToken.None);
    Assert(journal.Records[^1].Kind == expectedRecord,
        $"{boundary} must leave the expected authenticated journal boundary.");
    Assert(!Directory.Exists(Path.Combine(fixture.TargetRootPath, "config")),
        $"{boundary} handle close must remove the provisional empty directory.");
}

static void UnsupportedTargetMetadataFailsBeforeMutation()
{
    foreach (var attribute in new[] { FileAttributes.ReadOnly, FileAttributes.System })
    {
        using var fixture = TransactionAccessFixture.Create();
        var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
        var originalAttributes = File.GetAttributes(targetFile);
        File.SetAttributes(targetFile, originalAttributes | attribute);
        try
        {
            AssertBackupIsRejectedWithoutPermitConsumption(fixture, targetFile);
        }
        finally
        {
            File.SetAttributes(targetFile, originalAttributes);
        }
    }

    using (var fixture = TransactionAccessFixture.Create())
    {
        var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
        File.WriteAllText(targetFile + ":blockferry-fixture", "ads");
        AssertBackupIsRejectedWithoutPermitConsumption(fixture, targetFile);
    }

    using (var fixture = TransactionAccessFixture.Create())
    {
        var targetFile = Path.Combine(fixture.TargetRootPath, "options.txt");
        var hardlink = Path.Combine(fixture.TargetRootPath, "options-hardlink.txt");
        var info = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("/d");
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add("mklink");
        info.ArgumentList.Add("/H");
        info.ArgumentList.Add(hardlink);
        info.ArgumentList.Add(targetFile);
        using var process = System.Diagnostics.Process.Start(info) ??
            throw new InvalidOperationException("The hardlink fixture process did not start.");
        process.WaitForExit();
        Assert(process.ExitCode == 0 && File.Exists(hardlink),
            "The NTFS hardlink fixture must be available for the release safety gate.");
        AssertBackupIsRejectedWithoutPermitConsumption(fixture, targetFile);
    }
}

static void AssertBackupIsRejectedWithoutPermitConsumption(
    TransactionAccessFixture fixture,
    string targetFile)
{
    using var session = fixture.SessionFactory.Create(24, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var contentPlan = fixture.CreateVanillaPlan(adapter, context, session.Generation);
    var accepted = new AcceptedMigrationPlanFactory(
        fixture.SessionFactory,
        new Dictionary<string, IContentAdapter>(StringComparer.Ordinal) { [adapter.Id] = adapter })
        .Create(
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            lease,
            context,
            contentPlan);
    Assert(accepted.IsAccepted && accepted.Plan is not null,
        "Unsafe metadata fixture planning must remain read-only and accepted before the write-time metadata gate.");
    var plan = accepted.Plan!;
    var pair = fixture.SessionFactory.Revalidate(session, fixture.Source.Id, fixture.Target.Id).Pair!;
    var authority = CreateAuthorityFromAcceptedPlan(plan, pair, plan.RegeneratedAdapterAllowlists);
    var transactionId = new TransactionId(Guid.NewGuid());
    var storage = new MemoryTransactionStorageDirectory(transactionId);
    using var store = AuthenticatedTransactionStore.Bootstrap(
        storage,
        new FixtureProtectedData(),
        RecoveryLocator.Create(
            transactionId,
            fixture.Target.Id,
            fixture.TargetRootPath,
            pair.Target.GameRoot.Identity),
        StoredMigrationPlan.Create(
            transactionId,
            plan.IntegrityDigest,
            [StoredPlanPath.Create(adapter.Id, plan.WriteAllowlist.Single(), ConflictResolution.UseSource)]),
        CancellationToken.None);
    var backupStore = new BackupStore(store, new FixtureProtectedData());
    var operations = new WindowsTransactionFileOperations(fixture.AuditedCapability, backupStore);
    using var target = operations.OpenTargetRoot(authority, CancellationToken.None);
    var mutation = plan.AdapterStages[adapter.Id].Mutations.Single();
    var path = plan.WriteAllowlist.Single();
    var permit = store.Journal.AppendIntent(
        TransactionIntent.Create(
            TransactionRecordKind.BackupIntent,
            "unsafe-object",
            path,
            mutation.Change.TargetSnapshot.Sha256),
        CancellationToken.None);
    AssertThrows<NotSupportedException>(
        () => operations.BackupExisting(target, mutation.Change, permit, CancellationToken.None),
        $"Unsupported metadata on {Path.GetFileName(targetFile)} must fail before any target mutation.");
    AssertThrows<InvalidOperationException>(
        () => store.Journal.AppendVerified(
            permit,
            TransactionVerification.Create(
                TransactionRecordKind.BackupVerified,
                "unsafe-object",
                path,
                mutation.Change.TargetSnapshot.Sha256),
            CancellationToken.None),
        "A rejected metadata probe must not consume its durable mutation permit.");
}

static void BootstrapFailureTouchesNoTarget()
{
    var transactionId = new TransactionId(Guid.NewGuid());
    var storage = new MemoryTransactionStorageDirectory(transactionId)
    {
        FailAfterMutationCount = 3,
    };
    var targetSentinel = SHA256.HashData("synthetic-target-remains-read-only"u8);
    var before = targetSentinel.ToArray();

    AssertThrows<IOException>(
        () => AuthenticatedTransactionStore.Bootstrap(
            storage,
            new FixtureProtectedData(),
            CreateRecoveryLocator(transactionId),
            CreateStoredPlan(transactionId),
            CancellationToken.None),
        "A bootstrap flush failure must be reported before target access is possible.");
    Assert(storage.Names.Count == 0 && targetSentinel.SequenceEqual(before),
        "A failed fresh bootstrap must clean its app-storage artifacts and never touch target state.");
}

static void CrossTransactionReplayFails()
{
    var firstId = new TransactionId(Guid.NewGuid());
    var secondId = new TransactionId(Guid.NewGuid());
    var firstStorage = new MemoryTransactionStorageDirectory(firstId);
    var secondStorage = new MemoryTransactionStorageDirectory(secondId);
    using var first = AuthenticatedTransactionStore.Bootstrap(
        firstStorage,
        new FixtureProtectedData(),
        CreateRecoveryLocator(firstId),
        CreateStoredPlan(firstId),
        CancellationToken.None);
    using var second = AuthenticatedTransactionStore.Bootstrap(
        secondStorage,
        new FixtureProtectedData(),
        CreateRecoveryLocator(secondId),
        CreateStoredPlan(secondId),
        CancellationToken.None);

    var path = NormalizeRequired("options.txt");
    var intent = TransactionIntent.Create(
        TransactionRecordKind.BackupIntent,
        "object-0001",
        path,
        new string('A', 64));
    var permit = first.Journal.AppendIntent(intent, CancellationToken.None);
    permit.Consume(firstId, TransactionRecordKind.BackupIntent, "object-0001", path);
    first.Journal.AppendVerified(
        permit,
        TransactionVerification.Create(
            TransactionRecordKind.BackupVerified,
            "object-0001",
            path,
            new string('B', 64)),
        CancellationToken.None);

    secondStorage.OverwriteForTest("journal.log", firstStorage.ReadForTest("journal.log"));
    AssertThrows<TransactionAuthenticationException>(
        () => second.Journal.ReadAndVerify(secondId, CancellationToken.None),
        "A valid journal from another transaction must fail authentication.");

    secondStorage.OverwriteForTest("plan.dpapi", firstStorage.ReadForTest("plan.dpapi"));
    AssertThrows<TransactionAuthenticationException>(
        () => AuthenticatedTransactionStore.Open(
            secondStorage,
            new FixtureProtectedData(),
            CancellationToken.None),
        "A protected plan copied from another transaction must fail its ID-bound protection.");
}

static void EveryJournalTruncationFailsClosed()
{
    var transactionId = new TransactionId(Guid.NewGuid());
    var storage = new MemoryTransactionStorageDirectory(transactionId);
    using var store = AuthenticatedTransactionStore.Bootstrap(
        storage,
        new FixtureProtectedData(),
        CreateRecoveryLocator(transactionId),
        CreateStoredPlan(transactionId),
        CancellationToken.None);
    var original = storage.ReadForTest("journal.log");
    Assert(original.Length > 1, "The authenticated journal fixture must be non-empty.");

    for (var length = 0; length < original.Length; length++)
    {
        storage.OverwriteForTest("journal.log", original.AsSpan(0, length).ToArray());
        AssertThrows<TransactionAuthenticationException>(
            () => store.Journal.ReadAndVerify(transactionId, CancellationToken.None),
            $"Journal truncation at byte {length} must fail closed.");
    }

    storage.OverwriteForTest("journal.log", original);
    Assert(store.Journal.ReadAndVerify(transactionId, CancellationToken.None).Records.Count == 1,
        "The unmodified prepared journal must verify after exhaustive truncation checks.");
}

static void IntentPermitRequiresDurableAppend()
{
    var transactionId = new TransactionId(Guid.NewGuid());
    var storage = new MemoryTransactionStorageDirectory(transactionId);
    using var store = AuthenticatedTransactionStore.Bootstrap(
        storage,
        new FixtureProtectedData(),
        CreateRecoveryLocator(transactionId),
        CreateStoredPlan(transactionId),
        CancellationToken.None);
    var path = NormalizeRequired("options.txt");
    var intent = TransactionIntent.Create(
        TransactionRecordKind.BackupIntent,
        "object-0002",
        path,
        new string('C', 64));

    storage.FailNextAppend = true;
    AssertThrows<IOException>(
        () => store.Journal.AppendIntent(intent, CancellationToken.None),
        "An unflushed intent must not issue a mutation permit.");

    var permit = store.Journal.AppendIntent(intent, CancellationToken.None);
    var verification = TransactionVerification.Create(
        TransactionRecordKind.BackupVerified,
        "object-0002",
        path,
        new string('D', 64));
    AssertThrows<InvalidOperationException>(
        () => store.Journal.AppendVerified(permit, verification, CancellationToken.None),
        "Verification before mutation consumption must fail.");
    AssertThrows<InvalidOperationException>(
        () => permit.Consume(transactionId, TransactionRecordKind.CommitIntent, "object-0002", path),
        "A wrong-kind permit consumption must fail.");
    permit.Consume(transactionId, TransactionRecordKind.BackupIntent, "object-0002", path);
    AssertThrows<InvalidOperationException>(
        () => permit.Consume(transactionId, TransactionRecordKind.BackupIntent, "object-0002", path),
        "A mutation permit must be single-use.");
    store.Journal.AppendVerified(permit, verification, CancellationToken.None);
    AssertThrows<InvalidOperationException>(
        () => store.Journal.AppendVerified(permit, verification, CancellationToken.None),
        "A verification record must be single-use.");
}

static void CurrentUserProtectedStoreRoundTrips()
{
    using var sandbox = FixtureSandbox.Create();
    var localAppData = sandbox.CreateGuidDirectory();
    var audited = new AuditedFileSystemCapability([sandbox.GetRootProof(localAppData)]);
    using var appStorage = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData },
        audited);
    Assert(appStorage.IsAvailable, "The synthetic guarded LocalAppData fixture must be available.");
    var transactionId = new TransactionId(Guid.NewGuid());
    var targetRoot = Path.Combine(sandbox.RootPath, "synthetic-target");
    var locator = RecoveryLocator.Create(
        transactionId,
        "target-instance",
        targetRoot,
        new PhysicalDirectoryIdentity(11, 12, 13));
    var plan = CreateStoredPlan(transactionId);

    using (var created = AuthenticatedTransactionStore.Bootstrap(
               appStorage,
               locator,
               plan,
               CancellationToken.None))
    {
        Assert(created.Journal.ReadAndVerify(transactionId, CancellationToken.None).Records.Count == 1,
            "The production CurrentUser-protected store must verify its prepared journal.");
    }

    var transactionRoot = Path.Combine(
        localAppData,
        "BlockFerry",
        "transactions",
        transactionId.Value.ToString("N"));
    var artifactNames = Directory.EnumerateFileSystemEntries(transactionRoot)
        .Select(path => Path.GetFileName(path) ??
            throw new InvalidOperationException("The transaction artifact name was unavailable."))
        .Order(StringComparer.Ordinal)
        .ToArray();
    HashSet<string> expectedArtifactNames =
    [
        "before",
        "journal.log",
        "key.dpapi",
        "manifest.log",
        "plan.dpapi",
        "recovery-locator.dpapi",
    ];
    Assert(expectedArtifactNames.SetEquals(artifactNames),
        "The transaction bootstrap must create only fixed opaque artifact names.");
    var journalText = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(transactionRoot, "journal.log")));
    var manifestText = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(transactionRoot, "manifest.log")));
    Assert(!journalText.Contains("synthetic-target", StringComparison.OrdinalIgnoreCase) &&
           !manifestText.Contains("synthetic-target", StringComparison.OrdinalIgnoreCase),
        "Plaintext authenticated logs must not contain the target path.");

    using var reopened = AuthenticatedTransactionStore.Open(
        appStorage,
        transactionId,
        CancellationToken.None);
    Assert(reopened.Locator.TargetRootIdentity == locator.TargetRootIdentity &&
           reopened.Plan.AcceptedPlanDigest == plan.AcceptedPlanDigest &&
           reopened.Journal.ReadAndVerify(transactionId, CancellationToken.None).Records.Count == 1,
        "DPAPI CurrentUser transaction artifacts must round-trip through a fresh retained capability.");
}

static void LegacyOwnedAppRootIsHardenedWithoutChangingTheme()
{
    using var sandbox = FixtureSandbox.Create();
    var localAppData = sandbox.CreateGuidDirectory();
    var legacyRoot = Directory.CreateDirectory(Path.Combine(localAppData, "BlockFerry"));
    var themePath = Path.Combine(legacyRoot.FullName, "theme.txt");
    var expected = Encoding.UTF8.GetBytes("light");
    File.WriteAllBytes(themePath, expected);
    FixtureSecurityDescriptor.SetCurrentUserOwner(legacyRoot.FullName);
    FixtureSecurityDescriptor.SetCurrentUserOwner(themePath);
    var audited = new AuditedFileSystemCapability([sandbox.GetRootProof(localAppData)]);

    using var appStorage = new AppStorageGuard(
        new FakeEnvironmentPaths { LocalAppData = localAppData },
        audited);
    Assert(appStorage.IsAvailable,
        "An owned, normal legacy BlockFerry root must be hardened before guarded storage opens.");
    Assert(NormalizedRelativePath.TryCreate("theme.txt", out var themeRelativePath, out _) &&
           themeRelativePath is not null,
        "The legacy theme fixture path must normalize.");
    var result = appStorage.TryRead(themeRelativePath!, 16, CancellationToken.None);
    Assert(result.State == AppStorageReadState.Read &&
           result.Bytes is not null &&
           result.Bytes.SequenceEqual(expected) &&
           File.ReadAllBytes(themePath).SequenceEqual(expected),
        $"Legacy theme bytes must survive root and leaf DACL hardening exactly; " +
        $"state={result.State}; diagnostic={appStorage.LastDiagnostic?.Code}; " +
        $"safe={appStorage.LastDiagnostic?.Message}; " +
        $"events={string.Join(',', appStorage.AuditLog.Select(item => item.Operation))}.");
}

static void StoredPlanRejectsNormalizedCollisions()
{
    var transactionId = new TransactionId(Guid.NewGuid());
    var upper = StoredPlanPath.Create(
        "vanilla",
        NormalizeRequired("Config\\Value.txt"),
        ConflictResolution.UseSource);
    var lower = StoredPlanPath.Create(
        "vanilla",
        NormalizeRequired("config\\value.TXT"),
        ConflictResolution.UseSource);
    AssertThrows<ArgumentException>(
        () => StoredMigrationPlan.Create(
            transactionId,
            new string('F', 64),
            [upper, lower]),
        "Stored plan paths that collide under Windows rules must be rejected.");

    Assert(NormalizedRelativePath.TryCreate("config\\e\u0301.txt", out var decomposed, out _) &&
           decomposed is not null,
        "The Unicode collision fixture must be representable by the general path model.");
    AssertThrows<ArgumentException>(
        () => StoredPlanPath.Create("vanilla", decomposed!, ConflictResolution.UseSource),
        "A non-NFC stored plan path must be rejected before protection.");

    AssertThrows<ArgumentException>(
        () => StoredMigrationPlan.Create(
            transactionId,
            new string('F', 64),
            Enumerable.Repeat(upper, ContentContractLimits.MaximumFileChanges + 1)),
        "A protected plan may not exceed its fixed path-count bound.");
}

static void JournalStructuralTamperingFailsClosed()
{
    var transactionId = new TransactionId(Guid.NewGuid());
    var key = SHA256.HashData("known fixture journal key"u8);
    var prepared = TransactionJournal.CreatePreparedPayload(
        transactionId,
        new string('1', 64),
        key);
    try
    {
        var unknownSchema = prepared.ToArray();
        unknownSchema[8] = 2;
        AssertThrows<TransactionAuthenticationException>(
            () => TransactionJournalCodec.DecodeAndVerify(unknownSchema, transactionId, key),
            "An unknown journal schema must fail closed.");

        var first = TransactionJournalCodec.DecodeAndVerify(prepared, transactionId, key).Records.Single();
        var pathMac = TransactionJournalCodec.ComputePathMac(transactionId, "options.txt", key);
        var digest = SHA256.HashData("intent-before"u8);
        try
        {
            var duplicateSequence = TransactionJournalCodec.EncodeRecord(
                transactionId,
                1,
                TransactionRecordKind.BackupIntent,
                "object-structural",
                pathMac,
                digest,
                first.RecordMac,
                key);
            AssertThrows<TransactionAuthenticationException>(
                () => TransactionJournalCodec.DecodeAndVerify(
                    prepared.Concat(duplicateSequence).ToArray(),
                    transactionId,
                    key),
                "A duplicate or regressed journal sequence must fail closed.");

            var brokenPreviousMac = TransactionJournalCodec.EncodeRecord(
                transactionId,
                2,
                TransactionRecordKind.BackupIntent,
                "object-structural",
                pathMac,
                digest,
                new byte[32],
                key);
            AssertThrows<TransactionAuthenticationException>(
                () => TransactionJournalCodec.DecodeAndVerify(
                    prepared.Concat(brokenPreviousMac).ToArray(),
                    transactionId,
                    key),
                "A broken previous-MAC link must fail even when the new record MAC is valid.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pathMac);
            CryptographicOperations.ZeroMemory(digest);
        }
    }
    finally
    {
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(prepared);
    }
}

static RecoveryLocator CreateRecoveryLocator(TransactionId transactionId) => RecoveryLocator.Create(
    transactionId,
    "target-instance",
    "C:\\synthetic\\target",
    new PhysicalDirectoryIdentity(7, 8, 9));

static StoredMigrationPlan CreateStoredPlan(TransactionId transactionId) => StoredMigrationPlan.Create(
    transactionId,
    new string('E', 64),
    [StoredPlanPath.Create("vanilla", NormalizeRequired("options.txt"), ConflictResolution.UseSource)]);

static void AddStoredPlanPathForTest(
    FixtureMigrationTransactionRuntimeFactory runtimeFactory,
    StoredPlanPath additionalPath)
{
    var storage = runtimeFactory.Storages.Single();
    var transactionId = storage.TransactionId;
    byte[]? ciphertext = null;
    byte[]? entropy = null;
    byte[]? plaintext = null;
    byte[]? replacementPlaintext = null;
    byte[]? replacementCiphertext = null;
    try
    {
        ciphertext = storage.ReadForTest("plan.dpapi");
        entropy = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"BlockFerry.Transaction.v1|{transactionId.Value:N}|plan.dpapi"));
        plaintext = runtimeFactory.ProtectedData.Unprotect(
            ciphertext,
            entropy,
            512 * 1024);
        var original = RecoveryLocatorCodec.DecodePlan(plaintext, transactionId);
        var replacement = StoredMigrationPlan.Create(
            transactionId,
            original.AcceptedPlanDigest,
            original.Paths.Concat([additionalPath]),
            original.Purpose);
        replacementPlaintext = RecoveryLocatorCodec.Encode(replacement);
        replacementCiphertext = runtimeFactory.ProtectedData.Protect(
            replacementPlaintext,
            entropy,
            512 * 1024);
        storage.OverwriteForTest("plan.dpapi", replacementCiphertext);
    }
    finally
    {
        if (ciphertext is not null)
        {
            CryptographicOperations.ZeroMemory(ciphertext);
        }

        if (entropy is not null)
        {
            CryptographicOperations.ZeroMemory(entropy);
        }

        if (plaintext is not null)
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        if (replacementPlaintext is not null)
        {
            CryptographicOperations.ZeroMemory(replacementPlaintext);
        }

        if (replacementCiphertext is not null)
        {
            CryptographicOperations.ZeroMemory(replacementCiphertext);
        }
    }
}

static void ReplaceStoredPlanPathTextForTest(
    FixtureMigrationTransactionRuntimeFactory runtimeFactory,
    string originalPath,
    string replacementPath)
{
    var originalBytes = Encoding.UTF8.GetBytes(originalPath);
    var replacementBytes = Encoding.UTF8.GetBytes(replacementPath);
    if (originalBytes.Length != replacementBytes.Length)
    {
        throw new ArgumentException("The protected path fixture replacement must preserve encoded length.");
    }

    var storage = runtimeFactory.Storages.Single();
    var transactionId = storage.TransactionId;
    byte[]? ciphertext = null;
    byte[]? entropy = null;
    byte[]? plaintext = null;
    byte[]? replacementCiphertext = null;
    try
    {
        ciphertext = storage.ReadForTest("plan.dpapi");
        entropy = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"BlockFerry.Transaction.v1|{transactionId.Value:N}|plan.dpapi"));
        plaintext = runtimeFactory.ProtectedData.Unprotect(
            ciphertext,
            entropy,
            512 * 1024);
        var offset = plaintext.AsSpan().IndexOf(originalBytes);
        if (offset < 0 || plaintext.AsSpan((offset + originalBytes.Length)..).IndexOf(originalBytes) >= 0)
        {
            throw new InvalidOperationException("The protected plan path fixture was not unique.");
        }

        replacementBytes.CopyTo(plaintext.AsSpan(offset, replacementBytes.Length));
        replacementCiphertext = runtimeFactory.ProtectedData.Protect(
            plaintext,
            entropy,
            512 * 1024);
        storage.OverwriteForTest("plan.dpapi", replacementCiphertext);
    }
    finally
    {
        CryptographicOperations.ZeroMemory(originalBytes);
        CryptographicOperations.ZeroMemory(replacementBytes);
        if (ciphertext is not null)
        {
            CryptographicOperations.ZeroMemory(ciphertext);
        }

        if (entropy is not null)
        {
            CryptographicOperations.ZeroMemory(entropy);
        }

        if (plaintext is not null)
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        if (replacementCiphertext is not null)
        {
            CryptographicOperations.ZeroMemory(replacementCiphertext);
        }
    }
}

static NormalizedRelativePath NormalizeRequired(string value)
{
    if (!WritePathGuard.TryNormalize(value, out var path) || path is null)
    {
        throw new InvalidOperationException("The fixture path was unexpectedly rejected.");
    }

    return path;
}

static void WriteAllowlistRejectsEveryUnsafeShape()
{
    string[] unsafePaths =
    [
        "",
        "..\\escape.txt",
        ".\\escape.txt",
        "C:\\escape.txt",
        "C:escape.txt",
        "\\\\server\\share\\escape.txt",
        "\\\\?\\C:\\escape.txt",
        "file.txt:stream",
        "CON",
        "dir\\NUL.txt",
        "trailing. ",
        "trailing.",
        "double\\\\separator",
    ];
    foreach (var candidate in unsafePaths)
    {
        Assert(!WritePathGuard.TryNormalize(candidate, out _),
            $"Unsafe write path was accepted: {candidate}");
    }

    Assert(WritePathGuard.TryNormalize("config/jei/bookmarks.json", out var normalized) &&
           normalized!.Value == "config\\jei\\bookmarks.json",
        "A safe relative content path must normalize exactly once.");
}

static void PublicPairCannotAuthorizeAcceptedPlan()
{
    var acceptedFactoryMethods = typeof(AcceptedMigrationPlanFactory)
        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(method => method.Name == "Create")
        .ToArray();
    Assert(acceptedFactoryMethods.Length == 1,
        "AcceptedMigrationPlanFactory must expose one executable Create path.");
    var parameterTypes = acceptedFactoryMethods[0].GetParameters()
        .Select(parameter => parameter.ParameterType)
        .ToArray();
    Assert(parameterTypes.Contains(typeof(DiscoverySession)) &&
           parameterTypes.Contains(typeof(ContentAccessLease)) &&
           parameterTypes.Contains(typeof(ContentProbeContext)) &&
           parameterTypes.Contains(typeof(MigrationContentPlan)) &&
           !parameterTypes.Contains(typeof(DiscoveredInstancePair)) &&
           !parameterTypes.Contains(typeof(DiscoveredInstanceChoice)),
        "Only the original session, IDs, live lease/context, and content plan may authorize acceptance.");

    var authority = typeof(MigrationTransactionCoordinator.ExecutionAuthority);
    Assert(authority.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .All(constructor => constructor.IsPrivate),
        "ExecutionAuthority constructors must remain private to the coordinator.");
    Assert(typeof(TargetMutexFactory).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method =>
                method.Name == "Acquire" &&
                method.GetParameters()[0].ParameterType == authority)
            .GetParameters()[0].ParameterType == authority,
        "The target mutex must accept only opaque execution authority.");
    Assert(typeof(MinecraftProcessGuard).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method =>
                method.Name == "Begin" &&
                method.GetParameters()[0].ParameterType == authority)
            .GetParameters()[0].ParameterType == authority,
        "The process guard must accept only opaque execution authority.");

    Assert(typeof(ProcessInventoryEntry).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .All(property => property.Name != "CommandLine"),
        "Raw command lines must not be exposed by the public process model.");

    var productionRoot = FindProductionRoot();
    var productionSources = Directory.EnumerateFiles(
            Path.Combine(productionRoot, "src", "BlockFerry.Core"),
            "*.cs",
            SearchOption.AllDirectories)
        .Select(File.ReadAllText)
        .ToArray();
    var authorityConstructionSites = productionSources.Count(source =>
        source.Contains("new ExecutionAuthority(", StringComparison.Ordinal) ||
        source.Contains("new MigrationTransactionCoordinator.ExecutionAuthority(", StringComparison.Ordinal));
    Assert(authorityConstructionSites <= 1,
        "Opaque execution authority must have at most the coordinator's sole production construction site.");
}

static void AcceptanceRequiresOriginalSessionIdsLeaseAndContext()
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(11, fixture.Discovery);
    using var siblingSession = fixture.SessionFactory.Create(11, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    using var siblingLease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var adapter = fixture.CreateVanillaAdapter();
    var contentPlan = fixture.CreateVanillaPlan(adapter, context, session.Generation);
    var registry = new Dictionary<string, IContentAdapter>(StringComparer.Ordinal)
    {
        [adapter.Id] = adapter,
    };
    var factory = new AcceptedMigrationPlanFactory(fixture.SessionFactory, registry);

    var accepted = factory.Create(
        session,
        fixture.Source.Id,
        fixture.Target.Id,
        lease,
        context,
        contentPlan);
    Assert(accepted.IsAccepted &&
           accepted.Plan is not null &&
           accepted.Diagnostics.Count == 0 &&
           ReferenceEquals(accepted.Plan.Session, session) &&
           ReferenceEquals(accepted.Plan.ContentLease, lease) &&
           ReferenceEquals(accepted.Plan.ContentContext, context) &&
           accepted.Plan.WriteAllowlist.Count == 1 &&
           accepted.Plan.WriteAllowlist.Single().Value == "options.txt" &&
           accepted.Plan.IntegrityDigest.Length == 64,
        "A valid accepted plan must retain the exact original authority objects and one normalized write path.");

    var siblingContext = siblingLease.CreateProbeContext(fixture.CreateCompatibility());
    foreach (var rejected in new[]
             {
                 factory.Create(
                     session,
                     fixture.Target.Id,
                     fixture.Source.Id,
                     lease,
                     context,
                     contentPlan),
                 factory.Create(
                     siblingSession,
                     fixture.Source.Id,
                     fixture.Target.Id,
                     lease,
                     context,
                     contentPlan),
                 factory.Create(
                     session,
                     fixture.Source.Id,
                     fixture.Target.Id,
                     siblingLease,
                     context,
                     contentPlan),
                 factory.Create(
                     session,
                     fixture.Source.Id,
                     fixture.Target.Id,
                     lease,
                     siblingContext,
                     contentPlan),
             })
    {
        Assert(!rejected.IsAccepted && rejected.Plan is null && rejected.Diagnostics.Count == 1,
            "Reversed IDs, sibling sessions, leases, and contexts must all reject without an accepted plan.");
    }
}

static void AcceptanceRevalidatesBeforeStaging()
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(12, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var inner = fixture.CreateVanillaAdapter();
    var contentPlan = fixture.CreateVanillaPlan(inner, context, session.Generation);
    var counting = new CountingAdapter(inner);
    var factory = new AcceptedMigrationPlanFactory(
        fixture.SessionFactory,
        new Dictionary<string, IContentAdapter>(StringComparer.Ordinal)
        {
            [counting.Id] = counting,
        });

    fixture.MoveSourceRoot();
    var rejected = factory.Create(
        session,
        fixture.Source.Id,
        fixture.Target.Id,
        lease,
        context,
        contentPlan);
    Assert(!rejected.IsAccepted &&
           rejected.Plan is null &&
           rejected.Diagnostics.Single().Code == ContentDiagnosticCode.StaleContext &&
           counting.StageCount == 0 &&
           counting.RegenerateCount == 0,
        "Root drift must be rejected by discovery revalidation before any adapter stage or allowlist regeneration.");
}

static void AcceptanceRegeneratesAllowlistFromLiveContext()
{
    using var fixture = TransactionAccessFixture.Create();
    using var session = fixture.SessionFactory.Create(13, fixture.Discovery);
    using var lease = fixture.OpenLease(session);
    var context = lease.CreateProbeContext(fixture.CreateCompatibility());
    var inner = fixture.CreateVanillaAdapter();
    var contentPlan = fixture.CreateVanillaPlan(inner, context, session.Generation);
    var drifting = new CountingAdapter(inner) { ReturnEmptyAllowlist = true };
    var factory = new AcceptedMigrationPlanFactory(
        fixture.SessionFactory,
        new Dictionary<string, IContentAdapter>(StringComparer.Ordinal)
        {
            [drifting.Id] = drifting,
        });

    var rejected = factory.Create(
        session,
        fixture.Source.Id,
        fixture.Target.Id,
        lease,
        context,
        contentPlan);
    Assert(!rejected.IsAccepted &&
           rejected.Plan is null &&
           rejected.Diagnostics.Single().Code == ContentDiagnosticCode.PathConflict &&
           drifting.StageCount == 1 &&
           drifting.RegenerateCount == 1,
        "A staged path absent from the freshly regenerated adapter allowlist must reject acceptance.");
}

static void MinecraftCommandLineEvidenceIsBoundedAndRedacted()
{
    const string token = "fixture-secret-access-token";
    var entry = ProcessInventoryEntry.Readable(
        42,
        "javaw",
        $"javaw.exe -Xmx4G net.minecraft.client.main.Main --accessToken {token} --gameDir=\"C:\\fixture\\target\"");
    var parser = new MinecraftCommandLineParser(new RejectingArgumentFileReader());
    var evidence = parser.Parse(entry, ["C:\\fixture"]);
    Assert(evidence.Classification == MinecraftProcessClassification.Minecraft &&
           evidence.MainClass == "net.minecraft.client.main.Main" &&
           evidence.GameDirectory == "C:\\fixture\\target" &&
           !evidence.ToString().Contains(token, StringComparison.Ordinal) &&
           !entry.ToString().Contains(token, StringComparison.Ordinal),
        "Minecraft parsing must retain only bounded classification evidence and redact unrelated arguments.");

    var splitForm = ProcessInventoryEntry.Readable(
        43,
        "java",
        "java.exe cpw.mods.modlauncher.Launcher --gameDir \"C:\\fixture\\source\"");
    Assert(parser.Parse(splitForm, ["C:\\fixture"]).GameDirectory == "C:\\fixture\\source",
        "The split --gameDir form must parse.");
    Assert(parser.Parse(ProcessInventoryEntry.Unreadable(44, "javaw"), ["C:\\fixture"])
            .Classification == MinecraftProcessClassification.UnsafeCandidate,
        "An unreadable Java candidate must fail closed.");
    Assert(parser.Parse(
            ProcessInventoryEntry.Readable(45, "java", "java.exe org.gradle.launcher.GradleMain build"),
            ["C:\\fixture"])
            .Classification == MinecraftProcessClassification.Unrelated,
        "A readable, provably unrelated Java tool must not block migration.");

    var argumentFileParser = new MinecraftCommandLineParser(new DictionaryArgumentFileReader(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["C:\\fixture\\launch.args"] =
                $"cpw.mods.modlauncher.Launcher --accessToken {token} --gameDir C:\\fixture\\target",
        }));
    var fromArgumentFile = argumentFileParser.Parse(
        ProcessInventoryEntry.Readable(46, "javaw", "javaw.exe @C:\\fixture\\launch.args"),
        ["C:\\fixture"]);
    Assert(fromArgumentFile.Classification == MinecraftProcessClassification.Minecraft &&
           fromArgumentFile.ArgumentFileLocations.Count == 1 &&
           fromArgumentFile.GameDirectory == "C:\\fixture\\target" &&
           !fromArgumentFile.ToString().Contains(token, StringComparison.Ordinal),
        "A bounded approved argument file must parse without retaining unrelated secret arguments.");

    var nestedArgumentFile = argumentFileParser.Parse(
        ProcessInventoryEntry.Readable(47, "javaw", "javaw.exe @C:\\fixture\\nested.args"),
        ["C:\\fixture"]);
    Assert(nestedArgumentFile.Classification == MinecraftProcessClassification.UnsafeCandidate,
        "Nested or unreadable argument files must fail closed.");

    var serializedInventory = JsonSerializer.Serialize(ProcessInventorySnapshot.Create([entry]));
    Assert(!serializedInventory.Contains(token, StringComparison.Ordinal),
        "Serialized process diagnostics must never retain a raw command-line secret.");
}

static void ProcessGuardDetectsLateMinecraft()
{
    var inventory = new MutableProcessInventory();
    var targetIdentity = new PhysicalDirectoryIdentity(1, 2, 3);
    var sourceIdentity = new PhysicalDirectoryIdentity(1, 4, 5);
    var evaluator = new MinecraftProcessEvaluator(
        new MinecraftCommandLineParser(new RejectingArgumentFileReader()),
        new FixturePathIdentityResolver(new Dictionary<string, PhysicalDirectoryIdentity>(StringComparer.OrdinalIgnoreCase)
        {
            ["C:\\fixture\\source"] = sourceIdentity,
            ["C:\\fixture\\target"] = targetIdentity,
            ["C:\\fixture\\other"] = new PhysicalDirectoryIdentity(1, 6, 7),
        }));
    inventory.Set(
        ProcessInventoryEntry.Readable(
            50,
            "java",
            "java.exe net.minecraft.client.main.Main --gameDir C:\\fixture\\other"),
        ProcessInventoryEntry.Readable(
            51,
            "Plain Craft Launcher 2",
            "PCL2.exe"));
    using var session = new MinecraftProcessGuardSession(
        inventory,
        evaluator,
        sourceIdentity,
        targetIdentity,
        ["C:\\fixture"],
        CancellationToken.None);
    session.EnsureSafeBeforeMutation(CancellationToken.None);
    inventory.Set(ProcessInventoryEntry.Readable(
        55,
        "javaw",
        "javaw.exe net.minecraft.client.main.Main --gameDir C:\\fixture\\target"));
    AssertThrows<MinecraftProcessBlockedException>(
        () => session.EnsureSafeBeforeMutation(CancellationToken.None),
        "A target Minecraft process appearing before mutation must block.");

    var sourceInventory = new MutableProcessInventory();
    sourceInventory.Set(ProcessInventoryEntry.Readable(
        56,
        "javaw",
        "javaw.exe net.minecraft.client.main.Main --gameDir C:\\fixture\\source"));
    AssertThrows<MinecraftProcessBlockedException>(
        () =>
        {
            using var unexpected = new MinecraftProcessGuardSession(
                sourceInventory,
                evaluator,
                sourceIdentity,
                targetIdentity,
                ["C:\\fixture"],
                CancellationToken.None);
        },
        "A source Minecraft process must block before the guard session starts.");
}

static void PhysicalTargetMutexIsExclusive()
{
    var identity = new PhysicalDirectoryIdentity(0xAABBCCDD, 0x11223344, 0x55667788);
    var factory = new TargetMutexFactory();
    using var first = factory.Acquire(CreateAuthority(identity));
    var secondWasBlocked = Task.Run(() =>
    {
        try
        {
            using var unexpected = factory.Acquire(CreateAuthority(identity));
            return false;
        }
        catch (TargetMutexBusyException)
        {
            return true;
        }
    }).GetAwaiter().GetResult();
    Assert(secondWasBlocked,
        "A second transaction for the same physical target must be rejected.");
    using var other = factory.Acquire(CreateAuthority(
        new PhysicalDirectoryIdentity(identity.VolumeSerialNumber, identity.FileIdLow, identity.FileIdHigh + 1)));
}

static MigrationTransactionCoordinator.ExecutionAuthority CreateAuthority(
    PhysicalDirectoryIdentity targetIdentity)
{
    var sourceIdentity = new PhysicalDirectoryIdentity(
        targetIdentity.VolumeSerialNumber,
        targetIdentity.FileIdLow + 100,
        targetIdentity.FileIdHigh + 100);
    var source = new DiscoveredInstanceChoice(
        CreatePclInstance("source", "C:\\fixture\\source"),
        new VerifiedDirectorySnapshot("C:\\fixture\\source", sourceIdentity, true, false, true),
        "fixture-proof");
    var target = new DiscoveredInstanceChoice(
        CreatePclInstance("target", "C:\\fixture\\target"),
        new VerifiedDirectorySnapshot("C:\\fixture\\target", targetIdentity, true, false, true),
        "fixture-proof");
    var pair = new DiscoveredInstancePair(source, target, 1);
    var acceptedPlan = (AcceptedMigrationPlan)RuntimeHelpers.GetUninitializedObject(
        typeof(AcceptedMigrationPlan));
    return CreateAuthorityFromAcceptedPlan(
        acceptedPlan,
        pair,
        new Dictionary<string, IReadOnlySet<NormalizedRelativePath>>(StringComparer.Ordinal));
}

static MigrationTransactionCoordinator.ExecutionAuthority CreateAuthorityFromAcceptedPlan(
    AcceptedMigrationPlan acceptedPlan,
    DiscoveredInstancePair pair,
    IReadOnlyDictionary<string, IReadOnlySet<NormalizedRelativePath>> currentAllowlists)
{
    var constructor = typeof(MigrationTransactionCoordinator.ExecutionAuthority)
        .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
        .Single();
    var seal = typeof(MigrationTransactionCoordinator)
        .GetField("ExecutionAuthoritySeal", BindingFlags.Static | BindingFlags.NonPublic)?
        .GetValue(null) ?? throw new InvalidOperationException("The coordinator authority seal was unavailable.");
    return (MigrationTransactionCoordinator.ExecutionAuthority)constructor.Invoke(
    [
        seal,
        acceptedPlan,
        pair,
        currentAllowlists,
    ]);
}

static AcceptedMigrationPlan AcceptVanillaPlan(
    TransactionAccessFixture fixture,
    DiscoverySession session,
    ContentAccessLease lease,
    ContentProbeContext context,
    VanillaOptionsAdapter adapter)
{
    var contentPlan = fixture.CreateVanillaPlan(adapter, context, session.Generation);
    var accepted = new AcceptedMigrationPlanFactory(
        fixture.SessionFactory,
        new Dictionary<string, IContentAdapter>(StringComparer.Ordinal)
        {
            [adapter.Id] = adapter,
        }).Create(
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            lease,
            context,
            contentPlan);
    return accepted.IsAccepted && accepted.Plan is not null
        ? accepted.Plan
        : throw new InvalidOperationException("The coordinator fixture could not accept its vanilla plan.");
}

static AcceptedMigrationPlan CreateNestedAcceptedPlan(
    DiscoverySession session,
    ContentAccessLease lease,
    ContentProbeContext context,
    AcceptedMigrationPlan baseline,
    NestedDirectoryFixtureAdapter adapter)
{
    var source = context.Source.Read(
        adapter.Path,
        new ContentReadLimits(1024),
        CancellationToken.None);
    var target = context.Target.Read(
        adapter.Path,
        new ContentReadLimits(1024),
        CancellationToken.None);
    if (!source.Exists || target.Exists ||
        !ContentItemId.TryCreate(adapter.Id, "settings", out var itemId))
    {
        throw new InvalidOperationException("The nested directory fixture could not bind its source and target snapshots.");
    }

    var item = ContentPlanItem.Create(
        itemId,
        PlannedContentDisposition.Add,
        ConflictResolution.Skip,
        "Nested fixture settings");
    var change = PlannedFileChange.Create(
        adapter.Id,
        adapter.Path,
        source,
        target,
        [item]);
    var bytes = source.Bytes.CopyBytes();
    ContentStageResult stage;
    try
    {
        stage = ContentStageResult.Create(
            adapter.Id,
            [StagedFileMutation.Create(change, bytes)]);
    }
    finally
    {
        CryptographicOperations.ZeroMemory(bytes);
    }

    var normalized = NormalizeRequired(adapter.Path.Value);
    var normalizedSet = new BlockFerry.Core.Content.ReadOnlySet<NormalizedRelativePath>(
        [normalized],
        NormalizedRelativePathComparer.Instance);
    return new AcceptedMigrationPlan(
        baseline.AcceptedFingerprint,
        baseline.ContentPlan,
        new Dictionary<string, ContentStageResult>(StringComparer.Ordinal)
        {
            [adapter.Id] = stage,
        },
        new Dictionary<string, IReadOnlySet<NormalizedRelativePath>>(StringComparer.Ordinal)
        {
            [adapter.Id] = normalizedSet,
        },
        normalizedSet,
        baseline.IntegrityDigest,
        session,
        lease,
        context);
}

static AcceptedMigrationPlan CreateMultiPathAcceptedPlan(
    DiscoverySession session,
    ContentAccessLease lease,
    ContentProbeContext context,
    AcceptedMigrationPlan baseline,
    MultiPathEligibilityFixtureAdapter adapter)
{
    var mutations = new List<StagedFileMutation>();
    for (var index = 0; index < adapter.Paths.Count; index++)
    {
        var path = adapter.Paths[index];
        var source = context.Source.Read(path, new ContentReadLimits(1024), CancellationToken.None);
        var target = context.Target.Read(path, new ContentReadLimits(1024), CancellationToken.None);
        if (!source.Exists || target.Exists ||
            !ContentItemId.TryCreate(adapter.Id, $"settings-{index}", out var itemId))
        {
            throw new InvalidOperationException(
                "The multi-path eligibility fixture could not bind its generated source and target snapshots.");
        }

        var item = ContentPlanItem.Create(
            itemId,
            PlannedContentDisposition.Add,
            ConflictResolution.Skip,
            $"Eligibility fixture settings {index}");
        var change = PlannedFileChange.Create(
            adapter.Id,
            path,
            source,
            target,
            [item]);
        var bytes = source.Bytes.CopyBytes();
        try
        {
            mutations.Add(StagedFileMutation.Create(change, bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    var stage = ContentStageResult.Create(adapter.Id, mutations);
    var normalizedSet = new BlockFerry.Core.Content.ReadOnlySet<NormalizedRelativePath>(
        adapter.Paths.Select(path => NormalizeRequired(path.Value)),
        NormalizedRelativePathComparer.Instance);
    return new AcceptedMigrationPlan(
        baseline.AcceptedFingerprint,
        baseline.ContentPlan,
        new Dictionary<string, ContentStageResult>(StringComparer.Ordinal)
        {
            [adapter.Id] = stage,
        },
        new Dictionary<string, IReadOnlySet<NormalizedRelativePath>>(StringComparer.Ordinal)
        {
            [adapter.Id] = normalizedSet,
        },
        normalizedSet,
        baseline.IntegrityDigest,
        session,
        lease,
        context);
}

static MigrationTransactionCoordinator CreateFixtureCoordinator(
    TransactionAccessFixture fixture,
    IContentAdapter adapter,
    FixtureMigrationTransactionRuntimeFactory runtimeFactory,
    IFaultInjector faultInjector,
    out MutableProcessInventory inventory)
{
    inventory = new MutableProcessInventory();
    var guard = new MinecraftProcessGuard(
        inventory,
        new MinecraftProcessEvaluator(
            new MinecraftCommandLineParser(
                new DictionaryArgumentFileReader(
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))),
            new FixturePathIdentityResolver(
                new Dictionary<string, PhysicalDirectoryIdentity>(StringComparer.OrdinalIgnoreCase))));
    return new MigrationTransactionCoordinator(
        fixture.SessionFactory,
        new Dictionary<string, IContentAdapter>(StringComparer.Ordinal)
        {
            [adapter.Id] = adapter,
        },
        runtimeFactory,
        guard,
        new TargetMutexFactory(),
        faultInjector,
        new FixtureRandomSource());
}

static TransactionRecoveryService CreateFixtureRecovery(
    ITransactionStoreProvider stores,
    IFileSystemCapability fileSystem,
    out MutableProcessInventory inventory,
    TargetMutexFactory? mutexFactory = null,
    ITransactionRaceBoundaryHook? raceBoundaryHook = null,
    IReadOnlyDictionary<string, IContentAdapter>? adapters = null)
{
    inventory = new MutableProcessInventory();
    var guard = new MinecraftProcessGuard(
        inventory,
        new MinecraftProcessEvaluator(
            new MinecraftCommandLineParser(
                new DictionaryArgumentFileReader(
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))),
            new FixturePathIdentityResolver(
                new Dictionary<string, PhysicalDirectoryIdentity>(StringComparer.OrdinalIgnoreCase))));
    var currentAdapters = adapters ?? new Dictionary<string, IContentAdapter>(StringComparer.Ordinal)
    {
        ["vanilla"] = new VanillaOptionsAdapter(new Pcl2OptionsMigrationPreviewer(fileSystem)),
        ["appearance"] = new DarkModeEverywhereAdapter(),
        ["jei"] = new JeiBookmarksAdapter(),
        ["esm"] = new ExtremeSoundMufflerAdapter(),
    };
    var sessionFactory = new DiscoverySessionFactory();
    var authorizationResolver = new RecoveryAuthorizationResolver(
        new InstanceCandidateResolver(fileSystem),
        new Pcl2InstanceDiscovery(fileSystem),
        sessionFactory,
        new RecoveryCatalogContextFactory(fileSystem, new ModPresenceProbe()),
        currentAdapters);
    return new TransactionRecoveryService(
        stores,
        fileSystem,
        guard,
        mutexFactory ?? new TargetMutexFactory(),
        new FixtureRandomSource(),
        authorizationResolver,
        raceBoundaryHook);
}

static void AssertPendingRollbackIntent(FixtureMigrationTransactionRuntimeFactory runtimeFactory)
{
    using var reopened = runtimeFactory.Open(
        runtimeFactory.Storages.Single().TransactionId,
        CancellationToken.None);
    var journal = reopened.Journal.ReadAndVerify(reopened.TransactionId, CancellationToken.None);
    Assert(!journal.IsTerminal && journal.Records[^1].Kind == TransactionRecordKind.RollbackIntent,
        "A raced cleanup must retain its authenticated pending rollback intent without an illegal terminal record.");
}

static Pcl2Instance CreatePclInstance(string id, string root) => new(
    Id: id,
    DisplayName: id,
    MinecraftRoot: "C:\\fixture",
    InstanceRoot: root,
    GameRoot: root,
    InstanceJsonPath: null,
    SetupPath: Path.Combine(root, "PCL", "Setup.ini"),
    Isolation: Pcl2IsolationMode.Isolated,
    MinecraftVersion: "1.21.1",
    ModLoaders: [],
    ModpackIdentity: new Pcl2ModpackIdentity(
        "Fixture",
        "1",
        Pcl2IdentityConfidence.High,
        Pcl2IdentitySource.Manifest,
        "fixture"),
    HasUsableVersionMetadata: true,
    IsSelected: false,
    Diagnostics: []);

static string ReadCase(string[] arguments)
{
    for (var index = 0; index < arguments.Length - 1; index++)
    {
        if (arguments[index] == "--case")
        {
            return arguments[index + 1];
        }
    }

    return "all";
}

static string FindProductionRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (Directory.Exists(Path.Combine(current.FullName, "src", "BlockFerry.Core")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    throw new InvalidOperationException("Could not locate the production source root for static authority scanning.");
}

static void AssertTruthfulSuccessfulProgress(
    IReadOnlyList<MigrationProgress> observed,
    int expectedMutationCount,
    string scenario)
{
    var expectedTotal = checked((expectedMutationCount * 4) + 3);
    var indeterminate = observed
        .TakeWhile(progress => progress.TotalSteps <= 0)
        .ToArray();
    var determinate = observed
        .Skip(indeterminate.Length)
        .ToArray();

    Assert(indeterminate.Length >= 3 &&
           indeterminate.All(progress =>
               progress.CompletedSteps == 0 &&
               progress.TotalSteps <= 0),
        $"Progress/{scenario}: preflight and target-stability waits must be indeterminate.");
    Assert(determinate.Length > 0 &&
           determinate.All(progress =>
               progress.TotalSteps == expectedTotal &&
               progress.CompletedSteps >= 1 &&
               progress.CompletedSteps <= progress.TotalSteps),
        $"Progress/{scenario}: determinate work must use one stable truthful ledger.");
    Assert(determinate
               .Select(progress => progress.CompletedSteps)
               .Distinct()
               .SequenceEqual(Enumerable.Range(1, expectedTotal)),
        $"Progress/{scenario}: every real 4N+3 work unit must be observable without a terminal jump.");
    Assert(determinate
               .Zip(determinate.Skip(1))
               .All(pair => pair.Second.CompletedSteps >= pair.First.CompletedSteps),
        $"Progress/{scenario}: completed work must be monotonic.");
    Assert(determinate
               .Take(determinate.Length - 1)
               .All(progress => progress.CompletedSteps < progress.TotalSteps) &&
           determinate[^1].Stage == MigrationProgressStage.Completed &&
           determinate[^1].CompletedSteps == expectedTotal,
        $"Progress/{scenario}: 100% must occur only at the final completed notification.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static void AssertThrowsAny<TFirst, TSecond>(Action action, string message)
    where TFirst : Exception
    where TSecond : Exception
{
    try
    {
        action();
    }
    catch (Exception exception) when (exception is TFirst or TSecond)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static void AddFabricModFixture(
    TransactionAccessFixture fixture,
    bool source,
    string modId,
    string version)
{
    var instanceRoot = source ? fixture.SourceRootPath : fixture.TargetRootPath;
    var jarPath = Path.Combine(instanceRoot, "mods", modId + "-fixture.jar");
    var relativeJarPath = Path.GetRelativePath(fixture.Sandbox.RootPath, jarPath);
    var metadata = Encoding.UTF8.GetBytes(
        $"{{\"schemaVersion\":1,\"id\":\"{modId}\",\"version\":\"{version}\",\"name\":\"Fixture\"}}");
    fixture.Sandbox.CreateZip(relativeJarPath, ("fabric.mod.json", metadata));
}

internal sealed class RejectingArgumentFileReader : IMinecraftArgumentFileReader
{
    public bool TryRead(
        string path,
        IReadOnlyList<string> approvedRoots,
        out string content)
    {
        content = string.Empty;
        return false;
    }
}

internal sealed class MemoryTransactionStorageDirectory(
    TransactionId transactionId) : ITransactionStorageDirectory
{
    private readonly object gate = new();
    private readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
    private readonly HashSet<string> directories = new(StringComparer.Ordinal);
    private int mutationCount;

    public TransactionId TransactionId { get; } = transactionId;

    public long GetAvailableBytes(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return long.MaxValue;
    }

    internal int? FailAfterMutationCount { get; init; }

    internal bool FailNextAppend { get; set; }

    internal IReadOnlyList<string> Names => ListNames(CancellationToken.None);

    public IReadOnlyList<string> ListNames(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return directories.Concat(files.Keys).Order(StringComparer.Ordinal).ToArray();
        }
    }

    public void CreateDirectory(string opaqueName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            BeforeMutation();
            if (!directories.Add(opaqueName) || files.ContainsKey(opaqueName))
            {
                throw new IOException("The fixture directory already exists.");
            }
        }
    }

    public void CreateNewFile(
        string opaqueName,
        ReadOnlySpan<byte> bytes,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length > maximumBytes)
        {
            throw new IOException("The fixture file exceeded its bound.");
        }

        lock (gate)
        {
            BeforeMutation();
            if (files.ContainsKey(opaqueName) || directories.Contains(opaqueName))
            {
                throw new IOException("The fixture file already exists.");
            }

            files.Add(opaqueName, bytes.ToArray());
        }
    }

    public void AppendAndFlush(
        string opaqueName,
        ReadOnlySpan<byte> bytes,
        int maximumTotalBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (FailNextAppend)
            {
                FailNextAppend = false;
                throw new IOException("The fixture rejected the append before it became durable.");
            }

            BeforeMutation();
            if (!files.TryGetValue(opaqueName, out var current) ||
                bytes.Length > maximumTotalBytes - current.Length)
            {
                throw new IOException("The fixture append exceeded its bound.");
            }

            var combined = new byte[current.Length + bytes.Length];
            current.CopyTo(combined, 0);
            bytes.CopyTo(combined.AsSpan(current.Length));
            files[opaqueName] = combined;
        }
    }

    public byte[] ReadFile(
        string opaqueName,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!files.TryGetValue(opaqueName, out var bytes) || bytes.Length > maximumBytes)
            {
                throw new IOException("The fixture file was missing or oversized.");
            }

            return bytes.ToArray();
        }
    }

    public void CreateNewFileInDirectory(
        string directoryName,
        string opaqueName,
        ReadOnlySpan<byte> bytes,
        int maximumBytes,
        CancellationToken cancellationToken) =>
        CreateNewFile(
            directoryName + "/" + opaqueName,
            bytes,
            maximumBytes,
            cancellationToken);

    public byte[] ReadFileInDirectory(
        string directoryName,
        string opaqueName,
        int maximumBytes,
        CancellationToken cancellationToken) =>
        ReadFile(directoryName + "/" + opaqueName, maximumBytes, cancellationToken);

    public void DeleteBootstrapArtifacts(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            files.Clear();
            directories.Clear();
        }
    }

    internal byte[] ReadForTest(string name) => ReadFile(name, int.MaxValue, CancellationToken.None);

    internal void OverwriteForTest(string name, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        lock (gate)
        {
            if (!files.ContainsKey(name))
            {
                throw new IOException("The fixture file does not exist.");
            }

            files[name] = bytes.ToArray();
        }
    }

    public void Dispose()
    {
    }

    private void BeforeMutation()
    {
        mutationCount++;
        if (FailAfterMutationCount is { } failAt && mutationCount >= failAt)
        {
            throw new IOException("The fixture injected a bootstrap persistence failure.");
        }
    }
}

internal sealed class FixtureProtectedData : IProtectedData
{
    private static readonly byte[] Key = SHA256.HashData("BlockFerry transaction fixture protector"u8);

    public byte[] Protect(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> entropy,
        int maximumOutputBytes)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, entropy);
        var result = new byte[checked(nonce.Length + ciphertext.Length + tag.Length)];
        nonce.CopyTo(result, 0);
        ciphertext.CopyTo(result, nonce.Length);
        tag.CopyTo(result, nonce.Length + ciphertext.Length);
        if (result.Length > maximumOutputBytes)
        {
            CryptographicOperations.ZeroMemory(result);
            throw new ProtectedDataLimitException("The fixture ciphertext exceeded its bound.");
        }

        return result;
    }

    public byte[] Unprotect(
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> entropy,
        int maximumOutputBytes)
    {
        if (ciphertext.Length < 28 || ciphertext.Length - 28 > maximumOutputBytes)
        {
            throw new CryptographicException("The fixture ciphertext length was invalid.");
        }

        var plaintext = new byte[ciphertext.Length - 28];
        using var aes = new AesGcm(Key, 16);
        aes.Decrypt(
            ciphertext[..12],
            ciphertext.Slice(12, plaintext.Length),
            ciphertext[^16..],
            plaintext,
            entropy);
        return plaintext;
    }
}

internal sealed class DictionaryArgumentFileReader(
    IReadOnlyDictionary<string, string> values) : IMinecraftArgumentFileReader
{
    public bool TryRead(
        string path,
        IReadOnlyList<string> approvedRoots,
        out string content)
    {
        content = string.Empty;
        if (!values.TryGetValue(path, out var found) ||
            !approvedRoots.Any(root => path.StartsWith(
                Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        content = found;
        return true;
    }
}

internal sealed class FixturePathIdentityResolver(
    IReadOnlyDictionary<string, PhysicalDirectoryIdentity> identities) : IProcessPathIdentityResolver
{
    public bool TryResolve(string path, out PhysicalDirectoryIdentity identity) =>
        identities.TryGetValue(path, out identity);
}

internal sealed class MutableProcessInventory : IProcessInventory, IProcessMonitor
{
    private ProcessInventorySnapshot snapshot = ProcessInventorySnapshot.Create([]);

    public event EventHandler? InventoryChanged;

    internal int CaptureCount { get; private set; }

    public ProcessInventorySnapshot Capture(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CaptureCount++;
        return snapshot;
    }

    public IProcessMonitor StartMonitor() => this;

    public void Set(params ProcessInventoryEntry[] entries)
    {
        snapshot = ProcessInventorySnapshot.Create(entries);
        InventoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
    }
}

internal sealed class CountingAdapter(IContentAdapter inner) : IContentAdapter
{
    public string Id => inner.Id;

    public int StageCount { get; private set; }

    public int RegenerateCount { get; private set; }

    public bool ReturnEmptyAllowlist { get; set; }

    public ContentProbeResult Probe(
        ContentProbeContext context,
        CancellationToken cancellationToken) =>
        inner.Probe(context, cancellationToken);

    public ContentCatalog BuildCatalog(
        ContentProbeContext context,
        CancellationToken cancellationToken) =>
        inner.BuildCatalog(context, cancellationToken);

    public ContentAdapterPlan Plan(
        ContentProbeContext context,
        ContentCatalog catalog,
        ValidatedContentSelection selection,
        CancellationToken cancellationToken) =>
        inner.Plan(context, catalog, selection, cancellationToken);

    public ContentStageResult Stage(
        ContentAdapterPlan plan,
        CancellationToken cancellationToken)
    {
        StageCount++;
        return inner.Stage(plan, cancellationToken);
    }

    public ContentVerificationResult Verify(
        ContentStageResult staged,
        IReadOnlyList<ContentFileSnapshot> pathBoundRereads,
        CancellationToken cancellationToken) =>
        inner.Verify(staged, pathBoundRereads, cancellationToken);

    public IReadOnlySet<ContentRelativePath> RegenerateAllowedPaths(
        ContentProbeContext context,
        CancellationToken cancellationToken)
    {
        RegenerateCount++;
        return ReturnEmptyAllowlist
            ? new HashSet<ContentRelativePath>()
            : inner.RegenerateAllowedPaths(context, cancellationToken);
    }

    public IReadOnlySet<ContentRelativePath> RegenerateRecoveryAllowedPaths(
        RecoveryCatalogContext context,
        IReadOnlySet<ContentRelativePath> storedCandidatePaths,
        CancellationToken cancellationToken) =>
        inner.RegenerateRecoveryAllowedPaths(context, storedCandidatePaths, cancellationToken);
}

internal sealed class NestedDirectoryFixtureAdapter : IContentAdapter
{
    internal NestedDirectoryFixtureAdapter(
        string relativePath,
        string id = "directory-fixture")
    {
        if (!ContentRelativePath.TryCreate(relativePath, out var path, out _) || path is null)
        {
            throw new ArgumentException("A valid nested fixture path is required.", nameof(relativePath));
        }

        Path = path;
        Id = string.IsNullOrWhiteSpace(id)
            ? throw new ArgumentException("A fixture adapter ID is required.", nameof(id))
            : id;
    }

    public string Id { get; }

    internal ContentRelativePath Path { get; }

    public ContentProbeResult Probe(
        ContentProbeContext context,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ContentCatalog BuildCatalog(
        ContentProbeContext context,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ContentAdapterPlan Plan(
        ContentProbeContext context,
        ContentCatalog catalog,
        ValidatedContentSelection selection,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ContentStageResult Stage(
        ContentAdapterPlan plan,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ContentVerificationResult Verify(
        ContentStageResult staged,
        IReadOnlyList<ContentFileSnapshot> pathBoundRereads,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var valid = staged.Mutations.Count == 1 &&
                    pathBoundRereads.Count == 1 &&
                    pathBoundRereads[0].Exists &&
                    pathBoundRereads[0].RelativePath.Equals(Path) &&
                    string.Equals(
                        pathBoundRereads[0].Sha256,
                        staged.Mutations[0].AfterBytes.Sha256,
                        StringComparison.Ordinal);
        return ContentVerificationResult.Create(valid, []);
    }

    public IReadOnlySet<ContentRelativePath> RegenerateAllowedPaths(
        ContentProbeContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new HashSet<ContentRelativePath> { Path };
    }

    public IReadOnlySet<ContentRelativePath> RegenerateRecoveryAllowedPaths(
        RecoveryCatalogContext context,
        IReadOnlySet<ContentRelativePath> storedCandidatePaths,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.ThrowIfUnavailable();
        return storedCandidatePaths.Contains(Path)
            ? new HashSet<ContentRelativePath> { Path }
            : new HashSet<ContentRelativePath>();
    }
}

internal sealed class MultiPathEligibilityFixtureAdapter : IContentAdapter
{
    internal MultiPathEligibilityFixtureAdapter(IEnumerable<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        var paths = relativePaths.Select(relativePath =>
        {
            if (!ContentRelativePath.TryCreate(relativePath, out var path, out _) || path is null)
            {
                throw new ArgumentException(
                    "Every eligibility fixture path must be a safe content-relative path.",
                    nameof(relativePaths));
            }

            return path;
        }).ToArray();
        if (paths.Length != 2)
        {
            throw new ArgumentException(
                "The multi-path eligibility fixture requires exactly two paths.",
                nameof(relativePaths));
        }

        Paths = Array.AsReadOnly(paths);
    }

    public string Id => "eligibility-fixture";

    internal IReadOnlyList<ContentRelativePath> Paths { get; }

    public ContentProbeResult Probe(
        ContentProbeContext context,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ContentCatalog BuildCatalog(
        ContentProbeContext context,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ContentAdapterPlan Plan(
        ContentProbeContext context,
        ContentCatalog catalog,
        ValidatedContentSelection selection,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ContentStageResult Stage(
        ContentAdapterPlan plan,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ContentVerificationResult Verify(
        ContentStageResult staged,
        IReadOnlyList<ContentFileSnapshot> pathBoundRereads,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rereads = pathBoundRereads.ToDictionary(
            reread => reread.RelativePath);
        var valid = staged.Mutations.Count == Paths.Count &&
                    rereads.Count == Paths.Count &&
                    staged.Mutations.All(mutation =>
                        rereads.TryGetValue(mutation.Change.RelativePath, out var reread) &&
                        reread.Exists &&
                        string.Equals(
                            reread.Sha256,
                            mutation.AfterBytes.Sha256,
                            StringComparison.Ordinal));
        return ContentVerificationResult.Create(valid, []);
    }

    public IReadOnlySet<ContentRelativePath> RegenerateAllowedPaths(
        ContentProbeContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new HashSet<ContentRelativePath>(Paths);
    }

    public IReadOnlySet<ContentRelativePath> RegenerateRecoveryAllowedPaths(
        RecoveryCatalogContext context,
        IReadOnlySet<ContentRelativePath> storedCandidatePaths,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var allowed = new HashSet<ContentRelativePath>(Paths);
        return storedCandidatePaths.All(allowed.Contains)
            ? allowed
            : new HashSet<ContentRelativePath>();
    }
}

internal sealed class UndoEligibilityChurnHook(
    string rootPath,
    string firstPath,
    byte[] mutationBytes) : ITransactionRaceBoundaryHook
{
    private readonly byte[] mutationBytes = mutationBytes?.ToArray() ??
        throw new ArgumentNullException(nameof(mutationBytes));

    internal string FirstPath { get; } = Path.GetFullPath(firstPath);

    internal string RootPath { get; } = Path.GetFullPath(rootPath);

    internal int EligibilityRetainedHitCount { get; private set; }

    internal bool MutationBlocked { get; private set; }

    internal bool MutationSucceeded { get; private set; }

    internal bool DeletionBlocked { get; private set; }

    internal bool DeletionSucceeded { get; private set; }

    internal bool RootDeletionBlocked { get; private set; }

    internal bool RootDeletionSucceeded { get; private set; }

    public void Hit(TransactionRaceBoundary boundary, string finalPath)
    {
        if (!string.Equals(
                boundary.ToString(),
                "UndoEligibilityPathRetained",
                StringComparison.Ordinal))
        {
            return;
        }

        EligibilityRetainedHitCount++;
        if (EligibilityRetainedHitCount != 2)
        {
            return;
        }

        try
        {
            using var writer = File.OpenHandle(
                FirstPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.WriteThrough);
            RandomAccess.Write(writer, mutationBytes, 0);
            RandomAccess.FlushToDisk(writer);
            MutationSucceeded = true;
        }
        catch (IOException)
        {
            MutationBlocked = true;
        }

        var displaced = FirstPath + ".eligibility-churn";
        try
        {
            File.Move(FirstPath, displaced);
            DeletionSucceeded = true;
            File.Move(displaced, FirstPath);
        }
        catch (IOException)
        {
            DeletionBlocked = true;
        }

        var displacedRoot = RootPath + ".eligibility-churn";
        try
        {
            Directory.Move(RootPath, displacedRoot);
            RootDeletionSucceeded = true;
            Directory.Move(displacedRoot, RootPath);
        }
        catch (IOException)
        {
            RootDeletionBlocked = true;
        }
    }
}

internal sealed class FixtureMigrationTransactionRuntimeFactory(
    IFileSystemCapability fileSystem,
    ITransactionRaceBoundaryHook? raceBoundaryHook = null) :
    IMigrationTransactionRuntimeFactory,
    ITransactionStoreProvider
{
    private readonly IFileSystemCapability fileSystem =
        fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    internal FixtureProtectedData ProtectedData { get; } = new();

    internal List<MemoryTransactionStorageDirectory> Storages { get; } = [];

    internal int CreateCount { get; private set; }

    IProtectedData ITransactionStoreProvider.ProtectedData => ProtectedData;

    public IReadOnlyList<TransactionId> List(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Storages.Select(storage => storage.TransactionId).ToArray();
    }

    public AuthenticatedTransactionStore Open(
        TransactionId transactionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var storage = Storages.SingleOrDefault(candidate => candidate.TransactionId == transactionId) ??
            throw new DirectoryNotFoundException("The fixture transaction does not exist.");
        return AuthenticatedTransactionStore.Open(storage, ProtectedData, cancellationToken);
    }

    public AuthenticatedTransactionStore Create(
        RecoveryLocator locator,
        StoredMigrationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        var storage = new MemoryTransactionStorageDirectory(locator.TransactionId);
        Storages.Add(storage);
        return AuthenticatedTransactionStore.Bootstrap(
            storage,
            ProtectedData,
            locator,
            plan,
            cancellationToken);
    }

    public MigrationTransactionRuntime Create(
        TransactionId transactionId,
        AcceptedMigrationPlan plan,
        DiscoveredInstancePair currentPair,
        CancellationToken cancellationToken)
    {
        CreateCount++;
        var storage = new MemoryTransactionStorageDirectory(transactionId);
        Storages.Add(storage);
        var store = AuthenticatedTransactionStore.Bootstrap(
            storage,
            ProtectedData,
            RecoveryLocator.Create(
                transactionId,
                plan.TargetInstanceId,
                currentPair.Target.GameRoot.CanonicalPath,
                currentPair.Target.GameRoot.Identity),
            StoredMigrationPlan.Create(
                transactionId,
                plan.IntegrityDigest,
                plan.AdapterStages
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .SelectMany(pair => pair.Value.Mutations)
                    .Select(mutation => StoredPlanPath.Create(
                        mutation.Change.AdapterId,
                        NormalizeForRuntime(mutation.Change.RelativePath),
                        ConflictResolution.UseSource,
                        mutation.Change.TargetSnapshot.Exists,
                        mutation.Change.TargetSnapshot.Sha256,
                        afterExists: true,
                        mutation.AfterBytes.Sha256))),
            cancellationToken);
        var backups = new BackupStore(store, ProtectedData);
        return new MigrationTransactionRuntime(
            store,
            backups,
            new WindowsTransactionFileOperations(fileSystem, backups, raceBoundaryHook));
    }

    private static NormalizedRelativePath NormalizeForRuntime(ContentRelativePath path) =>
        WritePathGuard.TryNormalize(path, out var normalized) && normalized is not null
            ? normalized
            : throw new InvalidOperationException("The fixture runtime received an unsafe path.");
}

internal enum FixtureRaceMutation
{
    ContentWrite,
    Replacement,
}

internal enum DirectoryCrashBoundary
{
    NamespaceCreated,
    CreatedRecordDurableBeforePersistence,
}

internal sealed class FixtureTransactionRaceBoundaryHook(
    TransactionRaceBoundary expectedBoundary,
    FixtureRaceMutation mutation,
    byte[] raceBytes) : ITransactionRaceBoundaryHook
{
    private int hitCount;

    internal int HitCount => Volatile.Read(ref hitCount);

    internal string? AffectedPath { get; private set; }

    internal bool MutationVerified { get; private set; }

    internal DateTime AffectedLastWriteTimeUtc { get; private set; }

    internal FixtureSupportedFileState? AffectedState { get; private set; }

    public void Hit(TransactionRaceBoundary boundary, string finalPath)
    {
        if (boundary != expectedBoundary || Interlocked.Exchange(ref hitCount, 1) != 0)
        {
            return;
        }

        AffectedPath = finalPath;
        AffectedLastWriteTimeUtc = FixtureRaceMutationApplier.Apply(
            finalPath,
            mutation,
            raceBytes);
        AffectedState = FixtureSupportedFileStateCapture.Capture(finalPath);
        MutationVerified = File.ReadAllBytes(finalPath).SequenceEqual(raceBytes);
    }
}

internal sealed class FixtureMetadataOnlyRaceBoundaryHook(
    TransactionRaceBoundary expectedBoundary) : ITransactionRaceBoundaryHook
{
    private int hitCount;

    internal int HitCount => Volatile.Read(ref hitCount);

    internal FileAttributes ChangedAttributes { get; private set; }

    public void Hit(TransactionRaceBoundary boundary, string finalPath)
    {
        if (boundary != expectedBoundary || Interlocked.Exchange(ref hitCount, 1) != 0)
        {
            return;
        }

        var original = File.GetAttributes(finalPath);
        ChangedAttributes = original ^ FileAttributes.Hidden;
        File.SetAttributes(finalPath, ChangedAttributes);
        if (File.GetAttributes(finalPath) != ChangedAttributes)
        {
            throw new IOException("The fixture filesystem did not retain the metadata-only attribute race.");
        }
    }
}

internal sealed class ThrowingTransactionRaceBoundaryHook(
    TransactionRaceBoundary expectedBoundary) : ITransactionRaceBoundaryHook
{
    private int hitCount;

    internal int HitCount => Volatile.Read(ref hitCount);

    public void Hit(TransactionRaceBoundary boundary, string finalPath)
    {
        if (boundary == expectedBoundary && Interlocked.Increment(ref hitCount) == 1)
        {
            throw new IOException($"Injected transaction boundary failure at {boundary}.");
        }
    }
}

internal sealed record FixtureRaceStep(
    TransactionRaceBoundary Boundary,
    FixtureRaceMutation? Mutation = null,
    byte[]? RaceBytes = null,
    bool ThrowAfter = false,
    bool ExpectMutationBlocked = false,
    bool HoldWriter = false);

internal sealed record FixtureRaceResult(
    TransactionRaceBoundary Boundary,
    string AffectedPath,
    bool MutationBlocked,
    DateTime? LastWriteTimeUtc);

internal sealed class ScriptedTransactionRaceBoundaryHook(params FixtureRaceStep[] steps)
    : ITransactionRaceBoundaryHook, IDisposable
{
    private readonly Queue<FixtureRaceStep> pending = new(steps);
    private readonly List<FixtureRaceResult> results = [];
    private readonly List<SafeFileHandle> retainedWriters = [];

    internal IReadOnlyList<FixtureRaceResult> Results => results;

    public void Hit(TransactionRaceBoundary boundary, string finalPath)
    {
        if (pending.Count == 0 || pending.Peek().Boundary != boundary)
        {
            return;
        }

        var step = pending.Dequeue();
        var mutationBlocked = false;
        DateTime? lastWriteTimeUtc = null;
        if (step.HoldWriter)
        {
            retainedWriters.Add(File.OpenHandle(
                finalPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete));
        }

        if (step.Mutation is { } mutation && step.RaceBytes is { } raceBytes)
        {
            try
            {
                lastWriteTimeUtc = FixtureRaceMutationApplier.Apply(finalPath, mutation, raceBytes);
            }
            catch (IOException) when (step.ExpectMutationBlocked)
            {
                mutationBlocked = true;
            }
        }

        results.Add(new FixtureRaceResult(
            boundary,
            finalPath,
            mutationBlocked,
            lastWriteTimeUtc));
        if (step.ThrowAfter)
        {
            throw new IOException($"Injected fixture failure at {boundary}.");
        }
    }

    public void Dispose()
    {
        foreach (var writer in retainedWriters)
        {
            writer.Dispose();
        }
    }
}

internal static class FixtureRaceMutationApplier
{
    private static readonly DateTime RaceCreationTimeUtc =
        new(2038, 12, 1, 2, 3, 4, DateTimeKind.Utc);
    private static readonly DateTime RaceLastAccessTimeUtc =
        new(2039, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime RaceLastWriteTimeUtc =
        new(2040, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    internal static DateTime Apply(
        string finalPath,
        FixtureRaceMutation mutation,
        byte[] raceBytes)
    {
        if (mutation == FixtureRaceMutation.ContentWrite)
        {
            using var raceHandle = File.OpenHandle(
                finalPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.WriteThrough);
            RandomAccess.SetLength(raceHandle, raceBytes.Length);
            RandomAccess.Write(raceHandle, raceBytes, 0);
            RandomAccess.FlushToDisk(raceHandle);
        }
        else
        {
            var raceSource = Path.Combine(
                Path.GetDirectoryName(finalPath) ?? throw new IOException("The race target had no parent."),
                $".fixture-race-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(raceSource, raceBytes);
            File.Replace(raceSource, finalPath, destinationBackupFileName: null);
        }

        File.SetCreationTimeUtc(finalPath, RaceCreationTimeUtc);
        File.SetLastAccessTimeUtc(finalPath, RaceLastAccessTimeUtc);
        File.SetLastWriteTimeUtc(finalPath, RaceLastWriteTimeUtc);
        File.SetAttributes(finalPath, File.GetAttributes(finalPath) ^ FileAttributes.Hidden);
        return File.GetLastWriteTimeUtc(finalPath);
    }
}

internal sealed record FixtureSupportedFileState(
    PhysicalFileIdentity Identity,
    byte[] Bytes,
    DateTimeOffset CreationTimeUtc,
    DateTimeOffset LastAccessTimeUtc,
    DateTimeOffset LastWriteTimeUtc,
    FileAttributes Attributes,
    uint LinkCount,
    byte[] SecurityDescriptor,
    IReadOnlyList<string> StreamNames);

internal readonly record struct FixtureHandleWriteProbe(bool Succeeded, int Error);

internal static class FixtureHandleAccessProbe
{
    internal static FixtureHandleWriteProbe TrySetLastWriteTime(
        SafeFileHandle handle,
        DateTimeOffset lastWriteTimeUtc)
    {
        var value = lastWriteTimeUtc.ToFileTime();
        var writeTime = new NativeFileTime(
            unchecked((uint)value),
            unchecked((uint)(value >> 32)));
        var succeeded = SetFileTime(
            handle,
            IntPtr.Zero,
            IntPtr.Zero,
            ref writeTime);
        return new FixtureHandleWriteProbe(
            succeeded,
            succeeded ? 0 : Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeFileTime(uint LowDateTime, uint HighDateTime);

    [DllImport("kernel32.dll", EntryPoint = "SetFileTime", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileTime(
        SafeFileHandle file,
        IntPtr creationTime,
        IntPtr lastAccessTime,
        ref NativeFileTime lastWriteTime);
}

internal static class FixtureSecurityDescriptor
{
    private const uint SecurityDescriptorRevision = 1;
    private const ushort SecurityDescriptorDaclPresent = 0x0004;
    private const ushort SecurityDescriptorDaclProtected = 0x1000;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;

    internal static void SetCurrentUserOwner(string path)
    {
        var currentSid = WindowsIdentity.GetCurrent().User?.Value ??
            throw new InvalidOperationException("The fixture current-user SID was unavailable.");
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                $"O:{currentSid}",
                SecurityDescriptorRevision,
                out var descriptor,
                out _))
        {
            throw new IOException(
                $"The fixture owner descriptor could not be created (Windows error {Marshal.GetLastWin32Error()}).");
        }

        try
        {
            if (!GetSecurityDescriptorOwner(
                    descriptor,
                    out var owner,
                    out _) ||
                owner == IntPtr.Zero)
            {
                throw new IOException(
                    $"The fixture owner descriptor could not be read (Windows error {Marshal.GetLastWin32Error()}).");
            }

            var status = SetNamedSecurityInfo(
                path,
                1,
                OwnerSecurityInformation,
                owner,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
            if (status != 0)
            {
                throw new IOException(
                    $"The fixture current-user owner could not be applied (Windows error {status}).");
            }
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    internal static void SetCurrentUserOwnerTree(string rootPath)
    {
        foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            SetCurrentUserOwner(file);
        }

        foreach (var directory in Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories))
        {
            SetCurrentUserOwner(directory);
        }

        SetCurrentUserOwner(rootPath);
    }

    internal static void SetDistinctProtectedDacl(string path)
    {
        var currentSid = WindowsIdentity.GetCurrent().User?.Value ??
            throw new InvalidOperationException("The fixture current-user SID was unavailable.");
        var sddl = $"D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;{currentSid})(A;;GR;;;BU)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                sddl,
                SecurityDescriptorRevision,
                out var descriptor,
                out _))
        {
            throw new IOException(
                $"The fixture custom DACL could not be created (Windows error {Marshal.GetLastWin32Error()}).");
        }

        try
        {
            if (!GetSecurityDescriptorDacl(
                    descriptor,
                    out var daclPresent,
                    out var dacl,
                    out _) ||
                !daclPresent ||
                dacl == IntPtr.Zero)
            {
                throw new IOException(
                    $"The fixture custom DACL could not be read (Windows error {Marshal.GetLastWin32Error()}).");
            }

            var status = SetNamedSecurityInfo(
                path,
                1,
                DaclSecurityInformation | ProtectedDaclSecurityInformation,
                IntPtr.Zero,
                IntPtr.Zero,
                dacl,
                IntPtr.Zero);
            if (status != 0)
            {
                throw new IOException(
                    $"The fixture custom DACL could not be applied (Windows error {status}).");
            }
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    internal static bool DaclAndProtectionEqual(byte[] left, byte[] right)
    {
        var leftControl = ReadControl(left);
        var rightControl = ReadControl(right);
        return (leftControl & (SecurityDescriptorDaclPresent | SecurityDescriptorDaclProtected)) ==
               (rightControl & (SecurityDescriptorDaclPresent | SecurityDescriptorDaclProtected)) &&
               ReadDacl(left).SequenceEqual(ReadDacl(right));
    }

    private static ushort ReadControl(byte[] descriptor)
    {
        RequireHeader(descriptor);
        return BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(2, sizeof(ushort)));
    }

    private static ReadOnlySpan<byte> ReadDacl(byte[] descriptor)
    {
        RequireHeader(descriptor);
        var offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            descriptor.AsSpan(16, sizeof(uint))));
        if (offset == 0)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        if (offset > descriptor.Length - 8)
        {
            throw new IOException("The fixture security descriptor DACL was malformed.");
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(
            descriptor.AsSpan(offset + 2, sizeof(ushort)));
        if (length < 8 || length > descriptor.Length - offset)
        {
            throw new IOException("The fixture security descriptor DACL exceeded its bound.");
        }

        return descriptor.AsSpan(offset, length);
    }

    private static void RequireHeader(byte[] descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Length < 20 || descriptor[0] != 1)
        {
            throw new IOException("The fixture security descriptor header was malformed.");
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string securityDescriptor,
        uint revision,
        out IntPtr descriptor,
        out uint descriptorSize);

    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorDacl", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorDacl(
        IntPtr securityDescriptor,
        [MarshalAs(UnmanagedType.Bool)] out bool daclPresent,
        out IntPtr dacl,
        [MarshalAs(UnmanagedType.Bool)] out bool daclDefaulted);

    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorOwner", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorOwner(
        IntPtr securityDescriptor,
        out IntPtr owner,
        [MarshalAs(UnmanagedType.Bool)] out bool ownerDefaulted);

    [DllImport("advapi32.dll", EntryPoint = "SetNamedSecurityInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint SetNamedSecurityInfo(
        string objectName,
        int objectType,
        uint securityInformation,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}

internal static class FixtureSupportedFileStateCapture
{
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint GroupSecurityInformation = 0x00000002;
    private const uint DaclSecurityInformation = 0x00000004;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    internal static FixtureSupportedFileState Capture(string path)
    {
        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new IOException(
                $"The fixture could not capture file information (Windows error {Marshal.GetLastWin32Error()}).");
        }

        if (!GetFileInformationByHandleEx(
                handle,
                18,
                out var fileId,
                checked((uint)Marshal.SizeOf<FileIdInfo>())))
        {
            throw new IOException(
                $"The fixture could not capture file identity (Windows error {Marshal.GetLastWin32Error()}).");
        }

        return new FixtureSupportedFileState(
            new PhysicalFileIdentity(
                fileId.VolumeSerialNumber,
                fileId.FileId.LowPart,
                fileId.FileId.HighPart),
            File.ReadAllBytes(path),
            DateTimeOffset.FromFileTime(Combine(information.CreationTimeHigh, information.CreationTimeLow)),
            DateTimeOffset.FromFileTime(Combine(information.LastAccessTimeHigh, information.LastAccessTimeLow)),
            DateTimeOffset.FromFileTime(Combine(information.LastWriteTimeHigh, information.LastWriteTimeLow)),
            (FileAttributes)information.FileAttributes,
            information.NumberOfLinks,
            ReadSecurityDescriptor(path),
            ReadStreamNames(path));
    }

    internal static bool SemanticallyEquals(
        FixtureSupportedFileState expected,
        FixtureSupportedFileState actual) =>
        expected.Identity == actual.Identity &&
        expected.Bytes.SequenceEqual(actual.Bytes) &&
        expected.CreationTimeUtc == actual.CreationTimeUtc &&
        expected.LastAccessTimeUtc == actual.LastAccessTimeUtc &&
        expected.LastWriteTimeUtc == actual.LastWriteTimeUtc &&
        expected.Attributes == actual.Attributes &&
        expected.LinkCount == actual.LinkCount &&
        FileMetadataSnapshot.SecurityDescriptorsSemanticallyEqual(
            expected.SecurityDescriptor,
            actual.SecurityDescriptor) &&
        expected.StreamNames.SequenceEqual(actual.StreamNames, StringComparer.Ordinal);

    internal static bool HasIdentity(
        FixtureSupportedFileState observed,
        PhysicalFileIdentity expected) =>
        observed.Identity == expected;

    internal static bool MetadataEquals(
        FixtureSupportedFileState expected,
        FixtureSupportedFileState actual) =>
        expected.CreationTimeUtc == actual.CreationTimeUtc &&
        expected.LastAccessTimeUtc == actual.LastAccessTimeUtc &&
        expected.LastWriteTimeUtc == actual.LastWriteTimeUtc &&
        expected.Attributes == actual.Attributes &&
        expected.LinkCount == actual.LinkCount &&
        FileMetadataSnapshot.SecurityDescriptorsSemanticallyEqual(
            expected.SecurityDescriptor,
            actual.SecurityDescriptor) &&
        expected.StreamNames.SequenceEqual(actual.StreamNames, StringComparer.Ordinal);

    internal static string DescribeDifference(
        FixtureSupportedFileState expected,
        FixtureSupportedFileState actual) =>
        $"identity={expected.Identity == actual.Identity}, " +
        $"content={expected.Bytes.SequenceEqual(actual.Bytes)}, " +
        $"creation={expected.CreationTimeUtc == actual.CreationTimeUtc}, " +
        $"access={expected.LastAccessTimeUtc == actual.LastAccessTimeUtc}, " +
        $"write={expected.LastWriteTimeUtc == actual.LastWriteTimeUtc}, " +
        $"attributes={expected.Attributes == actual.Attributes}, " +
        $"links={expected.LinkCount == actual.LinkCount}, " +
        $"security={FileMetadataSnapshot.SecurityDescriptorsSemanticallyEqual(expected.SecurityDescriptor, actual.SecurityDescriptor)}, " +
        $"streams={expected.StreamNames.SequenceEqual(actual.StreamNames, StringComparer.Ordinal)}";

    private static byte[] ReadSecurityDescriptor(string path)
    {
        var securityInformation =
            OwnerSecurityInformation |
            GroupSecurityInformation |
            DaclSecurityInformation;
        _ = GetFileSecurity(path, securityInformation, null, 0, out var required);
        if (required == 0)
        {
            throw new IOException(
                $"The fixture could not size the security descriptor (Windows error {Marshal.GetLastWin32Error()}).");
        }

        var descriptor = new byte[required];
        if (!GetFileSecurity(path, securityInformation, descriptor, required, out _))
        {
            throw new IOException(
                $"The fixture could not capture the security descriptor (Windows error {Marshal.GetLastWin32Error()}).");
        }

        return descriptor;
    }

    private static List<string> ReadStreamNames(string path)
    {
        var streams = new List<string>();
        var find = FindFirstStream(path, 0, out var data, 0);
        if (find == InvalidHandleValue)
        {
            throw new IOException(
                $"The fixture could not capture stream names (Windows error {Marshal.GetLastWin32Error()}).");
        }

        try
        {
            do
            {
                streams.Add(data.StreamName);
            }
            while (FindNextStream(find, out data));

            const int errorHandleEof = 38;
            if (Marshal.GetLastWin32Error() != errorHandleEof)
            {
                throw new IOException(
                    $"The fixture stream enumeration failed (Windows error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            _ = FindClose(find);
        }

        return streams;
    }

    private static long Combine(uint high, uint low) =>
        ((long)high << 32) | low;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal uint CreationTimeLow;
        internal uint CreationTimeHigh;
        internal uint LastAccessTimeLow;
        internal uint LastAccessTimeHigh;
        internal uint LastWriteTimeLow;
        internal uint LastWriteTimeHigh;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        internal ulong VolumeSerialNumber;
        internal NativeFileId128 FileId;
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct NativeFileId128
    {
        internal ulong LowPart;
        internal ulong HighPart;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindStreamData
    {
        internal long StreamSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        internal string StreamName;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int informationClass,
        out FileIdInfo information,
        uint bufferSize);

    [DllImport("advapi32.dll", EntryPoint = "GetFileSecurityW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSecurity(
        string fileName,
        uint requestedInformation,
        byte[]? securityDescriptor,
        uint length,
        out uint lengthNeeded);

    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr FindFirstStream(
        string fileName,
        int informationLevel,
        out Win32FindStreamData findStreamData,
        uint flags);

    [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStream(
        IntPtr findStream,
        out Win32FindStreamData findStreamData);

    [DllImport("kernel32.dll", EntryPoint = "FindClose", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr findFile);
}

internal sealed class FixtureRandomSource : IRandomSource
{
    public Guid NewGuid() => Guid.NewGuid();

    public void Fill(Span<byte> destination) => RandomNumberGenerator.Fill(destination);
}

internal sealed class SimulatedProcessCrashException : Exception;

internal sealed class ProcessCrashFaultInjector(
    MigrationFaultPoint crashPoint) : IFaultInjector
{
    private int fired;

    public void Hit(MigrationFaultPoint point)
    {
        if (point == crashPoint && Interlocked.Exchange(ref fired, 1) == 0)
        {
            throw new SimulatedProcessCrashException();
        }
    }
}

internal sealed class CancellingFaultInjector(
    MigrationFaultPoint cancellationPoint,
    CancellationTokenSource cancellation) : IFaultInjector
{
    private int fired;

    public void Hit(MigrationFaultPoint point)
    {
        if (point == cancellationPoint && Interlocked.Exchange(ref fired, 1) == 0)
        {
            cancellation.Cancel();
        }
    }
}

internal sealed class DirectoryCrashFaultInjector(
    MigrationFaultPoint crashPoint,
    string directoryToRemoveAfterInputReread) : IFaultInjector
{
    private int removed;
    private int fired;

    public void Hit(MigrationFaultPoint point)
    {
        if (point == MigrationFaultPoint.InputsReread &&
            Interlocked.Exchange(ref removed, 1) == 0)
        {
            Directory.Delete(directoryToRemoveAfterInputReread, recursive: true);
        }

        if (point == crashPoint && Interlocked.Exchange(ref fired, 1) == 0)
        {
            throw new SimulatedProcessCrashException();
        }
    }
}

internal sealed class RollbackCrashFaultInjector(
    MigrationFaultPoint crashPoint) : IFaultInjector
{
    private int commitFailureFired;
    private int rollbackCrashFired;

    public void Hit(MigrationFaultPoint point)
    {
        if (point == MigrationFaultPoint.CommitVerified &&
            Interlocked.Exchange(ref commitFailureFired, 1) == 0)
        {
            throw new IOException("Injected pre-rollback fixture failure.");
        }

        if (point == crashPoint && Interlocked.Exchange(ref rollbackCrashFired, 1) == 0)
        {
            throw new SimulatedProcessCrashException();
        }
    }
}

internal sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
{
    private readonly Action<T> callback =
        callback ?? throw new ArgumentNullException(nameof(callback));

    public void Report(T value) => callback(value);
}

internal sealed class TransactionAccessFixture : IDisposable
{
    private TransactionAccessFixture(
        FixtureSandbox sandbox,
        AuditedFileSystemCapability auditedCapability,
        string sourceRootPath,
        string targetRootPath,
        Pcl2DiscoveryResult discovery,
        Pcl2Instance source,
        Pcl2Instance target)
    {
        Sandbox = sandbox;
        AuditedCapability = auditedCapability;
        SourceRootPath = sourceRootPath;
        TargetRootPath = targetRootPath;
        Discovery = discovery;
        Source = source;
        Target = target;
        SessionFactory = new DiscoverySessionFactory();
        AccessFactory = new CapabilityBoundInstanceAccessFactory(
            SessionFactory,
            auditedCapability);
    }

    internal FixtureSandbox Sandbox { get; }

    internal AuditedFileSystemCapability AuditedCapability { get; }

    internal string SourceRootPath { get; private set; }

    internal string TargetRootPath { get; }

    internal Pcl2DiscoveryResult Discovery { get; }

    internal Pcl2Instance Source { get; }

    internal Pcl2Instance Target { get; }

    internal DiscoverySessionFactory SessionFactory { get; }

    internal CapabilityBoundInstanceAccessFactory AccessFactory { get; }

    internal static TransactionAccessFixture Create()
    {
        var sandbox = FixtureSandbox.Create();
        try
        {
            var minecraftRoot = sandbox.CreateGuidDirectory();
            var minecraftRelative = Path.GetRelativePath(sandbox.RootPath, minecraftRoot);
            WriteInstance(sandbox, minecraftRelative, "Source", "source");
            WriteInstance(sandbox, minecraftRelative, "Target", "target");
            sandbox.WriteBytes(
                Path.Combine(minecraftRelative, "PCL.ini"),
                "Version:Source\r\n"u8.ToArray());
            sandbox.WriteBytes(
                Path.Combine(minecraftRelative, "versions", "Source", "options.txt"),
                "version:3955\nlang:en_us\nkey_key.jump:key.keyboard.space\n"u8.ToArray());
            sandbox.WriteBytes(
                Path.Combine(minecraftRelative, "versions", "Target", "options.txt"),
                "version:3955\nlang:zh_cn\nkey_key.jump:key.keyboard.j\n"u8.ToArray());
            FixtureSecurityDescriptor.SetCurrentUserOwnerTree(minecraftRoot);
            FixtureSecurityDescriptor.SetDistinctProtectedDacl(Path.Combine(
                minecraftRoot,
                "versions",
                "Target",
                "options.txt"));
            var sourceRoot = Path.Combine(minecraftRoot, "versions", "Source");
            var targetRoot = Path.Combine(minecraftRoot, "versions", "Target");
            var versionsRoot = Path.Combine(minecraftRoot, "versions");
            var audited = new AuditedFileSystemCapability(
            [
                sandbox.GetRootProof(minecraftRoot),
                sandbox.AuthorizeExistingDirectory(versionsRoot),
                sandbox.AuthorizeExistingDirectory(sourceRoot),
                sandbox.AuthorizeExistingDirectory(targetRoot),
            ]);
            var discovery = new Pcl2InstanceDiscovery(audited).Discover(
                Pcl2DiscoveryRequest.Create([minecraftRoot], []));
            var source = discovery.Instances.Single(instance =>
                string.Equals(Path.GetFileName(instance.InstanceRoot), "Source", StringComparison.Ordinal));
            var target = discovery.Instances.Single(instance =>
                string.Equals(Path.GetFileName(instance.InstanceRoot), "Target", StringComparison.Ordinal));
            return new TransactionAccessFixture(
                sandbox,
                audited,
                sourceRoot,
                targetRoot,
                discovery,
                source,
                target);
        }
        catch
        {
            sandbox.Dispose();
            throw;
        }
    }

    internal ContentAccessLease OpenLease(DiscoverySession session)
    {
        var result = AccessFactory.Open(
            session,
            Source.Id,
            Target.Id,
            ContentAccessLimits.Beta3);
        if (!result.IsValid || result.Lease is null || result.Diagnostics.Count != 0)
        {
            throw new InvalidOperationException("The transaction fixture could not open its content lease.");
        }

        return result.Lease;
    }

    internal AdapterCompatibilityEvidence CreateCompatibility() =>
        AdapterCompatibilityEvidence.Create(
            Source.MinecraftVersion,
            Target.MinecraftVersion,
            [],
            [],
            []);

    internal VanillaOptionsAdapter CreateVanillaAdapter() =>
        new(new Pcl2OptionsMigrationPreviewer(AuditedCapability));

    internal string WriteSourceBytes(string relativePath, byte[] bytes) =>
        WriteInstanceBytes(SourceRootPath, relativePath, bytes, protectTargetDacl: false);

    internal string WriteTargetBytes(string relativePath, byte[] bytes) =>
        WriteInstanceBytes(TargetRootPath, relativePath, bytes, protectTargetDacl: true);

    internal MigrationContentPlan CreateVanillaPlan(
        VanillaOptionsAdapter adapter,
        ContentProbeContext context,
        long generation)
    {
        var catalog = adapter.BuildCatalog(context, CancellationToken.None);
        var selection = ContentSelection.Create(
            catalog.Items.Where(item => item.IsSelectable).Select(item => item.Id),
            []);
        if (!ContentSelectionValidator.TryValidateExplicit(
                catalog,
                selection,
                out var validated,
                out _))
        {
            throw new InvalidOperationException("The transaction fixture selection was rejected.");
        }

        var plan = adapter.Plan(context, catalog, validated!, CancellationToken.None);
        return MigrationContentPlan.Create(generation, Source.Id, Target.Id, [plan]);
    }

    internal void MoveSourceRoot()
    {
        var destination = Sandbox.AllocateGuidPath();
        Sandbox.MoveDirectory(SourceRootPath, destination);
        SourceRootPath = destination;
    }

    private string WriteInstanceBytes(
        string instanceRoot,
        string relativePath,
        byte[] bytes,
        bool protectTargetDacl)
    {
        var absolutePath = Path.Combine(instanceRoot, relativePath);
        var path = Sandbox.WriteBytes(
            Path.GetRelativePath(Sandbox.RootPath, absolutePath),
            bytes);
        FixtureSecurityDescriptor.SetCurrentUserOwner(path);
        if (protectTargetDacl)
        {
            FixtureSecurityDescriptor.SetDistinctProtectedDacl(path);
        }

        return path;
    }

    public void Dispose()
    {
        if (AuditedCapability.AuditLog.Any(entry => entry.IsMutation))
        {
            throw new InvalidOperationException("The transaction preflight mutated a fixture instance.");
        }

        Sandbox.Dispose();
    }

    private static void WriteInstance(
        FixtureSandbox sandbox,
        string minecraftRelative,
        string directoryName,
        string instanceId)
    {
        sandbox.WriteBytes(
            Path.Combine(
                minecraftRelative,
                "versions",
                directoryName,
                directoryName + ".json"),
            Encoding.UTF8.GetBytes(
                $"{{\"id\":\"{instanceId}\",\"minecraftVersion\":\"1.21.1\",\"mainClass\":\"net.minecraft.client.main.Main\"}}"));
        sandbox.WriteBytes(
            Path.Combine(
                minecraftRelative,
                "versions",
                directoryName,
                "PCL",
                "Setup.ini"),
            "VersionArgumentIndieV2:true\r\n"u8.ToArray());
    }
}
