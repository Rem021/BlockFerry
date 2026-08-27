using BlockFerry.Core.Options;
using Microsoft.UI.Xaml.Controls;

namespace BlockFerry.App.WinUI.Selection;

internal static class OptionCategoryPresentation
{
    internal static Symbol GetSymbol(OptionSettingCategory category) => category switch
    {
        OptionSettingCategory.LanguageAndInterface => Symbol.Globe,
        OptionSettingCategory.Controls => Symbol.Keyboard,
        OptionSettingCategory.SoundAndDisplay => Symbol.Volume,
        OptionSettingCategory.OtherPlayerSettings => Symbol.Setting,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    internal static string FormatSummary(
        OptionCategorySelectionState state,
        int selectedCount,
        int totalCount) =>
        state switch
        {
            OptionCategorySelectionState.Selected =>
                $"\u5DF2\u9009 \u00B7 {selectedCount}/{totalCount}",
            OptionCategorySelectionState.Partial =>
                $"\u5DF2\u9009 \u00B7 {selectedCount}/{totalCount}",
            OptionCategorySelectionState.Unselected => $"\u672A\u9009\u62E9 \u00B7 \u5171 {totalCount} \u9879",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
        };
}
