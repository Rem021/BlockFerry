using System.Collections.ObjectModel;
using BlockFerry.Core.Content;

namespace BlockFerry.App.WinUI.Selection;

internal sealed class MigrationReviewItemViewModel(
    string title,
    string summary,
    string automationName)
{
    public string Title { get; } = title;

    public string Summary { get; } = summary;

    public string AutomationName { get; } = automationName;
}

internal sealed class MigrationReviewGroupViewModel(
    string title,
    string glyph,
    bool isActionable,
    IReadOnlyList<MigrationReviewItemViewModel> items)
{
    public string Title { get; } = title;

    public string Glyph { get; } = glyph;

    public bool IsActionable { get; } = isActionable;

    public IReadOnlyList<MigrationReviewItemViewModel> Items { get; } = items;

    public int Count => Items.Count;

    public string CountText => $"{Count} 项";

    public string AutomationName => $"{Title}，{CountText}";
}

internal static class MigrationReviewPresenter
{
    private static readonly PlannedContentDisposition[] GroupOrder =
    [
        PlannedContentDisposition.Add,
        PlannedContentDisposition.Update,
        PlannedContentDisposition.Same,
        PlannedContentDisposition.Unselected,
        PlannedContentDisposition.Protected,
        PlannedContentDisposition.Unsupported,
        PlannedContentDisposition.Conflict,
        PlannedContentDisposition.Skipped,
    ];

    internal static IReadOnlyList<MigrationReviewGroupViewModel> Build(
        IEnumerable<ContentPlanItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var copy = items.Take(ContentContractLimits.MaximumCatalogItems + 1).ToArray();
        if (copy.Length > ContentContractLimits.MaximumCatalogItems || copy.Any(item => item is null))
        {
            throw new ArgumentException("The review item set exceeded its fixed bound.", nameof(items));
        }

        var groups = new List<MigrationReviewGroupViewModel>(GroupOrder.Length);
        foreach (var disposition in GroupOrder)
        {
            var rows = copy
                .Where(item => item.Disposition == disposition)
                .OrderBy(item => AdapterOrder(item.Id.AdapterId))
                .ThenBy(item => item.Id.TechnicalKey, StringComparer.Ordinal)
                .Select(item => CreateRow(disposition, item))
                .ToArray();
            if (rows.Length == 0)
            {
                continue;
            }

            var presentation = GroupPresentation(disposition);
            groups.Add(new MigrationReviewGroupViewModel(
                presentation.Title,
                presentation.Glyph,
                disposition is PlannedContentDisposition.Add or PlannedContentDisposition.Update ||
                disposition == PlannedContentDisposition.Conflict &&
                rows.Any(row => row.Title.Contains("采用来源", StringComparison.Ordinal)),
                new ReadOnlyCollection<MigrationReviewItemViewModel>(rows)));
        }

        return new ReadOnlyCollection<MigrationReviewGroupViewModel>(groups);
    }

    private static MigrationReviewItemViewModel CreateRow(
        PlannedContentDisposition disposition,
        ContentPlanItem item)
    {
        var adapter = item.Id.AdapterId switch
        {
            "vanilla" => "原版设置",
            "appearance" => "界面外观",
            "jei" => "JEI 收藏",
            "esm" => "静音规则",
            _ => "其他设置",
        };
        var action = disposition == PlannedContentDisposition.Conflict
            ? item.Resolution switch
            {
                ConflictResolution.UseSource => "采用来源",
                ConflictResolution.KeepTarget => "保留目标",
                _ => "跳过冲突",
            }
            : GroupPresentation(disposition).Title;
        var title = $"{adapter} · {action}";
        var summary = ContentUiText.Sanitize(item.Summary, 180);
        if (string.IsNullOrEmpty(summary))
        {
            summary = "无可显示的详细值";
        }

        return new MigrationReviewItemViewModel(
            title,
            summary,
            ContentUiText.Sanitize($"{title}，{summary}", 320));
    }

    private static (string Title, string Glyph) GroupPresentation(
        PlannedContentDisposition disposition) => disposition switch
        {
            PlannedContentDisposition.Add => ("新增", "\uE710"),
            PlannedContentDisposition.Update => ("更新", "\uE777"),
            PlannedContentDisposition.Same => ("相同", "\uE73E"),
            PlannedContentDisposition.Unselected => ("未选择", "\uE711"),
            PlannedContentDisposition.Protected => ("受保护", "\uE72E"),
            PlannedContentDisposition.Unsupported => ("不支持", "\uE783"),
            PlannedContentDisposition.Conflict => ("冲突处理", "\uE814"),
            PlannedContentDisposition.Skipped => ("已跳过", "\uE73A"),
            _ => throw new ArgumentOutOfRangeException(nameof(disposition)),
        };

    private static int AdapterOrder(string adapterId) => adapterId switch
    {
        "vanilla" => 0,
        "appearance" => 1,
        "jei" => 2,
        "esm" => 3,
        _ => 4,
    };
}
