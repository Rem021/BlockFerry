using System.ComponentModel;
using System.Runtime.CompilerServices;
using BlockFerry.Core.Content;

namespace BlockFerry.App.WinUI.Selection;

internal sealed class ConflictResolutionViewModel : INotifyPropertyChanged
{
    private ConflictResolution _resolution;

    internal ConflictResolutionViewModel(
        ContentItemId itemId,
        ConflictResolution defaultResolution)
    {
        if (defaultResolution is not (ConflictResolution.KeepTarget or ConflictResolution.Skip))
        {
            throw new ArgumentOutOfRangeException(nameof(defaultResolution));
        }

        ItemId = itemId;
        DefaultResolution = defaultResolution;
        _resolution = defaultResolution;
    }

    internal ContentItemId ItemId { get; }

    internal ConflictResolution DefaultResolution { get; }

    internal ConflictResolution Resolution
    {
        get => _resolution;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (_resolution == value)
            {
                return;
            }

            _resolution = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedIndex));
            ResolutionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal int SelectedIndex
    {
        get => Resolution switch
        {
            ConflictResolution.KeepTarget => 0,
            ConflictResolution.UseSource => 1,
            ConflictResolution.Skip => 2,
            _ => -1,
        };
        set => Resolution = value switch
        {
            0 => ConflictResolution.KeepTarget,
            1 => ConflictResolution.UseSource,
            2 => ConflictResolution.Skip,
            _ => ConflictResolution.Unresolved,
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal event EventHandler? ResolutionChanged;

    internal void ResetToDefault() => Resolution = DefaultResolution;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
