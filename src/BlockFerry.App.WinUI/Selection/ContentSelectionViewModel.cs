using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using BlockFerry.Core.Content;
using BlockFerry.Core.Mods;
using BlockFerry.Core.Options;
using Microsoft.UI.Xaml.Controls;

namespace BlockFerry.App.WinUI.Selection;

internal static class ContentDiagnosticText
{
    internal static string Get(ContentDiagnosticCode code) => code switch
    {
        ContentDiagnosticCode.MissingSourceData => "来源中没有找到对应数据",
        ContentDiagnosticCode.MissingTargetData => "目标中没有找到对应数据",
        ContentDiagnosticCode.UnsupportedMinecraftVersion => "Minecraft 版本暂不兼容",
        ContentDiagnosticCode.UnsupportedModVersion => "模组版本暂不兼容",
        ContentDiagnosticCode.UnsupportedSchema => "数据格式版本暂不支持",
        ContentDiagnosticCode.UnsupportedEmiState => "检测到 EMI 收藏：beta.4 暂不支持",
        ContentDiagnosticCode.MalformedUtf8 => "文件不是有效的 UTF-8 文本",
        ContentDiagnosticCode.MalformedJson => "JSON 数据无法安全读取",
        ContentDiagnosticCode.DuplicateJsonProperty => "JSON 中存在重复字段",
        ContentDiagnosticCode.SemanticAliasCollision => "数据中存在含义重复的项目",
        ContentDiagnosticCode.LimitExceeded => "内容超过 beta.4 的安全读取上限",
        ContentDiagnosticCode.StaleContext => "实例内容已变化，请重新扫描",
        ContentDiagnosticCode.CapabilityRejected => "安全边界阻止了这项内容",
        ContentDiagnosticCode.InvalidRelativePath => "内容位置不在允许范围内",
        ContentDiagnosticCode.PathConflict => "多个内容项目指向同一目标位置",
        _ => "暂时无法安全读取这项内容",
    };
}

internal static class ContentUiText
{
    internal const string HiddenTechnicalText = "技术信息已隐藏";

    internal static string Sanitize(string? value, int maximumLength)
    {
        if (maximumLength is < 4 or > ContentContractLimits.MaximumVisibleTextUtf16Length)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (LooksLikeAbsolutePath(trimmed))
        {
            return HiddenTechnicalText;
        }

        var builder = new StringBuilder(Math.Min(trimmed.Length, maximumLength));
        foreach (var character in trimmed)
        {
            if (char.IsControl(character))
            {
                continue;
            }

            builder.Append(character switch
            {
                '<' => '‹',
                '>' => '›',
                '&' => '＆',
                _ => character,
            });
            if (builder.Length == maximumLength)
            {
                break;
            }
        }

        if (builder.Length == maximumLength && trimmed.Length > maximumLength)
        {
            builder[^1] = '…';
        }

        return builder.ToString();
    }

    private static bool LooksLikeAbsolutePath(string value)
    {
        if (value.StartsWith("\\\\", StringComparison.Ordinal) || value[0] == '/')
        {
            return true;
        }

        for (var index = 1; index < value.Length - 1; index++)
        {
            if (value[index] == ':' &&
                char.IsAsciiLetter(value[index - 1]) &&
                value[index + 1] is '\\' or '/')
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class ContentCompatibilityDisplayEvidence
{
    internal ContentCompatibilityDisplayEvidence(
        string? sourceMinecraftVersion,
        string? targetMinecraftVersion,
        IReadOnlyDictionary<string, string> sourceModVersions,
        IReadOnlyDictionary<string, string> targetModVersions)
    {
        SourceMinecraftVersion = sourceMinecraftVersion;
        TargetMinecraftVersion = targetMinecraftVersion;
        SourceModVersions = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(sourceModVersions, StringComparer.Ordinal));
        TargetModVersions = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(targetModVersions, StringComparer.Ordinal));
    }

    internal string? SourceMinecraftVersion { get; }

    internal string? TargetMinecraftVersion { get; }

    internal IReadOnlyDictionary<string, string> SourceModVersions { get; }

    internal IReadOnlyDictionary<string, string> TargetModVersions { get; }

    internal static ContentCompatibilityDisplayEvidence FromCore(
        AdapterCompatibilityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return new ContentCompatibilityDisplayEvidence(
            evidence.SourceMinecraftVersion,
            evidence.TargetMinecraftVersion,
            evidence.SourceModVersions,
            evidence.TargetModVersions);
    }
}

internal static class ContentCompatibilityText
{
    internal static string GetDisabledReason(
        string adapterId,
        ContentDiagnosticCode code,
        ContentCompatibilityDisplayEvidence? evidence)
    {
        if (code != ContentDiagnosticCode.UnsupportedModVersion || evidence is null)
        {
            return ContentDiagnosticText.Get(code);
        }

        var modId = ModDataCompatibilityPolicy.ModIdForAdapter(adapterId);
        var supportedLine = ModDataCompatibilityPolicy.SupportedLineDisplay(adapterId);
        if (modId is null || supportedLine is null)
        {
            return ContentDiagnosticText.Get(code);
        }

        if (!string.Equals(evidence.SourceMinecraftVersion, "1.21.1", StringComparison.Ordinal) ||
            !string.Equals(evidence.TargetMinecraftVersion, "1.21.1", StringComparison.Ordinal))
        {
            return $"Minecraft 版本不匹配：来源 {Display(evidence.SourceMinecraftVersion)}，" +
                   $"目标 {Display(evidence.TargetMinecraftVersion)}；当前支持 1.21.1";
        }

        evidence.SourceModVersions.TryGetValue(modId, out var sourceVersion);
        evidence.TargetModVersions.TryGetValue(modId, out var targetVersion);
        if (sourceVersion is null || targetVersion is null)
        {
            return $"未完整识别模组版本：来源 {Display(sourceVersion)}，目标 {Display(targetVersion)}；" +
                   $"当前支持 {supportedLine}，并逐文件验证格式";
        }

        if (!ModDataCompatibilityPolicy.AreModVersionsCompatible(
                modId,
                sourceVersion,
                targetVersion))
        {
            return $"版本系列不兼容：来源 {Display(sourceVersion)}，目标 {Display(targetVersion)}；" +
                   $"当前支持 {supportedLine}，并逐文件验证格式";
        }

        return "检测到重复或无法唯一确认的模组版本";
    }

    private static string Display(string? value) => string.IsNullOrEmpty(value)
        ? "未识别"
        : ContentUiText.Sanitize(value, 80);
}

internal sealed class ContentSelectionViewModel
{
    private static readonly string[] AdapterOrder = ["vanilla", "appearance", "jei", "esm"];
    private bool _bulkChanging;
    private bool _bulkChanged;

    internal ContentSelectionViewModel(
        IEnumerable<ContentCatalog> catalogs,
        ContentCompatibilityDisplayEvidence? compatibility = null)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        var byId = new Dictionary<string, ContentCatalog>(StringComparer.Ordinal);
        foreach (var catalog in catalogs.Take(ContentContractLimits.MaximumAdapters + 1))
        {
            ArgumentNullException.ThrowIfNull(catalog);
            if (!AdapterOrder.Contains(catalog.AdapterId, StringComparer.Ordinal) ||
                !byId.TryAdd(catalog.AdapterId, catalog))
            {
                throw new ArgumentException("Content catalogs must be unique and supported.", nameof(catalogs));
            }
        }

        if (byId.Count > AdapterOrder.Length)
        {
            throw new ArgumentException("Too many content catalogs.", nameof(catalogs));
        }

        Cards = Array.AsReadOnly(AdapterOrder
            .Select(adapterId => new ContentAdapterCardViewModel(
                adapterId,
                byId.GetValueOrDefault(adapterId),
                compatibility))
            .ToArray());
        SupplementalCards = Array.AsReadOnly(Cards
            .Where(card => !string.Equals(card.AdapterId, "vanilla", StringComparison.Ordinal))
            .ToArray());
        VanillaOptionsCatalog = CreateVanillaOptionsCatalog(byId.GetValueOrDefault("vanilla"));
        foreach (var card in Cards)
        {
            card.SelectionChanged += Card_SelectionChanged;
        }
    }

    internal IReadOnlyList<ContentAdapterCardViewModel> Cards { get; }

    internal IReadOnlyList<ContentAdapterCardViewModel> SupplementalCards { get; }

    internal OptionsSelectionCatalog? VanillaOptionsCatalog { get; }

    internal IReadOnlySet<string> VanillaSelectedKeys => new ImmutableSelectionKeySet(
        Cards[0].Items
            .Where(item => item.IsSelectedForTransfer)
            .Select(item => item.Id.TechnicalKey));

    internal bool HasUnresolvedConflicts => Cards
        .SelectMany(card => card.Items)
        .Any(item => item.Conflict?.Resolution == ConflictResolution.Unresolved);

    internal bool HasUnselectedSafeItems => Cards
        .SelectMany(card => card.Items)
        .Any(item => item.IsSelectable &&
                     item.Conflict is null &&
                     !item.IsSelectedForTransfer);

    internal event EventHandler? SelectionChanged;

    internal ContentSelection CaptureSelection()
    {
        var selectedItems = Cards
            .SelectMany(card => card.Items)
            .Where(item => item.IsSelectedForTransfer)
            .Select(item => item.Id)
            .ToArray();
        var resolutions = Cards
            .SelectMany(card => card.Items)
            .Where(item => item.Conflict is not null)
            .Select(item => new KeyValuePair<ContentItemId, ConflictResolution>(
                item.Id,
                item.Conflict!.Resolution))
            .ToArray();
        return ContentSelection.Create(selectedItems, resolutions);
    }

    internal void ApplyVanillaSelection(IReadOnlySet<string> selectedTechnicalKeys)
    {
        ArgumentNullException.ThrowIfNull(selectedTechnicalKeys);
        Cards[0].ApplySelectedTechnicalKeys(selectedTechnicalKeys);
    }

    internal void SelectAllSafeItems()
    {
        if (!HasUnselectedSafeItems)
        {
            return;
        }

        _bulkChanging = true;
        _bulkChanged = false;
        try
        {
            foreach (var card in Cards)
            {
                card.SelectAllSafeItems();
            }
        }
        finally
        {
            _bulkChanging = false;
        }

        if (_bulkChanged)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static OptionsSelectionCatalog? CreateVanillaOptionsCatalog(ContentCatalog? catalog)
    {
        if (catalog is null)
        {
            return null;
        }

        var classifier = new OptionSettingClassifier();
        var selectable = catalog.Items
            .Where(item => item.IsSelectable)
            .Select(item => new OptionSettingDescriptor(
                item.Id.TechnicalKey,
                ContentUiText.Sanitize(item.DisplayName, 80),
                classifier.GetDisplayKey(item.Id.TechnicalKey),
                classifier.Classify(item.Id.TechnicalKey),
                SourceValue: null,
                TargetValue: null))
            .ToArray();
        var protectedItems = catalog.Items
            .Where(item => item.Disposition == PlannedContentDisposition.Protected)
            .Select(item => new OptionsMergeItem(
                item.Id.TechnicalKey,
                SourceValue: null,
                TargetValue: null,
                FinalValue: null,
                OptionsMergeDecision.PreserveTarget,
                "Target pack setting is protected."))
            .ToArray();
        var requiredItems = catalog.Items
            .Where(item => !item.IsSelectable &&
                           item.Disposition is PlannedContentDisposition.Add or PlannedContentDisposition.Update)
            .Select(item => new OptionsMergeItem(
                item.Id.TechnicalKey,
                SourceValue: null,
                TargetValue: null,
                FinalValue: null,
                OptionsMergeDecision.UseSource,
                "Required setting is applied automatically when player settings are selected."))
            .ToArray();
        var targetOnly = catalog.Items
            .Where(item => !item.IsSelectable &&
                           item.Disposition == PlannedContentDisposition.Same)
            .Select(item => new OptionsMergeItem(
                item.Id.TechnicalKey,
                SourceValue: null,
                TargetValue: null,
                FinalValue: null,
                OptionsMergeDecision.PreserveTargetOnly,
                "Target-only setting is preserved."))
            .ToArray();
        return new OptionsSelectionCatalog(selectable, requiredItems, protectedItems, targetOnly);
    }

    private void Card_SelectionChanged(object? sender, EventArgs e)
    {
        if (_bulkChanging)
        {
            _bulkChanged = true;
            return;
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class ContentAdapterCardViewModel : INotifyPropertyChanged
{
    private readonly bool _catalogAvailable;
    private bool _bulkChanging;

    internal ContentAdapterCardViewModel(
        string adapterId,
        ContentCatalog? catalog,
        ContentCompatibilityDisplayEvidence? compatibility = null)
    {
        AdapterId = adapterId;
        (Title, Description, Symbol) = adapterId switch
        {
            "vanilla" => ("原版设置", "语言、按键、声音与显示选项", Symbol.Setting),
            "appearance" => ("界面外观", "Dark Mode Everywhere 深色模式", Symbol.Highlight),
            "jei" => ("JEI 合成收藏", "单人世界与服务器收藏", Symbol.Bookmarks),
            "esm" => ("声音静音设置", "Extreme Sound Muffler 音量规则", Symbol.Mute),
            _ => throw new ArgumentOutOfRangeException(nameof(adapterId)),
        };
        _catalogAvailable = catalog is not null;
        var items = catalog?.Items
            .Select(item => new ContentItemSelectionViewModel(item))
            .ToArray() ?? [];
        Items = new ReadOnlyCollection<ContentItemSelectionViewModel>(items);
        foreach (var item in Items)
        {
            item.SelectionChanged += Item_SelectionChanged;
        }

        HasUnsupportedEmiState = adapterId == "jei" &&
            catalog?.Diagnostics.Any(diagnostic =>
                diagnostic.Code == ContentDiagnosticCode.UnsupportedEmiState) == true;
        UnsupportedEmiText = ContentDiagnosticText.Get(ContentDiagnosticCode.UnsupportedEmiState);
        DisabledReason = ResolveDisabledReason(catalog, compatibility);
    }

    internal string AdapterId { get; }

    internal string Title { get; }

    internal string Description { get; }

    internal Symbol Symbol { get; }

    internal IReadOnlyList<ContentItemSelectionViewModel> Items { get; }

    internal bool IsEnabled => Items.Any(item => item.IsSelectable);

    internal bool HasUnsupportedEmiState { get; }

    internal string UnsupportedEmiText { get; }

    internal string DisabledReason { get; }

    internal bool? IsChecked
    {
        get
        {
            var selectable = Items.Where(item => item.IsSelectable).ToArray();
            if (selectable.Length == 0 || selectable.All(item => !item.IsSelectedForTransfer))
            {
                return false;
            }

            return selectable.All(item => item.IsSelectedForTransfer) ? true : null;
        }
        set
        {
            var select = value == true;
            if (!IsEnabled || IsChecked == select)
            {
                return;
            }

            _bulkChanging = true;
            try
            {
                foreach (var item in Items.Where(item => item.IsSelectable))
                {
                    item.SetAdapterSelected(select);
                }
            }
            finally
            {
                _bulkChanging = false;
            }

            PublishSelectionChanged();
        }
    }

    internal string SelectionSummary
    {
        get
        {
            var selectableCount = Items.Count(item => item.IsSelectable);
            var selectedCount = Items.Count(item => item.IsSelectedForTransfer);
            return selectableCount == 0
                ? DisabledReason
                : $"已选 {selectedCount} / {selectableCount} 项";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal event EventHandler? SelectionChanged;

    internal void ApplySelectedTechnicalKeys(IReadOnlySet<string> selectedTechnicalKeys)
    {
        ArgumentNullException.ThrowIfNull(selectedTechnicalKeys);
        var selectable = Items.Where(item => item.IsSelectable).ToArray();
        if (!selectable.Any(item =>
                item.IsSelectedForTransfer != selectedTechnicalKeys.Contains(item.Id.TechnicalKey)))
        {
            return;
        }

        _bulkChanging = true;
        try
        {
            foreach (var item in selectable)
            {
                item.SetAdapterSelected(selectedTechnicalKeys.Contains(item.Id.TechnicalKey));
            }
        }
        finally
        {
            _bulkChanging = false;
        }

        PublishSelectionChanged();
    }

    internal void SelectAllSafeItems()
    {
        var selectable = Items
            .Where(item => item.IsSelectable && item.Conflict is null)
            .ToArray();
        if (!selectable.Any(item => !item.IsSelectedForTransfer))
        {
            return;
        }

        _bulkChanging = true;
        try
        {
            foreach (var item in selectable)
            {
                item.IsSelected = true;
            }
        }
        finally
        {
            _bulkChanging = false;
        }

        PublishSelectionChanged();
    }

    private string ResolveDisabledReason(
        ContentCatalog? catalog,
        ContentCompatibilityDisplayEvidence? compatibility)
    {
        if (!_catalogAvailable)
        {
            return "等待读取实例内容";
        }

        var diagnostic = catalog!.Diagnostics.FirstOrDefault(item =>
            item.Code != ContentDiagnosticCode.UnsupportedEmiState);
        if (!Items.Any(item => item.IsSelectable))
        {
            if (string.Equals(AdapterId, "jei", StringComparison.Ordinal) &&
                diagnostic?.Code == ContentDiagnosticCode.MissingTargetData)
            {
                return "目标中没有可唯一对应的收藏作用域，请检查实例后重新探测";
            }

            return diagnostic is null
                ? "没有可迁移内容"
                : ContentCompatibilityText.GetDisabledReason(
                    AdapterId,
                    diagnostic.Code,
                    compatibility);
        }

        return string.Empty;
    }

    private void Item_SelectionChanged(object? sender, EventArgs e)
    {
        if (!_bulkChanging)
        {
            PublishSelectionChanged();
        }
    }

    private void PublishSelectionChanged()
    {
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(SelectionSummary));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed class ContentItemSelectionViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    internal ContentItemSelectionViewModel(ContentCatalogItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Id = item.Id;
        DisplayName = ContentUiText.Sanitize(item.DisplayName, 80);
        Description = ContentUiText.Sanitize(item.Description, 160);
        IsSelectable = item.IsSelectable;
        DisabledReason = item.DisabledReason is { } reason
            ? ContentDiagnosticText.Get(reason)
            : string.Empty;
        _isSelected = item.IsSelectable &&
                      item.Disposition != PlannedContentDisposition.Conflict &&
                      item.IsSelectedByDefault;
        if (item.Disposition == PlannedContentDisposition.Conflict)
        {
            Conflict = new ConflictResolutionViewModel(item.Id, item.DefaultResolution);
            Conflict.ResolutionChanged += Conflict_ResolutionChanged;
        }
    }

    internal ContentItemId Id { get; }

    internal string DisplayName { get; }

    internal string Description { get; }

    internal bool IsSelectable { get; }

    internal string DisabledReason { get; }

    internal ConflictResolutionViewModel? Conflict { get; }

    internal bool IsSelected
    {
        get => IsSelectedForTransfer;
        set
        {
            if (!IsSelectable || Conflict is not null || _isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PublishSelectionChanged();
        }
    }

    internal bool IsSelectedForTransfer => Conflict is not null
        ? Conflict.Resolution == ConflictResolution.UseSource
        : _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal event EventHandler? SelectionChanged;

    internal void SetAdapterSelected(bool selected)
    {
        if (!IsSelectable)
        {
            return;
        }

        if (Conflict is not null)
        {
            Conflict.Resolution = selected
                ? ConflictResolution.UseSource
                : Conflict.DefaultResolution;
        }
        else
        {
            IsSelected = selected;
        }
    }

    private void Conflict_ResolutionChanged(object? sender, EventArgs e) => PublishSelectionChanged();

    private void PublishSelectionChanged()
    {
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(IsSelectedForTransfer));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
