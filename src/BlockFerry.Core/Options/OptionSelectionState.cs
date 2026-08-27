namespace BlockFerry.Core.Options;

public sealed class OptionSelectionState
{
    private readonly IReadOnlyList<OptionSettingDescriptor> _selectableDifferences;
    private readonly HashSet<string> _selectableKeys;
    private readonly HashSet<string> _selectedKeys;

    public OptionSelectionState(OptionsSelectionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _selectableDifferences = catalog.SelectableDifferences;
        _selectableKeys = new HashSet<string>(
            _selectableDifferences.Select(item => item.Key),
            StringComparer.Ordinal);
        _selectedKeys = new HashSet<string>(_selectableKeys, StringComparer.Ordinal);
    }

    public IReadOnlySet<string> SelectedKeys => SnapshotSelectedKeys();

    public int SelectableCount => _selectableKeys.Count;

    public int SelectedCount => _selectedKeys.Count;

    public bool HasSelection => _selectedKeys.Count > 0;

    public OptionCategorySelectionState GetCategoryState(OptionSettingCategory category)
    {
        var categoryKeys = GetCategoryKeys(category);
        if (categoryKeys.Count == 0 || categoryKeys.All(key => !_selectedKeys.Contains(key)))
        {
            return OptionCategorySelectionState.Unselected;
        }

        return categoryKeys.All(_selectedKeys.Contains)
            ? OptionCategorySelectionState.Selected
            : OptionCategorySelectionState.Partial;
    }

    public void SetCategorySelected(OptionSettingCategory category, bool selected)
    {
        foreach (var key in GetCategoryKeys(category))
        {
            SetKeySelected(key, selected);
        }
    }

    public void SetKeySelected(string key, bool selected)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_selectableKeys.Contains(key))
        {
            return;
        }

        if (selected)
        {
            _selectedKeys.Add(key);
        }
        else
        {
            _selectedKeys.Remove(key);
        }
    }

    public IReadOnlySet<string> SnapshotSelectedKeys() =>
        new HashSet<string>(_selectedKeys, StringComparer.Ordinal);

    private List<string> GetCategoryKeys(OptionSettingCategory category) =>
        _selectableDifferences
            .Where(item => item.Category == category)
            .Select(item => item.Key)
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
