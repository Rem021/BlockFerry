using System.ComponentModel;
using BlockFerry.App.WinUI.Selection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace BlockFerry.App.WinUI.Controls;

public sealed partial class ConflictResolutionControl : UserControl
{
    private ConflictResolutionViewModel? _viewModel;
    private bool _updating;

    public ConflictResolutionControl()
    {
        InitializeComponent();
    }

    internal void Bind(
        ContentItemSelectionViewModel item,
        ConflictResolutionViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(viewModel);
        DetachViewModel();
        _viewModel = viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        ConflictTitleText.Text = item.DisplayName;
        ConflictDescriptionText.Text = item.Description;
        AutomationProperties.SetName(
            ResolutionChoices,
            $"{item.DisplayName}，冲突处理方式");
        RefreshSelection();
    }

    private void ResolutionChoices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && _viewModel is not null)
        {
            _viewModel.SelectedIndex = ResolutionChoices.SelectedIndex;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConflictResolutionViewModel.Resolution)
            or nameof(ConflictResolutionViewModel.SelectedIndex))
        {
            RefreshSelection();
        }
    }

    private void RefreshSelection()
    {
        _updating = true;
        try
        {
            ResolutionChoices.SelectedIndex = _viewModel?.SelectedIndex ?? -1;
        }
        finally
        {
            _updating = false;
        }
    }

    private void DetachViewModel()
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }
    }
}
