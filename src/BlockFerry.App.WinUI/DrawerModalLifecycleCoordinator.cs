namespace BlockFerry.App.WinUI;

internal enum DrawerCloseRequestOutcome
{
    Ignored,
    RejectedMutation,
    Closing,
}

internal readonly record struct DrawerCloseRequestDecision(
    DrawerCloseRequestOutcome Outcome,
    long Generation);

internal sealed class DrawerModalLifecycleCoordinator
{
    private long _generation;

    public DrawerModalPhase Phase { get; private set; } = DrawerModalPhase.Collapsed;

    public long BeginOpening()
    {
        if (Phase != DrawerModalPhase.Collapsed)
        {
            throw new InvalidOperationException("The drawer can begin opening only from Collapsed.");
        }

        Phase = DrawerModalPhase.Opening;
        return ++_generation;
    }

    public bool TryCompleteOpening(long generation)
    {
        if (generation != _generation || Phase != DrawerModalPhase.Opening)
        {
            return false;
        }

        Phase = DrawerModalPhase.Open;
        return true;
    }

    public long BeginClosing()
    {
        if (Phase != DrawerModalPhase.Open)
        {
            throw new InvalidOperationException("The drawer can begin closing only from Open.");
        }

        Phase = DrawerModalPhase.Closing;
        return ++_generation;
    }

    internal DrawerCloseRequestDecision RequestClose(bool isMutationInProgress)
    {
        if (Phase != DrawerModalPhase.Open)
        {
            return new DrawerCloseRequestDecision(DrawerCloseRequestOutcome.Ignored, 0);
        }

        if (isMutationInProgress)
        {
            return new DrawerCloseRequestDecision(DrawerCloseRequestOutcome.RejectedMutation, 0);
        }

        return new DrawerCloseRequestDecision(DrawerCloseRequestOutcome.Closing, BeginClosing());
    }

    public bool TryCompleteClosing(long generation)
    {
        if (generation != _generation || Phase != DrawerModalPhase.Closing)
        {
            return false;
        }

        Phase = DrawerModalPhase.Collapsed;
        return true;
    }

    public bool NormalizeCollapsed()
    {
        var phaseChanged = Phase != DrawerModalPhase.Collapsed;
        ++_generation;
        Phase = DrawerModalPhase.Collapsed;
        return phaseChanged;
    }
}
