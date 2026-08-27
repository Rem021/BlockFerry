namespace BlockFerry.App.WinUI.Selection;

internal readonly record struct OptionsSelectionLifecycleRecovery(
    bool ReturnToSelection,
    bool SelectionEnabled,
    bool RefreshNeeded);

internal static class OptionsSelectionLifecyclePolicy
{
    public static OptionsSelectionLifecycleRecovery DecideRecovery(
        bool operationWasInFlight,
        bool hasCatalog,
        bool hasUsableSession)
    {
        var canReuseSelection = hasCatalog && hasUsableSession;
        return new OptionsSelectionLifecycleRecovery(
            operationWasInFlight,
            canReuseSelection,
            !canReuseSelection);
    }
}
