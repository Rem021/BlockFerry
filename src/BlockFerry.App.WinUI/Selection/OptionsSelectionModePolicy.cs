namespace BlockFerry.App.WinUI.Selection;

internal static class OptionsSelectionModePolicy
{
    public static bool UsesLegacyOptionsSelection(bool workflowAttached, bool workflowIsDemo) =>
        !workflowAttached || workflowIsDemo;
}
