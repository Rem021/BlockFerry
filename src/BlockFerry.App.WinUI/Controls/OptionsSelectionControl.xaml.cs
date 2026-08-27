using BlockFerry.App.WinUI.Selection;
using BlockFerry.Core.Options;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BlockFerry.App.WinUI.Controls;

public sealed class OptionsSelectionFocusToken
{
    private OptionsSelectionFocusToken(
        OptionSettingCategory? category,
        string? settingKey,
        OptionsSelectionFocusTarget target)
    {
        Category = category;
        SettingKey = settingKey;
        Target = target;
    }

    internal OptionSettingCategory? Category { get; }

    internal string? SettingKey { get; }

    internal OptionsSelectionFocusTarget Target { get; }

    internal static OptionsSelectionFocusToken None { get; } =
        new(null, null, OptionsSelectionFocusTarget.None);

    internal static OptionsSelectionFocusToken ForCategoryToggle(OptionSettingCategory category) =>
        new(category, null, OptionsSelectionFocusTarget.CategoryToggle);

    internal static OptionsSelectionFocusToken ForDisclosure(OptionSettingCategory category) =>
        new(category, null, OptionsSelectionFocusTarget.Disclosure);

    internal static OptionsSelectionFocusToken ForSetting(OptionSettingCategory category, string settingKey) =>
        new(category, settingKey, OptionsSelectionFocusTarget.Setting);
}

internal enum OptionsSelectionFocusTarget
{
    None,
    CategoryToggle,
    Disclosure,
    Setting,
}

public sealed partial class OptionsSelectionControl : UserControl
{
    private readonly OptionsSelectionViewModel _viewModel = new();
    private readonly List<OptionCategoryControl> _categoryControls = [];
    private OptionsSelectionCatalog? _catalog;
    private bool _animationsEnabled = true;
    private bool _highContrast;

    public OptionsSelectionControl()
    {
        InitializeComponent();
        _viewModel.SelectionChanged += ViewModel_SelectionChanged;
        UpdateSelectedCount();
    }

    public event EventHandler<OptionsSelectionChangedEventArgs>? SelectionChanged;

    public void LoadCatalog(OptionsSelectionCatalog catalog) =>
        LoadCatalog(catalog, initialSelectedKeys: null);

    internal void LoadCatalog(
        OptionsSelectionCatalog catalog,
        IReadOnlySet<string>? initialSelectedKeys)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _viewModel.Reset(catalog, initialSelectedKeys);
        RenderCategories();

        LockedSafetyStrip.Visibility = catalog.ProtectedDifferences.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        LockedSafetySummaryText.Text = $"已保护 {catalog.ProtectedDifferences.Count} 项";
    }

    public void Clear()
    {
        _catalog = null;
        _viewModel.Clear();
        _categoryControls.Clear();
        CategoriesPanel.Children.Clear();
        LockedSafetyStrip.Visibility = Visibility.Collapsed;
        LockedSafetySummaryText.Text = string.Empty;
    }

    public IReadOnlySet<string> SnapshotSelectedKeys() => _viewModel.SnapshotSelectedKeys();

    public void ConfigureAccessibility(bool animationsEnabled, bool highContrast)
    {
        _animationsEnabled = animationsEnabled;
        _highContrast = highContrast;

        foreach (var category in _categoryControls)
        {
            category.ConfigureAccessibility(animationsEnabled, highContrast);
        }
    }

    public OptionsSelectionFocusToken CaptureFocus()
    {
        foreach (var category in _categoryControls)
        {
            var token = category.CaptureFocus();
            if (token is not null)
            {
                return token;
            }
        }

        return OptionsSelectionFocusToken.None;
    }

    public bool RestoreFocus(OptionsSelectionFocusToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return _categoryControls.Any(category => category.RestoreFocus(token));
    }

    private void RenderCategories()
    {
        _categoryControls.Clear();
        CategoriesPanel.Children.Clear();

        foreach (var categoryViewModel in _viewModel.Categories)
        {
            var categoryControl = new OptionCategoryControl();
            categoryControl.Bind(categoryViewModel, _viewModel.ToggleCategory);
            categoryControl.ConfigureAccessibility(_animationsEnabled, _highContrast);
            _categoryControls.Add(categoryControl);
            CategoriesPanel.Children.Add(categoryControl);
        }
    }

    private void ResetSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectAll();
    }

    private void ViewModel_SelectionChanged(object? sender, OptionsSelectionChangedEventArgs e)
    {
        UpdateSelectedCount();
        SelectionChanged?.Invoke(this, e);
    }

    private void UpdateSelectedCount()
    {
        ResetSelectionButton.IsEnabled = _catalog is not null && _viewModel.SelectedCount < _viewModel.SelectableCount;
    }
}
