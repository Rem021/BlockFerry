// UI projection types stay independent of the PCL parser implementation.
namespace BlockFerry.App.WinUI;

/// <summary>
/// UI-only projection point for a future read-only launcher adapter.
/// The native shell intentionally does not call Minecraft or PCL APIs by itself.
/// </summary>
public sealed record MigrationViewState(
    string ModeLabel,
    string MinecraftRoot,
    string SourceVersion,
    string TargetVersion,
    string SourceInstance,
    string TargetInstance,
    string PackName,
    string LauncherName,
    bool IsDemo,
    bool CanStart)
{
    public static MigrationViewState AwaitingDiscovery { get; } = new(
        ModeLabel: "等待发现实例",
        MinecraftRoot: "尚未选择（未访问 Minecraft/PCL 文件）",
        SourceVersion: "未选择",
        TargetVersion: "未选择",
        SourceInstance: "未选择来源",
        TargetInstance: "未选择目标",
        PackName: "等待发现实例",
        LauncherName: "PCL 2",
        IsDemo: false,
        CanStart: false);

    public static MigrationViewState Demo { get; } = new(
        ModeLabel: "演示数据 · 只读预览",
        MinecraftRoot: "尚未连接（未访问磁盘）",
        SourceVersion: "r19",
        TargetVersion: "r20",
        SourceInstance: "All the Mods 10 r19（演示）",
        TargetInstance: "All the Mods 10 r20（演示）",
        PackName: "All the Mods 10",
        LauncherName: "PCL 2",
        IsDemo: true,
        CanStart: true);
}

internal static class DiscoveryEntryVisibilityPolicy
{
    internal static bool IsVisible(MigrationViewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return true;
    }
}

internal static class MigrationViewCopy
{
    internal static string DrawerHeaderStatus(MigrationViewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.CanStart)
        {
            return $"{state.ModeLabel} · 0 写入";
        }

        return state.IsDemo
            ? $"{state.ModeLabel} · 0 写入"
            : "真实实例 · 选择后可安全同步";
    }
}

public enum SyncPresentationState
{
    Idle,
    Running,
    Completed,
    Blocked,
}
