namespace BlockFerry.Core.System;

internal interface IClock
{
    DateTimeOffset UtcNow { get; }
}

internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
