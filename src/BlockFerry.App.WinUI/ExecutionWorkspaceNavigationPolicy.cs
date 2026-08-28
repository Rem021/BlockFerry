namespace BlockFerry.App.WinUI;

internal enum ExecutionWorkspaceNavigationAction
{
    None,
    ShowHome,
    ShowWorkspace,
}

internal static class ExecutionWorkspaceNavigationPolicy
{
    internal static ExecutionWorkspaceNavigationAction Evaluate(
        MigrationWorkflowPhase? previousPhase,
        MigrationWorkflowState current,
        DrawerModalPhase drawerPhase)
    {
        ArgumentNullException.ThrowIfNull(current);
        var wasMutationInProgress = previousPhase is
            MigrationWorkflowPhase.Executing or MigrationWorkflowPhase.RollingBack;

        if (current.IsMutationInProgress &&
            !wasMutationInProgress &&
            drawerPhase == DrawerModalPhase.Open)
        {
            return ExecutionWorkspaceNavigationAction.ShowHome;
        }

        if (wasMutationInProgress &&
            !current.IsMutationInProgress &&
            current.Phase is MigrationWorkflowPhase.Blocked or MigrationWorkflowPhase.RecoveryRequired &&
            drawerPhase is DrawerModalPhase.Collapsed or DrawerModalPhase.Closing)
        {
            return ExecutionWorkspaceNavigationAction.ShowWorkspace;
        }

        return ExecutionWorkspaceNavigationAction.None;
    }
}
