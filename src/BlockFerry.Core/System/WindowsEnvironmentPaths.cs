namespace BlockFerry.Core.System;

public sealed class WindowsEnvironmentPaths : IEnvironmentPaths
{
    public string? RoamingAppData => ReadFolder(Environment.SpecialFolder.ApplicationData);
    public string? LocalAppData => ReadFolder(Environment.SpecialFolder.LocalApplicationData);
    public string? UserProfile => ReadFolder(Environment.SpecialFolder.UserProfile);
    public string? UserDesktop => ReadFolder(Environment.SpecialFolder.DesktopDirectory);
    public string? PublicDesktop => ReadFolder(Environment.SpecialFolder.CommonDesktopDirectory);

    public IReadOnlyList<string> StartMenuRoots =>
        new[]
        {
            ReadFolder(Environment.SpecialFolder.StartMenu),
            ReadFolder(Environment.SpecialFolder.CommonStartMenu),
        }
        .Where(path => path is not null)
        .Cast<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string? ReadFolder(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }
}
