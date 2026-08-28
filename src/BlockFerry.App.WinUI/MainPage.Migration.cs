using BlockFerry.App.WinUI.Services;
using BlockFerry.App.WinUI.Selection;
using BlockFerry.App.WinUI.Localization;
using BlockFerry.Core.Content;
using BlockFerry.Core.Pcl2;
using BlockFerry.Core.Transactions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace BlockFerry.App.WinUI;

public sealed partial class MainPage
{
    private readonly CancellationTokenSource _workflowLifetime = new();
    private MigrationWorkflowCoordinator? _workflow;
    private IFolderPickerService? _workflowFolderPicker;
    private IReadOnlyList<ContentCatalog>? _presentedWorkflowCatalogs;
    private IReadOnlyList<ContentPlanItem>? _presentedReviewItems;
    private MigrationWorkflowPhase? _presentedWorkflowPhase;
    private MigrationProgressStage? _presentedExecutionStage;
    private long _presentedCommittedFeedbackGeneration = -1;
    private TransactionId? _presentedCommittedFeedbackTransactionId;
    private bool _workflowStarted;

    internal MainPage(
        MigrationWorkflowCoordinator workflow,
        IFolderPickerService folderPicker,
        IFileSavePickerService fileSavePicker)
        : this()
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _workflowFolderPicker = folderPicker ?? throw new ArgumentNullException(nameof(folderPicker));
        ArgumentNullException.ThrowIfNull(fileSavePicker);
        _workflow.AttachFileSavePicker(fileSavePicker);
        _workflow.StateChanged += Workflow_StateChanged;
        PresentWorkflowState(_workflow.State);
    }

    private async Task StartWorkflowAsync()
    {
        if (_workflow is null || _workflowStarted || _disposed)
        {
            return;
        }

        _workflowStarted = true;
        try
        {
            await _workflow.InitializeAsync(_workflowLifetime.Token);
            if (_workflow.State.Phase == MigrationWorkflowPhase.AwaitingDiscovery)
            {
                await _workflow.AutoDiscoverAsync(_workflowLifetime.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing the page cancels only the UI request; the transaction layer rolls back if needed.
        }
    }

    private void Workflow_StateChanged(object? sender, MigrationWorkflowState next)
    {
        DispatcherQueue? dispatcher = DispatcherQueue;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
        {
            _ = dispatcher.TryEnqueue(() => PresentWorkflowState(next));
            return;
        }

        PresentWorkflowState(next);
    }

    private void PresentWorkflowState(
        MigrationWorkflowState workflowState,
        bool forceProjection = false)
    {
        if (_disposed)
        {
            return;
        }

        var wasMutationInProgress = _presentedWorkflowPhase is
            MigrationWorkflowPhase.Executing or MigrationWorkflowPhase.RollingBack;
        var workspaceNavigation = ExecutionWorkspaceNavigationPolicy.Evaluate(
            _presentedWorkflowPhase,
            workflowState,
            _drawerLifecycle.Phase);
        if (workflowState.IsMutationInProgress && !wasMutationInProgress)
        {
            _executionProgressAccumulator.Reset();
        }

        if (!forceProjection &&
            _presentedWorkflowPhase == workflowState.Phase &&
            workflowState.Phase is MigrationWorkflowPhase.Executing or MigrationWorkflowPhase.RollingBack)
        {
            PresentWorkflowProgress(workflowState);
            return;
        }

        var phaseChanged = _presentedWorkflowPhase != workflowState.Phase;

        _viewState = workflowState.ViewState;
        ProjectViewState(_viewState);
        ScanStatusText.Text = workflowState.StatusText;
        RecoveryStatusText.Text = workflowState.StatusText;
        SetDiscoveryActivity(workflowState.Phase == MigrationWorkflowPhase.Discovering || _discoveryInFlight);
        _discoveredInstances = workflowState.Instances;

        _updatingPickers = true;
        if (_discoveredInstances.Count == 0)
        {
            SourceInstancePicker.ItemsSource = new[] { _viewState.SourceInstance };
            TargetInstancePicker.ItemsSource = new[] { _viewState.TargetInstance };
            SourceInstancePicker.SelectedIndex = 0;
            TargetInstancePicker.SelectedIndex = 0;
        }
        else
        {
            var labels = _discoveredInstances.Select(InstanceLabel).ToArray();
            SourceInstancePicker.ItemsSource = labels;
            TargetInstancePicker.ItemsSource = labels;
            SourceInstancePicker.SelectedIndex = FindInstanceIndex(workflowState.SourceInstanceId);
            TargetInstancePicker.SelectedIndex = FindInstanceIndex(workflowState.TargetInstanceId);
        }

        _updatingPickers = false;
        var recoveryRequired = workflowState.Phase == MigrationWorkflowPhase.RecoveryRequired;
        var pendingRecovery = workflowState.PendingRecovery;
        var recoveryAuthenticationFailed = pendingRecovery?.AttentionStatus ==
                                           MigrationRecoveryStatus.AuthenticationFailed;
        var canRecover = _workflow?.CanRecoverCurrent == true;
        var isDemo = workflowState.Phase == MigrationWorkflowPhase.Demo;
        var isMutationInProgress = workflowState.IsMutationInProgress;
        DrawerCloseButton.IsEnabled = !isMutationInProgress;
        AutomationProperties.SetItemStatus(
            DrawerCloseButton,
            isMutationInProgress ? "同步正在进行，关闭暂时不可用" : "可以关闭");
        AutomationProperties.SetHelpText(
            DrawerCloseButton,
            isMutationInProgress
                ? "同步会在主页继续显示进度；事务完成前请保持程序打开。"
                : "返回主页。");
        var hasRealCatalogs = workflowState.Catalogs.Count > 0 && !isDemo;
        var showExecution = workflowState.Phase is
            MigrationWorkflowPhase.Executing or MigrationWorkflowPhase.RollingBack;
        var showResult = workflowState.Phase is
                             MigrationWorkflowPhase.Reviewing or
                             MigrationWorkflowPhase.Succeeded ||
                          workflowState.Phase == MigrationWorkflowPhase.Blocked &&
                          workflowState.ReviewItems.Count > 0;
        var showSelection = !showExecution && !showResult;
        WorkspaceSelectionLayout.Visibility = showSelection
            ? Visibility.Visible
            : Visibility.Collapsed;
        ExecutionExperience.Visibility = showExecution
            ? Visibility.Visible
            : Visibility.Collapsed;
        RecoveryCard.Visibility = recoveryRequired ? Visibility.Visible : Visibility.Collapsed;
        RecoveryFolderButton.Visibility = recoveryRequired &&
                                          !recoveryAuthenticationFailed &&
                                          !canRecover
            ? Visibility.Visible
            : Visibility.Collapsed;
        RecoverNowButton.Visibility = recoveryAuthenticationFailed
            ? Visibility.Collapsed
            : Visibility.Visible;
        RecoverNowButton.IsEnabled = canRecover;
        DiscoveryCard.Visibility = recoveryRequired ? Visibility.Collapsed : Visibility.Visible;
        ContentSelectionSection.Visibility = hasRealCatalogs && !showResult
            ? Visibility.Visible
            : Visibility.Collapsed;
        OptionsSelectionPanel.Visibility = isDemo || hasRealCatalogs && !showResult
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!isDemo)
        {
            OptionsSelectionControl.IsEnabled = hasRealCatalogs &&
                                                !showResult &&
                                                !isMutationInProgress;
        }
        var canDiscover = _workflow?.CanDiscoverCurrent ??
                          (!workflowState.IsMutationInProgress &&
                           !recoveryRequired &&
                           workflowState.Phase != MigrationWorkflowPhase.Discovering);
        SetDiscoveryButtonsEnabled(canDiscover);

        if (!ReferenceEquals(_presentedWorkflowCatalogs, workflowState.Catalogs))
        {
            _presentedWorkflowCatalogs = workflowState.Catalogs;
            ResetContentSelection(workflowState.Catalogs, workflowState.Compatibility);
        }

        ErrorCard.Visibility = workflowState.Phase == MigrationWorkflowPhase.Blocked &&
                               workflowState.Catalogs.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (ErrorCard.Visibility == Visibility.Visible)
        {
            SelectionErrorText.Text = workflowState.StatusText;
            SelectionErrorDiagnosticsItemsControl.ItemsSource = null;
            SelectionErrorDiagnosticsItemsControl.Visibility = Visibility.Collapsed;
        }

        ResultCard.Visibility = showResult ? Visibility.Visible : Visibility.Collapsed;
        if (showResult)
        {
            PresentWorkflowResult(workflowState);
            QueueSubtreeLocalization(ResultCard);
        }

        if (showExecution)
        {
            PresentExecutionExperience(workflowState);
            QueueSubtreeLocalization(ExecutionExperience);
        }

        UpdateWorkspaceStageRail(workflowState.Phase);

        SafetyBoundaryText.Text = workflowState.Phase switch
        {
            MigrationWorkflowPhase.Reviewing =>
                "尚未写入。点击“备份并同步”后会先创建可验证还原点，再提交所列文件。",
            MigrationWorkflowPhase.Executing or MigrationWorkflowPhase.RollingBack =>
                "正在执行受保护事务；请保持 Minecraft 关闭并暂时不要关闭此窗口。",
            MigrationWorkflowPhase.Succeeded when
                workflowState.LastExecutionStatus == MigrationExecutionStatus.Succeeded &&
                workflowState.HasDeferredJeiSync =>
                "原版与模组设置已复读验证；JEI 收藏会在目标首次生成真实服务器目录后自动复核。",
            MigrationWorkflowPhase.Succeeded when workflowState.LastExecutionStatus == MigrationExecutionStatus.Succeeded =>
                "目标文件已复读验证；可在未发生后续变化时撤销这次同步。",
            MigrationWorkflowPhase.RecoveryRequired =>
                "恢复优先：在上次事务安全结束前，不允许开始新的同步。",
            _ when isDemo => "当前是内存演示，不会访问或修改 Minecraft 实例。",
            _ => "发现和内容选择阶段只读；真正同步前还会显示最终清单并要求确认。",
        };

        PrimaryActionButton.IsEnabled = !workflowState.IsMutationInProgress;
        switch (workflowState.Phase)
        {
            case MigrationWorkflowPhase.Discovering:
                SetSyncPresentation(
                    SyncPresentationState.Running,
                    0,
                    workflowState.StatusText,
                    workflowState.Progress ?? new MigrationProgress(MigrationProgressStage.Revalidating, 0, 0, workflowState.StatusText));
                break;
            case MigrationWorkflowPhase.Executing:
            case MigrationWorkflowPhase.RollingBack:
                SetSyncPresentation(
                    SyncPresentationState.Running,
                    2,
                    workflowState.StatusText,
                    workflowState.Progress);
                break;
            case MigrationWorkflowPhase.Succeeded:
                SetSyncPresentation(SyncPresentationState.Completed, 3, workflowState.StatusText);
                break;
            case MigrationWorkflowPhase.Blocked:
            case MigrationWorkflowPhase.RecoveryRequired:
                SetSyncPresentation(SyncPresentationState.Blocked, 0, workflowState.StatusText);
                PrimaryActionButton.IsEnabled = true;
                break;
            default:
                SetSyncPresentation(SyncPresentationState.Idle, 0, workflowState.StatusText);
                break;
        }

        if (workflowState.Phase == MigrationWorkflowPhase.Succeeded)
        {
            PresentCommittedHomeFeedback(workflowState);
        }

        UpdateWorkflowFooter(workflowState);
        if (phaseChanged && !forceProjection)
        {
            AnimateWorkspacePhaseChange(showSelection, showExecution, showResult);
        }

        _presentedWorkflowPhase = workflowState.Phase;
        LocalizeElements(
            ScanStatusText,
            RecoveryStatusText,
            SafetyBoundaryText,
            DrawerCloseButton,
            ErrorCard,
            WorkspaceStageRail);
        switch (workspaceNavigation)
        {
            case ExecutionWorkspaceNavigationAction.ShowHome:
                CloseDrawerForBackgroundExecution();
                break;
            case ExecutionWorkspaceNavigationAction.ShowWorkspace:
                OpenDrawerForWorkflowAttention();
                break;
        }
    }

    private void PresentWorkflowProgress(MigrationWorkflowState workflowState)
    {
        ScanStatusText.Text = workflowState.StatusText;
        RecoveryStatusText.Text = workflowState.StatusText;
        PresentExecutionExperience(workflowState);
        SetSyncPresentation(
            SyncPresentationState.Running,
            2,
            workflowState.StatusText,
            workflowState.Progress);
        UpdateWorkflowFooter(workflowState);
        LocalizeElements(ScanStatusText, RecoveryStatusText);
    }

    private void PresentExecutionExperience(MigrationWorkflowState workflowState)
    {
        var presentation = MigrationProgressPresenter.Create(
            workflowState.Progress,
            workflowState.StatusText);
        var stage = workflowState.Progress?.Stage;
        var stageChanged = _presentedExecutionStage != stage;
        var displayedPercent = _executionProgressAccumulator.Advance(presentation.Percent);

        ExecutionStageTitleText.Text = presentation.StageText;
        ExecutionStageDetailText.Text = presentation.DetailText;
        ExecutionPercentText.Text = presentation.IsIndeterminate
            ? "…"
            : $"{Math.Round(displayedPercent):0}%";
        var continuousMotion = ContinuousMotionPolicy.Allows(
            active: true,
            _animationsEnabled,
            _highContrast);
        ExecutionActivityRing.IsActive = continuousMotion;
        ExecutionProgressBar.IsIndeterminate = continuousMotion && presentation.IsIndeterminate;
        SetExecutionProgressValue(displayedPercent);
        LocalizeElements(ExecutionStageTextHost);

        if (stageChanged)
        {
            UpdateExecutionStepPresentation(stage, workflowState.Phase);
            PlayReveal(ExecutionStageTextHost, ExecutionStageTextTranslate, 190, 5);
            _presentedExecutionStage = stage;
        }
    }

    private void SetExecutionProgressValue(double value)
    {
        value = Math.Clamp(value, 0, 100);
        var currentValue = ExecutionProgressBar.Value;
        _executionProgressStoryboard?.Stop();
        if (!_animationsEnabled || _highContrast || Math.Abs(currentValue - value) < 0.1)
        {
            ExecutionProgressBar.Value = value;
            return;
        }

        ExecutionProgressBar.Value = value;
        var animation = new DoubleAnimation
        {
            From = currentValue,
            To = value,
            Duration = new Duration(TimeSpan.FromMilliseconds(260)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, ExecutionProgressBar);
        Storyboard.SetTargetProperty(animation, "Value");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        _executionProgressStoryboard = storyboard;
        storyboard.Begin();
    }

    private void UpdateExecutionStepPresentation(
        MigrationProgressStage? stage,
        MigrationWorkflowPhase phase)
    {
        var activeStep = phase == MigrationWorkflowPhase.RollingBack ||
                         stage == MigrationProgressStage.RollingBack
            ? 1
            : stage switch
            {
                MigrationProgressStage.PreparingBackup or MigrationProgressStage.BackingUp => 1,
                MigrationProgressStage.Staging or MigrationProgressStage.Committing => 2,
                MigrationProgressStage.Verifying or MigrationProgressStage.CleaningUp or
                    MigrationProgressStage.Completed => 3,
                _ => 0,
            };
        Border[] steps =
        [
            ExecutionCheckStep,
            ExecutionBackupStep,
            ExecutionCommitStep,
            ExecutionVerifyStep,
        ];
        for (var index = 0; index < steps.Length; index++)
        {
            var targetOpacity = index == activeStep
                ? 1
                : index < activeStep
                    ? 0.68
                    : 0.34;
            AnimateStageOpacity(steps[index], targetOpacity);
        }
    }

    private void UpdateWorkspaceStageRail(MigrationWorkflowPhase phase)
    {
        var activeStep = phase switch
        {
            MigrationWorkflowPhase.Reviewing => 1,
            MigrationWorkflowPhase.Executing or MigrationWorkflowPhase.RollingBack or
                MigrationWorkflowPhase.Succeeded => 2,
            MigrationWorkflowPhase.Blocked when _workflow?.State.ReviewItems.Count > 0 => 1,
            _ => 0,
        };
        Border[] steps = [WorkspaceSelectStep, WorkspaceReviewStep, WorkspaceExecuteStep];
        for (var index = 0; index < steps.Length; index++)
        {
            var targetOpacity = _highContrast ? 1 : index == activeStep
                ? 1
                : index < activeStep
                    ? 0.68
                    : 0.4;
            steps[index].BorderThickness = new Thickness(index == activeStep ? 2 : 1);
            AutomationProperties.SetItemStatus(
                steps[index],
                index == activeStep ? "当前步骤" : index < activeStep ? "已完成步骤" : "待进行");
            AnimateStageOpacity(steps[index], targetOpacity);
        }

        QueueSubtreeLocalization(WorkspaceStageRail);
    }

    private void AnimateStageOpacity(FrameworkElement element, double targetOpacity)
    {
        if (Math.Abs(element.Opacity - targetOpacity) < 0.01)
        {
            return;
        }

        if (!_pageLoaded || !_animationsEnabled || _highContrast)
        {
            element.Opacity = targetOpacity;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void AnimateWorkspacePhaseChange(
        bool showSelection,
        bool showExecution,
        bool showResult)
    {
        if (showExecution)
        {
            PlayReveal(ExecutionExperience, ExecutionExperienceTranslate, 280, 14);
            return;
        }

        if (showResult)
        {
            PlayReveal(ResultCard, PreviewResultsTranslate, 250, 12);
            return;
        }

        if (showSelection)
        {
            PlayReveal(WorkspaceSelectionLayout, WorkspaceSelectionTranslate, 240, 10);
        }
    }

    private void PresentWorkflowResult(MigrationWorkflowState workflowState)
    {
        var committed = workflowState.Phase == MigrationWorkflowPhase.Succeeded &&
                        workflowState.LastExecutionStatus == MigrationExecutionStatus.Succeeded &&
                        !workflowState.HasDeferredJeiSync;
        var undoRetryAvailable = workflowState.Phase == MigrationWorkflowPhase.Succeeded &&
                                 workflowState.CommittedTransactionId is not null &&
                                 workflowState.CanUndo &&
                                 workflowState.LastExecutionStatus == MigrationExecutionStatus.Blocked;
        var undone = workflowState.Phase == MigrationWorkflowPhase.Succeeded &&
                     workflowState.LastExecutionStatus == MigrationExecutionStatus.RolledBack;
        PreviewResultTitleText.Text = workflowState.HasDeferredJeiSync
            ? "等待 JEI 自动复核"
            : committed
            ? "同步完成"
            : undoRetryAvailable
                ? "同步保持不变"
                : undone
                    ? "已安全撤销"
                    : workflowState.Phase == MigrationWorkflowPhase.Blocked
                        ? "需要处理"
            : workflowState.Phase == MigrationWorkflowPhase.Reviewing
                ? "确认同步清单"
                : workflowState.Phase == MigrationWorkflowPhase.RollingBack
                    ? "正在恢复"
                    : "正在安全同步";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            PreviewResultHeading,
            PreviewResultTitleText.Text);
        PreviewSummaryText.Text = workflowState.StatusText;
        if (!ReferenceEquals(_presentedReviewItems, workflowState.ReviewItems))
        {
            _presentedReviewItems = workflowState.ReviewItems;
            MigrationReviewControl.Bind(workflowState.ReviewItems);
            QueueSubtreeLocalization(MigrationReviewControl);
        }
        PreviewSecondaryCountsText.Text =
            $"将处理 {workflowState.PlannedItemCount} 项内容 · 涉及 {workflowState.PlannedFileCount} 个文件";
        PreviewPathsText.Text = workflowState.HasDeferredJeiSync
            ? "收藏已安全预置；进入目标服务器并关闭 Minecraft 后自动归位"
            : committed
            ? "还原点已通过身份与摘要验证"
            : undoRetryAvailable
                ? "撤销尚未执行；同步后的文件保持原样，可以关闭 Minecraft 后重试"
                : undone
                    ? "目标文件已复读验证为同步前状态"
                    : "执行前会再次核对来源、目标、运行中的 Minecraft 与文件摘要";
        ModifySelectionButton.Visibility = MigrationWorkflowPolicy.CanReturnToSelection(
                workflowState.Phase,
                workflowState.Catalogs.Count > 0,
                workflowState.IsMutationInProgress)
            ? Visibility.Visible
            : Visibility.Collapsed;
        UndoMigrationButton.Visibility = workflowState.CanUndo
            ? Visibility.Visible
            : Visibility.Collapsed;
        LocalizeElements(
            PreviewResultHeading,
            PreviewSummaryText,
            PreviewSecondaryCountsText,
            PreviewPathsText,
            ModifySelectionButton,
            UndoMigrationButton);

    }

    private void PresentCommittedHomeFeedback(MigrationWorkflowState workflowState)
    {
        if (_workflow is null ||
            workflowState.LastExecutionStatus != MigrationExecutionStatus.Succeeded ||
            workflowState.HasDeferredJeiSync ||
            workflowState.CommittedTransactionId is not { } transactionId ||
            _presentedCommittedFeedbackGeneration == workflowState.Generation &&
            _presentedCommittedFeedbackTransactionId == transactionId)
        {
            return;
        }

        var resultPresented = _pageLoaded &&
                              _drawerLifecycle.Phase == DrawerModalPhase.Collapsed &&
                              DrawerLayer.Visibility == Visibility.Collapsed;
        var focusAccepted = false;
        var validAutomationPeer = false;
        var notificationInvokedSuccessfully = false;
        if (resultPresented)
        {
            focusAccepted = PrimaryActionButton.Focus(FocusState.Programmatic);
            try
            {
                var peer = FrameworkElementAutomationPeer.FromElement(PrimaryActionButton) ??
                           FrameworkElementAutomationPeer.CreatePeerForElement(PrimaryActionButton);
                if (peer is not null)
                {
                    validAutomationPeer = true;
                    peer.RaiseNotificationEvent(
                        AutomationNotificationKind.ActionCompleted,
                        AutomationNotificationProcessing.MostRecent,
                        UiText.Translate(workflowState.StatusText),
                        "BlockFerry.Migration.Committed");
                    notificationInvokedSuccessfully = true;
                }
            }
            catch (Exception)
            {
                validAutomationPeer = false;
                notificationInvokedSuccessfully = false;
            }
        }

        if (_workflow.TryPlayCommittedSound(
                workflowState.Generation,
                transactionId,
                resultPresented,
                focusAccepted,
                validAutomationPeer,
                notificationInvokedSuccessfully))
        {
            _presentedCommittedFeedbackGeneration = workflowState.Generation;
            _presentedCommittedFeedbackTransactionId = transactionId;
        }
    }

    private void RetryCommittedHomeFeedbackFromCurrentState()
    {
        if (_workflow is not null)
        {
            PresentCommittedHomeFeedback(_workflow.State);
        }
    }

    private void UpdateWorkflowFooter(MigrationWorkflowState workflowState)
    {
        if (_workflow is null || workflowState.Phase == MigrationWorkflowPhase.Demo)
        {
            return;
        }

        var migrationRunning = workflowState.Phase is
            MigrationWorkflowPhase.Executing or MigrationWorkflowPhase.RollingBack;
        var progressPresentation = MigrationProgressPresenter.Create(
            workflowState.Progress,
            workflowState.StatusText);
        var displayedPercent = migrationRunning
            ? _executionProgressAccumulator.Current
            : progressPresentation.Percent;
        SetDrawerActivity(
            migrationRunning,
            migrationRunning && progressPresentation.IsIndeterminate,
            displayedPercent);
        switch (workflowState.Phase)
        {
            case MigrationWorkflowPhase.Selecting:
                var selection = _contentSelectionViewModel.CaptureSelection();
                SelectedCountFooterText.Text = $"已选 {selection.SelectedItems.Count} 项内容";
                DryRunPreviewButton.Content = "检查同步计划";
                DryRunPreviewButton.IsEnabled =
                    selection.SelectedItems.Count > 0 &&
                    !_contentSelectionViewModel.HasUnresolvedConflicts;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                    DryRunPreviewButton,
                    "检查同步计划；此步骤不写入文件");
                break;
            case MigrationWorkflowPhase.Reviewing:
                SelectedCountFooterText.Text =
                    $"将写入 {workflowState.PlannedFileCount} 个文件 · 先备份";
                DryRunPreviewButton.Content = "备份并同步";
                DryRunPreviewButton.IsEnabled = workflowState.CanExecute;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                    DryRunPreviewButton,
                    "备份并同步已确认的设置");
                break;
            case MigrationWorkflowPhase.Executing:
            case MigrationWorkflowPhase.RollingBack:
                SelectedCountFooterText.Text = progressPresentation.IsIndeterminate
                    ? progressPresentation.StageText
                    : $"{progressPresentation.StageText} · {Math.Round(displayedPercent):0}%";
                DryRunPreviewButton.Content = "正在安全处理…";
                DryRunPreviewButton.IsEnabled = false;
                break;
            case MigrationWorkflowPhase.Blocked when workflowState.Catalogs.Count > 0:
                var blockedSelection = _contentSelectionViewModel.CaptureSelection();
                SelectedCountFooterText.Text = $"已选 {blockedSelection.SelectedItems.Count} 项内容";
                DryRunPreviewButton.Content = "重新检查同步计划";
                DryRunPreviewButton.IsEnabled =
                    blockedSelection.SelectedItems.Count > 0 &&
                    !_contentSelectionViewModel.HasUnresolvedConflicts;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                    DryRunPreviewButton,
                    "重新检查同步计划；此步骤不写入文件");
                break;
            case MigrationWorkflowPhase.Succeeded:
                SelectedCountFooterText.Text = workflowState.StatusText;
                var canStartAnotherSync = MigrationWorkflowPolicy.CanStartAnotherSync(
                    workflowState.Phase,
                    workflowState.LastExecutionStatus,
                    workflowState.HasDeferredJeiSync,
                    workflowState.IsMutationInProgress,
                    workflowState.SourceInstanceId is not null &&
                    workflowState.TargetInstanceId is not null);
                DryRunPreviewButton.Content = canStartAnotherSync ? "再次同步" : "同步已验证";
                if (workflowState.HasDeferredJeiSync)
                {
                    DryRunPreviewButton.Content = "等待 JEI 复核";
                }

                DryRunPreviewButton.IsEnabled = canStartAnotherSync;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                    DryRunPreviewButton,
                    canStartAnotherSync
                        ? "重新验证实例和内容后开始新的同步"
                        : DryRunPreviewButton.Content?.ToString() ?? "同步已验证");
                break;
            case MigrationWorkflowPhase.RecoveryRequired:
                SelectedCountFooterText.Text = "请先完成上次同步的恢复";
                DryRunPreviewButton.Content = "恢复优先";
                DryRunPreviewButton.IsEnabled = false;
                break;
            default:
                SelectedCountFooterText.Text = workflowState.StatusText;
                DryRunPreviewButton.Content = "检查同步计划";
                DryRunPreviewButton.IsEnabled = false;
                break;
        }

        LocalizeElements(SelectedCountFooterText, DryRunPreviewButton);
    }

    private async Task RunWorkflowDiscoveryAsync(bool chooseFolder)
    {
        if (_workflow is null || _discoveryInFlight || _disposed)
        {
            return;
        }

        _discoveryInFlight = true;
        SetDiscoveryButtonsEnabled(false);
        SetDiscoveryActivity(true);
        ScanStatusText.Text = "正在搜索可用实例…";
        LocalizeElements(ScanStatusText);
        try
        {
            if (chooseFolder)
            {
                var selectedPath = await _workflowFolderPicker!.PickFolderAsync(_workflowLifetime.Token);
                if (!string.IsNullOrWhiteSpace(selectedPath))
                {
                    await _workflow.AddSelectedFolderAsync(selectedPath, _workflowLifetime.Token);
                }
            }
            else
            {
                await _workflow.AutoDiscoverAsync(_workflowLifetime.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation preserves the accepted instance pair.
        }
        finally
        {
            _discoveryInFlight = false;
            SetDiscoveryActivity(false);
            if (!_disposed)
            {
                PresentWorkflowState(_workflow.State);
            }
        }
    }

    private async Task PrepareOrExecuteWorkflowAsync()
    {
        if (_workflow is null)
        {
            return;
        }

        var snapshot = _workflow.State;
        var sourceId = snapshot.SourceInstanceId;
        var targetId = snapshot.TargetInstanceId;
        if (MigrationWorkflowPolicy.CanStartAnotherSync(
                snapshot.Phase,
                snapshot.LastExecutionStatus,
                snapshot.HasDeferredJeiSync,
                snapshot.IsMutationInProgress,
                sourceId is not null && targetId is not null) &&
            sourceId is not null &&
            targetId is not null)
        {
            await ChangeWorkflowPairAsync(sourceId, targetId);
            return;
        }

        if (snapshot.Phase == MigrationWorkflowPhase.Reviewing)
        {
            await _workflow.ExecuteAsync(_workflowLifetime.Token);
            return;
        }

        if (snapshot.Phase is MigrationWorkflowPhase.Selecting or MigrationWorkflowPhase.Blocked &&
            snapshot.Catalogs.Count > 0)
        {
            await _workflow.PreparePlanAsync(
                _contentSelectionViewModel.CaptureSelection(),
                _workflowLifetime.Token);
        }
    }

    private async Task ChangeWorkflowPairAsync(string sourceId, string targetId)
    {
        if (_workflow is null)
        {
            return;
        }

        try
        {
            await _workflow.SelectPairAsync(sourceId, targetId, _workflowLifetime.Token);
        }
        catch (OperationCanceledException)
        {
            // Page shutdown cancels pair preparation.
        }
    }

    private async void RecoveryFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workflow?.State.PendingRecovery is not { } pending)
        {
            return;
        }

        var selected = await _workflowFolderPicker!.PickFolderAsync(_workflowLifetime.Token);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            await _workflow.SupplyRecoveryFolderAsync(
                pending.TransactionId,
                selected,
                _workflowLifetime.Token);
        }
    }

    private async void RecoverNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workflow?.State.PendingRecovery is not { } pending)
        {
            return;
        }

        await _workflow.RecoverAsync(
            pending.TransactionId,
            reselection: null,
            _workflowLifetime.Token);
    }

    private async void ExportRecoveryDiagnosticButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workflow?.State.PendingRecovery is not { } pending)
        {
            return;
        }

        await _workflow.ExportRecoveryDiagnosticAsync(
            pending.TransactionId,
            _workflowLifetime.Token);
    }

    private async void UndoMigrationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workflow?.State.CommittedTransactionId is not { } transactionId)
        {
            return;
        }

        await _workflow.UndoAsync(transactionId, _workflowLifetime.Token);
    }

}
