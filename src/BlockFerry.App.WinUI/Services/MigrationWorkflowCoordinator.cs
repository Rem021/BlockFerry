using System.Collections.ObjectModel;
using System.Security.Cryptography;
using BlockFerry.App.WinUI.Discovery;
using BlockFerry.App.WinUI.Selection;
using BlockFerry.Core.Content;
using BlockFerry.Core.Discovery;
using BlockFerry.Core.Mods;
using BlockFerry.Core.Pcl2;
using BlockFerry.Core.Processes;
using BlockFerry.Core.Transactions;

namespace BlockFerry.App.WinUI.Services;

internal sealed class MigrationWorkflowCoordinator : IDisposable
{
    private static readonly ModProbeLimits ProductionModProbeLimits = new(
        MaximumJarFiles: 2_048,
        MaximumZipEntries: 65_536,
        MaximumEntryBytes: 2 * 1024 * 1024,
        MaximumTotalBytes: 32L * 1024 * 1024,
        MaximumArchiveBytes: 256L * 1024 * 1024,
        MaximumCentralDirectoryBytes: 32L * 1024 * 1024);

    private readonly object stateGate = new();
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly IDiscoveryRequestService discoveryService;
    private readonly CapabilityBoundInstanceAccessFactory accessFactory;
    private readonly ContentCompatibilityProbe compatibilityProbe;
    private readonly ReadOnlyDictionary<string, IContentAdapter> adapters;
    private readonly AcceptedMigrationPlanFactory acceptedPlanFactory;
    private readonly MigrationTransactionCoordinator transactionCoordinator;
    private readonly TransactionRecoveryService recoveryService;
    private readonly RecoverySelectionResolver recoverySelectionResolver;
    private readonly CompletionSoundGate completionSound;
    private readonly DeferredJeiSyncCoordinator deferredJei;
    private readonly MinecraftProcessGuard processGuard;
    private readonly UndoEligibilityRefreshGate undoEligibility;
    private readonly PendingRescanPublisher pendingRescanPublisher;
    private readonly TaskCompletionSource disposalCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task deferredJeiMonitor;
    private readonly Dictionary<TransactionId, VerifiedRecoverySelection> recoverySelections = [];
    private MigrationWorkflowState state = MigrationWorkflowState.Initial;
    private CoreDiscoverySessionHandle? activeSession;
    private ContentAccessLease? contentLease;
    private ContentProbeContext? contentContext;
    private AcceptedMigrationPlan? acceptedPlan;
    private DeferredJeiSyncRecord? activeDeferredJei;
    private TransactionId? pausedDeferredJei;
    private bool deferredJeiNeedsAttempt;
    private bool deferredJeiObservedRunningGame;
    private IFileSavePickerService? fileSavePicker;
    private long discoveryGeneration;
    private long mutationOperation;
    private bool recoveryCheckPassed;
    private bool disposed;

    internal MigrationWorkflowCoordinator(
        IDiscoveryRequestService discoveryService,
        CapabilityBoundInstanceAccessFactory accessFactory,
        ContentCompatibilityProbe compatibilityProbe,
        IReadOnlyDictionary<string, IContentAdapter> adapters,
        AcceptedMigrationPlanFactory acceptedPlanFactory,
        MigrationTransactionCoordinator transactionCoordinator,
        TransactionRecoveryService recoveryService,
        RecoverySelectionResolver recoverySelectionResolver,
        CompletionSoundGate completionSound,
        DeferredJeiSyncCoordinator deferredJei,
        MinecraftProcessGuard processGuard)
    {
        this.discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
        this.accessFactory = accessFactory ?? throw new ArgumentNullException(nameof(accessFactory));
        this.compatibilityProbe = compatibilityProbe ?? throw new ArgumentNullException(nameof(compatibilityProbe));
        this.adapters = new ReadOnlyDictionary<string, IContentAdapter>(
            new Dictionary<string, IContentAdapter>(
                adapters ?? throw new ArgumentNullException(nameof(adapters)),
                StringComparer.Ordinal));
        this.acceptedPlanFactory = acceptedPlanFactory ?? throw new ArgumentNullException(nameof(acceptedPlanFactory));
        this.transactionCoordinator = transactionCoordinator ?? throw new ArgumentNullException(nameof(transactionCoordinator));
        this.recoveryService = recoveryService ?? throw new ArgumentNullException(nameof(recoveryService));
        this.recoverySelectionResolver = recoverySelectionResolver ?? throw new ArgumentNullException(nameof(recoverySelectionResolver));
        this.completionSound = completionSound ?? throw new ArgumentNullException(nameof(completionSound));
        this.deferredJei = deferredJei ?? throw new ArgumentNullException(nameof(deferredJei));
        this.processGuard = processGuard ?? throw new ArgumentNullException(nameof(processGuard));
        undoEligibility = new UndoEligibilityRefreshGate(recoveryService.IsUndoEligibleAsync);
        pendingRescanPublisher = new PendingRescanPublisher(
            recoveryService.FindPending,
            () => State,
            Publish,
            SetRecoveryCheckPassed,
            IsRecoverable,
            lifetime.Token);
        deferredJeiMonitor = Task.Run(MonitorDeferredJeiAsync);
    }

    internal event EventHandler<MigrationWorkflowState>? StateChanged;

    internal Task DisposalCompletion => disposalCompletion.Task;

    internal MigrationWorkflowState State
    {
        get
        {
            lock (stateGate)
            {
                return state;
            }
        }
    }

    internal bool CanDiscoverCurrent
    {
        get
        {
            lock (stateGate)
            {
                return MigrationWorkflowPolicy.CanDiscover(
                    recoveryCheckPassed,
                    state.Phase);
            }
        }
    }

    internal bool CanRecoverCurrent
    {
        get
        {
            lock (stateGate)
            {
                var pending = state.PendingRecovery;
                return pending is not null &&
                       MigrationWorkflowPolicy.CanRecover(
                           pending.AttentionStatus,
                           pending.TargetPathAvailable,
                           recoverySelections.ContainsKey(pending.TransactionId));
            }
        }
    }

    internal void AttachFileSavePicker(IFileSavePickerService picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        lock (stateGate)
        {
            ThrowIfDisposedLocked();
            fileSavePicker = picker;
        }
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var linked = Link(cancellationToken);
        await operationGate.WaitAsync(linked.Token);
        try
        {
            ThrowIfDisposed();
            Publish(MigrationWorkflowState.Initial);
            var noPendingRecovery = await RescanPendingAndPublishAsync(
                MigrationWorkflowState.Initial with
                {
                    Phase = MigrationWorkflowPhase.AwaitingDiscovery,
                    StatusText = "恢复检查完成，可以自动探测或选择文件夹。",
                },
                "发现上次未完成的同步；请先安全恢复。",
                linked.Token);
            if (noPendingRecovery)
            {
                activeDeferredJei = deferredJei.Load(linked.Token)
                    .OrderByDescending(record => record.CreatedUtc)
                    .FirstOrDefault();
                if (activeDeferredJei is not null)
                {
                    ResetDeferredJeiObservation();
                    var generation = checked(Interlocked.Increment(ref discoveryGeneration));
                    Publish(State with
                    {
                        Phase = MigrationWorkflowPhase.Discovering,
                        StatusText = "正在恢复上次 JEI 收藏的自动复核…",
                    });
                    var result = await Task.Run(
                        () => discoveryService.DiscoverAutomatically(generation, linked.Token),
                        linked.Token);
                    await AcceptDiscoveryAsync(result, linked.Token);
                }
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task AutoDiscoverAsync(CancellationToken cancellationToken)
    {
        using var linked = Link(cancellationToken);
        await operationGate.WaitAsync(linked.Token);
        try
        {
            ThrowIfDisposed();
            if (!CanDiscover(State))
            {
                return;
            }

            var generation = checked(Interlocked.Increment(ref discoveryGeneration));
            Publish(State with
            {
                Phase = MigrationWorkflowPhase.Discovering,
                StatusText = "正在自动探测 PCL 与 Minecraft 实例…",
                Progress = null,
            });
            var result = await Task.Run(
                () => discoveryService.DiscoverAutomatically(generation, linked.Token),
                linked.Token);
            await AcceptDiscoveryAsync(result, linked.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            PublishBlocked("自动探测未完成；没有修改任何实例。", MigrationExecutionStatus.Blocked);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task AddSelectedFolderAsync(
        string selectedPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        using var linked = Link(cancellationToken);
        await operationGate.WaitAsync(linked.Token);
        try
        {
            ThrowIfDisposed();
            if (!CanDiscover(State))
            {
                return;
            }

            var generation = checked(Interlocked.Increment(ref discoveryGeneration));
            Publish(State with
            {
                Phase = MigrationWorkflowPhase.Discovering,
                StatusText = "正在验证所选文件夹并发现实例…",
                Progress = null,
            });
            var result = await Task.Run(
                () => discoveryService.DiscoverManual(
                    generation,
                    selectedPath,
                    linked.Token),
                linked.Token);
            await AcceptDiscoveryAsync(result, linked.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            PublishBlocked("文件夹验证未完成；原有选择保持不变。", MigrationExecutionStatus.Blocked);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task SelectPairAsync(
        string sourceId,
        string targetId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        using var linked = Link(cancellationToken);
        await operationGate.WaitAsync(linked.Token);
        try
        {
            ThrowIfDisposed();
            if (State.IsMutationInProgress ||
                activeSession is null ||
                !activeSession.IsActive ||
                !activeSession.CanPair(sourceId, targetId))
            {
                PublishBlocked("请选择两个不同且可安全访问的实例。", MigrationExecutionStatus.Blocked);
                return;
            }

            await PrepareCatalogsAsync(activeSession, sourceId, targetId, linked.Token);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task PreparePlanAsync(
        ContentSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        using var linked = Link(cancellationToken);
        await operationGate.WaitAsync(linked.Token);
        try
        {
            ThrowIfDisposed();
            var snapshot = State;
            var session = activeSession?.Session;
            var lease = contentLease;
            var context = contentContext;
            if (snapshot.IsMutationInProgress ||
                snapshot.Phase is MigrationWorkflowPhase.RecoveryRequired or MigrationWorkflowPhase.Demo ||
                session is null ||
                lease is null ||
                context is null ||
                snapshot.SourceInstanceId is null ||
                snapshot.TargetInstanceId is null)
            {
                return;
            }

            acceptedPlan = null;
            Publish(snapshot with
            {
                Phase = MigrationWorkflowPhase.Selecting,
                StatusText = "正在重新读取并检查所选内容…",
                CanExecute = false,
                ReviewItems = Array.Empty<ContentPlanItem>(),
            });
            var preparation = await Task.Run(
                () => CreateAcceptedPlan(
                    session,
                    snapshot.SourceInstanceId,
                    snapshot.TargetInstanceId,
                    lease,
                    context,
                    snapshot.Catalogs,
                    selection,
                    linked.Token),
                linked.Token);
            if (preparation.Plan is null || preparation.ContentPlan is null)
            {
                Publish(State with
                {
                    Phase = MigrationWorkflowPhase.Selecting,
                    StatusText = preparation.Message,
                    CanExecute = false,
                    PlannedFileCount = 0,
                    PlannedItemCount = 0,
                });
                return;
            }

            acceptedPlan = preparation.Plan;
            var reviewItems = preparation.ContentPlan.Items.ToArray();
            Publish(State with
            {
                Phase = MigrationWorkflowPhase.Reviewing,
                ReviewItems = reviewItems,
                StatusText = $"已检查 {reviewItems.Count(IsActionable)} 项内容；确认后将先备份再同步。",
                PlannedFileCount = preparation.ContentPlan.FileChanges.Count,
                PlannedItemCount = reviewItems.Count(IsActionable),
                CanExecute = true,
                CanUndo = false,
                LastExecutionStatus = null,
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            acceptedPlan = null;
            PublishBlocked("同步计划未能通过复核；请重新选择内容。", MigrationExecutionStatus.RejectedStale);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var linked = Link(cancellationToken);
        await operationGate.WaitAsync(linked.Token);
        try
        {
            ThrowIfDisposed();
            var snapshot = State;
            var plan = acceptedPlan;
            var session = activeSession?.Session;
            var lease = contentLease;
            var context = contentContext;
            if (!snapshot.CanExecute ||
                snapshot.Phase != MigrationWorkflowPhase.Reviewing ||
                plan is null ||
                session is null ||
                lease is null ||
                context is null ||
                snapshot.SourceInstanceId is null ||
                snapshot.TargetInstanceId is null)
            {
                return;
            }

            Publish(snapshot with
            {
                Phase = MigrationWorkflowPhase.Executing,
                StatusText = "正在创建还原点并安全同步，请保持窗口打开。",
                CanExecute = false,
                Progress = new MigrationProgress(
                    MigrationProgressStage.Revalidating,
                    0,
                    1,
                    "正在重新确认实例与同步清单"),
            });
            var operation = BeginMutationOperation();
            var progress = new Progress<MigrationProgress>(value =>
                PublishMutationProgress(operation, value));
            MigrationExecutionResult result;
            try
            {
                result = await transactionCoordinator.ExecuteAsync(
                    plan,
                    session,
                    snapshot.SourceInstanceId,
                    snapshot.TargetInstanceId,
                    lease,
                    context,
                    progress,
                    linked.Token);
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
            {
                acceptedPlan = null;
                _ = await RescanPendingAfterOutcomeAndPublishAsync(
                    State with
                    {
                        Phase = MigrationWorkflowPhase.Blocked,
                        PendingRecovery = null,
                        StatusText = "同步已取消；已重新检查未完成事务。",
                        LastExecutionStatus = MigrationExecutionStatus.CancelledBeforeMutation,
                        CanExecute = false,
                        CanUndo = false,
                    },
                    "取消后仍发现未完成的同步；请先安全恢复。",
                    lifetime.Token);
                throw;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                acceptedPlan = null;
                _ = await RescanPendingAfterOutcomeAndPublishAsync(
                    State with
                    {
                        Phase = MigrationWorkflowPhase.Blocked,
                        PendingRecovery = null,
                        StatusText = "同步未完成；已重新检查未完成事务。",
                        LastExecutionStatus = MigrationExecutionStatus.Blocked,
                        CanExecute = false,
                        CanUndo = false,
                    },
                    "同步失败后仍有未完成事务；请先安全恢复。",
                    lifetime.Token);
                return;
            }
            finally
            {
                EndMutationOperation(operation);
            }

            acceptedPlan = null;
            if (result.IsSuccess && result.TransactionId is { } committed)
            {
                var pendingJei = deferredJei.CreatePendingRecord(
                    plan,
                    committed,
                    DateTimeOffset.UtcNow);
                if (pendingJei is not null)
                {
                    var durable = deferredJei.Persist(pendingJei, lifetime.Token);
                    activeDeferredJei = pendingJei;
                    ResetDeferredJeiObservation();
                    Publish(State with
                    {
                        Phase = MigrationWorkflowPhase.Succeeded,
                        StatusText = durable
                            ? "设置已复读验证；JEI 收藏已安全预置，首次进入目标服务器并关闭 Minecraft 后会自动复核。"
                            : "设置已复读验证；JEI 收藏已安全预置，请保持 BlockFerry 打开，关闭 Minecraft 后会自动复核。",
                        Progress = new MigrationProgress(
                            MigrationProgressStage.Completed,
                            1,
                            1,
                            "基础设置已验证，等待 JEI 真实作用域"),
                        CommittedTransactionId = committed,
                        LastExecutionStatus = result.Status,
                        CanUndo = false,
                        CanExecute = false,
                        HasDeferredJeiSync = true,
                    });
                    return;
                }

                Publish(State with
                {
                    Phase = MigrationWorkflowPhase.Succeeded,
                    StatusText = $"同步完成并复读验证：已写入 {result.CommittedFileCount} 个文件。",
                    Progress = new MigrationProgress(
                        MigrationProgressStage.Completed,
                        1,
                        1,
                        "同步已验证"),
                    CommittedTransactionId = committed,
                    LastExecutionStatus = result.Status,
                    CanUndo = false,
                    CanExecute = false,
                    HasDeferredJeiSync = false,
                });
                await RefreshUndoEligibilityCoreAsync(committed, lifetime.Token);
                return;
            }

            var unsuccessful = State with
            {
                Phase = MigrationWorkflowPhase.Blocked,
                StatusText = ResultMessage(result),
                LastExecutionStatus = result.Status,
                CanUndo = false,
                CanExecute = false,
                PendingRecovery = null,
            };
            _ = await RescanPendingAfterOutcomeAndPublishAsync(
                unsuccessful,
                result.Status == MigrationExecutionStatus.RecoveryRequired
                    ? "同步未能安全结束；请先执行恢复，BlockFerry 不会猜测后续写入。"
                    : "仍有未完成的同步；请先安全恢复。",
                linked.Token,
                result.TransactionId);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task SupplyRecoveryFolderAsync(
        TransactionId transactionId,
        string selectedPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        using var linked = Link(cancellationToken);
        await operationGate.WaitAsync(linked.Token);
        try
        {
            ThrowIfDisposed();
            var generation = checked(Interlocked.Increment(ref discoveryGeneration));
            var result = await Task.Run(
                () => recoverySelectionResolver.Resolve(
                    transactionId,
                    generation,
                    selectedPath,
                    linked.Token),
                linked.Token);
            var selection = result.Selection;
            if (selection is null ||
                selection.RecordedTargetIdentity != selection.Target.GameRoot.Identity)
            {
                RemoveRecoverySelection(transactionId);
                Publish(State with
                {
                    Phase = MigrationWorkflowPhase.RecoveryRequired,
                    StatusText = "所选文件夹不是上次记录的同一个物理实例，请重新选择。",
                });
                return;
            }

            SetRecoverySelection(transactionId, selection);
            Publish(State with
            {
                Phase = MigrationWorkflowPhase.RecoveryRequired,
                StatusText = "已确认是同一个物理实例，可以开始安全恢复。",
            });
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task RecoverAsync(
        TransactionId transactionId,
        VerifiedRecoverySelection? reselection,
        CancellationToken cancellationToken)
    {
        using var linked = Link(cancellationToken);
        await operationGate.WaitAsync(linked.Token);
        try
        {
            ThrowIfDisposed();
            if (reselection is null)
            {
                reselection = GetRecoverySelection(transactionId);
            }

            Publish(State with
            {
                Phase = MigrationWorkflowPhase.RollingBack,
                StatusText = "正在验证还原点并恢复上次未完成的同步…",
            });
            var operation = BeginMutationOperation();
            MigrationRecoveryResult result;
            try
            {
                result = await recoveryService.RecoverAsync(
                    transactionId,
                    reselection,
                    new Progress<MigrationProgress>(value =>
                        PublishMutationProgress(operation, value)),
                    linked.Token);
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
            {
                _ = await RescanPendingAfterOutcomeAndPublishAsync(
                    State with
                    {
                        Phase = MigrationWorkflowPhase.Blocked,
                        PendingRecovery = null,
                        StatusText = "恢复已取消；已重新检查所有未完成事务。",
                        CanExecute = false,
                        CanUndo = false,
                    },
                    "恢复取消后事务仍未完成；请继续安全恢复。",
                    lifetime.Token,
                    transactionId);
                throw;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                _ = await RescanPendingAfterOutcomeAndPublishAsync(
                    State with
                    {
                        Phase = MigrationWorkflowPhase.Blocked,
                        PendingRecovery = null,
                        StatusText = "恢复未完成；已重新检查所有未完成事务。",
                        CanExecute = false,
                        CanUndo = false,
                    },
                    "恢复失败后事务仍未完成；请继续安全恢复。",
                    lifetime.Token,
                    transactionId);
                return;
            }
            finally
            {
                EndMutationOperation(operation);
            }

            if (result.IsRecovered || result.Status is
                MigrationRecoveryStatus.AuthenticationFailed or
                MigrationRecoveryStatus.TargetReselectionRequired)
            {
                RemoveRecoverySelection(transactionId);
            }

            var whenNone = result.IsRecovered ||
                           result.Status == MigrationRecoveryStatus.AlreadyTerminal
                ? MigrationWorkflowState.Initial with
                {
                    Phase = MigrationWorkflowPhase.AwaitingDiscovery,
                    StatusText = result.Status == MigrationRecoveryStatus.Recovered
                        ? $"恢复完成：已还原 {result.RestoredFileCount} 个文件，可以重新探测实例。"
                        : "恢复记录已经处于终态，可以重新探测实例。",
                }
                : State with
                {
                    Phase = MigrationWorkflowPhase.Blocked,
                    PendingRecovery = null,
                    StatusText = result.Message,
                    CanExecute = false,
                    CanUndo = false,
                };
            _ = await RescanPendingAfterOutcomeAndPublishAsync(
                whenNone,
                result.IsRecovered
                    ? "本次恢复已完成，但仍有另一项未完成的同步；请继续恢复。"
                    : result.Message,
                linked.Token,
                transactionId,
                result.Status);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task UndoAsync(
        TransactionId transactionId,
        CancellationToken cancellationToken)
    {
        using var linked = Link(cancellationToken);
        await operationGate.WaitAsync(linked.Token);
        try
        {
            ThrowIfDisposed();
            if (State.Phase != MigrationWorkflowPhase.Succeeded ||
                State.CommittedTransactionId != transactionId ||
                !State.CanUndo ||
                State.HasDeferredJeiSync)
            {
                return;
            }

            Publish(State with
            {
                Phase = MigrationWorkflowPhase.RollingBack,
                StatusText = "正在验证当前文件并撤销这次同步…",
                CanUndo = false,
            });
            var operation = BeginMutationOperation();
            MigrationUndoResult result;
            try
            {
                result = await recoveryService.UndoAsync(
                    transactionId,
                    new Progress<MigrationProgress>(value =>
                        PublishMutationProgress(operation, value)),
                    linked.Token);
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
            {
                _ = await RescanPendingAfterOutcomeAndPublishAsync(
                    State with
                    {
                        Phase = MigrationWorkflowPhase.Blocked,
                        PendingRecovery = null,
                        StatusText = "撤销已取消；已重新检查所有未完成事务。",
                        CommittedTransactionId = transactionId,
                        CanExecute = false,
                        CanUndo = false,
                    },
                    "撤销取消后仍有未完成事务；请先安全恢复。",
                    lifetime.Token);
                throw;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                _ = await RescanPendingAfterOutcomeAndPublishAsync(
                    State with
                    {
                        Phase = MigrationWorkflowPhase.Blocked,
                        PendingRecovery = null,
                        StatusText = "撤销未完成；已重新检查所有未完成事务。",
                        CommittedTransactionId = transactionId,
                        CanExecute = false,
                        CanUndo = false,
                    },
                    "撤销失败后仍有未完成事务；请先安全恢复。",
                    lifetime.Token);
                return;
            }
            finally
            {
                EndMutationOperation(operation);
            }

            var disposition = MigrationWorkflowPolicy.ResolveUndoResult(result.Status);
            var resolvedPhase = disposition.Phase == MigrationWorkflowPhase.RecoveryRequired
                ? MigrationWorkflowPhase.Blocked
                : disposition.Phase;
            var whenNone = State with
            {
                Phase = resolvedPhase,
                StatusText = result.Status == MigrationRecoveryStatus.RecoveryRequired
                    ? "撤销需要恢复，但暂时无法验证恢复位置；请重新打开 BlockFerry 后继续。"
                    : result.IsUndone
                        ? $"已安全撤销：恢复 {result.RestoredFileCount} 个文件。"
                        : result.Message,
                PendingRecovery = null,
                LastExecutionStatus = result.IsUndone
                    ? MigrationExecutionStatus.RolledBack
                    : result.Status == MigrationRecoveryStatus.RecoveryRequired
                        ? MigrationExecutionStatus.RecoveryRequired
                        : MigrationExecutionStatus.Blocked,
                CommittedTransactionId = disposition.KeepCommittedTransaction
                    ? transactionId
                    : null,
                CanUndo = false,
                CanExecute = false,
            };
            var noPending = await RescanPendingAfterOutcomeAndPublishAsync(
                whenNone,
                result.Status == MigrationRecoveryStatus.RecoveryRequired
                    ? "撤销尚未安全结束；请先执行恢复。"
                    : "仍有未完成的同步；请先安全恢复。",
                linked.Token,
                result.UndoTransactionId,
                result.Status);
            if (noPending && disposition.CanRetryUndo)
            {
                await RefreshUndoEligibilityCoreAsync(transactionId, linked.Token);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task ExportRecoveryDiagnosticAsync(
        TransactionId transactionId,
        CancellationToken cancellationToken)
    {
        using var linked = Link(cancellationToken);
        await operationGate.WaitAsync(linked.Token);
        try
        {
            ThrowIfDisposed();
            IFileSavePickerService? picker;
            lock (stateGate)
            {
                picker = fileSavePicker;
            }

            if (picker is null)
            {
                Publish(State with { StatusText = "诊断保存器尚未就绪。" });
                return;
            }

            var diagnostic = await Task.Run(
                () => recoveryService.ExportRedactedDiagnostic(transactionId, linked.Token),
                linked.Token);
            var bytes = diagnostic.CopyBytes();
            try
            {
                var saved = await picker.SaveDiagnosticAsync(
                    $"BlockFerry-recovery-{transactionId.Value:N}.json",
                    bytes,
                    linked.Token);
                Publish(State with
                {
                    StatusText = saved ? "脱敏诊断已保存到你选择的文件。" : "已取消保存诊断。",
                });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task RefreshUndoEligibilityAsync(CancellationToken cancellationToken)
    {
        using var linked = Link(cancellationToken);
        await operationGate.WaitAsync(linked.Token);
        try
        {
            ThrowIfDisposed();
            var snapshot = State;
            if (snapshot.CommittedTransactionId is not { } transactionId ||
                snapshot.Phase != MigrationWorkflowPhase.Succeeded ||
                snapshot.HasDeferredJeiSync)
            {
                if (snapshot.CanUndo)
                {
                    Publish(snapshot with { CanUndo = false });
                }

                return;
            }

            Publish(snapshot with { CanUndo = false });
            await RefreshUndoEligibilityCoreAsync(transactionId, linked.Token);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task RefreshUndoEligibilityCoreAsync(
        TransactionId transactionId,
        CancellationToken cancellationToken)
    {
        var snapshot = State;
        if (snapshot.Phase != MigrationWorkflowPhase.Succeeded ||
            snapshot.CommittedTransactionId != transactionId ||
            snapshot.HasDeferredJeiSync)
        {
            if (snapshot.CanUndo)
            {
                Publish(snapshot with { CanUndo = false });
            }

            return;
        }

        if (snapshot.CanUndo)
        {
            snapshot = snapshot with { CanUndo = false };
            Publish(snapshot);
        }

        var eligible = await undoEligibility.EvaluateAsync(snapshot, cancellationToken);
        var current = State;
        if (current.Phase == MigrationWorkflowPhase.Succeeded &&
            current.Generation == snapshot.Generation &&
            current.CommittedTransactionId == transactionId)
        {
            Publish(current with { CanUndo = eligible });
        }
    }

    internal bool TryPlayCommittedSound(
        long acceptedGeneration,
        TransactionId transactionId,
        bool resultPresented,
        bool focusAccepted,
        bool validAutomationPeer,
        bool notificationInvokedSuccessfully)
    {
        var snapshot = State;
        var durableVerifiedCommit = snapshot.Phase == MigrationWorkflowPhase.Succeeded &&
                                    snapshot.LastExecutionStatus == MigrationExecutionStatus.Succeeded &&
                                    !snapshot.HasDeferredJeiSync &&
                                    snapshot.Generation == acceptedGeneration &&
                                    snapshot.CommittedTransactionId == transactionId;
        return completionSound.TryPlayCommitted(
            acceptedGeneration,
            transactionId,
            durableVerifiedCommit,
            resultPresented,
            focusAccepted,
            validAutomationPeer,
            notificationInvokedSuccessfully);
    }

    internal void EnterDemo()
    {
        if (!operationGate.Wait(0))
        {
            return;
        }

        try
        {
            ThrowIfDisposed();
            ResetAcceptedContext(disposeSession: true);
            Publish(MigrationWorkflowState.Initial with
            {
                Phase = MigrationWorkflowPhase.Demo,
                ViewState = MigrationViewState.Demo,
                StatusText = "演示使用内存固定数据，不会访问或修改 Minecraft 实例。",
            });
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal void InvalidatePlan()
    {
        if (!operationGate.Wait(0))
        {
            return;
        }

        try
        {
            if (State.IsMutationInProgress)
            {
                return;
            }

            acceptedPlan = null;
            if (State.Phase == MigrationWorkflowPhase.Reviewing)
            {
                Publish(State with
                {
                    Phase = MigrationWorkflowPhase.Selecting,
                    ReviewItems = Array.Empty<ContentPlanItem>(),
                    PlannedFileCount = 0,
                    PlannedItemCount = 0,
                    CanExecute = false,
                    StatusText = "选择已变化，请重新检查同步计划。",
                });
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public void Dispose()
    {
        lock (stateGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        lifetime.Cancel();
        if (operationGate.Wait(0))
        {
            CompleteDisposalWithGateHeld();
            return;
        }

        _ = Task.Run(CompleteDisposalAfterOperation);
    }

    private void CompleteDisposalAfterOperation()
    {
        try
        {
            _ = operationGate.Wait(Timeout.Infinite);
            CompleteDisposalWithGateHeld();
        }
        catch (Exception exception)
        {
            _ = disposalCompletion.TrySetException(exception);
        }
    }

    private void CompleteDisposalWithGateHeld()
    {
        try
        {
            deferredJeiMonitor.GetAwaiter().GetResult();
            ResetAcceptedContext(disposeSession: true);
            discoveryService.Dispose();
            _ = disposalCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _ = disposalCompletion.TrySetException(exception);
            throw;
        }
        finally
        {
            operationGate.Release();
            operationGate.Dispose();
            lifetime.Dispose();
        }
    }

    private async Task AcceptDiscoveryAsync(
        DiscoveryRequestResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        var replacement = result.Session as CoreDiscoverySessionHandle;
        (string SourceId, string TargetId)? deferredPair =
            replacement is null || activeDeferredJei is null ||
            !replacement.CanPair(
                activeDeferredJei.SourceInstanceId,
                activeDeferredJei.TargetInstanceId)
                ? null
                : (activeDeferredJei.SourceInstanceId, activeDeferredJei.TargetInstanceId);
        var suggested = deferredPair ?? (replacement is null ? null : FindSuggestedPair(replacement));
        if (replacement is null || !replacement.IsActive || suggested is null)
        {
            result.Session?.Dispose();
            Publish(State with
            {
                Phase = MigrationWorkflowPhase.Blocked,
                StatusText = result.StatusText,
            });
            return;
        }

        ResetAcceptedContext(disposeSession: true);
        activeSession = replacement;
        completionSound.ResetForNewGeneration(replacement.Generation);
        await PrepareCatalogsAsync(
            replacement,
            suggested.Value.SourceId,
            suggested.Value.TargetId,
            cancellationToken);
    }

    private async Task PrepareCatalogsAsync(
        CoreDiscoverySessionHandle sessionHandle,
        string sourceId,
        string targetId,
        CancellationToken cancellationToken)
    {
        var session = sessionHandle.Session;
        if (session is null || !sessionHandle.CanPair(sourceId, targetId))
        {
            PublishBlocked("实例会话已经失效，请重新探测。", MigrationExecutionStatus.RejectedStale);
            return;
        }

        contentLease?.Dispose();
        contentLease = null;
        contentContext = null;
        acceptedPlan = null;
        Publish(State with
        {
            Phase = MigrationWorkflowPhase.Selecting,
            Generation = sessionHandle.Generation,
            Instances = sessionHandle.Instances.ToArray(),
            SourceInstanceId = sourceId,
            TargetInstanceId = targetId,
            ViewState = CreateViewState(sessionHandle.Instances, sourceId, targetId),
            Catalogs = Array.Empty<ContentCatalog>(),
            Compatibility = null,
            ReviewItems = Array.Empty<ContentPlanItem>(),
            StatusText = "正在识别原版设置、界面外观、JEI 收藏与静音规则…",
            PendingRecovery = null,
            CommittedTransactionId = null,
            LastExecutionStatus = null,
            PlannedFileCount = 0,
            PlannedItemCount = 0,
            CanExecute = false,
            CanUndo = false,
            HasDeferredJeiSync = false,
        });

        var prepared = await Task.Run(
            () =>
            {
                var opened = accessFactory.Open(
                    session,
                    sourceId,
                    targetId,
                    ContentAccessLimits.Beta3,
                    cancellationToken);
                if (!opened.IsValid || opened.Lease is null)
                {
                    return CatalogPreparation.Failed("实例在读取前发生变化，请重新探测。", opened.Diagnostics);
                }

                var lease = opened.Lease;
                try
                {
                    var context = compatibilityProbe.ProbeAndCreateContext(
                        lease,
                        ProductionModProbeLimits,
                        cancellationToken);
                    var catalogs = adapters.Values
                        .OrderBy(adapter => AdapterOrder(adapter.Id))
                        .Select(adapter => adapter.BuildCatalog(context, cancellationToken))
                        .ToArray();
                    return CatalogPreparation.Succeeded(lease, context, catalogs);
                }
                catch
                {
                    lease.Dispose();
                    throw;
                }
            },
            cancellationToken);
        if (prepared.Lease is null || prepared.Context is null)
        {
            Publish(State with
            {
                Phase = MigrationWorkflowPhase.Blocked,
                StatusText = prepared.Message,
                Catalogs = prepared.Catalogs,
            });
            return;
        }

        contentLease = prepared.Lease;
        contentContext = prepared.Context;
        Publish(State with
        {
            Phase = MigrationWorkflowPhase.Selecting,
            Catalogs = prepared.Catalogs,
            Compatibility = ContentCompatibilityDisplayEvidence.FromCore(
                prepared.Context.Compatibility),
            StatusText = "内容识别完成；请选择要同步的项目。",
        });
        ActivateDeferredJeiForCurrentPair();
    }

    private PlanPreparation CreateAcceptedPlan(
        DiscoverySession session,
        string sourceId,
        string targetId,
        ContentAccessLease lease,
        ContentProbeContext context,
        IReadOnlyList<ContentCatalog> catalogs,
        ContentSelection selection,
        CancellationToken cancellationToken)
    {
        var adapterPlans = new List<ContentAdapterPlan>();
        foreach (var catalog in catalogs.OrderBy(value => value.AdapterId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!adapters.TryGetValue(catalog.AdapterId, out var adapter))
            {
                return PlanPreparation.Failed("内容适配器不可用，请重新探测。", null);
            }

            var adapterSelection = ContentSelection.Create(
                selection.SelectedItems.Where(id =>
                    string.Equals(id.AdapterId, catalog.AdapterId, StringComparison.Ordinal)),
                selection.ConflictResolutions.Where(pair =>
                    string.Equals(pair.Key.AdapterId, catalog.AdapterId, StringComparison.Ordinal)));
            if (!ContentSelectionValidator.TryValidateExplicit(
                    catalog,
                    adapterSelection,
                    out var validated,
                    out _))
            {
                return PlanPreparation.Failed("存在未处理的冲突或失效选择。", null);
            }

            var adapterPlan = adapter.Plan(context, catalog, validated!, cancellationToken);
            if (adapterPlan.FileChanges.Count > 0)
            {
                adapterPlans.Add(adapterPlan);
            }
        }

        if (adapterPlans.Count == 0)
        {
            return PlanPreparation.Failed("尚未选择需要写入的变化。", null);
        }

        if (!ContentPlanCoordinator.TryCreateMigrationPlan(
                session.Generation,
                sourceId,
                targetId,
                adapterPlans,
                out var contentPlan,
                out _))
        {
            return PlanPreparation.Failed("同步内容存在路径冲突，计划未被接受。", null);
        }

        var accepted = acceptedPlanFactory.Create(
            session,
            sourceId,
            targetId,
            lease,
            context,
            contentPlan!,
            cancellationToken);
        return accepted.IsAccepted && accepted.Plan is not null
            ? PlanPreparation.Succeeded(accepted.Plan, contentPlan!)
            : PlanPreparation.Failed("实例或内容已经变化，请重新检查。", contentPlan);
    }

    private void ActivateDeferredJeiForCurrentPair()
    {
        var record = activeDeferredJei;
        var snapshot = State;
        if (record is null ||
            snapshot.SourceInstanceId is null ||
            snapshot.TargetInstanceId is null ||
            !string.Equals(
                record.SourceInstanceId,
                snapshot.SourceInstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(
                record.TargetInstanceId,
                snapshot.TargetInstanceId,
                StringComparison.Ordinal))
        {
            return;
        }

        pausedDeferredJei = null;
        ResetDeferredJeiObservation();
        Publish(snapshot with
        {
            Phase = MigrationWorkflowPhase.Succeeded,
            StatusText = "已恢复 JEI 收藏待复核任务；正在自动定位服务器收藏目录。",
            Progress = new MigrationProgress(
                MigrationProgressStage.Completed,
                1,
                1,
                "正在定位 JEI 服务器作用域"),
            CommittedTransactionId = record.OriginalTransactionId,
            LastExecutionStatus = MigrationExecutionStatus.Succeeded,
            CanExecute = false,
            CanUndo = false,
            HasDeferredJeiSync = true,
        });
    }

    private void ResetDeferredJeiObservation()
    {
        deferredJeiNeedsAttempt = true;
        deferredJeiObservedRunningGame = false;
        pausedDeferredJei = null;
    }

    private MinecraftProcessEvaluation EvaluateDeferredJeiProcessState(
        MigrationWorkflowState snapshot,
        CancellationToken cancellationToken)
    {
        var source = snapshot.Instances.SingleOrDefault(instance =>
            string.Equals(instance.Id, snapshot.SourceInstanceId, StringComparison.Ordinal));
        var target = snapshot.Instances.SingleOrDefault(instance =>
            string.Equals(instance.Id, snapshot.TargetInstanceId, StringComparison.Ordinal));
        if (source?.CapabilityAccess?.GameRootIdentity is not { } sourceIdentity ||
            target?.CapabilityAccess?.GameRootIdentity is not { } targetIdentity ||
            string.IsNullOrWhiteSpace(source.GameRoot) ||
            string.IsNullOrWhiteSpace(target.GameRoot))
        {
            return new MinecraftProcessEvaluation(
                false,
                MinecraftProcessBlockReason.PathCouldNotBeVerified);
        }

        return processGuard.Evaluate(
            sourceIdentity,
            targetIdentity,
            [source.GameRoot, target.GameRoot],
            cancellationToken);
    }

    private async Task MonitorDeferredJeiAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token);
                await operationGate.WaitAsync(lifetime.Token);
                try
                {
                    var record = activeDeferredJei;
                    var snapshot = State;
                    if (record is null ||
                        pausedDeferredJei == record.OriginalTransactionId ||
                        snapshot.Phase != MigrationWorkflowPhase.Succeeded ||
                        !snapshot.HasDeferredJeiSync ||
                        snapshot.SourceInstanceId is null ||
                        snapshot.TargetInstanceId is null ||
                        activeSession?.Session is null ||
                        !string.Equals(
                            record.SourceInstanceId,
                            snapshot.SourceInstanceId,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            record.TargetInstanceId,
                            snapshot.TargetInstanceId,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var processEvaluation = EvaluateDeferredJeiProcessState(
                        snapshot,
                        lifetime.Token);
                    if (!processEvaluation.IsSafe)
                    {
                        if (processEvaluation.BlockReason ==
                            MinecraftProcessBlockReason.RelatedGameRunning)
                        {
                            deferredJeiObservedRunningGame = true;
                        }

                        continue;
                    }

                    if (!deferredJeiNeedsAttempt && !deferredJeiObservedRunningGame)
                    {
                        continue;
                    }

                    deferredJeiNeedsAttempt = false;
                    deferredJeiObservedRunningGame = false;
                    await AttemptDeferredJeiWithGateAsync(
                        record,
                        activeSession.Session,
                        lifetime.Token);
                }
                finally
                {
                    operationGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task AttemptDeferredJeiWithGateAsync(
        DeferredJeiSyncRecord record,
        DiscoverySession session,
        CancellationToken cancellationToken)
    {
        var operation = BeginMutationOperation();
        var executionStarted = false;
        void BeforeExecution()
        {
            executionStarted = true;
            Publish(State with
            {
                Phase = MigrationWorkflowPhase.Executing,
                StatusText = "已定位目标服务器收藏目录，正在创建还原点并复核 JEI 收藏…",
                CanExecute = false,
                CanUndo = false,
                Progress = new MigrationProgress(
                    MigrationProgressStage.Revalidating,
                    0,
                    1,
                    "正在复核 JEI 收藏作用域"),
            });
        }

        DeferredJeiAttemptResult result;
        ContentAccessLease? attemptLease = null;
        try
        {
            var opened = accessFactory.Open(
                session,
                record.SourceInstanceId,
                record.TargetInstanceId,
                ContentAccessLimits.Beta3,
                cancellationToken);
            if (!opened.IsValid || opened.Lease is null)
            {
                result = new DeferredJeiAttemptResult(
                    DeferredJeiAttemptStatus.RejectedStale,
                    Message: "JEI 实例证据已变化，需要重新探测。");
            }
            else
            {
                attemptLease = opened.Lease;
                var attemptContext = compatibilityProbe.ProbeAndCreateContext(
                    attemptLease,
                    ProductionModProbeLimits,
                    cancellationToken);
                result = await deferredJei.AttemptAsync(
                    record,
                    session,
                    record.SourceInstanceId,
                    record.TargetInstanceId,
                    attemptLease,
                    attemptContext,
                    BeforeExecution,
                    new InlineProgress<MigrationProgress>(value =>
                        PublishMutationProgress(operation, value)),
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            result = new DeferredJeiAttemptResult(
                DeferredJeiAttemptStatus.RejectedStale,
                Message: "JEI 自动复核未通过最新实例检查。");
        }
        finally
        {
            attemptLease?.Dispose();
            EndMutationOperation(operation);
        }

        switch (result.Status)
        {
            case DeferredJeiAttemptStatus.PendingTargetScope:
                return;
            case DeferredJeiAttemptStatus.Blocked:
                deferredJeiNeedsAttempt = true;
                if (executionStarted)
                {
                    Publish(State with
                    {
                        Phase = MigrationWorkflowPhase.Succeeded,
                        StatusText = "JEI 真实目录已出现；检测到 Minecraft 仍在运行，关闭后会自动完成复核。",
                        LastExecutionStatus = MigrationExecutionStatus.Succeeded,
                        CanExecute = false,
                        CanUndo = false,
                        HasDeferredJeiSync = true,
                    });
                }

                return;
            case DeferredJeiAttemptStatus.CompletedAlready:
                activeDeferredJei = null;
                pausedDeferredJei = null;
                Publish(State with
                {
                    Phase = MigrationWorkflowPhase.Succeeded,
                    StatusText = "同步完成并复读验证：JEI 收藏已位于目标真实服务器目录。",
                    Progress = new MigrationProgress(
                        MigrationProgressStage.Completed,
                        1,
                        1,
                        "JEI 收藏已复核"),
                    CommittedTransactionId = record.OriginalTransactionId,
                    LastExecutionStatus = MigrationExecutionStatus.Succeeded,
                    CanExecute = false,
                    CanUndo = false,
                    HasDeferredJeiSync = false,
                });
                await RefreshUndoEligibilityCoreAsync(
                    record.OriginalTransactionId,
                    cancellationToken);
                return;
            case DeferredJeiAttemptStatus.Succeeded when result.TransactionId is { } committed:
                activeDeferredJei = null;
                pausedDeferredJei = null;
                Publish(State with
                {
                    Phase = MigrationWorkflowPhase.Succeeded,
                    StatusText = $"同步完成并复读验证：JEI 收藏已自动归位，共写入 {result.CommittedFileCount} 个收藏文件。",
                    Progress = new MigrationProgress(
                        MigrationProgressStage.Completed,
                        1,
                        1,
                        "JEI 收藏已复核"),
                    CommittedTransactionId = committed,
                    LastExecutionStatus = MigrationExecutionStatus.Succeeded,
                    CanExecute = false,
                    CanUndo = false,
                    HasDeferredJeiSync = false,
                });
                await RefreshUndoEligibilityCoreAsync(committed, cancellationToken);
                return;
            case DeferredJeiAttemptStatus.Conflict:
                pausedDeferredJei = record.OriginalTransactionId;
                Publish(State with
                {
                    Phase = MigrationWorkflowPhase.Blocked,
                    StatusText = result.Message ??
                        "目标服务器已有不同的 JEI 收藏；已保留目标，等待重新检查。",
                    LastExecutionStatus = MigrationExecutionStatus.Blocked,
                    CanExecute = false,
                    CanUndo = false,
                    HasDeferredJeiSync = true,
                });
                return;
            case DeferredJeiAttemptStatus.RecoveryRequired:
                activeDeferredJei = null;
                _ = await RescanPendingAfterOutcomeAndPublishAsync(
                    State with
                    {
                        Phase = MigrationWorkflowPhase.Blocked,
                        StatusText = "JEI 自动复核未能安全结束；正在检查恢复记录。",
                        LastExecutionStatus = MigrationExecutionStatus.RecoveryRequired,
                        CanExecute = false,
                        CanUndo = false,
                        HasDeferredJeiSync = true,
                    },
                    "JEI 自动复核需要先安全恢复。",
                    cancellationToken,
                    result.TransactionId);
                return;
            default:
                pausedDeferredJei = record.OriginalTransactionId;
                Publish(State with
                {
                    Phase = MigrationWorkflowPhase.Blocked,
                    StatusText = result.Message ??
                        "JEI 来源或实例证据已经变化；没有自动覆盖目标，请重新探测。",
                    LastExecutionStatus = MigrationExecutionStatus.RejectedStale,
                    CanExecute = false,
                    CanUndo = false,
                    HasDeferredJeiSync = true,
                });
                return;
        }
    }

    private static (string SourceId, string TargetId)? FindSuggestedPair(
        CoreDiscoverySessionHandle session)
    {
        var targets = session.Instances
            .OrderByDescending(instance => instance.IsSelected)
            .ThenByDescending(instance => instance.Id, StringComparer.Ordinal);
        foreach (var target in targets)
        {
            var sources = session.Instances
                .Where(instance => !ReferenceEquals(instance, target))
                .OrderByDescending(instance =>
                    string.Equals(
                        instance.ModpackIdentity.Name,
                        target.ModpackIdentity.Name,
                        StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(instance => instance.Id, StringComparer.Ordinal);
            foreach (var source in sources)
            {
                if (session.CanPair(source.Id, target.Id))
                {
                    return (source.Id, target.Id);
                }
            }
        }

        return null;
    }

    private static MigrationViewState CreateViewState(
        IReadOnlyList<Pcl2Instance> instances,
        string sourceId,
        string targetId)
    {
        var source = instances.Single(instance => string.Equals(instance.Id, sourceId, StringComparison.Ordinal));
        var target = instances.Single(instance => string.Equals(instance.Id, targetId, StringComparison.Ordinal));
        return new MigrationViewState(
            "真实数据 · 已验证实例",
            target.MinecraftRoot,
            InstanceVersion(source, "来源"),
            InstanceVersion(target, "目标"),
            source.DisplayName,
            target.DisplayName,
            target.ModpackIdentity.Name,
            "PCL 2",
            IsDemo: false,
            CanStart: true);
    }

    private static string InstanceVersion(Pcl2Instance instance, string fallback) =>
        instance.ModpackIdentity.Version ?? instance.MinecraftVersion ?? fallback;

    private static int AdapterOrder(string adapterId) => adapterId switch
    {
        "vanilla" => 0,
        "appearance" => 1,
        "jei" => 2,
        "esm" => 3,
        _ => 4,
    };

    private static bool IsActionable(ContentPlanItem item) =>
        item.Disposition is PlannedContentDisposition.Add or PlannedContentDisposition.Update ||
        item.Disposition == PlannedContentDisposition.Conflict &&
        item.Resolution == ConflictResolution.UseSource;

    private bool CanDiscover(MigrationWorkflowState snapshot) =>
        MigrationWorkflowPolicy.CanDiscover(recoveryCheckPassed, snapshot.Phase);

    private Task<bool> RescanPendingAndPublishAsync(
        MigrationWorkflowState whenNone,
        string pendingStatusText,
        CancellationToken cancellationToken,
        TransactionId? attentionTransactionId = null,
        MigrationRecoveryStatus? attentionStatus = null) =>
        pendingRescanPublisher.PublishRequestBoundAsync(
            whenNone,
            pendingStatusText,
            cancellationToken,
            attentionTransactionId,
            attentionStatus);

    private Task<bool> RescanPendingAfterOutcomeAndPublishAsync(
        MigrationWorkflowState whenNone,
        string pendingStatusText,
        CancellationToken cancellationToken,
        TransactionId? attentionTransactionId = null,
        MigrationRecoveryStatus? attentionStatus = null) =>
        pendingRescanPublisher.PublishAfterOutcomeAsync(
            whenNone,
            pendingStatusText,
            cancellationToken,
            attentionTransactionId,
            attentionStatus);

    private VerifiedRecoverySelection? GetRecoverySelection(TransactionId transactionId)
    {
        lock (stateGate)
        {
            return recoverySelections.GetValueOrDefault(transactionId);
        }
    }

    private void SetRecoveryCheckPassed(bool value)
    {
        lock (stateGate)
        {
            recoveryCheckPassed = value;
        }
    }

    private void SetRecoverySelection(
        TransactionId transactionId,
        VerifiedRecoverySelection selection)
    {
        lock (stateGate)
        {
            recoverySelections[transactionId] = selection;
        }
    }

    private void RemoveRecoverySelection(TransactionId transactionId)
    {
        lock (stateGate)
        {
            recoverySelections.Remove(transactionId);
        }
    }

    private static string ResultMessage(MigrationExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            return result.Message;
        }

        return result.Status switch
        {
            MigrationExecutionStatus.CancelledBeforeMutation => "已取消；目标实例未发生变化。",
            MigrationExecutionStatus.RolledBack => "同步没有完成，所有已开始的变化均已回滚。",
            MigrationExecutionStatus.RejectedStale => "实例或内容已经变化，请重新探测并检查。",
            _ => "安全检查阻止了同步；目标实例未被提交。",
        };
    }

    private void PublishBlocked(string message, MigrationExecutionStatus status) =>
        Publish(State with
        {
            Phase = MigrationWorkflowPhase.Blocked,
            StatusText = message,
            LastExecutionStatus = status,
            CanExecute = false,
        });

    private void Publish(MigrationWorkflowState next)
    {
        EventHandler<MigrationWorkflowState>? handler;
        lock (stateGate)
        {
            if (disposed)
            {
                return;
            }

            state = next;
            handler = StateChanged;
        }

        handler?.Invoke(this, next);
    }

    private long BeginMutationOperation() =>
        checked(Interlocked.Increment(ref mutationOperation));

    private void EndMutationOperation(long operation)
    {
        if (Interlocked.Read(ref mutationOperation) == operation)
        {
            _ = Interlocked.Increment(ref mutationOperation);
        }
    }

    private void PublishMutationProgress(long operation, MigrationProgress progress)
    {
        EventHandler<MigrationWorkflowState>? handler;
        MigrationWorkflowState next;
        lock (stateGate)
        {
            if (disposed ||
                !MigrationWorkflowPolicy.CanApplyMutationProgress(
                    mutationOperation,
                    operation,
                    state.Phase))
            {
                return;
            }

            next = state with
            {
                Phase = progress.Stage == MigrationProgressStage.RollingBack
                    ? MigrationWorkflowPhase.RollingBack
                    : state.Phase,
                Progress = progress,
                StatusText = progress.Message,
            };
            state = next;
            handler = StateChanged;
        }

        handler?.Invoke(this, next);
    }

    private CancellationTokenSource Link(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
    }

    private void ResetAcceptedContext(bool disposeSession)
    {
        acceptedPlan = null;
        contentContext = null;
        contentLease?.Dispose();
        contentLease = null;
        if (disposeSession)
        {
            activeSession?.Dispose();
            activeSession = null;
        }
    }

    private void ThrowIfDisposed()
    {
        lock (stateGate)
        {
            ThrowIfDisposedLocked();
        }
    }

    private void ThrowIfDisposedLocked() => ObjectDisposedException.ThrowIf(disposed, this);

    private static bool IsRecoverable(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            InvalidOperationException or
            ObjectDisposedException;

    private sealed record CatalogPreparation(
        ContentAccessLease? Lease,
        ContentProbeContext? Context,
        IReadOnlyList<ContentCatalog> Catalogs,
        string Message)
    {
        internal static CatalogPreparation Succeeded(
            ContentAccessLease lease,
            ContentProbeContext context,
            IReadOnlyList<ContentCatalog> catalogs) =>
            new(lease, context, catalogs, string.Empty);

        internal static CatalogPreparation Failed(
            string message,
            IReadOnlyList<ContentDiagnostic> diagnostics) =>
            new(null, null, Array.Empty<ContentCatalog>(), message);
    }

    private sealed record PlanPreparation(
        AcceptedMigrationPlan? Plan,
        MigrationContentPlan? ContentPlan,
        string Message)
    {
        internal static PlanPreparation Succeeded(
            AcceptedMigrationPlan plan,
            MigrationContentPlan contentPlan) =>
            new(plan, contentPlan, string.Empty);

        internal static PlanPreparation Failed(
            string message,
            MigrationContentPlan? contentPlan) =>
            new(null, contentPlan, message);
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        private readonly Action<T> handler =
            handler ?? throw new ArgumentNullException(nameof(handler));

        public void Report(T value) => handler(value);
    }
}
