using BlockFerry.App.WinUI.Selection;
using BlockFerry.Core.Content;
using Microsoft.UI.Xaml.Controls;

namespace BlockFerry.App.WinUI.Controls;

public sealed partial class MigrationReviewControl : UserControl
{
    public MigrationReviewControl()
    {
        InitializeComponent();
        ReviewGroupsItemsControl.ItemsSource = Array.Empty<MigrationReviewGroupViewModel>();
    }

    internal void Bind(IEnumerable<ContentPlanItem> items) =>
        ReviewGroupsItemsControl.ItemsSource = MigrationReviewPresenter.Build(items);

    internal void BindPreview(IEnumerable<string> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        var rows = summaries
            .Take(ContentContractLimits.MaximumCatalogItems + 1)
            .Select((summary, index) =>
            {
                var safeSummary = ContentUiText.Sanitize(summary, 180);
                return new MigrationReviewItemViewModel(
                    $"原版设置 · 变更 {index + 1}",
                    safeSummary,
                    ContentUiText.Sanitize($"原版设置，{safeSummary}", 320));
            })
            .ToArray();
        if (rows.Length > ContentContractLimits.MaximumCatalogItems)
        {
            throw new ArgumentException("The preview review set exceeded its fixed bound.", nameof(summaries));
        }

        ReviewGroupsItemsControl.ItemsSource = rows.Length == 0
            ? Array.Empty<MigrationReviewGroupViewModel>()
            :
            [
                new MigrationReviewGroupViewModel(
                    "计划变更",
                    "\uE777",
                    isActionable: true,
                    Array.AsReadOnly(rows)),
            ];
    }

    internal void Clear() =>
        ReviewGroupsItemsControl.ItemsSource = Array.Empty<MigrationReviewGroupViewModel>();
}
