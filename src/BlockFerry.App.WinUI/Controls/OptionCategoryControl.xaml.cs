using System.ComponentModel;
using BlockFerry.App.WinUI.Selection;
using BlockFerry.Core.Options;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace BlockFerry.App.WinUI.Controls;

public sealed partial class OptionCategoryControl : UserControl
{
    private readonly List<SettingControlRegistration> _settingControls = [];
    private readonly OptionExpansionMotionCoordinator _motionCoordinator = new();
    private OptionCategoryViewModel? _viewModel;
    private Action<OptionCategoryViewModel>? _toggleCategory;
    private Storyboard? _expansionStoryboard;

    public OptionCategoryControl()
    {
        InitializeComponent();
        DisclosureButton.ExpandedStateChanged += DisclosureButton_ExpandedStateChanged;
        UpdateDisclosurePresentation();
    }

    internal OptionSettingCategory Category =>
        _viewModel?.Category ?? OptionSettingCategory.OtherPlayerSettings;

    internal void Bind(
        OptionCategoryViewModel viewModel,
        Action<OptionCategoryViewModel> toggleCategory)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(toggleCategory);

        DetachViewModel();
        _viewModel = viewModel;
        _toggleCategory = toggleCategory;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

        CategoryTitleText.Text = viewModel.Title;
        CategoryIcon.Symbol = viewModel.Symbol;
        AutomationProperties.SetName(CategoryCheckBox, $"选择{viewModel.Title}");
        BuildSettingControls(viewModel);
        RefreshSelectionPresentation();
        UpdateDisclosurePresentation();
    }

    internal void ConfigureAccessibility(bool animationsEnabled, bool highContrast)
    {
        RefreshSettingThemeResources();

        var mode = highContrast
            ? OptionExpansionMotionMode.HighContrast
            : animationsEnabled
                ? OptionExpansionMotionMode.Normal
                : OptionExpansionMotionMode.Reduced;

        if (_motionCoordinator.ChangeMode(mode))
        {
            _expansionStoryboard?.Stop();
            _expansionStoryboard = null;
            CompleteExpansion(DisclosureButton.IsExpanded);
        }
    }

    internal OptionsSelectionFocusToken? CaptureFocus()
    {
        if (_viewModel is null)
        {
            return null;
        }

        if (CategoryCheckBox.FocusState != FocusState.Unfocused)
        {
            return OptionsSelectionFocusToken.ForCategoryToggle(_viewModel.Category);
        }

        if (DisclosureButton.FocusState != FocusState.Unfocused)
        {
            return OptionsSelectionFocusToken.ForDisclosure(_viewModel.Category);
        }

        var focusedSetting = _settingControls.FirstOrDefault(
            registration => registration.CheckBox.FocusState != FocusState.Unfocused);
        return focusedSetting is null
            ? null
            : OptionsSelectionFocusToken.ForSetting(_viewModel.Category, focusedSetting.ViewModel.Key);
    }

    internal bool RestoreFocus(OptionsSelectionFocusToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (_viewModel is null || token.Category != _viewModel.Category)
        {
            return false;
        }

        if (token.Target == OptionsSelectionFocusTarget.CategoryToggle)
        {
            return CategoryCheckBox.Focus(FocusState.Programmatic);
        }

        if (token.Target == OptionsSelectionFocusTarget.Disclosure)
        {
            return DisclosureButton.Focus(FocusState.Programmatic);
        }

        if (token.Target != OptionsSelectionFocusTarget.Setting)
        {
            return false;
        }

        var setting = _settingControls.FirstOrDefault(
            registration => string.Equals(registration.ViewModel.Key, token.SettingKey, StringComparison.Ordinal));
        if (setting is null)
        {
            return false;
        }

        if (!DisclosureButton.IsExpanded)
        {
            DisclosureButton.IsExpanded = true;
        }

        return setting.CheckBox.Focus(FocusState.Programmatic);
    }

    private void BuildSettingControls(OptionCategoryViewModel viewModel)
    {
        ChildrenPanel.Children.Clear();

        foreach (var setting in viewModel.Settings)
        {
            var settingCheckBox = new CheckBox
            {
                IsChecked = setting.IsSelected,
                MinWidth = 40,
                MinHeight = 44,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                UseSystemFocusVisuals = true,
            };
            AutomationProperties.SetName(settingCheckBox, setting.DisplayName);
            AutomationProperties.SetHelpText(settingCheckBox, setting.EscapedTechnicalKey);
            ToolTipService.SetToolTip(settingCheckBox, setting.EscapedTechnicalKey);

            var displayName = new TextBlock
            {
                Text = setting.DisplayName,
                Foreground = (Brush)Application.Current.Resources["DrawerTextBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var technicalKey = new TextBlock
            {
                Text = setting.EscapedTechnicalKey,
                Foreground = (Brush)Application.Current.Resources["DrawerSecondaryTextBrush"],
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            AutomationProperties.SetHelpText(technicalKey, setting.EscapedTechnicalKey);
            ToolTipService.SetToolTip(technicalKey, setting.EscapedTechnicalKey);

            var labels = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
            };
            labels.Children.Add(displayName);
            labels.Children.Add(technicalKey);

            var rowSurface = new Border
            {
                Padding = new Thickness(8, 6, 8, 6),
                Background = new SolidColorBrush(Colors.Transparent),
                CornerRadius = new CornerRadius(8),
                Child = labels,
            };
            rowSurface.PointerEntered += SettingRow_PointerEntered;
            rowSurface.PointerExited += SettingRow_PointerExited;
            rowSurface.PointerPressed += SettingRow_PointerPressed;
            rowSurface.PointerReleased += SettingRow_PointerReleased;
            rowSurface.PointerCaptureLost += SettingRow_PointerCaptureLost;
            settingCheckBox.Content = rowSurface;

            settingCheckBox.Click += (_, _) => setting.IsSelected = settingCheckBox.IsChecked == true;
            PropertyChangedEventHandler propertyChanged = (_, args) =>
            {
                if (args.PropertyName == nameof(OptionSettingViewModel.IsSelected))
                {
                    settingCheckBox.IsChecked = setting.IsSelected;
                }
            };
            setting.PropertyChanged += propertyChanged;
            _settingControls.Add(new SettingControlRegistration(setting, settingCheckBox, displayName, technicalKey, rowSurface, propertyChanged));
            ChildrenPanel.Children.Add(settingCheckBox);
        }

        RefreshSettingThemeResources();
    }

    private void RefreshSettingThemeResources()
    {
        foreach (var registration in _settingControls)
        {
            registration.DisplayNameText.Foreground = (Brush)Application.Current.Resources["DrawerTextBrush"];
            registration.TechnicalKeyText.Foreground = (Brush)Application.Current.Resources["DrawerSecondaryTextBrush"];
            registration.RowSurface.Background = new SolidColorBrush(Colors.Transparent);
        }
    }

    private void DetachViewModel()
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

        foreach (var registration in _settingControls)
        {
            registration.ViewModel.PropertyChanged -= registration.PropertyChanged;
        }

        _settingControls.Clear();
    }

    private void CategoryCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _toggleCategory?.Invoke(_viewModel);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OptionCategoryViewModel.SelectionState)
            or nameof(OptionCategoryViewModel.IsChecked)
            or nameof(OptionCategoryViewModel.SelectionSummary))
        {
            RefreshSelectionPresentation();
        }
    }

    private void RefreshSelectionPresentation()
    {
        if (_viewModel is null)
        {
            return;
        }

        CategoryCheckBox.IsChecked = _viewModel.IsChecked;
        CategorySummaryText.Text = _viewModel.SelectionSummary;
        AutomationProperties.SetName(CategoryCheckBox, $"{_viewModel.Title}, {_viewModel.SelectionSummary}");
        var hasSelection = _viewModel.SelectionState != OptionCategorySelectionState.Unselected;
        SelectedSurface.Opacity = hasSelection ? 1 : 0;
    }

    private void DisclosureButton_ExpandedStateChanged(object? sender, EventArgs e)
    {
        UpdateDisclosurePresentation();
        SetChildrenExpanded(DisclosureButton.IsExpanded);
    }

    private void UpdateDisclosurePresentation()
    {
        DisclosureButton.Content = DisclosureButton.IsExpanded ? "\uE70D" : "\uE76C";
        var categoryName = _viewModel?.Title ?? "设置类别";
        AutomationProperties.SetName(
            DisclosureButton,
            $"{(DisclosureButton.IsExpanded ? "折叠" : "展开")}{categoryName}");
    }

    private void SetChildrenExpanded(bool expanded)
    {
        if (!expanded && IsFocusWithinSettings())
        {
            DisclosureButton.Focus(FocusState.Programmatic);
        }

        var currentHeight = ChildrenRegion.ActualHeight;
        var currentOpacity = ChildrenRegion.Opacity;
        ChildrenPanel.Measure(new Size(Math.Max(0, ActualWidth), double.PositiveInfinity));
        var expandedHeight = Math.Max(1, ChildrenPanel.DesiredSize.Height);
        var plan = _motionCoordinator.BeginTransition(
            expanded,
            currentHeight,
            currentOpacity,
            expandedHeight);

        _expansionStoryboard?.Stop();
        _expansionStoryboard = null;

        switch (plan.AnimationKind)
        {
            case OptionExpansionAnimationKind.HeightAndOpacity:
                BeginHeightAndOpacityExpansion(plan);
                break;
            case OptionExpansionAnimationKind.OpacityOnly:
                BeginOpacityOnlyExpansion(plan);
                break;
            default:
                CompleteExpansion(expanded);
                break;
        }
    }

    private void BeginHeightAndOpacityExpansion(OptionExpansionTransitionPlan plan)
    {
        ChildrenRegion.Visibility = Visibility.Visible;
        ChildrenRegion.MaxHeight = plan.FromHeight;
        ChildrenRegion.Opacity = plan.FromOpacity;

        var storyboard = new Storyboard();
        var duration = new Duration(plan.Duration);
        var heightAnimation = new DoubleAnimation
        {
            From = plan.FromHeight,
            To = plan.ToHeight,
            Duration = duration,
            EnableDependentAnimation = true,
        };
        var opacityAnimation = new DoubleAnimation
        {
            From = plan.FromOpacity,
            To = plan.ToOpacity,
            Duration = duration,
        };
        Storyboard.SetTarget(heightAnimation, ChildrenRegion);
        Storyboard.SetTargetProperty(heightAnimation, "MaxHeight");
        Storyboard.SetTarget(opacityAnimation, ChildrenRegion);
        Storyboard.SetTargetProperty(opacityAnimation, "Opacity");
        storyboard.Children.Add(heightAnimation);
        storyboard.Children.Add(opacityAnimation);
        storyboard.Completed += (_, _) => CompleteCurrentTransition(storyboard, plan);
        _expansionStoryboard = storyboard;
        storyboard.Begin();
    }

    private void BeginOpacityOnlyExpansion(OptionExpansionTransitionPlan plan)
    {
        ChildrenRegion.Visibility = Visibility.Visible;
        ChildrenRegion.MaxHeight = double.PositiveInfinity;
        ChildrenRegion.Opacity = plan.FromOpacity;

        var storyboard = new Storyboard();
        var opacityAnimation = new DoubleAnimation
        {
            From = plan.FromOpacity,
            To = plan.ToOpacity,
            Duration = new Duration(plan.Duration),
        };
        Storyboard.SetTarget(opacityAnimation, ChildrenRegion);
        Storyboard.SetTargetProperty(opacityAnimation, "Opacity");
        storyboard.Children.Add(opacityAnimation);
        storyboard.Completed += (_, _) => CompleteCurrentTransition(storyboard, plan);
        _expansionStoryboard = storyboard;
        storyboard.Begin();
    }

    private void CompleteCurrentTransition(
        Storyboard storyboard,
        OptionExpansionTransitionPlan plan)
    {
        if (ReferenceEquals(_expansionStoryboard, storyboard)
            && _motionCoordinator.TryComplete(plan.Generation))
        {
            CompleteExpansion(plan.Expanded);
        }
    }

    private void CompleteExpansion(bool expanded)
    {
        _expansionStoryboard = null;
        ChildrenRegion.Opacity = expanded ? 1 : 0;
        ChildrenRegion.MaxHeight = expanded ? double.PositiveInfinity : 0;
        ChildrenRegion.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool IsFocusWithinSettings() =>
        _settingControls.Any(registration => registration.CheckBox.FocusState != FocusState.Unfocused);

    private static void SettingRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border row)
        {
            row.Background = (Brush)Application.Current.Resources["OptionSettingHoverBrush"];
        }
    }

    private static void SettingRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border row)
        {
            row.Background = new SolidColorBrush(Colors.Transparent);
        }
    }

    private static void SettingRow_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border row)
        {
            row.Background = (Brush)Application.Current.Resources["OptionSettingPressedBrush"];
        }
    }

    private static void SettingRow_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border row)
        {
            row.Background = (Brush)Application.Current.Resources["OptionSettingHoverBrush"];
        }
    }

    private static void SettingRow_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border row)
        {
            row.Background = new SolidColorBrush(Colors.Transparent);
        }
    }

    private sealed record SettingControlRegistration(
        OptionSettingViewModel ViewModel,
        CheckBox CheckBox,
        TextBlock DisplayNameText,
        TextBlock TechnicalKeyText,
        Border RowSurface,
        PropertyChangedEventHandler PropertyChanged);
}
