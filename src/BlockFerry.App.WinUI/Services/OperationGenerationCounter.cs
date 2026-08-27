namespace BlockFerry.App.WinUI.Services;

internal sealed class OperationGenerationCounter
{
    private long _current;

    public long Current => Interlocked.Read(ref _current);

    public long Next() => Interlocked.Increment(ref _current);
}
