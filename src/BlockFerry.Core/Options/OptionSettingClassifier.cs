using System.Text;

namespace BlockFerry.Core.Options;

public class OptionSettingClassifier
{
    private const char TruncationSuffix = '\u2026';

    private static readonly Dictionary<string, string> DisplayNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lang"] = "\u8BED\u8A00",
            ["narrator"] = "\u65C1\u767D",
            ["showSubtitles"] = "\u663E\u793A\u5B57\u5E55",
            ["forceUnicodeFont"] = "\u5F3A\u5236 Unicode \u5B57\u4F53",
            ["autoSuggestions"] = "\u81EA\u52A8\u5EFA\u8BAE",
            ["key_key.jump"] = "\u8DF3\u8DC3",
            ["sensitivity"] = "\u9F20\u6807\u7075\u654F\u5EA6",
            ["invertYMouse"] = "\u53CD\u8F6C Y \u8F74",
            ["discrete_mouse_scroll"] = "\u79BB\u6563\u9F20\u6807\u6EDA\u52A8",
            ["touchscreen"] = "\u89E6\u6478\u5C4F",
            ["toggleCrouch"] = "\u5207\u6362\u6F5C\u884C",
            ["toggleSprint"] = "\u5207\u6362\u75BE\u8DD1",
            ["rawMouseInput"] = "\u539F\u59CB\u9F20\u6807\u8F93\u5165",
            ["soundCategory_music"] = "\u97F3\u4E50\u97F3\u91CF",
            ["fov"] = "\u89C6\u91CE",
            ["gamma"] = "\u4EAE\u5EA6",
            ["renderDistance"] = "\u6E32\u67D3\u8DDD\u79BB",
            ["simulationDistance"] = "\u6A21\u62DF\u8DDD\u79BB",
            ["graphicsMode"] = "\u56FE\u5F62\u6A21\u5F0F",
            ["fullscreen"] = "\u5168\u5C4F",
            ["enableVsync"] = "\u5782\u76F4\u540C\u6B65",
            ["guiScale"] = "GUI \u7F29\u653E",
            ["entityShadows"] = "\u5B9E\u4F53\u9634\u5F71",
            ["particles"] = "\u7C92\u5B50\u6548\u679C",
            ["mipmapLevels"] = "Mipmap \u7EA7\u522B",
            ["biomeBlendRadius"] = "\u751F\u7269\u7FA4\u7CFB\u6DF7\u5408\u534A\u5F84",
        };

    private static readonly HashSet<string> LanguageAndInterfaceKeys = new HashSet<string>(
        ["lang", "narrator", "showSubtitles", "forceUnicodeFont", "autoSuggestions"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> ControlKeys = new HashSet<string>(
        ["sensitivity", "invertYMouse", "discrete_mouse_scroll", "touchscreen", "toggleCrouch", "toggleSprint", "rawMouseInput"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> SoundAndDisplayKeys = new HashSet<string>(
        ["fov", "gamma", "renderDistance", "simulationDistance", "graphicsMode", "fullscreen", "enableVsync", "guiScale", "entityShadows", "particles", "mipmapLevels", "biomeBlendRadius"],
        StringComparer.Ordinal);

    public virtual OptionSettingCategory Classify(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (LanguageAndInterfaceKeys.Contains(key) || key.StartsWith("chat", StringComparison.Ordinal))
        {
            return OptionSettingCategory.LanguageAndInterface;
        }

        if (ControlKeys.Contains(key) || key.StartsWith("key_", StringComparison.Ordinal))
        {
            return OptionSettingCategory.Controls;
        }

        if (SoundAndDisplayKeys.Contains(key) || key.StartsWith("soundCategory_", StringComparison.Ordinal))
        {
            return OptionSettingCategory.SoundAndDisplay;
        }

        return OptionSettingCategory.OtherPlayerSettings;
    }

    public string GetDisplayName(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return DisplayNames.TryGetValue(key, out var displayName)
            ? displayName
            : GetCategoryLabel(Classify(key));
    }

    public virtual string GetDisplayKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var output = new StringBuilder(Math.Min(key.Length, 120) + 1);
        foreach (var character in key)
        {
            var visible = char.IsControl(character)
                ? string.Format(global::System.Globalization.CultureInfo.InvariantCulture, "\\u{0:X4}", (int)character)
                : character.ToString();
            if (output.Length + visible.Length > 120)
            {
                output.Append(TruncationSuffix);
                return output.ToString();
            }

            output.Append(visible);
        }

        return output.ToString();
    }

    private static string GetCategoryLabel(OptionSettingCategory category) => category switch
    {
        OptionSettingCategory.LanguageAndInterface => "\u8BED\u8A00\u4E0E\u754C\u9762",
        OptionSettingCategory.Controls => "\u6309\u952E\u4E0E\u63A7\u5236",
        OptionSettingCategory.SoundAndDisplay => "\u58F0\u97F3\u4E0E\u663E\u793A",
        _ => "\u5176\u4ED6\u73A9\u5BB6\u8BBE\u7F6E",
    };
}
