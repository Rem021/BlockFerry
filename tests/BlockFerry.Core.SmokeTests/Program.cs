using BlockFerry.Core.Options;

var planner = new OptionsMergePlanner();

var source = string.Join('\n',
    "version:3700",
    "lang:zh_cn",
    "resourcePacks:[\"vanilla\",\"file/old-pack.zip\"]",
    "incompatibleResourcePacks:[\"file/old-pack.zip\"]",
    "key_key.jump:key.keyboard.space",
    "soundCategory_music:0.25",
    "sourceOnly:true",
    string.Empty);

var target = string.Join("\r\n",
    "version:3955",
    "lang:en_us",
    "lang:de_de",
    "resourcePacks:[\"vanilla\",\"file/new-atm10-pack.zip\"]",
    "incompatibleResourcePacks:[\"file/new-atm10-pack.zip\"]",
    "key_key.jump:key.keyboard.j",
    "soundCategory_music:0.80",
    "targetOnly:kept",
    "targetOnly:last-value-wins",
    string.Empty,
    "unparsed metadata line",
    string.Empty);

var first = planner.Plan(source, target);
Assert(first.Changed, "The first merge must report a change.");
Assert(Value(first.Content, "lang") == "zh_cn", "Language must migrate from the source instance.");
Assert(Count(first.Content, "lang") == 1, "Language must occur exactly once.");
Assert(Value(first.Content, "resourcePacks") == "[\"vanilla\",\"file/new-atm10-pack.zip\"]", "Target resource packs must be preserved.");
Assert(Value(first.Content, "incompatibleResourcePacks") == "[\"file/new-atm10-pack.zip\"]", "Target incompatible resource packs must be preserved.");
Assert(Value(first.Content, "version") == "3955", "Target options schema version must be preserved.");
Assert(Value(first.Content, "key_key.jump") == "key.keyboard.space", "Player key bindings must migrate.");
Assert(Value(first.Content, "soundCategory_music") == "0.25", "Player audio settings must migrate.");
Assert(Value(first.Content, "sourceOnly") == "true", "Source-only player options must be added.");
Assert(Value(first.Content, "targetOnly") == "last-value-wins", "The target's last duplicate value must retain Minecraft's effective semantics.");
Assert(Count(first.Content, "targetOnly") == 1, "Duplicate target-only keys must be canonicalized.");
Assert(first.Content.Contains("\r\n\r\nunparsed metadata line\r\n", StringComparison.Ordinal), "Unknown and blank target lines must be preserved.");
Assert(first.Content.Contains("\r\n", StringComparison.Ordinal), "The target newline style must be retained.");

var languageItem = first.Items.Single(item => item.Key == "lang");
Assert(languageItem.Decision == OptionsMergeDecision.UseSource, "Language must be classified as player-owned.");
var resourcesItem = first.Items.Single(item => item.Key == "resourcePacks");
Assert(resourcesItem.Decision == OptionsMergeDecision.PreserveTarget, "Resource packs must be classified as target-owned.");

var second = planner.Plan(source, first.Content);
Assert(!second.Changed, "The second merge must be idempotent.");
Assert(second.Content == first.Content, "The second merge must keep the exact content hash stable.");

var sourceWithoutLanguage = "key_key.jump:key.keyboard.space\n";
var targetWithLanguage = "lang:en_us\nkey_key.jump:key.keyboard.j\n";
var missingSourceLanguage = planner.Plan(sourceWithoutLanguage, targetWithLanguage);
Assert(Value(missingSourceLanguage.Content, "lang") == "en_us", "A missing source language must not erase the target language.");

var modernUnstartedSource = string.Join('\n',
    "version:3955",
    "lang:zh_cn",
    "resourcePacks:[\"vanilla\",\"file/old-pack.zip\"]",
    "incompatibleResourcePacks:[\"file/old-pack.zip\"]",
    "key_key.jump:key.keyboard.space",
    "soundCategory_music:0.25",
    string.Empty);
var emptyTarget = planner.Plan(modernUnstartedSource, string.Empty);
Assert(Value(emptyTarget.Content, "lang") == "zh_cn", "An unstarted target must receive the player language without a first launch.");
Assert(Value(emptyTarget.Content, "resourcePacks") is null, "An unstarted target must not inherit the old pack's resource list.");
Assert(Value(emptyTarget.Content, "version") == "3955", "A Minecraft 1.21.1 target must receive data version 3955 so the game does not run legacy option fixers on modern key names.");
Assert(Count(emptyTarget.Content, "version") == 1, "An unstarted target must receive exactly one options data version.");

var selectedIntoUnstartedTarget = planner.PlanSelected(
    modernUnstartedSource,
    "lang:en_us\nresourcePacks:[new]\n",
    new HashSet<string>(["lang"], StringComparer.Ordinal));
Assert(Value(selectedIntoUnstartedTarget.Content, "version") == "3955", "A selected migration into an unstarted Minecraft 1.21.1 target must automatically carry data version 3955.");
Assert(Value(selectedIntoUnstartedTarget.Content, "lang") == "zh_cn", "The selected player setting must still migrate into an unstarted target.");
Assert(Value(selectedIntoUnstartedTarget.Content, "resourcePacks") == "[new]", "The unstarted target's pack-owned resource selection must remain protected.");
Assert(selectedIntoUnstartedTarget.PlannedChanges.Select(item => item.Key).ToHashSet(StringComparer.Ordinal).SetEquals(["lang", "version"]), "The automatic data-version prerequisite must be explicit in the selected plan.");

var schemaOnlyIntoUnstartedTarget = planner.PlanSelected(
    modernUnstartedSource,
    "lang:en_us\nresourcePacks:[new]\n",
    new HashSet<string>(["version"], StringComparer.Ordinal));
Assert(!schemaOnlyIntoUnstartedTarget.Changed, "The schema prerequisite must not activate without a selected player-setting difference.");
Assert(Value(schemaOnlyIntoUnstartedTarget.Content, "version") is null, "A caller must not be able to select the automatic schema prerequisite directly.");
Assert(schemaOnlyIntoUnstartedTarget.PlannedChanges.Count == 0, "A schema-only request must produce no planned changes.");

var noTrailingNewline = planner.Plan("lang:zh_cn", "lang:en_us");
Assert(!noTrailingNewline.Content.EndsWith('\n'), "A target without a trailing newline must retain that format.");
Assert(!planner.Plan("lang:zh_cn", noTrailingNewline.Content).Changed, "No-trailing-newline output must also be idempotent.");

var catalog = new OptionsSelectionCatalogBuilder().Build(
    "lang:zh_cn\nkey_key.jump:key.keyboard.space\nsoundCategory_music:0.25\nfutureOption:value\nresourcePacks:[old]\nincompatibleResourcePacks:[old-bad]\nversion:3700\n",
    "lang:en_us\nkey_key.jump:key.keyboard.j\nsoundCategory_music:1.0\nresourcePacks:[new]\nincompatibleResourcePacks:[new-bad]\nversion:3800\ntargetOnly:keep\n");

Assert(catalog.SelectableDifferences.Single(item => item.Key == "lang").Category == OptionSettingCategory.LanguageAndInterface, "lang must be language/interface.");
Assert(catalog.SelectableDifferences.Single(item => item.Key == "key_key.jump").Category == OptionSettingCategory.Controls, "key bindings must be controls.");
Assert(catalog.SelectableDifferences.Single(item => item.Key == "soundCategory_music").Category == OptionSettingCategory.SoundAndDisplay, "sound categories must be sound/display.");
Assert(catalog.SelectableDifferences.Single(item => item.Key == "futureOption").Category == OptionSettingCategory.OtherPlayerSettings, "unknown player keys must use the fallback category.");
Assert(catalog.SelectableDifferences.Single(item => item.Key == "lang").DisplayName == "\u8BED\u8A00", "lang must have a friendly Chinese label.");
Assert(catalog.SelectableDifferences.Single(item => item.Key == "key_key.jump").DisplayName == "\u8DF3\u8DC3", "jump must have a friendly Chinese label.");
Assert(catalog.SelectableDifferences.Single(item => item.Key == "soundCategory_music").DisplayName == "\u97F3\u4E50\u97F3\u91CF", "music volume must have a friendly Chinese label.");
Assert(catalog.ProtectedDifferences.Select(item => item.Key).ToHashSet(StringComparer.Ordinal).SetEquals(["resourcePacks", "incompatibleResourcePacks", "version"]), "all three fixed keys must be protected.");
Assert(catalog.TargetOnlyItems.Single().Key == "targetOnly", "target-only keys must be separated.");

var classifier = new OptionSettingClassifier();
Assert(classifier.GetDisplayName("chatFutureOption") == "\u8BED\u8A00\u4E0E\u754C\u9762", "unknown language/interface keys must use the Chinese category label.");
Assert(classifier.GetDisplayName("key_futureOption") == "\u6309\u952E\u4E0E\u63A7\u5236", "unknown control keys must use the Chinese category label.");
Assert(classifier.GetDisplayName("soundCategory_futureOption") == "\u58F0\u97F3\u4E0E\u663E\u793A", "unknown sound/display keys must use the Chinese category label.");
Assert(classifier.GetDisplayName("futureOption") == "\u5176\u4ED6\u73A9\u5BB6\u8BBE\u7F6E", "unknown player keys must use the Chinese fallback category label.");

var longDisplayKey = classifier.GetDisplayKey(new string('x', 121));
Assert(longDisplayKey.Length == 121 && longDisplayKey[^1] == '\u2026' && longDisplayKey.Count(character => character == '\u2026') == 1,
    "technical keys over 120 visible characters must end in one ellipsis.");
Assert(!longDisplayKey.Any(character => character is >= '\uE000' and <= '\uF8FF'),
    "technical-key display must not contain private-use characters.");

var customProtectionCatalog = new OptionsSelectionCatalogBuilder().Build(
    "lang:zh_cn\nresourcePacks:[old]\nincompatibleResourcePacks:[old-bad]\nversion:3700\n",
    "lang:en_us\nresourcePacks:[new]\nincompatibleResourcePacks:[new-bad]\nversion:3800\n",
    new HashSet<string>(["lang"], StringComparer.Ordinal));
Assert(customProtectionCatalog.ProtectedDifferences.Select(item => item.Key).ToHashSet(StringComparer.Ordinal).SetEquals(["lang", "resourcePacks", "incompatibleResourcePacks", "version"]), "caller protection must add to, never replace, the fixed set.");

var selection = new OptionSelectionState(catalog);
Assert(selection.SelectedKeys.Count == catalog.SelectableDifferences.Count, "catalog differences must start selected.");
selection.SetKeySelected("key_key.jump", false);
Assert(selection.GetCategoryState(OptionSettingCategory.Controls) == OptionCategorySelectionState.Unselected, "one-item category must become unselected.");
selection.SetCategorySelected(OptionSettingCategory.Controls, true);
Assert(selection.SelectedKeys.Contains("key_key.jump"), "category selection must restore its keys.");

var selected = planner.PlanSelected(
    "lang:zh_cn\nkey_key.jump:key.keyboard.space\nsourceOnly:true\nresourcePacks:[old]\nincompatibleResourcePacks:[old-bad]\nversion:3700\n",
    "lang:en_us\nkey_key.jump:key.keyboard.g\nkey_key.jump:key.keyboard.j\ntargetOnly:first\ntargetOnly:last\nresourcePacks:[new]\nincompatibleResourcePacks:[new-bad]\nversion:3800\n:metadata\nplain unparsed\n",
    new HashSet<string>(["lang", "sourceOnly", "resourcePacks", "incompatibleResourcePacks", "version"], StringComparer.Ordinal));

Assert(Value(selected.Content, "lang") == "zh_cn", "selected language must use source.");
Assert(Count(selected.Content, "key_key.jump") == 2, "unselected target duplicates must remain byte-preserved.");
Assert(Value(selected.Content, "sourceOnly") == "true", "selected source-only key must append.");
Assert(Value(selected.Content, "resourcePacks") == "[new]", "fixed protection must defeat an injected selected key.");
Assert(Value(selected.Content, "incompatibleResourcePacks") == "[new-bad]", "incompatible packs must remain target-owned.");
Assert(Value(selected.Content, "version") == "3800", "version must remain target-owned.");
Assert(selected.Content.Contains(":metadata\nplain unparsed\n", StringComparison.Ordinal), "unparsed target lines must remain in order.");
Assert(selected.PlannedChanges.Select(item => item.Key).SequenceEqual(["lang", "sourceOnly"]), "only accepted selected keys may be planned.");
Assert(selected.SkippedDifferences.Any(item => item.Key == "key_key.jump"), "unselected differences must be explicit.");
Assert(selected.ProtectedDifferences.Select(item => item.Key).ToHashSet(StringComparer.Ordinal).SetEquals(["resourcePacks", "incompatibleResourcePacks", "version"]), "all fixed protected differences must be explicit.");
Assert(selected.TargetOnlyItems.Select(item => item.Key).SequenceEqual(["targetOnly"]), "target-only keys must remain separately reported.");

var none = planner.PlanSelected(source, target, new HashSet<string>(StringComparer.Ordinal));
Assert(none.Content == target, "an empty allowlist must preserve target bytes.");
Assert(!none.Changed, "an empty allowlist must report no change.");

var mixedNewlineTarget = "resourcePacks:[new]\r\n"
    + "key_key.jump:key.keyboard.g\n"
    + "key_key.jump:key.keyboard.j\r\n"
    + ":metadata\n"
    + "plain unparsed\r\n";
var protectedOnlyMixedNewline = planner.PlanSelected(
    "resourcePacks:[old]\nkey_key.jump:key.keyboard.space\n",
    mixedNewlineTarget,
    new HashSet<string>(["resourcePacks"], StringComparer.Ordinal));
var protectedMixedSegment = "resourcePacks:[new]\r\n";
var unselectedMixedDuplicateSegment = "key_key.jump:key.keyboard.g\nkey_key.jump:key.keyboard.j\r\n";
var unparsedMixedSegment = ":metadata\nplain unparsed\r\n";
Assert(protectedOnlyMixedNewline.Content == mixedNewlineTarget, "selecting only a protected key must preserve the complete mixed-newline target bytes.");
Assert(protectedOnlyMixedNewline.Content.StartsWith(protectedMixedSegment, StringComparison.Ordinal), "the protected target segment must retain its CRLF delimiter.");
Assert(protectedOnlyMixedNewline.Content.Contains(unselectedMixedDuplicateSegment, StringComparison.Ordinal), "unselected duplicate target rows must retain their mixed delimiters.");
Assert(protectedOnlyMixedNewline.Content.EndsWith(unparsedMixedSegment, StringComparison.Ordinal), "unparsed target rows must retain their mixed delimiters.");
Assert(!protectedOnlyMixedNewline.Changed, "a protected-only mixed-newline selection must not report a byte change.");

var duplicateSelected = planner.PlanSelected(
    "key_key.jump:key.keyboard.space\n",
    "key_key.jump:key.keyboard.g\nkey_key.jump:key.keyboard.j\n",
    new HashSet<string>(["key_key.jump"], StringComparer.Ordinal));
Assert(Count(duplicateSelected.Content, "key_key.jump") == 1, "selected duplicates must canonicalize once.");
Assert(Value(duplicateSelected.Content, "key_key.jump") == "key.keyboard.space", "selected duplicate must use effective source.");

var additionalPlanner = new OptionsMergePlanner(
    new HashSet<string>(["lang"], StringComparer.Ordinal));
var additionallyProtected = additionalPlanner.PlanSelected(
    "lang:zh_cn\n",
    "lang:en_us\n",
    new HashSet<string>(["lang"], StringComparer.Ordinal));
Assert(Value(additionallyProtected.Content, "lang") == "en_us", "additional protection may only add protected keys.");

var fixedProtectionWithCustomPlanner = additionalPlanner.PlanSelected(
    "resourcePacks:[old]\nincompatibleResourcePacks:[old-bad]\nversion:3700\n",
    "resourcePacks:[new]\nincompatibleResourcePacks:[new-bad]\nversion:3800\n",
    new HashSet<string>(["resourcePacks", "incompatibleResourcePacks", "version"], StringComparer.Ordinal));
Assert(Value(fixedProtectionWithCustomPlanner.Content, "resourcePacks") == "[new]", "fixed protection must survive a custom planner protection set.");
Assert(Value(fixedProtectionWithCustomPlanner.Content, "incompatibleResourcePacks") == "[new-bad]", "all fixed protections must survive a custom planner set.");
Assert(Value(fixedProtectionWithCustomPlanner.Content, "version") == "3800", "version protection must survive a custom planner set.");

var fullCatalog = new OptionsSelectionCatalogBuilder().Build(source, target);
var fullSelection = fullCatalog.SelectableDifferences
    .Select(item => item.Key)
    .ToHashSet(StringComparer.Ordinal);
var selectedAll = planner.PlanSelected(source, target, fullSelection);
var legacyAll = planner.Plan(source, target);
foreach (var key in fullSelection)
{
    Assert(Value(selectedAll.Content, key) == Value(legacyAll.Content, key), $"full selection must remain semantically equivalent for {key}.");
}

Console.WriteLine("PASS: options semantic merge, language migration, target pack protection, missing-target preparation, duplicate-key cleanup, and second-run idempotence");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static int Count(string content, string key)
{
    return content.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
        .Count(line => line.StartsWith(key + ':', StringComparison.Ordinal));
}

static string? Value(string content, string key)
{
    var prefix = key + ':';
    var line = content.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
        .LastOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));
    return line?[prefix.Length..];
}
