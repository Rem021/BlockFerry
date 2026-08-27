using System.Security.Cryptography;
using System.Text.Json;
using BlockFerry.Core.Content;
using BlockFerry.Core.Discovery;
using BlockFerry.Core.Processes;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Transactions;

public sealed class TransactionRecoveryService
{
    private readonly ITransactionStoreProvider stores;
    private readonly IFileSystemCapability fileSystem;
    private readonly MinecraftProcessGuard processGuard;
    private readonly TargetMutexFactory mutexFactory;
    private readonly IRandomSource randomSource;
    private readonly ITransactionRaceBoundaryHook raceBoundaryHook;
    private readonly RecoveryAuthorizationResolver authorizationResolver;

    internal TransactionRecoveryService(
        ITransactionStoreProvider stores,
        IFileSystemCapability fileSystem,
        MinecraftProcessGuard processGuard,
        TargetMutexFactory mutexFactory,
        IRandomSource randomSource,
        RecoveryAuthorizationResolver authorizationResolver,
        ITransactionRaceBoundaryHook? raceBoundaryHook = null)
    {
        this.stores = stores ?? throw new ArgumentNullException(nameof(stores));
        this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        this.processGuard = processGuard ?? throw new ArgumentNullException(nameof(processGuard));
        this.mutexFactory = mutexFactory ?? throw new ArgumentNullException(nameof(mutexFactory));
        this.randomSource = randomSource ?? throw new ArgumentNullException(nameof(randomSource));
        this.authorizationResolver = authorizationResolver ??
            throw new ArgumentNullException(nameof(authorizationResolver));
        this.raceBoundaryHook = raceBoundaryHook ?? NullTransactionRaceBoundaryHook.Instance;
    }

    public IReadOnlyList<PendingRecovery> FindPending(CancellationToken cancellationToken = default)
    {
        var result = new List<PendingRecovery>();
        foreach (var transactionId in stores.List(cancellationToken).OrderBy(id => id.Value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var store = stores.Open(transactionId, cancellationToken);
                var journal = store.Journal.ReadAndVerify(transactionId, cancellationToken);
                if (journal.TerminalKind is TransactionRecordKind.Committed or TransactionRecordKind.RolledBack)
                {
                    continue;
                }

                result.Add(new PendingRecovery(
                    transactionId,
                    store.Locator.TargetInstanceId,
                    TargetIdentityMatches(store.Locator, cancellationToken)));
            }
            catch (TransactionAuthenticationException)
            {
                result.Add(new PendingRecovery(
                    transactionId,
                    "无法验证的目标",
                    TargetPathAvailable: false,
                    MigrationRecoveryStatus.AuthenticationFailed));
            }
        }

        return result.AsReadOnly();
    }

    internal VerifiedRecoverySelection? TryCreateVerifiedSelection(
        TransactionId transactionId,
        DiscoverySession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        using var store = stores.Open(transactionId, cancellationToken);
        _ = store.Journal.ReadAndVerify(transactionId, cancellationToken);
        var recordedIdentity = store.Locator.TargetRootIdentity;
        var target = session.RevalidateTarget(
            store.Locator.TargetInstanceId,
            cancellationToken);
        return target is null
               || target.GameRoot.Identity != recordedIdentity
            ? null
            : new VerifiedRecoverySelection(target, recordedIdentity);
    }

    public Task<MigrationRecoveryResult> RecoverAsync(
        TransactionId transactionId,
        VerifiedRecoverySelection? reselection = null,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => Recover(
                transactionId,
                reselection,
                progress,
                cancellationToken),
            CancellationToken.None);

    public Task<MigrationUndoResult> UndoAsync(
        TransactionId committedTransactionId,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => Undo(
                committedTransactionId,
                progress,
                cancellationToken),
            CancellationToken.None);

    public Task<bool> IsUndoEligibleAsync(
        TransactionId committedTransactionId,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => IsUndoEligible(committedTransactionId, cancellationToken),
            CancellationToken.None);

    public ImmutableByteBuffer ExportRedactedDiagnostic(
        TransactionId transactionId,
        CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream(capacity: 4096);
        try
        {
            using var store = stores.Open(transactionId, cancellationToken);
            var journal = store.Journal.ReadAndVerify(transactionId, cancellationToken);
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schema", 1);
                writer.WriteString("transactionId", transactionId.Value);
                writer.WriteString("targetInstanceId", store.Locator.TargetInstanceId);
                writer.WriteString(
                    "state",
                    journal.TerminalKind?.ToString() ??
                    (journal.Records[^1].Kind == TransactionRecordKind.RecoveryRequired
                        ? nameof(TransactionRecordKind.RecoveryRequired)
                        : "NonTerminal"));
                writer.WriteNumber("recordCount", journal.Records.Count);
                writer.WriteStartObject("recordKinds");
                foreach (var group in journal.Records
                             .GroupBy(record => record.Kind)
                             .OrderBy(group => group.Key))
                {
                    writer.WriteNumber(group.Key.ToString(), group.Count());
                }

                writer.WriteEndObject();
                writer.WriteNumber("plannedPathCount", store.Plan.Paths.Count);
                writer.WriteEndObject();
            }
        }
        catch (TransactionAuthenticationException)
        {
            stream.SetLength(0);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteString("transactionId", transactionId.Value);
            writer.WriteString("state", nameof(MigrationRecoveryStatus.AuthenticationFailed));
            writer.WriteEndObject();
        }

        if (stream.Length > 64 * 1024)
        {
            throw new IOException("The redacted recovery diagnostic exceeded its fixed bound.");
        }

        return ImmutableByteBuffer.CopyFrom(stream.ToArray());
    }

    private MigrationRecoveryResult Recover(
        TransactionId transactionId,
        VerifiedRecoverySelection? reselection,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        AuthenticatedTransactionStore? store = null;
        try
        {
            store = stores.Open(transactionId, cancellationToken);
            var journal = store.Journal.ReadAndVerify(transactionId, cancellationToken);
            if (journal.TerminalKind is TransactionRecordKind.RolledBack or TransactionRecordKind.Committed)
            {
                return new MigrationRecoveryResult(
                    MigrationRecoveryStatus.AlreadyTerminal,
                    transactionId,
                    0,
                    "这次同步已经处于可验证的终态。");
            }

            var candidatePath = ResolveCandidatePath(store.Locator, reselection);
            using var authority = candidatePath is null
                ? null
                : authorizationResolver.Resolve(
                    store.Locator,
                    store.Plan,
                    candidatePath,
                    cancellationToken);
            if (authority is null)
            {
                return new MigrationRecoveryResult(
                    MigrationRecoveryStatus.TargetReselectionRequired,
                    transactionId,
                    0,
                    "请重新选择原来的目标实例文件夹以继续恢复。");
            }

            using var targetMutex = mutexFactory.Acquire(authority, cancellationToken);
            using var gameGuard = processGuard.Begin(authority, cancellationToken);
            var backupStore = new BackupStore(store, stores.ProtectedData);
            var operations = new WindowsTransactionFileOperations(fileSystem, backupStore, raceBoundaryHook);
            using var target = operations.OpenRecoveryTargetRoot(authority, cancellationToken);
            progress?.Report(new MigrationProgress(
                MigrationProgressStage.RollingBack,
                0,
                store.Plan.Paths.Count,
                "正在验证并恢复上次同步"));

            if (store.Plan.Purpose == StoredTransactionPurpose.Undo)
            {
                NormalizePendingUndoIntent(
                    store,
                    backupStore,
                    operations,
                    target,
                    cancellationToken);
                if (!UndoPreparationComplete(store, backupStore, cancellationToken))
                {
                    EnsureRollbackPhase(store, cancellationToken);
                    store.Journal.AppendTerminal(
                        TransactionRecordKind.RolledBack,
                        store.Plan.AcceptedPlanDigest,
                        CancellationToken.None);
                    return new MigrationRecoveryResult(
                        MigrationRecoveryStatus.Recovered,
                        transactionId,
                        0,
                        "撤销准备未完成，目标实例保持同步后的已验证状态。");
                }

                var completedUndoPaths = ResumeUndoPlan(
                    store,
                    backupStore,
                    operations,
                    target,
                    gameGuard,
                    progress,
                    cancellationToken);
                store.Journal.AppendTerminal(
                    TransactionRecordKind.Committed,
                    store.Plan.AcceptedPlanDigest,
                    CancellationToken.None);
                return new MigrationRecoveryResult(
                    MigrationRecoveryStatus.Recovered,
                    transactionId,
                    completedUndoPaths,
                    $"已继续完成撤销并恢复 {completedUndoPaths} 个文件。");
            }

            NormalizePendingIntent(
                store,
                backupStore,
                operations,
                target,
                cancellationToken);
            var restored = RestorePlan(
                store,
                backupStore,
                operations,
                target,
                gameGuard,
                progress,
                cancellationToken);
            EnsureRollbackPhase(store, cancellationToken);
            store.Journal.AppendTerminal(
                TransactionRecordKind.RolledBack,
                store.Plan.AcceptedPlanDigest,
                CancellationToken.None);
            return new MigrationRecoveryResult(
                MigrationRecoveryStatus.Recovered,
                transactionId,
                restored,
                $"已恢复 {restored} 个文件，目标实例回到同步前状态。");
        }
        catch (TransactionAuthenticationException)
        {
            return new MigrationRecoveryResult(
                MigrationRecoveryStatus.AuthenticationFailed,
                transactionId,
                0,
                "恢复记录未通过身份验证，没有写入目标实例。");
        }
        catch (Exception exception) when (
            exception is TargetMutexBusyException or MinecraftProcessBlockedException)
        {
            return new MigrationRecoveryResult(
                MigrationRecoveryStatus.Blocked,
                transactionId,
                0,
                "请关闭目标 Minecraft，并等待其他同步操作结束后重试。");
        }
        catch (RecoveryStateChangedException)
        {
            return new MigrationRecoveryResult(
                MigrationRecoveryStatus.CurrentStateChanged,
                transactionId,
                0,
                "目标文件已在同步后被修改，BlockFerry 未覆盖这些新变化。");
        }
        catch (RecoveryCatalogRejectedException)
        {
            return new MigrationRecoveryResult(
                MigrationRecoveryStatus.CurrentStateChanged,
                transactionId,
                0,
                "当前内容适配器不再授权恢复记录中的目标路径，没有写入目标实例。");
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            TryMarkRecoveryRequired(store);
            return new MigrationRecoveryResult(
                MigrationRecoveryStatus.RecoveryRequired,
                transactionId,
                0,
                "恢复尚未完成；经过验证的备份仍然保留，可再次重试。");
        }
        finally
        {
            store?.Dispose();
        }
    }

    private bool IsUndoEligible(
        TransactionId committedTransactionId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var original = stores.Open(committedTransactionId, cancellationToken);
            var journal = original.Journal.ReadAndVerify(
                committedTransactionId,
                cancellationToken);
            if (journal.TerminalKind != TransactionRecordKind.Committed)
            {
                return false;
            }

            using var readContext = authorizationResolver.ResolveReadOnly(
                original.Locator,
                original.Plan,
                original.Locator.CanonicalTargetRoot,
                cancellationToken);
            if (readContext is null)
            {
                return false;
            }

            var backups = new BackupStore(original, stores.ProtectedData);
            var operations = new WindowsTransactionFileOperations(
                fileSystem,
                backups,
                raceBoundaryHook);
            using var target = operations.OpenReadOnlyTargetRoot(
                readContext,
                cancellationToken);
            RequireCurrentAfterState(
                original,
                backups,
                WindowsTransactionFileOperations.RereadReadOnly,
                target,
                raceBoundaryHook,
                cancellationToken);
            return true;
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

    private MigrationUndoResult Undo(
        TransactionId committedTransactionId,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        AuthenticatedTransactionStore? original = null;
        AuthenticatedTransactionStore? undoStore = null;
        try
        {
            original = stores.Open(committedTransactionId, cancellationToken);
            var originalJournal = original.Journal.ReadAndVerify(
                committedTransactionId,
                cancellationToken);
            if (originalJournal.TerminalKind != TransactionRecordKind.Committed)
            {
                return new MigrationUndoResult(
                    MigrationRecoveryStatus.Blocked,
                    null,
                    0,
                    "只有已验证完成且当前状态未变化的同步可以撤销。");
            }

            using var authority = authorizationResolver.Resolve(
                original.Locator,
                original.Plan,
                original.Locator.CanonicalTargetRoot,
                cancellationToken);
            if (authority is null)
            {
                return new MigrationUndoResult(
                    MigrationRecoveryStatus.TargetReselectionRequired,
                    null,
                    0,
                    "无法重新发现原目标实例，本次撤销没有写入。");
            }

            using var targetMutex = mutexFactory.Acquire(authority, cancellationToken);
            using var gameGuard = processGuard.Begin(authority, cancellationToken);
            var originalBackups = new BackupStore(original, stores.ProtectedData);
            var probeOperations = new WindowsTransactionFileOperations(fileSystem, originalBackups, raceBoundaryHook);
            using var target = probeOperations.OpenRecoveryTargetRoot(authority, cancellationToken);
            progress?.Report(new MigrationProgress(
                MigrationProgressStage.CheckingRunningGames,
                0,
                original.Plan.Paths.Count,
                "正在确认撤销期间 Minecraft 保持关闭"));
            gameGuard.EnsureSafeBeforeMutation(cancellationToken);
            RequireCurrentAfterState(
                original,
                originalBackups,
                probeOperations.Reread,
                target,
                null,
                cancellationToken);

            var undoId = new TransactionId(randomSource.NewGuid());
            undoStore = stores.Create(
                RecoveryLocator.Create(
                    undoId,
                    original.Locator.TargetInstanceId,
                    original.Locator.CanonicalTargetRoot,
                    original.Locator.TargetRootIdentity),
                 StoredMigrationPlan.Create(
                     undoId,
                     original.Plan.AcceptedPlanDigest,
                     original.Plan.Paths.Select(path => StoredPlanPath.Create(
                        path.AdapterId,
                        path.RelativePath,
                        path.Resolution,
                         path.AfterExists,
                         path.ExpectedAfterSha256,
                         path.BeforeExists,
                         path.ExpectedBeforeSha256)),
                     StoredTransactionPurpose.Undo),
                 cancellationToken);
            var undoBackups = new BackupStore(undoStore, stores.ProtectedData);
            var operations = new WindowsTransactionFileOperations(fileSystem, undoBackups, raceBoundaryHook);
            var restored = UndoCommittedPlan(
                original,
                originalBackups,
                undoStore,
                undoBackups,
                operations,
                target,
                gameGuard,
                progress,
                cancellationToken);
            undoStore.Journal.AppendTerminal(
                TransactionRecordKind.Committed,
                original.Plan.AcceptedPlanDigest,
                CancellationToken.None);
            return new MigrationUndoResult(
                MigrationRecoveryStatus.Recovered,
                undoId,
                restored,
                $"已撤销这次同步并恢复 {restored} 个文件。");
        }
        catch (RecoveryStateChangedException)
        {
            return new MigrationUndoResult(
                MigrationRecoveryStatus.CurrentStateChanged,
                null,
                0,
                "目标设置已经变化，为避免覆盖新修改，本次撤销未执行。");
        }
        catch (RecoveryCatalogRejectedException)
        {
            return new MigrationUndoResult(
                MigrationRecoveryStatus.CurrentStateChanged,
                null,
                0,
                "当前内容适配器不再授权这次撤销的目标路径。");
        }
        catch (Exception exception) when (
            exception is TargetMutexBusyException or MinecraftProcessBlockedException)
        {
            return new MigrationUndoResult(
                MigrationRecoveryStatus.Blocked,
                null,
                0,
                "请关闭目标 Minecraft，并等待其他同步操作结束后重试。");
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            TryMarkRecoveryRequired(undoStore);
            return new MigrationUndoResult(
                MigrationRecoveryStatus.RecoveryRequired,
                undoStore?.TransactionId,
                0,
                "撤销尚未完成；备份已保留，可从恢复页继续。");
        }
        finally
        {
            undoStore?.Dispose();
            original?.Dispose();
        }
    }

    private static int RestorePlan(
        AuthenticatedTransactionStore store,
        BackupStore backups,
        ITransactionFileOperations operations,
        TransactionRootLease target,
        MinecraftProcessGuardSession gameGuard,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var restored = 0;
        var journal = store.Journal.ReadAndVerify(store.TransactionId, cancellationToken);
        foreach (var planPath in store.Plan.Paths.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            gameGuard.EnsureSafeBeforeMutation(cancellationToken);
            var objectId = FileObjectId(planPath);
            var records = journal.Records
                .Where(record => string.Equals(record.OpaqueObjectId, objectId, StringComparison.Ordinal))
                .ToArray();
            var committedRecord = records.LastOrDefault(record =>
                record.Kind == TransactionRecordKind.CommitVerified);
            var beforeExists = planPath.BeforeExists;
            using var current = TryReread(operations, target, planPath.RelativePath, cancellationToken);
            if (committedRecord is not null)
            {
                using var after = backups.Read(
                    MigrationTransactionCoordinator.AfterObjectId(objectId),
                    cancellationToken);
                if (!CurrentMatchesBefore(current, beforeExists, backups, objectId, cancellationToken))
                {
                    if (current is null || !Matches(current.Metadata, after.Metadata, requireIdentity: true))
                    {
                        throw new RecoveryStateChangedException();
                    }

                    RestoreOne(
                        store,
                        backups,
                        operations,
                        target,
                        planPath.RelativePath,
                        objectId,
                        beforeExists,
                        current,
                        cancellationToken);
                    restored++;
                    progress?.Report(new MigrationProgress(
                        MigrationProgressStage.RollingBack,
                        restored,
                        store.Plan.Paths.Count,
                        $"已恢复 {restored} 个文件"));
                }
            }

            CleanupTemporary(
                store,
                operations,
                target,
                planPath.RelativePath,
                objectId,
                "stage",
                cancellationToken);
            CleanupTemporary(
                store,
                operations,
                target,
                planPath.RelativePath,
                objectId,
                "displaced",
                cancellationToken);
            CleanupTemporary(
                store,
                operations,
                target,
                planPath.RelativePath,
                objectId,
                "recovery",
                cancellationToken);
        }

        RemoveCreatedDirectories(store, operations, target, cancellationToken);
        return restored;
    }

    private static void RestoreOne(
        AuthenticatedTransactionStore store,
        BackupStore backups,
        ITransactionFileOperations operations,
        TransactionRootLease target,
        NormalizedRelativePath path,
        string objectId,
        bool beforeExists,
        VerifiedObject current,
        CancellationToken cancellationToken)
    {
        var permit = store.Journal.AppendIntent(
            TransactionIntent.Create(
                TransactionRecordKind.RollbackIntent,
                objectId,
                path,
                current.Metadata.Sha256),
            cancellationToken);
        if (!beforeExists)
        {
            var created = new CommittedObject(
                objectId,
                path,
                current.RetainedPath,
                current.DetachHandle(),
                current.Metadata);
            operations.DeleteCreatedFile(target, created, permit, cancellationToken);
            store.Journal.AppendVerified(
                permit,
                TransactionVerification.Create(
                    TransactionRecordKind.RollbackVerified,
                    objectId,
                    path,
                    EmptySha256),
                cancellationToken);
            return;
        }

        using var before = backups.Read(objectId, cancellationToken);
        using var displacedTemporary = operations.TryOpenTemporary(
            target,
            path,
            store.TransactionId,
            objectId,
            "displaced",
            cancellationToken);
        if (displacedTemporary is not null &&
            Matches(displacedTemporary.Metadata, before.Metadata, requireIdentity: true))
        {
            var displaced = new DisplacedObject(
                objectId,
                path,
                displacedTemporary.RetainedPath,
                current.RetainedPath,
                displacedTemporary.DetachHandle(),
                displacedTemporary.Metadata,
                current.Metadata);
            displaced.LinkReplacement(current);
            operations.RestoreDisplaced(target, displaced, permit, cancellationToken);
        }
        else
        {
            operations.RestoreBackup(target, path, before, current, permit, cancellationToken);
        }

        store.Journal.AppendVerified(
            permit,
            TransactionVerification.Create(
                TransactionRecordKind.RollbackVerified,
                objectId,
                path,
                before.Metadata.Sha256),
            cancellationToken);
    }

    private static void NormalizePendingUndoIntent(
        AuthenticatedTransactionStore store,
        BackupStore backups,
        WindowsTransactionFileOperations operations,
        TransactionRootLease target,
        CancellationToken cancellationToken)
    {
        if (store.Plan.Purpose != StoredTransactionPurpose.Undo)
        {
            throw new InvalidOperationException("Only an authenticated undo plan can resume undo intents.");
        }

        var journal = store.Journal.ReadAndVerify(store.TransactionId, cancellationToken);
        var last = journal.Records[^1];
        if (!TransactionStateMachine.IsIntent(last.Kind))
        {
            return;
        }

        if (last.Kind != TransactionRecordKind.RollbackIntent)
        {
            NormalizePendingIntent(store, backups, operations, target, cancellationToken);
            return;
        }

        var known = ResolveJournalObject(store.Plan, last.OpaqueObjectId);
        var planPath = ResolveStoredPlanPath(store.Plan, last.OpaqueObjectId);
        if (known is null || known.Value.IsDirectory || planPath is null)
        {
            throw new TransactionAuthenticationException(
                "The pending undo intent was not a protected file in its authenticated plan.");
        }

        using var current = TryReread(
            operations,
            target,
            planPath.RelativePath,
            cancellationToken);
        if (CurrentMatchesUndoAfter(
                current,
                planPath,
                backups,
                last.OpaqueObjectId,
                cancellationToken))
        {
            var permit = store.Journal.ResumeObservedIntent(
                last.Kind,
                last.OpaqueObjectId,
                planPath.RelativePath,
                cancellationToken);
            store.Journal.AppendVerified(
                permit,
                TransactionVerification.Create(
                    TransactionRecordKind.RollbackVerified,
                    last.OpaqueObjectId,
                    planPath.RelativePath,
                    planPath.AfterExists ? planPath.ExpectedAfterSha256 : EmptySha256),
                cancellationToken);
        }
        else if (CurrentMatchesUndoBefore(
                     current,
                     planPath,
                     backups,
                     last.OpaqueObjectId,
                     cancellationToken))
        {
            store.Journal.AppendIntentAborted(
                last.Kind,
                last.OpaqueObjectId,
                planPath.RelativePath,
                cancellationToken);
        }
        else
        {
            throw new RecoveryStateChangedException();
        }

        CleanupTemporary(
            store,
            operations,
            target,
            planPath.RelativePath,
            last.OpaqueObjectId,
            "recovery",
            cancellationToken);
    }

    private static void NormalizePendingIntent(
        AuthenticatedTransactionStore store,
        BackupStore backups,
        WindowsTransactionFileOperations operations,
        TransactionRootLease target,
        CancellationToken cancellationToken)
    {
        var journal = store.Journal.ReadAndVerify(store.TransactionId, cancellationToken);
        var last = journal.Records[^1];
        if (!TransactionStateMachine.IsIntent(last.Kind))
        {
            return;
        }

        var known = ResolveJournalObject(store.Plan, last.OpaqueObjectId);
        if (known is null)
        {
            throw new TransactionAuthenticationException("The pending recovery intent was not in the protected plan.");
        }

        if (last.Kind == TransactionRecordKind.StageIntent)
        {
            using var temporary = operations.TryOpenTemporary(
                target,
                known.Value.Path,
                store.TransactionId,
                last.OpaqueObjectId,
                "stage",
                cancellationToken);
            if (temporary is null)
            {
                store.Journal.AppendIntentAborted(
                    last.Kind,
                    last.OpaqueObjectId,
                    known.Value.Path,
                    cancellationToken);
                return;
            }

            var permit = store.Journal.ResumeObservedIntent(
                last.Kind,
                last.OpaqueObjectId,
                known.Value.Path,
                cancellationToken);
            store.Journal.AppendVerified(
                permit,
                TransactionVerification.Create(
                    TransactionRecordKind.StageVerified,
                    last.OpaqueObjectId,
                    known.Value.Path,
                    temporary.Metadata.Sha256),
                cancellationToken);
            return;
        }

        if (last.Kind == TransactionRecordKind.CommitIntent)
        {
            if (known.Value.IsDirectory)
            {
                throw new TransactionAuthenticationException("A protected commit intent referred to a directory.");
            }

            var planPath = ResolveStoredPlanPath(store.Plan, last.OpaqueObjectId) ??
                throw new TransactionAuthenticationException("The protected commit object was missing from its plan.");
            using var stage = operations.TryOpenTemporary(
                target,
                known.Value.Path,
                store.TransactionId,
                last.OpaqueObjectId,
                "stage",
                cancellationToken);
            if (stage is not null)
            {
                store.Journal.AppendIntentAborted(
                    last.Kind,
                    last.OpaqueObjectId,
                    known.Value.Path,
                    cancellationToken);
                return;
            }

            using var current = TryReread(
                operations,
                target,
                known.Value.Path,
                cancellationToken);
            using var expectedAfter = backups.Read(
                MigrationTransactionCoordinator.AfterObjectId(last.OpaqueObjectId),
                cancellationToken);
            if (current is null ||
                !Matches(current.Metadata, expectedAfter.Metadata, requireIdentity: true))
            {
                throw new RecoveryStateChangedException();
            }

            using var displaced = operations.TryOpenTemporary(
                target,
                known.Value.Path,
                store.TransactionId,
                last.OpaqueObjectId,
                "displaced",
                cancellationToken);
            if (planPath.BeforeExists && displaced is not null)
            {
                using var expectedBefore = backups.Read(last.OpaqueObjectId, cancellationToken);
                if (!Matches(displaced.Metadata, expectedBefore.Metadata, requireIdentity: true))
                {
                    throw new RecoveryStateChangedException();
                }
            }
            else if (!planPath.BeforeExists && displaced is not null)
            {
                throw new RecoveryStateChangedException();
            }

            var permit = store.Journal.ResumeObservedIntent(
                last.Kind,
                last.OpaqueObjectId,
                known.Value.Path,
                cancellationToken);
            store.Journal.AppendVerified(
                permit,
                TransactionVerification.Create(
                    TransactionRecordKind.CommitVerified,
                    last.OpaqueObjectId,
                    known.Value.Path,
                    current.Metadata.Sha256),
                cancellationToken);
            return;
        }

        if (last.Kind == TransactionRecordKind.CleanupIntent)
        {
            using var temporary = operations.TryOpenTemporary(
                target,
                known.Value.Path,
                store.TransactionId,
                last.OpaqueObjectId,
                "displaced",
                cancellationToken);
            if (temporary is not null)
            {
                if (!CryptographicOperations.FixedTimeEquals(
                        last.ContentDigest,
                        Convert.FromHexString(temporary.Metadata.Sha256)))
                {
                    throw new RecoveryStateChangedException();
                }

                store.Journal.AppendIntentAborted(
                    last.Kind,
                    last.OpaqueObjectId,
                    known.Value.Path,
                    cancellationToken);
                return;
            }

            var permit = store.Journal.ResumeObservedIntent(
                last.Kind,
                last.OpaqueObjectId,
                known.Value.Path,
                cancellationToken);
            store.Journal.AppendVerified(
                permit,
                TransactionVerification.Create(
                    TransactionRecordKind.CleanupVerified,
                    last.OpaqueObjectId,
                    known.Value.Path,
                    Convert.ToHexString(last.ContentDigest)),
                cancellationToken);
            return;
        }

        if (last.Kind == TransactionRecordKind.RollbackIntent)
        {
            if (known.Value.IsDirectory)
            {
                var createdRecord = journal.Records.LastOrDefault(record =>
                    record.Kind == TransactionRecordKind.DirectoryCreated &&
                    string.Equals(
                        record.OpaqueObjectId,
                        last.OpaqueObjectId,
                        StringComparison.Ordinal));
                if (createdRecord is null)
                {
                    throw new TransactionAuthenticationException(
                        "A protected directory rollback had no authenticated creation record.");
                }

                using var directory = operations.TryOpenDirectory(
                    target,
                    known.Value.Path,
                    last.OpaqueObjectId,
                    cancellationToken);
                if (directory is null)
                {
                    var permit = store.Journal.ResumeObservedIntent(
                        last.Kind,
                        last.OpaqueObjectId,
                        known.Value.Path,
                        cancellationToken);
                    store.Journal.AppendVerified(
                        permit,
                        TransactionVerification.Create(
                            TransactionRecordKind.RollbackVerified,
                            last.OpaqueObjectId,
                            known.Value.Path,
                            EmptySha256),
                        cancellationToken);
                    return;
                }

                if (!CryptographicOperations.FixedTimeEquals(
                        createdRecord.ContentDigest,
                        Convert.FromHexString(
                            MigrationTransactionCoordinator.IdentityDigest(directory.Identity))))
                {
                    throw new RecoveryStateChangedException();
                }

                store.Journal.AppendIntentAborted(
                    last.Kind,
                    last.OpaqueObjectId,
                    known.Value.Path,
                    cancellationToken);
                return;
            }

            var planPath = ResolveStoredPlanPath(store.Plan, last.OpaqueObjectId) ??
                throw new TransactionAuthenticationException("The protected rollback object was missing from its plan.");
            using var current = TryReread(
                operations,
                target,
                known.Value.Path,
                cancellationToken);
            if (CurrentMatchesBefore(
                    current,
                    planPath.BeforeExists,
                    backups,
                    last.OpaqueObjectId,
                    cancellationToken))
            {
                var permit = store.Journal.ResumeObservedIntent(
                    last.Kind,
                    last.OpaqueObjectId,
                    known.Value.Path,
                    cancellationToken);
                store.Journal.AppendVerified(
                    permit,
                    TransactionVerification.Create(
                        TransactionRecordKind.RollbackVerified,
                        last.OpaqueObjectId,
                        known.Value.Path,
                        planPath.ExpectedBeforeSha256),
                    cancellationToken);
                return;
            }

            using var expectedAfter = backups.Read(
                MigrationTransactionCoordinator.AfterObjectId(last.OpaqueObjectId),
                cancellationToken);
            if (current is null ||
                !Matches(current.Metadata, expectedAfter.Metadata, requireIdentity: true))
            {
                throw new RecoveryStateChangedException();
            }

            store.Journal.AppendIntentAborted(
                last.Kind,
                last.OpaqueObjectId,
                known.Value.Path,
                cancellationToken);
            return;
        }

        if (last.Kind == TransactionRecordKind.DirectoryIntent)
        {
            using var directory = operations.TryOpenDirectory(
                target,
                known.Value.Path,
                last.OpaqueObjectId,
                cancellationToken);
            if (directory is not null)
            {
                throw new RecoveryStateChangedException();
            }

            store.Journal.AppendIntentAborted(
                last.Kind,
                last.OpaqueObjectId,
                known.Value.Path,
                cancellationToken);
            return;
        }

        if (last.Kind == TransactionRecordKind.BackupIntent)
        {
            store.Journal.AppendIntentAborted(
                last.Kind,
                last.OpaqueObjectId,
                known.Value.Path,
                cancellationToken);
            return;
        }

        throw new RecoveryStateChangedException();
    }

    private static void CleanupTemporary(
        AuthenticatedTransactionStore store,
        ITransactionFileOperations operations,
        TransactionRootLease target,
        NormalizedRelativePath path,
        string objectId,
        string suffix,
        CancellationToken cancellationToken)
    {
        using var temporary = operations.TryOpenTemporary(
            target,
            path,
            store.TransactionId,
            objectId,
            suffix,
            cancellationToken);
        if (temporary is null)
        {
            return;
        }

        var permit = store.Journal.AppendIntent(
            TransactionIntent.Create(
                TransactionRecordKind.CleanupIntent,
                objectId,
                path,
                temporary.Metadata.Sha256),
            cancellationToken);
        operations.DeleteStagedOrDisplaced(
            target,
            temporary,
            permit,
            cancellationToken);
        store.Journal.AppendVerified(
            permit,
            TransactionVerification.Create(
                TransactionRecordKind.CleanupVerified,
                objectId,
                path,
                temporary.Metadata.Sha256),
            cancellationToken);
    }

    private static void RemoveCreatedDirectories(
        AuthenticatedTransactionStore store,
        ITransactionFileOperations operations,
        TransactionRootLease target,
        CancellationToken cancellationToken)
    {
        var journal = store.Journal.ReadAndVerify(store.TransactionId, cancellationToken);
        var parents = store.Plan.Paths
            .SelectMany(path => ParentPaths(path.RelativePath))
            .Distinct(NormalizedRelativePathComparer.Instance)
            .OrderByDescending(path => path.Segments.Count)
            .ThenByDescending(path => path.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var path in parents)
        {
            var objectId = MigrationTransactionCoordinator.ComputeOpaqueObjectId("directory", path.Value);
            var createdRecord = journal.Records.LastOrDefault(record =>
                record.Kind == TransactionRecordKind.DirectoryCreated &&
                string.Equals(record.OpaqueObjectId, objectId, StringComparison.Ordinal));
            if (createdRecord is null)
            {
                continue;
            }

            using var directory = operations.TryOpenDirectory(
                target,
                path,
                objectId,
                cancellationToken);
            if (directory is null)
            {
                continue;
            }

            if (!CryptographicOperations.FixedTimeEquals(
                    createdRecord.ContentDigest,
                    Convert.FromHexString(
                        MigrationTransactionCoordinator.IdentityDigest(directory.Identity))))
            {
                throw new RecoveryStateChangedException();
            }

            var permit = store.Journal.AppendIntent(
                TransactionIntent.Create(
                    TransactionRecordKind.RollbackIntent,
                    objectId,
                    path,
                    EmptySha256),
                cancellationToken);
            operations.RemoveCreatedDirectory(target, directory, permit, cancellationToken);
            store.Journal.AppendVerified(
                permit,
                TransactionVerification.Create(
                    TransactionRecordKind.RollbackVerified,
                    objectId,
                    path,
                    EmptySha256),
                cancellationToken);
        }
    }

    private static void RequireCurrentAfterState(
        AuthenticatedTransactionStore store,
        BackupStore backups,
        Func<
            TransactionRootLease,
            NormalizedRelativePath,
            CancellationToken,
            VerifiedObject> reread,
        TransactionRootLease target,
        ITransactionRaceBoundaryHook? retainedObjectHook,
        CancellationToken cancellationToken)
    {
        var journal = store.Journal.ReadAndVerify(store.TransactionId, cancellationToken);
        var retainedCurrentObjects = new List<VerifiedObject>(store.Plan.Paths.Count);
        try
        {
            foreach (var path in store.Plan.Paths)
            {
                var objectId = FileObjectId(path);
                if (!journal.Records.Any(record =>
                        record.Kind == TransactionRecordKind.CommitVerified &&
                        string.Equals(record.OpaqueObjectId, objectId, StringComparison.Ordinal)))
                {
                    throw new RecoveryStateChangedException();
                }

                using var after = backups.Read(
                    MigrationTransactionCoordinator.AfterObjectId(objectId),
                    cancellationToken);
                var current = TryReread(reread, target, path.RelativePath, cancellationToken);
                if (current is null)
                {
                    throw new RecoveryStateChangedException();
                }

                retainedCurrentObjects.Add(current);
                retainedObjectHook?.Hit(
                    TransactionRaceBoundary.UndoEligibilityPathRetained,
                    current.RetainedPath);
                if (!Matches(current.Metadata, after.Metadata, requireIdentity: true))
                {
                    throw new RecoveryStateChangedException();
                }
            }
        }
        finally
        {
            for (var index = retainedCurrentObjects.Count - 1; index >= 0; index--)
            {
                retainedCurrentObjects[index].Dispose();
            }
        }
    }

    private static int UndoCommittedPlan(
        AuthenticatedTransactionStore original,
        BackupStore originalBackups,
        AuthenticatedTransactionStore undoStore,
        BackupStore undoBackups,
        ITransactionFileOperations operations,
        TransactionRootLease target,
        MinecraftProcessGuardSession gameGuard,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        PrepareUndoPlan(
            original,
            originalBackups,
            undoStore,
            undoBackups,
            operations,
            target,
            gameGuard,
            cancellationToken);
        return ResumeUndoPlan(
            undoStore,
            undoBackups,
            operations,
            target,
            gameGuard,
            progress,
            cancellationToken);
    }

    private static void PrepareUndoPlan(
        AuthenticatedTransactionStore original,
        BackupStore originalBackups,
        AuthenticatedTransactionStore undoStore,
        BackupStore undoBackups,
        ITransactionFileOperations operations,
        TransactionRootLease target,
        MinecraftProcessGuardSession gameGuard,
        CancellationToken cancellationToken)
    {
        var originalJournal = original.Journal.ReadAndVerify(original.TransactionId, cancellationToken);
        foreach (var path in original.Plan.Paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            gameGuard.EnsureSafeBeforeMutation(cancellationToken);
            var objectId = FileObjectId(path);
            if (!originalJournal.Records.Any(record =>
                    record.Kind == TransactionRecordKind.CommitVerified &&
                    string.Equals(record.OpaqueObjectId, objectId, StringComparison.Ordinal)))
            {
                throw new TransactionAuthenticationException(
                    "The committed transaction did not verify every protected path.");
            }

            using var after = originalBackups.Read(
                MigrationTransactionCoordinator.AfterObjectId(objectId),
                cancellationToken);
            using var current = TryReread(operations, target, path.RelativePath, cancellationToken) ??
                throw new RecoveryStateChangedException();
            if (!Matches(current.Metadata, after.Metadata, requireIdentity: true))
            {
                throw new RecoveryStateChangedException();
            }

            var backupPermit = undoStore.Journal.AppendIntent(
                TransactionIntent.Create(
                    TransactionRecordKind.BackupIntent,
                    objectId,
                    path.RelativePath,
                    current.Metadata.Sha256),
                cancellationToken);
            backupPermit.Consume(
                undoStore.TransactionId,
                TransactionRecordKind.BackupIntent,
                objectId,
                path.RelativePath);
            if (path.BeforeExists)
            {
                if (!originalJournal.Records.Any(record =>
                        record.Kind == TransactionRecordKind.BackupVerified &&
                        string.Equals(record.OpaqueObjectId, objectId, StringComparison.Ordinal)))
                {
                    throw new TransactionAuthenticationException(
                        "The committed transaction was missing a required before-state backup.");
                }

                using var before = originalBackups.Read(objectId, cancellationToken);
                undoBackups.WriteVerified(
                    MigrationTransactionCoordinator.AfterObjectId(objectId),
                    before.Bytes,
                    before.Metadata,
                    cancellationToken);
            }

            undoBackups.WriteVerified(
                objectId,
                after.Bytes,
                after.Metadata,
                cancellationToken);
            undoStore.Journal.AppendVerified(
                backupPermit,
                TransactionVerification.Create(
                    TransactionRecordKind.BackupVerified,
                    objectId,
                    path.RelativePath,
                    after.Metadata.Sha256),
                cancellationToken);
        }
    }

    private static int ResumeUndoPlan(
        AuthenticatedTransactionStore undoStore,
        BackupStore undoBackups,
        ITransactionFileOperations operations,
        TransactionRootLease target,
        MinecraftProcessGuardSession gameGuard,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var completed = 0;
        foreach (var path in undoStore.Plan.Paths.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            gameGuard.EnsureSafeBeforeMutation(cancellationToken);
            var objectId = FileObjectId(path);
            var journal = undoStore.Journal.ReadAndVerify(undoStore.TransactionId, cancellationToken);
            var alreadyRestored = journal.Records.Any(record =>
                record.Kind == TransactionRecordKind.RollbackVerified &&
                string.Equals(record.OpaqueObjectId, objectId, StringComparison.Ordinal));
            using var current = TryReread(operations, target, path.RelativePath, cancellationToken);
            if (alreadyRestored)
            {
                if (!CurrentMatchesUndoAfter(
                        current,
                        path,
                        undoBackups,
                        objectId,
                        cancellationToken))
                {
                    throw new RecoveryStateChangedException();
                }
            }
            else
            {
                if (!CurrentMatchesUndoBefore(
                        current,
                        path,
                        undoBackups,
                        objectId,
                        cancellationToken) ||
                    current is null)
                {
                    throw new RecoveryStateChangedException();
                }

                RestoreUndoOne(
                    undoStore,
                    undoBackups,
                    operations,
                    target,
                    path,
                    objectId,
                    current,
                    cancellationToken);
            }

            completed++;
            progress?.Report(new MigrationProgress(
                MigrationProgressStage.RollingBack,
                completed,
                undoStore.Plan.Paths.Count,
                $"已撤销 {completed} 个文件"));
        }

        return completed;
    }

    private static void RestoreUndoOne(
        AuthenticatedTransactionStore undoStore,
        BackupStore undoBackups,
        ITransactionFileOperations operations,
        TransactionRootLease target,
        StoredPlanPath path,
        string objectId,
        VerifiedObject current,
        CancellationToken cancellationToken)
    {
        var permit = undoStore.Journal.AppendIntent(
            TransactionIntent.Create(
                TransactionRecordKind.RollbackIntent,
                objectId,
                path.RelativePath,
                current.Metadata.Sha256),
            cancellationToken);
        if (path.AfterExists)
        {
            using var desired = undoBackups.Read(
                MigrationTransactionCoordinator.AfterObjectId(objectId),
                cancellationToken);
            operations.RestoreBackup(
                target,
                path.RelativePath,
                desired,
                current,
                permit,
                cancellationToken);
        }
        else
        {
            var created = new CommittedObject(
                objectId,
                path.RelativePath,
                current.RetainedPath,
                current.DetachHandle(),
                current.Metadata);
            operations.DeleteCreatedFile(target, created, permit, cancellationToken);
        }

        undoStore.Journal.AppendVerified(
            permit,
            TransactionVerification.Create(
                TransactionRecordKind.RollbackVerified,
                objectId,
                path.RelativePath,
                path.AfterExists ? path.ExpectedAfterSha256 : EmptySha256),
            cancellationToken);
    }

    private static bool UndoPreparationComplete(
        AuthenticatedTransactionStore store,
        BackupStore backups,
        CancellationToken cancellationToken)
    {
        if (store.Plan.Purpose != StoredTransactionPurpose.Undo)
        {
            throw new InvalidOperationException("Only an authenticated undo plan has undo preparation state.");
        }

        var journal = store.Journal.ReadAndVerify(store.TransactionId, cancellationToken);
        var rollbackStarted = journal.Records.Any(record =>
            record.Kind is TransactionRecordKind.RollbackIntent or TransactionRecordKind.RollbackVerified);
        foreach (var path in store.Plan.Paths)
        {
            var objectId = FileObjectId(path);
            var backupRecord = journal.Records.LastOrDefault(record =>
                record.Kind == TransactionRecordKind.BackupVerified &&
                string.Equals(record.OpaqueObjectId, objectId, StringComparison.Ordinal));
            if (backupRecord is null)
            {
                if (rollbackStarted)
                {
                    throw new TransactionAuthenticationException(
                        "An undo mutation began before every before-state backup was durable.");
                }

                return false;
            }

            if (!path.BeforeExists ||
                !CryptographicOperations.FixedTimeEquals(
                    backupRecord.ContentDigest,
                    Convert.FromHexString(path.ExpectedBeforeSha256)))
            {
                throw new TransactionAuthenticationException(
                    "An undo before-state did not match its authenticated plan.");
            }

            using var before = backups.Read(objectId, cancellationToken);
            if (!string.Equals(
                    before.Metadata.Sha256,
                    path.ExpectedBeforeSha256,
                    StringComparison.Ordinal))
            {
                throw new TransactionAuthenticationException(
                    "An undo before-state backup did not match its protected digest.");
            }

            if (path.AfterExists)
            {
                using var desired = backups.Read(
                    MigrationTransactionCoordinator.AfterObjectId(objectId),
                    cancellationToken);
                if (!string.Equals(
                        desired.Metadata.Sha256,
                        path.ExpectedAfterSha256,
                        StringComparison.Ordinal))
                {
                    throw new TransactionAuthenticationException(
                        "An undo desired-state backup did not match its protected digest.");
                }
            }
            else if (!string.Equals(path.ExpectedAfterSha256, EmptySha256, StringComparison.Ordinal))
            {
                throw new TransactionAuthenticationException(
                    "An absent undo desired-state had a non-empty protected digest.");
            }
        }

        return true;
    }

    private static bool CurrentMatchesUndoBefore(
        VerifiedObject? current,
        StoredPlanPath path,
        BackupStore backups,
        string objectId,
        CancellationToken cancellationToken)
    {
        if (!path.BeforeExists)
        {
            return current is null;
        }

        if (current is null)
        {
            return false;
        }

        using var before = backups.Read(objectId, cancellationToken);
        return Matches(current.Metadata, before.Metadata, requireIdentity: true);
    }

    private static bool CurrentMatchesUndoAfter(
        VerifiedObject? current,
        StoredPlanPath path,
        BackupStore backups,
        string objectId,
        CancellationToken cancellationToken)
    {
        if (!path.AfterExists)
        {
            return current is null;
        }

        if (current is null)
        {
            return false;
        }

        using var desired = backups.Read(
            MigrationTransactionCoordinator.AfterObjectId(objectId),
            cancellationToken);
        return Matches(current.Metadata, desired.Metadata, requireIdentity: false);
    }

    private static bool CurrentMatchesBefore(
        VerifiedObject? current,
        bool beforeExists,
        BackupStore backups,
        string objectId,
        CancellationToken cancellationToken)
    {
        if (!beforeExists)
        {
            return current is null;
        }

        if (current is null)
        {
            return false;
        }

        using var before = backups.Read(objectId, cancellationToken);
        return Matches(current.Metadata, before.Metadata, requireIdentity: false);
    }

    private static bool Matches(
        FileMetadataSnapshot current,
        FileMetadataSnapshot expected,
        bool requireIdentity) =>
        (!requireIdentity || current.Identity == expected.Identity) &&
        current.StableStateEquals(expected);

    private static VerifiedObject? TryReread(
        ITransactionFileOperations operations,
        TransactionRootLease target,
        NormalizedRelativePath path,
        CancellationToken cancellationToken)
    {
        try
        {
            return operations.Reread(target, path, cancellationToken);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static VerifiedObject? TryReread(
        Func<
            TransactionRootLease,
            NormalizedRelativePath,
            CancellationToken,
            VerifiedObject> reread,
        TransactionRootLease target,
        NormalizedRelativePath path,
        CancellationToken cancellationToken)
    {
        try
        {
            return reread(target, path, cancellationToken);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static string? ResolveCandidatePath(
        RecoveryLocator recorded,
        VerifiedRecoverySelection? reselection)
    {
        if (reselection is null)
        {
            return recorded.CanonicalTargetRoot;
        }

        var target = reselection.Target;
        return reselection.RecordedTargetIdentity == recorded.TargetRootIdentity &&
               target is not null &&
               target.GameRoot.Identity == recorded.TargetRootIdentity &&
               string.Equals(
                   target.Instance.Id,
                   recorded.TargetInstanceId,
                   StringComparison.Ordinal)
            ? target.GameRoot.CanonicalPath
            : null;
    }

    private bool TargetIdentityMatches(
        RecoveryLocator locator,
        CancellationToken cancellationToken)
    {
        try
        {
            using var root = fileSystem.OpenRoot(
                locator.CanonicalTargetRoot,
                FileSystemOpenPurpose.MigrationTarget,
                cancellationToken);
            return root.Identity == locator.TargetRootIdentity &&
                   root.IsLocalVolume &&
                   !root.IsNetworkRedirected;
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return false;
        }
    }

    private static (NormalizedRelativePath Path, bool IsDirectory)? ResolveJournalObject(
        StoredMigrationPlan plan,
        string objectId)
    {
        foreach (var path in plan.Paths)
        {
            if (string.Equals(FileObjectId(path), objectId, StringComparison.Ordinal))
            {
                return (path.RelativePath, false);
            }

            foreach (var parent in ParentPaths(path.RelativePath))
            {
                if (string.Equals(
                        MigrationTransactionCoordinator.ComputeOpaqueObjectId("directory", parent.Value),
                        objectId,
                        StringComparison.Ordinal))
                {
                    return (parent, true);
                }
            }
        }

        return null;
    }

    private static StoredPlanPath? ResolveStoredPlanPath(
        StoredMigrationPlan plan,
        string objectId) =>
        plan.Paths.SingleOrDefault(path =>
            string.Equals(FileObjectId(path), objectId, StringComparison.Ordinal));

    private static IEnumerable<NormalizedRelativePath> ParentPaths(NormalizedRelativePath file)
    {
        for (var count = 1; count < file.Segments.Count; count++)
        {
            if (!WritePathGuard.TryNormalize(
                    string.Join('\\', file.Segments.Take(count)),
                    out var path) || path is null)
            {
                throw new TransactionAuthenticationException("A protected plan parent path was invalid.");
            }

            yield return path;
        }
    }

    private static string FileObjectId(StoredPlanPath path) =>
        MigrationTransactionCoordinator.ComputeOpaqueObjectId(
            "file",
            path.AdapterId + "\0" + path.RelativePath.Value);

    private static void TryMarkRecoveryRequired(AuthenticatedTransactionStore? store)
    {
        if (store is null)
        {
            return;
        }

        try
        {
            var journal = store.Journal.ReadAndVerify(store.TransactionId, CancellationToken.None);
            if (!journal.IsTerminal &&
                journal.Records[^1].Kind != TransactionRecordKind.RecoveryRequired)
            {
                store.Journal.AppendTerminal(
                    TransactionRecordKind.RecoveryRequired,
                    store.Plan.AcceptedPlanDigest,
                    CancellationToken.None);
            }
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
        }
    }

    private static void EnsureRollbackPhase(
        AuthenticatedTransactionStore store,
        CancellationToken cancellationToken)
    {
        var journal = store.Journal.ReadAndVerify(store.TransactionId, cancellationToken);
        if (journal.Records.Any(record =>
                record.Kind is TransactionRecordKind.RollbackIntent or TransactionRecordKind.RollbackVerified))
        {
            return;
        }

        var path = store.Plan.Paths[0];
        var objectId = FileObjectId(path);
        _ = store.Journal.AppendIntent(
            TransactionIntent.Create(
                TransactionRecordKind.RollbackIntent,
                objectId,
                path.RelativePath,
                path.ExpectedAfterSha256),
            cancellationToken);
        store.Journal.AppendIntentAborted(
            TransactionRecordKind.RollbackIntent,
            objectId,
            path.RelativePath,
            cancellationToken);
    }

    private static bool IsRecoverableFailure(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        InvalidOperationException or
        ArgumentException or
        ObjectDisposedException or
        CryptographicException or
        TransactionAuthenticationException or
        CapabilityBoundaryException;

    private static string EmptySha256 { get; } = Convert.ToHexString(SHA256.HashData([]));

    private sealed class RecoveryStateChangedException : IOException;
}
