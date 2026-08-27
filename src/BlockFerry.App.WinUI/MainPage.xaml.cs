// WinUI presentation for discovery, review, protected migration, recovery, and undo.
using BlockFerry.App.WinUI.Controls;
using BlockFerry.App.WinUI.Discovery;
using BlockFerry.App.WinUI.Selection;
using BlockFerry.App.WinUI.Services;
using BlockFerry.Core.Content;
using BlockFerry.Core.Options;
using BlockFerry.Core.Pcl2;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Text;

namespace BlockFerry.App.WinUI;

public sealed partial class MainPage : Page, IDisposable
{
    internal event EventHandler<DrawerModalPhaseChangedEventArgs>? DrawerModalPhaseChanged;
    private Pcl2OptionsMigrationPreviewer? _previewer;
    private readonly OperationGenerationCounter _operationGenerations = new();
    private readonly DrawerModalLifecycleCoordinator _drawerLifecycle = new();
    private CancellationTokenSource? _catalogCancellation;
    private CancellationTokenSource? _previewCancellation;
    private bool _catalogInFlight;
    private bool _previewInFlight;
    private Pcl2OptionsSelectionSession? _selectionSession;
    private OptionsSelectionFocusToken? _focusBeforeResult;
    private IReadOnlyList<Pcl2Instance> _discoveredInstances = [];
    private readonly Dictionary<FrameworkElement, Storyboard> _activeRevealTransitions = [];
    private readonly List<ContentAdapterCard> _contentAdapterCards = [];
    private ContentSelectionViewModel _contentSelectionViewModel =
        new(Array.Empty<ContentCatalog>());
    private MigrationViewState _viewState = MigrationViewState.AwaitingDiscovery;
    private DiscoveryViewModel? _discoveryViewModel;
    private OptionsSelectionCatalog? _selectionCatalog;
    private bool _animationsEnabled = true;
    private bool _highContrast;
    private bool _pageLoaded;
    private bool _updatingPickers;
    private bool _drawerTransitioning;
    private bool _drawerClosing;
    private bool _refreshOptionsOnLoad;
    private Control? _focusBeforeDrawer;
    private Control? _drawerInitialFocus;
    private Storyboard? _drawerTransition;
    private long _drawerTransitionGeneration;
    private SyncPresentationState? _lastPresentationState;
    private int _lastPresentationStep = -1;
    private string? _lastPresentationDetail;
    private int _lastPlannedChangeCount;
    private bool _discoveryInFlight;
    private bool _disposed;

    public MainPage()
    {
        InitializeComponent();
        ApplyViewState(MigrationViewState.AwaitingDiscovery);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (_workflow is not null)
        {
            return;
        }

        if (e.Parameter is not MainPageNavigationContext navigation)
        {
            return;
        }

        _discoveryViewModel?.Dispose();
        _discoveryViewModel = DiscoveryViewModel.CreateProduction(navigation.FolderPickerFactory());
        ApplyViewState(_discoveryViewModel.State);
        ScanStatusText.Text = _discoveryViewModel.StatusText;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Dispose();
        base.OnNavigatedFrom(e);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _workflowLifetime.Cancel();
        if (_workflow is not null)
        {
            _workflow.StateChanged -= Workflow_StateChanged;
        }

        CancelRequest(ref _catalogCancellation);
        CancelRequest(ref _previewCancellation);
        _discoveryViewModel?.Dispose();
        _discoveryViewModel = null;
        _previewer = null;
        _workflowLifetime.Dispose();
    }

    /// <summary>
    /// Central UI projection point retained for future host/view-model integration.
    /// </summary>
    public void ApplyViewState(MigrationViewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ResetOptionsSelectionForPairChange();
        _viewState = state;
        _discoveredInstances = [];
        ProjectViewState(state);

        _updatingPickers = true;
        SourceInstancePicker.ItemsSource = new[] { state.SourceInstance };
        TargetInstancePicker.ItemsSource = new[] { state.TargetInstance };
        SourceInstancePicker.SelectedIndex = 0;
        TargetInstancePicker.SelectedIndex = 0;
        _updatingPickers = false;

        DiscoveryCard.Visibility = DiscoveryEntryVisibilityPolicy.IsVisible(state)
            ? Visibility.Visible
            : Visibility.Collapsed;
        OptionsSelectionPanel.Visibility = state.IsDemo || state.CanStart
            ? Visibility.Visible
            : Visibility.Collapsed;
        ErrorCard.Visibility = Visibility.Collapsed;
        ResultCard.Visibility = Visibility.Collapsed;
        PrimaryActionButton.IsEnabled = state.CanStart;
        SetSyncPresentation(SyncPresentationState.Idle, 0, null);
        RepositionDecorativeNumber(PageRoot.ActualWidth, PageRoot.ActualHeight);
        AnimateProjectionChange();
        if (state.IsDemo)
        {
            _ = RefreshOptionsSelectionSessionAsync();
        }

        UpdateGenerateButtonState();
    }

    public void ConfigureAccessibility(bool animationsEnabled, bool advancedEffectsEnabled, bool highContrast)
    {
        _animationsEnabled = animationsEnabled;
        _highContrast = highContrast;
        OptionsSelectionControl.ConfigureAccessibility(animationsEnabled, highContrast);
        foreach (var contentCard in _contentAdapterCards)
        {
            contentCard.ConfigureAccessibility(animationsEnabled, highContrast);
        }

        if (DrawerPanel.Background is AcrylicBrush drawerAcrylic)
        {
            drawerAcrylic.AlwaysUseFallback = !advancedEffectsEnabled || highContrast;
        }

        if (!animationsEnabled)
        {
            StatusProgressRing.IsActive = false;
            PrimaryProgressRing.IsActive = false;
        }

        if (highContrast)
        {
            CompleteRevealTransitions();
            CompleteActiveDrawerTransition();
        }
    }

    public void SetSyncPresentation(
        SyncPresentationState state,
        int stepIndex,
        string? detail)
    {
        var clampedStep = Math.Clamp(stepIndex, 0, 3);
        var presentationChanged = _lastPresentationState != state ||
                                  _lastPresentationStep != clampedStep ||
                                  !string.Equals(_lastPresentationDetail, detail, StringComparison.Ordinal);
        var isRunning = state == SyncPresentationState.Running;
        var isCompleted = state == SyncPresentationState.Completed;
        var isBlocked = state == SyncPresentationState.Blocked;

        StatusIconHost.Visibility = state == SyncPresentationState.Idle
            ? Visibility.Collapsed
            : Visibility.Visible;
        StatusProgressRing.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;
        StatusCheckIcon.Visibility = isCompleted ? Visibility.Visible : Visibility.Collapsed;
        StatusWarningIcon.Visibility = isBlocked ? Visibility.Visible : Visibility.Collapsed;
        SyncProgressBar.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;

        PrimaryIdleContent.Visibility = state is SyncPresentationState.Idle or SyncPresentationState.Blocked
            ? Visibility.Visible
            : Visibility.Collapsed;
        PrimaryRunningContent.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;
        PrimaryDoneContent.Visibility = isCompleted ? Visibility.Visible : Visibility.Collapsed;

        StatusProgressRing.IsActive = isRunning && _animationsEnabled;
        PrimaryProgressRing.IsActive = isRunning && _animationsEnabled;

        switch (state)
        {
            case SyncPresentationState.Idle:
                StatusTitleText.Text = "准备同步";
                StatusSubtitleText.Text = _viewState.IsDemo
                    ? "演示数据 · 只读预览 · 0 写入"
                    : "真实实例 · 选择内容后会显示最终清单";
                SyncProgressBar.Value = 0;
                PrimaryActionButton.IsEnabled = _viewState.CanStart;
                PrimaryDoneText.Text = _viewState.IsDemo ? "演示完成" : "同步完成";
                break;

            case SyncPresentationState.Running:
                StatusTitleText.Text = _viewState.IsDemo
                    ? "正在生成演示预览"
                    : "正在安全处理同步";
                StatusSubtitleText.Text = detail ?? "正在执行受保护操作";
                SyncProgressBar.Value = 42;
                PrimaryActionButton.IsEnabled = false;
                break;

            case SyncPresentationState.Completed:
                StatusTitleText.Text = _viewState.IsDemo ? "演示预览完成" : "同步已验证";
                StatusSubtitleText.Text = detail ?? "目标文件已经复读验证";
                SyncProgressBar.Value = 100;
                PrimaryDoneText.Text = _viewState.IsDemo ? "演示完成" : "同步完成";
                PrimaryActionButton.IsEnabled = false;
                break;

            case SyncPresentationState.Blocked:
                StatusTitleText.Text = "操作被阻止";
                StatusSubtitleText.Text = detail ?? "请在同步设置中查看安全提示";
                SyncProgressBar.Value = 0;
                PrimaryActionButton.IsEnabled = _viewState.CanStart;
                break;
        }

        _lastPresentationState = state;
        _lastPresentationStep = clampedStep;
        _lastPresentationDetail = detail;
        UpdateDrawerFooterPresentation();

        if (presentationChanged)
        {
            PlayReveal(StatusArea, StatusTranslate, 160, 5);
            PlayReveal(PrimaryContentHost, PrimaryContentTranslate, 160, 4);
        }
    }

    private void ProjectViewState(MigrationViewState state)
    {
        ModeLabelText.Text = state.ModeLabel;
        DrawerHeaderStatusText.Text = MigrationViewCopy.DrawerHeaderStatus(state);
        SourceVersionRun.Text = state.SourceVersion;
        TargetVersionRun.Text = state.TargetVersion;
        GiantVersionText.Text = DecorativeVersion(state.TargetVersion);
        PackNameText.Text = state.PackName;
        HeaderContextText.Text = $"{CompactPackName(state.PackName)} · {state.LauncherName}";
        PrimaryIdleText.Text = "选择同步设置";
        SafetyBoundaryText.Text = state.IsDemo
            ? "当前是 UI 状态演示。未读取真实实例、未创建还原点、未迁移设置，也不会显示完成 Toast。"
            : "发现与选择阶段只读；最终清单确认后才会先备份、再同步并复读验证。";

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            PrimaryActionButton,
            "打开同步设置选择");
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _pageLoaded = true;
        if (_workflow is not null)
        {
            _ = StartWorkflowAsync();
        }

        SetDiscoveryButtonsEnabled(!_discoveryInFlight);
        var displayWeight = new FontWeight { Weight = 630 };
        SourceVersionLine.FontWeight = displayWeight;
        TargetVersionLine.FontWeight = displayWeight;
        PackNameText.FontWeight = displayWeight;
        GiantVersionText.FontWeight = displayWeight;
        PrimaryIdleText.FontWeight = displayWeight;
        RepositionDecorativeNumber(PageRoot.ActualWidth, PageRoot.ActualHeight);
        if ((_refreshOptionsOnLoad || _selectionCatalog is null) &&
            !_catalogInFlight &&
            _viewState.CanStart)
        {
            _refreshOptionsOnLoad = false;
            _ = RefreshOptionsSelectionSessionAsync();
        }
        else
        {
            UpdateGenerateButtonState();
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _pageLoaded = false;
        var operationWasInFlight = _catalogInFlight || _previewInFlight;
        var recovery = OptionsSelectionLifecyclePolicy.DecideRecovery(
            operationWasInFlight,
            _selectionCatalog is not null,
            _viewState.IsDemo || _selectionSession is not null);

        CancelRequest(ref _catalogCancellation);
        CancelRequest(ref _previewCancellation);
        _catalogInFlight = false;
        _previewInFlight = false;
        NormalizeDrawerForUnload();
        CompleteRevealTransitions();

        _refreshOptionsOnLoad = recovery.RefreshNeeded;
        if (recovery.ReturnToSelection)
        {
            ResultCard.Visibility = Visibility.Collapsed;
            ErrorCard.Visibility = Visibility.Collapsed;
            OptionsSelectionPanel.Visibility = Visibility.Visible;
            OptionsSelectionControl.IsEnabled = recovery.SelectionEnabled;
            SetSyncPresentation(SyncPresentationState.Idle, 0, null);
            UpdateGenerateButtonState();
        }
    }

    private void PageRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        PageRoot.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
        };

        DrawerPanel.Width = e.NewSize.Width <= 620
            ? e.NewSize.Width
            : Math.Min(420, e.NewSize.Width);
        RepositionDecorativeNumber(e.NewSize.Width, e.NewSize.Height);
    }

    private void RepositionDecorativeNumber(double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var narrow = width <= 620;
        var fontSize = narrow ? 184 : Math.Clamp(width * 0.275, 220, 270);
        var textWidth = fontSize * 1.22;
        GiantVersionText.FontSize = fontSize;
        GiantVersionText.Width = textWidth;
        GiantVersionText.LineHeight = fontSize * 0.77;
        GiantVersionText.TextAlignment = TextAlignment.Right;
        Canvas.SetLeft(GiantVersionText, width - textWidth + (narrow ? 30 : 44));
        Canvas.SetTop(GiantVersionText, height - (fontSize * 0.77) - (narrow ? 160 : 42));
    }

    private void PrimaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        OpenDrawer();
    }

    private async void DryRunPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workflow is not null && _workflow.State.Phase != MigrationWorkflowPhase.Demo)
        {
            await PrepareOrExecuteWorkflowAsync();
            return;
        }

        await GenerateSelectedPreviewAsync();
    }

    private void ModifySelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workflow is not null && _workflow.State.Phase == MigrationWorkflowPhase.Reviewing)
        {
            _workflow.InvalidatePlan();
            return;
        }

        ResultCard.Visibility = Visibility.Collapsed;
        ErrorCard.Visibility = Visibility.Collapsed;
        OptionsSelectionPanel.Visibility = Visibility.Visible;
        SetSyncPresentation(SyncPresentationState.Idle, 0, null);

        var restored = _focusBeforeResult is not null &&
                       OptionsSelectionControl.RestoreFocus(_focusBeforeResult);
        if (!restored && _selectionCatalog is not null)
        {
            var firstCategory = _selectionCatalog.SelectableDifferences
                .Select(item => item.Category)
                .Distinct()
                .OrderBy(category => category)
                .FirstOrDefault();
            if (_selectionCatalog.SelectableDifferences.Count > 0)
            {
                OptionsSelectionControl.RestoreFocus(
                    OptionsSelectionFocusToken.ForCategoryToggle(firstCategory));
            }
        }

        UpdateGenerateButtonState();
    }

    private void OptionsSelectionControl_SelectionChanged(
        object sender,
        OptionsSelectionChangedEventArgs e)
    {
        if (_workflow is not null && _workflow.State.Phase != MigrationWorkflowPhase.Demo)
        {
            _contentSelectionViewModel.ApplyVanillaSelection(
                OptionsSelectionControl.SnapshotSelectedKeys());
            return;
        }

        InvalidateAcceptedPlanForSelectionChange();
        var focus = OptionsSelectionControl.CaptureFocus();
        if (focus.Target != OptionsSelectionFocusTarget.None)
        {
            _focusBeforeResult = focus;
        }

        if (ErrorCard.Visibility == Visibility.Visible)
        {
            ErrorCard.Visibility = Visibility.Collapsed;
            SetSyncPresentation(SyncPresentationState.Idle, 0, null);
        }

        UpdateGenerateButtonState();
    }

    private void ContentSelectionViewModel_SelectionChanged(object? sender, EventArgs e)
    {
        _workflow?.InvalidatePlan();
        if (_workflow is not null && _workflow.State.Phase != MigrationWorkflowPhase.Demo)
        {
            UpdateWorkflowFooter(_workflow.State);
            return;
        }

        InvalidateAcceptedPlanForSelectionChange();
        UpdateGenerateButtonState();
    }

    private void InvalidateAcceptedPlanForSelectionChange()
    {
        if (_catalogInFlight)
        {
            return;
        }

        _operationGenerations.Next();
        CancelRequest(ref _previewCancellation);
        _previewInFlight = false;
        ResultCard.Visibility = Visibility.Collapsed;
        if (_lastPresentationState == SyncPresentationState.Completed)
        {
            SetSyncPresentation(SyncPresentationState.Idle, 0, null);
        }
    }

    private async void AutomaticDiscoveryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workflow is not null)
        {
            await RunWorkflowDiscoveryAsync(chooseFolder: false);
            return;
        }

        if (_discoveryViewModel is null)
        {
            return;
        }

        await RunDiscoveryActionAsync(
            "正在检查受保护的最近位置、常见 Minecraft 目录和 PCL 快捷方式…",
            _discoveryViewModel.DiscoverAutomaticallyAsync);
    }

    private async void FolderPickerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workflow is not null)
        {
            await RunWorkflowDiscoveryAsync(chooseFolder: true);
            return;
        }

        if (_discoveryViewModel is null)
        {
            return;
        }

        await RunDiscoveryActionAsync(
            "请选择 PCL 文件夹、.minecraft、versions 或具体实例文件夹…",
            _discoveryViewModel.ChooseFolderAsync);
    }

    private void DemoModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workflow is not null)
        {
            _workflow.EnterDemo();
            ApplyViewState(_workflow.State.ViewState);
            return;
        }

        if (_discoveryViewModel is null || _discoveryInFlight)
        {
            return;
        }

        _discoveryViewModel.EnterDemo();
        _previewer = null;
        ApplyViewState(_discoveryViewModel.State);
        ScanStatusText.Text = _discoveryViewModel.StatusText;
    }

    private async Task RunDiscoveryActionAsync(
        string pendingText,
        Func<CancellationToken, Task> action)
    {
        if (_discoveryInFlight || _disposed)
        {
            return;
        }

        _discoveryInFlight = true;
        SetDiscoveryButtonsEnabled(false);
        ScanStatusText.Text = pendingText;
        PlayReveal(ScanStatusText, ScanStatusTranslate, 160, 4);
        try
        {
            await action(CancellationToken.None);
            if (!_disposed)
            {
                PresentDiscoveryViewModel();
            }
        }
        catch (OperationCanceledException)
        {
            // Closing or navigating away cancels the request without changing the accepted pair.
        }
        finally
        {
            _discoveryInFlight = false;
            if (!_disposed)
            {
                SetDiscoveryButtonsEnabled(true);
                UpdateGenerateButtonState();
            }
        }
    }

    private void PresentDiscoveryViewModel()
    {
        var viewModel = _discoveryViewModel;
        if (viewModel is null)
        {
            return;
        }

        ScanStatusText.Text = viewModel.StatusText;
        var acceptedSession = viewModel.ActiveSession;
        if (acceptedSession is null)
        {
            if (viewModel.Diagnostics.Count > 0)
            {
                ShowSelectionError(
                    "没有发现可安全配对的来源与目标；当前选择未改变。",
                    viewModel.Diagnostics.Select(FormatDiagnostic).ToArray());
            }

            PlayReveal(ScanStatusText, ScanStatusTranslate, 160, 4);
            return;
        }

        var sessionChanged = !ReferenceEquals(_discoveredInstances, viewModel.Instances);
        _discoveredInstances = viewModel.Instances;
        var labels = _discoveredInstances.Select(InstanceLabel).ToArray();
        var sourceIndex = FindInstanceIndex(viewModel.SourceInstanceId);
        var targetIndex = FindInstanceIndex(viewModel.TargetInstanceId);
        _updatingPickers = true;
        SourceInstancePicker.ItemsSource = labels;
        TargetInstancePicker.ItemsSource = labels;
        SourceInstancePicker.SelectedIndex = sourceIndex;
        TargetInstancePicker.SelectedIndex = targetIndex;
        _updatingPickers = false;

        _previewer = viewModel.OptionsPreviewer;
        _viewState = viewModel.State;
        ProjectViewState(_viewState);
        DiscoveryCard.Visibility = Visibility.Visible;
        OptionsSelectionPanel.Visibility = Visibility.Visible;
        PrimaryActionButton.IsEnabled = _viewState.CanStart;
        if (sessionChanged)
        {
            ResetOptionsSelectionForPairChange();
        }

        ErrorCard.Visibility = Visibility.Collapsed;
        ResultCard.Visibility = Visibility.Collapsed;
        SetSyncPresentation(SyncPresentationState.Idle, 0, null);
        AnimateProjectionChange();
        PlayReveal(ScanStatusText, ScanStatusTranslate, 160, 4);
        if (_pageLoaded && _viewState.CanStart)
        {
            _ = RefreshOptionsSelectionSessionAsync();
        }
    }

    private void SetDiscoveryButtonsEnabled(bool enabled)
    {
        AutomaticDiscoveryButton.IsEnabled = enabled;
        FolderPickerButton.IsEnabled = enabled;
        DemoModeButton.IsEnabled = enabled;
    }

    private int FindInstanceIndex(string? instanceId)
    {
        if (instanceId is null)
        {
            return -1;
        }

        for (var index = 0; index < _discoveredInstances.Count; index++)
        {
            if (string.Equals(_discoveredInstances[index].Id, instanceId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private async void InstancePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPickers || _discoveredInstances.Count == 0)
        {
            return;
        }

        if (_workflow is not null)
        {
            var changedPicker = sender as ComboBox;
            var selected = changedPicker is null ? null : SelectedInstance(changedPicker);
            var currentState = _workflow.State;
            if (selected is not null &&
                currentState.SourceInstanceId is { } currentSourceId &&
                currentState.TargetInstanceId is { } currentTargetId)
            {
                await RouteSelectionIntentDispatcher.DispatchAsync(
                    changedPicker: changedPicker,
                    sourcePicker: SourceInstancePicker,
                    targetPicker: TargetInstancePicker,
                    currentSourceId: currentSourceId,
                    currentTargetId: currentTargetId,
                    selectedInstanceId: selected.Id,
                    submitPairAsync: ChangeWorkflowPairAsync);
            }

            return;
        }

        UpdateRealSelectionProjection();
    }

    private void UpdateRealSelectionProjection()
    {
        var source = SelectedInstance(SourceInstancePicker);
        var target = SelectedInstance(TargetInstancePicker);
        var canStart = source is not null &&
                       target is not null &&
                       _discoveryViewModel?.SelectPair(source.Id, target.Id) == true;
        if (canStart)
        {
            _viewState = _discoveryViewModel!.State;
        }

        ProjectViewState(_viewState);
        ResetOptionsSelectionForPairChange();
        PrimaryActionButton.IsEnabled = canStart;
        SetSyncPresentation(SyncPresentationState.Idle, 0, null);
        AnimateProjectionChange();
        if (canStart)
        {
            _ = RefreshOptionsSelectionSessionAsync();
        }
        else
        {
            ShowSelectionError("请选择两个不同且可用的来源与目标实例。", []);
        }
    }

    private async Task RefreshOptionsSelectionSessionAsync()
    {
        if (!OptionsSelectionModePolicy.UsesLegacyOptionsSelection(
                workflowAttached: _workflow is not null,
                workflowIsDemo: _workflow?.State.Phase == MigrationWorkflowPhase.Demo))
        {
            return;
        }

        if (_catalogInFlight)
        {
            return;
        }

        var requestedState = _viewState;
        var source = SelectedInstance(SourceInstancePicker);
        var target = SelectedInstance(TargetInstancePicker);
        var previewer = _previewer;
        if (!requestedState.IsDemo &&
            (source is null || target is null || previewer is null))
        {
            ShowSelectionError("请选择来源与目标后再准备可选设置。", []);
            return;
        }

        var generation = _operationGenerations.Next();
        var requestedSession = _selectionSession;
        CancelRequest(ref _catalogCancellation);
        CancelRequest(ref _previewCancellation);
        _previewInFlight = false;

        var cancellation = new CancellationTokenSource();
        _catalogCancellation = cancellation;
        _catalogInFlight = true;
        _selectionCatalog = null;
        OptionsSelectionControl.Clear();
        OptionsSelectionPanel.Visibility = Visibility.Visible;
        ResultCard.Visibility = Visibility.Collapsed;
        ErrorCard.Visibility = Visibility.Collapsed;
        UpdateGenerateButtonState();

        try
        {
            OptionsSelectionCatalog catalog;
            Pcl2OptionsSelectionSession? nextSession = null;
            Pcl2OptionsSelectionPreparation? preparation = null;
            if (requestedState.IsDemo)
            {
                catalog = await Task.Run(
                    DemoOptionsSelectionData.CreateCatalog,
                    cancellation.Token);
            }
            else
            {
                preparation = await Task.Run(
                    () => previewer!.PrepareSelection(source!, target!, cancellation.Token),
                    cancellation.Token);
                if (!SelectionRequestAcceptance.IsCurrent(
                        generation,
                        _operationGenerations.Current,
                        requestedSession,
                        _selectionSession,
                        IsCurrentPair(source!, target!),
                        cancellation.Token))
                {
                    return;
                }

                if (preparation.IsBlocked || preparation.Session is null)
                {
                    ShowSelectionError(
                        "安全检查阻止了设置目录准备；没有执行任何写入。",
                        preparation.Diagnostics.Select(FormatDiagnostic).ToArray());
                    return;
                }

                nextSession = preparation.Session;
                catalog = nextSession.Catalog;
            }

            var isCurrentPair = requestedState.IsDemo
                ? ReferenceEquals(requestedState, _viewState) && _viewState.IsDemo
                : IsCurrentPair(source!, target!);
            if (!SelectionRequestAcceptance.IsCurrent(
                    generation,
                    _operationGenerations.Current,
                    requestedSession,
                    _selectionSession,
                    isCurrentPair,
                    cancellation.Token))
            {
                return;
            }

            _selectionSession = nextSession;
            _selectionCatalog = catalog;
            OptionsSelectionControl.LoadCatalog(catalog);
            OptionsSelectionPanel.Visibility = Visibility.Visible;
            ResultCard.Visibility = Visibility.Collapsed;
            ErrorCard.Visibility = Visibility.Collapsed;
            SetSyncPresentation(SyncPresentationState.Idle, 0, null);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            var isCurrentPair = requestedState.IsDemo
                ? ReferenceEquals(requestedState, _viewState) && _viewState.IsDemo
                : source is not null && target is not null && IsCurrentPair(source, target);
            if (SelectionRequestAcceptance.IsCurrent(
                    generation,
                    _operationGenerations.Current,
                    requestedSession,
                    _selectionSession,
                    isCurrentPair,
                    cancellation.Token))
            {
                ShowSelectionError(
                    "无法准备当前设置目录；选择尚未生成。",
                    [exception.Message]);
            }
        }
        finally
        {
            if (ReferenceEquals(_catalogCancellation, cancellation))
            {
                _catalogCancellation = null;
                _catalogInFlight = false;
                UpdateGenerateButtonState();
            }

            cancellation.Dispose();
        }
    }

    private async Task GenerateSelectedPreviewAsync()
    {
        if (_previewInFlight ||
            ResultCard.Visibility == Visibility.Visible ||
            _selectionCatalog is null)
        {
            return;
        }

        var selectedKeys = OptionsSelectionControl.SnapshotSelectedKeys();
        if (selectedKeys.Count == 0)
        {
            UpdateGenerateButtonState();
            return;
        }

        var requestedState = _viewState;
        var requestedSession = _selectionSession;
        var source = SelectedInstance(SourceInstancePicker);
        var target = SelectedInstance(TargetInstancePicker);
        var previewer = _previewer;
        if (!requestedState.IsDemo &&
            (requestedSession is null ||
             source is null ||
             target is null ||
             previewer is null ||
             !IsCurrentPair(source, target)))
        {
            return;
        }

        var generation = _operationGenerations.Next();
        CancelRequest(ref _previewCancellation);
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        _previewInFlight = true;

        var focus = OptionsSelectionControl.CaptureFocus();
        if (focus.Target != OptionsSelectionFocusTarget.None)
        {
            _focusBeforeResult = focus;
        }

        SetSyncPresentation(SyncPresentationState.Running, 0, "正在生成当前选择的只读预览 · 0 写入");
        UpdateGenerateButtonState();

        try
        {
            var preview = requestedState.IsDemo
                ? await Task.Run(
                    () => DemoOptionsSelectionData.CreatePreview(selectedKeys),
                    cancellation.Token)
                : await Task.Run(
                    () => previewer!.PreviewSelected(requestedSession!, selectedKeys, cancellation.Token),
                    cancellation.Token);

            var isCurrentPair = requestedState.IsDemo
                ? ReferenceEquals(requestedState, _viewState) && _viewState.IsDemo
                : IsCurrentPair(source!, target!);
            if (!SelectionRequestAcceptance.IsCurrent(
                    generation,
                    _operationGenerations.Current,
                    requestedSession,
                    _selectionSession,
                    isCurrentPair,
                    cancellation.Token))
            {
                return;
            }

            if (preview.IsStale)
            {
                const string staleMessage =
                    "来源或目标 options.txt 已变化；旧选择已失效，已重新准备最新目录。";
                _selectionSession = null;
                _selectionCatalog = null;
                OptionsSelectionControl.Clear();
                ShowSelectionError(staleMessage, preview.Diagnostics.Select(FormatDiagnostic).ToArray());
                await RefreshOptionsSelectionSessionAsync();
                if (_selectionCatalog is not null &&
                    ReferenceEquals(requestedState, _viewState) &&
                    source is not null &&
                    target is not null &&
                    IsCurrentPair(source, target))
                {
                    ShowSelectionError(staleMessage, []);
                }

                return;
            }

            if (preview.IsBlocked)
            {
                ShowSelectionError(
                    "安全检查阻止了当前预览；你的设置选择已保留。",
                    preview.Diagnostics.Select(FormatDiagnostic).ToArray());
                return;
            }

            PresentSelectedPreview(preview);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            var isCurrentPair = requestedState.IsDemo
                ? ReferenceEquals(requestedState, _viewState) && _viewState.IsDemo
                : source is not null && target is not null && IsCurrentPair(source, target);
            if (SelectionRequestAcceptance.IsCurrent(
                    generation,
                    _operationGenerations.Current,
                    requestedSession,
                    _selectionSession,
                    isCurrentPair,
                    cancellation.Token))
            {
                ShowSelectionError(
                    "只读预览失败；你的设置选择已保留。",
                    [exception.Message]);
            }
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                _previewCancellation = null;
                _previewInFlight = false;
                UpdateGenerateButtonState();
            }

            cancellation.Dispose();
        }
    }

    private void PresentSelectedPreview(Pcl2SelectedOptionsPreview preview)
    {
        MigrationReviewControl.BindPreview(preview.PlannedChanges
            .Select(OptionsPreviewResultFormatter.FormatDifference));
        PreviewSummaryText.Text =
            $"计划同步 {preview.PlannedChanges.Count} 项设置；这是只读预览，0 写入。";
        PreviewPathsText.Text = DiscoveryUiText.FormatPreviewLocations(
            preview.SourceOptionsPath,
            preview.TargetOptionsPath);
        PreviewSecondaryCountsText.Text =
            $"未选择 {preview.SkippedDifferences.Count} · 受保护 {preview.ProtectedDifferences.Count} · 仅目标 {preview.TargetOnlyItems.Count}";

        _lastPlannedChangeCount = preview.PlannedChanges.Count;
        ErrorCard.Visibility = Visibility.Collapsed;
        OptionsSelectionPanel.Visibility = Visibility.Collapsed;
        ResultCard.Visibility = Visibility.Visible;
        PreviewResultHeading.Focus(FocusState.Programmatic);

        var peer = FrameworkElementAutomationPeer.FromElement(PreviewResultHeading) ??
                   FrameworkElementAutomationPeer.CreatePeerForElement(PreviewResultHeading);
        peer?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted,
            AutomationNotificationProcessing.MostRecent,
            $"只读预览已完成，计划同步 {preview.PlannedChanges.Count} 项设置。",
            "BlockFerry.OptionsPreview.Completed");

        SetSyncPresentation(
            SyncPresentationState.Completed,
            3,
            $"已生成 {preview.PlannedChanges.Count} 项计划变更 · 0 写入");
        RevealPreviewResults();
    }

    private void ShowSelectionError(string message, string[] diagnostics)
    {
        ResultCard.Visibility = Visibility.Collapsed;
        OptionsSelectionPanel.Visibility = Visibility.Visible;
        SelectionErrorText.Text = message;
        SelectionErrorDiagnosticsItemsControl.ItemsSource = diagnostics;
        SelectionErrorDiagnosticsItemsControl.Visibility = diagnostics.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        ErrorCard.Visibility = Visibility.Visible;
        SetSyncPresentation(SyncPresentationState.Blocked, 0, $"{message} · 0 写入");
    }

    private void ResetOptionsSelectionForPairChange()
    {
        CancelRequest(ref _catalogCancellation);
        CancelRequest(ref _previewCancellation);
        _catalogInFlight = false;
        _previewInFlight = false;
        _selectionSession = null;
        _selectionCatalog = null;
        _focusBeforeResult = null;
        _lastPlannedChangeCount = 0;
        OptionsSelectionControl.Clear();
        ResetContentSelection(Array.Empty<ContentCatalog>());
        OptionsSelectionPanel.Visibility = Visibility.Visible;
        OptionsSelectionControl.IsEnabled = true;
        ResultCard.Visibility = Visibility.Collapsed;
        ErrorCard.Visibility = Visibility.Collapsed;
        _presentedReviewItems = null;
        MigrationReviewControl.Clear();
        UpdateGenerateButtonState();
    }

    internal void ResetContentSelection(
        IEnumerable<ContentCatalog> catalogs,
        ContentCompatibilityDisplayEvidence? compatibility = null)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        _contentSelectionViewModel.SelectionChanged -= ContentSelectionViewModel_SelectionChanged;
        _contentSelectionViewModel = new ContentSelectionViewModel(catalogs, compatibility);
        _contentSelectionViewModel.SelectionChanged += ContentSelectionViewModel_SelectionChanged;
        if (_workflow is not null)
        {
            if (_contentSelectionViewModel.VanillaOptionsCatalog is { } vanillaCatalog)
            {
                OptionsSelectionControl.LoadCatalog(
                    vanillaCatalog,
                    _contentSelectionViewModel.VanillaSelectedKeys);
            }
            else
            {
                OptionsSelectionControl.Clear();
            }
        }

        _contentAdapterCards.Clear();
        ContentAdapterCardsPanel.Children.Clear();
        foreach (var cardViewModel in _contentSelectionViewModel.SupplementalCards)
        {
            var card = new ContentAdapterCard();
            card.Bind(cardViewModel);
            card.ConfigureAccessibility(_animationsEnabled, _highContrast);
            _contentAdapterCards.Add(card);
            ContentAdapterCardsPanel.Children.Add(card);
        }
    }

    private static void CancelRequest(ref CancellationTokenSource? cancellation)
    {
        var request = cancellation;
        cancellation = null;
        request?.Cancel();
    }

    private void UpdateGenerateButtonState()
    {
        if (_workflow is not null && _workflow.State.Phase != MigrationWorkflowPhase.Demo)
        {
            OptionsSelectionControl.IsEnabled =
                _workflow.State.Phase == MigrationWorkflowPhase.Selecting &&
                _workflow.State.Catalogs.Count > 0 &&
                !_workflow.State.IsMutationInProgress;
            return;
        }

        var hasSession = _viewState.IsDemo || _selectionSession is not null;
        UpdateDrawerFooterPresentation();
        OptionsSelectionControl.IsEnabled =
            !_catalogInFlight &&
            !_previewInFlight &&
            _selectionCatalog is not null &&
            hasSession;
    }

    private void UpdateDrawerFooterPresentation()
    {
        var selectedCount = OptionsSelectionControl.SnapshotSelectedKeys().Count;
        var hasSession = _viewState.IsDemo || _selectionSession is not null;
        var canGeneratePreview =
            !_catalogInFlight &&
            !_previewInFlight &&
            ResultCard.Visibility != Visibility.Visible &&
            _selectionCatalog is not null &&
            selectedCount > 0 &&
            hasSession;

        string footerText;
        string buttonContent;
        bool buttonEnabled;
        if (ResultCard.Visibility == Visibility.Visible &&
            _lastPresentationState == SyncPresentationState.Completed)
        {
            footerText = $"计划 {_lastPlannedChangeCount} 项 · 0 写入";
            buttonContent = "预览已完成";
            buttonEnabled = false;
        }
        else if (ErrorCard.Visibility == Visibility.Visible ||
                 _lastPresentationState == SyncPresentationState.Blocked)
        {
            footerText = _lastPresentationDetail ?? "暂时无法生成预览 · 0 写入";
            buttonContent = canGeneratePreview ? "重试预览" : "生成预览";
            buttonEnabled = canGeneratePreview;
        }
        else if (_previewInFlight || _lastPresentationState == SyncPresentationState.Running)
        {
            footerText = "正在生成只读预览 · 0 写入";
            buttonContent = "正在生成预览…";
            buttonEnabled = false;
        }
        else if (_catalogInFlight)
        {
            footerText = "正在准备可选设置…";
            buttonContent = "生成预览";
            buttonEnabled = false;
        }
        else if (_selectionCatalog is not null)
        {
            footerText = $"已选 {selectedCount} / {_selectionCatalog.SelectableDifferences.Count} 项设置";
            buttonContent = "生成预览";
            buttonEnabled = canGeneratePreview;
        }
        else
        {
            footerText = "尚未准备可选设置";
            buttonContent = "生成预览";
            buttonEnabled = false;
        }

        SelectedCountFooterText.Text = footerText;
        DryRunPreviewButton.Content = buttonContent;
        DryRunPreviewButton.IsEnabled = buttonEnabled;
    }

    private bool IsCurrentPair(Pcl2Instance source, Pcl2Instance target) =>
        ReferenceEquals(source, SelectedInstance(SourceInstancePicker)) &&
        ReferenceEquals(target, SelectedInstance(TargetInstancePicker));

    private Pcl2Instance? SelectedInstance(ComboBox picker)
    {
        var index = picker.SelectedIndex;
        return index >= 0 && index < _discoveredInstances.Count
            ? _discoveredInstances[index]
            : null;
    }

    private static string InstanceLabel(Pcl2Instance instance)
    {
        var minecraft = instance.MinecraftVersion ?? "MC 未知";
        var packVersion = instance.ModpackIdentity.Version;
        var version = string.IsNullOrWhiteSpace(packVersion) ? minecraft : $"{packVersion} · MC {minecraft}";
        var isolation = instance.Isolation == Pcl2IsolationMode.Isolated ? "独立" : "需诊断";
        return $"{instance.DisplayName} · {version} · {isolation}";
    }

    private static string FormatDiagnostic(Pcl2Diagnostic diagnostic) =>
        DiscoveryUiText.FormatDiagnostic(diagnostic);

    private void DrawerCloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseDrawer();
    }

    private void DrawerScrim_Tapped(object sender, TappedRoutedEventArgs e)
    {
        CloseDrawer();
    }

    private void PageRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && DrawerLayer.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            CloseDrawer();
        }
    }

    private void OpenDrawer(Control? initialFocus = null)
    {
        if (_drawerLifecycle.Phase != DrawerModalPhase.Collapsed || _drawerTransitioning)
        {
            return;
        }

        _focusBeforeDrawer = FocusManager.GetFocusedElement(PageRoot.XamlRoot) as Control;
        _drawerInitialFocus = initialFocus ?? DrawerCloseButton;
        var generation = _drawerLifecycle.BeginOpening();
        _drawerTransitionGeneration = generation;
        RaiseDrawerModalPhaseChanged();
        DrawerLayer.Visibility = Visibility.Visible;
        DrawerPanel.Width = PageRoot.ActualWidth <= 620
            ? PageRoot.ActualWidth
            : Math.Min(420, PageRoot.ActualWidth);
        EnsureDrawerFocusWithin();

        if (_selectionCatalog is null && !_catalogInFlight && _viewState.CanStart)
        {
            _ = RefreshOptionsSelectionSessionAsync();
        }

        if (_highContrast)
        {
            CompleteDrawerOpen(generation);
            return;
        }

        _drawerTransitioning = true;
        _drawerClosing = false;
        DrawerPanel.IsHitTestVisible = false;
        DrawerScrim.IsHitTestVisible = true;
        DrawerTranslate.X = _animationsEnabled ? DrawerPanel.Width : 0;
        DrawerScrim.Opacity = 0;
        DrawerPanel.Opacity = _animationsEnabled ? 0.86 : 0;
        var storyboard = CreateDrawerStoryboard(
            drawerTarget: 0,
            scrimTarget: 1,
            panelOpacityTarget: 1,
            duration: TimeSpan.FromMilliseconds(_animationsEnabled ? 260 : 120),
            animatePosition: _animationsEnabled);
        _drawerTransition = storyboard;
        storyboard.Completed += (_, _) => CompleteDrawerOpen(storyboard, generation);
        storyboard.Begin();
    }

    private void CloseDrawer()
    {
        if (_drawerTransitioning)
        {
            return;
        }

        var isMutationInProgress = _workflow?.State.IsMutationInProgress == true;
        var closeRequest = _drawerLifecycle.RequestClose(isMutationInProgress);
        if (closeRequest.Outcome == DrawerCloseRequestOutcome.RejectedMutation)
        {
            FocusDrawerLiveStatus();
            return;
        }

        if (closeRequest.Outcome != DrawerCloseRequestOutcome.Closing)
        {
            return;
        }

        var generation = closeRequest.Generation;
        _drawerTransitionGeneration = generation;
        RaiseDrawerModalPhaseChanged();
        EnsureDrawerFocusWithin();
        if (_highContrast)
        {
            CompleteDrawerClose(generation);
            return;
        }

        _drawerTransitioning = true;
        _drawerClosing = true;
        DrawerPanel.IsHitTestVisible = false;
        DrawerScrim.IsHitTestVisible = true;
        var storyboard = CreateDrawerStoryboard(
            drawerTarget: _animationsEnabled ? DrawerPanel.Width : 0,
            scrimTarget: 0,
            panelOpacityTarget: _animationsEnabled ? 0.86 : 0,
            duration: TimeSpan.FromMilliseconds(_animationsEnabled ? 160 : 120),
            animatePosition: _animationsEnabled);
        _drawerTransition = storyboard;
        storyboard.Completed += (_, _) => CompleteDrawerClose(storyboard, generation);
        storyboard.Begin();
    }

    private void FocusDrawerLiveStatus()
    {
        _drawerInitialFocus = FooterStatusHost;
        _ = FooterStatusHost.Focus(FocusState.Programmatic);
    }

    private Storyboard CreateDrawerStoryboard(
        double drawerTarget,
        double scrimTarget,
        double panelOpacityTarget,
        TimeSpan duration,
        bool animatePosition)
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scrimAnimation = new DoubleAnimation
        {
            To = scrimTarget,
            Duration = duration,
            EasingFunction = easing,
        };
        Storyboard.SetTarget(scrimAnimation, DrawerScrim);
        Storyboard.SetTargetProperty(scrimAnimation, "Opacity");

        var panelOpacityAnimation = new DoubleAnimation
        {
            To = panelOpacityTarget,
            Duration = duration,
            EasingFunction = easing,
        };
        Storyboard.SetTarget(panelOpacityAnimation, DrawerPanel);
        Storyboard.SetTargetProperty(panelOpacityAnimation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(scrimAnimation);
        storyboard.Children.Add(panelOpacityAnimation);
        if (animatePosition)
        {
            var drawerAnimation = new DoubleAnimation
            {
                To = drawerTarget,
                Duration = duration,
                EasingFunction = easing,
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(drawerAnimation, DrawerTranslate);
            Storyboard.SetTargetProperty(drawerAnimation, "X");
            storyboard.Children.Add(drawerAnimation);
        }

        return storyboard;
    }

    private void CompleteDrawerOpen(Storyboard storyboard, long generation)
    {
        if (!ReferenceEquals(_drawerTransition, storyboard) ||
            generation != _drawerTransitionGeneration)
        {
            return;
        }

        _drawerTransition = null;
        storyboard.Stop();
        CompleteDrawerOpen(generation);
    }

    private void CompleteDrawerOpen(long generation)
    {
        if (!_drawerLifecycle.TryCompleteOpening(generation))
        {
            return;
        }

        _drawerTransitionGeneration = 0;
        _drawerTransitioning = false;
        _drawerClosing = false;
        DrawerTranslate.X = 0;
        DrawerScrim.Opacity = 1;
        DrawerPanel.Opacity = 1;
        DrawerPanel.IsHitTestVisible = true;
        DrawerScrim.IsHitTestVisible = true;
        EnsureDrawerFocusWithin();
        RaiseDrawerModalPhaseChanged();
    }

    private void EnsureDrawerFocusWithin()
    {
        if (_drawerInitialFocus is null)
        {
            return;
        }

        var focusedElement = FocusManager.GetFocusedElement(PageRoot.XamlRoot) as DependencyObject;
        if (DrawerModalFocusPolicy.ShouldMoveInside(
                _drawerLifecycle.Phase,
                IsDescendantOrSelf(focusedElement, DrawerPanel)))
        {
            _drawerInitialFocus.Focus(FocusState.Programmatic);
        }
    }

    private static bool IsDescendantOrSelf(DependencyObject? element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private void CompleteDrawerClose(Storyboard storyboard, long generation)
    {
        if (!ReferenceEquals(_drawerTransition, storyboard) ||
            generation != _drawerTransitionGeneration)
        {
            return;
        }

        _drawerTransition = null;
        storyboard.Stop();
        CompleteDrawerClose(generation);
    }

    private void CompleteDrawerClose(long generation)
    {
        if (!_drawerLifecycle.TryCompleteClosing(generation))
        {
            return;
        }

        _drawerTransitionGeneration = 0;
        _drawerTransitioning = false;
        _drawerClosing = false;
        DrawerLayer.Visibility = Visibility.Collapsed;
        DrawerTranslate.X = 0;
        DrawerScrim.Opacity = 0;
        DrawerPanel.Opacity = 1;
        DrawerPanel.IsHitTestVisible = true;
        DrawerScrim.IsHitTestVisible = true;
        _focusBeforeDrawer?.Focus(FocusState.Programmatic);
        _focusBeforeDrawer = null;
        _drawerInitialFocus = null;
        RaiseDrawerModalPhaseChanged();
    }

    private void CompleteActiveDrawerTransition()
    {
        if (!_drawerTransitioning)
        {
            return;
        }

        var generation = _drawerTransitionGeneration;
        var storyboard = _drawerTransition;
        _drawerTransition = null;
        storyboard?.Stop();
        if (_drawerClosing)
        {
            CompleteDrawerClose(generation);
            return;
        }

        CompleteDrawerOpen(generation);
    }

    private void NormalizeDrawerForUnload()
    {
        var storyboard = _drawerTransition;
        _drawerTransition = null;
        var phaseChanged = _drawerLifecycle.NormalizeCollapsed();
        _drawerTransitionGeneration = 0;
        storyboard?.Stop();

        _drawerTransitioning = false;
        _drawerClosing = false;
        DrawerLayer.Visibility = Visibility.Collapsed;
        DrawerTranslate.X = 0;
        DrawerScrim.Opacity = 0;
        DrawerPanel.Opacity = 1;
        DrawerPanel.IsHitTestVisible = true;
        DrawerScrim.IsHitTestVisible = true;
        _focusBeforeDrawer = null;
        _drawerInitialFocus = null;

        if (phaseChanged)
        {
            RaiseDrawerModalPhaseChanged();
        }
    }

    private void RaiseDrawerModalPhaseChanged() =>
        DrawerModalPhaseChanged?.Invoke(
            this,
            new DrawerModalPhaseChangedEventArgs(_drawerLifecycle.Phase));

    private void AnimateProjectionChange()
    {
        PlayReveal(SceneLayer, SceneTranslate, 260, 8);
        PlayReveal(GiantVersionText, GiantVersionTranslate, 260, 6);
    }

    private void RevealPreviewResults()
    {
        if (ResultCard.Visibility == Visibility.Visible)
        {
            PlayReveal(ResultCard, PreviewResultsTranslate, 160, 6);
        }
    }

    private void PlayReveal(
        FrameworkElement element,
        TranslateTransform translate,
        int normalDurationMilliseconds,
        double normalOffset)
    {
        StopReveal(element, translate);
        if (!_pageLoaded || _highContrast || element.Visibility != Visibility.Visible)
        {
            return;
        }

        var duration = TimeSpan.FromMilliseconds(_animationsEnabled ? normalDurationMilliseconds : 90);
        var offset = _animationsEnabled ? normalOffset : 0;
        element.Opacity = 0;
        translate.Y = offset;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opacityAnimation = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = duration,
            EasingFunction = easing,
        };
        Storyboard.SetTarget(opacityAnimation, element);
        Storyboard.SetTargetProperty(opacityAnimation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(opacityAnimation);
        if (offset != 0)
        {
            var translateAnimation = new DoubleAnimation
            {
                From = offset,
                To = 0,
                Duration = duration,
                EasingFunction = easing,
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(translateAnimation, translate);
            Storyboard.SetTargetProperty(translateAnimation, "Y");
            storyboard.Children.Add(translateAnimation);
        }

        _activeRevealTransitions[element] = storyboard;
        storyboard.Completed += (_, _) => CompleteReveal(element, translate, storyboard);
        storyboard.Begin();
    }

    private void StopReveal(FrameworkElement element, TranslateTransform translate)
    {
        if (_activeRevealTransitions.Remove(element, out var active))
        {
            active.Stop();
        }

        element.Opacity = 1;
        translate.Y = 0;
    }

    private void CompleteReveal(
        FrameworkElement element,
        TranslateTransform translate,
        Storyboard storyboard)
    {
        if (!_activeRevealTransitions.TryGetValue(element, out var active) ||
            !ReferenceEquals(active, storyboard))
        {
            return;
        }

        storyboard.Stop();
        _activeRevealTransitions.Remove(element);
        element.Opacity = 1;
        translate.Y = 0;
    }

    private void CompleteRevealTransitions()
    {
        foreach (var pair in _activeRevealTransitions.ToArray())
        {
            pair.Value.Stop();
            pair.Key.Opacity = 1;
            if (pair.Key.RenderTransform is TranslateTransform translate)
            {
                translate.Y = 0;
            }
        }

        _activeRevealTransitions.Clear();
    }

    private static string DecorativeVersion(string version)
    {
        var trimmed = version.Trim();
        if (trimmed.StartsWith('r') || trimmed.StartsWith('R'))
        {
            trimmed = trimmed[1..];
        }

        return trimmed.Length <= 4 ? trimmed : trimmed[^4..];
    }

    private static string CompactPackName(string packName)
    {
        return packName.Equals("All the Mods 10", StringComparison.OrdinalIgnoreCase)
            ? "ATM10"
            : packName;
    }

}
