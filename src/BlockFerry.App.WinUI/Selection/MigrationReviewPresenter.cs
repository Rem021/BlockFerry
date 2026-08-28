using System.Collections.ObjectModel;
using BlockFerry.Core.Content;
using BlockFerry.Core.Options;

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

internal sealed class MigrationReviewBundleViewModel(
    string title,
    string summary,
    IReadOnlyList<MigrationReviewItemViewModel> items)
{
    public string Title { get; } = title;

    public string Summary { get; } = summary;

    public IReadOnlyList<MigrationReviewItemViewModel> Items { get; } = items;

    public int Count => Items.Count;

    public string CountText => $"{Count} 项";

    public string AutomationName => $"{Title}，{CountText}，{Summary}";
}

internal sealed class MigrationReviewGroupViewModel(
    string title,
    string glyph,
    bool isActionable,
    IReadOnlyList<MigrationReviewBundleViewModel> bundles)
{
    public string Title { get; } = title;

    public string Glyph { get; } = glyph;

    public bool IsActionable { get; } = isActionable;

    public IReadOnlyList<MigrationReviewBundleViewModel> Bundles { get; } = bundles;

    public int Count => Bundles.Sum(bundle => bundle.Count);

    public string CountText => $"{Count} 项";

    public string AutomationName => $"{Title}，{CountText}";
}

internal static class MigrationReviewPresenter
{
    private static readonly OptionSettingClassifier OptionClassifier = new();

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
            var dispositionItems = copy
                .Where(item => item.Disposition == disposition)
                .ToArray();
            if (dispositionItems.Length == 0)
            {
                continue;
            }

            var bundles = dispositionItems
                .GroupBy(BundleKey, StringComparer.Ordinal)
                .OrderBy(group => BundleOrder(group.First()))
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => CreateBundle(disposition, group.ToArray()))
                .ToArray();
            var presentation = GroupPresentation(disposition);
            groups.Add(new MigrationReviewGroupViewModel(
                presentation.Title,
                presentation.Glyph,
                disposition is PlannedContentDisposition.Add or PlannedContentDisposition.Update ||
                disposition == PlannedContentDisposition.Conflict &&
                dispositionItems.Any(item => item.Resolution == ConflictResolution.UseSource),
                new ReadOnlyCollection<MigrationReviewBundleViewModel>(bundles)));
        }

        return new ReadOnlyCollection<MigrationReviewGroupViewModel>(groups);
    }

    private static MigrationReviewBundleViewModel CreateBundle(
        PlannedContentDisposition disposition,
        ContentPlanItem[] items)
    {
        var first = items[0];
        var rows = items
            .OrderBy(item => item.Id.TechnicalKey, StringComparer.Ordinal)
            .Select((item, index) => CreateRow(disposition, item, index))
            .ToArray();
        var title = first.Id.AdapterId == "vanilla"
            ? OptionCategoryTitle(OptionClassifier.Classify(first.Id.TechnicalKey))
            : AdapterTitle(first.Id.AdapterId);
        var summary = first.Id.AdapterId switch
        {
            "vanilla" => "原版设置 · 展开查看具体键值",
            "appearance" => "界面主题与外观 · 展开查看详情",
            "jei" => "合成列表收藏 · 展开查看详情",
            "esm" => "静音与音量规则 · 展开查看详情",
            _ => "其他可迁移设置 · 展开查看详情",
        };

        return new MigrationReviewBundleViewModel(
            title,
            summary,
            new ReadOnlyCollection<MigrationReviewItemViewModel>(rows));
    }

    private static MigrationReviewItemViewModel CreateRow(
        PlannedContentDisposition disposition,
        ContentPlanItem item,
        int index)
    {
        var action = disposition == PlannedContentDisposition.Conflict
            ? item.Resolution switch
            {
                ConflictResolution.UseSource => "采用来源",
                ConflictResolution.KeepTarget => "保留目标",
                _ => "跳过冲突",
            }
            : GroupPresentation(disposition).Title;
        var title = CreateItemTitle(item, index);
        var summary = ContentUiText.Sanitize(item.Summary, 180);
        if (string.IsNullOrEmpty(summary))
        {
            summary = "无可显示的详细值";
        }

        return new MigrationReviewItemViewModel(
            title,
            summary,
            ContentUiText.Sanitize($"{title}，{action}，{summary}", 320));
    }

    private static string CreateItemTitle(ContentPlanItem item, int index)
    {
        var title = item.Id.AdapterId switch
        {
            "vanilla" => $"{OptionClassifier.GetDisplayName(item.Id.TechnicalKey)} · {OptionClassifier.GetDisplayKey(item.Id.TechnicalKey)}",
            "appearance" when item.Id.TechnicalKey == "dark-mode" => "深色模式",
            "appearance" => $"界面外观项 {index + 1}",
            "jei" => $"收藏项 {index + 1}",
            "esm" => $"静音规则 {index + 1}",
            _ => $"设置项 {index + 1}",
        };
        var safeTitle = ContentUiText.Sanitize(title, 180);
        return string.IsNullOrEmpty(safeTitle) || safeTitle == ContentUiText.HiddenTechnicalText
            ? $"{AdapterTitle(item.Id.AdapterId)}项 {index + 1}"
            : safeTitle;
    }

    private static string BundleKey(ContentPlanItem item) => item.Id.AdapterId == "vanilla"
        ? $"vanilla:{(int)OptionClassifier.Classify(item.Id.TechnicalKey)}"
        : item.Id.AdapterId;

    private static int BundleOrder(ContentPlanItem item) => item.Id.AdapterId == "vanilla"
        ? (int)OptionClassifier.Classify(item.Id.TechnicalKey)
        : 10 + AdapterOrder(item.Id.AdapterId);

    private static string AdapterTitle(string adapterId) => adapterId switch
    {
        "vanilla" => "原版设置",
        "appearance" => "界面外观",
        "jei" => "JEI 收藏",
        "esm" => "声音静音设置",
        _ => "其他设置",
    };

    private static string OptionCategoryTitle(OptionSettingCategory category) => category switch
    {
        OptionSettingCategory.LanguageAndInterface => "语言与界面",
        OptionSettingCategory.Controls => "按键与控制",
        OptionSettingCategory.SoundAndDisplay => "声音与显示",
        _ => "其他玩家设置",
    };

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
