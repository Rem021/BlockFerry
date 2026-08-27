using BlockFerry.Core.Pcl2;

namespace BlockFerry.App.WinUI.Discovery;

internal static class DiscoveryUiText
{
    internal static string FormatDiagnostic(Pcl2Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var severity = diagnostic.Severity switch
        {
            Pcl2DiagnosticSeverity.Info => "提示",
            Pcl2DiagnosticSeverity.Warning => "注意",
            _ => "错误",
        };

        return $"{severity}：{FriendlyMessage(diagnostic.Code)}（{diagnostic.Code}）";
    }

    internal static string FormatPreviewLocations(
        string? sourceOptionsPath,
        string? targetOptionsPath)
    {
        if (sourceOptionsPath is null && targetOptionsPath is null)
        {
            return "演示数据：纯内存目录与预览，不包含文件路径。";
        }

        var source = sourceOptionsPath is null
            ? "来源设置文件未找到"
            : "来源实例内的 options.txt 已验证";
        var target = targetOptionsPath is null
            ? "目标设置文件尚不存在"
            : "目标实例内的 options.txt 已验证";
        return $"来源：{source}\n目标：{target}\n完整路径已隐藏。";
    }

    private static string FriendlyMessage(Pcl2DiagnosticCode code) => code switch
    {
        Pcl2DiagnosticCode.Pcl2NotFound or
        Pcl2DiagnosticCode.NoVersionInstances =>
            "没有找到可用的 PCL2 隔离实例。",
        Pcl2DiagnosticCode.DiscoveryLimitReached =>
            "已达到安全探测上限，剩余位置没有继续检查。",
        Pcl2DiagnosticCode.CandidateEnumerationFailed or
        Pcl2DiagnosticCode.VersionsDirectoryUnreadable or
        Pcl2DiagnosticCode.PclIniReadFailed or
        Pcl2DiagnosticCode.SetupReadFailed or
        Pcl2DiagnosticCode.IsolationEvidenceUnreadable =>
            "有一处启动器信息暂时无法读取。",
        Pcl2DiagnosticCode.CandidatePathInvalid or
        Pcl2DiagnosticCode.MinecraftRootInvalid or
        Pcl2DiagnosticCode.ReparsePointRejected or
        Pcl2DiagnosticCode.PathOutsideMinecraftRoot or
        Pcl2DiagnosticCode.MultipleMinecraftRoots or
        Pcl2DiagnosticCode.GameRootInvalid or
        Pcl2DiagnosticCode.GameRootUnresolved or
        Pcl2DiagnosticCode.UnsupportedGameRootVolume =>
            "为安全起见，已跳过一个无效或不受支持的位置。",
        Pcl2DiagnosticCode.NonIsolatedInstance or
        Pcl2DiagnosticCode.IsolationSettingMissing or
        Pcl2DiagnosticCode.IsolationSettingUnknown or
        Pcl2DiagnosticCode.IsolationSettingConflict or
        Pcl2DiagnosticCode.IsolationInferredFromContent =>
            "某个实例的文件夹隔离状态无法安全确认。",
        Pcl2DiagnosticCode.SourceOptionsMissing =>
            "来源实例没有可迁移的 options.txt。",
        Pcl2DiagnosticCode.TargetOptionsMissing =>
            "目标实例尚未生成 options.txt；预览会按安全缺失处理。",
        Pcl2DiagnosticCode.OptionsReadFailed or
        Pcl2DiagnosticCode.OptionsSnapshotChanged =>
            "设置文件无法稳定读取，请关闭游戏后重试。",
        Pcl2DiagnosticCode.SameSourceAndTarget =>
            "来源和目标不能是同一个实例。",
        Pcl2DiagnosticCode.DiscoverySessionInactive or
        Pcl2DiagnosticCode.DiscoveryGenerationMismatch or
        Pcl2DiagnosticCode.DiscoveryProofInvalid or
        Pcl2DiagnosticCode.DiscoveryInstanceUnavailable or
        Pcl2DiagnosticCode.DiscoveryRootStale =>
            "实例状态已经变化，请重新探测后再继续。",
        _ => "某个实例没有通过完整的安全检查。",
    };
}
