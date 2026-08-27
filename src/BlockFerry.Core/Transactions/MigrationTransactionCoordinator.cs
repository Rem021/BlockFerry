using System.Collections.ObjectModel;
using System.Security.Cryptography;
using BlockFerry.Core.Content;
using BlockFerry.Core.Discovery;
using BlockFerry.Core.Processes;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Transactions;

internal interface IMigrationTransactionRuntimeFactory
{
    MigrationTransactionRuntime Create(
        TransactionId transactionId,
        AcceptedMigrationPlan plan,
        DiscoveredInstancePair currentPair,
        CancellationToken cancellationToken);
}

internal sealed class WindowsMigrationTransactionRuntimeFactory(
    AppStorageGuard appStorage,
    IFileSystemCapability fileSystem,
    IProtectedData protectedData) : IMigrationTransactionRuntimeFactory
{
    private readonly AppStorageGuard appStorage =
        appStorage ?? throw new ArgumentNullException(nameof(appStorage));
    private readonly IFileSystemCapability fileSystem =
        fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly IProtectedData protectedData =
        protectedData ?? throw new ArgumentNullException(nameof(protectedData));

    public MigrationTransactionRuntime Create(
        TransactionId transactionId,
        AcceptedMigrationPlan plan,
        DiscoveredInstancePair currentPair,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(currentPair);
        var locator = RecoveryLocator.Create(
            transactionId,
            plan.TargetInstanceId,
            currentPair.Target.GameRoot.CanonicalPath,
            currentPair.Target.GameRoot.Identity);
        var storedPlan = StoredMigrationPlan.Create(
            transactionId,
            plan.IntegrityDigest,
            plan.AdapterStages
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .SelectMany(pair => pair.Value.Mutations)
                .Select(mutation => StoredPlanPath.Create(
                    mutation.Change.AdapterId,
                    NormalizeRequired(mutation.Change.RelativePath),
                    ConflictResolution.UseSource,
                    mutation.Change.TargetSnapshot.Exists,
                    mutation.Change.TargetSnapshot.Sha256,
                    afterExists: true,
                    mutation.AfterBytes.Sha256)));
        var storage = appStorage.CreateTransactionStorage(transactionId, cancellationToken);
        AuthenticatedTransactionStore? store = null;
        try
        {
            store = AuthenticatedTransactionStore.Bootstrap(
                storage,
                protectedData,
                locator,
                storedPlan,
                cancellationToken);
            storage = null!;
            var backups = new BackupStore(store, protectedData);
            return new MigrationTransactionRuntime(
                store,
                backups,
                new WindowsTransactionFileOperations(fileSystem, backups));
        }
        catch
        {
            store?.Dispose();
            storage?.Dispose();
            throw;
        }
    }

    private static NormalizedRelativePath NormalizeRequired(ContentRelativePath path)
    {
        if (!WritePathGuard.TryNormalize(path, out var normalized) || normalized is null)
        {
            throw new InvalidOperationException("An accepted transaction path could not be normalized.");
        }

        return normalized;
    }
}

internal sealed class MigrationTransactionRuntime : IDisposable
{
    internal MigrationTransactionRuntime(
        AuthenticatedTransactionStore store,
        BackupStore backupStore,
        ITransactionFileOperations fileOperations)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        BackupStore = backupStore ?? throw new ArgumentNullException(nameof(backupStore));
        FileOperations = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
    }

    internal AuthenticatedTransactionStore Store { get; }

    internal BackupStore BackupStore { get; }

    internal ITransactionFileOperations FileOperations { get; }

    public void Dispose() => Store.Dispose();
}

internal sealed partial class MigrationTransactionCoordinator
{
    private const int MaximumTransactionFileBytes = 256 * 1024 * 1024;
    private static readonly string EmptySha256 = Convert.ToHexString(SHA256.HashData([]));
    private static readonly object PostCommitCleanupAuthoritySeal = new();
    private readonly DiscoverySessionFactory discoverySessions;
    private readonly ReadOnlyDictionary<string, IContentAdapter> adapters;
    private readonly IMigrationTransactionRuntimeFactory runtimeFactory;
    private readonly MinecraftProcessGuard processGuard;
    private readonly TargetMutexFactory mutexFactory;
    private readonly IFaultInjector faultInjector;
    private readonly IRandomSource randomSource;
    private readonly ITargetContentStabilityGate targetStabilityGate;

    internal MigrationTransactionCoordinator(
        DiscoverySessionFactory discoverySessions,
        IReadOnlyDictionary<string, IContentAdapter> adapters,
        IMigrationTransactionRuntimeFactory runtimeFactory,
        MinecraftProcessGuard processGuard,
        TargetMutexFactory mutexFactory,
        IFaultInjector faultInjector,
        IRandomSource randomSource,
        ITargetContentStabilityGate? targetStabilityGate = null)
    {
        this.discoverySessions = discoverySessions ??
            throw new ArgumentNullException(nameof(discoverySessions));
        this.adapters = CopyAdapters(adapters);
        this.runtimeFactory = runtimeFactory ??
            throw new ArgumentNullException(nameof(runtimeFactory));
        this.processGuard = processGuard ?? throw new ArgumentNullException(nameof(processGuard));
        this.mutexFactory = mutexFactory ?? throw new ArgumentNullException(nameof(mutexFactory));
        this.faultInjector = faultInjector ?? throw new ArgumentNullException(nameof(faultInjector));
        this.randomSource = randomSource ?? throw new ArgumentNullException(nameof(randomSource));
        this.targetStabilityGate = targetStabilityGate ?? NoTargetContentStabilityGate.Instance;
    }

    internal Task<MigrationExecutionResult> ExecuteAsync(
        AcceptedMigrationPlan plan,
        DiscoverySession session,
        string sourceId,
        string targetId,
        ContentAccessLease contentLease,
        ContentProbeContext contentContext,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => Execute(
                plan,
                session,
                sourceId,
                targetId,
                contentLease,
                contentContext,
                progress,
                cancellationToken),
            CancellationToken.None);

    private MigrationExecutionResult Execute(
        AcceptedMigrationPlan plan,
        DiscoverySession session,
        string sourceId,
        string targetId,
        ContentAccessLease contentLease,
        ContentProbeContext contentContext,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, MigrationProgressStage.Revalidating, 0, 1, "正在重新确认来源、目标和同步清单");
        if (!TryCreateExecutionAuthority(
                plan,
                session,
                sourceId,
                targetId,
                contentLease,
                contentContext,
                cancellationToken,
                out var authority))
        {
            return MigrationExecutionResult.Create(
                MigrationExecutionStatus.RejectedStale,
                null,
                0,
                "实例或同步清单已经变化，请重新检查后再试。");
        }

        faultInjector.Hit(MigrationFaultPoint.AuthorityValidated);
        cancellationToken.ThrowIfCancellationRequested();
        var transactionId = new TransactionId(randomSource.NewGuid());
        MigrationTransactionRuntime? runtime = null;
        TransactionRootLease? targetRoot = null;
        var staged = new List<StagedEntry>();
        var committed = new List<CommittedEntry>();
        var createdDirectories = new List<CreatedDirectory>();
        var namespaceMutationBegan = false;
        try
        {
            using var targetMutex = mutexFactory.Acquire(authority!, cancellationToken);
            faultInjector.Hit(MigrationFaultPoint.MutexAcquired);
            Report(progress, MigrationProgressStage.CheckingRunningGames, 0, 1, "正在确认 Minecraft 已关闭");
            using var runningGameGuard = processGuard.Begin(authority!, cancellationToken);
            faultInjector.Hit(MigrationFaultPoint.ProcessGuardStarted);

            Report(progress, MigrationProgressStage.Revalidating, 0, 1, "正在确认 PCL 已完成实例写入");
            if (!targetStabilityGate.WaitUntilStable(
                    authority!.CurrentPairEvidence.Target.GameRoot.CanonicalPath,
                    cancellationToken))
            {
                return MigrationExecutionResult.Create(
                    MigrationExecutionStatus.Blocked,
                    null,
                    0,
                    "PCL 仍在写入目标实例；本次没有修改文件，请等待安装完成后重试。");
            }

            runtime = runtimeFactory.Create(
                transactionId,
                plan,
                authority!.CurrentPairEvidence,
                cancellationToken);
            faultInjector.Hit(MigrationFaultPoint.StorePrepared);
            targetRoot = runtime.FileOperations.OpenTargetRoot(authority, cancellationToken);
            faultInjector.Hit(MigrationFaultPoint.TargetOpened);

            EnsureAuthorityLive(plan, session, sourceId, targetId, contentLease, contentContext);
            RereadInputs(plan, contentContext, cancellationToken);
            faultInjector.Hit(MigrationFaultPoint.InputsReread);

            var mutations = OrderedMutations(plan);
            var totalSteps = checked((mutations.Count * 4) + 3);
            var completedSteps = 0;
            var backups = new Dictionary<string, BackupObject>(StringComparer.Ordinal);
            Report(progress, MigrationProgressStage.PreparingBackup, completedSteps, totalSteps, "正在创建可验证还原点");
            foreach (var mutation in mutations.Where(item => item.Mutation.Change.TargetSnapshot.Exists))
            {
                cancellationToken.ThrowIfCancellationRequested();
                runningGameGuard.EnsureSafeBeforeMutation(cancellationToken);
                EnsureAuthorityLive(plan, session, sourceId, targetId, contentLease, contentContext);
                var permit = runtime.Store.Journal.AppendIntent(
                    TransactionIntent.Create(
                        TransactionRecordKind.BackupIntent,
                        mutation.OpaqueObjectId,
                        mutation.Path,
                        mutation.Mutation.Change.TargetSnapshot.Sha256),
                    cancellationToken);
                faultInjector.Hit(MigrationFaultPoint.BackupIntentFlushed);
                var backup = runtime.FileOperations.BackupExisting(
                    targetRoot,
                    mutation.Mutation.Change,
                    permit,
                    cancellationToken);
                runtime.Store.Journal.AppendVerified(
                    permit,
                    TransactionVerification.Create(
                        TransactionRecordKind.BackupVerified,
                        mutation.OpaqueObjectId,
                        mutation.Path,
                        backup.Metadata.Sha256),
                    cancellationToken);
                backups.Add(mutation.OpaqueObjectId, backup);
                faultInjector.Hit(MigrationFaultPoint.BackupVerified);
                Report(
                    progress,
                    MigrationProgressStage.BackingUp,
                    ++completedSteps,
                    totalSteps,
                    $"已备份 {backups.Count} 个目标文件");
            }

            var missingDirectories = mutations
                .SelectMany(mutation => runtime.FileOperations.FindMissingParentDirectories(
                    targetRoot,
                    mutation.Path,
                    cancellationToken))
                .Distinct(NormalizedRelativePathComparer.Instance)
                .OrderBy(path => path.Segments.Count)
                .ThenBy(path => path.Value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var directory in missingDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                runningGameGuard.EnsureSafeBeforeMutation(cancellationToken);
                EnsureAuthorityLive(plan, session, sourceId, targetId, contentLease, contentContext);
                var objectId = OpaqueId("directory", directory.Value);
                var permit = runtime.Store.Journal.AppendIntent(
                    TransactionIntent.Create(
                        TransactionRecordKind.DirectoryIntent,
                        objectId,
                        directory,
                        EmptySha256),
                    cancellationToken);
                faultInjector.Hit(MigrationFaultPoint.DirectoryIntentFlushed);
                namespaceMutationBegan = true;
                var created = runtime.FileOperations.CreateDirectory(
                    targetRoot,
                    directory,
                    permit,
                    cancellationToken);
                createdDirectories.Add(created);
                faultInjector.Hit(MigrationFaultPoint.DirectoryNamespaceCreated);
                runtime.Store.Journal.AppendVerified(
                    permit,
                    TransactionVerification.Create(
                        TransactionRecordKind.DirectoryCreated,
                        objectId,
                        directory,
                        IdentityDigest(created.Identity)),
                    cancellationToken);
                faultInjector.Hit(MigrationFaultPoint.DirectoryCreatedDurableBeforePersistence);
                runtime.FileOperations.PersistCreatedDirectory(
                    targetRoot,
                    created,
                    cancellationToken);
                faultInjector.Hit(MigrationFaultPoint.DirectoryCreated);
            }

            foreach (var mutation in mutations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                runningGameGuard.EnsureSafeBeforeMutation(cancellationToken);
                EnsureAuthorityLive(plan, session, sourceId, targetId, contentLease, contentContext);
                var permit = runtime.Store.Journal.AppendIntent(
                    TransactionIntent.Create(
                        TransactionRecordKind.StageIntent,
                        mutation.OpaqueObjectId,
                        mutation.Path,
                        mutation.Mutation.Change.TargetSnapshot.Sha256),
                    cancellationToken);
                faultInjector.Hit(MigrationFaultPoint.StageIntentFlushed);
                namespaceMutationBegan = true;
                var stagedObject = runtime.FileOperations.Stage(
                    targetRoot,
                    mutation.Mutation,
                    permit,
                    cancellationToken);
                runtime.Store.Journal.AppendVerified(
                    permit,
                    TransactionVerification.Create(
                        TransactionRecordKind.StageVerified,
                        mutation.OpaqueObjectId,
                        mutation.Path,
                        stagedObject.Metadata.Sha256),
                    cancellationToken);
                staged.Add(new StagedEntry(mutation, stagedObject));
                Report(
                    progress,
                    MigrationProgressStage.Staging,
                    ++completedSteps,
                    totalSteps,
                    $"已准备 {staged.Count} / {mutations.Count} 个文件");
            }

            VerifyStagedAdapters(plan, staged, cancellationToken);
            foreach (var entry in staged)
            {
                var bytes = entry.Mutation.Mutation.AfterBytes.CopyBytes();
                try
                {
                    var expectedAfterMetadata = entry.Mutation.Mutation.Change.TargetSnapshot.Exists
                        ? backups[entry.Mutation.OpaqueObjectId].Metadata.WithContentIdentity(
                            entry.StagedObject.Metadata.Identity,
                            bytes.Length,
                            entry.Mutation.Mutation.AfterBytes.Sha256)
                        : entry.StagedObject.Metadata;
                    runtime.BackupStore.WriteVerified(
                        AfterObjectId(entry.Mutation.OpaqueObjectId),
                        bytes,
                        expectedAfterMetadata,
                        cancellationToken);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }

            faultInjector.Hit(MigrationFaultPoint.StageVerified);

            foreach (var entry in staged)
            {
                cancellationToken.ThrowIfCancellationRequested();
                runningGameGuard.EnsureSafeBeforeMutation(cancellationToken);
                EnsureAuthorityLive(plan, session, sourceId, targetId, contentLease, contentContext);
                var permit = runtime.Store.Journal.AppendIntent(
                    TransactionIntent.Create(
                        TransactionRecordKind.CommitIntent,
                        entry.Mutation.OpaqueObjectId,
                        entry.Mutation.Path,
                        entry.Mutation.Mutation.Change.TargetSnapshot.Sha256),
                    cancellationToken);
                faultInjector.Hit(MigrationFaultPoint.CommitIntentFlushed);
                CommittedEntry committedEntry;
                if (entry.Mutation.Mutation.Change.TargetSnapshot.Exists)
                {
                    var outcome = runtime.FileOperations.ReplaceExisting(
                        targetRoot,
                        entry.StagedObject,
                        backups[entry.Mutation.OpaqueObjectId].ExpectedTarget,
                        permit,
                        cancellationToken);
                    committedEntry = CommittedEntry.Existing(entry, outcome);
                    if (!outcome.DisplacedMatchesExpected)
                    {
                        committed.Add(committedEntry);
                        throw new IOException("The displaced target no longer matched its authenticated backup.");
                    }
                }
                else
                {
                    var created = runtime.FileOperations.CreateMissing(
                        targetRoot,
                        entry.StagedObject,
                        permit,
                        cancellationToken);
                    committedEntry = CommittedEntry.Missing(entry, created);
                }

                committed.Add(committedEntry);
                runtime.Store.Journal.AppendVerified(
                    permit,
                    TransactionVerification.Create(
                        TransactionRecordKind.CommitVerified,
                        entry.Mutation.OpaqueObjectId,
                        entry.Mutation.Path,
                        committedEntry.Final.Metadata.Sha256),
                    cancellationToken);
                faultInjector.Hit(MigrationFaultPoint.CommitVerified);
                Report(
                    progress,
                    MigrationProgressStage.Committing,
                    ++completedSteps,
                    totalSteps,
                    $"已安全写入 {committed.Count} / {mutations.Count} 个文件");
            }

            runningGameGuard.EnsureSafeBeforeMutation(cancellationToken);
            EnsureAuthorityLive(plan, session, sourceId, targetId, contentLease, contentContext);
            VerifyFinalAdapters(plan, contentContext, cancellationToken);
            faultInjector.Hit(MigrationFaultPoint.FinalRereadVerified);
            Report(
                progress,
                MigrationProgressStage.Verifying,
                ++completedSteps,
                totalSteps,
                "已复读并验证全部同步结果");

            runtime.Store.Journal.AppendTerminal(
                TransactionRecordKind.Committed,
                plan.IntegrityDigest,
                CancellationToken.None);
            faultInjector.Hit(MigrationFaultPoint.CommittedFlushed);
            foreach (var entry in committed.Where(item => item.Outcome is not null))
            {
                var displaced = entry.Outcome!.Displaced;
                try
                {
                    var cleanupAuthority = PostCommitCleanupAuthority.Issue(
                        PostCommitCleanupAuthoritySeal,
                        transactionId,
                        targetRoot.Identity,
                        displaced);
                    runtime.FileOperations.CleanupDisplacedAfterCommit(
                        targetRoot,
                        displaced,
                        cleanupAuthority,
                        CancellationToken.None);
                }
                catch (Exception cleanupException) when (IsExpectedExecutionFailure(cleanupException))
                {
                    // Committed is already durable. Preserve any uncertain opaque artifact and success.
                }
            }

            Report(
                progress,
                MigrationProgressStage.Completed,
                totalSteps,
                totalSteps,
                $"已验证完成 {committed.Count} 个文件");
            return MigrationExecutionResult.Create(
                MigrationExecutionStatus.Succeeded,
                transactionId,
                committed.Count);
        }
        catch (OperationCanceledException) when (!namespaceMutationBegan)
        {
            if (runtime is not null &&
                !TryRetirePreNamespaceStore(runtime.Store, plan.IntegrityDigest))
            {
                return MigrationExecutionResult.Create(
                    MigrationExecutionStatus.RecoveryRequired,
                    transactionId,
                    0,
                    "取消发生在目标写入前，但事务记录未能安全终结；请继续恢复。");
            }

            return MigrationExecutionResult.Create(
                MigrationExecutionStatus.CancelledBeforeMutation,
                runtime is null ? null : transactionId,
                0,
                "操作已取消，没有改动目标实例。");
        }
        catch (Exception exception) when (IsExpectedExecutionFailure(exception))
        {
            if (runtime is not null && IsDurablyCommitted(runtime.Store, plan.IntegrityDigest))
            {
                return MigrationExecutionResult.Create(
                    MigrationExecutionStatus.Succeeded,
                    transactionId,
                    committed.Count);
            }

            if (!namespaceMutationBegan || runtime is null || targetRoot is null)
            {
                if (runtime is not null &&
                    !TryRetirePreNamespaceStore(runtime.Store, plan.IntegrityDigest))
                {
                    return MigrationExecutionResult.Create(
                        MigrationExecutionStatus.RecoveryRequired,
                        transactionId,
                        0,
                        "目标写入尚未开始，但事务记录未能安全终结；请继续恢复。");
                }

                return MigrationExecutionResult.Create(
                    exception is MinecraftProcessBlockedException
                        ? MigrationExecutionStatus.Blocked
                        : MigrationExecutionStatus.RejectedStale,
                    runtime is null ? null : transactionId,
                    0,
                    SafeFailureMessage(exception));
            }

            Report(progress, MigrationProgressStage.RollingBack, 0, 1, "同步未完成，正在恢复原状");
            try
            {
                RollBack(
                    plan,
                    runtime,
                    targetRoot,
                    committed,
                    staged,
                    createdDirectories);
                runtime.Store.Journal.AppendTerminal(
                    TransactionRecordKind.RolledBack,
                    plan.IntegrityDigest,
                    CancellationToken.None);
                faultInjector.Hit(MigrationFaultPoint.RolledBackFlushed);
                return MigrationExecutionResult.Create(
                    MigrationExecutionStatus.RolledBack,
                    transactionId,
                    0,
                    "同步未完成，目标实例已恢复到开始前的状态。");
            }
            catch (Exception rollbackException) when (IsExpectedExecutionFailure(rollbackException))
            {
                TryMarkRecoveryRequired(runtime.Store.Journal, plan.IntegrityDigest);
                return MigrationExecutionResult.Create(
                    MigrationExecutionStatus.RecoveryRequired,
                    transactionId,
                    0,
                    "自动恢复尚未完成；备份已保留，请在 BlockFerry 中继续恢复。");
            }
        }
        finally
        {
            foreach (var entry in committed)
            {
                entry.Dispose();
            }

            foreach (var entry in staged)
            {
                entry.StagedObject.Dispose();
            }

            foreach (var directory in createdDirectories)
            {
                directory.Dispose();
            }

            targetRoot?.Dispose();
            runtime?.Dispose();
        }
    }

    private bool TryCreateExecutionAuthority(
        AcceptedMigrationPlan plan,
        DiscoverySession session,
        string sourceId,
        string targetId,
        ContentAccessLease contentLease,
        ContentProbeContext contentContext,
        CancellationToken cancellationToken,
        out ExecutionAuthority? authority)
    {
        authority = null;
        if (plan is null ||
            session is null ||
            contentLease is null ||
            contentContext is null ||
            !ReferenceEquals(plan.Session, session) ||
            !ReferenceEquals(plan.ContentLease, contentLease) ||
            !ReferenceEquals(plan.ContentContext, contentContext) ||
            !string.Equals(plan.SourceInstanceId, sourceId, StringComparison.Ordinal) ||
            !string.Equals(plan.TargetInstanceId, targetId, StringComparison.Ordinal) ||
            !session.IsActive ||
            !contentLease.IsBoundTo(session, sourceId, targetId) ||
            !contentContext.IsOwnedBy(contentLease))
        {
            return false;
        }

        try
        {
            var validation = discoverySessions.Revalidate(
                session,
                sourceId,
                targetId,
                cancellationToken);
            if (!validation.IsValid || validation.Pair is not { } pair || !FingerprintMatches(plan, pair))
            {
                return false;
            }

            var regenerated = RegenerateCurrentAllowlists(plan, contentContext, cancellationToken);
            if (regenerated is null)
            {
                return false;
            }

            authority = CreateExecutionAuthorityAfterChecks(plan, pair, regenerated);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedExecutionFailure(exception))
        {
            return false;
        }
    }

    private static ExecutionAuthority CreateExecutionAuthorityAfterChecks(
        AcceptedMigrationPlan plan,
        DiscoveredInstancePair currentPairEvidence,
        IReadOnlyDictionary<string, IReadOnlySet<NormalizedRelativePath>> currentRegeneratedAdapterAllowlists) =>
        ExecutionAuthority.Issue(
            ExecutionAuthoritySeal,
            plan,
            currentPairEvidence,
            currentRegeneratedAdapterAllowlists);

    private ReadOnlyDictionary<string, IReadOnlySet<NormalizedRelativePath>>? RegenerateCurrentAllowlists(
        AcceptedMigrationPlan plan,
        ContentProbeContext context,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlySet<NormalizedRelativePath>>(StringComparer.Ordinal);
        var allPaths = new HashSet<NormalizedRelativePath>(NormalizedRelativePathComparer.Instance);
        foreach (var adapterId in plan.AdapterStages.Keys.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!adapters.TryGetValue(adapterId, out var adapter))
            {
                return null;
            }

            var raw = adapter.RegenerateAllowedPaths(context, cancellationToken);
            if (raw is null || raw.Count > ContentContractLimits.MaximumFileChanges)
            {
                return null;
            }

            var normalized = new HashSet<NormalizedRelativePath>(NormalizedRelativePathComparer.Instance);
            foreach (var path in raw)
            {
                if (!WritePathGuard.TryNormalize(path, out var checkedPath) ||
                    checkedPath is null ||
                    !normalized.Add(checkedPath) ||
                    !allPaths.Add(checkedPath))
                {
                    return null;
                }
            }

            if (!plan.RegeneratedAdapterAllowlists.TryGetValue(adapterId, out var accepted) ||
                !normalized.SetEquals(accepted))
            {
                return null;
            }

            result.Add(
                adapterId,
                new BlockFerry.Core.Content.ReadOnlySet<NormalizedRelativePath>(
                    normalized,
                    NormalizedRelativePathComparer.Instance));
        }

        return result.Count == plan.RegeneratedAdapterAllowlists.Count &&
               plan.WriteAllowlist.All(allPaths.Contains)
            ? new ReadOnlyDictionary<string, IReadOnlySet<NormalizedRelativePath>>(result)
            : null;
    }

    private static void RereadInputs(
        AcceptedMigrationPlan plan,
        ContentProbeContext context,
        CancellationToken cancellationToken)
    {
        foreach (var mutation in OrderedMutations(plan))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var change = mutation.Mutation.Change;
            var maximumBytes = Math.Max(
                1,
                Math.Min(
                    MaximumTransactionFileBytes,
                    Math.Max(change.SourceSnapshot.Bytes.Length, change.TargetSnapshot.Bytes.Length) + 1));
            var source = context.Source.Read(
                change.SourceRelativePath,
                new ContentReadLimits(maximumBytes),
                cancellationToken);
            var target = context.Target.Read(
                change.RelativePath,
                new ContentReadLimits(maximumBytes),
                cancellationToken);
            if (!ContentSnapshotMatches(source, change.SourceSnapshot) ||
                !ContentSnapshotMatches(target, change.TargetSnapshot))
            {
                throw new IOException("A migration input changed after plan acceptance.");
            }
        }
    }

    private void VerifyStagedAdapters(
        AcceptedMigrationPlan plan,
        IReadOnlyList<StagedEntry> staged,
        CancellationToken cancellationToken)
    {
        foreach (var adapterStage in plan.AdapterStages.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var snapshots = staged
                .Where(entry => string.Equals(
                    entry.Mutation.Mutation.Change.AdapterId,
                    adapterStage.Key,
                    StringComparison.Ordinal))
                .Select(CreateStagedSnapshot)
                .ToArray();
            if (!adapters.TryGetValue(adapterStage.Key, out var adapter) ||
                !ContentPlanCoordinator.TryBindVerificationRereads(
                    adapterStage.Value,
                    snapshots,
                    out var bound,
                    out _) ||
                !adapter.Verify(adapterStage.Value, bound, cancellationToken).IsValid)
            {
                throw new IOException("A staged adapter result failed semantic verification.");
            }
        }
    }

    private void VerifyFinalAdapters(
        AcceptedMigrationPlan plan,
        ContentProbeContext context,
        CancellationToken cancellationToken)
    {
        foreach (var adapterStage in plan.AdapterStages.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var rereads = adapterStage.Value.Mutations
                .Select(mutation => context.Target.Read(
                    mutation.Change.RelativePath,
                    new ContentReadLimits(Math.Max(1, mutation.AfterBytes.Length + 1L)),
                    cancellationToken))
                .ToArray();
            if (!ContentPlanCoordinator.TryBindVerificationRereads(
                    adapterStage.Value,
                    rereads,
                    out var bound,
                    out _) ||
                !adapters[adapterStage.Key].Verify(
                    adapterStage.Value,
                    bound,
                    cancellationToken).IsValid)
            {
                throw new IOException("The committed adapter result failed final semantic verification.");
            }
        }
    }

    private void RollBack(
        AcceptedMigrationPlan plan,
        MigrationTransactionRuntime runtime,
        TransactionRootLease target,
        IReadOnlyList<CommittedEntry> committed,
        IReadOnlyList<StagedEntry> staged,
        IReadOnlyList<CreatedDirectory> createdDirectories)
    {
        var committedIds = committed
            .Select(entry => entry.Mutation.OpaqueObjectId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var entry in committed.Reverse())
        {
            var expectedHash = entry.Final.Metadata.Sha256;
            var permit = runtime.Store.Journal.AppendIntent(
                TransactionIntent.Create(
                    TransactionRecordKind.RollbackIntent,
                    entry.Mutation.OpaqueObjectId,
                    entry.Mutation.Path,
                    expectedHash),
                CancellationToken.None);
            faultInjector.Hit(MigrationFaultPoint.RollbackIntentFlushed);
            if (entry.Outcome is not null)
            {
                runtime.FileOperations.RestoreDisplaced(
                    target,
                    entry.Outcome.Displaced,
                    permit,
                    CancellationToken.None);
            }
            else
            {
                runtime.FileOperations.DeleteCreatedFile(
                    target,
                    entry.Created!,
                    permit,
                    CancellationToken.None);
            }

            faultInjector.Hit(MigrationFaultPoint.RollbackActionCompleted);
            var restoredHash = entry.Outcome?.Displaced.Metadata.Sha256 ?? EmptySha256;
            runtime.Store.Journal.AppendVerified(
                permit,
                TransactionVerification.Create(
                    TransactionRecordKind.RollbackVerified,
                    entry.Mutation.OpaqueObjectId,
                    entry.Mutation.Path,
                    restoredHash),
                CancellationToken.None);
            faultInjector.Hit(MigrationFaultPoint.RollbackVerified);
        }

        foreach (var entry in staged
                     .Where(item => !committedIds.Contains(item.Mutation.OpaqueObjectId))
                     .Reverse())
        {
            var permit = runtime.Store.Journal.AppendIntent(
                TransactionIntent.Create(
                    TransactionRecordKind.CleanupIntent,
                    entry.Mutation.OpaqueObjectId,
                    entry.Mutation.Path,
                    entry.StagedObject.Metadata.Sha256),
                CancellationToken.None);
            runtime.FileOperations.DeleteStagedOrDisplaced(
                target,
                entry.StagedObject,
                permit,
                CancellationToken.None);
            runtime.Store.Journal.AppendVerified(
                permit,
                TransactionVerification.Create(
                    TransactionRecordKind.CleanupVerified,
                    entry.Mutation.OpaqueObjectId,
                    entry.Mutation.Path,
                    entry.StagedObject.Metadata.Sha256),
                CancellationToken.None);
        }

        foreach (var directory in createdDirectories.Reverse())
        {
            var permit = runtime.Store.Journal.AppendIntent(
                TransactionIntent.Create(
                    TransactionRecordKind.RollbackIntent,
                    directory.OpaqueObjectId,
                    directory.RelativePath,
                    EmptySha256),
                CancellationToken.None);
            runtime.FileOperations.RemoveCreatedDirectory(
                target,
                directory,
                permit,
                CancellationToken.None);
            runtime.Store.Journal.AppendVerified(
                permit,
                TransactionVerification.Create(
                    TransactionRecordKind.RollbackVerified,
                    directory.OpaqueObjectId,
                    directory.RelativePath,
                    EmptySha256),
                CancellationToken.None);
        }
    }

    private static ContentFileSnapshot CreateStagedSnapshot(StagedEntry entry)
    {
        var bytes = entry.Mutation.Mutation.AfterBytes.CopyBytes();
        try
        {
            var metadata = entry.StagedObject.Metadata;
            return ContentFileSnapshot.Create(
                entry.Mutation.Mutation.Change.RelativePath,
                true,
                bytes,
                metadata.LastWriteTimeUtc,
                (uint)metadata.Attributes,
                new ContentFileIdentity(
                    metadata.Identity.VolumeSerialNumber,
                    metadata.Identity.FileIdLow,
                    metadata.Identity.FileIdHigh));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static List<MutationEntry> OrderedMutations(AcceptedMigrationPlan plan) =>
        plan.AdapterStages
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SelectMany(pair => pair.Value.Mutations)
            .Select(mutation => new MutationEntry(
                mutation,
                NormalizeRequired(mutation.Change.RelativePath),
                OpaqueId("file", pairKey: mutation.Change.AdapterId + "\0" + mutation.Change.RelativePath.Value)))
            .OrderBy(entry => entry.Path.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Path.Value, StringComparer.Ordinal)
            .ToList();

    private static string OpaqueId(string prefix, string pairKey)
    {
        var bytes = global::System.Text.Encoding.UTF8.GetBytes(pairKey);
        try
        {
            return prefix + "-" + Convert.ToHexString(SHA256.HashData(bytes))[..24];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static string ComputeOpaqueObjectId(string prefix, string pairKey) =>
        OpaqueId(prefix, pairKey);

    internal static string AfterObjectId(string opaqueObjectId)
    {
        TransactionValueValidation.RequireOpaqueId(opaqueObjectId, nameof(opaqueObjectId));
        return "after-" + opaqueObjectId;
    }

    internal static string IdentityDigest(PhysicalDirectoryIdentity identity)
    {
        Span<byte> bytes = stackalloc byte[3 * sizeof(ulong)];
        global::System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
            bytes,
            identity.VolumeSerialNumber);
        global::System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
            bytes[sizeof(ulong)..],
            identity.FileIdLow);
        global::System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
            bytes[(2 * sizeof(ulong))..],
            identity.FileIdHigh);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static NormalizedRelativePath NormalizeRequired(ContentRelativePath path)
    {
        if (!WritePathGuard.TryNormalize(path, out var normalized) || normalized is null)
        {
            throw new InvalidOperationException("An accepted content path could not be normalized.");
        }

        return normalized;
    }

    private static bool ContentSnapshotMatches(ContentFileSnapshot actual, ContentFileSnapshot expected) =>
        actual.Exists == expected.Exists &&
        actual.Length == expected.Length &&
        string.Equals(actual.Sha256, expected.Sha256, StringComparison.Ordinal) &&
        actual.LastWriteTimeUtc == expected.LastWriteTimeUtc &&
        actual.WindowsFileAttributes == expected.WindowsFileAttributes &&
        actual.Identity == expected.Identity;

    private static bool FingerprintMatches(
        AcceptedMigrationPlan plan,
        DiscoveredInstancePair pair) =>
        pair.Generation == plan.AcceptedFingerprint.Generation &&
        string.Equals(pair.Source.Instance.Id, plan.SourceInstanceId, StringComparison.Ordinal) &&
        string.Equals(pair.Target.Instance.Id, plan.TargetInstanceId, StringComparison.Ordinal) &&
        pair.Source.GameRoot.Identity == plan.AcceptedFingerprint.SourceRootIdentity &&
        pair.Target.GameRoot.Identity == plan.AcceptedFingerprint.TargetRootIdentity;

    private static void EnsureAuthorityLive(
        AcceptedMigrationPlan plan,
        DiscoverySession session,
        string sourceId,
        string targetId,
        ContentAccessLease lease,
        ContentProbeContext context)
    {
        if (!ReferenceEquals(plan.Session, session) ||
            !ReferenceEquals(plan.ContentLease, lease) ||
            !ReferenceEquals(plan.ContentContext, context) ||
            !session.IsActive ||
            !lease.IsBoundTo(session, sourceId, targetId) ||
            !context.IsOwnedBy(lease))
        {
            throw new InvalidOperationException("The accepted migration authority became inactive.");
        }
    }

    private static ReadOnlyDictionary<string, IContentAdapter> CopyAdapters(
        IReadOnlyDictionary<string, IContentAdapter> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copy = new Dictionary<string, IContentAdapter>(StringComparer.Ordinal);
        foreach (var (id, adapter) in source)
        {
            if (adapter is null ||
                !string.Equals(id, adapter.Id, StringComparison.Ordinal) ||
                !copy.TryAdd(id, adapter))
            {
                throw new ArgumentException("The transaction adapter registry is invalid.", nameof(source));
            }
        }

        return copy.Count is > 0 and <= ContentContractLimits.MaximumAdapters
            ? new ReadOnlyDictionary<string, IContentAdapter>(copy)
            : throw new ArgumentException("A bounded transaction adapter registry is required.", nameof(source));
    }

    private static bool IsExpectedExecutionFailure(Exception exception) => exception is
        OperationCanceledException or
        IOException or
        UnauthorizedAccessException or
        InvalidOperationException or
        ArgumentException or
        ObjectDisposedException or
        CapabilityBoundaryException or
        CryptographicException or
        TransactionAuthenticationException;

    private static bool IsDurablyCommitted(
        AuthenticatedTransactionStore store,
        string acceptedPlanDigest)
    {
        var journal = store.Journal.ReadAndVerify(store.TransactionId, CancellationToken.None);
        if (journal.TerminalKind != TransactionRecordKind.Committed)
        {
            return false;
        }

        var expected = Convert.FromHexString(acceptedPlanDigest);
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                journal.Records[^1].ContentDigest,
                expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private static string SafeFailureMessage(Exception exception) => exception switch
    {
        MinecraftProcessBlockedException => "请先关闭来源或目标实例中的 Minecraft，再开始同步。",
        OperationCanceledException => "操作已取消，没有改动目标实例。",
        TargetMutexBusyException => "目标实例正在被另一个 BlockFerry 操作使用。",
        UnauthorizedAccessException => "目标实例权限不足；请以管理员身份重新打开 BlockFerry。",
        _ => "安全检查发现实例或文件已变化，请重新检查后再试。",
    };

    private static bool TryRetirePreNamespaceStore(
        AuthenticatedTransactionStore store,
        string acceptedPlanDigest)
    {
        try
        {
            var journal = store.Journal.ReadAndVerify(store.TransactionId, CancellationToken.None);
            if (journal.TerminalKind == TransactionRecordKind.RolledBack)
            {
                return true;
            }

            if (journal.IsTerminal ||
                journal.Records.Any(record => record.Kind is
                    TransactionRecordKind.DirectoryIntent or
                    TransactionRecordKind.DirectoryCreated or
                    TransactionRecordKind.StageIntent or
                    TransactionRecordKind.StageCreated or
                    TransactionRecordKind.StageVerified or
                    TransactionRecordKind.CommitIntent or
                    TransactionRecordKind.CommitVerified or
                    TransactionRecordKind.RollbackIntent or
                    TransactionRecordKind.RollbackVerified or
                    TransactionRecordKind.CleanupIntent or
                    TransactionRecordKind.CleanupVerified or
                    TransactionRecordKind.RecoveryRequired))
            {
                return false;
            }

            if (journal.Records[^1].Kind == TransactionRecordKind.BackupIntent)
            {
                var pending = store.Plan.Paths.SingleOrDefault(path =>
                    string.Equals(
                        OpaqueId("file", path.AdapterId + "\0" + path.RelativePath.Value),
                        journal.Records[^1].OpaqueObjectId,
                        StringComparison.Ordinal));
                if (pending is null)
                {
                    return false;
                }

                store.Journal.AppendIntentAborted(
                    TransactionRecordKind.BackupIntent,
                    journal.Records[^1].OpaqueObjectId,
                    pending.RelativePath,
                    CancellationToken.None);
            }

            var rollbackPath = store.Plan.Paths[0];
            var rollbackObjectId = OpaqueId(
                "file",
                rollbackPath.AdapterId + "\0" + rollbackPath.RelativePath.Value);
            _ = store.Journal.AppendIntent(
                TransactionIntent.Create(
                    TransactionRecordKind.RollbackIntent,
                    rollbackObjectId,
                    rollbackPath.RelativePath,
                    rollbackPath.ExpectedAfterSha256),
                CancellationToken.None);
            store.Journal.AppendIntentAborted(
                TransactionRecordKind.RollbackIntent,
                rollbackObjectId,
                rollbackPath.RelativePath,
                CancellationToken.None);
            store.Journal.AppendTerminal(
                TransactionRecordKind.RolledBack,
                acceptedPlanDigest,
                CancellationToken.None);
            return store.Journal
                .ReadAndVerify(store.TransactionId, CancellationToken.None)
                .TerminalKind == TransactionRecordKind.RolledBack;
        }
        catch (Exception exception) when (IsExpectedExecutionFailure(exception))
        {
            return false;
        }
    }

    private void TryMarkRecoveryRequired(TransactionJournal journal, string digest)
    {
        try
        {
            var current = journal.ReadAndVerify(journal.TransactionId, CancellationToken.None);
            if (!current.IsTerminal &&
                current.Records[^1].Kind != TransactionRecordKind.RecoveryRequired)
            {
                journal.AppendTerminal(
                    TransactionRecordKind.RecoveryRequired,
                    digest,
                    CancellationToken.None);
                faultInjector.Hit(MigrationFaultPoint.RecoveryRequiredFlushed);
            }
        }
        catch (Exception exception) when (IsExpectedExecutionFailure(exception))
        {
            // A non-authentic or unflushable journal is intentionally left for startup recovery.
        }
    }

    private static void Report(
        IProgress<MigrationProgress>? progress,
        MigrationProgressStage stage,
        int completed,
        int total,
        string message) =>
        progress?.Report(new MigrationProgress(stage, completed, total, message));

    private sealed record MutationEntry(
        StagedFileMutation Mutation,
        NormalizedRelativePath Path,
        string OpaqueObjectId);

    private sealed record StagedEntry(MutationEntry Mutation, StagedObject StagedObject);

    internal sealed class PostCommitCleanupAuthority
    {
        private readonly TransactionId transactionId;
        private readonly PhysicalDirectoryIdentity targetRootIdentity;
        private readonly DisplacedObject displaced;
        private readonly string opaqueObjectId;
        private readonly NormalizedRelativePath relativePath;
        private readonly string retainedPath;
        private readonly FileMetadataSnapshot metadata;
        private int consumed;

        private PostCommitCleanupAuthority(
            object seal,
            TransactionId transactionId,
            PhysicalDirectoryIdentity targetRootIdentity,
            DisplacedObject displaced)
        {
            if (!ReferenceEquals(seal, PostCommitCleanupAuthoritySeal))
            {
                throw new InvalidOperationException(
                    "Post-commit cleanup authority can only be issued after the durable decision.");
            }

            this.transactionId = transactionId;
            this.targetRootIdentity = targetRootIdentity;
            this.displaced = displaced ?? throw new ArgumentNullException(nameof(displaced));
            opaqueObjectId = displaced.OpaqueObjectId;
            relativePath = displaced.RelativePath;
            retainedPath = displaced.RetainedPath;
            metadata = displaced.Metadata;
        }

        internal static PostCommitCleanupAuthority Issue(
            object seal,
            TransactionId transactionId,
            PhysicalDirectoryIdentity targetRootIdentity,
            DisplacedObject displaced) =>
            new(seal, transactionId, targetRootIdentity, displaced);

        internal void Consume(
            TransactionId leaseTransactionId,
            PhysicalDirectoryIdentity leaseTargetRootIdentity,
            DisplacedObject candidate)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            if (Interlocked.Exchange(ref consumed, 1) != 0 ||
                leaseTransactionId != transactionId ||
                leaseTargetRootIdentity != targetRootIdentity ||
                !ReferenceEquals(candidate, displaced) ||
                !string.Equals(
                    candidate.OpaqueObjectId,
                    opaqueObjectId,
                    StringComparison.Ordinal) ||
                !NormalizedRelativePathComparer.Instance.Equals(
                    candidate.RelativePath,
                    relativePath) ||
                !string.Equals(
                    candidate.RetainedPath,
                    retainedPath,
                    StringComparison.OrdinalIgnoreCase) ||
                candidate.Metadata.Identity != metadata.Identity ||
                !candidate.Metadata.SemanticallyEquals(metadata))
            {
                throw new InvalidOperationException(
                    "The post-commit cleanup authority did not match the retained displaced object.");
            }
        }
    }

    private sealed class CommittedEntry : IDisposable
    {
        private CommittedEntry(
            MutationEntry mutation,
            ReplaceOutcome? outcome,
            CommittedObject? created)
        {
            Mutation = mutation;
            Outcome = outcome;
            Created = created;
        }

        internal MutationEntry Mutation { get; }

        internal ReplaceOutcome? Outcome { get; }

        internal CommittedObject? Created { get; }

        internal CommittedObject Final => Outcome?.Replacement ?? Created!;

        internal static CommittedEntry Existing(StagedEntry staged, ReplaceOutcome outcome) =>
            new(staged.Mutation, outcome, null);

        internal static CommittedEntry Missing(StagedEntry staged, CommittedObject created) =>
            new(staged.Mutation, null, created);

        public void Dispose()
        {
            Outcome?.Dispose();
            Created?.Dispose();
        }
    }
}
