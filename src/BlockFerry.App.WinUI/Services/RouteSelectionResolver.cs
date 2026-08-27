namespace BlockFerry.App.WinUI.Services;

internal enum RouteEndpoint
{
    Source,
    Target,
}

internal readonly record struct RouteSelectionPair(string SourceId, string TargetId);

internal static class RouteSelectionResolver
{
    public static RouteSelectionPair Resolve(
        string currentSourceId,
        string currentTargetId,
        RouteEndpoint changedEndpoint,
        string selectedInstanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentSourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentTargetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedInstanceId);

        return changedEndpoint switch
        {
            RouteEndpoint.Source when string.Equals(
                selectedInstanceId,
                currentTargetId,
                StringComparison.Ordinal) => new(selectedInstanceId, currentSourceId),
            RouteEndpoint.Source => new(selectedInstanceId, currentTargetId),
            RouteEndpoint.Target when string.Equals(
                selectedInstanceId,
                currentSourceId,
                StringComparison.Ordinal) => new(currentTargetId, selectedInstanceId),
            RouteEndpoint.Target => new(currentSourceId, selectedInstanceId),
            _ => throw new ArgumentOutOfRangeException(nameof(changedEndpoint)),
        };
    }
}
