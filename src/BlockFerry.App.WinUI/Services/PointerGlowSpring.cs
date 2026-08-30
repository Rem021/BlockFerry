namespace BlockFerry.App.WinUI.Services;

internal static class PointerGlowSpring
{
    public static void Advance(
        ref double position,
        ref double velocity,
        double target,
        double elapsedSeconds,
        double angularFrequency,
        double dampingRatio)
    {
        if (elapsedSeconds <= 0)
        {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(angularFrequency);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dampingRatio, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(dampingRatio, 1);

        // Exact underdamped solution for a fixed target. It preserves velocity
        // when a pointer event retargets the spring and is refresh-rate invariant.
        var displacement = position - target;
        var dampedFrequency = angularFrequency * Math.Sqrt(1 - (dampingRatio * dampingRatio));
        var decay = Math.Exp(-dampingRatio * angularFrequency * elapsedSeconds);
        var phase = dampedFrequency * elapsedSeconds;
        var cosine = Math.Cos(phase);
        var sine = Math.Sin(phase);
        var coupledVelocity = velocity + (dampingRatio * angularFrequency * displacement);
        var nextDisplacement = decay *
            (displacement * cosine + ((coupledVelocity / dampedFrequency) * sine));

        velocity = decay *
            (velocity * cosine -
             (((dampingRatio * angularFrequency * velocity) +
               (angularFrequency * angularFrequency * displacement)) /
              dampedFrequency * sine));
        position = target + nextDisplacement;
    }
}
