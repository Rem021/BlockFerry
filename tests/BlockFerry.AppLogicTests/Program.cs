using BlockFerry.App.WinUI;
using BlockFerry.App.WinUI.Controls;
using BlockFerry.App.WinUI.Discovery;
using BlockFerry.App.WinUI.Localization;
using BlockFerry.App.WinUI.Selection;
using BlockFerry.App.WinUI.Services;
using BlockFerry.Core.Content;
using BlockFerry.Core.Pcl2;
using BlockFerry.Core.Options;
using BlockFerry.Core.Transactions;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

var testProjectDirectory = Path.GetDirectoryName(CurrentTestSource())!;
var repositoryRoot = Path.GetFullPath(Path.Combine(testProjectDirectory, "..", ".."));
XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
var applicationManifest = XDocument.Load(Path.Combine(
    repositoryRoot,
    "src",
    "BlockFerry.App.WinUI",
    "app.manifest"));
var requestedExecutionLevel = applicationManifest
    .Descendants()
    .SingleOrDefault(element => element.Name.LocalName == "requestedExecutionLevel");
Assert(requestedExecutionLevel is not null &&
       (string?)requestedExecutionLevel.Attribute("level") == "requireAdministrator" &&
       (string?)requestedExecutionLevel.Attribute("uiAccess") == "false",
    "The transaction build must request one explicit administrator launch so Windows security metadata can be preserved without a misleading late failure.");

var deferredSourcePathValid = ContentRelativePath.TryCreate(
        @"config\jei\world\server\source-runtime-name\bookmarks.json",
        out var deferredSourcePath,
        out _);
var deferredTargetPathValid = ContentRelativePath.TryCreate(
        @"config\jei\world\server\source-runtime-name\bookmarks.json",
        out var deferredTargetPath,
        out _);
Assert(deferredSourcePathValid && deferredTargetPathValid,
    "Deferred JEI test paths must be valid bounded relative paths.");
var deferredRecord = new DeferredJeiSyncRecord(
    "source-instance",
    "target-instance",
    new TransactionId(Guid.NewGuid()),
    DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
    [new DeferredJeiSeed(
        deferredSourcePath!,
        deferredTargetPath!,
        new string('A', 64))]);
var deferredPayload = DeferredJeiSyncPayloadCodec.Serialize([deferredRecord]);
var deferredRoundTrip = DeferredJeiSyncPayloadCodec.Parse(deferredPayload).Single();
Assert(deferredRoundTrip.SourceInstanceId == deferredRecord.SourceInstanceId &&
       deferredRoundTrip.TargetInstanceId == deferredRecord.TargetInstanceId &&
       deferredRoundTrip.OriginalTransactionId == deferredRecord.OriginalTransactionId &&
       deferredRoundTrip.Seeds.Single().SourceRelativePath.Equals(deferredSourcePath) &&
       deferredRoundTrip.Seeds.Single().ProvisionalTargetRelativePath.Equals(deferredTargetPath) &&
       deferredRoundTrip.Seeds.Single().SourceSha256 == new string('A', 64),
    "Deferred JEI records must survive strict bounded serialization without losing path or hash binding.");
var malformedDeferredPayload = deferredPayload
    .AsSpan(0, deferredPayload.Length - 1)
    .ToArray()
    .Concat(",\"Unexpected\":true}"u8.ToArray())
    .ToArray();
var deferredUnknownPropertyRejected = false;
try
{
    _ = DeferredJeiSyncPayloadCodec.Parse(malformedDeferredPayload);
}
catch (JsonException)
{
    deferredUnknownPropertyRejected = true;
}

Assert(deferredUnknownPropertyRejected,
    "Deferred JEI persistence must reject unknown or structurally altered payload fields.");
var appMarkup = XDocument.Load(Path.Combine(
    repositoryRoot,
    "src",
    "BlockFerry.App.WinUI",
    "App.xaml"));
var fontAssetPath = Path.Combine(
    repositoryRoot,
    "src",
    "BlockFerry.App.WinUI",
    "Assets",
    "Fonts",
    "NotoSansSC-Variable.ttf");
var fontLicensePath = Path.Combine(
    repositoryRoot,
    "src",
    "BlockFerry.App.WinUI",
    "Assets",
    "Fonts",
    "NotoSansSC-OFL.txt");
Assert(File.Exists(fontAssetPath) && new FileInfo(fontAssetPath).Length > 10_000_000,
    "The release must include the complete Noto Sans SC variable font, not a placeholder asset.");
Assert(File.Exists(fontLicensePath) && File.ReadAllText(fontLicensePath).Contains("SIL OPEN FONT LICENSE", StringComparison.Ordinal),
    "The Noto Sans SC OFL license must ship beside the font.");
var thirdPartyNotices = File.ReadAllText(Path.Combine(repositoryRoot, "THIRD-PARTY-NOTICES.txt"));
Assert(thirdPartyNotices.Contains("SIL OPEN FONT LICENSE Version 1.1", StringComparison.Ordinal) &&
       thirdPartyNotices.Contains("PERMISSION & CONDITIONS", StringComparison.Ordinal) &&
       thirdPartyNotices.Contains("TERMINATION", StringComparison.Ordinal),
    "The portable top-level notices must contain the complete, human-readable Noto Sans SC license.");
Assert(!File.Exists(Path.Combine(Path.GetDirectoryName(fontAssetPath)!, "MiSans-Bold.ttf")),
    "The retired MiSans asset must not remain in the distributable font folder.");
Assert(appMarkup.Descendants().Any(element =>
        (string?)element.Attribute(xamlNamespace + "Key") == "AppFontFamily" &&
        element.Value.Contains("NotoSansSC-Variable.ttf#Noto Sans SC", StringComparison.Ordinal)),
    "App.xaml must apply the bundled Noto Sans SC family.");

UiText.SetLanguage(UiLanguage.ChineseSimplified);
var initialLanguageRevision = UiText.Revision;
UiText.SetLanguage(UiLanguage.English);
var englishLanguageRevision = UiText.Revision;
UiText.SetLanguage(UiLanguage.English);
Assert(englishLanguageRevision == initialLanguageRevision + 1 &&
       UiText.Revision == englishLanguageRevision,
    "Language revisions must advance exactly once per real switch so queued passes cannot apply stale state.");
Assert(UiText.Translate("选择同步设置") == "Choose sync settings" &&
       UiText.Translate("已选 7 / 12 项设置") == "Selected 7 / 12 items" &&
       UiText.Translate("已生成 4 项计划变更 · 0 写入") ==
           "Created 4 planned changes · 0 writes" &&
       UiText.Translate("计划同步 4 项设置；这是只读预览，0 写入。") ==
           "Planned sync: 4 settings. This is a read-only preview with 0 writes." &&
       UiText.Translate("只读预览已完成，计划同步 4 项设置。") ==
           "Read-only preview complete. 4 settings are planned." &&
       UiText.Translate("当前步骤") == "Current step" &&
       UiText.Translate("已完成步骤") == "Completed step" &&
       UiText.Translate("待进行") == "Pending" &&
       UiText.Translate("计划变更，4 项") == "Planned changes, 4 items" &&
       UiText.Translate("原版设置，4 项，按类别汇总 · 展开查看具体键值") ==
           "Vanilla settings, 4 items, Grouped by category · expand for individual values" &&
       UiText.Translate("未选择 0 · 受保护 1 · 仅目标 1") ==
           "Unselected 0 · protected 1 · target-only 1" &&
       UiText.Translate("已检查 2 / 5 个还原点；备份 2 个文件") ==
           "Checked 2 / 5 restore points; backed up 2 files" &&
       UiText.Translate("已准备 3 / 5 个文件") == "Staged 3 / 5 files" &&
       UiText.Translate("已封存 4 / 5 个验证副本") == "Sealed 4 / 5 verification copies" &&
       UiText.Translate("已安全写入 5 / 5 个文件") == "Safely wrote 5 / 5 files" &&
       UiText.Translate("已验证完成 5 个文件") == "Verified 5 files",
    "English UI projection must cover static and count-based sync copy.");
var unknownProgress = MigrationProgressPresenter.Create(
    new MigrationProgress(
        MigrationProgressStage.Revalidating,
        0,
        0,
        "正在确认 PCL 已完成实例写入"),
    "fallback");
Assert(unknownProgress.IsIndeterminate &&
       Math.Abs(unknownProgress.Percent) < 0.01,
    "Unknown-duration safety waits must animate as indeterminate and must not invent a stage percentage.");
var halfwayProgress = MigrationProgressPresenter.Create(
    new MigrationProgress(MigrationProgressStage.Committing, 3, 6, "提交文件"),
    "fallback");
Assert(Math.Abs(halfwayProgress.Percent - 50) < 0.01 &&
       !halfwayProgress.IsIndeterminate &&
       halfwayProgress.StageText == "提交文件" &&
       UiText.Translate(halfwayProgress.StageText) == "Committing files",
    "The progress presenter must preserve source copy while exposing a real completed-step percentage for lightweight localization.");
var rollbackProgress = MigrationProgressPresenter.Create(
    new MigrationProgress(MigrationProgressStage.RollingBack, 0, 1, "安全回滚"),
    "fallback");
var monotonicProgress = new MigrationProgressAccumulator();
Assert(Math.Abs(monotonicProgress.Advance(halfwayProgress.Percent) - 50) < 0.01 &&
       Math.Abs(monotonicProgress.Advance(rollbackProgress.Percent) - 50) < 0.01 &&
       Math.Abs(monotonicProgress.Current - 50) < 0.01 &&
       Math.Abs(monotonicProgress.Advance(100) - 100) < 0.01 &&
       Math.Abs(monotonicProgress.Current - 100) < 0.01,
    "A rollback must never move the visible operation progress behind its established high-water mark.");
monotonicProgress.Reset();
Assert(Math.Abs(monotonicProgress.Advance(8) - 8) < 0.01,
    "A new operation must be able to reset and establish a fresh progress high-water mark.");
Assert(ContinuousMotionPolicy.Allows(active: true, animationsEnabled: true, highContrast: false) &&
       !ContinuousMotionPolicy.Allows(active: true, animationsEnabled: false, highContrast: false) &&
       !ContinuousMotionPolicy.Allows(active: true, animationsEnabled: true, highContrast: true) &&
       !ContinuousMotionPolicy.Allows(active: false, animationsEnabled: true, highContrast: false),
    "Continuous busy animation must stay disabled for reduced motion, high contrast, and inactive states.");
UiText.SetLanguage(UiLanguage.ChineseSimplified);
Assert(UiText.Translate("选择同步设置") == "选择同步设置" &&
       UiText.Revision == englishLanguageRevision + 1,
    "Switching back to Chinese must preserve the source UI copy.");
var uiTextSource = File.ReadAllText(Path.Combine(
    repositoryRoot,
    "src",
    "BlockFerry.App.WinUI",
    "Localization",
    "UiText.cs"));
Assert(uiTextSource.Contains("if (root is not UIElement)", StringComparison.Ordinal),
    "Localization traversal must never pass non-visual Run nodes to VisualTreeHelper.");
Assert(uiTextSource.Contains("AutomationProperties.GetItemStatus(value)", StringComparison.Ordinal) &&
       uiTextSource.Contains("AutomationProperties.SetItemStatus(value, text)", StringComparison.Ordinal),
    "Localization must project automation item status alongside name and help text.");

var localizedMainWindowMarkup = XDocument.Load(Path.Combine(
    repositoryRoot,
    "src",
    "BlockFerry.App.WinUI",
    "MainWindow.xaml"));
var languageButton = localizedMainWindowMarkup.Descendants().Single(element =>
    (string?)element.Attribute(xamlNamespace + "Name") == "LanguageButton");
var languageButtonText = localizedMainWindowMarkup.Descendants().Single(element =>
    (string?)element.Attribute(xamlNamespace + "Name") == "LanguageButtonText");
Assert(languageButton.Name.LocalName == "Button" &&
       (string?)languageButtonText.Parent?.Attribute("Height") == "48" &&
       (string?)languageButtonText.Attribute("HorizontalAlignment") == "Center" &&
       (string?)languageButtonText.Attribute("VerticalAlignment") == "Center" &&
       (string?)languageButtonText.Attribute("TextAlignment") == "Center",
    "The title bar must expose an in-app language switch.");
var titleBarChromeButtonStyle = appMarkup.Descendants().Single(element =>
    element.Name.LocalName == "Style" &&
    (string?)element.Attribute(xamlNamespace + "Key") == "TitleBarChromeButtonStyle");
var titleBarChromeButtonSetters = titleBarChromeButtonStyle
    .Elements()
    .Where(element => element.Name.LocalName == "Setter")
    .ToDictionary(
        element => (string)element.Attribute("Property")!,
        element => (string)element.Attribute("Value")!,
        StringComparer.Ordinal);
Assert((string?)languageButtonText.Attribute("Margin") == "0,-2,0,2" &&
       (string?)languageButton.Attribute("Style") ==
           "{StaticResource TitleBarChromeButtonStyle}" &&
       titleBarChromeButtonSetters.GetValueOrDefault("Width") == "46" &&
       titleBarChromeButtonSetters.GetValueOrDefault("Height") == "48" &&
       titleBarChromeButtonSetters.GetValueOrDefault("MinWidth") == "46" &&
       titleBarChromeButtonSetters.GetValueOrDefault("MinHeight") == "48",
    "TitleBarLanguageOpticalAlignment: the EN/中 label must move up 2 DIPs without resizing the 46-by-48 title-bar hit target.");
var mainPageMarkupForActivity = XDocument.Load(Path.Combine(
    repositoryRoot,
    "src",
    "BlockFerry.App.WinUI",
    "MainPage.xaml"));
foreach (var requiredName in new[]
         {
             "DiscoveryProgressRing",
             "DiscoveryProgressBar",
             "DrawerActivityRing",
             "DrawerProgressBar",
             "ExecutionActivityRing",
             "ExecutionProgressBar",
         })
{
    Assert(mainPageMarkupForActivity.Descendants().Any(element =>
            (string?)element.Attribute(xamlNamespace + "Name") == requiredName),
        $"The UI must contain the {requiredName} activity indicator.");
}

UiText.SetLanguage(UiLanguage.English);
var untranslatedStaticCopy = new SortedSet<string>(StringComparer.Ordinal);
var localizedMarkupPaths = new[]
{
    Path.Combine(repositoryRoot, "src", "BlockFerry.App.WinUI", "MainWindow.xaml"),
    Path.Combine(repositoryRoot, "src", "BlockFerry.App.WinUI", "MainPage.xaml"),
}.Concat(Directory.EnumerateFiles(
    Path.Combine(repositoryRoot, "src", "BlockFerry.App.WinUI", "Controls"),
    "*.xaml",
    SearchOption.TopDirectoryOnly));
foreach (var markupPath in localizedMarkupPaths)
{
    var markup = XDocument.Load(markupPath);
    var visibleStrings = markup.Descendants()
        .SelectMany(element => element.Attributes()
            .Where(attribute => !attribute.IsNamespaceDeclaration)
            .Select(attribute => attribute.Value)
            .Concat(element.Name.LocalName == "String" ? [element.Value] : []))
        .Where(value => value.Any(character => character is >= '\u3400' and <= '\u9fff'));
    foreach (var visibleString in visibleStrings)
    {
        if (UiText.Translate(visibleString).Any(character => character is >= '\u3400' and <= '\u9fff'))
        {
            _ = untranslatedStaticCopy.Add(visibleString);
        }
    }
}

Assert(untranslatedStaticCopy.Count == 0,
    $"Every static XAML string must have complete English copy: {string.Join(" | ", untranslatedStaticCopy)}");

var uiSourceRoot = Path.Combine(repositoryRoot, "src", "BlockFerry.App.WinUI");
var dynamicCopyFiles = new[]
{
    Path.Combine(uiSourceRoot, "MainWindow.xaml.cs"),
    Path.Combine(uiSourceRoot, "MainPage.xaml.cs"),
    Path.Combine(uiSourceRoot, "MainPage.Migration.cs"),
    Path.Combine(uiSourceRoot, "MigrationViewState.cs"),
    Path.Combine(uiSourceRoot, "Discovery", "DiscoveryUiText.cs"),
    Path.Combine(uiSourceRoot, "Services", "MigrationWorkflowCoordinator.cs"),
}.Concat(Directory.EnumerateFiles(Path.Combine(uiSourceRoot, "Selection"), "*.cs"))
 .Concat(Directory.EnumerateFiles(Path.Combine(uiSourceRoot, "Controls"), "*.cs"));
var untranslatedDynamicCopy = new SortedSet<string>(StringComparer.Ordinal);
var quotedChinese = new Regex("\\\"(?:\\\\.|[^\\\"\\\\])*[\\u3400-\\u9fff](?:\\\\.|[^\\\"\\\\])*\\\"",
    RegexOptions.CultureInvariant);
foreach (var copyFile in dynamicCopyFiles)
{
    foreach (Match match in quotedChinese.Matches(File.ReadAllText(copyFile)))
    {
        var sourceCopy = match.Value[1..^1];
        if (sourceCopy == "中")
        {
            continue;
        }

        if (UiText.Translate(sourceCopy).Any(character => character is >= '\u3400' and <= '\u9fff'))
        {
            _ = untranslatedDynamicCopy.Add(sourceCopy);
        }
    }
}

if (untranslatedDynamicCopy.Count > 0)
{
    Console.WriteLine($"UNTRANSLATED_DYNAMIC_COUNT={untranslatedDynamicCopy.Count}");
    foreach (var untranslated in untranslatedDynamicCopy.Take(100))
    {
        Console.WriteLine($"UNTRANSLATED_DYNAMIC={untranslated}");
    }
}

Assert(untranslatedDynamicCopy.Count == 0,
    "Every dynamic UI string must have complete English copy.");
UiText.SetLanguage(UiLanguage.ChineseSimplified);
var requiredCardResourceKeys = new[]
{
    "OptionCardStrokeBrush",
    "OptionCardSelectedStrokeBrush",
    "OptionCardIconSurfaceBrush",
    "OptionCardIconForegroundBrush",
    "OptionSettingPressedBrush",
    "OptionResultSuccessSurfaceBrush",
};
var themeCardResources = new Dictionary<(string Theme, string Key), XElement>();
foreach (var themeName in new[] { "Default", "Light", "HighContrast" })
{
    var themeMatches = appMarkup
        .Descendants()
        .Where(element =>
            element.Name.LocalName == "ResourceDictionary" &&
            (string?)element.Attribute(xamlNamespace + "Key") == themeName)
        .ToArray();
    var themeDictionary = themeMatches.Length <= 1 ? themeMatches.SingleOrDefault() : null;
    Assert(themeMatches.Length == 1 && themeDictionary is not null,
        $"App.xaml must contain exactly one {themeName} theme dictionary.");

    foreach (var resourceKey in requiredCardResourceKeys)
    {
        var resourceMatches = themeDictionary!
            .Elements()
            .Where(element => (string?)element.Attribute(xamlNamespace + "Key") == resourceKey)
            .ToArray();
        var resource = resourceMatches.Length <= 1 ? resourceMatches.SingleOrDefault() : null;
        Assert(resourceMatches.Length == 1 && resource is not null,
            $"The {themeName} theme must define {resourceKey} exactly once.");
        themeCardResources[(themeName, resourceKey)] = resource!;
    }
}

var expectedHighContrastCardColors = new Dictionary<string, string>
{
    ["OptionCardStrokeBrush"] = "{ThemeResource SystemColorWindowTextColor}",
    ["OptionCardSelectedStrokeBrush"] = "{ThemeResource SystemColorHighlightColor}",
    ["OptionCardIconSurfaceBrush"] = "{ThemeResource SystemColorWindowColor}",
    ["OptionCardIconForegroundBrush"] = "{ThemeResource SystemColorHighlightColor}",
    ["OptionSettingPressedBrush"] = "{ThemeResource SystemColorWindowColor}",
    ["OptionResultSuccessSurfaceBrush"] = "{ThemeResource SystemColorWindowColor}",
};
foreach (var (resourceKey, expectedColor) in expectedHighContrastCardColors)
{
    Assert((string?)themeCardResources[("HighContrast", resourceKey)].Attribute("Color") == expectedColor,
        $"High contrast {resourceKey} must use {expectedColor}.");
}

var highContrastMatches = appMarkup
    .Descendants()
    .Where(element =>
        element.Name.LocalName == "ResourceDictionary" &&
        (string?)element.Attribute(xamlNamespace + "Key") == "HighContrast")
    .ToArray();
var highContrastDictionary = highContrastMatches.Length <= 1
    ? highContrastMatches.SingleOrDefault()
    : null;
Assert(highContrastMatches.Length == 1 && highContrastDictionary is not null,
    "App.xaml must contain exactly one HighContrast theme dictionary.");
foreach (var existingSurfaceKey in new[]
         {
             "OptionCategorySelectedSurfaceBrush",
             "OptionSettingHoverBrush",
         })
{
    var surfaceMatches = highContrastDictionary!
        .Elements()
        .Where(element => (string?)element.Attribute(xamlNamespace + "Key") == existingSurfaceKey)
        .ToArray();
    var surface = surfaceMatches.Length <= 1 ? surfaceMatches.SingleOrDefault() : null;
    Assert(surfaceMatches.Length == 1 && surface is not null,
        $"High contrast must define {existingSurfaceKey} exactly once.");
    Assert((string?)surface!.Attribute("Color") == "{ThemeResource SystemColorWindowColor}",
        $"High contrast {existingSurfaceKey} must use the window surface instead of a highlight fill.");
}

var categoryMarkup = XDocument.Load(
    Path.Combine(AppContext.BaseDirectory, "UiContracts", "OptionCategoryControl.xaml"));
var categoryCheckBoxElement = categoryMarkup
    .Descendants()
    .Single(element => (string?)element.Attribute(xamlNamespace + "Name") == "CategoryCheckBox");
var categoryTitleElement = categoryMarkup
    .Descendants()
    .Single(element => (string?)element.Attribute(xamlNamespace + "Name") == "CategoryTitleText");
Assert(categoryTitleElement.Ancestors().Contains(categoryCheckBoxElement),
    "The category title must be inside the native checkbox hit target so clicking the label toggles the category.");
var categoryIconMatches = categoryMarkup
    .Descendants()
    .Where(element => (string?)element.Attribute(xamlNamespace + "Name") == "CategoryIcon")
    .ToArray();
var categoryIconElement = categoryIconMatches.Length <= 1 ? categoryIconMatches.SingleOrDefault() : null;
Assert(categoryIconMatches.Length == 1 && categoryIconElement is not null,
    "Each category card must contain exactly one named CategoryIcon.");
var categoryIconTileMatches = categoryMarkup
    .Descendants()
    .Where(element => (string?)element.Attribute(xamlNamespace + "Name") == "CategoryIconTile")
    .ToArray();
var categoryIconTileElement = categoryIconTileMatches.Length <= 1 ? categoryIconTileMatches.SingleOrDefault() : null;
Assert(categoryIconTileMatches.Length == 1 && categoryIconTileElement is not null,
    "Each category card must contain exactly one named CategoryIconTile.");
var categorySummaryMatches = categoryMarkup
    .Descendants()
    .Where(element => (string?)element.Attribute(xamlNamespace + "Name") == "CategorySummaryText")
    .ToArray();
var categorySummaryElement = categorySummaryMatches.Length <= 1 ? categorySummaryMatches.SingleOrDefault() : null;
Assert(categorySummaryMatches.Length == 1 && categorySummaryElement is not null,
    "Each category card must contain exactly one named CategorySummaryText.");
var categorySurfaceMatches = categoryMarkup
    .Descendants()
    .Where(element => (string?)element.Attribute(xamlNamespace + "Name") == "CategorySurface")
    .ToArray();
var categorySurfaceElement = categorySurfaceMatches.Length <= 1 ? categorySurfaceMatches.SingleOrDefault() : null;
Assert(categorySurfaceMatches.Length == 1 && categorySurfaceElement is not null,
    "Each category control must retain exactly one root CategorySurface.");
var disclosureMatches = categoryMarkup
    .Descendants()
    .Where(element => (string?)element.Attribute(xamlNamespace + "Name") == "DisclosureButton")
    .ToArray();
var disclosureElement = disclosureMatches.Length <= 1 ? disclosureMatches.SingleOrDefault() : null;
Assert(disclosureMatches.Length == 1 && disclosureElement is not null,
    "Each category card must retain exactly one DisclosureButton.");
var childrenRegionMatches = categoryMarkup
    .Descendants()
    .Where(element => (string?)element.Attribute(xamlNamespace + "Name") == "ChildrenRegion")
    .ToArray();
var childrenRegionElement = childrenRegionMatches.Length <= 1 ? childrenRegionMatches.SingleOrDefault() : null;
Assert(childrenRegionMatches.Length == 1 && childrenRegionElement is not null,
    "Each category card must retain exactly one ChildrenRegion.");

Assert(categoryIconElement!.Name.LocalName == "SymbolIcon" &&
       (string?)categoryIconElement.Attribute("Width") == "20" &&
       (string?)categoryIconElement.Attribute("Height") == "20" &&
       (string?)categoryIconElement.Attribute("AutomationProperties.AccessibilityView") == "Raw" &&
       (string?)categoryIconElement.Attribute("IsHitTestVisible") == "False",
    "CategoryIcon must be a decorative, non-hit-testable 20-DIP SymbolIcon.");
Assert((string?)categoryIconTileElement!.Attribute("Width") == "38" &&
       (string?)categoryIconTileElement.Attribute("Height") == "38" &&
       (string?)categoryIconTileElement.Attribute("AutomationProperties.AccessibilityView") == "Raw" &&
       (string?)categoryIconTileElement.Attribute("IsHitTestVisible") == "False",
    "CategoryIconTile must be a decorative, non-hit-testable 38-by-38 tile.");
Assert(categoryIconElement.Ancestors().Contains(categoryIconTileElement) &&
       categoryIconTileElement.Ancestors().Contains(categoryCheckBoxElement),
    "CategoryIcon and its tile must be descendants of the native category checkbox content.");
Assert(categoryTitleElement.Ancestors().Contains(categoryCheckBoxElement) &&
       categorySummaryElement!.Ancestors().Contains(categoryCheckBoxElement),
    "Category title and non-color-only summary must both be inside the native category checkbox content.");
Assert((string?)categorySurfaceElement!.Attribute("CornerRadius") == "16" &&
       (string?)categorySurfaceElement.Attribute("BorderThickness") == "1" &&
       (string?)categorySurfaceElement.Attribute("BorderBrush") == "{ThemeResource OptionCardStrokeBrush}" &&
       (string?)categorySurfaceElement.Attribute("Background") == "{ThemeResource OptionCategorySurfaceBrush}",
    "CategorySurface must be a 16-DIP, one-stroke category card.");
var selectedSurfaceMatches = categoryMarkup
    .Descendants()
    .Where(element => (string?)element.Attribute(xamlNamespace + "Name") == "SelectedSurface")
    .ToArray();
var selectedSurfaceElement = selectedSurfaceMatches.Length <= 1
    ? selectedSurfaceMatches.SingleOrDefault()
    : null;
Assert(selectedSurfaceMatches.Length == 1 && selectedSurfaceElement is not null,
    "Each category card must retain exactly one named SelectedSurface.");
Assert((string?)selectedSurfaceElement!.Attribute("BorderBrush") ==
           "{ThemeResource OptionCardSelectedStrokeBrush}" &&
       (string?)selectedSurfaceElement.Attribute("BorderThickness") == "1",
    "The selected category stroke must remain a ThemeResource-bound overlay across theme changes.");
var categoryContentMatches = categoryMarkup
    .Descendants()
    .Where(element => (string?)element.Attribute(xamlNamespace + "Name") == "CategoryContent")
    .ToArray();
var categoryContentElement = categoryContentMatches.Length <= 1
    ? categoryContentMatches.SingleOrDefault()
    : null;
Assert(categoryContentMatches.Length == 1 && categoryContentElement is not null,
    "Each category card must contain exactly one named CategoryContent container.");
Assert((string?)categorySurfaceElement.Attribute("Padding") == "0" &&
       (string?)categoryContentElement!.Attribute("Padding") == "16" &&
       selectedSurfaceElement.Parent == categoryContentElement.Parent &&
       categoryContentElement.Ancestors().Contains(categorySurfaceElement),
    "Category content padding must be inner content so the selected overlay covers the full outer card.");
var categoryRootContentMatches = categoryMarkup.Root?.Elements().ToArray() ?? [];
var categoryRootContent = categoryRootContentMatches.Length <= 1
    ? categoryRootContentMatches.SingleOrDefault()
    : null;
Assert(categoryRootContentMatches.Length == 1 && categoryRootContent == categorySurfaceElement,
    "CategorySurface must remain the root content border of OptionCategoryControl.");
Assert(disclosureElement!.Ancestors().Contains(categoryCheckBoxElement) is false &&
       ((string?)disclosureElement.Attribute("Width") is "40" or "44") &&
       ((string?)disclosureElement.Attribute("Height") is "40" or "44") &&
       (string?)disclosureElement.Attribute("IsExpanded") == "False",
    "DisclosureButton must remain a separate 40- or 44-DIP control and start collapsed.");
Assert((string?)childrenRegionElement!.Attribute("Visibility") == "Collapsed" &&
       (string?)childrenRegionElement.Attribute("Opacity") == "0" &&
       (string?)childrenRegionElement.Attribute("MaxHeight") == "0",
    "ChildrenRegion must start collapsed without construction-time animation.");

var categoryControlSource = File.ReadAllText(Path.Combine(
    repositoryRoot,
    "src",
    "BlockFerry.App.WinUI",
    "Controls",
    "OptionCategoryControl.xaml.cs"));
Assert(categoryControlSource.Contains("CategoryIcon.Symbol = viewModel.Symbol;", StringComparison.Ordinal),
    "OptionCategoryControl must consume the Task 3 symbol through viewModel.Symbol.");
Assert(categoryControlSource.Contains("CategorySummaryText.Text = _viewModel.SelectionSummary;", StringComparison.Ordinal),
    "Category selection changes must refresh CategorySummaryText from SelectionSummary.");
Assert(categoryControlSource.Contains(
        "AutomationProperties.SetName(CategoryCheckBox, $\"{_viewModel.Title}, {_viewModel.SelectionSummary}\");",
        StringComparison.Ordinal),
    "The native category checkbox automation name must include the title and current summary.");
Assert(!categoryControlSource.Contains("OptionSettingCategory.LanguageAndInterface", StringComparison.Ordinal) &&
       !categoryControlSource.Contains("OptionSettingCategory.Controls", StringComparison.Ordinal) &&
       !categoryControlSource.Contains("OptionSettingCategory.SoundAndDisplay", StringComparison.Ordinal) &&
       !categoryControlSource.Contains("OptionCategoryPresentation.GetSymbol", StringComparison.Ordinal),
    "OptionCategoryControl must not duplicate the Task 3 category-to-symbol mapping.");
Assert(!categoryControlSource.Contains("CategorySurface.BorderBrush =", StringComparison.Ordinal),
    "Selection refresh must not replace CategorySurface's ThemeResource brush with a concrete brush.");
var categoryBindBody = ExtractCSharpMethodBody(categoryControlSource, "internal void Bind(");
Assert(!categoryBindBody.Contains("BuildSettingControls(viewModel);", StringComparison.Ordinal) &&
       categoryControlSource.Contains("if (expanded)", StringComparison.Ordinal) &&
       categoryControlSource.Contains("EnsureSettingControls();", StringComparison.Ordinal),
    "Collapsed option categories must defer constructing individual setting rows until first expansion.");
var expandBeforeSettingFocus = categoryControlSource.IndexOf(
    "DisclosureButton.IsExpanded = true;",
    StringComparison.Ordinal);
var settingFocus = categoryControlSource.IndexOf(
    "return setting.CheckBox.Focus(FocusState.Programmatic);",
    StringComparison.Ordinal);
Assert(categoryControlSource.Contains("if (!DisclosureButton.IsExpanded)", StringComparison.Ordinal) &&
       expandBeforeSettingFocus >= 0 &&
       settingFocus > expandBeforeSettingFocus,
    "Restoring a setting focus token must expand the category before focusing the setting checkbox.");
var collapseFocusGuard = categoryControlSource.IndexOf(
    "if (!expanded && IsFocusWithinSettings())",
    StringComparison.Ordinal);
var disclosureFocus = categoryControlSource.IndexOf(
    "DisclosureButton.Focus(FocusState.Programmatic);",
    collapseFocusGuard,
    StringComparison.Ordinal);
Assert(collapseFocusGuard >= 0 && disclosureFocus > collapseFocusGuard,
    "Collapsing focused children must move focus to the disclosure control before hiding them.");
Assert(categoryControlSource.Contains("settingCheckBox.Content = rowSurface;", StringComparison.Ordinal),
    "Each setting checkbox must own rowSurface as its native content hit target.");
Assert(categoryControlSource.Contains(
        "HorizontalContentAlignment = HorizontalAlignment.Stretch",
        StringComparison.Ordinal) &&
       categoryControlSource.Contains(
           "HorizontalAlignment = HorizontalAlignment.Stretch",
           StringComparison.Ordinal),
    "Each setting checkbox must stretch itself and its content across the available row width.");
Assert(categoryControlSource.Contains("Child = labels", StringComparison.Ordinal) &&
       categoryControlSource.Contains("ChildrenPanel.Children.Add(settingCheckBox);", StringComparison.Ordinal),
    "The full-row setting checkbox must contain both visible label lines and be added directly to ChildrenPanel.");
Assert(!categoryControlSource.Contains("rowGrid.Children.Add(labels);", StringComparison.Ordinal) &&
       !categoryControlSource.Contains("ChildrenPanel.Children.Add(row);", StringComparison.Ordinal),
    "Setting rows must not retain the old sibling-label grid or outer pseudo-clickable row.");
Assert(categoryControlSource.Contains(
        "AutomationProperties.SetName(settingCheckBox, setting.DisplayName);",
        StringComparison.Ordinal) &&
       categoryControlSource.Contains(
           "AutomationProperties.SetHelpText(settingCheckBox, setting.EscapedTechnicalKey);",
           StringComparison.Ordinal) &&
       categoryControlSource.Contains(
           "ToolTipService.SetToolTip(settingCheckBox, setting.EscapedTechnicalKey);",
           StringComparison.Ordinal),
    "The native setting checkbox must retain its friendly automation name and complete escaped-key help and tooltip text.");
var configureAccessibilityStart = categoryControlSource.IndexOf(
    "internal void ConfigureAccessibility(bool animationsEnabled, bool highContrast)",
    StringComparison.Ordinal);
var captureFocusStart = categoryControlSource.IndexOf(
    "internal OptionsSelectionFocusToken? CaptureFocus()",
    configureAccessibilityStart,
    StringComparison.Ordinal);
Assert(configureAccessibilityStart >= 0 && captureFocusStart > configureAccessibilityStart,
    "OptionCategoryControl must retain its ConfigureAccessibility method before CaptureFocus.");
var dynamicDrawerStyles = appMarkup.Descendants()
    .Where(element => element.Name.LocalName == "Style" &&
                      (string?)element.Attribute(xamlNamespace + "Key") is
                          "DynamicDrawerPrimaryTextStyle" or "DynamicDrawerSecondaryTextStyle")
    .ToDictionary(
        element => (string)element.Attribute(xamlNamespace + "Key")!,
        StringComparer.Ordinal);
Assert(dynamicDrawerStyles.Count == 2 &&
       dynamicDrawerStyles.Values.All(style =>
           (string?)style.Attribute("TargetType") == "TextBlock" &&
           style.Elements().Any(setter =>
               (string?)setter.Attribute("Property") == "FontFamily" &&
               (string?)setter.Attribute("Value") == "{StaticResource AppFontFamily}")) &&
       dynamicDrawerStyles["DynamicDrawerPrimaryTextStyle"].Elements().Any(setter =>
           (string?)setter.Attribute("Property") == "Foreground" &&
           (string?)setter.Attribute("Value") == "{ThemeResource DrawerTextBrush}") &&
       dynamicDrawerStyles["DynamicDrawerSecondaryTextStyle"].Elements().Any(setter =>
           (string?)setter.Attribute("Property") == "Foreground" &&
           (string?)setter.Attribute("Value") == "{ThemeResource DrawerSecondaryTextBrush}"),
    "Dynamic drawer copy must keep live ThemeResource foreground references through the two shared TextBlock styles.");
Assert(categoryControlSource.Contains(
           "Style = TryGetTextStyle(\"DynamicDrawerPrimaryTextStyle\")",
           StringComparison.Ordinal) &&
       categoryControlSource.Contains(
           "Style = TryGetTextStyle(\"DynamicDrawerSecondaryTextStyle\")",
           StringComparison.Ordinal) &&
       categoryControlSource.Contains(
           "Application.Current.Resources.TryGetValue(key, out var resource)",
           StringComparison.Ordinal) &&
       !categoryControlSource.Contains("RefreshSettingThemeResources", StringComparison.Ordinal) &&
       !categoryControlSource.Contains("SettingRow_Pointer", StringComparison.Ordinal),
    "Programmatic setting labels must use safe dynamic styles without concrete-brush refreshes or manual pointer-color handlers.");
Assert(categoryControlSource.Contains(
        "new SettingControlRegistration(setting, settingCheckBox, propertyChanged)",
        StringComparison.Ordinal),
    "Setting registrations must retain only behavioral state after presentation moves into ThemeResource styles.");
Assert(categoryControlSource.Contains("QueueHeaderLocalization();", StringComparison.Ordinal) &&
       categoryControlSource.Contains("UiText.ApplyToVisualTree(CategoryCheckBox);", StringComparison.Ordinal) &&
       categoryControlSource.Contains("UiText.ApplyToVisualTree(DisclosureButton);", StringComparison.Ordinal),
    "New and updated option-category cards must project the active language after their dynamic labels are bound.");

var selectionMarkup = XDocument.Load(
    Path.Combine(AppContext.BaseDirectory, "UiContracts", "OptionsSelectionControl.xaml"));
var resetSelectionButtonMatches = selectionMarkup
    .Descendants()
    .Where(element => (string?)element.Attribute(xamlNamespace + "Name") == "ResetSelectionButton")
    .ToArray();
var resetSelectionButtonElement = resetSelectionButtonMatches.Length <= 1
    ? resetSelectionButtonMatches.SingleOrDefault()
    : null;
Assert(resetSelectionButtonMatches.Length == 1 && resetSelectionButtonElement is not null,
    "OptionsSelectionControl must contain exactly one ResetSelectionButton.");
Assert((string?)resetSelectionButtonElement!.Attribute("Content") == "全选",
    "ResetSelectionButton must expose the single global 全选 action.");
var selectionControlSource = File.ReadAllText(Path.Combine(
    repositoryRoot,
    "src",
    "BlockFerry.App.WinUI",
    "Controls",
    "OptionsSelectionControl.xaml.cs"));
Assert(selectionControlSource.Contains("public event EventHandler? SelectAllRequested;", StringComparison.Ordinal) &&
       selectionControlSource.Contains("internal void SelectAll()", StringComparison.Ordinal) &&
       selectionControlSource.Contains("SelectAllRequested?.Invoke(this, EventArgs.Empty);", StringComparison.Ordinal),
    "The select-all control must expose one parent request while retaining an in-place vanilla selection method.");
Assert(selectionControlSource.Split("RenderCategories();", StringSplitOptions.None).Length - 1 == 1,
    "RenderCategories must be invoked exactly once, only when a catalog is loaded.");
Assert(!selectionControlSource.Contains("_viewModel.Reset(_catalog);", StringComparison.Ordinal),
    "The restore-all click handler must not reset the catalog or rebuild category controls.");
Assert(selectionControlSource.Contains("QueueLocalization();", StringComparison.Ordinal) &&
       selectionControlSource.Contains("UiText.ApplyToVisualTree(this);", StringComparison.Ordinal),
    "A newly rendered options catalog must localize its realized cards in the current language.");
var lockedSafetyIconMatches = selectionMarkup
    .Descendants()
    .Where(element => (string?)element.Attribute(xamlNamespace + "Name") == "LockedSafetyIcon")
    .ToArray();
var lockedSafetyIconElement = lockedSafetyIconMatches.Length <= 1
    ? lockedSafetyIconMatches.SingleOrDefault()
    : null;
Assert(lockedSafetyIconMatches.Length == 1 && lockedSafetyIconElement is not null,
    "The protection card must contain exactly one named LockedSafetyIcon.");
var lockedSafetyStripMatches = selectionMarkup
    .Descendants()
    .Where(element => (string?)element.Attribute(xamlNamespace + "Name") == "LockedSafetyStrip")
    .ToArray();
var lockedSafetyStripElement = lockedSafetyStripMatches.Length <= 1
    ? lockedSafetyStripMatches.SingleOrDefault()
    : null;
Assert(lockedSafetyStripMatches.Length == 1 && lockedSafetyStripElement is not null,
    "OptionsSelectionControl must retain exactly one LockedSafetyStrip.");
var lockedSafetyTitleMatches = selectionMarkup
    .Descendants()
    .Where(element => (string?)element.Attribute(xamlNamespace + "Name") == "LockedSafetyTitleText")
    .ToArray();
var lockedSafetyTitleElement = lockedSafetyTitleMatches.Length <= 1
    ? lockedSafetyTitleMatches.SingleOrDefault()
    : null;
Assert(lockedSafetyTitleMatches.Length == 1 && lockedSafetyTitleElement is not null,
    "The protection card must contain exactly one named LockedSafetyTitleText.");
var lockedSafetySummaryMatches = selectionMarkup
    .Descendants()
    .Where(element => (string?)element.Attribute(xamlNamespace + "Name") == "LockedSafetySummaryText")
    .ToArray();
var lockedSafetySummaryElement = lockedSafetySummaryMatches.Length <= 1
    ? lockedSafetySummaryMatches.SingleOrDefault()
    : null;
Assert(lockedSafetySummaryMatches.Length == 1 && lockedSafetySummaryElement is not null,
    "The protection card must contain exactly one named LockedSafetySummaryText.");
var lockedSafetyBodyMatches = selectionMarkup
    .Descendants()
    .Where(element => (string?)element.Attribute(xamlNamespace + "Name") == "LockedSafetyBodyText")
    .ToArray();
var lockedSafetyBodyElement = lockedSafetyBodyMatches.Length <= 1
    ? lockedSafetyBodyMatches.SingleOrDefault()
    : null;
Assert(lockedSafetyBodyMatches.Length == 1 && lockedSafetyBodyElement is not null,
    "The protection card must contain exactly one named LockedSafetyBodyText.");
var categoriesPanelMatches = selectionMarkup
    .Descendants()
    .Where(element => (string?)element.Attribute(xamlNamespace + "Name") == "CategoriesPanel")
    .ToArray();
var categoriesPanelElement = categoriesPanelMatches.Length <= 1
    ? categoriesPanelMatches.SingleOrDefault()
    : null;
Assert(categoriesPanelMatches.Length == 1 && categoriesPanelElement is not null,
    "OptionsSelectionControl must retain exactly one CategoriesPanel.");
var selectionRootContentMatches = selectionMarkup.Root?.Elements().ToArray() ?? [];
var selectionRootContent = selectionRootContentMatches.Length <= 1
    ? selectionRootContentMatches.SingleOrDefault()
    : null;
Assert(selectionRootContentMatches.Length == 1 && selectionRootContent?.Name.LocalName == "Grid",
    "OptionsSelectionControl must retain a Grid as its root content.");
Assert(!categoriesPanelElement!.Ancestors().Any(element => element.Name.LocalName == "Border"),
    "CategoriesPanel must not be wrapped by a category-equivalent outer Border.");
Assert((string?)lockedSafetyStripElement!.Attribute("CornerRadius") == "16" &&
       (string?)lockedSafetyStripElement.Attribute("BorderThickness") == "1" &&
       (string?)lockedSafetyStripElement.Attribute("BorderBrush") == "{ThemeResource OptionCardStrokeBrush}" &&
       (string?)lockedSafetyStripElement.Attribute("Visibility") == "Collapsed",
    "LockedSafetyStrip must be a default-collapsed 16-DIP one-stroke protection card.");
Assert(lockedSafetyIconElement!.Name.LocalName == "SymbolIcon" &&
       (string?)lockedSafetyIconElement.Attribute("Symbol") == "Admin" &&
       (string?)lockedSafetyIconElement.Attribute("AutomationProperties.AccessibilityView") == "Raw" &&
       lockedSafetyIconElement.Ancestors().Contains(lockedSafetyStripElement),
    "LockedSafetyIcon must use this WinUI SDK's decorative built-in Admin shield symbol inside the protection card.");
Assert((string?)lockedSafetyTitleElement!.Attribute("Text") == "整合包保护" &&
       (string?)lockedSafetySummaryElement!.Attribute("Text") == "已保护 0 项" &&
       (string?)lockedSafetyBodyElement!.Attribute("Text") == "资源包结构和版本标记将保留目标值。" &&
       lockedSafetyTitleElement.Ancestors().Contains(lockedSafetyStripElement) &&
       lockedSafetySummaryElement.Ancestors().Contains(lockedSafetyStripElement) &&
       lockedSafetyBodyElement.Ancestors().Contains(lockedSafetyStripElement),
    "The protection card must contain the exact named UTF-8 title, initial summary, and body copy.");
Assert(!lockedSafetyStripElement.Descendants().Any(element =>
        element.Name.LocalName is "Button" or "CheckBox" or "ToggleButton"),
    "The protection card must remain non-interactive and outside the Tab sequence.");
Assert(!categoryMarkup.Descendants().Concat(selectionMarkup.Descendants()).Any(element =>
        element.Attributes().Any(attribute =>
            attribute.Name.LocalName == "AutomationProperties.LiveSetting")),
    "Neither selection control may introduce a polite or assertive live region.");

var contentCardMarkup = XDocument.Load(
    Path.Combine(AppContext.BaseDirectory, "UiContracts", "ContentAdapterCard.xaml"));
var adapterSurface = RequireNamedElement(contentCardMarkup, xamlNamespace, "AdapterSurface");
var adapterCheckBox = RequireNamedElement(contentCardMarkup, xamlNamespace, "AdapterCheckBox");
var adapterIcon = RequireNamedElement(contentCardMarkup, xamlNamespace, "AdapterIcon");
var adapterSummary = RequireNamedElement(contentCardMarkup, xamlNamespace, "AdapterSummaryText");
var adapterDisclosure = RequireNamedElement(contentCardMarkup, xamlNamespace, "AdapterDisclosureButton");
var adapterDetails = RequireNamedElement(contentCardMarkup, xamlNamespace, "AdapterDetailsRegion");
var emiStatus = RequireNamedElement(contentCardMarkup, xamlNamespace, "EmiUnsupportedStatus");
Assert(adapterSurface.Name.LocalName == "Border" &&
       (string?)adapterSurface.Attribute("CornerRadius") == "16" &&
       (string?)adapterSurface.Attribute("BorderThickness") == "1",
    "ContentAdapterCard must use one rounded 16-DIP card surface.");
Assert(adapterCheckBox.Name.LocalName == "CheckBox" &&
       adapterIcon.Ancestors().Contains(adapterCheckBox) &&
       adapterSummary.Ancestors().Contains(adapterCheckBox),
    "The adapter icon, title, and selection summary must be inside the native checkbox hit target.");
Assert(adapterIcon.Name.LocalName == "SymbolIcon" &&
       (string?)adapterIcon.Attribute("AutomationProperties.AccessibilityView") == "Raw" &&
       (string?)adapterIcon.Attribute("IsHitTestVisible") == "False",
    "Adapter icons must be decorative built-in WinUI SymbolIcons.");
Assert(adapterDisclosure.Name.LocalName == "ExpandCollapseButton" &&
       !adapterDisclosure.Ancestors().Contains(adapterCheckBox) &&
       (string?)adapterDisclosure.Attribute("FontFamily") == "Segoe MDL2 Assets",
    "Each adapter card must have an independent disclosure button outside its selection checkbox.");
Assert((string?)adapterDetails.Attribute("Visibility") == "Collapsed" &&
       (string?)adapterDetails.Attribute("Opacity") == "0",
    "Adapter details must start independently collapsed.");
Assert((string?)emiStatus.Attribute("Visibility") == "Collapsed" &&
       (string?)emiStatus.Attribute("IsHitTestVisible") == "False" &&
       (string?)emiStatus.Attribute("AutomationProperties.Name") == "检测到 EMI 收藏：beta.5 暂不支持",
    "The EMI row must be a hidden-by-default read-only status with fixed safe UIA text.");
var contentCardSource = File.ReadAllText(Path.Combine(
    repositoryRoot,
    "src",
    "BlockFerry.App.WinUI",
    "Controls",
    "ContentAdapterCard.xaml.cs"));
var contentCardBindBody = ExtractCSharpMethodBody(contentCardSource, "internal void Bind(");
Assert(!contentCardBindBody.Contains("BuildItemControls(viewModel);", StringComparison.Ordinal) &&
       contentCardSource.Contains("if (expanded && !_itemsBuilt", StringComparison.Ordinal),
    "Collapsed mod-setting cards must defer constructing large item lists until first expansion.");
Assert(contentCardSource.Contains("QueueHeaderLocalization();", StringComparison.Ordinal) &&
       contentCardSource.Contains("UiText.ApplyToVisualTree(AdapterCheckBox);", StringComparison.Ordinal) &&
       contentCardSource.Contains("UiText.ApplyToVisualTree(AdapterDisclosureButton);", StringComparison.Ordinal),
    "New and updated mod-setting cards must project the active language after their dynamic labels are bound.");
Assert(contentCardSource.Contains(
           "Style = TryGetTextStyle(\"DynamicDrawerPrimaryTextStyle\")",
           StringComparison.Ordinal) &&
       contentCardSource.Contains(
           "Style = TryGetTextStyle(\"DynamicDrawerSecondaryTextStyle\")",
           StringComparison.Ordinal) &&
       contentCardSource.Contains(
           "Application.Current.Resources.TryGetValue(key, out var resource)",
           StringComparison.Ordinal),
    "Programmatic content-adapter labels must use the same safe live ThemeResource text styles.");

var conflictMarkup = XDocument.Load(
    Path.Combine(AppContext.BaseDirectory, "UiContracts", "ConflictResolutionControl.xaml"));
var conflictChoices = RequireNamedElement(conflictMarkup, xamlNamespace, "ResolutionChoices");
Assert(conflictChoices.Name.LocalName == "RadioButtons" &&
       (string?)conflictChoices.Attribute("SelectionChanged") == "ResolutionChoices_SelectionChanged",
    "ConflictResolutionControl must expose one keyboard-accessible RadioButtons choice control.");
var conflictChoiceCopy = conflictChoices.Elements().Select(element => element.Value).ToArray();
Assert(conflictChoiceCopy.Length == 3 &&
       conflictChoiceCopy[0] == "保留目标" &&
       conflictChoiceCopy[1] == "采用来源" &&
       conflictChoiceCopy[2] == "跳过此项",
    "ConflictResolutionControl must expose exactly the three approved localized choices in stable order.");

ContentAdapterSelectionContracts();
var mainPageMarkup = XDocument.Load(
    Path.Combine(AppContext.BaseDirectory, "UiContracts", "MainPage.xaml"));
var optionsSelectionPanel = RequireNamedElement(mainPageMarkup, xamlNamespace, "OptionsSelectionPanel");
var optionsSelectionControl = RequireNamedElement(mainPageMarkup, xamlNamespace, "OptionsSelectionControl");
var contentSelectionSection = RequireNamedElement(mainPageMarkup, xamlNamespace, "ContentSelectionSection");
var contentCardsPanel = RequireNamedElement(mainPageMarkup, xamlNamespace, "ContentAdapterCardsPanel");
var drawerPanelElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "DrawerPanel");
var workspaceSelectionLayout = RequireNamedElement(mainPageMarkup, xamlNamespace, "WorkspaceSelectionLayout");
var workspaceGuideColumn = RequireNamedElement(mainPageMarkup, xamlNamespace, "WorkspaceGuideColumn");
var workspaceStageColumn = RequireNamedElement(mainPageMarkup, xamlNamespace, "WorkspaceStageColumn");
var executionExperience = RequireNamedElement(mainPageMarkup, xamlNamespace, "ExecutionExperience");
var sceneHeaderPanel = RequireNamedElement(mainPageMarkup, xamlNamespace, "SceneHeaderPanel");
var sceneTaglineText = RequireNamedElement(mainPageMarkup, xamlNamespace, "SceneTaglineText");
foreach (var persistentLanguageName in new[]
         {
             "SceneHeaderTitleText",
             "DrawerEyebrowText",
             "DrawerWorkspaceTitleText",
             "WorkspaceSelectStepText",
             "WorkspaceReviewStepText",
             "WorkspaceExecuteStepText",
         })
{
    _ = RequireNamedElement(mainPageMarkup, xamlNamespace, persistentLanguageName);
}
Assert(sceneHeaderPanel.Descendants().Any() && sceneTaglineText.Name.LocalName == "TextBlock",
    "Persistent scene, workspace, and stage labels must be named for explicit language projection.");
Assert((string?)drawerPanelElement.Attribute("HorizontalAlignment") == "Stretch" &&
       (string?)drawerPanelElement.Attribute("VerticalAlignment") == "Stretch" &&
       drawerPanelElement.Attribute("Width") is null &&
       (string?)drawerPanelElement.Attribute("BorderThickness") == "0",
    "The migration workflow must occupy the full scene instead of remaining a fixed-width right drawer.");
Assert(workspaceSelectionLayout.Descendants().Contains(workspaceGuideColumn) &&
       workspaceSelectionLayout.Descendants().Contains(workspaceStageColumn) &&
       !workspaceSelectionLayout.Descendants().Contains(executionExperience) &&
       (string?)executionExperience.Attribute("Visibility") == "Collapsed",
    "Selection and execution must be separate full-workspace stages.");
Assert(optionsSelectionPanel.Descendants().Contains(optionsSelectionControl) &&
       (string?)optionsSelectionControl.Attribute("SelectAllRequested") ==
           "OptionsSelectionControl_SelectAllRequested" &&
       contentSelectionSection.Descendants().Contains(contentCardsPanel) &&
       ReferenceEquals(optionsSelectionPanel.Parent, contentSelectionSection.Parent) &&
       optionsSelectionPanel.ElementsBeforeSelf().Count() < contentSelectionSection.ElementsBeforeSelf().Count(),
    "MainPage must place the four-category options control before the supplemental JEI and ESM card section.");
Assert(mainPageMarkup.Descendants().Count(element =>
           (string?)element.Attribute("AutomationProperties.LiveSetting") == "Polite") == 1 &&
       !mainPageMarkup.Descendants().Any(element =>
           (string?)element.Attribute("AutomationProperties.LiveSetting") == "Assertive"),
    "The migration drawer must contain exactly one polite live region and no assertive live region.");
ProductionStartsAwaitingDiscovery(mainPageMarkup, xamlNamespace);
DemoModeKeepsDiscoveryRoutesAvailable();
AbsolutePathsAreRedactedFromUi();
await PickerCancelPreservesPair();
await DiscoveryRequestAdvancesGenerationOnce();
await RediscoveryDisposesPreviousSession();
DemoDoesNotTouchCapability();
var routeCardElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "RouteCard");
var discoveryCardElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "DiscoveryCard");
var errorCardElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "ErrorCard");
var resultCardElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "ResultCard");
foreach (var card in new[]
         {
             (Element: routeCardElement, Name: "RouteCard"),
             (Element: discoveryCardElement, Name: "DiscoveryCard"),
             (Element: errorCardElement, Name: "ErrorCard"),
             (Element: resultCardElement, Name: "ResultCard"),
         })
{
    Assert(card.Element.Name.LocalName == "Border" &&
           (string?)card.Element.Attribute("CornerRadius") == "16" &&
           (string?)card.Element.Attribute("BorderThickness") == "1",
        $"{card.Name} must be a 16-DIP Border card with one visible stroke.");
}

foreach (var symbol in new[] { "Folder", "Download", "Forward" })
{
    RequireRawSymbolIcon(routeCardElement, symbol);
}

RequireRawSymbolIcon(discoveryCardElement, "Folder");
RequireRawSymbolIcon(errorCardElement, "Important");
RequireRawSymbolIcon(resultCardElement, "Accept");

var drawerHeaderStatusElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "DrawerHeaderStatusText");
Assert(drawerHeaderStatusElement.Name.LocalName == "TextBlock",
    "DrawerHeaderStatusText must be the drawer's one compact status TextBlock.");
Assert(!mainPageMarkup.Descendants().Any(element =>
        (string?)element.Attribute(xamlNamespace + "Name") is "DrawerHeaderEyebrowText" or "DrawerModeLabelText"),
    "The split legacy drawer eyebrow and mode status elements must be removed.");

var routeFieldsGridElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "RouteFieldsGrid");
var sourceRouteFieldElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "SourceRouteField");
var routeDirectionIconElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "RouteDirectionIcon");
var targetRouteFieldElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "TargetRouteField");
Assert(routeFieldsGridElement.Descendants().Contains(sourceRouteFieldElement) &&
       routeFieldsGridElement.Descendants().Contains(routeDirectionIconElement) &&
       routeFieldsGridElement.Descendants().Contains(targetRouteFieldElement),
    "RouteCard must keep both named route fields and the direction icon inside RouteFieldsGrid.");
foreach (var pickerName in new[] { "SourceInstancePicker", "TargetInstancePicker" })
{
    var picker = RequireNamedElement(mainPageMarkup, xamlNamespace, pickerName);
    Assert(picker.Name.LocalName == "ComboBox" &&
           (string?)picker.Attribute("SelectionChanged") == "InstancePicker_SelectionChanged" &&
           !string.IsNullOrWhiteSpace((string?)picker.Attribute("Header")) &&
           !string.IsNullOrWhiteSpace((string?)picker.Attribute("AutomationProperties.Name")),
        $"{pickerName} must retain its ComboBox handler, visible header, and automation name.");
}

Assert((string?)discoveryCardElement.Attribute("Visibility") == "Visible",
    "DiscoveryCard must be visible in the production awaiting-discovery state.");
Assert((string?)errorCardElement.Attribute("Visibility") == "Collapsed" &&
       (string?)resultCardElement.Attribute("Visibility") == "Collapsed",
    "ErrorCard and ResultCard must be separate default-collapsed surfaces.");
var previewResultHeadingElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "PreviewResultHeading");
var previewSummaryElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "PreviewSummaryText");
var migrationReviewElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "MigrationReviewControl");
var previewSecondaryCountsElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "PreviewSecondaryCountsText");
var previewPathsElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "PreviewPathsText");
var modifySelectionButtonElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "ModifySelectionButton");
var resultDescendants = resultCardElement.Descendants().ToList();
Assert(new[]
       {
           previewResultHeadingElement,
           previewSummaryElement,
           migrationReviewElement,
           previewSecondaryCountsElement,
           previewPathsElement,
       }.All(resultDescendants.Contains),
    "ResultCard must retain its heading, summary, grouped migration review, counts, and verification details.");
Assert(resultDescendants.IndexOf(previewSummaryElement) < resultDescendants.IndexOf(migrationReviewElement) &&
       resultDescendants.IndexOf(migrationReviewElement) < resultDescendants.IndexOf(previewSecondaryCountsElement) &&
       resultDescendants.IndexOf(previewSecondaryCountsElement) < resultDescendants.IndexOf(previewPathsElement),
    "ResultCard must present summary, grouped review cards, secondary counts, then verification details.");
Assert(modifySelectionButtonElement.Name.LocalName == "Button" &&
       (string?)modifySelectionButtonElement.Attribute("Click") == "ModifySelectionButton_Click",
    "ModifySelectionButton must remain the fixed-footer focus-return action.");

var migrationReviewPath = Path.Combine(
    repositoryRoot,
    "src",
    "BlockFerry.App.WinUI",
    "Controls",
    "MigrationReviewControl.xaml");
Assert(File.Exists(migrationReviewPath),
    "MigrationReviewControl must exist as the grouped card review surface.");
var migrationReviewSource = File.ReadAllText(Path.ChangeExtension(migrationReviewPath, ".xaml.cs"));
Assert(migrationReviewSource.Split("QueueLocalization();", StringSplitOptions.None).Length - 1 >= 2 &&
       migrationReviewSource.Contains("UiText.ApplyToVisualTree(this);", StringComparison.Ordinal) &&
       migrationReviewSource.Contains("DispatcherQueuePriority.Low", StringComparison.Ordinal),
    "Both real and demo review models must localize their newly realized grouped cards after item containers are laid out.");
var migrationReviewMarkup = XDocument.Load(migrationReviewPath);
var reviewGroupsElement = RequireNamedElement(migrationReviewMarkup, xamlNamespace, "ReviewGroupsItemsControl");
var reviewGroupCardElement = RequireNamedElement(migrationReviewMarkup, xamlNamespace, "ReviewGroupCard");
var reviewBundleCardElement = RequireNamedElement(migrationReviewMarkup, xamlNamespace, "ReviewBundleCard");
var reviewBundleExpanderElement = RequireNamedElement(migrationReviewMarkup, xamlNamespace, "ReviewBundleExpander");
var reviewDetailsItemsElement = RequireNamedElement(migrationReviewMarkup, xamlNamespace, "ReviewDetailsItemsControl");
var reviewDetailRowElement = RequireNamedElement(migrationReviewMarkup, xamlNamespace, "ReviewDetailRow");
var reviewGroupTitleElement = RequireNamedElement(migrationReviewMarkup, xamlNamespace, "ReviewGroupTitleText");
var reviewBundleTitleElement = RequireNamedElement(migrationReviewMarkup, xamlNamespace, "ReviewBundleTitleText");
Assert(reviewGroupsElement.Name.LocalName == "ItemsControl" &&
       (string?)reviewGroupCardElement.Attribute("CornerRadius") == "14" &&
       (string?)reviewBundleCardElement.Attribute("CornerRadius") == "11" &&
       (string?)reviewDetailRowElement.Attribute("CornerRadius") == "8" &&
       reviewBundleExpanderElement.Name.LocalName == "Expander" &&
       (string?)reviewBundleExpanderElement.Attribute("IsExpanded") == "False" &&
       (string?)reviewBundleExpanderElement.Attribute("Expanding") == "ReviewBundleExpander_Expanding" &&
       reviewDetailsItemsElement.Attribute("ItemsSource") is null,
    "Migration review must collapse detailed rows inside rounded category bundles by default.");
Assert((string?)reviewGroupTitleElement.Attribute("FontWeight") == "Bold" &&
       (string?)reviewBundleTitleElement.Attribute("FontWeight") == "Bold",
    "Migration review group and category headings must retain the stronger visual weight.");
Assert((string?)reviewBundleExpanderElement.Attribute("HorizontalAlignment") == "Stretch" &&
       (string?)reviewBundleExpanderElement.Attribute("HorizontalContentAlignment") == "Stretch",
    "Migration review bundle expanders must stretch themselves and their content across the bundle card.");
var reviewDetailsLayoutElement = reviewDetailsItemsElement
    .Descendants()
    .SingleOrDefault(element => element.Name.LocalName == "UniformGridLayout");
Assert(reviewDetailsItemsElement.Name.LocalName == "ItemsRepeater" &&
       reviewDetailsLayoutElement is not null &&
       (string?)reviewDetailsLayoutElement.Attribute("Orientation") == "Vertical" &&
       (string?)reviewDetailsLayoutElement.Attribute("MaximumRowsOrColumns") == "2" &&
       (string?)reviewDetailsLayoutElement.Attribute("MinItemWidth") == "320" &&
       (string?)reviewDetailsLayoutElement.Attribute("MinColumnSpacing") == "8" &&
       (string?)reviewDetailsLayoutElement.Attribute("MinRowSpacing") == "6" &&
       (string?)reviewDetailsLayoutElement.Attribute("ItemsStretch") == "Fill",
    "Migration review details must fill a responsive grid capped at two columns and collapse to one column below two 320px cards.");

var drawerFooterGridElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "DrawerFooterGrid");
var footerStatusHostElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "FooterStatusHost");
var selectedCountFooterElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "SelectedCountFooterText");
var dryRunPreviewButtonElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "DryRunPreviewButton");
Assert(drawerFooterGridElement.Descendants().Contains(footerStatusHostElement) &&
       footerStatusHostElement.Descendants().Contains(selectedCountFooterElement) &&
       drawerFooterGridElement.Descendants().Contains(dryRunPreviewButtonElement) &&
       drawerFooterGridElement.Descendants().Contains(modifySelectionButtonElement),
    "DrawerFooterGrid must own status, review-back, and primary transaction actions.");
Assert((string?)dryRunPreviewButtonElement.Attribute("Click") == "DryRunPreviewButton_Click" &&
       !string.IsNullOrWhiteSpace((string?)dryRunPreviewButtonElement.Attribute("AutomationProperties.HelpText")) &&
       !string.IsNullOrWhiteSpace((string?)dryRunPreviewButtonElement.Attribute("AutomationProperties.Name")) &&
       (string?)dryRunPreviewButtonElement.Attribute("UseSystemFocusVisuals") == "True",
    "DryRunPreviewButton must retain its handler, accessible copy, and system focus visuals.");

var compactStateElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "DrawerCompactState");
var wideStateElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "DrawerWideState");
var drawerWidthStatesElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "DrawerWidthStates");
var drawerWidthStateNames = drawerWidthStatesElement
    .Elements()
    .Where(element => element.Name.LocalName == "VisualState")
    .Select(element => (string?)element.Attribute(xamlNamespace + "Name"))
    .ToList();
Assert(drawerWidthStateNames.IndexOf("DrawerWideState") < drawerWidthStateNames.IndexOf("DrawerCompactState"),
    "DrawerWideState must be declared before DrawerCompactState so the desktop trigger wins when both are active.");
var compactTrigger = compactStateElement.Descendants()
    .Single(element => element.Name.LocalName == "AdaptiveTrigger");
var wideTrigger = wideStateElement.Descendants()
    .Single(element => element.Name.LocalName == "AdaptiveTrigger");
Assert((string?)compactTrigger.Attribute("MinWindowWidth") == "0",
    "DrawerCompactState must be the MinWindowWidth=0 fallback.");
Assert(int.TryParse((string?)wideTrigger.Attribute("MinWindowWidth"), out var wideMinimum) && wideMinimum >= 381,
    "DrawerWideState must override compact layout at a desktop-safe width.");
var compactSetterMap = compactStateElement
    .Descendants()
    .Where(element => element.Name.LocalName == "Setter")
    .ToDictionary(
        element => (string)element.Attribute("Target")!,
        element => (string)element.Attribute("Value")!,
        StringComparer.Ordinal);
var expectedCompactSetters = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["WorkspaceGuideColumn.(Grid.Row)"] = "0",
    ["WorkspaceGuideColumn.(Grid.Column)"] = "0",
    ["WorkspaceGuideColumn.(Grid.ColumnSpan)"] = "2",
    ["WorkspaceStageColumn.(Grid.Row)"] = "1",
    ["WorkspaceStageColumn.(Grid.Column)"] = "0",
    ["WorkspaceStageColumn.(Grid.ColumnSpan)"] = "2",
    ["FooterStatusHost.(Grid.Row)"] = "0",
    ["DryRunPreviewButton.(Grid.Row)"] = "1",
    ["DryRunPreviewButton.(Grid.Column)"] = "0",
    ["DryRunPreviewButton.(Grid.ColumnSpan)"] = "2",
    ["DryRunPreviewButton.HorizontalAlignment"] = "Stretch",
};
foreach (var (target, value) in expectedCompactSetters)
{
    Assert(compactSetterMap.TryGetValue(target, out var actual) && actual == value,
        $"DrawerCompactState must set {target} to {value}.");
}

var mainPageSource = File.ReadAllText(Path.Combine(
    repositoryRoot,
    "src",
    "BlockFerry.App.WinUI",
    "MainPage.xaml.cs"));
var mainPageMigrationSource = File.ReadAllText(Path.Combine(
    repositoryRoot,
    "src",
    "BlockFerry.App.WinUI",
    "MainPage.Migration.cs"));
var applyLanguageSource = ExtractCSharpMethodBody(
    mainPageSource,
    "internal void ApplyLanguage()");
Assert(applyLanguageSource.Contains("QueueLocalization();", StringComparison.Ordinal) &&
       applyLanguageSource.Contains("QueueSubtreeLocalization(SceneLayer);", StringComparison.Ordinal) &&
       applyLanguageSource.Contains("QueueSubtreeLocalization(SceneHeaderPanel);", StringComparison.Ordinal) &&
       applyLanguageSource.Contains("QueueSubtreeLocalization(SceneTaglineText);", StringComparison.Ordinal) &&
       applyLanguageSource.Contains("QueueSubtreeLocalization(DrawerPanel);", StringComparison.Ordinal) &&
       applyLanguageSource.Contains("QueueSubtreeLocalization(DrawerHeaderPanel);", StringComparison.Ordinal) &&
       applyLanguageSource.Contains("QueueSubtreeLocalization(WorkspaceGuideColumn);", StringComparison.Ordinal) &&
       applyLanguageSource.Contains("QueueSubtreeLocalization(WorkspaceStageColumn);", StringComparison.Ordinal) &&
       applyLanguageSource.Contains("QueueSubtreeLocalization(ResultCard);", StringComparison.Ordinal) &&
       applyLanguageSource.Contains("QueueSubtreeLocalization(ExecutionExperience);", StringComparison.Ordinal) &&
       applyLanguageSource.Contains("QueueSubtreeLocalization(DrawerFooterGrid);", StringComparison.Ordinal) &&
       applyLanguageSource.Contains("QueuePrefixLocalization();", StringComparison.Ordinal) &&
       applyLanguageSource.Contains("ProjectPersistentLanguageCopy();", StringComparison.Ordinal) &&
       !applyLanguageSource.Contains("PresentWorkflowState(", StringComparison.Ordinal) &&
       !applyLanguageSource.Contains("ProjectViewState(", StringComparison.Ordinal),
    "Changing language must translate the visible workspace in place without resetting selection, review, execution, or result stages.");
var persistentLanguageSource = ExtractCSharpMethodBody(
    mainPageSource,
    "private void ProjectPersistentLanguageCopy()");
foreach (var requiredTarget in new[]
         {
             "SceneHeaderTitleText",
             "SceneTaglineText",
             "DrawerEyebrowText",
             "DrawerWorkspaceTitleText",
             "WorkspaceSelectStepText",
             "WorkspaceReviewStepText",
             "WorkspaceExecuteStepText",
         })
{
    Assert(persistentLanguageSource.Contains(requiredTarget, StringComparison.Ordinal),
        $"Persistent language projection must update {requiredTarget} directly.");
}
var queuePrefixSource = ExtractCSharpMethodBody(
    mainPageSource,
    "private void QueuePrefixLocalization()");
Assert(queuePrefixSource.Contains("UiText.Translate(_viewState.SourceVersion)", StringComparison.Ordinal) &&
       queuePrefixSource.Contains("UiText.Translate(_viewState.TargetVersion)", StringComparison.Ordinal),
    "The non-visual source and target Run nodes must be projected directly for cold-start and language switching.");
var projectViewStateSource = ExtractCSharpMethodBody(mainPageSource, "private void ProjectViewState(");
Assert(projectViewStateSource.Contains("QueuePrefixLocalization();", StringComparison.Ordinal),
    "Every projected discovery state must refresh the non-visual version Run nodes.");
var openDrawerSource = ExtractCSharpMethodBody(mainPageSource, "private void OpenDrawer(");
Assert(openDrawerSource.Contains("QueueSubtreeLocalization(DrawerPanel);", StringComparison.Ordinal) &&
       openDrawerSource.Contains("QueueSubtreeLocalization(DrawerHeaderPanel);", StringComparison.Ordinal) &&
       openDrawerSource.Contains("QueueSubtreeLocalization(DrawerFooterGrid);", StringComparison.Ordinal),
    "Opening a previously collapsed workspace must localize its now-realized visual subtree before interaction.");
var completeDrawerOpenSource = ExtractCSharpMethodBody(
    mainPageSource,
    "private void CompleteDrawerOpen(long generation)");
Assert(completeDrawerOpenSource.Contains("QueueSubtreeLocalization(DrawerPanel);", StringComparison.Ordinal) &&
       completeDrawerOpenSource.Contains("QueueSubtreeLocalization(WorkspaceStageColumn);", StringComparison.Ordinal) &&
       completeDrawerOpenSource.Contains("QueueSubtreeLocalization(DrawerFooterGrid);", StringComparison.Ordinal),
    "Completing the open animation must re-localize controls that were created or realized while the workspace was collapsed.");
var presentSelectedPreviewSource = ExtractCSharpMethodBody(
    mainPageSource,
    "private void PresentSelectedPreview(");
Assert(presentSelectedPreviewSource.Contains("QueueSubtreeLocalization(ResultCard);", StringComparison.Ordinal),
    "A newly realized demo review surface must be localized before it is announced or focused.");
Assert(presentSelectedPreviewSource.Contains("PreviewResultTitleText.Text = \"确认同步清单\";", StringComparison.Ordinal) &&
       presentSelectedPreviewSource.Contains("LocalizeElements(PreviewResultTitleText);", StringComparison.Ordinal),
    "The demo review heading must be re-projected after the result card becomes visible.");
Assert(presentSelectedPreviewSource.Contains("UiText.Translate(", StringComparison.Ordinal),
    "The demo preview completion announcement must use the active UI language.");
var presentDiscoverySource = ExtractCSharpMethodBody(mainPageSource, "private void PresentDiscoveryViewModel()");
Assert(presentDiscoverySource.Contains("LocalizeElements(ScanStatusText);", StringComparison.Ordinal),
    "A discovery status assigned after an asynchronous scan must be localized immediately.");
var showSelectionErrorSource = ExtractCSharpMethodBody(mainPageSource, "private void ShowSelectionError(");
Assert(showSelectionErrorSource.Contains("QueueSubtreeLocalization(ErrorCard);", StringComparison.Ordinal),
    "A newly realized selection error and its diagnostics must be localized before display.");
Assert(mainPageMigrationSource.Contains("var showExecution = workflowState.Phase is", StringComparison.Ordinal) &&
       mainPageMigrationSource.Contains("WorkspaceSelectionLayout.Visibility = showSelection", StringComparison.Ordinal) &&
       mainPageMigrationSource.Contains("ExecutionExperience.Visibility = showExecution", StringComparison.Ordinal) &&
       mainPageMigrationSource.Contains("PresentExecutionExperience(workflowState);", StringComparison.Ordinal) &&
       mainPageMigrationSource.Contains("QueueSubtreeLocalization(ResultCard);", StringComparison.Ordinal) &&
       mainPageMigrationSource.Contains("QueueSubtreeLocalization(ExecutionExperience);", StringComparison.Ordinal) &&
       mainPageMigrationSource.Contains("AnimateWorkspacePhaseChange(showSelection, showExecution, showResult);", StringComparison.Ordinal),
    "The migration workflow must project selection, review, and execution as separate animated workspace stages.");
Assert(mainPageMigrationSource.Contains("UiText.Translate(workflowState.StatusText)", StringComparison.Ordinal),
    "Completed migration announcements must use the active UI language.");
var syncProgressSource = ExtractCSharpMethodBody(mainPageSource, "private void SetSyncProgressValue(");
Assert(syncProgressSource.IndexOf("var currentValue = SyncProgressBar.Value;", StringComparison.Ordinal) <
           syncProgressSource.IndexOf("_syncProgressStoryboard?.Stop();", StringComparison.Ordinal) &&
       syncProgressSource.Contains("SyncProgressBar.Value = value;", StringComparison.Ordinal) &&
       syncProgressSource.Contains("From = currentValue", StringComparison.Ordinal) &&
       syncProgressSource.Contains("FillBehavior = FillBehavior.Stop", StringComparison.Ordinal),
    "Header progress must continue from its presented value and commit the new base value without snapping backward.");
var executionProgressSource = ExtractCSharpMethodBody(
    mainPageMigrationSource,
    "private void SetExecutionProgressValue(");
Assert(executionProgressSource.IndexOf("var currentValue = ExecutionProgressBar.Value;", StringComparison.Ordinal) <
           executionProgressSource.IndexOf("_executionProgressStoryboard?.Stop();", StringComparison.Ordinal) &&
       executionProgressSource.Contains("ExecutionProgressBar.Value = value;", StringComparison.Ordinal) &&
       executionProgressSource.Contains("From = currentValue", StringComparison.Ordinal) &&
        executionProgressSource.Contains("FillBehavior = FillBehavior.Stop", StringComparison.Ordinal),
    "Execution progress must continue from its presented value and commit the new base value without snapping backward.");
Assert(syncProgressSource.Contains("_syncProgressAccumulator.Advance(value)", StringComparison.Ordinal) &&
       syncProgressSource.Contains("_workflow?.State.IsMutationInProgress == true", StringComparison.Ordinal) &&
       syncProgressSource.Contains("_executionProgressAccumulator.Current", StringComparison.Ordinal) &&
       ExtractCSharpMethodBody(mainPageMigrationSource, "private void PresentExecutionExperience(")
           .Contains("var displayedPercent = _executionProgressAccumulator.Advance(presentation.Percent);", StringComparison.Ordinal) &&
       ExtractCSharpMethodBody(mainPageSource, "public void SetSyncPresentation(")
           .Contains("_syncProgressAccumulator.Reset();", StringComparison.Ordinal) &&
       ExtractCSharpMethodBody(mainPageMigrationSource, "private void PresentWorkflowState(")
           .Contains("_executionProgressAccumulator.Reset();", StringComparison.Ordinal),
    "Header and execution progress must reset only for a new operation and remain monotonic through rollback.");
var syncPresentationActivitySource = ExtractCSharpMethodBody(
    mainPageSource,
    "public void SetSyncPresentation(");
Assert(syncPresentationActivitySource.Contains(
           "PrimaryActionButton.Visibility = isRunning ? Visibility.Collapsed : Visibility.Visible;",
           StringComparison.Ordinal) &&
       !syncPresentationActivitySource.Contains("PrimaryRunningContent", StringComparison.Ordinal) &&
       !syncPresentationActivitySource.Contains("PrimaryProgressRing", StringComparison.Ordinal),
    "A running home operation must use the stable status area and full-width progress bar instead of floating a redundant action over the decorative version.");
var discoveryActivitySource = ExtractCSharpMethodBody(
    mainPageSource,
    "private void SetDiscoveryActivity(");
var drawerActivitySource = ExtractCSharpMethodBody(
    mainPageSource,
    "private void SetDrawerActivity(");
var executionActivitySource = ExtractCSharpMethodBody(
    mainPageMigrationSource,
    "private void PresentExecutionExperience(");
Assert(executionActivitySource.Contains(
           "ExecutionPercentText.Text = presentation.IsIndeterminate",
           StringComparison.Ordinal) &&
       executionActivitySource.Contains(
           "$\"{Math.Round(displayedPercent):0}%\"",
           StringComparison.Ordinal) &&
       executionActivitySource.Contains("SetExecutionProgressValue(displayedPercent);", StringComparison.Ordinal),
    "The execution percentage label must avoid fake values for unknown waits and share determinate values with the main bar.");
var workflowFooterSource = ExtractCSharpMethodBody(
    mainPageMigrationSource,
    "private void UpdateWorkflowFooter(");
Assert(workflowFooterSource.Contains(
           "var displayedPercent = migrationRunning",
           StringComparison.Ordinal) &&
       workflowFooterSource.Contains("_executionProgressAccumulator.Current", StringComparison.Ordinal) &&
       workflowFooterSource.Contains("progressPresentation.StageText} · {Math.Round(displayedPercent):0}%", StringComparison.Ordinal) &&
       workflowFooterSource.Contains("displayedPercent);", StringComparison.Ordinal),
    "The footer label and bar must reuse the accumulated operation percentage instead of raw rollback progress.");
Assert(executionActivitySource.Contains(
           "ExecutionProgressBar.IsIndeterminate = continuousMotion && presentation.IsIndeterminate;",
           StringComparison.Ordinal) &&
       executionActivitySource.Contains(
           "presentation.IsIndeterminate",
           StringComparison.Ordinal) &&
       syncPresentationActivitySource.Contains(
           "running.IsIndeterminate",
           StringComparison.Ordinal) &&
       workflowFooterSource.Contains(
           "progressPresentation.IsIndeterminate",
           StringComparison.Ordinal),
    "Execution, home status, and footer must share the same truthful indeterminate-progress decision.");
Assert(syncPresentationActivitySource.Contains(
           "PrimaryDoneText.Text = _viewState.IsDemo ? \"查看演示结果\" : \"查看同步结果\";",
           StringComparison.Ordinal) &&
       syncPresentationActivitySource.Contains(
           "PrimaryActionButton.IsEnabled = true;",
           StringComparison.Ordinal),
    "Verified home completion must remain actionable so the user can reopen the result and undo details.");
var presentWorkflowStateProgressSource = ExtractCSharpMethodBody(
    mainPageMigrationSource,
    "private void PresentWorkflowState(");
Assert(presentWorkflowStateProgressSource.Contains(
           "case MigrationWorkflowPhase.Discovering:",
           StringComparison.Ordinal) &&
       presentWorkflowStateProgressSource.Contains(
           "new MigrationProgress(MigrationProgressStage.Revalidating, 0, 0, workflowState.StatusText)",
           StringComparison.Ordinal),
    "Read-only instance and catalog revalidation must project an explicit indeterminate progress state on the home surface.");
foreach (var activitySource in new[]
         {
             syncPresentationActivitySource,
             discoveryActivitySource,
             drawerActivitySource,
             executionActivitySource,
         })
{
    Assert(activitySource.Contains("ContinuousMotionPolicy.Allows(", StringComparison.Ordinal) &&
           activitySource.Contains("IsIndeterminate = continuousMotion", StringComparison.Ordinal),
        "Every busy-state refresh must preserve reduced-motion and high-contrast animation suppression.");
}
var stageRailSource = ExtractCSharpMethodBody(
    mainPageMigrationSource,
    "private void UpdateWorkspaceStageRail(");
Assert(stageRailSource.Contains("_highContrast ? 1", StringComparison.Ordinal) &&
       stageRailSource.Contains("AutomationProperties.SetItemStatus", StringComparison.Ordinal) &&
       stageRailSource.Contains("new Thickness(index == activeStep ? 2 : 1)", StringComparison.Ordinal) &&
       stageRailSource.Contains("QueueSubtreeLocalization(WorkspaceStageRail);", StringComparison.Ordinal),
    "The stage rail must retain high-contrast visibility and expose the active/completed/pending state to assistive technology.");
var instancePickerHandlerSource = ExtractCSharpMethodBody(
    mainPageSource,
    "private async void InstancePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)");
Assert(instancePickerHandlerSource.Contains(
           "RouteSelectionIntentDispatcher.DispatchAsync(",
           StringComparison.Ordinal) &&
       instancePickerHandlerSource.Contains("changedPicker: changedPicker", StringComparison.Ordinal) &&
       instancePickerHandlerSource.Contains("sourcePicker: SourceInstancePicker", StringComparison.Ordinal) &&
       instancePickerHandlerSource.Contains("targetPicker: TargetInstancePicker", StringComparison.Ordinal) &&
       instancePickerHandlerSource.Contains("currentSourceId: currentSourceId", StringComparison.Ordinal) &&
       instancePickerHandlerSource.Contains("currentTargetId: currentTargetId", StringComparison.Ordinal) &&
       instancePickerHandlerSource.Contains("selectedInstanceId: selected.Id", StringComparison.Ordinal) &&
       instancePickerHandlerSource.Contains("submitPairAsync: ChangeWorkflowPairAsync", StringComparison.Ordinal) &&
       !instancePickerHandlerSource.Contains("RouteSelectionResolver.Resolve(", StringComparison.Ordinal),
    "RoutePickerDispatch: the WinUI handler must bind each picker, accepted ID, selected ID, and submit callback to its named dispatcher parameter.");
var refreshOptionsSource = ExtractCSharpMethodBody(
    mainPageSource,
    "private async Task RefreshOptionsSelectionSessionAsync()");
var normalizedRefreshOptionsSource = refreshOptionsSource.Replace("\r\n", "\n", StringComparison.Ordinal);
var expectedOptionsModeGuard = """
        if (!OptionsSelectionModePolicy.UsesLegacyOptionsSelection(
                workflowAttached: _workflow is not null,
                workflowIsDemo: _workflow?.State.Phase == MigrationWorkflowPhase.Demo))
        {
            return;
        }
""".Replace("\r\n", "\n", StringComparison.Ordinal);
var optionsModeGuardIndex = normalizedRefreshOptionsSource.IndexOf(
    expectedOptionsModeGuard,
    StringComparison.Ordinal);
var optionsCatalogGuardIndex = normalizedRefreshOptionsSource.IndexOf(
    "if (_catalogInFlight)",
    StringComparison.Ordinal);
Assert(optionsModeGuardIndex >= 0 &&
       optionsModeGuardIndex < optionsCatalogGuardIndex &&
       normalizedRefreshOptionsSource[..optionsModeGuardIndex].Trim().Length == 0,
    "WorkflowOptionsMode: the complete first guard must negate the legacy-mode policy and return before any catalog or false missing-pair handling.");
Assert(mainPageSource.Contains(
        "ApplyViewState(MigrationViewState.AwaitingDiscovery);",
        StringComparison.Ordinal) &&
       !mainPageSource.Contains(
           "ApplyViewState(MigrationViewState.Demo);",
           StringComparison.Ordinal),
    "ProductionStartsAwaitingDiscovery: MainPage construction must project AwaitingDiscovery rather than demo.");
foreach (var prohibitedPageAccess in new[]
         {
             "Directory.",
             "File.",
             "Environment.",
             "new FolderPicker(",
             "Pcl2InstanceDiscovery.Discover(",
         })
{
    Assert(!mainPageSource.Contains(prohibitedPageAccess, StringComparison.Ordinal),
        $"MainPage must delegate discovery and picker access instead of using {prohibitedPageAccess} directly.");
}
Assert(MigrationViewState.Demo.ModeLabel == "演示数据 · 只读预览",
    "The deterministic demo mode label must retain its complete read-only meaning.");
Assert(MigrationViewCopy.DrawerHeaderStatus(MigrationViewState.Demo) ==
       "演示数据 · 只读预览 · 0 写入",
    "Demo drawer header must compose the existing mode label once as 演示数据 · 只读预览 · 0 写入.");
Assert(mainPageSource.Contains("private void UpdateDrawerFooterPresentation()", StringComparison.Ordinal) &&
       mainPageSource.Split("SelectedCountFooterText.Text =", StringSplitOptions.None).Length - 1 == 1 &&
       mainPageSource.Split("DryRunPreviewButton.Content =", StringSplitOptions.None).Length - 1 == 1 &&
       mainPageSource.Split("DryRunPreviewButton.IsEnabled =", StringSplitOptions.None).Length - 1 == 1,
    "All footer phase text, button content, and enabled state must be centralized in one projection method.");
foreach (var footerCopy in new[]
         {
             "正在准备可选设置…",
             "已选 {selectedCount} / {_selectionCatalog.SelectableDifferences.Count} 项设置",
             "正在生成只读预览 · 0 写入",
             "计划 {_lastPlannedChangeCount} 项 · 0 写入",
         })
{
    Assert(mainPageSource.Contains(footerCopy, StringComparison.Ordinal),
        $"The centralized footer projection must contain the phase copy: {footerCopy}");
}
var presentResultStart = mainPageSource.IndexOf(
    "private void PresentSelectedPreview(",
    StringComparison.Ordinal);
var showErrorStart = mainPageSource.IndexOf(
    "private void ShowSelectionError(",
    presentResultStart,
    StringComparison.Ordinal);
var resetSelectionStart = mainPageSource.IndexOf(
    "private void ResetOptionsSelectionForPairChange()",
    showErrorStart,
    StringComparison.Ordinal);
Assert(presentResultStart >= 0 && showErrorStart > presentResultStart && resetSelectionStart > showErrorStart,
    "MainPage must retain separate result, error, and pair-reset presentation methods.");
var presentResultSource = mainPageSource[presentResultStart..showErrorStart];
var showErrorSource = mainPageSource[showErrorStart..resetSelectionStart];
Assert(presentResultSource.Contains("ErrorCard.Visibility = Visibility.Collapsed;", StringComparison.Ordinal) &&
       presentResultSource.Contains("ResultCard.Visibility = Visibility.Visible;", StringComparison.Ordinal) &&
       showErrorSource.Contains("ResultCard.Visibility = Visibility.Collapsed;", StringComparison.Ordinal) &&
       showErrorSource.Contains("ErrorCard.Visibility = Visibility.Visible;", StringComparison.Ordinal),
    "Result and error presentation must keep ResultCard and ErrorCard mutually exclusive.");
var resultFocusIndex = presentResultSource.IndexOf(
    "PreviewResultHeading.Focus(FocusState.Programmatic);",
    StringComparison.Ordinal);
var resultAnnouncementIndex = presentResultSource.IndexOf(
    "peer?.RaiseNotificationEvent(",
    StringComparison.Ordinal);
Assert(resultFocusIndex >= 0 &&
       resultAnnouncementIndex > resultFocusIndex &&
       !presentResultSource.Contains("TryPlay(", StringComparison.Ordinal) &&
       !mainPageSource.Contains("_completionSound", StringComparison.Ordinal),
    "A read-only preview must focus and announce its result but stay silent until a verified transaction commits.");

var executionHomeState = MigrationWorkflowState.Initial with
{
    Phase = MigrationWorkflowPhase.Executing,
};
var blockedAfterExecutionState = MigrationWorkflowState.Initial with
{
    Phase = MigrationWorkflowPhase.Blocked,
};
var recoveryAfterExecutionState = MigrationWorkflowState.Initial with
{
    Phase = MigrationWorkflowPhase.RecoveryRequired,
};
var succeededAfterExecutionState = MigrationWorkflowState.Initial with
{
    Phase = MigrationWorkflowPhase.Succeeded,
    LastExecutionStatus = MigrationExecutionStatus.Succeeded,
};
Assert(ExecutionWorkspaceNavigationPolicy.Evaluate(
           MigrationWorkflowPhase.Reviewing,
           executionHomeState,
           DrawerModalPhase.Open) == ExecutionWorkspaceNavigationAction.ShowHome &&
       ExecutionWorkspaceNavigationPolicy.Evaluate(
           MigrationWorkflowPhase.Executing,
           succeededAfterExecutionState,
           DrawerModalPhase.Collapsed) == ExecutionWorkspaceNavigationAction.None &&
       ExecutionWorkspaceNavigationPolicy.Evaluate(
           MigrationWorkflowPhase.Executing,
           blockedAfterExecutionState,
           DrawerModalPhase.Collapsed) == ExecutionWorkspaceNavigationAction.ShowWorkspace &&
       ExecutionWorkspaceNavigationPolicy.Evaluate(
           MigrationWorkflowPhase.Executing,
           recoveryAfterExecutionState,
           DrawerModalPhase.Closing) == ExecutionWorkspaceNavigationAction.ShowWorkspace &&
       ExecutionWorkspaceNavigationPolicy.Evaluate(
           MigrationWorkflowPhase.Selecting,
           blockedAfterExecutionState,
           DrawerModalPhase.Collapsed) == ExecutionWorkspaceNavigationAction.None,
    "ExecutionWorkspaceNavigation: confirmed execution must continue on home, verified success must stay there, and mutation failures must restore the workspace.");
Assert(MigrationWorkflowPolicy.CanReturnToSelection(
           MigrationWorkflowPhase.Reviewing,
           hasCatalogs: false,
           isMutationInProgress: false) &&
       MigrationWorkflowPolicy.CanReturnToSelection(
           MigrationWorkflowPhase.Blocked,
           hasCatalogs: true,
           isMutationInProgress: false) &&
       !MigrationWorkflowPolicy.CanReturnToSelection(
           MigrationWorkflowPhase.Blocked,
           hasCatalogs: false,
           isMutationInProgress: false) &&
       !MigrationWorkflowPolicy.CanReturnToSelection(
           MigrationWorkflowPhase.Blocked,
           hasCatalogs: true,
           isMutationInProgress: true),
    "BlockedSelectionRecovery: a stopped real workflow with retained catalogs must expose a safe route back to selection, while mutation and catalog-less states remain closed.");
Assert(MigrationWorkflowPolicy.CanStartAnotherSync(
           MigrationWorkflowPhase.Succeeded,
           MigrationExecutionStatus.Succeeded,
           hasDeferredJeiSync: false,
           isMutationInProgress: false,
           hasPair: true) &&
       !MigrationWorkflowPolicy.CanStartAnotherSync(
           MigrationWorkflowPhase.Succeeded,
           MigrationExecutionStatus.Succeeded,
           hasDeferredJeiSync: true,
           isMutationInProgress: false,
           hasPair: true) &&
       !MigrationWorkflowPolicy.CanStartAnotherSync(
           MigrationWorkflowPhase.Reviewing,
           MigrationExecutionStatus.Succeeded,
           hasDeferredJeiSync: false,
           isMutationInProgress: false,
           hasPair: true),
    "RepeatSync: only a fully verified, non-deferred result with a retained pair may begin a fresh read-only revalidation cycle.");
var presentWorkflowStateForNavigation = ExtractCSharpMethodBody(
    mainPageMigrationSource,
    "private void PresentWorkflowState(");
Assert(presentWorkflowStateForNavigation.Contains(
           "ExecutionWorkspaceNavigationPolicy.Evaluate(",
           StringComparison.Ordinal) &&
       presentWorkflowStateForNavigation.Contains(
           "CloseDrawerForBackgroundExecution();",
           StringComparison.Ordinal) &&
       presentWorkflowStateForNavigation.Contains(
           "OpenDrawerForWorkflowAttention();",
           StringComparison.Ordinal),
    "ExecutionWorkspaceNavigation: the live workflow projection must apply the tested home/attention policy.");
var workflowResultProjectionSource = ExtractCSharpMethodBody(
    mainPageMigrationSource,
    "private void PresentWorkflowResult(");
var modifySelectionClickSource = ExtractCSharpMethodBody(
    mainPageSource,
    "private void ModifySelectionButton_Click(");
var blockedWorkflowFooterSource = ExtractCSharpMethodBody(
    mainPageMigrationSource,
    "private void UpdateWorkflowFooter(");
Assert(workflowResultProjectionSource.Contains(
           "MigrationWorkflowPolicy.CanReturnToSelection(",
           StringComparison.Ordinal) &&
       modifySelectionClickSource.Contains(
           "MigrationWorkflowPolicy.CanReturnToSelection(",
           StringComparison.Ordinal),
    "BlockedSelectionRecovery: both the result projection and its edit action must share the tested workflow policy.");
Assert(blockedWorkflowFooterSource.Contains(
           "case MigrationWorkflowPhase.Blocked when workflowState.Catalogs.Count > 0:",
           StringComparison.Ordinal) &&
       blockedWorkflowFooterSource.Contains(
           "_contentSelectionViewModel.CaptureSelection()",
           StringComparison.Ordinal) &&
       blockedWorkflowFooterSource.Contains(
           "!_contentSelectionViewModel.HasUnresolvedConflicts",
           StringComparison.Ordinal),
    "BlockedSelectionRecovery: a blocked workflow with retained catalogs must expose a read-only plan retry only for a valid conflict-free selection.");
Assert(blockedWorkflowFooterSource.Contains(
           "MigrationWorkflowPolicy.CanStartAnotherSync(",
           StringComparison.Ordinal) &&
       blockedWorkflowFooterSource.Contains(
           "DryRunPreviewButton.Content = canStartAnotherSync ? \"再次同步\" : \"同步已验证\";",
           StringComparison.Ordinal),
    "RepeatSync: the completed result footer must turn its existing primary action into an explicit again-sync entry only when the policy allows it.");
var prepareOrExecuteSource = ExtractCSharpMethodBody(
    mainPageMigrationSource,
    "private async Task PrepareOrExecuteWorkflowAsync(");
Assert(prepareOrExecuteSource.Contains(
           "MigrationWorkflowPolicy.CanStartAnotherSync(",
           StringComparison.Ordinal) &&
       prepareOrExecuteSource.Contains(
           "await ChangeWorkflowPairAsync(sourceId, targetId);",
           StringComparison.Ordinal),
    "RepeatSync: activating the completed footer must re-open the retained pair through fresh read-only catalog preparation rather than reusing the old accepted plan.");
var coordinatorSource = File.ReadAllText(Path.Combine(
    repositoryRoot,
    "src",
    "BlockFerry.App.WinUI",
    "Services",
    "MigrationWorkflowCoordinator.cs"));
var prepareCatalogsSource = ExtractCSharpMethodBody(
    coordinatorSource,
    "private async Task PrepareCatalogsAsync(");
Assert(prepareCatalogsSource.Contains(
           "Phase = MigrationWorkflowPhase.Discovering,",
           StringComparison.Ordinal) &&
       prepareCatalogsSource.Contains(
           "MigrationProgressStage.Revalidating",
           StringComparison.Ordinal),
    "RepeatSync: selecting the same pair again must publish read-only revalidation progress before exposing a new selection catalog.");

var closeRequestMethod = typeof(DrawerModalLifecycleCoordinator).GetMethod(
    "RequestClose",
    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
    binder: null,
    types: [typeof(bool)],
    modifiers: null);
Assert(closeRequestMethod is not null,
    "MutationDrawerDismissal: the shared drawer lifecycle must expose one mutation-aware close request guard.");
var guardedLifecycle = new DrawerModalLifecycleCoordinator();
var openingGeneration = guardedLifecycle.BeginOpening();
Assert(guardedLifecycle.TryCompleteOpening(openingGeneration),
    "MutationDrawerDismissal/shared lifecycle: the fixture drawer must reach Open before requesting close.");
var rejected = closeRequestMethod!.Invoke(guardedLifecycle, [true]);
Assert(rejected is not null &&
       string.Equals(
           rejected.GetType().GetProperty("Outcome")?.GetValue(rejected)?.ToString(),
           "RejectedMutation",
           StringComparison.Ordinal) &&
       guardedLifecycle.Phase == DrawerModalPhase.Open,
    "MutationDrawerDismissal/shared lifecycle: mutation must reject the close and retain the live drawer.");

var accepted = closeRequestMethod.Invoke(guardedLifecycle, [false]);
var acceptedGeneration = accepted?.GetType().GetProperty("Generation")?.GetValue(accepted) as long?;
Assert(accepted is not null &&
       string.Equals(
           accepted.GetType().GetProperty("Outcome")?.GetValue(accepted)?.ToString(),
           "Closing",
           StringComparison.Ordinal) &&
       acceptedGeneration is > 0 &&
       guardedLifecycle.Phase == DrawerModalPhase.Closing &&
       guardedLifecycle.TryCompleteClosing(acceptedGeneration.Value) &&
       guardedLifecycle.Phase == DrawerModalPhase.Collapsed,
    "MutationDrawerDismissal/shared lifecycle: the guard must close after a legal terminal state.");

var closeDrawerSource = ExtractCSharpMethodBody(mainPageSource, "private void CloseDrawer()");
Assert(closeDrawerSource.Contains("_workflow?.State.IsMutationInProgress == true", StringComparison.Ordinal) &&
       closeDrawerSource.Contains("_drawerLifecycle.RequestClose(isMutationInProgress)", StringComparison.Ordinal) &&
       closeDrawerSource.Contains("FocusDrawerLiveStatus();", StringComparison.Ordinal),
    "MutationDrawerDismissal: CloseDrawer itself must reject mutation-time requests and restore live-status focus.");
var backgroundExecutionCloseSource = ExtractCSharpMethodBody(
    mainPageSource,
    "private void CloseDrawerForBackgroundExecution()");
Assert(backgroundExecutionCloseSource.Contains(
           "_workflow?.State.IsMutationInProgress != true",
           StringComparison.Ordinal) &&
       backgroundExecutionCloseSource.Contains(
           "_drawerLifecycle.RequestClose(isMutationInProgress: false)",
           StringComparison.Ordinal),
    "ExecutionWorkspaceNavigation: only an active internal transaction projection may bypass the visible drawer-close guard to reveal home progress.");
foreach (var (handlerName, signature) in new[]
         {
             ("DrawerCloseButton_Click", "private void DrawerCloseButton_Click(object sender, RoutedEventArgs e)"),
             ("DrawerScrim_Tapped", "private void DrawerScrim_Tapped(object sender, TappedRoutedEventArgs e)"),
             ("PageRoot_KeyDown", "private void PageRoot_KeyDown(object sender, KeyRoutedEventArgs e)"),
         })
{
    Assert(RouteMethodCallsFinalGuard(mainPageSource, signature),
        $"MutationDrawerDismissal/{handlerName}: every visible dismissal route must use the guarded shared close.");
    var routeWithoutItsOwnClose = RemoveFirstInvocationFromMethod(
        mainPageSource,
        signature,
        "CloseDrawer();");
    Assert(!RouteMethodCallsFinalGuard(routeWithoutItsOwnClose, signature),
        $"ExactMethodRouteMapping/{handlerName}: route proof must not borrow a later method's CloseDrawer call.");
}

var escapeHandlerSource = ExtractCSharpMethodBody(
    mainPageSource,
    "private void PageRoot_KeyDown(object sender, KeyRoutedEventArgs e)");
Assert(escapeHandlerSource.Contains("e.Key == VirtualKey.Escape", StringComparison.Ordinal) &&
       escapeHandlerSource.Contains("DrawerLayer.Visibility == Visibility.Visible", StringComparison.Ordinal) &&
       escapeHandlerSource.Contains("e.Handled = true;", StringComparison.Ordinal) &&
       escapeHandlerSource.Contains("CloseDrawer();", StringComparison.Ordinal),
    "MutationDrawerDismissal/Escape: the exact Escape handler body must handle the key and call the final close guard.");

var drawerScrimElement = mainPageMarkup
    .Descendants()
    .Single(element => (string?)element.Attribute(xamlNamespace + "Name") == "DrawerScrim");
Assert(drawerScrimElement.Name.LocalName is "Grid" or "Border",
    "The drawer scrim must use a non-button surface so pointer movement cannot trigger default button visual states.");
Assert((string?)drawerScrimElement.Attribute("Tapped") == "DrawerScrim_Tapped",
    "The drawer scrim must close through its tapped handler.");
Assert(drawerScrimElement.Attribute("Click") is null,
    "The drawer scrim must not retain a button click handler.");
Assert((string?)drawerScrimElement.Attribute("AutomationProperties.AccessibilityView") == "Raw",
    "The drawer scrim must stay out of the accessibility control view.");
Assert((string?)drawerScrimElement.Attribute("Background") == "Transparent",
    "The full-window migration workspace must not leave a brightening scrim over an inert side scene.");
Assert((string?)drawerScrimElement.Attribute("IsTabStop") == "False",
    "The drawer scrim must remain outside keyboard tab navigation.");
Assert((string?)drawerScrimElement.Attribute("IsHitTestVisible") == "True",
    "The drawer scrim must intercept pointer input throughout modal transitions.");
Assert(drawerScrimElement.Attribute("PointerEntered") is null &&
       drawerScrimElement.Attribute("PointerMoved") is null &&
       !drawerScrimElement.Descendants().Any(element => element.Name.LocalName == "VisualState"),
    "The drawer scrim must not gain hover handlers or visual states that brighten the inert left scene.");
var drawerCloseButtonElement = RequireNamedElement(mainPageMarkup, xamlNamespace, "DrawerCloseButton");
Assert(!string.IsNullOrWhiteSpace(
           (string?)drawerCloseButtonElement.Attribute("AutomationProperties.HelpText")) &&
       !string.IsNullOrWhiteSpace(
           (string?)drawerCloseButtonElement.Attribute("AutomationProperties.ItemStatus")),
    "The drawer close button must expose persistent accessible help and status for its busy disablement.");
Assert(mainPageMigrationSource.Contains("DrawerCloseButton.IsEnabled = !isMutationInProgress;", StringComparison.Ordinal) &&
       mainPageMigrationSource.Contains("AutomationProperties.SetItemStatus(", StringComparison.Ordinal) &&
       mainPageMigrationSource.Contains("DrawerCloseButton", StringComparison.Ordinal),
    "The visible drawer close affordance must project the current mutation busy/disabled state.");
var footerStatusHost = RequireNamedElement(mainPageMarkup, xamlNamespace, "FooterStatusHost");
Assert(footerStatusHost.Name.LocalName == "ContentControl" &&
       (string?)footerStatusHost.Attribute("IsTabStop") == "True" &&
       (string?)footerStatusHost.Attribute("UseSystemFocusVisuals") == "True",
    "The drawer's unique live footer status host must accept visible programmatic focus after a rejected close.");
var politeLiveRegions = selectionMarkup
    .Descendants()
    .Concat(categoryMarkup.Descendants())
    .Concat(mainPageMarkup.Descendants())
    .Where(element => element.Attributes().Any(attribute =>
        attribute.Name.LocalName == "AutomationProperties.LiveSetting" &&
        attribute.Value == "Polite"))
    .ToArray();
Assert(politeLiveRegions.Length == 1 &&
       (string?)politeLiveRegions[0].Attribute(xamlNamespace + "Name") == "SelectedCountFooterText",
    "The fixed drawer footer must be the only polite selected-count live region.");

Assert(OptionCategoryPresentation.GetSymbol(OptionSettingCategory.LanguageAndInterface) == Symbol.Globe,
    "语言与界面分类必须使用内置 Globe symbol。");
Assert(OptionCategoryPresentation.GetSymbol(OptionSettingCategory.Controls) == Symbol.Keyboard,
    "Controls must use the built-in Keyboard symbol.");
Assert(OptionCategoryPresentation.GetSymbol(OptionSettingCategory.SoundAndDisplay) == Symbol.Volume,
    "Sound and display must use the built-in Volume symbol.");
Assert(OptionCategoryPresentation.GetSymbol(OptionSettingCategory.OtherPlayerSettings) == Symbol.Setting,
    "Other player settings must use the built-in Setting symbol.");
Assert(OptionCategoryPresentation.FormatSummary(OptionCategorySelectionState.Selected, 5, 5) == "\u5DF2\u9009 \u00B7 5/5",
    "A fully selected category must show the exact selected summary.");
Assert(OptionCategoryPresentation.FormatSummary(OptionCategorySelectionState.Partial, 2, 5) == "\u5DF2\u9009 \u00B7 2/5",
    "A partially selected category must show the exact selected summary.");
Assert(OptionCategoryPresentation.FormatSummary(OptionCategorySelectionState.Unselected, 0, 5) == "\u672A\u9009\u62E9 \u00B7 \u5171 5 \u9879",
    "An unselected category must show the exact unselected summary.");

var emptySelection = new OptionsSelectionViewModel();
var emptySelectionEvents = 0;
emptySelection.SelectionChanged += (_, _) => emptySelectionEvents++;
emptySelection.SelectAll();
Assert(emptySelection.Categories.Count == 0 && emptySelectionEvents == 0,
    "SelectAll must do nothing before a selection state exists.");

var catalog = CreateCatalog();
var selection = new OptionsSelectionViewModel();

selection.Reset(catalog);
Assert(selection.SelectableCount == 3, "The catalog must expose all three selectable settings.");
Assert(selection.SelectedCount == 3, "Loading a catalog must select every setting by default.");
Assert(selection.HasSelection, "A default-all catalog must report an active selection.");
Assert(selection.Categories.All(category => category.SelectionState == OptionCategorySelectionState.Selected),
    "Every category must begin in the selected state.");

var explicitlyEmptySelection = new OptionsSelectionViewModel();
explicitlyEmptySelection.Reset(
    catalog,
    new HashSet<string>(StringComparer.Ordinal));
Assert(explicitlyEmptySelection.SelectedCount == 0 &&
       explicitlyEmptySelection.Categories.All(category =>
           category.SelectionState == OptionCategorySelectionState.Unselected &&
           category.Settings.All(setting => !setting.IsSelected)),
    "A real catalog must preserve an explicit default-empty selection while the demo overload remains default-all.");

var controls = selection.Categories.Single(category => category.Category == OptionSettingCategory.Controls);
var sensitivity = controls.Settings.Single(setting => setting.Key == "mouseSensitivity");
OptionsSelectionChangedEventArgs? observedChange = null;
selection.SelectionChanged += (_, change) => observedChange = change;

sensitivity.IsSelected = false;
Assert(controls.SelectionState == OptionCategorySelectionState.Partial,
    "Clearing one setting must place its category in the partial state.");
Assert(selection.SelectedCount == 2, "Clearing one setting must decrement the selected count.");
Assert(observedChange is { SelectedCount: 2, SelectableCount: 3, HasSelection: true },
    "A setting change must publish the updated aggregate selection state.");

selection.ToggleCategory(controls);
Assert(controls.SelectionState == OptionCategorySelectionState.Selected,
    "Clicking a partial category must select the whole category.");
Assert(controls.Settings.All(setting => setting.IsSelected),
    "Selecting a partial category must select each child setting.");
Assert(selection.SelectedCount == 3, "Selecting a partial category must restore the aggregate count.");

selection.ToggleCategory(controls);
Assert(controls.SelectionState == OptionCategorySelectionState.Unselected,
    "Clicking a selected category must clear the whole category.");
Assert(controls.Settings.All(setting => !setting.IsSelected),
    "Clearing a selected category must clear each child setting.");
Assert(selection.SelectedCount == 1, "Clearing the controls category must retain only the other category.");

var categoryReferencesBeforeSelectAll = selection.Categories.ToArray();
var settingReferencesBeforeSelectAll = selection.Categories
    .SelectMany(category => category.Settings)
    .ToArray();
var selectAllEvents = 0;
var controlsPropertyChanges = new List<string>();
selection.SelectionChanged += (_, _) => selectAllEvents++;
controls.PropertyChanged += (_, change) => controlsPropertyChanges.Add(change.PropertyName!);

selection.SelectAll();

Assert(selection.Categories.All(category => category.SelectionState == OptionCategorySelectionState.Selected) &&
       selection.Categories.SelectMany(category => category.Settings).All(setting => setting.IsSelected),
    "SelectAll must select every existing category and setting.");
Assert(selectAllEvents == 1,
    "SelectAll must publish exactly one aggregate selection event.");
Assert(selection.Categories.Count == categoryReferencesBeforeSelectAll.Length &&
       selection.Categories.Zip(categoryReferencesBeforeSelectAll).All(pair => ReferenceEquals(pair.First, pair.Second)),
    "SelectAll must preserve every existing category view-model object.");
var settingReferencesAfterSelectAll = selection.Categories
    .SelectMany(category => category.Settings)
    .ToArray();
Assert(settingReferencesAfterSelectAll.Length == settingReferencesBeforeSelectAll.Length &&
       settingReferencesAfterSelectAll.Zip(settingReferencesBeforeSelectAll).All(pair => ReferenceEquals(pair.First, pair.Second)),
    "SelectAll must preserve every existing setting view-model object.");
Assert(selection.SnapshotSelectedKeys().SetEquals(["lang", "mouseSensitivity", "key_key.forward"]) &&
       !selection.SnapshotSelectedKeys().Contains("LANG"),
    "SelectAll must preserve the ordinal selected-key snapshot contract.");
Assert(controlsPropertyChanges.Count(propertyName => propertyName == nameof(OptionCategoryViewModel.SelectionState)) == 1 &&
       controlsPropertyChanges.Count(propertyName => propertyName == nameof(OptionCategoryViewModel.IsChecked)) == 1 &&
       controlsPropertyChanges.Count(propertyName => propertyName == nameof(OptionCategoryViewModel.SelectedCount)) == 1 &&
       controlsPropertyChanges.Count(propertyName => propertyName == nameof(OptionCategoryViewModel.SelectionSummary)) == 1,
    "An applied category state update must notify each selection presentation property exactly once.");

selection.Reset(catalog);
Assert(selection.SelectedCount == 3 && selection.Categories.All(category => category.SelectionState == OptionCategorySelectionState.Selected),
    "Resetting the same catalog must restore the default-all selection.");

var snapshot = selection.SnapshotSelectedKeys();
Assert(snapshot.SetEquals(["lang", "mouseSensitivity", "key_key.forward"]),
    "The selected-key snapshot must contain the selected technical keys.");
Assert(!snapshot.Contains("LANG"), "Selected-key snapshots must use ordinal case-sensitive keys.");
Assert(snapshot is not ISet<string>,
    "A selected-key snapshot must not expose a mutable set interface.");
Assert(snapshot is not ICollection<string>,
    "A selected-key snapshot must not expose another mutable collection interface.");

var escapedSelection = new OptionsSelectionViewModel();
escapedSelection.Reset(new OptionsSelectionCatalog(
    [
        new OptionSettingDescriptor(
            "custom\u0001option",
            "Custom option",
            "custom option",
            OptionSettingCategory.OtherPlayerSettings,
            "source",
            "target"),
    ],
    [],
    [],
    []));
Assert(escapedSelection.Categories.Single().Settings.Single().EscapedTechnicalKey == "custom\\u0001option",
    "Accessibility text must expose the complete escaped technical key.");

var partialPresentationSelection = new OptionsSelectionViewModel();
partialPresentationSelection.Reset(new OptionsSelectionCatalog(
    [
        new OptionSettingDescriptor(
            "control.one",
            "Control one",
            "control one",
            OptionSettingCategory.Controls,
            "source",
            "target"),
        new OptionSettingDescriptor(
            "control.two",
            "Control two",
            "control two",
            OptionSettingCategory.Controls,
            "source",
            "target"),
        new OptionSettingDescriptor(
            "control.three",
            "Control three",
            "control three",
            OptionSettingCategory.Controls,
            "source",
            "target"),
    ],
    [],
    [],
    []));
var partialControls = partialPresentationSelection.Categories.Single();
partialControls.Settings[0].IsSelected = false;
Assert(partialControls.SelectionState == OptionCategorySelectionState.Partial &&
       partialControls.SelectedCount == 2 &&
       partialControls.TotalCount == 3 &&
       partialControls.SelectionSummary == "\u5DF2\u9009 \u00B7 2/3",
    "A three-setting category must present its initial partial 2/3 selection exactly.");
var partialToPartialPropertyChanges = new List<string>();
partialControls.PropertyChanged += (_, change) => partialToPartialPropertyChanges.Add(change.PropertyName!);

partialControls.Settings[1].IsSelected = false;

Assert(partialControls.SelectionState == OptionCategorySelectionState.Partial &&
       partialControls.SelectedCount == 1 &&
       partialControls.TotalCount == 3 &&
       partialControls.SelectionSummary == "\u5DF2\u9009 \u00B7 1/3",
    "A partial-to-partial update must refresh the exact 1/3 Chinese presentation.");
Assert(partialToPartialPropertyChanges.Count(propertyName => propertyName == nameof(OptionCategoryViewModel.SelectionState)) == 1 &&
       partialToPartialPropertyChanges.Count(propertyName => propertyName == nameof(OptionCategoryViewModel.IsChecked)) == 1 &&
       partialToPartialPropertyChanges.Count(propertyName => propertyName == nameof(OptionCategoryViewModel.SelectedCount)) == 1 &&
       partialToPartialPropertyChanges.Count(propertyName => propertyName == nameof(OptionCategoryViewModel.SelectionSummary)) == 1,
    "A partial-to-partial update must notify every category selection presentation property exactly once.");

selection.Clear();
Assert(selection.Categories.Count == 0, "Clearing selection must remove every category.");
Assert(selection.SelectedCount == 0 && selection.SelectableCount == 0 && !selection.HasSelection,
    "Clearing selection must reset aggregate state.");
Assert(selection.SnapshotSelectedKeys().Count == 0, "Clearing selection must return an empty key snapshot.");
Assert(snapshot.Count == 3, "A selected-key snapshot must remain immutable after later selection changes.");

var normalMotion = new OptionExpansionMotionCoordinator();
var expandingPlan = normalMotion.BeginTransition(
    expanded: true,
    currentHeight: 32,
    currentOpacity: 0.30,
    expandedHeight: 96);
Assert(expandingPlan.AnimationKind == OptionExpansionAnimationKind.HeightAndOpacity,
    "Normal motion must animate both height and opacity.");
Assert(expandingPlan.Duration == TimeSpan.FromMilliseconds(180),
    "Normal motion must use the 180 ms expansion duration.");
Assert(expandingPlan.FromHeight == 32 && expandingPlan.FromOpacity == 0.30,
    "A normal expansion must begin at the current rendered presentation.");
Assert(expandingPlan.ToHeight == 96 && expandingPlan.ToOpacity == 1,
    "A normal expansion must target the fully expanded presentation.");

var reversingPlan = normalMotion.BeginTransition(
    expanded: false,
    currentHeight: 57,
    currentOpacity: 0.61,
    expandedHeight: 96);
Assert(reversingPlan.Generation > expandingPlan.Generation,
    "A rapid reversal must receive a newer transition generation.");
Assert(reversingPlan.FromHeight == 57 && reversingPlan.FromOpacity == 0.61,
    "A rapid reversal must continue from the current rendered presentation.");
Assert(reversingPlan.ToHeight == 0 && reversingPlan.ToOpacity == 0,
    "A collapse reversal must target the collapsed presentation.");
Assert(!normalMotion.TryComplete(expandingPlan.Generation),
    "A stale completion callback must not complete after a rapid reversal.");
Assert(normalMotion.TryComplete(reversingPlan.Generation),
    "The current transition completion must be accepted once.");
Assert(!normalMotion.TryComplete(reversingPlan.Generation),
    "A duplicate completion callback must be ignored.");

var accessibilityMotion = new OptionExpansionMotionCoordinator();
var normalBeforeModeChange = accessibilityMotion.BeginTransition(
    expanded: true,
    currentHeight: 24,
    currentOpacity: 0.25,
    expandedHeight: 96);
Assert(accessibilityMotion.ChangeMode(OptionExpansionMotionMode.Reduced),
    "Changing to reduced motion must report a motion-mode change.");
Assert(!accessibilityMotion.TryComplete(normalBeforeModeChange.Generation),
    "Changing motion mode must invalidate an in-flight transition completion.");

var reducedPlan = accessibilityMotion.BeginTransition(
    expanded: true,
    currentHeight: 0,
    currentOpacity: 0.40,
    expandedHeight: 96);
Assert(reducedPlan.AnimationKind == OptionExpansionAnimationKind.OpacityOnly,
    "Reduced motion must not produce a height animation.");
Assert(reducedPlan.Duration == TimeSpan.FromMilliseconds(120),
    "Reduced motion must use the 120 ms opacity duration.");
Assert(reducedPlan.FromOpacity == 0.40 && reducedPlan.ToOpacity == 1,
    "Reduced expansion opacity must continue from its current presentation.");

Assert(accessibilityMotion.ChangeMode(OptionExpansionMotionMode.HighContrast),
    "Changing to high contrast must report a motion-mode change.");
Assert(!accessibilityMotion.TryComplete(reducedPlan.Generation),
    "Changing to high contrast must invalidate the reduced-motion completion.");
var highContrastPlan = accessibilityMotion.BeginTransition(
    expanded: false,
    currentHeight: 96,
    currentOpacity: 1,
    expandedHeight: 96);
Assert(highContrastPlan.AnimationKind == OptionExpansionAnimationKind.Immediate,
    "High contrast must complete expansion state without animation.");
Assert(highContrastPlan.Duration == TimeSpan.Zero,
    "An immediate high-contrast transition must have zero duration.");

var sound = new FakeCompletionSoundPlayer();
var gate = new CompletionSoundGate(sound);
var generations = new OperationGenerationCounter();
var firstCommittedGeneration = generations.Next();
var secondCommittedGeneration = generations.Next();
Assert(secondCommittedGeneration > firstCommittedGeneration, "Every request must receive a new monotonic generation.");
var firstTransaction = new BlockFerry.Core.Transactions.TransactionId(Guid.NewGuid());
var secondTransaction = new BlockFerry.Core.Transactions.TransactionId(Guid.NewGuid());
var committedPresentationMethod = typeof(CompletionSoundGate).GetMethod(
    "TryPlayCommitted",
    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
    System.Reflection.BindingFlags.NonPublic,
    binder: null,
    types:
    [
        typeof(long),
        typeof(TransactionId),
        typeof(bool),
        typeof(bool),
        typeof(bool),
        typeof(bool),
        typeof(bool),
    ],
    modifiers: null);
Assert(committedPresentationMethod is not null,
    "CommittedPresentationProof: sound gating must require durable commit, presented result, accepted focus, valid peer, and successful notification evidence.");
bool TryPlayWithProof(
    long generation,
    TransactionId transactionId,
    bool durableVerifiedCommit,
    bool resultPresented,
    bool focusAccepted,
    bool validAutomationPeer,
    bool notificationInvokedSuccessfully) =>
    committedPresentationMethod!.Invoke(
        gate,
        [
            generation,
            transactionId,
            durableVerifiedCommit,
            resultPresented,
            focusAccepted,
            validAutomationPeer,
            notificationInvokedSuccessfully,
        ]) is true;

gate.ResetForNewGeneration(firstCommittedGeneration);
foreach (var silentCase in new[]
         {
             (Name: "preview", Durable: false, Presented: true, Focus: true, Peer: true, Announced: true),
             (Name: "failed", Durable: false, Presented: true, Focus: true, Peer: true, Announced: true),
             (Name: "blocked", Durable: false, Presented: true, Focus: true, Peer: true, Announced: true),
             (Name: "recovery-required", Durable: false, Presented: true, Focus: true, Peer: true, Announced: true),
             (Name: "result-not-presented", Durable: true, Presented: false, Focus: true, Peer: true, Announced: true),
             (Name: "focus-rejected", Durable: true, Presented: true, Focus: false, Peer: true, Announced: true),
             (Name: "missing-peer", Durable: true, Presented: true, Focus: true, Peer: false, Announced: false),
             (Name: "invalid-peer", Durable: true, Presented: true, Focus: true, Peer: false, Announced: true),
             (Name: "announcement-failed", Durable: true, Presented: true, Focus: true, Peer: true, Announced: false),
         })
{
    Assert(!TryPlayWithProof(
            firstCommittedGeneration,
            firstTransaction,
            silentCase.Durable,
            silentCase.Presented,
            silentCase.Focus,
            silentCase.Peer,
            silentCase.Announced),
        $"CommittedPresentationProof/{silentCase.Name}: incomplete evidence must remain silent without consuming the transaction gate.");
}

Assert(sound.Count == 0,
    "CommittedPresentationProof: preview, failure, recovery, missing focus, and invalid announcement paths must remain silent.");
Assert(TryPlayWithProof(firstCommittedGeneration, firstTransaction, true, true, true, true, true),
    "CommittedGenerationPlaysExactlyOnce: one fully presented verified commit must play.");
Assert(!TryPlayWithProof(firstCommittedGeneration, firstTransaction, true, true, true, true, true),
    "CommittedGenerationPlaysExactlyOnce: the same committed transaction must not play twice.");
gate.ResetForNewGeneration(secondCommittedGeneration);
Assert(!TryPlayWithProof(firstCommittedGeneration, firstTransaction, true, true, true, true, true),
    "CommittedGenerationPlaysExactlyOnce: a stale generation must remain silent.");
Assert(TryPlayWithProof(secondCommittedGeneration, secondTransaction, true, true, true, true, true),
    "CommittedGenerationPlaysExactlyOnce: a later verified committed transaction may play.");
Assert(sound.Count == 2, "Exactly two distinct verified committed generations must sound.");

var presentWorkflowResultStart = mainPageMigrationSource.IndexOf(
    "private void PresentWorkflowResult(",
    StringComparison.Ordinal);
var presentCommittedHomeFeedbackStart = mainPageMigrationSource.IndexOf(
    "private void PresentCommittedHomeFeedback(",
    presentWorkflowResultStart,
    StringComparison.Ordinal);
var updateWorkflowFooterStart = mainPageMigrationSource.IndexOf(
    "private void UpdateWorkflowFooter(",
    presentCommittedHomeFeedbackStart,
    StringComparison.Ordinal);
Assert(presentWorkflowResultStart >= 0 &&
       presentCommittedHomeFeedbackStart > presentWorkflowResultStart &&
       updateWorkflowFooterStart > presentCommittedHomeFeedbackStart,
    "CommittedPresentationProof: review rendering and home completion feedback must remain isolated for review.");
var presentWorkflowResultSource =
    mainPageMigrationSource[presentWorkflowResultStart..presentCommittedHomeFeedbackStart];
var committedHomeFeedbackSource =
    mainPageMigrationSource[presentCommittedHomeFeedbackStart..updateWorkflowFooterStart];
var committedResultPresentedIndex = committedHomeFeedbackSource.IndexOf(
    "var resultPresented = _pageLoaded",
    StringComparison.Ordinal);
var committedFocusIndex = committedHomeFeedbackSource.IndexOf(
    "focusAccepted = PrimaryActionButton.Focus(FocusState.Programmatic);",
    StringComparison.Ordinal);
var committedPeerIndex = committedHomeFeedbackSource.IndexOf(
    "FrameworkElementAutomationPeer.CreatePeerForElement(PrimaryActionButton)",
    StringComparison.Ordinal);
var committedAnnouncementIndex = committedHomeFeedbackSource.IndexOf(
    "peer.RaiseNotificationEvent(",
    StringComparison.Ordinal);
var announcementAcceptedIndex = committedHomeFeedbackSource.IndexOf(
    "notificationInvokedSuccessfully = true;",
    StringComparison.Ordinal);
var committedSoundIndex = committedHomeFeedbackSource.IndexOf(
    "TryPlayCommittedSound(",
    StringComparison.Ordinal);
Assert(committedResultPresentedIndex >= 0 &&
       committedFocusIndex > committedResultPresentedIndex &&
       committedPeerIndex > committedFocusIndex &&
       committedAnnouncementIndex > committedPeerIndex &&
       announcementAcceptedIndex > committedAnnouncementIndex &&
       committedSoundIndex > announcementAcceptedIndex &&
       committedHomeFeedbackSource.Contains("catch (Exception)", StringComparison.Ordinal) &&
       !presentWorkflowResultSource.Contains("TryPlayCommittedSound(", StringComparison.Ordinal),
    "CommittedPresentationProof: home presentation, accepted focus, valid peer, and nonthrowing UIA notification must precede the sound gate exactly once.");
var completeDrawerCloseSource = ExtractCSharpMethodBody(
    mainPageSource,
    "private void CompleteDrawerClose(long generation)");
var pageLoadedForCommittedSource = ExtractCSharpMethodBody(
    mainPageSource,
    "private void Page_Loaded(object sender, RoutedEventArgs e)");
var drawerCollapsedIndex = completeDrawerCloseSource.IndexOf(
    "DrawerLayer.Visibility = Visibility.Collapsed;",
    StringComparison.Ordinal);
var retryCommittedFeedbackIndex = completeDrawerCloseSource.IndexOf(
    "RetryCommittedHomeFeedbackFromCurrentState();",
    StringComparison.Ordinal);
var reopenAttentionIndex = completeDrawerCloseSource.IndexOf(
    "OpenDrawer(DrawerCloseButton);",
    StringComparison.Ordinal);
Assert(drawerCollapsedIndex >= 0 &&
       retryCommittedFeedbackIndex > drawerCollapsedIndex &&
       reopenAttentionIndex > retryCommittedFeedbackIndex,
    "CommittedPresentationClosingRace: when a verified commit arrives during drawer closing, the collapsed home must retry focus, announcement, and the exactly-once completion sound before any failure-attention reopen.");
Assert(pageLoadedForCommittedSource.Contains(
           "RetryCommittedHomeFeedbackFromCurrentState();",
           StringComparison.Ordinal) &&
       committedHomeFeedbackSource.Contains("_pageLoaded", StringComparison.Ordinal) &&
       committedHomeFeedbackSource.Contains(
           "_drawerLifecycle.Phase == DrawerModalPhase.Collapsed",
           StringComparison.Ordinal),
    "CommittedPresentationSurface: unloaded pages must defer completion feedback until load, and the authoritative Collapsed lifecycle must agree with visibility.");

var undoEligibilityGateType = typeof(MigrationWorkflowCoordinator).Assembly.GetType(
    "BlockFerry.App.WinUI.Services.UndoEligibilityRefreshGate",
    throwOnError: false);
Assert(undoEligibilityGateType is not null,
    "UndoEligibilityRefresh: production must have one fail-closed read-only refresh gate.");
var undoQueryType = typeof(Func<TransactionId, CancellationToken, Task<bool>>);
var undoEligibilityGateConstructor = undoEligibilityGateType!.GetConstructor(
    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
    System.Reflection.BindingFlags.NonPublic,
    binder: null,
    types: [undoQueryType],
    modifiers: null);
var evaluateUndoEligibilityMethod = undoEligibilityGateType.GetMethod(
    "EvaluateAsync",
    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
    binder: null,
    types: [typeof(MigrationWorkflowState), typeof(CancellationToken)],
    modifiers: null);
Assert(undoEligibilityGateConstructor is not null && evaluateUndoEligibilityMethod is not null,
    "UndoEligibilityRefresh: the gate must evaluate a workflow snapshot through the bounded query seam.");
var eligibilityResults = new Queue<Func<Task<bool>>>(
[
    () => Task.FromResult(true),
    () => Task.FromResult(false),
    () => Task.FromException<bool>(new IOException("fixture read failure")),
]);
var undoQueryCalls = 0;
Func<TransactionId, CancellationToken, Task<bool>> scriptedUndoEligibility = (_, _) =>
{
    undoQueryCalls++;
    return eligibilityResults.Dequeue()();
};
var undoEligibilityGate = undoEligibilityGateConstructor!.Invoke([scriptedUndoEligibility]);
async Task<bool> EvaluateUndoEligibilityAsync(MigrationWorkflowState snapshot)
{
    var invocation = evaluateUndoEligibilityMethod!.Invoke(
        undoEligibilityGate,
        [snapshot, CancellationToken.None]);
    return invocation is Task<bool> task && await task;
}

var eligibilityTransaction = new TransactionId(Guid.NewGuid());
var committedUndoCandidate = MigrationWorkflowState.Initial with
{
    Phase = MigrationWorkflowPhase.Succeeded,
    Generation = 91,
    CommittedTransactionId = eligibilityTransaction,
    LastExecutionStatus = MigrationExecutionStatus.Succeeded,
    CanUndo = false,
};
Assert(await EvaluateUndoEligibilityAsync(committedUndoCandidate),
    "UndoEligibilityRefresh/matching-after-state: Undo may enable only after the read-only query succeeds.");
Assert(!await EvaluateUndoEligibilityAsync(committedUndoCandidate),
    "UndoEligibilityRefresh/reactivation-change: a later mismatch must disable Undo again.");
Assert(!await EvaluateUndoEligibilityAsync(committedUndoCandidate),
    "UndoEligibilityRefresh/read-failure: a read exception must fail closed.");
var callsBeforeMutation = undoQueryCalls;
Assert(!await EvaluateUndoEligibilityAsync(committedUndoCandidate with
{
    Phase = MigrationWorkflowPhase.RollingBack,
    CanUndo = false,
}) &&
       undoQueryCalls == callsBeforeMutation,
    "UndoEligibilityRefresh/mutation: mutation must stay disabled without issuing another eligibility query.");
Assert(!await EvaluateUndoEligibilityAsync(committedUndoCandidate with
{
    CommittedTransactionId = null,
    CanUndo = false,
}),
    "UndoEligibilityRefresh/missing-transaction: an incomplete success snapshot must stay disabled.");

var undoWorkflowCoordinatorSource = File.ReadAllText(Path.Combine(
    repositoryRoot,
    "src",
    "BlockFerry.App.WinUI",
    "Services",
    "MigrationWorkflowCoordinator.cs"));
var coordinatorRefreshStart = undoWorkflowCoordinatorSource.IndexOf(
    "internal async Task RefreshUndoEligibilityAsync(",
    StringComparison.Ordinal);
var coordinatorSoundStart = undoWorkflowCoordinatorSource.IndexOf(
    "internal bool TryPlayCommittedSound(",
    coordinatorRefreshStart,
    StringComparison.Ordinal);
Assert(coordinatorRefreshStart >= 0 && coordinatorSoundStart > coordinatorRefreshStart,
    "UndoEligibilityRefresh: the coordinator must expose a bounded activation refresh entry point.");
var coordinatorRefreshSource = undoWorkflowCoordinatorSource[coordinatorRefreshStart..coordinatorSoundStart];
Assert(coordinatorRefreshSource.Contains("CanUndo = false", StringComparison.Ordinal) &&
       coordinatorRefreshSource.Contains("RefreshUndoEligibilityCoreAsync(", StringComparison.Ordinal),
    "UndoEligibilityRefresh: every refresh must publish disabled state before the read-only proof can re-enable Undo.");
var executeSuccessStart = undoWorkflowCoordinatorSource.IndexOf(
    "if (result.IsSuccess && result.TransactionId is { } committed)",
    StringComparison.Ordinal);
var executeFailureStart = undoWorkflowCoordinatorSource.IndexOf(
    "var unsuccessful = State with",
    executeSuccessStart,
    StringComparison.Ordinal);
var executeSuccessSource = undoWorkflowCoordinatorSource[executeSuccessStart..executeFailureStart];
Assert(executeSuccessSource.Contains("CanUndo = false", StringComparison.Ordinal) &&
       executeSuccessSource.Contains("RefreshUndoEligibilityCoreAsync(committed", StringComparison.Ordinal),
    "UndoEligibilityRefresh: commit success must remain disabled until its current after-state proof completes.");
Assert(undoWorkflowCoordinatorSource.Contains("result = await recoveryService.UndoAsync(", StringComparison.Ordinal),
    "UndoEligibilityRefresh: clicking Undo must retain the existing execution-time full revalidation path.");

var mainWindowSource = File.ReadAllText(Path.Combine(
    repositoryRoot,
    "src",
    "BlockFerry.App.WinUI",
    "MainWindow.xaml.cs"));
Assert(mainWindowSource.Contains("Activated += MainWindow_Activated;", StringComparison.Ordinal) &&
       mainWindowSource.Contains("Activated -= MainWindow_Activated;", StringComparison.Ordinal) &&
       mainWindowSource.Contains("RefreshUndoEligibilityAsync(", StringComparison.Ordinal),
    "UndoEligibilityRefresh: window reactivation must trigger a fresh read-only Undo eligibility check.");
Assert(mainWindowSource.Contains(
           "AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;",
           StringComparison.Ordinal),
    "The custom title-bar actions must share the native tall caption-button baseline.");

Assert(mainWindowSource.Contains("CompositionTarget.Rendering +=", StringComparison.Ordinal) &&
       mainWindowSource.Contains("CompositionTarget.Rendering -=", StringComparison.Ordinal) &&
       mainWindowSource.Contains("ElementCompositionPreview.GetElementVisual(", StringComparison.Ordinal) &&
       Regex.Count(
           mainWindowSource,
           @"\.Offset\s*=\s*new\s+Vector3\s*\(",
           RegexOptions.CultureInvariant) == 2,
     "PointerGlowCompositionContract: pointer following must be synchronized to compositor rendering and update exactly two behind-glass composition offsets.");
var glowRenderingSource = ExtractCSharpMethodBody(
    mainWindowSource,
    "private void GlowFollowRendering(object? sender, object e)");
var pointerGlowSpringSource = File.ReadAllText(Path.Combine(
    repositoryRoot,
    "src",
    "BlockFerry.App.WinUI",
    "Services",
    "PointerGlowSpring.cs"));
var coreFrequencyMatch = Regex.Match(
    mainWindowSource,
    @"GlowCoreAngularFrequency\s*=\s*(?<value>[0-9]+(?:\.[0-9]+)?)",
    RegexOptions.CultureInvariant);
var coreDampingMatch = Regex.Match(
    mainWindowSource,
    @"GlowCoreDampingRatio\s*=\s*(?<value>[0-9]+(?:\.[0-9]+)?)",
    RegexOptions.CultureInvariant);
var trailFrequencyMatch = Regex.Match(
    mainWindowSource,
    @"GlowTrailAngularFrequency\s*=\s*(?<value>[0-9]+(?:\.[0-9]+)?)",
    RegexOptions.CultureInvariant);
var trailDampingMatch = Regex.Match(
    mainWindowSource,
    @"GlowTrailDampingRatio\s*=\s*(?<value>[0-9]+(?:\.[0-9]+)?)",
    RegexOptions.CultureInvariant);
var coreFrequencyParsed = double.TryParse(
    coreFrequencyMatch.Groups["value"].Value,
    System.Globalization.NumberStyles.Float,
    System.Globalization.CultureInfo.InvariantCulture,
    out var coreFrequency);
var coreDampingParsed = double.TryParse(
    coreDampingMatch.Groups["value"].Value,
    System.Globalization.NumberStyles.Float,
    System.Globalization.CultureInfo.InvariantCulture,
    out var coreDamping);
var trailFrequencyParsed = double.TryParse(
    trailFrequencyMatch.Groups["value"].Value,
    System.Globalization.NumberStyles.Float,
    System.Globalization.CultureInfo.InvariantCulture,
    out var trailFrequency);
var trailDampingParsed = double.TryParse(
    trailDampingMatch.Groups["value"].Value,
    System.Globalization.NumberStyles.Float,
    System.Globalization.CultureInfo.InvariantCulture,
    out var trailDamping);
Assert(coreFrequencyMatch.Success &&
       coreDampingMatch.Success &&
       trailFrequencyMatch.Success &&
       trailDampingMatch.Success &&
       coreFrequencyParsed &&
       coreDampingParsed &&
       trailFrequencyParsed &&
       trailDampingParsed &&
       coreFrequency is >= 18 and <= 20 &&
       coreDamping is >= 0.9 and <= 0.94 &&
       trailFrequency is >= 11.5 and <= 13.5 &&
       trailDamping is >= 0.82 and <= 0.9 &&
       mainWindowSource.Contains("_glowVelocityX", StringComparison.Ordinal) &&
       mainWindowSource.Contains("_glowVelocityY", StringComparison.Ordinal) &&
       mainWindowSource.Contains("_trailGlowVelocityX", StringComparison.Ordinal) &&
       mainWindowSource.Contains("_trailGlowVelocityY", StringComparison.Ordinal) &&
       Regex.Count(
           glowRenderingSource,
           @"PointerGlowSpring\.Advance\(",
           RegexOptions.CultureInvariant) == 4 &&
       glowRenderingSource.Contains("remainingSpeed < 4", StringComparison.Ordinal) &&
       pointerGlowSpringSource.Contains("Math.Exp(-dampingRatio * angularFrequency * elapsedSeconds)", StringComparison.Ordinal) &&
       pointerGlowSpringSource.Contains("Math.Sqrt(1 - (dampingRatio * dampingRatio))", StringComparison.Ordinal) &&
       !glowRenderingSource.Contains("responsiveness", StringComparison.Ordinal),
    "PointerGlowMotionContract: pointer light must use two velocity-preserving analytic springs with a connected core and a slower ambient trail.");
Assert(!mainWindowSource.Contains("DispatcherTimer", StringComparison.Ordinal) &&
       !mainWindowSource.Contains("_glowFollowTimer", StringComparison.Ordinal) &&
       !mainWindowSource.Contains("ForegroundPointerGlow", StringComparison.Ordinal) &&
       !mainWindowSource.Contains("ForegroundGlowTransform", StringComparison.Ordinal) &&
       !localizedMainWindowMarkup.Descendants().Any(element =>
           (string?)element.Attribute(xamlNamespace + "Name") is
               "ForegroundPointerGlow" or "ForegroundGlowTransform"),
    "PointerGlowCompositionContract: the fixed-rate UI timer and foreground glow layer must not return.");
var glowCoreElement = RequireNamedElement(localizedMainWindowMarkup, xamlNamespace, "BackgroundPointerGlow");
var glowTrailElement = RequireNamedElement(localizedMainWindowMarkup, xamlNamespace, "TrailPointerGlow");
Assert((string?)glowCoreElement.Attribute("Width") == "220" &&
       (string?)glowCoreElement.Attribute("Height") == "180" &&
       (string?)glowCoreElement.Attribute("Fill") == "{ThemeResource PointerGlowStrongBrush}" &&
       (string?)glowTrailElement.Attribute("Width") == "320" &&
       (string?)glowTrailElement.Attribute("Height") == "264" &&
       (string?)glowTrailElement.Attribute("Fill") == "{ThemeResource PointerGlowWeakBrush}" &&
       glowCoreElement.Parent == glowTrailElement.Parent,
    "PointerGlowCompositionContract: one restrained core and one faint trail must share the same behind-veil canvas.");

var coreAt60Hz = SimulateGlowStep(coreFrequency, coreDamping, 60, durationSeconds: 1);
var coreAt144Hz = SimulateGlowStep(coreFrequency, coreDamping, 144, durationSeconds: 1);
var coreAt240Hz = SimulateGlowStep(coreFrequency, coreDamping, 240, durationSeconds: 1);
var trailAt60Hz = SimulateGlowStep(trailFrequency, trailDamping, 60, durationSeconds: 1);
var trailAt240Hz = SimulateGlowStep(trailFrequency, trailDamping, 240, durationSeconds: 1);
Assert(Math.Abs(coreAt60Hz.Position - coreAt144Hz.Position) < 0.000001 &&
       Math.Abs(coreAt60Hz.Position - coreAt240Hz.Position) < 0.000001 &&
       Math.Abs(trailAt60Hz.Position - trailAt240Hz.Position) < 0.000001,
    "PointerGlowSpringContract: analytic motion must feel the same at 60, 144, and 240 Hz.");
Assert(coreAt240Hz.Maximum <= 100.1 && trailAt240Hz.Maximum <= 100.6,
    "PointerGlowSpringContract: both light layers must avoid a visible bounce past the pointer.");
var steadyRamp = SimulateGlowRamp(
    coreFrequency,
    coreDamping,
    trailFrequency,
    trailDamping,
    speed: 1000,
    updatesPerSecond: 240,
    durationSeconds: 1.5);
Assert(steadyRamp.CoreTrailSeparation is >= 32 and <= 52 &&
       steadyRamp.CoreLag is >= 80 and <= 110 &&
       steadyRamp.TrailLag is >= 120 and <= 155,
    "PointerGlowSpringContract: a fast sweep must retain a connected core and a clearly slower ambient trail.");

Assert(Regex.IsMatch(
           mainWindowSource,
           @"_micaBackdrop\s*\?\?=\s*new\s+MicaBackdrop\s*\(\s*\)",
           RegexOptions.CultureInvariant) &&
       !mainWindowSource.Contains(
           "advancedEffects ? new MicaBackdrop() : null",
           StringComparison.Ordinal),
    "ThemeBackdropLifetimeContract: theme refreshes must reuse one lazily-created Mica backdrop instead of allocating one per refresh.");
Assert(mainWindowSource.Contains("DispatcherQueue.TryEnqueue", StringComparison.Ordinal) &&
       mainWindowSource.Contains("_themeTransitionGeneration", StringComparison.Ordinal) &&
       Regex.IsMatch(
           mainWindowSource,
           @"(?:generation\s*!=\s*_themeTransitionGeneration|_themeTransitionGeneration\s*!=\s*generation)",
           RegexOptions.CultureInvariant) &&
       Regex.IsMatch(
           mainWindowSource,
           @"(?:\+\+_themeTransitionGeneration|_themeTransitionGeneration\+\+)",
           RegexOptions.CultureInvariant),
     "ThemeTransitionQueueContract: theme work must be deferred through the dispatcher and reject stale callbacks with a monotonically changing generation.");

var themeToggleSource = ExtractCSharpMethodBody(
    mainWindowSource,
    "private void ThemeButton_Click(object sender, RoutedEventArgs e)");
var prepareCoverIndex = themeToggleSource.IndexOf(
    "_themeTransitionPending = PrepareBackgroundTransitionCover();",
    StringComparison.Ordinal);
var requestedThemeIndex = themeToggleSource.IndexOf(
    "WindowRoot.RequestedTheme =",
    StringComparison.Ordinal);
var transitionCoverSource = ExtractCSharpMethodBody(
    mainWindowSource,
    "private bool PrepareBackgroundTransitionCover()");
var existingCoverReuseIndex = transitionCoverSource.IndexOf(
    "_themeTransitionPending &&",
    StringComparison.Ordinal);
var freshCoverCaptureIndex = transitionCoverSource.IndexOf(
    "SceneBackgroundImage.Source is not ImageSource outgoingBackground",
    StringComparison.Ordinal);
var backgroundLoadSource = ExtractCSharpMethodBody(
    mainWindowSource,
    "private void EnsureThemeBackground(bool force)");
var backgroundOpenedSource = ExtractCSharpMethodBody(
    mainWindowSource,
    "private void BackgroundImageOpened(BitmapImage image, long generation)");
var beginThemeTransitionSource = ExtractCSharpMethodBody(
    mainWindowSource,
    "private void BeginThemeTransition()");
Assert(prepareCoverIndex >= 0 && requestedThemeIndex > prepareCoverIndex &&
       existingCoverReuseIndex >= 0 && freshCoverCaptureIndex > existingCoverReuseIndex &&
       transitionCoverSource.Contains("PreviousSceneBackgroundImage.Source is not null", StringComparison.Ordinal) &&
       transitionCoverSource.Contains(
            "PreviousSceneBackgroundImage.Source = outgoingBackground;",
           StringComparison.Ordinal) &&
       transitionCoverSource.Contains("PreviousSceneVeil.Fill = SceneVeil.Fill;", StringComparison.Ordinal) &&
       transitionCoverSource.Contains("ThemeTransitionCover.Opacity = 1;", StringComparison.Ordinal),
    "ThemeNoBlankFrameContract: the fully rendered outgoing scene and its veil must cover the window before ThemeResource values switch.");
Assert(themeToggleSource.Contains(
           "(_pendingBackground is not null && !_themeTransitionPending)",
           StringComparison.Ordinal),
    "ThemeNoBlankFrameContract: an undecoded initial bitmap must never be promoted to the outgoing cover.");
var imageOpenedHookIndex = backgroundLoadSource.IndexOf("image.ImageOpened +=", StringComparison.Ordinal);
var sourceAssignmentIndex = backgroundLoadSource.IndexOf("SceneBackgroundImage.Source = image;", StringComparison.Ordinal);
var uriAssignmentIndex = backgroundLoadSource.IndexOf("image.UriSource =", StringComparison.Ordinal);
Assert(imageOpenedHookIndex >= 0 &&
       sourceAssignmentIndex > imageOpenedHookIndex &&
       uriAssignmentIndex > sourceAssignmentIndex &&
       backgroundOpenedSource.Contains("generation != _backgroundLoadGeneration", StringComparison.Ordinal) &&
       backgroundOpenedSource.Contains("ReferenceEquals(_pendingBackground, image)", StringComparison.Ordinal) &&
       backgroundOpenedSource.Contains("BeginThemeTransition();", StringComparison.Ordinal) &&
       !beginThemeTransitionSource.Contains("ContentLayer.Opacity", StringComparison.Ordinal),
    "ThemeNoBlankFrameContract: the incoming bitmap must be generation-checked and decoded before only the background cover dissolves.");
var sceneLayerNames = localizedMainWindowMarkup.Root!
    .Elements()
    .Single(element => (string?)element.Attribute(xamlNamespace + "Name") == "WindowRoot")
    .Elements()
    .Select(element => (string?)element.Attribute(xamlNamespace + "Name"))
    .ToList();
Assert(sceneLayerNames.IndexOf("SceneVeil") >= 0 &&
       sceneLayerNames.IndexOf("ThemeTransitionCover") > sceneLayerNames.IndexOf("SceneVeil") &&
       sceneLayerNames.IndexOf("ContentLayer") > sceneLayerNames.IndexOf("ThemeTransitionCover"),
    "ThemeNoBlankFrameContract: the outgoing scene cover must sit above the active veil and below interactive content.");

var appWindowChangedSource = ExtractCSharpMethodBody(
    mainWindowSource,
    "private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)");
var queueBackgroundResizeSource = ExtractCSharpMethodBody(
    mainWindowSource,
    "private void QueueBackgroundResizeRefresh()");
var completeThemeToggleSource = ExtractCSharpMethodBody(
    mainWindowSource,
    "private void CompleteThemeToggle()");
var cancelThemeTransitionSource = ExtractCSharpMethodBody(
    mainWindowSource,
    "private void CancelThemeTransition(bool restoreOutgoingBackground)");
Assert(mainWindowSource.Contains("BackgroundResizeDebounceMilliseconds = 140", StringComparison.Ordinal) &&
       appWindowChangedSource.Contains("QueueBackgroundResizeRefresh();", StringComparison.Ordinal) &&
       !appWindowChangedSource.Contains("EnsureThemeBackground(", StringComparison.Ordinal) &&
       queueBackgroundResizeSource.Contains("_backgroundResizeTimer.Stop();", StringComparison.Ordinal) &&
       queueBackgroundResizeSource.Contains("_backgroundResizeTimer.Start();", StringComparison.Ordinal),
    "SceneResizeDebounceContract: live resizing must wait for a stable 140 ms window before starting another PNG decode.");
Assert(completeThemeToggleSource.Contains("_backgroundResizePending", StringComparison.Ordinal) &&
       completeThemeToggleSource.Contains("QueueBackgroundResizeRefresh();", StringComparison.Ordinal),
    "SceneResizeDebounceContract: a size change observed during theme switching must be replayed after the transition.");
Assert(cancelThemeTransitionSource.Contains("_backgroundTheme = _outgoingBackgroundTheme;", StringComparison.Ordinal) &&
       cancelThemeTransitionSource.Contains("_backgroundRenderWidthKey = _outgoingBackgroundRenderWidthKey;", StringComparison.Ordinal),
    "ThemeRollbackMetadataContract: restoring the outgoing bitmap must restore its cache identity as well.");

var renderSizePolicySource = ExtractCSharpMethodBody(
    mainWindowSource,
    "private int CalculateBackgroundRenderWidthKey()");
Assert(mainWindowSource.Contains("BackgroundReloadQuantum = 128", StringComparison.Ordinal) &&
       mainWindowSource.Contains("BackgroundSourceWidth = 3172", StringComparison.Ordinal) &&
       mainWindowSource.Contains("BackgroundAspectRatio = 3172d / 1984d", StringComparison.Ordinal) &&
       renderSizePolicySource.Contains("AppWindow.ClientSize", StringComparison.Ordinal) &&
       renderSizePolicySource.Contains("requiredWidth / (double)BackgroundReloadQuantum", StringComparison.Ordinal) &&
       renderSizePolicySource.Contains("Math.Clamp(roundedWidth, BackgroundReloadQuantum, BackgroundSourceWidth)", StringComparison.Ordinal) &&
       !backgroundLoadSource.Contains("DecodePixelWidth", StringComparison.Ordinal) &&
       !backgroundLoadSource.Contains("DecodePixelType", StringComparison.Ordinal),
    "SceneAutoDecodeContract: WinUI must right-size the live background from the real client area without an oversized manual decode and second bilinear shrink.");

var sceneAssetDirectory = Path.Combine(
    repositoryRoot,
    "src",
    "BlockFerry.App.WinUI",
    "Assets");
var darkScenePath = Path.Combine(sceneAssetDirectory, "blockferry-ambient.png");
var lightScenePath = Path.Combine(sceneAssetDirectory, "blockferry-ambient-light.png");
var darkSceneFrame = ReadPngFrameInfo(darkScenePath);
var lightSceneFrame = ReadPngFrameInfo(lightScenePath);
Assert(darkSceneFrame.Width == 3172 && darkSceneFrame.Height == 1984 &&
       lightSceneFrame.Width == 3172 && lightSceneFrame.Height == 1984,
    $"SceneAssetNativeResolutionContract: both ambient PNGs must preserve the selected originals at 3172x1984; dark={darkSceneFrame.Width}x{darkSceneFrame.Height}, light={lightSceneFrame.Width}x{lightSceneFrame.Height}.");
Assert(darkSceneFrame == lightSceneFrame,
    "SceneAssetAlignmentContract: dark and light ambient PNGs must have identical dimensions, bit depth, and color type for an aligned theme dissolve.");
Assert(new FileInfo(darkScenePath).Length > 6_000_000 &&
       new FileInfo(lightScenePath).Length > 6_000_000,
    "SceneAssetDetailContract: both selected PNG originals must retain their full encoded detail instead of a recompressed derivative.");
Assert(darkSceneFrame.BitDepth == 8 && darkSceneFrame.ColorType == 2,
    $"SceneAssetNativeResolutionContract: ambient PNGs must be 8-bit truecolor RGB images; bitDepth={darkSceneFrame.BitDepth}, colorType={darkSceneFrame.ColorType}.");
Assert(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(darkScenePath))) ==
           "C06DADA0E0C42D8C1AF1421CF91AD07E8B96D67D9D9608CFB8586B76C6D818F9" &&
       Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(lightScenePath))) ==
           "1E6E1C208EF1DD538948C3E8FD945F4EF415D0CA2FEF55CEF96BFF5DACEFA35E",
    "SceneAssetOriginalBytesContract: packaged dark and light scenes must remain byte-identical to the two user-selected PNG originals.");

var demoCatalog = DemoOptionsSelectionData.CreateCatalog();
Assert(demoCatalog.SelectableDifferences.Select(item => item.Key).SequenceEqual(
        ["lang", "key_key.jump", "soundCategory_music", "futureOption"]),
    "The deterministic demo catalog must expose the four specified changed settings in a stable order.");
var localizedSelection = new OptionsSelectionViewModel();
localizedSelection.Reset(demoCatalog);
Assert(localizedSelection.Categories.Select(category => category.Title).SequenceEqual(
        ["\u8BED\u8A00\u4E0E\u754C\u9762", "\u6309\u952E\u4E0E\u63A7\u5236", "\u58F0\u97F3\u4E0E\u663E\u793A", "\u5176\u4ED6\u73A9\u5BB6\u8BBE\u7F6E"]),
    "All four category headings must use the exact Chinese selection copy.");
Assert(demoCatalog.SelectableDifferences.Select(item => item.Category).SequenceEqual(
        [
            OptionSettingCategory.LanguageAndInterface,
            OptionSettingCategory.Controls,
            OptionSettingCategory.SoundAndDisplay,
            OptionSettingCategory.OtherPlayerSettings,
        ]),
    "The demo settings must exercise all four production selection categories.");
Assert(demoCatalog.ProtectedDifferences.Select(item => item.Key).SequenceEqual(["resourcePacks"]),
    "The demo catalog must keep resource packs in the protected collection.");
Assert(demoCatalog.ProtectedDifferences.Single().Decision == OptionsMergeDecision.PreserveTarget,
    "The demo protected setting must retain the target value.");
Assert(demoCatalog.TargetOnlyItems.Select(item => item.Key).SequenceEqual(["fullscreenResolution"]),
    "The demo catalog must keep fullscreen resolution in the target-only collection.");
Assert(demoCatalog.TargetOnlyItems.Single().Decision == OptionsMergeDecision.PreserveTargetOnly,
    "The demo target-only setting must remain target-owned.");

var demoPreview = DemoOptionsSelectionData.CreatePreview(
    new HashSet<string>(["lang", "futureOption", "LANG", "unknown"], StringComparer.Ordinal));
Assert(!demoPreview.IsBlocked && !demoPreview.IsStale,
    "An in-memory demo preview must be accepted without a stale or blocked result.");
Assert(demoPreview.SourceOptionsPath is null && demoPreview.TargetOptionsPath is null,
    "An in-memory demo preview must not expose filesystem paths.");
Assert(demoPreview.Content is null,
    "An in-memory demo preview must not imply a writable options.txt payload.");
Assert(demoPreview.PlannedChanges.Select(item => item.Key).SequenceEqual(["lang", "futureOption"]),
    "The demo preview must plan only the ordinal, explicitly selected descriptors.");
Assert(demoPreview.PlannedChanges.All(item =>
        item.Decision == OptionsMergeDecision.UseSource && item.FinalValue == item.SourceValue),
    "Each selected demo descriptor must be represented as a source-value plan.");
Assert(demoPreview.SkippedDifferences.Select(item => item.Key).SequenceEqual(
        ["key_key.jump", "soundCategory_music"]),
    "Unselected demo descriptors must remain separate skipped differences.");
Assert(demoPreview.ProtectedDifferences.Select(item => item.Key).SequenceEqual(["resourcePacks"]),
    "Demo preview results must retain the protected collection separately.");
Assert(demoPreview.TargetOnlyItems.Select(item => item.Key).SequenceEqual(["fullscreenResolution"]),
    "Demo preview results must retain the target-only collection separately.");

const string unsafeResultKey = "future\u0001option";
var safeResultLine = OptionsPreviewResultFormatter.FormatDifference(new OptionsMergeItem(
    unsafeResultKey,
    "source\tvalue",
    "target\rvalue",
    "source\tvalue",
    OptionsMergeDecision.UseSource,
    "selected"));
Assert(safeResultLine.Contains("future\\u0001option", StringComparison.Ordinal),
    "Preview result text must escape control characters in technical keys.");
Assert(safeResultLine.Contains("source\\u0009value", StringComparison.Ordinal) &&
       safeResultLine.Contains("target\\u000Dvalue", StringComparison.Ordinal),
    "Preview result text must escape control characters in displayed values.");
Assert(!safeResultLine.Any(char.IsControl),
    "Preview result text must never expose raw control characters to TextBlock or UI Automation.");
Assert(!safeResultLine.Contains(nameof(OptionsMergeDecision.UseSource), StringComparison.Ordinal),
    "Preview result text must not leak an English implementation enum into Chinese UI.");
Assert(unsafeResultKey == "future\u0001option",
    "Display escaping must not alter the original ordinal option identity.");

var requestSession = new object();
using var activeRequest = new CancellationTokenSource();
Assert(SelectionRequestAcceptance.IsCurrent(
        requestGeneration: 12,
        currentGeneration: 12,
        requestedSession: requestSession,
        currentSession: requestSession,
        isCurrentPair: true,
        activeRequest.Token),
    "A current generation, reference-identical session, current pair, and active token must be accepted.");
Assert(!SelectionRequestAcceptance.IsCurrent(11, 12, requestSession, requestSession, true, activeRequest.Token),
    "A stale generation must never be accepted for presentation or sound.");
Assert(!SelectionRequestAcceptance.IsCurrent(12, 12, requestSession, new object(), true, activeRequest.Token),
    "A replaced selection session must invalidate an otherwise current request.");
Assert(!SelectionRequestAcceptance.IsCurrent(12, 12, requestSession, requestSession, false, activeRequest.Token),
    "A changed source or target pair must invalidate an otherwise current request.");
activeRequest.Cancel();
Assert(!SelectionRequestAcceptance.IsCurrent(12, 12, requestSession, requestSession, true, activeRequest.Token),
    "A canceled request must never be accepted for presentation or sound.");

var sourceCollision = RouteSelectionResolver.Resolve(
    currentSourceId: "atm10-8",
    currentTargetId: "atm10-7",
    changedEndpoint: RouteEndpoint.Source,
    selectedInstanceId: "atm10-7");
Assert(sourceCollision.SourceId == "atm10-7" && sourceCollision.TargetId == "atm10-8",
    "Choosing the current target as the new source must atomically swap the route instead of submitting a duplicate pair.");

var targetCollision = RouteSelectionResolver.Resolve(
    currentSourceId: "atm10-8",
    currentTargetId: "atm10-7",
    changedEndpoint: RouteEndpoint.Target,
    selectedInstanceId: "atm10-8");
Assert(targetCollision.SourceId == "atm10-7" && targetCollision.TargetId == "atm10-8",
    "Choosing the current source as the new target must atomically swap the route instead of submitting a duplicate pair.");

var thirdSource = RouteSelectionResolver.Resolve(
    currentSourceId: "atm10-8",
    currentTargetId: "atm10-7",
    changedEndpoint: RouteEndpoint.Source,
    selectedInstanceId: "atm10-9");
Assert(thirdSource.SourceId == "atm10-9" && thirdSource.TargetId == "atm10-7",
    "Choosing a third source must keep the accepted target unchanged.");

var thirdTarget = RouteSelectionResolver.Resolve(
    currentSourceId: "atm10-8",
    currentTargetId: "atm10-7",
    changedEndpoint: RouteEndpoint.Target,
    selectedInstanceId: "atm10-9");
Assert(thirdTarget.SourceId == "atm10-8" && thirdTarget.TargetId == "atm10-9",
    "Choosing a third target must keep the accepted source unchanged.");

var sourcePickerIdentity = new object();
var targetPickerIdentity = new object();
var submittedRoutes = new List<RouteSelectionPair>();
Func<string, string, Task> recordRoute = (sourceId, targetId) =>
{
    submittedRoutes.Add(new RouteSelectionPair(sourceId, targetId));
    return Task.CompletedTask;
};
var sourceIntentSubmitted = await RouteSelectionIntentDispatcher.DispatchAsync(
    changedPicker: sourcePickerIdentity,
    sourcePicker: sourcePickerIdentity,
    targetPicker: targetPickerIdentity,
    currentSourceId: "atm10-8",
    currentTargetId: "atm10-7",
    selectedInstanceId: "atm10-7",
    submitPairAsync: recordRoute);
Assert(sourceIntentSubmitted &&
       submittedRoutes.SequenceEqual([new RouteSelectionPair("atm10-7", "atm10-8")]),
    "A source-picker collision must submit exactly one swapped pair and never the transient duplicate pair.");

submittedRoutes.Clear();
var targetIntentSubmitted = await RouteSelectionIntentDispatcher.DispatchAsync(
    changedPicker: targetPickerIdentity,
    sourcePicker: sourcePickerIdentity,
    targetPicker: targetPickerIdentity,
    currentSourceId: "atm10-8",
    currentTargetId: "atm10-7",
    selectedInstanceId: "atm10-8",
    submitPairAsync: recordRoute);
Assert(targetIntentSubmitted &&
       submittedRoutes.SequenceEqual([new RouteSelectionPair("atm10-7", "atm10-8")]),
    "A target-picker collision must submit exactly one swapped pair and never the transient duplicate pair.");

submittedRoutes.Clear();
var unrelatedIntentSubmitted = await RouteSelectionIntentDispatcher.DispatchAsync(
    changedPicker: new object(),
    sourcePicker: sourcePickerIdentity,
    targetPicker: targetPickerIdentity,
    currentSourceId: "atm10-8",
    currentTargetId: "atm10-7",
    selectedInstanceId: "atm10-7",
    submitPairAsync: recordRoute);
Assert(!unrelatedIntentSubmitted && submittedRoutes.Count == 0,
    "An unrelated sender must never be treated as the target picker or submit a route.");

var drawerLifecycle = new DrawerModalLifecycleCoordinator();
foreach (var modalPhase in new[]
         {
             DrawerModalPhase.Opening,
             DrawerModalPhase.Open,
             DrawerModalPhase.Closing,
         })
{
    Assert(DrawerModalFocusPolicy.ShouldMoveInside(modalPhase, focusAlreadyWithinDrawer: false),
        $"Focus outside the drawer during {modalPhase} must move inside immediately.");
    Assert(!DrawerModalFocusPolicy.ShouldMoveInside(modalPhase, focusAlreadyWithinDrawer: true),
        $"Focus already inside the drawer during {modalPhase} must not be moved again.");
}

Assert(!DrawerModalFocusPolicy.ShouldMoveInside(
        DrawerModalPhase.Collapsed,
        focusAlreadyWithinDrawer: false),
    "A collapsed drawer must not steal focus from the scene.");

var firstOpeningGeneration = drawerLifecycle.BeginOpening();
Assert(drawerLifecycle.Phase == DrawerModalPhase.Opening,
    "Beginning an open must enter the Opening phase.");
Assert(drawerLifecycle.NormalizeCollapsed(),
    "Unloading during Opening must report one transition to canonical Collapsed.");
Assert(drawerLifecycle.Phase == DrawerModalPhase.Collapsed,
    "Unloading during Opening must leave the lifecycle canonically Collapsed.");
Assert(!drawerLifecycle.TryCompleteOpening(firstOpeningGeneration),
    "An Opening completion captured before unload must be rejected as stale.");

var reopenedGeneration = drawerLifecycle.BeginOpening();
Assert(reopenedGeneration > firstOpeningGeneration &&
       drawerLifecycle.TryCompleteOpening(reopenedGeneration) &&
       drawerLifecycle.Phase == DrawerModalPhase.Open,
    "A lifecycle normalized on unload must allow a fresh open to complete after reload.");
Assert(drawerLifecycle.NormalizeCollapsed(),
    "Unloading from Open must report one transition to canonical Collapsed.");
Assert(!drawerLifecycle.NormalizeCollapsed(),
    "Normalizing an already Collapsed lifecycle must not report a duplicate phase change.");

var closingOpenGeneration = drawerLifecycle.BeginOpening();
Assert(drawerLifecycle.TryCompleteOpening(closingOpenGeneration),
    "A fresh opening must complete before exercising close interruption.");
var closingGeneration = drawerLifecycle.BeginClosing();
Assert(drawerLifecycle.Phase == DrawerModalPhase.Closing,
    "Beginning a close must enter the Closing phase.");
Assert(drawerLifecycle.NormalizeCollapsed(),
    "Unloading during Closing must report one transition to canonical Collapsed.");
Assert(!drawerLifecycle.TryCompleteClosing(closingGeneration),
    "A Closing completion captured before unload must be rejected as stale.");
var afterClosingUnloadGeneration = drawerLifecycle.BeginOpening();
Assert(afterClosingUnloadGeneration > closingGeneration &&
       drawerLifecycle.TryCompleteOpening(afterClosingUnloadGeneration),
    "A close interrupted by unload must not prevent the next loaded open.");

var pointerGlowModal = new PointerGlowModalCoordinator();
foreach (var modalPhase in new[]
         {
             DrawerModalPhase.Opening,
             DrawerModalPhase.Open,
             DrawerModalPhase.Closing,
         })
{
    var phaseDecision = pointerGlowModal.OnDrawerPhaseChanged(modalPhase);
    Assert(phaseDecision.HideGlow && phaseDecision.StopFollow &&
           !phaseDecision.RevealGlow && !phaseDecision.StartFollow,
        $"{modalPhase} must suppress both glow layers and follow motion.");
    Assert(!pointerGlowModal.AllowsGlow,
        $"{modalPhase} must keep timer and reveal guards closed.");

    var modalMove = pointerGlowModal.OnPointerMoved();
    Assert(modalMove.RecordTarget && modalMove.HideGlow && modalMove.StopFollow &&
           !modalMove.InitializeAtTarget && !modalMove.StartFollow && !modalMove.RevealGlow,
        $"Pointer movement during {modalPhase} may record the target but must remain suppressed.");
    Assert(pointerGlowModal.OnPointerMoved() == modalMove,
        $"Repeated pointer movement during {modalPhase} must produce the same suppressed decision.");
}

var collapsedDecision = pointerGlowModal.OnDrawerPhaseChanged(DrawerModalPhase.Collapsed);
Assert(collapsedDecision.HideGlow && collapsedDecision.StopFollow && !collapsedDecision.RevealGlow,
    "Collapsed must keep both glow layers hidden instead of restoring from prior inside state.");
Assert(pointerGlowModal.AwaitingFreshPointerInput,
    "Collapsed must require fresh pointer input before re-arming the glow.");
Assert(!pointerGlowModal.AllowsGlow,
    "Collapsed must not open the timer or reveal guard from stale inside state.");

var firstPostCollapseMove = pointerGlowModal.OnPointerMoved();
Assert(firstPostCollapseMove.RecordTarget && firstPostCollapseMove.InitializeAtTarget &&
       firstPostCollapseMove.RevealGlow && !firstPostCollapseMove.StartFollow,
    "The first fresh move after Collapsed must initialize at its current target and may reveal without a stale sweep.");
Assert(pointerGlowModal.AllowsGlow,
    "A fresh post-collapse pointer input must re-open the timer and reveal guard.");
var followingMove = pointerGlowModal.OnPointerMoved();
Assert(followingMove.RecordTarget && !followingMove.InitializeAtTarget &&
       followingMove.StartFollow && !followingMove.RevealGlow,
    "Later pointer movement may follow normally without repeatedly restarting reveal.");

var enteredAfterClose = new PointerGlowModalCoordinator();
enteredAfterClose.OnDrawerPhaseChanged(DrawerModalPhase.Opening);
var enteredWhileModal = enteredAfterClose.OnPointerEntered();
Assert(enteredWhileModal.RecordTarget && enteredWhileModal.HideGlow && enteredWhileModal.StopFollow &&
       !enteredWhileModal.RevealGlow && !enteredWhileModal.StartFollow,
    "PointerEntered while modal must not arm or reveal the glow.");
var exitedWhileModal = enteredAfterClose.OnPointerExited();
Assert(exitedWhileModal.HideGlow && exitedWhileModal.StopFollow &&
       !exitedWhileModal.RecordTarget && !exitedWhileModal.RevealGlow,
    "PointerExited while modal must remain a hide-and-stop operation.");
enteredAfterClose.OnDrawerPhaseChanged(DrawerModalPhase.Collapsed);
var firstPostCollapseEnter = enteredAfterClose.OnPointerEntered();
Assert(firstPostCollapseEnter.InitializeAtTarget && firstPostCollapseEnter.RevealGlow,
    "A fresh PointerEntered after Collapsed may initialize and re-arm the glow.");
var exitedAfterRearm = enteredAfterClose.OnPointerExited();
Assert(exitedAfterRearm.HideGlow && exitedAfterRearm.StopFollow && !exitedAfterRearm.RevealGlow,
    "PointerExited after re-arm must hide both layers and stop follow motion.");

var firstRepeatedClosing = enteredAfterClose.OnDrawerPhaseChanged(DrawerModalPhase.Closing);
var secondRepeatedClosing = enteredAfterClose.OnDrawerPhaseChanged(DrawerModalPhase.Closing);
Assert(firstRepeatedClosing == secondRepeatedClosing && secondRepeatedClosing.HideGlow &&
       !secondRepeatedClosing.RevealGlow,
    "Repeated modal phase notifications must be deterministic and cannot create a close-time flash.");

Assert(!OptionsSelectionModePolicy.UsesLegacyOptionsSelection(
        workflowAttached: true,
        workflowIsDemo: false),
    "A real migration workflow must own content selection and must not run the legacy options refresh.");
Assert(OptionsSelectionModePolicy.UsesLegacyOptionsSelection(
        workflowAttached: true,
        workflowIsDemo: true),
    "The deterministic demo inside the migration workflow must retain the legacy in-memory options selection.");
Assert(OptionsSelectionModePolicy.UsesLegacyOptionsSelection(
        workflowAttached: false,
        workflowIsDemo: false),
    "The legacy discovery page must retain its options-selection refresh path.");

var retainedPreviewRecovery = OptionsSelectionLifecyclePolicy.DecideRecovery(
    operationWasInFlight: true,
    hasCatalog: true,
    hasUsableSession: true);
Assert(retainedPreviewRecovery.ReturnToSelection &&
       retainedPreviewRecovery.SelectionEnabled &&
       !retainedPreviewRecovery.RefreshNeeded,
    "An interrupted preview with a retained catalog and session must recover to enabled Selection without refresh.");

var missingCatalogRecovery = OptionsSelectionLifecyclePolicy.DecideRecovery(
    operationWasInFlight: true,
    hasCatalog: false,
    hasUsableSession: false);
Assert(missingCatalogRecovery.ReturnToSelection &&
       !missingCatalogRecovery.SelectionEnabled &&
       missingCatalogRecovery.RefreshNeeded,
    "An interrupted operation without a catalog must recover to Selection and request refresh before enabling it.");

var missingSessionRecovery = OptionsSelectionLifecyclePolicy.DecideRecovery(
    operationWasInFlight: true,
    hasCatalog: true,
    hasUsableSession: false);
Assert(missingSessionRecovery.ReturnToSelection &&
       !missingSessionRecovery.SelectionEnabled &&
       missingSessionRecovery.RefreshNeeded,
    "A retained real catalog without its session must request refresh rather than enable stale selection.");

var workflowCoordinatorPath = Path.Combine(
    repositoryRoot,
    "src",
    "BlockFerry.App.WinUI",
    "Services",
    "MigrationWorkflowCoordinator.cs");
Assert(File.Exists(workflowCoordinatorPath),
    "RecoveryPrecedesDiscovery: the production workflow coordinator contract is missing.");
var workflowCoordinatorSource = File.ReadAllText(workflowCoordinatorPath);
var invalidateWorkflowPlanSource = ExtractCSharpMethodBody(
    workflowCoordinatorSource,
    "internal void InvalidatePlan()");
Assert(invalidateWorkflowPlanSource.Contains(
           "MigrationWorkflowPolicy.CanReturnToSelection(",
           StringComparison.Ordinal) &&
       invalidateWorkflowPlanSource.Contains(
           "ReviewItems = Array.Empty<ContentPlanItem>()",
           StringComparison.Ordinal) &&
       invalidateWorkflowPlanSource.Contains("PlannedFileCount = 0", StringComparison.Ordinal) &&
       invalidateWorkflowPlanSource.Contains("PlannedItemCount = 0", StringComparison.Ordinal) &&
       invalidateWorkflowPlanSource.Contains("CanExecute = false", StringComparison.Ordinal),
    "BlockedSelectionRecovery: returning from a failed review must discard only the accepted plan and stale review while preserving the retained catalogs for a fresh read-only check.");
Assert(workflowCoordinatorSource.Contains(
           "string.IsNullOrWhiteSpace(result.Message)",
           StringComparison.Ordinal) &&
       workflowCoordinatorSource.Contains(
           "result.Message",
           StringComparison.Ordinal),
    "Execution result copy must preserve the transaction coordinator's sanitized permission/stale distinction instead of replacing every failure with one stale message.");
var workflowDisposeStart = workflowCoordinatorSource.IndexOf(
    "public void Dispose()",
    StringComparison.Ordinal);
var workflowAcceptDiscoveryStart = workflowCoordinatorSource.IndexOf(
    "private async Task AcceptDiscoveryAsync(",
    workflowDisposeStart,
    StringComparison.Ordinal);
Assert(workflowDisposeStart >= 0 && workflowAcceptDiscoveryStart > workflowDisposeStart,
    "WorkflowShutdown/nonblocking-contract: the coordinator disposal boundary is missing.");
var workflowDisposeSource = workflowCoordinatorSource[
    workflowDisposeStart..workflowAcceptDiscoveryStart];
Assert(workflowDisposeSource.Contains("operationGate.Wait(0)", StringComparison.Ordinal) &&
       workflowDisposeSource.Contains("Task.Run", StringComparison.Ordinal) &&
       !workflowDisposeSource.Contains("operationGate.Wait();", StringComparison.Ordinal),
    "WorkflowShutdown/nonblocking-contract: closing an idle/read-only window must not synchronously wait on an operation whose continuation needs the UI thread.");
Assert(workflowCoordinatorSource.Contains("VerifiedRecoverySelection", StringComparison.Ordinal) &&
       workflowCoordinatorSource.Contains("RecordedTargetIdentity", StringComparison.Ordinal),
    "RecoveryReselectionRequiresPhysicalMatch: recovery folder selection must be bound to the recorded physical identity.");
var fileSavePickerPath = Path.Combine(
    repositoryRoot,
    "src",
    "BlockFerry.App.WinUI",
    "Services",
    "FileSavePickerService.cs");
Assert(File.Exists(fileSavePickerPath) &&
       File.ReadAllText(fileSavePickerPath).Contains("PickSaveFileAsync", StringComparison.Ordinal),
    "DiagnosticExportUsesChosenFileOnly: diagnostic export must use the window-owned save picker result.");

Assert(!MigrationWorkflowPolicy.CanDiscover(
           recoveryCheckPassed: false,
           MigrationWorkflowPhase.AwaitingDiscovery) &&
       MigrationWorkflowPolicy.CanDiscover(
           recoveryCheckPassed: true,
           MigrationWorkflowPhase.AwaitingDiscovery) &&
       !MigrationWorkflowPolicy.CanDiscover(
           recoveryCheckPassed: true,
           MigrationWorkflowPhase.RecoveryRequired),
    "RecoveryGateBlocksDiscovery: discovery must remain unavailable until recovery inspection succeeds.");
await PendingRescanBehaviorFixture.ProveTwoPendingAndUnsuccessfulExecutionAsync();
await PendingRescanBehaviorFixture.ProveCancelledOutcomePublishesBeforeCancellationAsync();
Assert(MigrationWorkflowPolicy.CanApplyMutationProgress(
           currentOperation: 7,
           callbackOperation: 7,
           MigrationWorkflowPhase.Executing) &&
       !MigrationWorkflowPolicy.CanApplyMutationProgress(
           currentOperation: 7,
           callbackOperation: 6,
           MigrationWorkflowPhase.Executing) &&
       !MigrationWorkflowPolicy.CanApplyMutationProgress(
           currentOperation: 7,
           callbackOperation: 7,
           MigrationWorkflowPhase.Succeeded),
    "LateProgressCannotReplaceTerminalState: only current-operation progress may update an active mutation state.");
Assert(!MigrationWorkflowPolicy.CanRecover(
           MigrationRecoveryStatus.AuthenticationFailed,
           targetPathAvailable: true,
           hasVerifiedReselection: true) &&
       !MigrationWorkflowPolicy.CanRecover(
           attentionStatus: null,
           targetPathAvailable: false,
           hasVerifiedReselection: false) &&
       MigrationWorkflowPolicy.CanRecover(
           attentionStatus: null,
           targetPathAvailable: true,
           hasVerifiedReselection: false) &&
       MigrationWorkflowPolicy.CanRecover(
           attentionStatus: null,
           targetPathAvailable: false,
           hasVerifiedReselection: true),
    "RecoveryActionRequiresVerifiedTarget: authentication failure and an unverified moved target must disable recovery.");

var recoveredUndo = MigrationWorkflowPolicy.ResolveUndoResult(
    MigrationRecoveryStatus.Recovered);
var retryableUndo = MigrationWorkflowPolicy.ResolveUndoResult(
    MigrationRecoveryStatus.Blocked);
var interruptedUndo = MigrationWorkflowPolicy.ResolveUndoResult(
    MigrationRecoveryStatus.RecoveryRequired);
var staleUndo = MigrationWorkflowPolicy.ResolveUndoResult(
    MigrationRecoveryStatus.CurrentStateChanged);
Assert(recoveredUndo.Phase == MigrationWorkflowPhase.Succeeded &&
       !recoveredUndo.KeepCommittedTransaction &&
       !recoveredUndo.CanRetryUndo &&
       retryableUndo.Phase == MigrationWorkflowPhase.Succeeded &&
       retryableUndo.KeepCommittedTransaction &&
       retryableUndo.CanRetryUndo &&
       interruptedUndo.Phase == MigrationWorkflowPhase.RecoveryRequired &&
       !interruptedUndo.KeepCommittedTransaction &&
       !interruptedUndo.CanRetryUndo &&
       staleUndo.Phase == MigrationWorkflowPhase.Blocked &&
       !staleUndo.KeepCommittedTransaction &&
       !staleUndo.CanRetryUndo,
    "UndoResultRoutingIsSafe: retryable blocks retain undo, interrupted undo enters recovery, and stale state terminally blocks.");

var reviewDispositions = new[]
{
    PlannedContentDisposition.Add,
    PlannedContentDisposition.Update,
    PlannedContentDisposition.Same,
    PlannedContentDisposition.Unselected,
    PlannedContentDisposition.Protected,
    PlannedContentDisposition.Unsupported,
    PlannedContentDisposition.Conflict,
    PlannedContentDisposition.Skipped,
};
var reviewItems = reviewDispositions.Select((disposition, index) =>
{
    Assert(ContentItemId.TryCreate("vanilla", $"review-{index}", out var id),
        "The review fixture ID must be valid.");
    return ContentPlanItem.Create(
        id,
        disposition,
        disposition == PlannedContentDisposition.Conflict
            ? ConflictResolution.KeepTarget
            : ConflictResolution.Skip,
        index == 0 ? "<C:\\private\\options.txt>" : $"可读摘要 {index}");
}).ToArray();
var reviewGroups = MigrationReviewPresenter.Build(reviewItems);
string[] expectedReviewTitles = ["新增", "更新", "相同", "未选择", "受保护", "不支持", "冲突处理", "已跳过"];
Assert(reviewGroups.Select(group => group.Title).SequenceEqual(
           expectedReviewTitles) &&
       reviewGroups.All(group => group.Count == 1) &&
       reviewGroups[0].Bundles.Single().Items.Single().Summary == ContentUiText.HiddenTechnicalText,
    "ReviewCardsGroupAndSanitize: review rows must use stable groups and never expose rooted technical values.");

Assert(ContentItemId.TryCreate("vanilla", "lang", out var reviewLanguageId),
    "The grouped language review fixture must use a valid bounded ID.");
Assert(ContentItemId.TryCreate("vanilla", "guiScale", out var reviewGuiScaleId),
    "The grouped display review fixture must use a valid bounded ID.");
Assert(ContentItemId.TryCreate("appearance", "dark-mode", out var reviewAppearanceId),
    "The grouped appearance review fixture must use a valid bounded ID.");
var bundledReview = MigrationReviewPresenter.Build(
[
    ContentPlanItem.Create(
        reviewLanguageId,
        PlannedContentDisposition.Add,
        ConflictResolution.Skip,
        "将新增到目标"),
    ContentPlanItem.Create(
        reviewGuiScaleId,
        PlannedContentDisposition.Add,
        ConflictResolution.Skip,
        "将新增到目标"),
    ContentPlanItem.Create(
        reviewAppearanceId,
        PlannedContentDisposition.Add,
        ConflictResolution.Skip,
        "将新增到目标"),
]);
var bundledAddGroup = bundledReview.Single();
Assert(bundledAddGroup.Title == "新增" &&
       bundledAddGroup.Count == 3 &&
       bundledAddGroup.Bundles.Count == 3 &&
       bundledAddGroup.Bundles.Select(bundle => bundle.Title).SequenceEqual(
           ["语言与界面", "声音与显示", "界面外观"]),
    "ReviewCardsBundleByCategory: repeated rows must collapse into stable, user-facing setting categories.");
Assert(bundledAddGroup.Bundles[0].Items.Single().Title == "语言 · lang" &&
       bundledAddGroup.Bundles[1].Items.Single().Title == "GUI 缩放 · guiScale" &&
       bundledAddGroup.Bundles[2].Items.Single().Title == "深色模式",
    "ReviewCardsExposeClearDetails: expanded rows must identify the setting instead of repeating the same action label.");

Assert(SemanticVersion.TryParse("v0.1.0-beta.5", out var beta5) &&
       SemanticVersion.TryParse("0.1.0-beta.6", out var beta6) &&
       SemanticVersion.TryParse("0.1.0", out var stable) &&
       beta6.CompareTo(beta5) > 0 &&
       stable.CompareTo(beta6) > 0 &&
       !SemanticVersion.TryParse("0.1", out _) &&
       !SemanticVersion.TryParse("0.1.0-beta.04", out _),
    "UpdateSemVerOrdering: prerelease ordering must follow SemVer and reject ambiguous versions.");

var updatePayload = System.Text.Encoding.UTF8.GetBytes(
    """
    [
      {
        "tag_name": "v0.1.0-beta.6",
        "html_url": "https://github.com/Rem021/BlockFerry/releases/tag/v0.1.0-beta.6",
        "draft": false,
        "prerelease": true
      },
      {
        "tag_name": "v9.0.0",
        "html_url": "https://example.com/unsafe",
        "draft": false,
        "prerelease": false
      },
      {
        "tag_name": "v10.0.0",
        "html_url": "https://github.com/Rem021/BlockFerry/releases/tag/v10.0.0",
        "draft": true,
        "prerelease": false
      }
    ]
    """);
var updateResult = GitHubReleaseUpdateChecker.EvaluateReleasePayload(
    BlockFerryReleaseInfo.CurrentVersion,
    updatePayload);
Assert(updateResult.Status == UpdateCheckStatus.UpdateAvailable &&
       updateResult.LatestVersion == "v0.1.0-beta.6" &&
       updateResult.ReleasePage?.Host == "github.com",
    "UpdateCheckTrustBoundary: only a newer non-draft release on the official repository may be shown.");

var noUpdatePayload = System.Text.Encoding.UTF8.GetBytes(
    """
    [
      {
        "tag_name": "v0.1.0-beta.5",
        "html_url": "https://github.com/Rem021/BlockFerry/releases/tag/v0.1.0-beta.5",
        "draft": false,
        "prerelease": true
      }
    ]
    """);
Assert(GitHubReleaseUpdateChecker.EvaluateReleasePayload(
           BlockFerryReleaseInfo.CurrentVersion,
           noUpdatePayload).Status == UpdateCheckStatus.UpToDate,
    "UpdateCheckCurrentRelease: the running release must never advertise itself as an update.");

var appProject = XDocument.Load(Path.Combine(
    repositoryRoot,
    "src",
    "BlockFerry.App.WinUI",
    "BlockFerry.App.WinUI.csproj"));
var versionPrefix = appProject.Descendants("VersionPrefix").Single().Value;
var versionSuffix = appProject.Descendants("VersionSuffix").Single().Value;
Assert(BlockFerryReleaseInfo.CurrentVersion == $"{versionPrefix}-{versionSuffix}",
    "UpdateVersionSingleSourceContract: the update checker and packaged version must remain aligned.");

var mainWindowMarkup = XDocument.Load(Path.Combine(
    repositoryRoot,
    "src",
    "BlockFerry.App.WinUI",
    "MainWindow.xaml"));
var updateButton = mainWindowMarkup
    .Descendants()
    .Single(element => (string?)element.Attribute(xamlNamespace + "Name") == "UpdateButton");
Assert((string?)updateButton.Attribute("Visibility") == "Collapsed" &&
       (string?)updateButton.Attribute("Click") == "UpdateButton_Click",
    "UpdateBannerIsQuietByDefault: the title bar must stay unchanged until a trusted newer release is found.");

Console.WriteLine("PASS: WinUI selection, recovery, motion, update trust boundary, and commit-only sound gating");

static string CurrentTestSource([CallerFilePath] string path = "") => path;

static void ProductionStartsAwaitingDiscovery(XDocument markup, XNamespace xamlNamespace)
{
    Assert(MigrationViewState.AwaitingDiscovery.ModeLabel == "等待发现实例",
        "ProductionStartsAwaitingDiscovery: production must not start in demo mode.");
    Assert(!MigrationViewState.AwaitingDiscovery.IsDemo &&
           !MigrationViewState.AwaitingDiscovery.CanStart,
        "ProductionStartsAwaitingDiscovery: awaiting discovery cannot start a preview.");
    Assert(MigrationViewCopy.DrawerHeaderStatus(MigrationViewState.AwaitingDiscovery) ==
           "等待发现实例 · 0 写入",
        "ProductionStartsAwaitingDiscovery: the drawer must describe the initial state as awaiting discovery.");

    var headerContext = RequireNamedElement(markup, xamlNamespace, "HeaderContextText");
    var modeLabel = RequireNamedElement(markup, xamlNamespace, "ModeLabelText");
    var sourceVersion = RequireNamedElement(markup, xamlNamespace, "SourceVersionRun");
    var targetVersion = RequireNamedElement(markup, xamlNamespace, "TargetVersionRun");
    var packName = RequireNamedElement(markup, xamlNamespace, "PackNameText");
    var progress = RequireNamedElement(markup, xamlNamespace, "SyncProgressBar");
    var primaryAction = RequireNamedElement(markup, xamlNamespace, "PrimaryActionButton");
    var primaryIdleText = RequireNamedElement(markup, xamlNamespace, "PrimaryIdleText");
    var drawerStatus = RequireNamedElement(markup, xamlNamespace, "DrawerHeaderStatusText");
    Assert((string?)headerContext.Attribute("Text") == "等待发现实例 · PCL 2" &&
           (string?)modeLabel.Attribute("Text") == "等待发现实例" &&
           (string?)sourceVersion.Attribute("Text") == "未选择" &&
           (string?)targetVersion.Attribute("Text") == "未选择" &&
           (string?)packName.Attribute("Text") == "等待发现实例",
        "ProductionStartsAwaitingDiscovery: construction-time copy must match the awaiting-discovery state without a demo flash.");
    Assert(!markup.Descendants().Any(element =>
               (string?)element.Attribute(xamlNamespace + "Name") is
                   "SourceInfo" or "SourceInfoText" or "DetailsButton") &&
           markup.Descendants().Count(element =>
               (string?)element.Attribute(xamlNamespace + "Name") == "PrimaryActionButton") == 1,
        "ProductionStartsAwaitingDiscovery: ActionDock must expose one synchronization entry and no duplicate source/details controls.");
    Assert((string?)progress.Attribute("AutomationProperties.Name") == "同步准备与执行进度" &&
           (string?)progress.Attribute("Grid.Row") == "2" &&
           (string?)progress.Attribute("Grid.Column") == "0" &&
           (string?)progress.Attribute("Grid.ColumnSpan") == "5" &&
           (string?)progress.Attribute("Height") == "4" &&
           (string?)primaryAction.Attribute("AutomationProperties.Name") == "打开同步设置选择" &&
           (string?)primaryAction.Attribute("AutomationProperties.HelpText") ==
               "请先发现并选择两个不同实例，再选择内容并检查最终清单" &&
           (string?)primaryIdleText.Attribute("Text") == "选择同步设置" &&
           !markup.Descendants().Any(element =>
               (string?)element.Attribute(xamlNamespace + "Name") is
                   "PrimaryRunningContent" or "PrimaryProgressRing") &&
           (string?)drawerStatus.Attribute("Text") == "等待发现实例 · 0 写入",
        "ProductionStartsAwaitingDiscovery: initial copy must describe the real review workflow, with one full-width progress lane and no floating busy action.");

    var automaticButton = RequireNamedElement(markup, xamlNamespace, "AutomaticDiscoveryButton");
    var pickerButton = RequireNamedElement(markup, xamlNamespace, "FolderPickerButton");
    var demoButton = RequireNamedElement(markup, xamlNamespace, "DemoModeButton");
    Assert((string?)automaticButton.Attribute("Content") == "自动探测" &&
           (string?)automaticButton.Attribute("Click") == "AutomaticDiscoveryButton_Click",
        "ProductionStartsAwaitingDiscovery: the primary automatic-discovery action must use exact copy and one handler.");
    Assert((string?)pickerButton.Attribute("Content") == "选择文件夹" &&
           (string?)pickerButton.Attribute("Click") == "FolderPickerButton_Click",
        "ProductionStartsAwaitingDiscovery: the folder-picker action must use exact copy and one handler.");
    Assert((string?)demoButton.Attribute("Content") == "试用演示" &&
           (string?)demoButton.Attribute("Click") == "DemoModeButton_Click",
        "ProductionStartsAwaitingDiscovery: demo must remain a clearly secondary explicit action.");
}

static void DemoModeKeepsDiscoveryRoutesAvailable()
{
    Assert(DiscoveryEntryVisibilityPolicy.IsVisible(MigrationViewState.AwaitingDiscovery) &&
           DiscoveryEntryVisibilityPolicy.IsVisible(MigrationViewState.Demo),
        "DemoModeKeepsDiscoveryRoutesAvailable: explicit demo mode must keep the routes back to automatic and folder discovery visible.");
}

static void AbsolutePathsAreRedactedFromUi()
{
    const string sourcePath = @"C:\Users\private-user\Games\ATM10-source\options.txt";
    const string targetPath = @"D:\Minecraft\ATM10-target\options.txt";
    var diagnostic = new Pcl2Diagnostic(
        Pcl2DiagnosticCode.CandidatePathInvalid,
        Pcl2DiagnosticSeverity.Warning,
        $"Could not inspect {sourcePath}",
        sourcePath);

    var diagnosticText = DiscoveryUiText.FormatDiagnostic(diagnostic);
    var previewLocations = DiscoveryUiText.FormatPreviewLocations(sourcePath, targetPath);

    Assert(!diagnosticText.Contains(sourcePath, StringComparison.Ordinal) &&
           !diagnosticText.Contains("private-user", StringComparison.Ordinal) &&
           diagnosticText.Contains(nameof(Pcl2DiagnosticCode.CandidatePathInvalid), StringComparison.Ordinal),
        "AbsolutePathsAreRedactedFromUi: diagnostics must preserve a shareable code without exposing raw message or path text.");
    Assert(!previewLocations.Contains(sourcePath, StringComparison.Ordinal) &&
           !previewLocations.Contains(targetPath, StringComparison.Ordinal) &&
           previewLocations.Contains("完整路径已隐藏", StringComparison.Ordinal),
        "AbsolutePathsAreRedactedFromUi: preview locations must explain verification without exposing absolute paths.");
    Assert(DiscoveryUiText.FormatPreviewLocations(null, null) ==
           "演示数据：纯内存目录与预览，不包含文件路径。",
        "AbsolutePathsAreRedactedFromUi: the deterministic demo copy must remain unchanged.");
}

static async Task PickerCancelPreservesPair()
{
    var picker = new FakeFolderPickerService();
    var service = new FakeDiscoveryRequestService();
    var first = new FakeDiscoverySessionHandle(1, "source-a", "target-a");
    service.Enqueue(first);
    using var viewModel = new DiscoveryViewModel(picker, () => service);

    await viewModel.DiscoverAutomaticallyAsync(CancellationToken.None);
    var retainedState = viewModel.State;
    var retainedGeneration = viewModel.Generation;
    var retainedSession = viewModel.ActiveSession;
    picker.Enqueue(null);

    await viewModel.ChooseFolderAsync(CancellationToken.None);

    Assert(picker.CallCount == 1,
        "PickerCancelPreservesPair: one folder action must invoke the picker exactly once.");
    Assert(service.ManualCallCount == 0,
        "PickerCancelPreservesPair: cancel must not invoke capability-backed discovery.");
    Assert(viewModel.Generation == retainedGeneration &&
           ReferenceEquals(viewModel.ActiveSession, retainedSession) &&
           ReferenceEquals(viewModel.State, retainedState) &&
           !first.IsDisposed,
        "PickerCancelPreservesPair: cancel must retain the current pair, generation, state, and live session.");
}

static async Task DiscoveryRequestAdvancesGenerationOnce()
{
    var picker = new FakeFolderPickerService();
    var service = new FakeDiscoveryRequestService();
    service.Enqueue(new FakeDiscoverySessionHandle(1, "source-a", "target-a"));
    service.Enqueue(new FakeDiscoverySessionHandle(2, "source-b", "target-b"));
    using var viewModel = new DiscoveryViewModel(picker, () => service);

    await viewModel.DiscoverAutomaticallyAsync(CancellationToken.None);
    Assert(viewModel.Generation == 1 && service.AutomaticCallCount == 1,
        "DiscoveryRequestAdvancesGenerationOnce: one automatic click must make one service call and advance once.");

    picker.Enqueue("C:\\fixtures\\chosen-root");
    await viewModel.ChooseFolderAsync(CancellationToken.None);
    Assert(viewModel.Generation == 2 &&
           picker.CallCount == 1 &&
           service.ManualCallCount == 1,
        "DiscoveryRequestAdvancesGenerationOnce: one picker click must pick once, discover once, and advance once.");
}

static async Task RediscoveryDisposesPreviousSession()
{
    var picker = new FakeFolderPickerService();
    var service = new FakeDiscoveryRequestService();
    var first = new FakeDiscoverySessionHandle(1, "source-a", "target-a");
    var second = new FakeDiscoverySessionHandle(2, "source-b", "target-b");
    service.Enqueue(first);
    service.Enqueue(second);
    using var viewModel = new DiscoveryViewModel(picker, () => service);

    await viewModel.DiscoverAutomaticallyAsync(CancellationToken.None);
    await viewModel.DiscoverAutomaticallyAsync(CancellationToken.None);

    Assert(service.AutomaticCallCount == 2,
        "RediscoveryDisposesPreviousSession: two clicks must make exactly two discovery calls.");
    Assert(first.IsDisposed && !second.IsDisposed && ReferenceEquals(viewModel.ActiveSession, second),
        "RediscoveryDisposesPreviousSession: successful rediscovery must swap atomically and then dispose the prior session.");
}

static void DemoDoesNotTouchCapability()
{
    var picker = new FakeFolderPickerService();
    var factoryCallCount = 0;
    using var viewModel = new DiscoveryViewModel(
        picker,
        () =>
        {
            factoryCallCount++;
            return new FakeDiscoveryRequestService();
        });

    viewModel.EnterDemo();

    Assert(ReferenceEquals(viewModel.State, MigrationViewState.Demo) &&
           factoryCallCount == 0 &&
           picker.CallCount == 0,
        "DemoDoesNotTouchCapability: explicit demo mode must remain deterministic and memory-only.");
}

static void ContentAdapterSelectionContracts()
{
    var vanillaItem = CreateContentItem(
        "vanilla",
        "lang",
        "语言",
        "同步语言设置",
        PlannedContentDisposition.Update,
        isSelectable: true,
        ConflictResolution.Skip);
    var jeiItem = CreateContentItem(
        "jei",
        "local-bookmarks",
        "单人收藏",
        "目标收藏不同",
        PlannedContentDisposition.Conflict,
        isSelectable: true,
        ConflictResolution.KeepTarget);
    var appearanceItem = CreateContentItem(
        "appearance",
        "dark-mode",
        "深色模式",
        "Dark Mode Everywhere 当前模式",
        PlannedContentDisposition.Update,
        isSelectable: true,
        ConflictResolution.Skip);
    var esmItem = CreateContentItem(
        "esm",
        "minecraft-weather-rain",
        "天气声音",
        "来源音量 0.1",
        PlannedContentDisposition.Add,
        isSelectable: true,
        ConflictResolution.Skip);
    var catalogs = new[]
    {
        ContentCatalog.Create("vanilla", [vanillaItem], []),
        ContentCatalog.Create("appearance", [appearanceItem], []),
        ContentCatalog.Create(
            "jei",
            [jeiItem],
            [ContentDiagnostic.Create(
                ContentDiagnosticCode.UnsupportedEmiState,
                ContentDiagnosticSeverity.Information,
                "jei")]),
        ContentCatalog.Create("esm", [esmItem], []),
    };

    var selection = new ContentSelectionViewModel(catalogs);
    Assert(selection.Cards.Count == 4 &&
           selection.Cards[0].AdapterId == "vanilla" &&
           selection.Cards[1].AdapterId == "appearance" &&
           selection.Cards[2].AdapterId == "jei" &&
           selection.Cards[3].AdapterId == "esm",
        "Content selection must expose vanilla, appearance, JEI, and ESM in the approved stable order.");
    Assert(selection.Cards.Select(card => card.Symbol).Distinct().Count() == 4 &&
           selection.Cards[0].Symbol == Symbol.Setting &&
           selection.Cards[1].Symbol == Symbol.Highlight &&
           selection.Cards[2].Symbol == Symbol.Bookmarks &&
           selection.Cards[3].Symbol == Symbol.Mute,
        "The four adapter cards must use unique settings, appearance, bookmarks, and mute symbols.");
    Assert(selection.Cards[1].Title == "界面外观" &&
           selection.Cards[1].Description == "Dark Mode Everywhere 深色模式" &&
           selection.Cards[2].HasUnsupportedEmiState &&
           selection.Cards[2].UnsupportedEmiText == "检测到 EMI 收藏：beta.5 暂不支持",
        "UnsupportedEmiState must map to one fixed nonselectable JEI detail row.");
    Assert(!selection.HasUnresolvedConflicts,
        "A default KeepTarget conflict must be resolved without silently selecting source data.");

    var categoryItems = new[]
    {
        CreateContentItem("vanilla", "lang", "语言", "语言与界面 · lang",
            PlannedContentDisposition.Update, isSelectable: true, ConflictResolution.Skip),
        CreateContentItem("vanilla", "key_key.jump", "跳跃", "按键与控制 · key_key.jump",
            PlannedContentDisposition.Update, isSelectable: true, ConflictResolution.Skip),
        CreateContentItem("vanilla", "soundCategory_music", "音乐音量", "声音与显示 · soundCategory_music",
            PlannedContentDisposition.Update, isSelectable: true, ConflictResolution.Skip),
        CreateContentItem("vanilla", "autoJump", "自动跳跃", "其他玩家设置 · autoJump",
            PlannedContentDisposition.Update, isSelectable: true, ConflictResolution.Skip),
        CreateContentItem("vanilla", "resourcePacks", "受保护设置", "resourcePacks",
            PlannedContentDisposition.Protected, isSelectable: false, ConflictResolution.Skip,
            ContentDiagnosticCode.CapabilityRejected),
        CreateContentItem("vanilla", "targetOnly", "仅目标设置", "targetOnly",
            PlannedContentDisposition.Same, isSelectable: false, ConflictResolution.Skip),
    };
    var categorySelection = new ContentSelectionViewModel(
        [ContentCatalog.Create("vanilla", categoryItems, [])]);
    var realOptionsCatalog = categorySelection.VanillaOptionsCatalog;
    Assert(realOptionsCatalog is not null &&
           realOptionsCatalog.SelectableDifferences
               .Select(item => item.Category)
               .Distinct()
               .SequenceEqual(Enum.GetValues<OptionSettingCategory>()) &&
           realOptionsCatalog.ProtectedDifferences.Select(item => item.Key)
               .SequenceEqual(["resourcePacks"]) &&
           realOptionsCatalog.TargetOnlyItems.Select(item => item.Key)
               .SequenceEqual(["targetOnly"]),
        "The real vanilla catalog must project into the same four option categories and separate pack-protection card as the preview UI.");
    Assert(categorySelection.SupplementalCards.Select(card => card.AdapterId)
               .SequenceEqual(["appearance", "jei", "esm"]),
        "Appearance, JEI, and ESM must render as supplemental cards below the four vanilla categories.");
    var categoryChangeCount = 0;
    categorySelection.SelectionChanged += (_, _) => categoryChangeCount++;
    categorySelection.ApplyVanillaSelection(
        new HashSet<string>(["lang", "soundCategory_music"], StringComparer.Ordinal));
    Assert(categoryChangeCount == 1 &&
           categorySelection.CaptureSelection().SelectedItems
               .Select(item => item.TechnicalKey)
               .Order(StringComparer.Ordinal)
               .SequenceEqual(["lang", "soundCategory_music"]),
        "A category selection change must update the real migration selection once without selecting hidden or protected items.");

    var incompatibleCompatibility = new ContentCompatibilityDisplayEvidence(
        "1.21.1",
        "1.21.1",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["jei"] = "18.99.0.1",
            ["extremesoundmuffler"] = "3.56",
        },
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["jei"] = "19.44.0.401",
            ["extremesoundmuffler"] = "3.56",
        });
    var incompatibleSelection = new ContentSelectionViewModel(
        [
            ContentCatalog.Create("vanilla", [], []),
            ContentCatalog.Create("appearance", [], []),
            ContentCatalog.Create("jei", [],
                [ContentDiagnostic.Create(
                    ContentDiagnosticCode.UnsupportedModVersion,
                    ContentDiagnosticSeverity.Error,
                    "jei")]),
            ContentCatalog.Create("esm", [], []),
        ],
        incompatibleCompatibility);
    Assert(incompatibleSelection.Cards[2].DisabledReason ==
           "版本系列不兼容：来源 18.99.0.1，目标 19.44.0.401；当前支持 JEI 19.x，并逐文件验证格式",
        "An incompatible JEI card must explain the detected source, target, and schema-led supported line instead of showing a generic error.");
    var missingRuntimeScopeSelection = new ContentSelectionViewModel(
        [
            ContentCatalog.Create("vanilla", [], []),
            ContentCatalog.Create("appearance", [], []),
            ContentCatalog.Create(
                "jei",
                [],
                [ContentDiagnostic.Create(
                    ContentDiagnosticCode.MissingTargetData,
                    ContentDiagnosticSeverity.Error,
                    "jei")]),
            ContentCatalog.Create("esm", [], []),
        ]);
    Assert(missingRuntimeScopeSelection.Cards[2].DisabledReason ==
           "目标中没有可唯一对应的收藏作用域，请检查实例后重新探测",
        "A missing JEI runtime scope must explain the ambiguous mapping without requiring target initialization.");

    var safeAllSelection = new ContentSelectionViewModel(catalogs);
    var safeAllChangeCount = 0;
    safeAllSelection.SelectionChanged += (_, _) => safeAllChangeCount++;
    Assert(safeAllSelection.HasUnselectedSafeItems,
        "SelectAllSafeItems: a fresh explicit-selection model must report its unselected safe items.");
    safeAllSelection.SelectAllSafeItems();
    var safeAllCaptured = safeAllSelection.CaptureSelection();
    Assert(safeAllChangeCount == 1 &&
           !safeAllSelection.HasUnselectedSafeItems &&
           safeAllCaptured.SelectedItems.SetEquals(
               new[] { vanillaItem.Id, appearanceItem.Id, esmItem.Id }) &&
           safeAllCaptured.ConflictResolutions.TryGetValue(jeiItem.Id, out var safeResolution) &&
           safeResolution == ConflictResolution.KeepTarget,
        "SelectAllSafeItems: one bulk action must select every conflict-free item exactly once while leaving explicit conflicts at their existing safe resolution.");

    var changeCount = 0;
    selection.SelectionChanged += (_, _) => changeCount++;
    selection.Cards[0].IsChecked = true;
    selection.Cards[1].IsChecked = true;
    selection.Cards[3].IsChecked = true;
    var conflict = selection.Cards[2].Items.Single().Conflict!;
    conflict.Resolution = ConflictResolution.UseSource;
    var captured = selection.CaptureSelection();
    Assert(changeCount == 4 &&
           captured.SelectedItems.SetEquals(new[] { vanillaItem.Id, appearanceItem.Id, jeiItem.Id, esmItem.Id }) &&
           captured.ConflictResolutions.TryGetValue(jeiItem.Id, out var resolution) &&
           resolution == ConflictResolution.UseSource,
        "Adapter and conflict changes must publish once each and capture an explicit immutable content selection.");

    conflict.Resolution = ConflictResolution.Unresolved;
    Assert(selection.HasUnresolvedConflicts,
        "An explicit unresolved conflict must block the next planning generation.");

    foreach (var code in Enum.GetValues<ContentDiagnosticCode>())
    {
        var localized = ContentDiagnosticText.Get(code);
        Assert(!string.IsNullOrWhiteSpace(localized) && localized.Length <= 80,
            $"{code} must have one fixed, readable, capped localized UI/UIA string.");
    }

    Assert(ContentUiText.Sanitize("<C:\\private\\world>\u0001", 18) == "技术信息已隐藏",
        "Rooted path-like technical text must be redacted rather than bound to the UI.");
    Assert(ContentUiText.Sanitize(new string('x', 40), 12) == "xxxxxxxxxxx…",
        "Technical text must be capped deterministically before binding.");
}

static ContentCatalogItem CreateContentItem(
    string adapterId,
    string technicalKey,
    string displayName,
    string description,
    PlannedContentDisposition disposition,
    bool isSelectable,
    ConflictResolution defaultResolution,
    ContentDiagnosticCode? disabledReason = null)
{
    Assert(ContentItemId.TryCreate(adapterId, technicalKey, out var id),
        "The fixture item ID must be valid.");
    return ContentCatalogItem.Create(
        id,
        displayName,
        description,
        disposition,
        isSelectable,
        isSelectedByDefault: false,
        defaultResolution,
        disabledReason);
}

static XElement RequireNamedElement(XDocument document, XNamespace xamlNamespace, string name)
{
    var matches = document
        .Descendants()
        .Where(element => (string?)element.Attribute(xamlNamespace + "Name") == name)
        .ToArray();
    Assert(matches.Length == 1, $"The XAML contract must contain exactly one {name}.");
    return matches[0];
}

static XElement RequireRawSymbolIcon(XElement card, string symbol)
{
    var matches = card
        .Descendants()
        .Where(element =>
            element.Name.LocalName == "SymbolIcon" &&
            (string?)element.Attribute("Symbol") == symbol)
        .ToArray();
    Assert(matches.Length == 1 &&
           (string?)matches[0].Attribute("AutomationProperties.AccessibilityView") == "Raw",
        $"{(string?)card.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Name")} must contain exactly one raw {symbol} SymbolIcon.");
    return matches[0];
}

static bool RouteMethodCallsFinalGuard(string source, string signature)
    => ExtractCSharpMethodBody(source, signature)
        .Contains("CloseDrawer();", StringComparison.Ordinal);

static string RemoveFirstInvocationFromMethod(
    string source,
    string signature,
    string invocation)
{
    var (bodyStart, bodyEnd) = FindCSharpMethodBodySpan(source, signature);
    var invocationStart = source.IndexOf(
        invocation,
        bodyStart,
        bodyEnd - bodyStart,
        StringComparison.Ordinal);
    if (invocationStart < 0)
    {
        throw new InvalidOperationException($"The method '{signature}' did not contain '{invocation}'.");
    }

    return source.Remove(invocationStart, invocation.Length);
}

static string ExtractCSharpMethodBody(string source, string signature)
{
    var (bodyStart, bodyEnd) = FindCSharpMethodBodySpan(source, signature);
    return source[bodyStart..bodyEnd];
}

static (int BodyStart, int BodyEnd) FindCSharpMethodBodySpan(string source, string signature)
{
    var signatureStart = source.IndexOf(signature, StringComparison.Ordinal);
    if (signatureStart < 0)
    {
        throw new InvalidOperationException($"The C# method signature '{signature}' was not found.");
    }

    var openingBrace = source.IndexOf('{', signatureStart + signature.Length);
    if (openingBrace < 0)
    {
        throw new InvalidOperationException($"The C# method '{signature}' had no opening brace.");
    }

    var depth = 0;
    for (var index = openingBrace; index < source.Length; index++)
    {
        if (source[index] == '{')
        {
            depth++;
        }
        else if (source[index] == '}' && --depth == 0)
        {
            return (openingBrace + 1, index);
        }
    }

    throw new InvalidOperationException($"The C# method '{signature}' had unbalanced braces.");
}

static OptionsSelectionCatalog CreateCatalog() => new(
    [
        new OptionSettingDescriptor(
            "lang",
            "Language",
            "lang",
            OptionSettingCategory.LanguageAndInterface,
            "zh_cn",
            "en_us"),
        new OptionSettingDescriptor(
            "mouseSensitivity",
            "Mouse sensitivity",
            "mouseSensitivity",
            OptionSettingCategory.Controls,
            "0.6",
            "0.5"),
        new OptionSettingDescriptor(
            "key_key.forward",
            "Move forward",
            "key_key.forward",
            OptionSettingCategory.Controls,
            "key.keyboard.w",
            "key.keyboard.up"),
    ],
    [],
    [],
    []);

static (int Width, int Height, int BitDepth, int ColorType) ReadPngFrameInfo(string path)
{
    var data = File.ReadAllBytes(path);
    ReadOnlySpan<byte> pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    if (data.Length < 33 || !data.AsSpan(0, pngSignature.Length).SequenceEqual(pngSignature))
    {
        throw new InvalidOperationException($"SceneAssetNativeResolutionContract: '{path}' is not a PNG image.");
    }

    var chunkLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
    if (chunkLength != 13 || !data.AsSpan(12, 4).SequenceEqual("IHDR"u8))
    {
        throw new InvalidOperationException(
            $"SceneAssetNativeResolutionContract: '{path}' does not begin with a valid IHDR chunk.");
    }

    return (
        Width: checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4))),
        Height: checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20, 4))),
        BitDepth: data[24],
        ColorType: data[25]);
}

static (double Position, double Velocity, double Maximum) SimulateGlowStep(
    double angularFrequency,
    double dampingRatio,
    int updatesPerSecond,
    double durationSeconds)
{
    const double target = 100;
    var position = 0d;
    var velocity = 0d;
    var maximum = position;
    var updateCount = checked((int)Math.Round(updatesPerSecond * durationSeconds));
    var elapsedSeconds = durationSeconds / updateCount;

    for (var update = 0; update < updateCount; update++)
    {
        PointerGlowSpring.Advance(
            ref position,
            ref velocity,
            target,
            elapsedSeconds,
            angularFrequency,
            dampingRatio);
        maximum = Math.Max(maximum, position);
    }

    return (position, velocity, maximum);
}

static (double CoreLag, double TrailLag, double CoreTrailSeparation) SimulateGlowRamp(
    double coreAngularFrequency,
    double coreDampingRatio,
    double trailAngularFrequency,
    double trailDampingRatio,
    double speed,
    int updatesPerSecond,
    double durationSeconds)
{
    var corePosition = 0d;
    var coreVelocity = 0d;
    var trailPosition = 0d;
    var trailVelocity = 0d;
    var target = 0d;
    var updateCount = checked((int)Math.Round(updatesPerSecond * durationSeconds));
    var elapsedSeconds = durationSeconds / updateCount;

    for (var update = 0; update < updateCount; update++)
    {
        target += speed * elapsedSeconds;
        PointerGlowSpring.Advance(
            ref corePosition,
            ref coreVelocity,
            target,
            elapsedSeconds,
            coreAngularFrequency,
            coreDampingRatio);
        PointerGlowSpring.Advance(
            ref trailPosition,
            ref trailVelocity,
            target,
            elapsedSeconds,
            trailAngularFrequency,
            trailDampingRatio);
    }

    return (
        CoreLag: target - corePosition,
        TrailLag: target - trailPosition,
        CoreTrailSeparation: corePosition - trailPosition);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class FakeCompletionSoundPlayer : ICompletionSoundPlayer
{
    public int Count { get; private set; }

    public void PlayShow()
    {
        Count++;
    }
}

internal sealed class PendingRescanBehaviorFixture
{
    private readonly ScriptedPendingScanner scanner;
    private readonly PendingRescanPublisher publisher;
    private bool recoveryCheckPassed;

    private PendingRescanBehaviorFixture(ScriptedPendingScanner scanner)
    {
        this.scanner = scanner;
        State = MigrationWorkflowState.Initial;
        publisher = new PendingRescanPublisher(
            scanner.FindPending,
            () => State,
            next => State = next,
            value => recoveryCheckPassed = value,
            exception => exception is IOException or InvalidOperationException,
            CancellationToken.None);
    }

    internal MigrationWorkflowState State { get; private set; }

    internal bool CanDiscover => MigrationWorkflowPolicy.CanDiscover(
        recoveryCheckPassed,
        State.Phase);

    internal static async Task ProveTwoPendingAndUnsuccessfulExecutionAsync()
    {
        var firstId = new TransactionId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var secondId = new TransactionId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var first = new PendingRecovery(firstId, "fixture-target-a", TargetPathAvailable: true);
        var second = new PendingRecovery(secondId, "fixture-target-b", TargetPathAvailable: true);
        var scanner = new ScriptedPendingScanner(
            [first, second],
            [second],
            [second],
            []);
        var fixture = new PendingRescanBehaviorFixture(scanner);
        var awaiting = MigrationWorkflowState.Initial with
        {
            Phase = MigrationWorkflowPhase.AwaitingDiscovery,
            StatusText = "fixture awaiting discovery",
        };

        _ = await fixture.publisher.PublishRequestBoundAsync(
            awaiting,
            "fixture pending",
            CancellationToken.None);
        Assert(fixture.State.Phase == MigrationWorkflowPhase.RecoveryRequired &&
               fixture.State.PendingRecovery?.TransactionId == firstId &&
               !fixture.State.CanExecute &&
               !fixture.CanDiscover,
            "BehavioralPendingGate: initialization must expose the first authenticated pending transaction with discovery and execution disabled.");

        _ = await fixture.publisher.PublishAfterOutcomeAsync(
            awaiting,
            "fixture second pending",
            CancellationToken.None,
            firstId,
            MigrationRecoveryStatus.Recovered);
        Assert(fixture.State.Phase == MigrationWorkflowPhase.RecoveryRequired &&
               fixture.State.PendingRecovery?.TransactionId == secondId &&
               !fixture.State.CanExecute &&
               !fixture.CanDiscover,
            "BehavioralPendingGate: completing the first recovery must publish the second transaction immediately without exposing discovery.");

        var unsuccessful = awaiting with
        {
            Phase = MigrationWorkflowPhase.Blocked,
            LastExecutionStatus = MigrationExecutionStatus.RecoveryRequired,
            StatusText = "fixture unsuccessful execution",
        };
        _ = await fixture.publisher.PublishAfterOutcomeAsync(
            unsuccessful,
            "fixture execution pending",
            CancellationToken.None,
            secondId,
            MigrationRecoveryStatus.RecoveryRequired);
        Assert(fixture.State.Phase == MigrationWorkflowPhase.RecoveryRequired &&
               fixture.State.PendingRecovery?.TransactionId == secondId &&
               fixture.State.LastExecutionStatus == MigrationExecutionStatus.RecoveryRequired &&
               !fixture.State.CanExecute &&
               !fixture.CanDiscover,
            "BehavioralPendingGate: an unsuccessful execution must rescan and retain the authenticated pending transaction with execution disabled.");

        _ = await fixture.publisher.PublishAfterOutcomeAsync(
            awaiting,
            "fixture pending",
            CancellationToken.None,
            secondId,
            MigrationRecoveryStatus.Recovered);
        Assert(fixture.State.Phase == MigrationWorkflowPhase.AwaitingDiscovery &&
               fixture.State.PendingRecovery is null &&
               !fixture.State.CanExecute &&
               fixture.CanDiscover &&
               scanner.RemainingResultCount == 0,
            "BehavioralPendingGate: only the final authenticated zero-pending scan may enable discovery.");
    }

    internal static async Task ProveCancelledOutcomePublishesBeforeCancellationAsync()
    {
        var transactionId = new TransactionId(Guid.Parse("33333333-3333-3333-3333-333333333333"));
        var uncertain = new PendingRecovery(
            transactionId,
            "fixture-cancelled-target",
            TargetPathAvailable: true,
            MigrationRecoveryStatus.RecoveryRequired);
        var scanner = new ScriptedPendingScanner([uncertain]);
        var fixture = new PendingRescanBehaviorFixture(scanner);
        using var requestCancellation = new CancellationTokenSource();
        requestCancellation.Cancel();
        var propagated = false;
        try
        {
            _ = await fixture.publisher.PublishAfterOutcomeAsync(
                MigrationWorkflowState.Initial with
                {
                    Phase = MigrationWorkflowPhase.Blocked,
                    LastExecutionStatus = MigrationExecutionStatus.CancelledBeforeMutation,
                    StatusText = "fixture returned cancellation",
                },
                "fixture cancellation pending",
                requestCancellation.Token,
                transactionId,
                MigrationRecoveryStatus.RecoveryRequired);
        }
        catch (OperationCanceledException)
        {
            propagated = true;
        }

        Assert(propagated &&
               scanner.CallCount == 1 &&
               !scanner.ObservedCancelledScanToken &&
               fixture.State.Phase == MigrationWorkflowPhase.RecoveryRequired &&
               fixture.State.PendingRecovery?.TransactionId == transactionId &&
               fixture.State.LastExecutionStatus == MigrationExecutionStatus.CancelledBeforeMutation &&
               !fixture.State.CanExecute &&
               !fixture.CanDiscover,
            "CancelledMandatoryRescan: a returned pre-namespace cancellation must publish its uncertain pending store using workflow lifetime before request cancellation propagates; it must not remain at CheckingRecovery.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed class ScriptedPendingScanner
{
    private readonly Queue<IReadOnlyList<PendingRecovery>> results;

    internal ScriptedPendingScanner(params IReadOnlyList<PendingRecovery>[] results)
    {
        this.results = new Queue<IReadOnlyList<PendingRecovery>>(results);
    }

    internal int CallCount { get; private set; }

    internal bool ObservedCancelledScanToken { get; private set; }

    internal int RemainingResultCount => results.Count;

    internal IReadOnlyList<PendingRecovery> FindPending(CancellationToken cancellationToken)
    {
        CallCount++;
        ObservedCancelledScanToken |= cancellationToken.IsCancellationRequested;
        cancellationToken.ThrowIfCancellationRequested();
        return results.Count == 0
            ? throw new InvalidOperationException("The pending scan fixture had no scripted result.")
            : results.Dequeue();
    }
}

internal sealed class FakeFolderPickerService : IFolderPickerService
{
    private readonly Queue<string?> results = new();

    public int CallCount { get; private set; }

    public void Enqueue(string? result) => results.Enqueue(result);

    public Task<string?> PickFolderAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        return Task.FromResult(results.Count == 0 ? null : results.Dequeue());
    }
}

internal sealed class FakeDiscoveryRequestService : IDiscoveryRequestService
{
    private readonly Queue<IDiscoverySessionHandle> sessions = new();

    public int AutomaticCallCount { get; private set; }
    public int ManualCallCount { get; private set; }
    public Pcl2OptionsMigrationPreviewer? OptionsPreviewer => null;

    public void Enqueue(IDiscoverySessionHandle session) => sessions.Enqueue(session);

    public DiscoveryRequestResult DiscoverAutomatically(
        long generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AutomaticCallCount++;
        return Next(generation);
    }

    public DiscoveryRequestResult DiscoverManual(
        long generation,
        string selectedPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        ManualCallCount++;
        return Next(generation);
    }

    public void Dispose()
    {
    }

    private DiscoveryRequestResult Next(long generation)
    {
        var session = sessions.Dequeue();
        if (session.Generation != generation)
        {
            throw new InvalidOperationException(
                "The fake discovery result must belong to the requested generation.");
        }

        return new DiscoveryRequestResult(session, [], "fixture discovery complete");
    }
}

internal sealed class FakeDiscoverySessionHandle : IDiscoverySessionHandle
{
    private readonly string sourceId;
    private readonly string targetId;

    public FakeDiscoverySessionHandle(long generation, string sourceId, string targetId)
    {
        Generation = generation;
        this.sourceId = sourceId;
        this.targetId = targetId;
        Instances =
        [
            CreateInstance(sourceId, "来源实例"),
            CreateInstance(targetId, "目标实例", isSelected: true),
        ];
    }

    public long Generation { get; }
    public bool IsActive => !IsDisposed;
    public bool IsDisposed { get; private set; }
    public IReadOnlyList<Pcl2Instance> Instances { get; }

    public bool CanPair(string candidateSourceId, string candidateTargetId) =>
        !IsDisposed &&
        string.Equals(candidateSourceId, sourceId, StringComparison.Ordinal) &&
        string.Equals(candidateTargetId, targetId, StringComparison.Ordinal);

    public void Dispose() => IsDisposed = true;

    private static Pcl2Instance CreateInstance(string id, string displayName, bool isSelected = false) =>
        new(
            Id: id,
            DisplayName: displayName,
            MinecraftRoot: "C:\\fixtures\\minecraft",
            InstanceRoot: $"C:\\fixtures\\minecraft\\versions\\{id}",
            GameRoot: $"C:\\fixtures\\games\\{id}",
            InstanceJsonPath: null,
            SetupPath: $"C:\\fixtures\\minecraft\\versions\\{id}\\PCL\\Setup.ini",
            Isolation: Pcl2IsolationMode.Isolated,
            MinecraftVersion: "1.21.1",
            ModLoaders: [],
            ModpackIdentity: new Pcl2ModpackIdentity(
                "Fixture Pack",
                "1",
                Pcl2IdentityConfidence.High,
                Pcl2IdentitySource.Manifest,
                "fixture"),
            HasUsableVersionMetadata: true,
            IsSelected: isSelected,
            Diagnostics: []);
}
