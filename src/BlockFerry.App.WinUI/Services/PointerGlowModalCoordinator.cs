using BlockFerry.App.WinUI;

namespace BlockFerry.App.WinUI.Services;

internal readonly record struct PointerGlowDecision(
    bool RecordTarget,
    bool InitializeAtTarget,
    bool StartFollow,
    bool StopFollow,
    bool RevealGlow,
    bool HideGlow);

internal sealed class PointerGlowModalCoordinator
{
    private static readonly PointerGlowDecision HiddenDecision = new(
        RecordTarget: false,
        InitializeAtTarget: false,
        StartFollow: false,
        StopFollow: true,
        RevealGlow: false,
        HideGlow: true);

    private static readonly PointerGlowDecision SuppressedPositionDecision = new(
        RecordTarget: true,
        InitializeAtTarget: false,
        StartFollow: false,
        StopFollow: true,
        RevealGlow: false,
        HideGlow: true);

    private bool _isModal;
    private bool _pointerIsInside;

    public bool AwaitingFreshPointerInput { get; private set; } = true;

    public bool AllowsGlow => !_isModal && !AwaitingFreshPointerInput;

    public PointerGlowDecision OnDrawerPhaseChanged(DrawerModalPhase phase)
    {
        _isModal = phase != DrawerModalPhase.Collapsed;
        AwaitingFreshPointerInput = phase == DrawerModalPhase.Collapsed;
        return HiddenDecision;
    }

    public PointerGlowDecision OnPointerMoved() => OnPositionInput(isPointerEntered: false);

    public PointerGlowDecision OnPointerEntered() => OnPositionInput(isPointerEntered: true);

    public PointerGlowDecision OnPointerExited()
    {
        _pointerIsInside = false;
        return HiddenDecision;
    }

    private PointerGlowDecision OnPositionInput(bool isPointerEntered)
    {
        var wasInside = _pointerIsInside;
        _pointerIsInside = true;

        if (_isModal)
        {
            return SuppressedPositionDecision;
        }

        if (AwaitingFreshPointerInput)
        {
            AwaitingFreshPointerInput = false;
            return new PointerGlowDecision(
                RecordTarget: true,
                InitializeAtTarget: true,
                StartFollow: false,
                StopFollow: false,
                RevealGlow: true,
                HideGlow: false);
        }

        return new PointerGlowDecision(
            RecordTarget: true,
            InitializeAtTarget: false,
            StartFollow: true,
            StopFollow: false,
            RevealGlow: isPointerEntered || !wasInside,
            HideGlow: false);
    }
}
