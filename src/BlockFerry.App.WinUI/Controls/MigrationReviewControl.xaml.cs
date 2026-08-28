using BlockFerry.App.WinUI.Localization;
using BlockFerry.App.WinUI.Selection;
using BlockFerry.Core.Content;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace BlockFerry.App.WinUI.Controls;

public sealed partial class MigrationReviewControl : UserControl
{
    public MigrationReviewControl()
    {
        InitializeComponent();
        ReviewGroupsItemsControl.ItemsSource = Array.Empty<MigrationReviewGroupViewModel>();
    }

    internal void Bind(IEnumerable<ContentPlanItem> items)
    {
        ReviewGroupsItemsControl.ItemsSource = MigrationReviewPresenter.Build(items);
        QueueLocalization();
    }

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
                    [
                        new MigrationReviewBundleViewModel(
                            "原版设置",
                            "按类别汇总 · 展开查看具体键值",
                            Array.AsReadOnly(rows)),
                    ]),
            ];
        QueueLocalization();
    }

    internal void Clear() =>
        ReviewGroupsItemsControl.ItemsSource = Array.Empty<MigrationReviewGroupViewModel>();

    private void ReviewBundleExpander_Expanding(object sender, ExpanderExpandingEventArgs e)
    {
        if (sender is not Expander expander ||
            expander.DataContext is not MigrationReviewBundleViewModel bundle ||
            DetailItemsControl(expander) is not { } details)
        {
            return;
        }

        details.ItemsSource = bundle.Items;
        UiText.ApplyToVisualTree(expander);
        var revision = UiText.Revision;
        _ = DispatcherQueue?.TryEnqueue(() =>
        {
            if (revision == UiText.Revision && expander.IsExpanded)
            {
                UiText.ApplyToVisualTree(expander);
            }
        });
    }

    private void ReviewBundleExpander_Collapsed(object sender, ExpanderCollapsedEventArgs e)
    {
        if (sender is Expander expander && DetailItemsControl(expander) is { } details)
        {
            details.ItemsSource = null;
        }
    }

    private static ItemsRepeater? DetailItemsControl(Expander expander) =>
        (expander.Content as Border)?.Child as ItemsRepeater;

    private void QueueLocalization()
    {
        UiText.ApplyToVisualTree(this);
        var revision = UiText.Revision;
        _ = DispatcherQueue?.TryEnqueue(() =>
        {
            if (revision == UiText.Revision)
            {
                UiText.ApplyToVisualTree(this);
                _ = DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    if (revision == UiText.Revision)
                    {
                        UiText.ApplyToVisualTree(this);
                    }
                });
            }
        });
    }
}
