namespace BlockFerry.App.WinUI.Controls;

internal enum OptionExpansionMotionMode
{
    Normal,
    Reduced,
    HighContrast,
}

internal enum OptionExpansionAnimationKind
{
    HeightAndOpacity,
    OpacityOnly,
    Immediate,
}

internal readonly record struct OptionExpansionTransitionPlan(
    long Generation,
    bool Expanded,
    OptionExpansionAnimationKind AnimationKind,
    double FromHeight,
    double ToHeight,
    double FromOpacity,
    double ToOpacity,
    TimeSpan Duration);

internal sealed class OptionExpansionMotionCoordinator
{
    private long _generation;
    private long? _activeGeneration;

    public OptionExpansionMotionMode Mode { get; private set; } = OptionExpansionMotionMode.Normal;

    public bool ChangeMode(OptionExpansionMotionMode mode)
    {
        if (Mode == mode)
        {
            return false;
        }

        Mode = mode;
        _activeGeneration = null;
        _generation++;
        return true;
    }

    public OptionExpansionTransitionPlan BeginTransition(
        bool expanded,
        double currentHeight,
        double currentOpacity,
        double expandedHeight)
    {
        var boundedExpandedHeight = Math.Max(0, expandedHeight);
        var fromHeight = Math.Clamp(currentHeight, 0, boundedExpandedHeight);
        var fromOpacity = Math.Clamp(currentOpacity, 0, 1);
        var generation = ++_generation;
        var animationKind = Mode switch
        {
            OptionExpansionMotionMode.Normal => OptionExpansionAnimationKind.HeightAndOpacity,
            OptionExpansionMotionMode.Reduced => OptionExpansionAnimationKind.OpacityOnly,
            _ => OptionExpansionAnimationKind.Immediate,
        };
        var duration = Mode switch
        {
            OptionExpansionMotionMode.Normal => TimeSpan.FromMilliseconds(180),
            OptionExpansionMotionMode.Reduced => TimeSpan.FromMilliseconds(120),
            _ => TimeSpan.Zero,
        };

        _activeGeneration = animationKind == OptionExpansionAnimationKind.Immediate
            ? null
            : generation;

        return new OptionExpansionTransitionPlan(
            generation,
            expanded,
            animationKind,
            fromHeight,
            expanded ? boundedExpandedHeight : 0,
            fromOpacity,
            expanded ? 1 : 0,
            duration);
    }

    public bool TryComplete(long generation)
    {
        if (_activeGeneration != generation)
        {
            return false;
        }

        _activeGeneration = null;
        return true;
    }
}
