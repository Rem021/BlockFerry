using BlockFerry.Core.System;

namespace BlockFerry.TestSupport;

public sealed class FakeEnvironmentPaths : IEnvironmentPaths
{
    public string? RoamingAppData { get; init; }
    public string? LocalAppData { get; init; }
    public string? UserProfile { get; init; }
    public string? UserDesktop { get; init; }
    public string? PublicDesktop { get; init; }
    public IReadOnlyList<string> StartMenuRoots { get; init; } = [];
}
