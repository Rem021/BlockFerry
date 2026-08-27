using System.Collections.Frozen;
using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using BlockFerry.Core.Options;
using Microsoft.UI.Xaml.Controls;

namespace BlockFerry.App.WinUI.Selection;

public sealed class OptionsSelectionChangedEventArgs(
    int selectedCount,
    int selectableCount,
    bool hasSelection) : EventArgs
{
    public int SelectedCount { get; } = selectedCount;

    public int SelectableCount { get; } = selectableCount;

    public bool HasSelection { get; } = hasSelection;
}

internal sealed class OptionsSelectionViewModel : INotifyPropertyChanged
{
    private OptionSelectionState? _selectionState;

    public ObservableCollection<OptionCategoryViewModel> Categories { get; } = [];

    public int SelectedCount => _selectionState?.SelectedCount ?? 0;

    public int SelectableCount => _selectionState?.SelectableCount ?? 0;

    public bool HasSelection => _selectionState?.HasSelection ?? false;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<OptionsSelectionChangedEventArgs>? SelectionChanged;

    public void Reset(OptionsSelectionCatalog catalog) => Reset(catalog, initialSelectedKeys: null);

    internal void Reset(
        OptionsSelectionCatalog catalog,
        IReadOnlySet<string>? initialSelectedKeys)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        Categories.Clear();
        _selectionState = new OptionSelectionState(catalog);
        if (initialSelectedKeys is not null)
        {
            foreach (var descriptor in catalog.SelectableDifferences)
            {
                _selectionState.SetKeySelected(
                    descriptor.Key,
                    initialSelectedKeys.Contains(descriptor.Key));
            }
        }

        var selectedKeys = _selectionState.SnapshotSelectedKeys();

        foreach (var category in Enum.GetValues<OptionSettingCategory>())
        {
            var descriptors = catalog.SelectableDifferences
                .Where(setting => setting.Category == category)
                .ToList();
            if (descriptors.Count == 0)
            {
                continue;
            }

            Categories.Add(new OptionCategoryViewModel(
                category,
                descriptors,
                _selectionState.GetCategoryState(category),
                selectedKeys,
                OnSettingSelectionChanged));
        }

        PublishSelectionChanged();
    }

    public void Clear()
    {
        Categories.Clear();
        _selectionState = null;
        PublishSelectionChanged();
    }

    internal void SelectAll()
    {
        if (_selectionState is null)
        {
            return;
        }

        foreach (var category in Categories)
        {
            _selectionState.SetCategorySelected(category.Category, selected: true);
            var state = _selectionState.GetCategoryState(category.Category);
            if (category.SelectionState != state)
            {
                category.ApplySelection(selected: true, state);
            }
        }

        PublishSelectionChanged();
    }

    public void ToggleCategory(OptionCategoryViewModel category)
    {
        ArgumentNullException.ThrowIfNull(category);
        if (_selectionState is null || !Categories.Contains(category))
        {
            return;
        }

        var select = category.SelectionState != OptionCategorySelectionState.Selected;
        _selectionState.SetCategorySelected(category.Category, select);
        category.ApplySelection(select, _selectionState.GetCategoryState(category.Category));
        PublishSelectionChanged();
    }

    public IReadOnlySet<string> SnapshotSelectedKeys()
    {
        var selectedKeys = _selectionState?.SnapshotSelectedKeys();
        IEnumerable<string> snapshotKeys = selectedKeys is null
            ? Array.Empty<string>()
            : selectedKeys;
        return new ImmutableSelectionKeySet(snapshotKeys);
    }

    private void OnSettingSelectionChanged(OptionCategoryViewModel category, OptionSettingViewModel setting)
    {
        if (_selectionState is null)
        {
            return;
        }

        _selectionState.SetKeySelected(setting.Key, setting.IsSelected);
        category.ApplySelectionState(_selectionState.GetCategoryState(category.Category));
        PublishSelectionChanged();
    }

    private void PublishSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectableCount));
        OnPropertyChanged(nameof(HasSelection));
        SelectionChanged?.Invoke(
            this,
            new OptionsSelectionChangedEventArgs(SelectedCount, SelectableCount, HasSelection));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed class ImmutableSelectionKeySet(IEnumerable<string> keys) : IReadOnlySet<string>
{
    private readonly FrozenSet<string> _keys = keys.ToFrozenSet(StringComparer.Ordinal);

    public int Count => _keys.Count;

    public bool Contains(string item) => _keys.Contains(item);

    public IEnumerator<string> GetEnumerator() => _keys.GetEnumerator();

    public bool IsProperSubsetOf(IEnumerable<string> other) => _keys.IsProperSubsetOf(other);

    public bool IsProperSupersetOf(IEnumerable<string> other) => _keys.IsProperSupersetOf(other);

    public bool IsSubsetOf(IEnumerable<string> other) => _keys.IsSubsetOf(other);

    public bool IsSupersetOf(IEnumerable<string> other) => _keys.IsSupersetOf(other);

    public bool Overlaps(IEnumerable<string> other) => _keys.Overlaps(other);

    public bool SetEquals(IEnumerable<string> other) => _keys.SetEquals(other);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class OptionCategoryViewModel : INotifyPropertyChanged
{
    private readonly Action<OptionCategoryViewModel, OptionSettingViewModel> _settingSelectionChanged;
    private OptionCategorySelectionState _selectionState;

    internal OptionCategoryViewModel(
        OptionSettingCategory category,
        IReadOnlyList<OptionSettingDescriptor> descriptors,
        OptionCategorySelectionState selectionState,
        IReadOnlySet<string> selectedKeys,
        Action<OptionCategoryViewModel, OptionSettingViewModel> settingSelectionChanged)
    {
        Category = category;
        Title = category switch
        {
            OptionSettingCategory.LanguageAndInterface => "\u8BED\u8A00\u4E0E\u754C\u9762",
            OptionSettingCategory.Controls => "\u6309\u952E\u4E0E\u63A7\u5236",
            OptionSettingCategory.SoundAndDisplay => "\u58F0\u97F3\u4E0E\u663E\u793A",
            _ => "\u5176\u4ED6\u73A9\u5BB6\u8BBE\u7F6E",
        };
        Symbol = OptionCategoryPresentation.GetSymbol(category);
        _selectionState = selectionState;
        _settingSelectionChanged = settingSelectionChanged;
        Settings = new ObservableCollection<OptionSettingViewModel>(
            descriptors.Select(descriptor => new OptionSettingViewModel(
                descriptor,
                isSelected: selectedKeys.Contains(descriptor.Key),
                setting => _settingSelectionChanged(this, setting))));
    }

    public OptionSettingCategory Category { get; }

    public string Title { get; }

    public Symbol Symbol { get; }

    public ObservableCollection<OptionSettingViewModel> Settings { get; }

    public int SelectedCount => Settings.Count(setting => setting.IsSelected);

    public int TotalCount => Settings.Count;

    public string SelectionSummary => OptionCategoryPresentation.FormatSummary(
        SelectionState,
        SelectedCount,
        TotalCount);

    public OptionCategorySelectionState SelectionState
    {
        get => _selectionState;
        private set
        {
            _selectionState = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionState)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionSummary)));
        }
    }

    public bool? IsChecked => SelectionState switch
    {
        OptionCategorySelectionState.Selected => true,
        OptionCategorySelectionState.Partial => null,
        _ => false,
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void ApplySelection(bool selected, OptionCategorySelectionState state)
    {
        foreach (var setting in Settings)
        {
            setting.ApplySelection(selected);
        }

        ApplySelectionState(state);
    }

    internal void ApplySelectionState(OptionCategorySelectionState state) => SelectionState = state;
}

internal sealed class OptionSettingViewModel : INotifyPropertyChanged
{
    private readonly Action<OptionSettingViewModel> _selectionChanged;
    private bool _isSelected;

    internal OptionSettingViewModel(
        OptionSettingDescriptor descriptor,
        bool isSelected,
        Action<OptionSettingViewModel> selectionChanged)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _selectionChanged = selectionChanged;
        Key = descriptor.Key;
        DisplayName = descriptor.DisplayName;
        EscapedTechnicalKey = EscapeTechnicalKey(descriptor.Key);
        SourceValue = descriptor.SourceValue;
        TargetValue = descriptor.TargetValue;
        _isSelected = isSelected;
    }

    public string Key { get; }

    public string DisplayName { get; }

    public string EscapedTechnicalKey { get; }

    public string? SourceValue { get; }

    public string? TargetValue { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            _selectionChanged(this);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void ApplySelection(bool selected)
    {
        if (_isSelected == selected)
        {
            return;
        }

        _isSelected = selected;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
    }

    private static string EscapeTechnicalKey(string key)
    {
        var escaped = new StringBuilder(key.Length);
        foreach (var character in key)
        {
            if (char.IsControl(character))
            {
                escaped.AppendFormat(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "\\u{0:X4}",
                    (int)character);
            }
            else
            {
                escaped.Append(character);
            }
        }

        return escaped.ToString();
    }
}
