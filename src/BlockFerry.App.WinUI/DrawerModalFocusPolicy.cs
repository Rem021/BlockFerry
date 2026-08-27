namespace BlockFerry.App.WinUI;

internal static class DrawerModalFocusPolicy
{
    public static bool ShouldMoveInside(
        DrawerModalPhase phase,
        bool focusAlreadyWithinDrawer) =>
        !focusAlreadyWithinDrawer &&
        phase is DrawerModalPhase.Opening or DrawerModalPhase.Open or DrawerModalPhase.Closing;
}
