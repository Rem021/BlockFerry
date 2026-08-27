namespace BlockFerry.Core.System;

public interface IEnvironmentPaths
{
    string? RoamingAppData { get; }
    string? LocalAppData { get; }
    string? UserProfile { get; }
    string? UserDesktop { get; }
    string? PublicDesktop { get; }
    IReadOnlyList<string> StartMenuRoots { get; }
}
