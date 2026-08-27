using BlockFerry.Core.Options;
using BlockFerry.Core.Pcl2;

namespace BlockFerry.App.WinUI.Selection;

internal static class DemoOptionsSelectionData
{
    public static OptionsSelectionCatalog CreateCatalog() => new(
        [
            new OptionSettingDescriptor(
                "lang",
                "语言",
                "lang",
                OptionSettingCategory.LanguageAndInterface,
                "zh_cn",
                "en_us"),
            new OptionSettingDescriptor(
                "key_key.jump",
                "跳跃",
                "key_key.jump",
                OptionSettingCategory.Controls,
                "key.keyboard.space",
                "key.keyboard.j"),
            new OptionSettingDescriptor(
                "soundCategory_music",
                "音乐音量",
                "soundCategory_music",
                OptionSettingCategory.SoundAndDisplay,
                "0.8",
                "0.2"),
            new OptionSettingDescriptor(
                "futureOption",
                "其他玩家设置",
                "futureOption",
                OptionSettingCategory.OtherPlayerSettings,
                "enabled",
                "disabled"),
        ],
        [],
        [
            new OptionsMergeItem(
                "resourcePacks",
                "[\"vanilla\",\"file/source-pack.zip\"]",
                "[\"vanilla\",\"file/target-pack.zip\"]",
                "[\"vanilla\",\"file/target-pack.zip\"]",
                OptionsMergeDecision.PreserveTarget,
                "Safety-protected target-owned setting."),
        ],
        [
            new OptionsMergeItem(
                "fullscreenResolution",
                null,
                "1920x1080@60:24",
                "1920x1080@60:24",
                OptionsMergeDecision.PreserveTargetOnly,
                "Target-only setting remains unchanged."),
        ]);

    public static Pcl2SelectedOptionsPreview CreatePreview(IReadOnlySet<string> selectedKeys)
    {
        ArgumentNullException.ThrowIfNull(selectedKeys);

        var catalog = CreateCatalog();
        var planned = catalog.SelectableDifferences
            .Where(item => selectedKeys.Contains(item.Key))
            .Select(item => new OptionsMergeItem(
                item.Key,
                item.SourceValue,
                item.TargetValue,
                item.SourceValue,
                OptionsMergeDecision.UseSource,
                "Selected for the in-memory demo preview."))
            .ToArray();
        var skipped = catalog.SelectableDifferences
            .Where(item => !selectedKeys.Contains(item.Key))
            .Select(item => new OptionsMergeItem(
                item.Key,
                item.SourceValue,
                item.TargetValue,
                item.TargetValue,
                OptionsMergeDecision.PreserveTarget,
                "Not selected for the in-memory demo preview."))
            .ToArray();

        return new Pcl2SelectedOptionsPreview(
            false,
            false,
            null,
            null,
            null,
            planned,
            skipped,
            catalog.ProtectedDifferences,
            catalog.TargetOnlyItems,
            Array.Empty<Pcl2Diagnostic>());
    }
}
