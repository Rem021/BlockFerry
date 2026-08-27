namespace BlockFerry.App.WinUI.Services;

internal static class RouteSelectionIntentDispatcher
{
    public static async Task<bool> DispatchAsync(
        object? changedPicker,
        object sourcePicker,
        object targetPicker,
        string currentSourceId,
        string currentTargetId,
        string selectedInstanceId,
        Func<string, string, Task> submitPairAsync)
    {
        ArgumentNullException.ThrowIfNull(sourcePicker);
        ArgumentNullException.ThrowIfNull(targetPicker);
        ArgumentNullException.ThrowIfNull(submitPairAsync);

        RouteEndpoint changedEndpoint;
        if (ReferenceEquals(changedPicker, sourcePicker))
        {
            changedEndpoint = RouteEndpoint.Source;
        }
        else if (ReferenceEquals(changedPicker, targetPicker))
        {
            changedEndpoint = RouteEndpoint.Target;
        }
        else
        {
            return false;
        }

        var nextPair = RouteSelectionResolver.Resolve(
            currentSourceId,
            currentTargetId,
            changedEndpoint,
            selectedInstanceId);
        await submitPairAsync(nextPair.SourceId, nextPair.TargetId);
        return true;
    }
}
