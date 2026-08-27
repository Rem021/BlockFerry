using BlockFerry.Core.Transactions;

namespace BlockFerry.TestSupport;

internal sealed class ScriptedFaultInjector : IFaultInjector
{
    private readonly MigrationFaultPoint? failurePoint;
    private readonly Action<MigrationFaultPoint>? observer;
    private int fired;

    internal ScriptedFaultInjector(
        MigrationFaultPoint? failurePoint = null,
        Action<MigrationFaultPoint>? observer = null)
    {
        this.failurePoint = failurePoint;
        this.observer = observer;
    }

    internal IReadOnlyList<MigrationFaultPoint> Observed
    {
        get
        {
            lock (observed)
            {
                return observed.ToArray();
            }
        }
    }

    private List<MigrationFaultPoint> observed { get; } = [];

    public void Hit(MigrationFaultPoint point)
    {
        lock (observed)
        {
            observed.Add(point);
        }

        observer?.Invoke(point);
        if (failurePoint == point && Interlocked.Exchange(ref fired, 1) == 0)
        {
            throw new IOException($"Injected transaction fault at {point}.");
        }
    }
}
