// Window-level theme, backdrop, and local theme-preference coordination.
using BlockFerry.App.WinUI.Services;
using BlockFerry.App.WinUI.Localization;
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
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
    private const double GlowCoreVisibleOpacity = 0.30;
    private const double GlowTrailVisibleOpacity = 0.12;
    private const int BackgroundSourceWidth = 3172;
    private const int BackgroundReloadQuantum = 128;
    private const double BackgroundAspectRatio = 3172d / 1984d;
    private const int BackgroundResizeDebounceMilliseconds = 140;
    private const double GlowCoreAngularFrequency = 19.0;
    private const double GlowCoreDampingRatio = 0.92;
    private const double GlowTrailAngularFrequency = 12.5;
    private const double GlowTrailDampingRatio = 0.86;

    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private readonly BlockFerryCompositionRoot _composition;
    private readonly IThemePreferenceStore _themePreferenceStore;
    private readonly ILanguagePreferenceStore _languagePreferenceStore;
    private readonly PointerGlowModalCoordinator _pointerGlowModalCoordinator = new();
    private readonly GitHubReleaseUpdateChecker _updateChecker = new();
    private readonly CancellationTokenSource _updateCheckCancellation = new();
    private MicaBackdrop? _micaBackdrop;
    private Visual? _backgroundGlowVisual;
    private Visual? _trailGlowVisual;
    private Storyboard? _themeTransitionStoryboard;
    private Storyboard? _pointerGlowStoryboard;
    private DispatcherQueueTimer? _backgroundResizeTimer;
    private MainPage? _drawerModalPhaseSource;
    private ImageSource? _outgoingThemeBackground;
    private ElementTheme _outgoingBackgroundTheme = ElementTheme.Default;
    private int _outgoingBackgroundRenderWidthKey;
    private bool _themeTransitionPending;
    private bool _themeRefreshQueued;
    private bool _themeToggleInProgress;
    private bool _backgroundResizePending;
    private bool _systemBackdropAttached;
    private bool _systemBackdropUnavailable;
    private bool _animationsEnabled = true;
    private bool _isHighContrast;
    private bool _glowPositionInitialized;
    private bool _glowRenderingSubscribed;
    private double _glowCurrentX;
    private double _glowCurrentY;
    private double _glowVelocityX;
    private double _glowVelocityY;
    private double _trailGlowCurrentX;
    private double _trailGlowCurrentY;
    private double _trailGlowVelocityX;
    private double _trailGlowVelocityY;
    private double _glowTargetX;
    private double _glowTargetY;
    private long _lastGlowFrameTimestamp;
    private long _themeTransitionGeneration;
    private long _pointerGlowStoryboardGeneration;
    private long _backgroundLoadGeneration;
    private ElementTheme _backgroundTheme = ElementTheme.Default;
    private int _backgroundRenderWidthKey;
    private BitmapImage? _pendingBackground;
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
        if (_backgroundResizeTimer is not null)
        {
            _backgroundResizeTimer.Stop();
            _backgroundResizeTimer.Tick -= BackgroundResizeTimer_Tick;
            _backgroundResizeTimer = null;
        }

        StopGlowFollow();
        InvalidatePointerGlowStoryboard();
        InvalidateThemeTransitionStoryboard();
        WindowRoot.ActualThemeChanged -= WindowRoot_ActualThemeChanged;
        WindowRoot.Loaded -= WindowRoot_Loaded;
        InvalidateBackgroundLoad();
        AppWindow.Changed -= AppWindow_Changed;
        AppWindow.Closing -= AppWindow_Closing;
        Activated -= MainWindow_Activated;
        Closed -= MainWindow_Closed;

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
        if (_themeRefreshQueued || _disposed)
        {
            return;
        }

        // Theme resources are still being swapped while ActualThemeChanged is raised.
        // Defer authored-image and caption updates to avoid re-entering the XAML tree.
        _themeRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(ApplyQueuedThemeRefresh))
        {
            _themeRefreshQueued = false;
            ApplyQueuedThemeRefresh();
        }
    }

    private void ApplyQueuedThemeRefresh()
    {
        _themeRefreshQueued = false;
        if (_disposed)
        {
            return;
        }

        try
        {
            UpdateThemePresentation();
            if (_themeToggleInProgress && !_themeTransitionPending)
            {
                CompleteThemeToggle();
            }
        }
        catch (Exception)
        {
            // Theme presentation is optional chrome. Native ThemeResource values have
            // already switched, so a failed image/caption refresh must never close the app.
            CancelThemeTransition(restoreOutgoingBackground: true);
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        UpdateCaptionInsets();
        if (args.DidSizeChange)
        {
            try
            {
                QueueBackgroundResizeRefresh();
            }
            catch (Exception)
            {
                // Resizing remains usable if an optional authored image cannot decode.
                _backgroundTheme = ElementTheme.Default;
                _backgroundRenderWidthKey = 0;
            }
        }
    }

    private void QueueBackgroundResizeRefresh()
    {
        if (_disposed)
        {
            return;
        }

        _backgroundResizePending = true;
        if (_themeToggleInProgress)
        {
            return;
        }

        if (_backgroundResizeTimer is null)
        {
            _backgroundResizeTimer = DispatcherQueue.CreateTimer();
            _backgroundResizeTimer.Interval = TimeSpan.FromMilliseconds(
                BackgroundResizeDebounceMilliseconds);
            _backgroundResizeTimer.IsRepeating = false;
            _backgroundResizeTimer.Tick += BackgroundResizeTimer_Tick;
        }

        _backgroundResizeTimer.Stop();
        _backgroundResizeTimer.Start();
    }

    private void BackgroundResizeTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_disposed || _themeToggleInProgress)
        {
            return;
        }

        _backgroundResizePending = false;
        try
        {
            EnsureThemeBackground(force: false);
        }
        catch (Exception)
        {
            // A resize only adjusts optional image decode density. Keep the
            // already rendered frame and retry after the next stable size.
            _backgroundTheme = ElementTheme.Default;
            _backgroundRenderWidthKey = 0;
        }
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
        catch (Exception)
        {
            // A local preference is non-critical; use the Windows theme when storage
            // is unavailable or another portable instance owns the preference file.
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
        catch (Exception)
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
        catch (Exception)
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
        if (_isHighContrast ||
            _themeToggleInProgress ||
            (_pendingBackground is not null && !_themeTransitionPending))
        {
            return;
        }

        _themeTransitionPending = PrepareBackgroundTransitionCover();
        _themeToggleInProgress = true;
        ThemeButton.IsEnabled = false;

        try
        {
            WindowRoot.RequestedTheme = WindowRoot.ActualTheme == ElementTheme.Dark
                ? ElementTheme.Light
                : ElementTheme.Dark;
            PersistThemePreference(WindowRoot.RequestedTheme);
        }
        catch (Exception)
        {
            CancelThemeTransition(restoreOutgoingBackground: true);
        }
    }

    private void PersistThemePreference(ElementTheme theme)
    {
        try
        {
            _ = _themePreferenceStore.Write(
                theme == ElementTheme.Light ? LightThemeName : DarkThemeName);
        }
        catch (Exception)
        {
            // The theme still changes for this session if local persistence is unavailable.
        }
    }

    private void UpdateThemePresentation()
    {
        var isDark = WindowRoot.ActualTheme == ElementTheme.Dark;
        EnsureThemeBackground(force: false);
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

    private void EnsureThemeBackground(bool force)
    {
        if (_isHighContrast || _disposed)
        {
            return;
        }

        var theme = WindowRoot.ActualTheme == ElementTheme.Dark
            ? ElementTheme.Dark
            : ElementTheme.Light;
        var renderWidthKey = CalculateBackgroundRenderWidthKey();
        if (!force && theme == _backgroundTheme && renderWidthKey == _backgroundRenderWidthKey)
        {
            return;
        }

        if (!_themeTransitionPending &&
            _pendingBackground is null &&
            SceneBackgroundImage.Source is not null)
        {
            _themeTransitionPending = PrepareBackgroundTransitionCover();
        }

        var imageName = theme == ElementTheme.Dark
            ? "blockferry-ambient.png"
            : "blockferry-ambient-light.png";
        // Keep the explicit decode size unset. Because the BitmapImage is connected
        // to the live Image before UriSource is assigned, WinUI can right-size it for
        // the real client area instead of bilinearly shrinking an oversized decode.
        var image = new BitmapImage();
        var generation = ++_backgroundLoadGeneration;
        image.ImageOpened += (_, _) => BackgroundImageOpened(image, generation);
        image.ImageFailed += (_, _) => BackgroundImageFailed(image, generation);

        _backgroundTheme = theme;
        _backgroundRenderWidthKey = renderWidthKey;
        _pendingBackground = image;
        if (!_themeTransitionPending && SceneBackgroundImage.Source is null)
        {
            ThemeButton.IsEnabled = false;
        }

        SceneBackgroundImage.Source = image;
        image.UriSource = new Uri($"ms-appx:///Assets/{imageName}");
    }

    private int CalculateBackgroundRenderWidthKey()
    {
        var clientSize = AppWindow.ClientSize;
        var requiredWidth = Math.Max(
            clientSize.Width,
            (int)Math.Ceiling(clientSize.Height * BackgroundAspectRatio));
        var roundedWidth = (int)Math.Ceiling(
            requiredWidth / (double)BackgroundReloadQuantum) * BackgroundReloadQuantum;
        return Math.Clamp(roundedWidth, BackgroundReloadQuantum, BackgroundSourceWidth);
    }

    private void BackgroundImageOpened(BitmapImage image, long generation)
    {
        if (_disposed ||
            generation != _backgroundLoadGeneration ||
            !ReferenceEquals(_pendingBackground, image) ||
            !ReferenceEquals(SceneBackgroundImage.Source, image))
        {
            return;
        }

        _pendingBackground = null;
        if (_themeTransitionPending)
        {
            BeginThemeTransition();
        }
        else if (_themeToggleInProgress)
        {
            CompleteThemeToggle();
        }
        else
        {
            ThemeButton.IsEnabled = true;
        }
    }

    private void BackgroundImageFailed(BitmapImage image, long generation)
    {
        if (_disposed ||
            generation != _backgroundLoadGeneration ||
            !ReferenceEquals(_pendingBackground, image))
        {
            return;
        }

        _pendingBackground = null;
        _backgroundTheme = ElementTheme.Default;
        _backgroundRenderWidthKey = 0;
        CancelThemeTransition(restoreOutgoingBackground: true);
    }

    private bool PrepareBackgroundTransitionCover()
    {
        if (_themeTransitionPending &&
            _outgoingThemeBackground is not null &&
            PreviousSceneBackgroundImage.Source is not null)
        {
            ThemeTransitionCover.Opacity = 1;
            return true;
        }

        if (_isHighContrast || SceneBackgroundImage.Source is not ImageSource outgoingBackground)
        {
            return false;
        }

        InvalidateThemeTransitionStoryboard();
        _outgoingThemeBackground = outgoingBackground;
        _outgoingBackgroundTheme = _backgroundTheme;
        _outgoingBackgroundRenderWidthKey = _backgroundRenderWidthKey;
        PreviousSceneBackgroundImage.Source = outgoingBackground;
        PreviousSceneVeil.Fill = SceneVeil.Fill;
        ThemeTransitionCover.Opacity = 1;
        return true;
    }

    private void InvalidateBackgroundLoad()
    {
        ++_backgroundLoadGeneration;
        _pendingBackground = null;
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
        UpdateSystemBackdrop(advancedEffects);

        if (highContrast)
        {
            CancelThemeTransition(restoreOutgoingBackground: false);
            PreviousSceneBackgroundImage.Source = null;
            PreviousSceneVeil.Fill = null;
            ThemeTransitionCover.Opacity = 0;
            StopGlowFollow();
            _glowPositionInitialized = false;
            SetPointerGlowVisibility(false, animate: false);
        }

        if (RootFrame.Content is MainPage page)
        {
            page.ConfigureAccessibility(animationsEnabled, advancedEffects, highContrast);
        }
    }

    private void UpdateSystemBackdrop(bool advancedEffects)
    {
        if (_systemBackdropUnavailable || advancedEffects == _systemBackdropAttached)
        {
            return;
        }

        try
        {
            if (advancedEffects)
            {
                _micaBackdrop ??= new MicaBackdrop();
                SystemBackdrop = _micaBackdrop;
                _systemBackdropAttached = true;
            }
            else
            {
                SystemBackdrop = null;
                _systemBackdropAttached = false;
            }
        }
        catch (Exception)
        {
            // Mica is optional and can fail on older Windows builds, RDP sessions,
            // virtual GPUs, or disabled composition. The authored scene is the fallback.
            _systemBackdropUnavailable = true;
            _systemBackdropAttached = false;
            try
            {
                SystemBackdrop = null;
            }
            catch (Exception)
            {
                // Even detaching an unavailable compositor is best-effort.
            }
        }
    }

    private void BeginThemeTransition()
    {
        var outgoingBackground = _outgoingThemeBackground;
        _themeTransitionPending = false;
        _outgoingThemeBackground = null;
        _outgoingBackgroundTheme = ElementTheme.Default;
        _outgoingBackgroundRenderWidthKey = 0;

        if (_isHighContrast || outgoingBackground is null)
        {
            PreviousSceneBackgroundImage.Source = null;
            PreviousSceneVeil.Fill = null;
            ThemeTransitionCover.Opacity = 0;
            CompleteThemeToggle();
            return;
        }

        var generation = InvalidateThemeTransitionStoryboard();

        // Reduced-motion mode keeps a short, stationary dissolve. High contrast
        // bypasses this method entirely.
        var duration = TimeSpan.FromMilliseconds(_animationsEnabled ? 320 : 110);
        var storyboard = new Storyboard();
        storyboard.Children.Add(CreateOpacityAnimation(
            ThemeTransitionCover,
            ThemeTransitionCover.Opacity,
            0,
            duration));
        storyboard.Completed += (_, _) =>
        {
            if (generation != _themeTransitionGeneration ||
                !ReferenceEquals(_themeTransitionStoryboard, storyboard))
            {
                return;
            }

            _themeTransitionStoryboard = null;
            PreviousSceneBackgroundImage.Source = null;
            PreviousSceneVeil.Fill = null;
            ThemeTransitionCover.Opacity = 0;
            CompleteThemeToggle();
        };

        _themeTransitionStoryboard = storyboard;
        storyboard.Begin();
    }

    private long InvalidateThemeTransitionStoryboard()
    {
        var generation = ++_themeTransitionGeneration;
        var storyboard = _themeTransitionStoryboard;
        _themeTransitionStoryboard = null;
        storyboard?.Stop();
        return generation;
    }

    private void CancelThemeTransition(bool restoreOutgoingBackground)
    {
        InvalidateThemeTransitionStoryboard();
        InvalidateBackgroundLoad();
        if (restoreOutgoingBackground && _outgoingThemeBackground is not null)
        {
            SceneBackgroundImage.Source = _outgoingThemeBackground;
            _backgroundTheme = _outgoingBackgroundTheme;
            _backgroundRenderWidthKey = _outgoingBackgroundRenderWidthKey;
        }
        else
        {
            _backgroundTheme = ElementTheme.Default;
            _backgroundRenderWidthKey = 0;
        }

        _themeTransitionPending = false;
        _outgoingThemeBackground = null;
        _outgoingBackgroundTheme = ElementTheme.Default;
        _outgoingBackgroundRenderWidthKey = 0;
        PreviousSceneBackgroundImage.Source = null;
        PreviousSceneVeil.Fill = null;
        ThemeTransitionCover.Opacity = 0;
        CompleteThemeToggle();
    }

    private void CompleteThemeToggle()
    {
        _themeToggleInProgress = false;
        if (!_disposed)
        {
            ThemeButton.IsEnabled = true;
            if (_backgroundResizePending)
            {
                QueueBackgroundResizeRefresh();
            }
        }
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
        _glowVelocityX = 0;
        _glowVelocityY = 0;
        _trailGlowCurrentX = _glowTargetX;
        _trailGlowCurrentY = _glowTargetY;
        _trailGlowVelocityX = 0;
        _trailGlowVelocityY = 0;
        _glowPositionInitialized = true;
        ApplyPointerGlowPosition();
    }

    private void StartGlowFollow()
    {
        if (_isHighContrast || !_pointerGlowModalCoordinator.AllowsGlow)
        {
            return;
        }

        if (!_glowPositionInitialized)
        {
            InitializePointerGlowAtTarget();
            return;
        }

        if (!_animationsEnabled)
        {
            _glowCurrentX = _glowTargetX;
            _glowCurrentY = _glowTargetY;
            _glowVelocityX = 0;
            _glowVelocityY = 0;
            _trailGlowCurrentX = _glowTargetX;
            _trailGlowCurrentY = _glowTargetY;
            _trailGlowVelocityX = 0;
            _trailGlowVelocityY = 0;
            ApplyPointerGlowPosition();
            return;
        }

        if (_glowRenderingSubscribed)
        {
            return;
        }

        _lastGlowFrameTimestamp = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += GlowFollowRendering;
        _glowRenderingSubscribed = true;
    }

    private void StopGlowFollow()
    {
        _glowVelocityX = 0;
        _glowVelocityY = 0;
        _trailGlowVelocityX = 0;
        _trailGlowVelocityY = 0;
        _lastGlowFrameTimestamp = 0;
        if (!_glowRenderingSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering -= GlowFollowRendering;
        _glowRenderingSubscribed = false;
    }

    private void GlowFollowRendering(object? sender, object e)
    {
        if (_isHighContrast || !_pointerGlowModalCoordinator.AllowsGlow)
        {
            StopGlowFollow();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds = Math.Min(
            (now - _lastGlowFrameTimestamp) / (double)Stopwatch.Frequency,
            0.05);
        _lastGlowFrameTimestamp = now;
        if (elapsedSeconds <= 0)
        {
            return;
        }

        // Two analytic springs keep both light layers behind the scene veil. The
        // compact core preserves pointer connection while the softer trail carries
        // visible inertia. Only composition offsets change, so pointer motion never
        // invalidates XAML layout.
        PointerGlowSpring.Advance(
            ref _glowCurrentX,
            ref _glowVelocityX,
            _glowTargetX,
            elapsedSeconds,
            GlowCoreAngularFrequency,
            GlowCoreDampingRatio);
        PointerGlowSpring.Advance(
            ref _glowCurrentY,
            ref _glowVelocityY,
            _glowTargetY,
            elapsedSeconds,
            GlowCoreAngularFrequency,
            GlowCoreDampingRatio);
        PointerGlowSpring.Advance(
            ref _trailGlowCurrentX,
            ref _trailGlowVelocityX,
            _glowTargetX,
            elapsedSeconds,
            GlowTrailAngularFrequency,
            GlowTrailDampingRatio);
        PointerGlowSpring.Advance(
            ref _trailGlowCurrentY,
            ref _trailGlowVelocityY,
            _glowTargetY,
            elapsedSeconds,
            GlowTrailAngularFrequency,
            GlowTrailDampingRatio);

        var remainingX = Math.Max(
            Math.Abs(_glowTargetX - _glowCurrentX),
            Math.Abs(_glowTargetX - _trailGlowCurrentX));
        var remainingY = Math.Max(
            Math.Abs(_glowTargetY - _glowCurrentY),
            Math.Abs(_glowTargetY - _trailGlowCurrentY));
        var remainingSpeed = Math.Max(
            Math.Max(Math.Abs(_glowVelocityX), Math.Abs(_glowVelocityY)),
            Math.Max(Math.Abs(_trailGlowVelocityX), Math.Abs(_trailGlowVelocityY)));
        if (remainingX < 0.3 && remainingY < 0.3 && remainingSpeed < 4)
        {
            _glowCurrentX = _glowTargetX;
            _glowCurrentY = _glowTargetY;
            _glowVelocityX = 0;
            _glowVelocityY = 0;
            _trailGlowCurrentX = _glowTargetX;
            _trailGlowCurrentY = _glowTargetY;
            _trailGlowVelocityX = 0;
            _trailGlowVelocityY = 0;
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

        _backgroundGlowVisual ??= ElementCompositionPreview.GetElementVisual(BackgroundPointerGlow);
        _backgroundGlowVisual.Offset = new Vector3(
            (float)(_glowCurrentX - (BackgroundPointerGlow.Width / 2)),
            (float)(_glowCurrentY - (BackgroundPointerGlow.Height / 2)),
            0);
        _trailGlowVisual ??= ElementCompositionPreview.GetElementVisual(TrailPointerGlow);
        _trailGlowVisual.Offset = new Vector3(
            (float)(_trailGlowCurrentX - (TrailPointerGlow.Width / 2)),
            (float)(_trailGlowCurrentY - (TrailPointerGlow.Height / 2)),
            0);
    }

    private void SetPointerGlowVisibility(bool visible, bool animate)
    {
        if (_isHighContrast || (visible && !_pointerGlowModalCoordinator.AllowsGlow))
        {
            visible = false;
            animate = false;
        }

        var coreOpacity = visible ? GlowCoreVisibleOpacity : 0;
        var trailOpacity = visible ? GlowTrailVisibleOpacity : 0;

        var generation = InvalidatePointerGlowStoryboard();

        if (!animate)
        {
            BackgroundPointerGlow.Opacity = coreOpacity;
            TrailPointerGlow.Opacity = trailOpacity;
            return;
        }

        var coreDuration = TimeSpan.FromMilliseconds(
            _animationsEnabled ? (visible ? 140 : 120) : 80);
        var trailDuration = TimeSpan.FromMilliseconds(
            _animationsEnabled ? (visible ? 210 : 230) : 100);
        var storyboard = new Storyboard
        {
            FillBehavior = FillBehavior.Stop,
        };
        storyboard.Children.Add(CreateOpacityAnimation(
            BackgroundPointerGlow,
            BackgroundPointerGlow.Opacity,
            coreOpacity,
            coreDuration));
        storyboard.Children.Add(CreateOpacityAnimation(
            TrailPointerGlow,
            TrailPointerGlow.Opacity,
            trailOpacity,
            trailDuration));
        storyboard.Completed += (_, _) =>
        {
            if (generation != _pointerGlowStoryboardGeneration ||
                !ReferenceEquals(_pointerGlowStoryboard, storyboard))
            {
                return;
            }

            _pointerGlowStoryboard = null;
            BackgroundPointerGlow.Opacity = coreOpacity;
            TrailPointerGlow.Opacity = trailOpacity;
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

        var coreOpacity = BackgroundPointerGlow.Opacity;
        var trailOpacity = TrailPointerGlow.Opacity;
        _pointerGlowStoryboard = null;
        storyboard.Stop();
        BackgroundPointerGlow.Opacity = coreOpacity;
        TrailPointerGlow.Opacity = trailOpacity;
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
