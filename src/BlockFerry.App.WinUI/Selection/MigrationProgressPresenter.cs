using BlockFerry.Core.Transactions;

namespace BlockFerry.App.WinUI.Selection;

internal sealed record MigrationProgressPresentation(
    double Percent,
    bool IsIndeterminate,
    string StageText,
    string DetailText);

internal sealed class MigrationProgressAccumulator
{
    private double highWaterMark;

    internal double Current => highWaterMark;

    internal double Advance(double proposedPercent)
    {
        highWaterMark = Math.Max(highWaterMark, Math.Clamp(proposedPercent, 0, 100));
        return highWaterMark;
    }

    internal void Reset() => highWaterMark = 0;
}

internal static class ContinuousMotionPolicy
{
    internal static bool Allows(bool active, bool animationsEnabled, bool highContrast) =>
        active && animationsEnabled && !highContrast;
}

internal static class MigrationProgressPresenter
{
    internal static MigrationProgressPresentation Create(MigrationProgress? progress, string fallbackDetail)
    {
        if (progress is null)
        {
            return new MigrationProgressPresentation(0, true, "正在准备", fallbackDetail);
        }

        var isIndeterminate = progress.TotalSteps <= 0;
        var percent = isIndeterminate
            ? 0
            : Math.Clamp(progress.CompletedSteps * 100d / progress.TotalSteps, 0, 100);
        var stage = progress.Stage switch
        {
            MigrationProgressStage.Revalidating => "重新验证实例",
            MigrationProgressStage.CheckingRunningGames => "检查 Minecraft 进程",
            MigrationProgressStage.PreparingBackup => "准备安全备份",
            MigrationProgressStage.BackingUp => "正在创建备份",
            MigrationProgressStage.Staging => "准备待写入文件",
            MigrationProgressStage.Committing => "提交文件",
            MigrationProgressStage.Verifying => "复读验证目标文件",
            MigrationProgressStage.RollingBack => "安全回滚",
            MigrationProgressStage.CleaningUp => "完成清理",
            MigrationProgressStage.Completed => "同步完成",
            MigrationProgressStage.Blocked => "操作被阻止",
            _ => "正在安全处理",
        };
        return new MigrationProgressPresentation(percent, isIndeterminate, stage, progress.Message);
    }
}
