using System.ComponentModel;
using BlockFerry.App.WinUI.Selection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace BlockFerry.App.WinUI.Controls;

public sealed partial class ContentAdapterCard : UserControl
{
    private const int MaximumRenderedItems = 256;
    private readonly OptionExpansionMotionCoordinator _motionCoordinator = new();
    private readonly List<ItemControlRegistration> _itemControls = [];
    private ContentAdapterCardViewModel? _viewModel;
    private Storyboard? _expansionStoryboard;

    public ContentAdapterCard()
    {
        InitializeComponent();
        AdapterDisclosureButton.ExpandedStateChanged += AdapterDisclosureButton_ExpandedStateChanged;
        UpdateDisclosurePresentation();
    }

    internal void Bind(ContentAdapterCardViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DetachViewModel();
        _viewModel = viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        AdapterTitleText.Text = viewModel.Title;
        AdapterDescriptionText.Text = viewModel.Description;
        AdapterIcon.Symbol = viewModel.Symbol;
        AdapterCheckBox.IsEnabled = viewModel.IsEnabled;
        AdapterDisabledReasonText.Text = viewModel.DisabledReason;
        AdapterDisabledReasonText.Visibility = string.IsNullOrEmpty(viewModel.DisabledReason)
            ? Visibility.Collapsed
            : Visibility.Visible;
        EmiUnsupportedStatus.Visibility = viewModel.HasUnsupportedEmiState
            ? Visibility.Visible
            : Visibility.Collapsed;
        EmiUnsupportedText.Text = viewModel.UnsupportedEmiText;
        BuildItemControls(viewModel);
        RefreshSelectionPresentation();
        UpdateDisclosurePresentation();
    }

    internal void ConfigureAccessibility(bool animationsEnabled, bool highContrast)
    {
        var mode = highContrast
            ? OptionExpansionMotionMode.HighContrast
            : animationsEnabled
                ? OptionExpansionMotionMode.Normal
                : OptionExpansionMotionMode.Reduced;
        if (_motionCoordinator.ChangeMode(mode))
        {
            _expansionStoryboard?.Stop();
            _expansionStoryboard = null;
            CompleteExpansion(AdapterDisclosureButton.IsExpanded);
        }
    }

    private void BuildItemControls(ContentAdapterCardViewModel viewModel)
    {
        AdapterItemsPanel.Children.Clear();
        _itemControls.Clear();
        foreach (var item in viewModel.Items.Take(MaximumRenderedItems))
        {
            if (item.Conflict is not null)
            {
                var conflict = new ConflictResolutionControl();
                conflict.Bind(item, item.Conflict);
                AdapterItemsPanel.Children.Add(conflict);
                continue;
            }

            if (item.IsSelectable)
            {
                var checkBox = CreateItemCheckBox(item);
                AdapterItemsPanel.Children.Add(checkBox);
                continue;
            }

            AdapterItemsPanel.Children.Add(CreateReadOnlyItem(item));
        }

        if (viewModel.Items.Count > MaximumRenderedItems)
        {
            AdapterItemsPanel.Children.Add(new TextBlock
            {
                Text = $"另有 {viewModel.Items.Count - MaximumRenderedItems} 项；提交前仍会完整校验。",
                Foreground = (Brush)Application.Current.Resources["DrawerSecondaryTextBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    private CheckBox CreateItemCheckBox(ContentItemSelectionViewModel item)
    {
        var checkBox = new CheckBox
        {
            IsChecked = item.IsSelected,
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            UseSystemFocusVisuals = true,
            Content = CreateItemLabels(item),
        };
        AutomationProperties.SetName(checkBox, item.DisplayName);
        AutomationProperties.SetHelpText(checkBox, item.Description);
        checkBox.Click += (_, _) => item.IsSelected = checkBox.IsChecked == true;
        PropertyChangedEventHandler changed = (_, args) =>
        {
            if (args.PropertyName == nameof(ContentItemSelectionViewModel.IsSelected))
            {
                checkBox.IsChecked = item.IsSelected;
            }
        };
        item.PropertyChanged += changed;
        _itemControls.Add(new ItemControlRegistration(item, changed));
        return checkBox;
    }

    private static ContentControl CreateReadOnlyItem(ContentItemSelectionViewModel item)
    {
        var control = new ContentControl
        {
            IsEnabled = false,
            IsTabStop = false,
            Content = CreateItemLabels(item),
        };
        AutomationProperties.SetName(
            control,
            string.IsNullOrEmpty(item.DisabledReason)
                ? item.DisplayName
                : $"{item.DisplayName}，{item.DisabledReason}");
        return control;
    }

    private static StackPanel CreateItemLabels(ContentItemSelectionViewModel item)
    {
        var labels = new StackPanel { Spacing = 2 };
        labels.Children.Add(new TextBlock
        {
            Text = item.DisplayName,
            Foreground = (Brush)Application.Current.Resources["DrawerTextBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        labels.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(item.DisabledReason)
                ? item.Description
                : item.DisabledReason,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["DrawerSecondaryTextBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
        });
        return labels;
    }

    private void AdapterCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.IsChecked = AdapterCheckBox.IsChecked == true;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ContentAdapterCardViewModel.IsChecked)
            or nameof(ContentAdapterCardViewModel.SelectionSummary))
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

        AdapterCheckBox.IsChecked = _viewModel.IsChecked;
        AdapterSummaryText.Text = _viewModel.SelectionSummary;
        AutomationProperties.SetName(
            AdapterCheckBox,
            $"{_viewModel.Title}，{_viewModel.SelectionSummary}");
        AdapterSelectedSurface.Opacity = _viewModel.IsChecked is true or null ? 1 : 0;
    }

    private void AdapterDisclosureButton_ExpandedStateChanged(object? sender, EventArgs e)
    {
        UpdateDisclosurePresentation();
        SetDetailsExpanded(AdapterDisclosureButton.IsExpanded);
    }

    private void UpdateDisclosurePresentation()
    {
        AdapterDisclosureButton.Content = AdapterDisclosureButton.IsExpanded ? "\uE70D" : "\uE76C";
        AutomationProperties.SetName(
            AdapterDisclosureButton,
            $"{(AdapterDisclosureButton.IsExpanded ? "折叠" : "展开")}{_viewModel?.Title ?? "同步内容"}详情");
    }

    private void SetDetailsExpanded(bool expanded)
    {
        if (!expanded && IsFocusWithinDetails())
        {
            AdapterDisclosureButton.Focus(FocusState.Programmatic);
        }

        AdapterItemsPanel.Measure(new Size(Math.Max(0, ActualWidth), double.PositiveInfinity));
        var desiredHeight = Math.Max(1, AdapterItemsPanel.DesiredSize.Height +
            (EmiUnsupportedStatus.Visibility == Visibility.Visible ? 54 : 0));
        var plan = _motionCoordinator.BeginTransition(
            expanded,
            AdapterDetailsRegion.ActualHeight,
            AdapterDetailsRegion.Opacity,
            desiredHeight);
        _expansionStoryboard?.Stop();
        _expansionStoryboard = null;
        if (plan.AnimationKind == OptionExpansionAnimationKind.Immediate)
        {
            CompleteExpansion(expanded);
            return;
        }

        AdapterDetailsRegion.Visibility = Visibility.Visible;
        AdapterDetailsRegion.MaxHeight = plan.FromHeight;
        AdapterDetailsRegion.Opacity = plan.FromOpacity;
        var storyboard = new Storyboard();
        var duration = new Duration(plan.Duration);
        if (plan.AnimationKind == OptionExpansionAnimationKind.HeightAndOpacity)
        {
            var height = new DoubleAnimation
            {
                From = plan.FromHeight,
                To = plan.ToHeight,
                Duration = duration,
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(height, AdapterDetailsRegion);
            Storyboard.SetTargetProperty(height, "MaxHeight");
            storyboard.Children.Add(height);
        }
        else
        {
            AdapterDetailsRegion.MaxHeight = double.PositiveInfinity;
        }

        var opacity = new DoubleAnimation
        {
            From = plan.FromOpacity,
            To = plan.ToOpacity,
            Duration = duration,
        };
        Storyboard.SetTarget(opacity, AdapterDetailsRegion);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        storyboard.Children.Add(opacity);
        storyboard.Completed += (_, _) => CompleteCurrentTransition(storyboard, plan);
        _expansionStoryboard = storyboard;
        storyboard.Begin();
    }

    private void CompleteCurrentTransition(
        Storyboard storyboard,
        OptionExpansionTransitionPlan plan)
    {
        if (ReferenceEquals(_expansionStoryboard, storyboard) &&
            _motionCoordinator.TryComplete(plan.Generation))
        {
            CompleteExpansion(plan.Expanded);
        }
    }

    private void CompleteExpansion(bool expanded)
    {
        _expansionStoryboard = null;
        AdapterDetailsRegion.Opacity = expanded ? 1 : 0;
        AdapterDetailsRegion.MaxHeight = expanded ? double.PositiveInfinity : 0;
        AdapterDetailsRegion.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool IsFocusWithinDetails() => AdapterItemsPanel.Children
        .OfType<Control>()
        .Any(control => control.FocusState != FocusState.Unfocused);

    private void DetachViewModel()
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

        foreach (var registration in _itemControls)
        {
            registration.Item.PropertyChanged -= registration.PropertyChanged;
        }

        _itemControls.Clear();
    }

    private sealed record ItemControlRegistration(
        ContentItemSelectionViewModel Item,
        PropertyChangedEventHandler PropertyChanged);
}
