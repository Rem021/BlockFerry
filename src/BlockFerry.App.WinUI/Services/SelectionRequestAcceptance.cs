namespace BlockFerry.App.WinUI.Services;

internal static class SelectionRequestAcceptance
{
    public static bool IsCurrent(
        long requestGeneration,
        long currentGeneration,
        object? requestedSession,
        object? currentSession,
        bool isCurrentPair,
        CancellationToken cancellationToken) =>
        requestGeneration == currentGeneration &&
        ReferenceEquals(requestedSession, currentSession) &&
        isCurrentPair &&
        !cancellationToken.IsCancellationRequested;
}
