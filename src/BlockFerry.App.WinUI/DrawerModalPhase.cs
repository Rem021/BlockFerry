namespace BlockFerry.App.WinUI;

internal enum DrawerModalPhase
{
    Collapsed,
    Opening,
    Open,
    Closing,
}

internal sealed class DrawerModalPhaseChangedEventArgs(DrawerModalPhase phase) : EventArgs
{
    public DrawerModalPhase Phase { get; } = phase;

    public bool IsModal => Phase != DrawerModalPhase.Collapsed;
}
