// Window-level theme, backdrop, and local theme-preference coordination.
using BlockFerry.App.WinUI.Services;
using BlockFerry.App.WinUI.Localization;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace BlockFerry.App.WinUI;

public sealed partial class MainWindow : Window, IDisposable
{
    private const string LightThemeName = "light";
    private const string DarkThemeName = "dark";
    private const double BackgroundGlowVisibleOpacity = 0.41;
    private const double ForegroundGlowVisibleOpacity = 0.032;

    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private readonly BlockFerryCompositionRoot _composition;
    private readonly IThemePreferenceStore _themePreferenceStore;
    private readonly ILanguagePreferenceStore _languagePreferenceStore;
    private readonly PointerGlowModalCoordinator _pointerGlowModalCoordinator = new();
    private readonly GitHubReleaseUpdateChecker _updateChecker = new();
    private readonly CancellationTokenSource _updateCheckCancellation = new();
    private readonly DispatcherTimer _glowFollowTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(16),
    };
    private Storyboard? _themeTransitionStoryboard;
    private Storyboard? _pointerGlowStoryboard;
    private MainPage? _drawerModalPhaseSource;
    private ImageSource? _outgoingThemeBackground;
    private bool _themeTransitionPending;
    private bool _animationsEnabled = true;
    private bool _isHighContrast;
    private bool _glowPositionInitialized;
    private bool _glowFollowTimerRunning;
    private double _glowCurrentX;
    private double _glowCurrentY;
    private double _glowTargetX;
    private double _glowTargetY;
    private long _lastGlowFrameTimestamp;
    private long _pointerGlowStoryboardGeneration;
    private UpdateCheckResult? _availableUpdate;
    private bool _disposed;

    public MainWindow()
        : this(BlockFerryCompositionRoot.CreateProduction())
    {
    }

    internal MainWindow(BlockFerryCompositionRoot composition)
    {
        _composition = composition ?? throw new ArgumentNullException(nameof(composition));
        _themePreferenceStore = composition.ThemePreferences;
        _languagePreferenceStore = composition.LanguagePreferences;
        ApplySavedLanguagePreference();
        InitializeComponent();
        UpdateLocalizedChrome();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        ConfigureCaptionButtons();
        AppWindow.Changed += AppWindow_Changed;
        AppWindow.Closing += AppWindow_Closing;
        Activated += MainWindow_Activated;
        _glowFollowTimer.Tick += GlowFollowTimer_Tick;
        Closed += MainWindow_Closed;

        ConfigureWindowSizing();
        AppWindow.Resize(new SizeInt32(1080, 720));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        ApplySavedThemePreference();
        WindowRoot.ActualThemeChanged += WindowRoot_ActualThemeChanged;
        WindowRoot.Loaded += WindowRoot_Loaded;

        var folderPicker = new FolderPickerService(AppWindow.Id);
        var fileSavePicker = new FileSavePickerService(AppWindow.Id);
        _composition.Workflow.AttachFileSavePicker(fileSavePicker);
        RootFrame.Content = new MainPage(
            _composition.Workflow,
            folderPicker,
            fileSavePicker);
        if (RootFrame.Content is MainPage page)
        {
            page.ApplyLanguage();
        }
        SubscribeToDrawerModalPhases();
    }

    private void SubscribeToDrawerModalPhases()
    {
        if (RootFrame.Content is not MainPage page)
        {
            return;
        }

        _drawerModalPhaseSource = page;
        page.DrawerModalPhaseChanged += MainPage_DrawerModalPhaseChanged;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _updateCheckCancellation.Cancel();
        _updateCheckCancellation.Dispose();
        _updateChecker.Dispose();
        StopGlowFollow();
        InvalidatePointerGlowStoryboard();
        _glowFollowTimer.Tick -= GlowFollowTimer_Tick;
        AppWindow.Changed -= AppWindow_Changed;
        AppWindow.Closing -= AppWindow_Closing;
        Activated -= MainWindow_Activated;

        if (_drawerModalPhaseSource is not null)
        {
            _drawerModalPhaseSource.DrawerModalPhaseChanged -= MainPage_DrawerModalPhaseChanged;
            _drawerModalPhaseSource.Dispose();
            _drawerModalPhaseSource = null;
        }

        _composition.Dispose();
    }

    private void MainPage_DrawerModalPhaseChanged(
        object? sender,
        DrawerModalPhaseChangedEventArgs args)
    {
        var decision = _pointerGlowModalCoordinator.OnDrawerPhaseChanged(args.Phase);
        ApplyPointerGlowDecision(
            decision,
            pointerArgs: null,
            animateHide: args.Phase == DrawerModalPhase.Opening);
    }

    public void ApplyViewState(MigrationViewState state)
    {
        if (RootFrame.Content is MainPage page)
        {
            page.ApplyViewState(state);
        }
    }

    private async void WindowRoot_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateCaptionInsets();
        UpdateThemePresentation();
        UpdateLocalizedChrome();
        ApplyAccessibilityPreferences();
        try
        {
            await CheckForUpdatesAsync();
        }
        catch (OperationCanceledException) when (_updateCheckCancellation.IsCancellationRequested)
        {
            // Closing the window cancels the optional, read-only update request.
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        var result = await _updateChecker.CheckOnceAsync(_updateCheckCancellation.Token);
        if (result.Status != UpdateCheckStatus.UpdateAvailable ||
            result.ReleasePage is null ||
            string.IsNullOrWhiteSpace(result.LatestVersion))
        {
            return;
        }

        _availableUpdate = result;
        UpdateVersionText.Text = UiText.Current == UiLanguage.English
            ? $"New {result.LatestVersion}"
            : $"新版本 {result.LatestVersion}";
        ToolTipService.SetToolTip(
            UpdateButton,
            UiText.Current == UiLanguage.English
                ? $"View BlockFerry {result.LatestVersion} on GitHub"
                : $"在 GitHub 查看 BlockFerry {result.LatestVersion}");
        UpdateButton.Visibility = Visibility.Visible;
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate?.ReleasePage is not Uri releasePage)
        {
            return;
        }

        try
        {
            _ = await Windows.System.Launcher.LaunchUriAsync(releasePage);
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            // Failure to open the browser must not affect migration work.
        }
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        try
        {
            await _composition.Workflow.RefreshUndoEligibilityAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Window shutdown cancels the optional read-only refresh.
        }
    }

    private void WindowRoot_ActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateThemePresentation();
        ApplyAccessibilityPreferences();

        if (_themeTransitionPending)
        {
            BeginThemeTransition();
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        UpdateCaptionInsets();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_composition.Workflow.State.IsMutationInProgress)
        {
            return;
        }

        args.Cancel = true;
        if (RootFrame.Content is MainPage page)
        {
            page.SetSyncPresentation(
                SyncPresentationState.Running,
                2,
                "安全事务仍在进行；完成提交或回滚后即可关闭窗口。");
        }
    }

    private void ConfigureCaptionButtons()
    {
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        UpdateCaptionInsets();
    }

    private void ConfigureWindowSizing()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // Below this width the brand, compact rail, and native caption group
            // no longer have enough independent hit-test space.
            presenter.PreferredMinimumWidth = 420;
            presenter.PreferredMinimumHeight = 480;
        }
    }

    private void UpdateCaptionInsets()
    {
        // RightInset is the live width reserved by the native caption buttons.
        // The theme button ends exactly where the native 46-DIP caption rhythm begins.
        CaptionRightPaddingColumn.Width = new GridLength(
            Math.Max(0, AppWindow.TitleBar.RightInset));
    }

    private void ApplySavedThemePreference()
    {
        try
        {
            WindowRoot.RequestedTheme = _themePreferenceStore.Read() switch
            {
                LightThemeName => ElementTheme.Light,
                DarkThemeName => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
        catch (IOException)
        {
            WindowRoot.RequestedTheme = ElementTheme.Default;
        }
        catch (UnauthorizedAccessException)
        {
            WindowRoot.RequestedTheme = ElementTheme.Default;
        }
    }

    private void ApplySavedLanguagePreference()
    {
        try
        {
            UiText.SetLanguage(_languagePreferenceStore.Read() == "en-US"
                ? UiLanguage.English
                : UiLanguage.ChineseSimplified);
        }
        catch (IOException)
        {
            UiText.SetLanguage(UiLanguage.ChineseSimplified);
        }
        catch (UnauthorizedAccessException)
        {
            UiText.SetLanguage(UiLanguage.ChineseSimplified);
        }
    }

    private void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        UiText.SetLanguage(UiText.Current == UiLanguage.English
            ? UiLanguage.ChineseSimplified
            : UiLanguage.English);
        try
        {
            _ = _languagePreferenceStore.Write(UiText.LanguageTag);
        }
        catch (IOException)
        {
            // The language still changes for this session if persistence is unavailable.
        }
        catch (UnauthorizedAccessException)
        {
            // The language still changes for this session if persistence is unavailable.
        }

        UpdateLocalizedChrome();
        if (RootFrame.Content is MainPage page)
        {
            page.ApplyLanguage();
        }
    }

    private void UpdateLocalizedChrome()
    {
        var english = UiText.Current == UiLanguage.English;
        Title = english ? "BlockFerry" : "BlockFerry · 方块渡口";
        LanguageButtonText.Text = english ? "中" : "EN";
        ToolTipService.SetToolTip(LanguageButton, english ? "切换到中文" : "Switch to English");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            LanguageButton,
            english ? "切换到中文" : "Switch interface language to English");
        UiText.ApplyToVisualTree(TopBar);
        UpdateThemePresentation();
        if (_availableUpdate is { LatestVersion: { } version })
        {
            UpdateVersionText.Text = english ? $"New {version}" : $"新版本 {version}";
            ToolTipService.SetToolTip(
                UpdateButton,
                english ? $"View BlockFerry {version} on GitHub" : $"在 GitHub 查看 BlockFerry {version}");
        }
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isHighContrast)
        {
            _outgoingThemeBackground = SceneBackgroundImage.Source;
            _themeTransitionPending = _outgoingThemeBackground is not null;
        }

        WindowRoot.RequestedTheme = WindowRoot.ActualTheme == ElementTheme.Dark
            ? ElementTheme.Light
            : ElementTheme.Dark;

        PersistThemePreference(WindowRoot.RequestedTheme);
    }

    private void PersistThemePreference(ElementTheme theme)
    {
        try
        {
            _ = _themePreferenceStore.Write(
                theme == ElementTheme.Light ? LightThemeName : DarkThemeName);
        }
        catch (IOException)
        {
            // The theme still changes for this session if local persistence is unavailable.
        }
        catch (UnauthorizedAccessException)
        {
            // The theme still changes for this session if local persistence is unavailable.
        }
    }

    private void UpdateThemePresentation()
    {
        var isDark = WindowRoot.ActualTheme == ElementTheme.Dark;
        var imageName = isDark ? "blockferry-ambient.jpg" : "blockferry-ambient-light.jpg";
        SceneBackgroundImage.Source = new BitmapImage(new Uri($"ms-appx:///Assets/{imageName}"));
        ThemeGlyph.Glyph = isDark ? "\uE706" : "\uE708";

        var nextThemeName = isDark ? "浅色" : "深色";
        var themeTooltip = UiText.Current == UiLanguage.English
            ? $"Switch to {(isDark ? "light" : "dark")} theme"
            : $"切换到{nextThemeName}主题";
        ToolTipService.SetToolTip(ThemeButton, themeTooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ThemeButton, themeTooltip);

        var foreground = isDark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonForegroundColor = foreground;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = foreground;
    }

    private void ApplyAccessibilityPreferences()
    {
        var highContrast = _accessibilitySettings.HighContrast;
        var advancedEffects = _uiSettings.AdvancedEffectsEnabled && !highContrast;
        var animationsEnabled = _uiSettings.AnimationsEnabled && !highContrast;

        _isHighContrast = highContrast;
        _animationsEnabled = animationsEnabled;

        // A disabled Windows transparency setting must only turn off system/acrylic
        // effects. The authored scene image remains part of the app's visual design.
        SceneBackgroundImage.Opacity = highContrast ? 0 : 1;
        TopBlurLayer.Opacity = advancedEffects ? 0.56 : 0;
        SystemBackdrop = advancedEffects ? new MicaBackdrop() : null;

        if (highContrast)
        {
            _themeTransitionPending = false;
            _outgoingThemeBackground = null;
            _themeTransitionStoryboard?.Stop();
            PreviousSceneBackgroundImage.Source = null;
            PreviousSceneBackgroundImage.Opacity = 0;
            ContentLayer.Opacity = 1;
            StopGlowFollow();
            _glowPositionInitialized = false;
            SetPointerGlowVisibility(false, animate: false);
        }

        if (RootFrame.Content is MainPage page)
        {
            page.ConfigureAccessibility(animationsEnabled, advancedEffects, highContrast);
        }
    }

    private void BeginThemeTransition()
    {
        var outgoingBackground = _outgoingThemeBackground;
        _themeTransitionPending = false;
        _outgoingThemeBackground = null;

        if (_isHighContrast || outgoingBackground is null)
        {
            PreviousSceneBackgroundImage.Source = null;
            PreviousSceneBackgroundImage.Opacity = 0;
            ContentLayer.Opacity = 1;
            return;
        }

        _themeTransitionStoryboard?.Stop();
        PreviousSceneBackgroundImage.Source = outgoingBackground;
        PreviousSceneBackgroundImage.Opacity = 1;
        ContentLayer.Opacity = _animationsEnabled ? 0.88 : 0.94;

        // Reduced-motion mode keeps a short, stationary dissolve. High contrast
        // bypasses this method entirely.
        var duration = TimeSpan.FromMilliseconds(_animationsEnabled ? 260 : 110);
        var storyboard = new Storyboard();
        storyboard.Children.Add(CreateOpacityAnimation(
            PreviousSceneBackgroundImage,
            PreviousSceneBackgroundImage.Opacity,
            0,
            duration));
        storyboard.Children.Add(CreateOpacityAnimation(
            ContentLayer,
            ContentLayer.Opacity,
            1,
            duration));
        storyboard.Completed += (_, _) =>
        {
            PreviousSceneBackgroundImage.Source = null;
            PreviousSceneBackgroundImage.Opacity = 0;
            ContentLayer.Opacity = 1;
        };

        _themeTransitionStoryboard = storyboard;
        storyboard.Begin();
    }

    private void WindowRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ApplyPointerGlowDecision(
            _pointerGlowModalCoordinator.OnPointerEntered(),
            e,
            animateHide: _pointerGlowModalCoordinator.AllowsGlow);
    }

    private void WindowRoot_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ApplyPointerGlowDecision(
            _pointerGlowModalCoordinator.OnPointerMoved(),
            e,
            animateHide: _pointerGlowModalCoordinator.AllowsGlow);
    }

    private void WindowRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ApplyPointerGlowDecision(
            _pointerGlowModalCoordinator.OnPointerExited(),
            pointerArgs: null,
            animateHide: _pointerGlowModalCoordinator.AllowsGlow);
    }

    private void ApplyPointerGlowDecision(
        PointerGlowDecision decision,
        PointerRoutedEventArgs? pointerArgs,
        bool animateHide)
    {
        if (decision.RecordTarget && pointerArgs is not null)
        {
            RecordPointerGlowTarget(pointerArgs);
        }

        if (decision.StopFollow)
        {
            StopGlowFollow();
        }

        if (!_isHighContrast && decision.InitializeAtTarget)
        {
            InitializePointerGlowAtTarget();
        }
        else if (!_isHighContrast && decision.StartFollow)
        {
            StartGlowFollow();
        }

        if (decision.HideGlow)
        {
            SetPointerGlowVisibility(false, animateHide);
        }
        else if (decision.RevealGlow)
        {
            SetPointerGlowVisibility(true, animate: true);
        }
    }

    private void RecordPointerGlowTarget(PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(WindowRoot).Position;
        _glowTargetX = position.X;
        _glowTargetY = position.Y;
    }

    private void InitializePointerGlowAtTarget()
    {
        if (_isHighContrast || !_pointerGlowModalCoordinator.AllowsGlow)
        {
            return;
        }

        _glowCurrentX = _glowTargetX;
        _glowCurrentY = _glowTargetY;
        _glowPositionInitialized = true;
        ApplyPointerGlowPosition();
    }

    private void StartGlowFollow()
    {
        if (_isHighContrast || !_pointerGlowModalCoordinator.AllowsGlow || _glowFollowTimerRunning)
        {
            return;
        }

        if (!_glowPositionInitialized)
        {
            InitializePointerGlowAtTarget();
            return;
        }

        _lastGlowFrameTimestamp = Stopwatch.GetTimestamp();
        _glowFollowTimer.Start();
        _glowFollowTimerRunning = true;
    }

    private void StopGlowFollow()
    {
        if (!_glowFollowTimerRunning)
        {
            return;
        }

        _glowFollowTimer.Stop();
        _glowFollowTimerRunning = false;
        _lastGlowFrameTimestamp = 0;
    }

    private void GlowFollowTimer_Tick(object? sender, object e)
    {
        if (_isHighContrast || !_pointerGlowModalCoordinator.AllowsGlow)
        {
            StopGlowFollow();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds = Math.Clamp(
            (now - _lastGlowFrameTimestamp) / (double)Stopwatch.Frequency,
            1.0 / 240,
            0.05);
        _lastGlowFrameTimestamp = now;

        // Exponential damping gives the diffuse light a little visual mass without
        // overshoot. Reduced-motion mode settles more quickly.
        var responsiveness = _animationsEnabled ? 22.0 : 40.0;
        var blend = 1 - Math.Exp(-responsiveness * elapsedSeconds);
        _glowCurrentX += (_glowTargetX - _glowCurrentX) * blend;
        _glowCurrentY += (_glowTargetY - _glowCurrentY) * blend;

        var remainingX = Math.Abs(_glowTargetX - _glowCurrentX);
        var remainingY = Math.Abs(_glowTargetY - _glowCurrentY);
        if (remainingX < 0.2 && remainingY < 0.2)
        {
            _glowCurrentX = _glowTargetX;
            _glowCurrentY = _glowTargetY;
            StopGlowFollow();
        }

        ApplyPointerGlowPosition();
    }

    private void ApplyPointerGlowPosition()
    {
        if (_isHighContrast || !_pointerGlowModalCoordinator.AllowsGlow)
        {
            return;
        }

        BackgroundGlowTransform.X = _glowCurrentX - (BackgroundPointerGlow.Width / 2);
        BackgroundGlowTransform.Y = _glowCurrentY - (BackgroundPointerGlow.Height / 2);
        ForegroundGlowTransform.X = _glowCurrentX - (ForegroundPointerGlow.Width / 2);
        ForegroundGlowTransform.Y = _glowCurrentY - (ForegroundPointerGlow.Height / 2);
    }

    private void SetPointerGlowVisibility(bool visible, bool animate)
    {
        if (_isHighContrast || (visible && !_pointerGlowModalCoordinator.AllowsGlow))
        {
            visible = false;
            animate = false;
        }

        var backgroundOpacity = visible ? BackgroundGlowVisibleOpacity : 0;
        var foregroundOpacity = visible ? ForegroundGlowVisibleOpacity : 0;

        var generation = InvalidatePointerGlowStoryboard();

        if (!animate)
        {
            BackgroundPointerGlow.Opacity = backgroundOpacity;
            ForegroundPointerGlow.Opacity = foregroundOpacity;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(_animationsEnabled ? 170 : 90);
        var storyboard = new Storyboard
        {
            FillBehavior = FillBehavior.Stop,
        };
        storyboard.Children.Add(CreateOpacityAnimation(
            BackgroundPointerGlow,
            BackgroundPointerGlow.Opacity,
            backgroundOpacity,
            duration));
        storyboard.Children.Add(CreateOpacityAnimation(
            ForegroundPointerGlow,
            ForegroundPointerGlow.Opacity,
            foregroundOpacity,
            duration));
        storyboard.Completed += (_, _) =>
        {
            if (generation != _pointerGlowStoryboardGeneration ||
                !ReferenceEquals(_pointerGlowStoryboard, storyboard))
            {
                return;
            }

            _pointerGlowStoryboard = null;
            BackgroundPointerGlow.Opacity = backgroundOpacity;
            ForegroundPointerGlow.Opacity = foregroundOpacity;
        };

        _pointerGlowStoryboard = storyboard;
        storyboard.Begin();
    }

    private long InvalidatePointerGlowStoryboard()
    {
        var generation = ++_pointerGlowStoryboardGeneration;
        var storyboard = _pointerGlowStoryboard;
        if (storyboard is null)
        {
            return generation;
        }

        var backgroundOpacity = BackgroundPointerGlow.Opacity;
        var foregroundOpacity = ForegroundPointerGlow.Opacity;
        _pointerGlowStoryboard = null;
        storyboard.Stop();
        BackgroundPointerGlow.Opacity = backgroundOpacity;
        ForegroundPointerGlow.Opacity = foregroundOpacity;
        return generation;
    }

    private static DoubleAnimation CreateOpacityAnimation(
        DependencyObject target,
        double from,
        double to,
        TimeSpan duration)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(duration),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        return animation;
    }
}
