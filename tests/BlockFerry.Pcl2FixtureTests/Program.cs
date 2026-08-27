using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using BlockFerry.Core.Discovery;
using BlockFerry.Core.Options;
using BlockFerry.Core.Pcl2;
using BlockFerry.Core.System;

var fixture = new FixtureSandbox();
var fixtureRoot = fixture.RootPath;

try
{
    Directory.CreateDirectory(fixtureRoot);
    var fileSystem = new WindowsFileSystemCapability();
    var discoveryService = new Pcl2InstanceDiscovery(fileSystem);

    var launcherA = Path.Combine(fixtureRoot, "launcher-a");
    var minecraftRootA = Path.Combine(launcherA, ".minecraft");
    var minecraftRootB = Path.Combine(fixtureRoot, "launcher-b", ".minecraft");
    var invalidExistingRoot = Path.Combine(fixtureRoot, "not-a-minecraft-root");
    Directory.CreateDirectory(Path.Combine(minecraftRootA, "versions"));
    Directory.CreateDirectory(Path.Combine(minecraftRootB, "versions"));
    Directory.CreateDirectory(invalidExistingRoot);

    const string sourceName = "ATM10 Source r19";
    const string targetName = "ATM10 Target r20";
    const string fabricSharedName = "Fabric Shared False";
    const string quiltSharedName = "Quilt Shared Zero";
    const string legacyIsolatedName = "Legacy Isolated One";
    const string legacySharedName = "Legacy Shared Two";
    const string inferredLegacyName = "Legacy Global With Mod Evidence";
    const string missingSetupName = "Missing Setup";
    const string unknownIsolationName = "Unknown Isolation";
    const string missingJsonName = "Missing Version Json";
    const string nonObjectJsonName = "Non Object Json";
    const string nonObjectManifestName = "Non Object Manifest";
    const string precedenceName = "Inheritance Beats Id";
    const string forgePatchName = "Forge Patch Is Not Minecraft";
    const string fmlArgumentName = "FML Argument Beats Id";
    const string invalidInheritanceName = "Invalid Inheritance Path";
    const string jarPrecedenceName = "Jar Beats Inheritance";
    const string snapshotVersionName = "Snapshot Suffix";
    const string reparseModsName = "Reparse Mod Evidence";
    const string reparseSavesName = "Reparse Save Evidence";
    const string libraryPrecedenceName = "Library Beats Jar Without Inheritance";
    const string exactOptionsLimitName = "Exact Options Limit";
    const string oversizedOptionsLimitName = "Oversized Options Limit";
    const string invalidSourceOptionsSchemaName = "Invalid Source Options Schema";
    const string duplicateTargetOptionsSchemaName = "Duplicate Target Options Schema";

    CreateInstance(
        minecraftRootA,
        "1.21.1",
        "VersionArgumentIndieV2:true\r\n",
        "{ \"id\": \"1.21.1\", \"type\": \"release\", \"mainClass\": \"net.minecraft.client.main.Main\" }",
        null,
        null);

    File.WriteAllText(
        Path.Combine(minecraftRootA, "PCL.ini"),
        $"Version:{sourceName}\r\nInstanceCache:fixture-only\r\n");

    var sourceRoot = CreateInstance(
        minecraftRootA,
        sourceName,
        "VersionArgumentIndieV2:TrUe\r\n",
        """
        {
          "id": "atm10-source-r19",
          "inheritsFrom": "1.21.1",
          "libraries": [
            { "name": "net.neoforged:neoforge:21.1.176" }
          ]
        }
        """,
        """
        {
          "name": "All the Mods 10",
          "version": "7.3-r19"
        }
        """,
        """
        version:3955
        lang:zh_cn
        resourcePacks:["vanilla","file/old-pack.zip"]
        incompatibleResourcePacks:["file/old-pack.zip"]
        key_key.jump:key.keyboard.space
        soundCategory_music:0.25

        """);

    var targetRoot = CreateInstance(
        minecraftRootA,
        targetName,
        "VersionArgumentIndieV2:1\r\n",
        """
        {
          "id": "atm10-target-r20",
          "inheritsFrom": "1.21.1",
          "modpack": {
            "name": "All the Mods 10",
            "version": "7.3-r20"
          },
          "libraries": [
            { "name": "net.minecraftforge:forge:1.21.1-52.0.1" }
          ]
        }
        """,
        null,
        """
        version:3955
        lang:en_us
        resourcePacks:["vanilla","file/new-pack.zip"]
        incompatibleResourcePacks:["file/new-pack.zip"]
        key_key.jump:key.keyboard.j
        soundCategory_music:0.80
        targetOnly:kept

        """);

    CreateInstance(
        minecraftRootA,
        fabricSharedName,
        "VersionArgumentIndieV2:FALSE\r\n",
        """
        {
          "id": "fabric-shared",
          "minecraftVersion": "1.20.1",
          "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
          "libraries": [
            { "name": "net.fabricmc:fabric-loader:0.16.14" }
          ]
        }
        """,
        null,
        null);

    CreateInstance(
        minecraftRootA,
        quiltSharedName,
        "VersionArgumentIndieV2:0\r\n",
        """
        {
          "id": "quilt-shared",
          "mainClass": "org.quiltmc.loader.impl.launch.knot.KnotClient",
          "patches": [
            { "id": "game", "version": "1.20.4" },
            { "id": "quilt", "version": "0.27.1" }
          ]
        }
        """,
        null,
        null);

    CreateInstance(
        minecraftRootA,
        legacyIsolatedName,
        "VersionArgumentIndie:1\r\n",
        "{ \"id\": \"1.19.4\", \"type\": \"release\", \"mainClass\": \"net.minecraft.client.main.Main\" }",
        null,
        null);

    CreateInstance(
        minecraftRootA,
        legacySharedName,
        "VersionArgumentIndie:2\r\n",
        "{ \"id\": \"1.18.2\", \"type\": \"release\", \"mainClass\": \"net.minecraft.client.main.Main\" }",
        null,
        null);

    CreateInstance(
        minecraftRootA,
        inferredLegacyName,
        "VersionArgumentIndie:0\r\n",
        "{ \"id\": \"1.20.1\", \"type\": \"release\", \"mainClass\": \"net.minecraft.client.main.Main\" }",
        null,
        null,
        createModEvidence: true);

    CreateInstance(
        minecraftRootA,
        missingSetupName,
        null,
        "{ \"id\": \"1.20.2\", \"type\": \"release\", \"mainClass\": \"net.minecraft.client.main.Main\" }",
        null,
        null);

    CreateInstance(
        minecraftRootA,
        unknownIsolationName,
        "VersionArgumentIndieV2:sometimes\r\nVersionArgumentIndie:1\r\n",
        "{ \"id\": \"1.20.3\", \"type\": \"release\", \"mainClass\": \"net.minecraft.client.main.Main\" }",
        null,
        null);

    CreateInstance(
        minecraftRootA,
        missingJsonName,
        "VersionArgumentIndieV2:true\r\n",
        null,
        null,
        null);

    CreateInstance(
        minecraftRootA,
        nonObjectJsonName,
        "VersionArgumentIndieV2:true\r\n",
        "[]",
        null,
        null);

    CreateInstance(
        minecraftRootA,
        nonObjectManifestName,
        "VersionArgumentIndieV2:true\r\n",
        "{ \"id\": \"1.21.1\", \"mainClass\": \"net.minecraft.client.main.Main\" }",
        "[]",
        null);

    CreateInstance(
        minecraftRootA,
        precedenceName,
        "VersionArgumentIndieV2:true\r\n",
        "{ \"id\": \"1.20.1\", \"inheritsFrom\": \"1.21.1\", \"mainClass\": \"net.minecraft.client.main.Main\" }",
        null,
        null);

    CreateInstance(
        minecraftRootA,
        forgePatchName,
        "VersionArgumentIndieV2:true\r\n",
        "{ \"id\": \"custom-forge\", \"patches\": [{ \"id\": \"minecraftforge\", \"version\": \"47.2.0\", \"priority\": 1, \"mainClass\": \"cpw.mods.modlauncher.Launcher\", \"libraries\": [{ \"name\": \"net.minecraftforge:fmlloader:1.20.1-47.2.0\" }] }, { \"id\": \"game\", \"version\": \"1.20.1\", \"priority\": 0, \"mainClass\": \"net.minecraft.client.main.Main\" }] }",
        null,
        null);

    CreateInstance(
        minecraftRootA,
        fmlArgumentName,
        "VersionArgumentIndieV2:true\r\n",
        "{ \"id\": \"47.2.0\", \"mainClass\": \"cpw.mods.modlauncher.Launcher\", \"arguments\": { \"game\": [\"--fml.mcVersion\", \"1.20.1\"] } }",
        null,
        null);

    CreateInstance(
        minecraftRootA,
        invalidInheritanceName,
        "VersionArgumentIndieV2:true\r\n",
        "{ \"id\": \"custom-invalid-parent\", \"inheritsFrom\": \"\\u0000bad\", \"mainClass\": \"net.minecraft.client.main.Main\" }",
        null,
        null);

    CreateInstance(
        minecraftRootA,
        jarPrecedenceName,
        "VersionArgumentIndieV2:true\r\n",
        "{ \"id\": \"liteloader-child\", \"jar\": \"1.20.1\", \"inheritsFrom\": \"1.21.1\", \"mainClass\": \"com.mumfrey.liteloader.launch.LiteLoaderTweaker\" }",
        null,
        null);

    CreateInstance(
        minecraftRootA,
        snapshotVersionName,
        "VersionArgumentIndieV2:true\r\n",
        "{ \"id\": \"26.1-snapshot-1\", \"mainClass\": \"net.minecraft.client.main.Main\" }",
        null,
        null);

    var reparseModsInstanceRoot = CreateInstance(
        minecraftRootA,
        reparseModsName,
        "VersionArgumentIndie:0\r\n",
        "{ \"id\": \"1.20.1\", \"mainClass\": \"net.minecraft.client.main.Main\" }",
        null,
        null);

    var reparseSavesInstanceRoot = CreateInstance(
        minecraftRootA,
        reparseSavesName,
        "VersionArgumentIndie:0\r\n",
        "{ \"id\": \"1.20.1\", \"mainClass\": \"net.minecraft.client.main.Main\" }",
        null,
        null);

    CreateInstance(
        minecraftRootA,
        libraryPrecedenceName,
        "VersionArgumentIndieV2:true\r\n",
        "{ \"id\": \"fabric-conflict\", \"jar\": \"1.20.1\", \"mainClass\": \"net.fabricmc.loader.impl.launch.knot.KnotClient\", \"libraries\": [{ \"name\": \"net.fabricmc:intermediary:1.21.1\" }, { \"name\": \"net.fabricmc:fabric-loader:0.16.14\" }] }",
        null,
        null);

    CreateInstance(
        minecraftRootB,
        "Second Root Vanilla",
        "VersionArgumentIndie:1\r\n",
        "{ \"id\": \"1.21.1\", \"type\": \"release\", \"mainClass\": \"net.minecraft.client.main.Main\" }",
        null,
        null);

    var plainMinecraftRoot = Path.Combine(fixtureRoot, "plain-launcher", ".minecraft");
    var plainVersionRoot = Path.Combine(plainMinecraftRoot, "versions", "1.21.1");
    Directory.CreateDirectory(plainVersionRoot);
    File.WriteAllText(
        Path.Combine(plainVersionRoot, "1.21.1.json"),
        "{ \"id\": \"1.21.1\", \"mainClass\": \"net.minecraft.client.main.Main\" }");

    _ = CreateInstance(
        minecraftRootA,
        exactOptionsLimitName,
        "VersionArgumentIndieV2:true\r\n",
        "{ \"id\": \"1.21.1\", \"minecraftVersion\": \"1.21.1\", \"mainClass\": \"net.minecraft.client.main.Main\" }",
        null,
        "version:3955\nlang:en_us\n".PadRight(4 * 1024 * 1024, ' '));
    _ = CreateInstance(
        minecraftRootA,
        oversizedOptionsLimitName,
        "VersionArgumentIndieV2:true\r\n",
        "{ \"id\": \"1.21.1\", \"minecraftVersion\": \"1.21.1\", \"mainClass\": \"net.minecraft.client.main.Main\" }",
        null,
        "version:3955\nlang:en_us\n".PadRight(4 * 1024 * 1024 + 1, ' '));
    _ = CreateInstance(
        minecraftRootA,
        invalidSourceOptionsSchemaName,
        "VersionArgumentIndieV2:true\r\n",
        "{ \"id\": \"1.21.1\", \"minecraftVersion\": \"1.21.1\", \"mainClass\": \"net.minecraft.client.main.Main\" }",
        null,
        "version:0\nlang:zh_cn\nkey_key.jump:key.keyboard.space\n");
    _ = CreateInstance(
        minecraftRootA,
        duplicateTargetOptionsSchemaName,
        "VersionArgumentIndieV2:true\r\n",
        "{ \"id\": \"1.21.1\", \"minecraftVersion\": \"1.21.1\", \"mainClass\": \"net.minecraft.client.main.Main\" }",
        null,
        "version:3955\nversion:3955\nlang:en_us\n");

    var outsideMinecraftRoot = Path.Combine(fixtureRoot, "outside-root");
    var outsideInstanceRoot = CreateInstance(
        outsideMinecraftRoot,
        "Escaped Instance",
        "VersionArgumentIndieV2:true\r\n",
        "{ \"id\": \"1.21.1\", \"mainClass\": \"net.minecraft.client.main.Main\" }",
        null,
        null);

    var outsideModsPath = Path.Combine(fixtureRoot, "outside-mod-evidence");
    Directory.CreateDirectory(outsideModsPath);
    File.WriteAllText(Path.Combine(outsideModsPath, "external.jar"), "fixture-only");
    var outsideSavesPath = Path.Combine(fixtureRoot, "outside-save-evidence");
    Directory.CreateDirectory(Path.Combine(outsideSavesPath, "External World"));

    var versionsJunctionRoot = Path.Combine(fixtureRoot, "versions-junction-root");
    Directory.CreateDirectory(versionsJunctionRoot);
    File.WriteAllText(Path.Combine(versionsJunctionRoot, "PCL.ini"), "Version:Escaped Instance\r\n");

    var beforeReadOnlyOperations = SnapshotTree(fixtureRoot);
    var request = Pcl2DiscoveryRequest.Create(
        [launcherA, invalidExistingRoot, Path.Combine(fixtureRoot, "does-not-exist"), string.Empty],
        [Path.Combine(minecraftRootA, "versions"), minecraftRootB]);
    var discovery = discoveryService.Discover(request);

    Assert(discovery.Roots.Count == 2, "Manual and automatic candidates must resolve to two distinct fixture roots.");
    Assert(HasDiagnostic(discovery.Diagnostics, Pcl2DiagnosticCode.MultipleMinecraftRoots), "Multiple roots must have a structured diagnostic.");
    Assert(HasDiagnostic(discovery.Diagnostics, Pcl2DiagnosticCode.MinecraftRootInvalid), "An existing or missing path without versions must be diagnosed as an invalid root.");
    Assert(HasDiagnostic(discovery.Diagnostics, Pcl2DiagnosticCode.CandidatePathInvalid), "An empty candidate path must be diagnosed structurally.");

    var rootA = discovery.Roots.Single(root =>
        Pcl2PathNormalizer.AreEquivalent(root.RootPath, minecraftRootA));
    Assert(rootA.Origins.SequenceEqual([Pcl2CandidateOrigin.Manual, Pcl2CandidateOrigin.Automatic]), "Equivalent manual and automatic candidates must be deduplicated while retaining both origins.");
    Assert(rootA.Instances.Count == 26, $"Every ordinary versions/* fixture directory, including malformed and incomplete ones, must be enumerated without aborting the root scan. Actual: {rootA.Instances.Count}.");
    Assert(rootA.SelectedInstanceName == sourceName, "PCL.ini Version must identify the selected instance.");

    var source = FindInstance(rootA, sourceName);
    var target = FindInstance(rootA, targetName);
    var discoverySessionFactory = new DiscoverySessionFactory();
    using (var discoverySession = discoverySessionFactory.Create(1, discovery))
    {
        var pairValidation = discoverySessionFactory.Revalidate(
            discoverySession,
            source.Id,
            target.Id);
        Assert(
            pairValidation.IsValid &&
            pairValidation.Pair is not null &&
            pairValidation.Diagnostics.Count == 0,
            "Fresh PCL discovery results must bind to a revalidated physical source/target session.");
    }

    var fabricShared = FindInstance(rootA, fabricSharedName);
    var quiltShared = FindInstance(rootA, quiltSharedName);
    var legacyIsolated = FindInstance(rootA, legacyIsolatedName);
    var legacyShared = FindInstance(rootA, legacySharedName);
    var inferredLegacy = FindInstance(rootA, inferredLegacyName);
    var missingSetup = FindInstance(rootA, missingSetupName);
    var unknownIsolation = FindInstance(rootA, unknownIsolationName);
    var missingJson = FindInstance(rootA, missingJsonName);
    var nonObjectJson = FindInstance(rootA, nonObjectJsonName);
    var nonObjectManifest = FindInstance(rootA, nonObjectManifestName);
    var precedence = FindInstance(rootA, precedenceName);
    var forgePatch = FindInstance(rootA, forgePatchName);
    var fmlArgument = FindInstance(rootA, fmlArgumentName);
    var invalidInheritance = FindInstance(rootA, invalidInheritanceName);
    var jarPrecedence = FindInstance(rootA, jarPrecedenceName);
    var snapshotVersion = FindInstance(rootA, snapshotVersionName);
    var reparseMods = FindInstance(rootA, reparseModsName);
    var reparseSaves = FindInstance(rootA, reparseSavesName);
    var libraryPrecedence = FindInstance(rootA, libraryPrecedenceName);
    var exactOptionsLimit = FindInstance(rootA, exactOptionsLimitName);
    var oversizedOptionsLimit = FindInstance(rootA, oversizedOptionsLimitName);
    var invalidSourceOptionsSchema = FindInstance(rootA, invalidSourceOptionsSchemaName);
    var duplicateTargetOptionsSchema = FindInstance(rootA, duplicateTargetOptionsSchemaName);

    Assert(source.IsSelected, "The source selected by PCL.ini must be marked selected.");
    Assert(source.Isolation == Pcl2IsolationMode.Isolated, "VersionArgumentIndieV2 true must mean isolated.");
    Assert(target.Isolation == Pcl2IsolationMode.Isolated, "VersionArgumentIndieV2 1 must mean isolated.");
    Assert(fabricShared.Isolation == Pcl2IsolationMode.SharedMinecraftRoot, "VersionArgumentIndieV2 false must mean shared.");
    Assert(quiltShared.Isolation == Pcl2IsolationMode.SharedMinecraftRoot, "VersionArgumentIndieV2 0 must mean shared.");
    Assert(legacyIsolated.Isolation == Pcl2IsolationMode.Isolated, "Legacy VersionArgumentIndie 1 must mean isolated.");
    Assert(legacyShared.Isolation == Pcl2IsolationMode.SharedMinecraftRoot, "Legacy VersionArgumentIndie 2 must mean shared.");
    Assert(inferredLegacy.Isolation == Pcl2IsolationMode.Isolated, "Legacy global mode may be inferred as isolated only from non-empty instance mods/saves evidence.");
    Assert(HasDiagnostic(inferredLegacy.Diagnostics, Pcl2DiagnosticCode.IsolationInferredFromContent), "Content-based isolation inference must be explicit in diagnostics.");

    Assert(Pcl2PathNormalizer.AreEquivalent(source.GameRoot!, sourceRoot), "An isolated instance gameRoot must be its normalized instanceRoot.");
    Assert(Pcl2PathNormalizer.AreEquivalent(fabricShared.GameRoot!, minecraftRootA), "A non-isolated instance gameRoot must be the normalized Minecraft root.");
    Assert(HasDiagnostic(fabricShared.Diagnostics, Pcl2DiagnosticCode.NonIsolatedInstance), "A non-isolated instance must carry an explicit diagnostic.");
    Assert(missingSetup.GameRoot is null, "Missing Setup.ini without safe evidence must leave gameRoot unresolved.");
    Assert(HasDiagnostic(missingSetup.Diagnostics, Pcl2DiagnosticCode.SetupMissing), "Missing Setup.ini must be diagnosed.");
    Assert(HasDiagnostic(missingSetup.Diagnostics, Pcl2DiagnosticCode.GameRootUnresolved), "Missing isolation must diagnose an unresolved gameRoot.");
    Assert(unknownIsolation.GameRoot is null, "An unknown V2 value must not fall back to a legacy value.");
    Assert(HasDiagnostic(unknownIsolation.Diagnostics, Pcl2DiagnosticCode.IsolationSettingUnknown), "An unknown isolation value must be diagnosed.");
    Assert(HasDiagnostic(missingJson.Diagnostics, Pcl2DiagnosticCode.InstanceJsonMissing), "A versions directory without JSON must remain visible with a diagnostic.");

    Assert(source.MinecraftVersion == "1.21.1", "Minecraft version must be read from inheritsFrom.");
    Assert(source.ModLoaders.Single().Kind == Pcl2ModLoaderKind.NeoForge, "NeoForge must be identified from its Maven coordinate.");
    Assert(target.ModLoaders.Single().Kind == Pcl2ModLoaderKind.Forge, "Forge must be identified from its Maven coordinate.");
    Assert(fabricShared.ModLoaders.Single().Kind == Pcl2ModLoaderKind.Fabric, "Fabric must be identified from its loader coordinate.");
    Assert(quiltShared.ModLoaders.Single().Kind == Pcl2ModLoaderKind.Quilt, "Quilt must be identified from PCL patch metadata.");
    Assert(legacyIsolated.ModLoaders.Single().Kind == Pcl2ModLoaderKind.Vanilla, "A readable version JSON without loader metadata must be identified as vanilla.");
    Assert(missingJson.ModLoaders.Single().Kind == Pcl2ModLoaderKind.Unknown, "An unreadable loader identity must remain unknown rather than being called vanilla.");
    Assert(!nonObjectJson.HasUsableVersionMetadata, "A non-object version JSON must be unusable rather than crashing discovery.");
    Assert(nonObjectJson.ModLoaders.Single().Kind == Pcl2ModLoaderKind.Unknown, "A non-object version JSON must never be labeled Vanilla.");
    Assert(HasDiagnostic(nonObjectJson.Diagnostics, Pcl2DiagnosticCode.InstanceJsonSchemaInvalid), "A non-object version JSON must have a structured schema diagnostic.");
    Assert(HasDiagnostic(nonObjectManifest.Diagnostics, Pcl2DiagnosticCode.ManifestInvalid), "A non-object optional manifest must be ignored with a structured diagnostic.");
    Assert(precedence.MinecraftVersion == "1.21.1", "inheritsFrom must outrank a conflicting child id for Minecraft version detection.");
    Assert(forgePatch.MinecraftVersion == "1.20.1", "A minecraftforge loader patch version must not be mistaken for the Minecraft game version.");
    Assert(forgePatch.HasUsableVersionMetadata, "An HMCL patches-only JSON must merge patch mainClass metadata and remain usable.");
    Assert(forgePatch.ModLoaders.Any(loader => loader.Kind == Pcl2ModLoaderKind.Forge), "An HMCL loader patch/fmlloader coordinate must identify Forge.");
    Assert(fmlArgument.MinecraftVersion == "1.20.1", "--fml.mcVersion must outrank a conflicting id fallback.");
    Assert(!invalidInheritance.HasUsableVersionMetadata, "An inheritsFrom value containing an invalid path character must be blocked without terminating discovery.");
    Assert(HasDiagnostic(invalidInheritance.Diagnostics, Pcl2DiagnosticCode.InheritancePathInvalid), "An invalid inheritsFrom path must have a structured diagnostic.");
    Assert(jarPrecedence.MinecraftVersion == "1.20.1", "PCL's jar value must outrank inheritsFrom for LiteLoader-style version JSON.");
    Assert(snapshotVersion.MinecraftVersion == "26.1-snapshot-1", "PCL snapshot-suffix version ids must remain recognizable.");
    Assert(reparseMods.Isolation == Pcl2IsolationMode.Unknown, "Legacy global isolation without ordinary local evidence must remain unknown.");
    Assert(reparseSaves.Isolation == Pcl2IsolationMode.Unknown, "Legacy global isolation without ordinary local save evidence must remain unknown.");
    Assert(libraryPrecedence.MinecraftVersion == "1.21.1", "Without inheritance, an intermediary/loader game library must outrank a conflicting jar fallback.");
    Assert(source.ModpackIdentity.Name == "All the Mods 10", "A standard manifest must provide modpack identity.");
    Assert(source.ModpackIdentity.Version == "7.3-r19", "Manifest version must be retained.");
    Assert(source.ModpackIdentity.Confidence == Pcl2IdentityConfidence.High, "Manifest identity must be high confidence.");
    Assert(source.ModpackIdentity.Source == Pcl2IdentitySource.Manifest, "Manifest identity source must be explicit.");
    Assert(target.ModpackIdentity.Confidence == Pcl2IdentityConfidence.High, "Explicit instance-JSON modpack metadata must be high confidence.");
    Assert(fabricShared.ModpackIdentity.Confidence == Pcl2IdentityConfidence.Low, "Directory-name fallback must be labeled low confidence.");

    Assert(SnapshotTree(fixtureRoot) == beforeReadOnlyOperations, "PCL discovery must not modify fixture files or create directories.");

    var previewer = new Pcl2OptionsMigrationPreviewer(fileSystem);
    var driveRoot = Path.GetPathRoot(fixtureRoot) ??
        throw new InvalidOperationException("The fixture path must have a filesystem root.");
    Assert(Pcl2PathNormalizer.Normalize(driveRoot) == Path.GetFullPath(driveRoot), "Path normalization must retain a drive-root separator.");

    var preview = previewer.Preview(source, target);
    Assert(!preview.IsBlocked, "Different normalized isolated game roots must permit a dry-run preview.");
    Assert(Pcl2PathNormalizer.AreEquivalent(preview.SourceGameRoot!, sourceRoot), "Preview must expose a normalized source gameRoot.");
    Assert(Pcl2PathNormalizer.AreEquivalent(preview.TargetGameRoot!, targetRoot), "Preview must expose a normalized target gameRoot.");
    var mergeResult = preview.MergeResult ??
        throw new InvalidOperationException("A permitted preview must return OptionsMergePlanner output.");
    Assert(preview.WouldChangeTarget, "Fixture options must produce a target change.");
    Assert(Value(mergeResult.Content, "lang") == "zh_cn", "Dry-run must carry source language.");
    Assert(Value(mergeResult.Content, "version") == "3955", "Dry-run must protect target options schema.");
    Assert(Value(mergeResult.Content, "resourcePacks") == "[\"vanilla\",\"file/new-pack.zip\"]", "Dry-run must protect target resource packs.");
    Assert(preview.Differences.Any(item => item.Key == "lang" && item.Decision == OptionsMergeDecision.UseSource), "Preview must return semantic option differences.");
    Assert(preview.Differences.Any(item => item.Key == "resourcePacks" && item.Decision == OptionsMergeDecision.PreserveTarget), "Preview must expose protected target-owned differences.");

    var preparation = previewer.PrepareSelection(source, target);
    Assert(!preparation.IsBlocked && preparation.Session is not null, "proven distinct roots must prepare a selection session.");
    var session = preparation.Session!;
    Assert(session.Catalog.SelectableDifferences.Any(item => item.Key == "lang"), "session must expose selectable language difference.");
    var exactLimitPreparation = previewer.PrepareSelection(exactOptionsLimit, target);
    Assert(
        !exactLimitPreparation.IsBlocked && exactLimitPreparation.Session is not null,
        "An options.txt of exactly 4 MiB must remain readable by the legacy selection seam.");
    var oversizedLimitPreparation = previewer.PrepareSelection(oversizedOptionsLimit, target);
    Assert(
        oversizedLimitPreparation.IsBlocked &&
        oversizedLimitPreparation.Session is null &&
        HasDiagnostic(oversizedLimitPreparation.Diagnostics, Pcl2DiagnosticCode.OptionsReadFailed),
        "An options.txt of 4 MiB plus one byte must be rejected by the capability before parsing.");
    var invalidSchemaPreview = previewer.Preview(invalidSourceOptionsSchema, target);
    var invalidSchemaPreparation = previewer.PrepareSelection(invalidSourceOptionsSchema, target);
    Assert(invalidSchemaPreview.IsBlocked &&
           HasDiagnostic(invalidSchemaPreview.Diagnostics, Pcl2DiagnosticCode.OptionsSchemaUnsupported) &&
           invalidSchemaPreparation.IsBlocked &&
           invalidSchemaPreparation.Session is null &&
           HasDiagnostic(invalidSchemaPreparation.Diagnostics, Pcl2DiagnosticCode.OptionsSchemaUnsupported),
        "Legacy preview and selection preparation must reject a nonpositive source options data version.");
    var duplicateSchemaPreview = previewer.Preview(source, duplicateTargetOptionsSchema);
    var duplicateSchemaPreparation = previewer.PrepareSelection(source, duplicateTargetOptionsSchema);
    Assert(duplicateSchemaPreview.IsBlocked &&
           HasDiagnostic(duplicateSchemaPreview.Diagnostics, Pcl2DiagnosticCode.OptionsSchemaUnsupported) &&
           duplicateSchemaPreparation.IsBlocked &&
           duplicateSchemaPreparation.Session is null &&
           HasDiagnostic(duplicateSchemaPreparation.Diagnostics, Pcl2DiagnosticCode.OptionsSchemaUnsupported),
        "Legacy preview and selection preparation must reject duplicate target options data versions.");

    var callerOwnedProtectedKeys = new HashSet<string>(["lang"], StringComparer.Ordinal);
    var customProtectionPreviewer = new Pcl2OptionsMigrationPreviewer(
        fileSystem,
        new OptionsMergePlanner(callerOwnedProtectedKeys));
    callerOwnedProtectedKeys.Clear();
    callerOwnedProtectedKeys.Add("gamma");
    var customLegacyPreview = customProtectionPreviewer.Preview(source, target);
    Assert(!customLegacyPreview.IsBlocked && customLegacyPreview.MergeResult is not null, "A custom planner must still support legacy preview.");
    Assert(
        Value(customLegacyPreview.MergeResult!.Content, "lang") == "en_us" &&
        Value(customLegacyPreview.MergeResult.Content, "resourcePacks") == "[\"vanilla\",\"file/new-pack.zip\"]" &&
        Value(customLegacyPreview.MergeResult.Content, "incompatibleResourcePacks") == "[\"file/new-pack.zip\"]" &&
        Value(customLegacyPreview.MergeResult.Content, "version") == "3955",
        "Legacy preview must defensively retain caller protection, pack-owned resource keys, and the initialized target version.");
    var customProtectionPreparation = customProtectionPreviewer.PrepareSelection(source, target);
    Assert(
        !customProtectionPreparation.IsBlocked && customProtectionPreparation.Session is not null,
        "A custom protected option must not block selection preparation for otherwise valid roots.");
    Assert(
        customProtectionPreparation.Session!.Catalog.SelectableDifferences.All(item => item.Key != "lang"),
        "A custom planner-protected option must never be presented as selectable in the UI catalog.");
    Assert(
        customProtectionPreparation.Session.Catalog.ProtectedDifferences.Any(item => item.Key == "lang"),
        "A custom planner-protected option must appear in the catalog's locked safety group.");
    var customProtectionPreview = customProtectionPreviewer.PreviewSelected(
        customProtectionPreparation.Session,
        new HashSet<string>(["lang"], StringComparer.Ordinal));
    Assert(
        customProtectionPreview.PlannedChanges.All(item => item.Key != "lang") &&
        customProtectionPreview.ProtectedDifferences.Any(item => item.Key == "lang"),
        "A custom planner-protected option must remain protected even if a stale caller submits its key.");

    var selectedPreview = previewer.PreviewSelected(
        session,
        new HashSet<string>(["lang", "resourcePacks", "incompatibleResourcePacks", "version"], StringComparer.Ordinal));
    Assert(!selectedPreview.IsBlocked && !selectedPreview.IsStale, "matching snapshots must preview.");
    Assert(selectedPreview.PlannedChanges.Select(item => item.Key).SequenceEqual(["lang"]), "UI result must contain selected planned changes only.");
    Assert(selectedPreview.ProtectedDifferences.Select(item => item.Key).ToHashSet(StringComparer.Ordinal).SetEquals(["resourcePacks", "incompatibleResourcePacks"]), "differing pack-owned resource keys must remain separate while the matching target version stays unchanged.");
    Assert(Value(selectedPreview.Content!, "resourcePacks") == "[\"vanilla\",\"file/new-pack.zip\"]", "selected preview must preserve target pack resources.");
    Assert(Value(selectedPreview.Content!, "incompatibleResourcePacks") == "[\"file/new-pack.zip\"]", "selected preview must preserve target incompatible packs.");
    Assert(Value(selectedPreview.Content!, "version") == "3955", "selected preview must preserve target version.");

    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    var preparationCanceled = false;
    try
    {
        previewer.PrepareSelection(source, target, canceled.Token);
    }
    catch (OperationCanceledException)
    {
        preparationCanceled = true;
    }
    Assert(preparationCanceled, "pre-canceled preparation must throw OperationCanceledException.");

    var previewCanceled = false;
    try
    {
        previewer.PreviewSelected(session, new HashSet<string>(["lang"], StringComparer.Ordinal), canceled.Token);
    }
    catch (OperationCanceledException)
    {
        previewCanceled = true;
    }
    Assert(previewCanceled, "pre-canceled selected preview must throw OperationCanceledException.");

    var sameSharedRoot = previewer.Preview(fabricShared, quiltShared);
    Assert(sameSharedRoot.IsBlocked, "Two non-isolated versions sharing one gameRoot must be blocked.");
    Assert(sameSharedRoot.MergeResult is null, "A same-root preview must not call the merge planner.");
    Assert(HasDiagnostic(sameSharedRoot.Diagnostics, Pcl2DiagnosticCode.SameSourceAndTarget), "A same normalized source/target must have a structured blocker.");
    var sameSharedPreparation = previewer.PrepareSelection(fabricShared, quiltShared);
    Assert(sameSharedPreparation.IsBlocked && sameSharedPreparation.Session is null, "Selection preparation must block a normalized same game root.");
    Assert(HasDiagnostic(sameSharedPreparation.Diagnostics, Pcl2DiagnosticCode.SameSourceAndTarget), "Selection preparation must retain the structured same-root blocker.");

    var logicalAlias = source with
    {
        GameRoot = Path.Combine(source.GameRoot!, ".") + Path.DirectorySeparatorChar,
    };
    var sameLogicalAlias = previewer.Preview(logicalAlias, source);
    Assert(sameLogicalAlias.IsBlocked, "Dot-segment and trailing-separator aliases of one gameRoot must be blocked.");
    Assert(HasDiagnostic(sameLogicalAlias.Diagnostics, Pcl2DiagnosticCode.SameSourceAndTarget), "Logical gameRoot aliases must normalize to the same structured blocker.");

    var forgedContract = source with
    {
        GameRoot = target.GameRoot,
    };
    var forgedPreview = previewer.Preview(forgedContract, target);
    Assert(forgedPreview.IsBlocked, "A public Pcl2Instance with a gameRoot inconsistent with its isolation contract must be blocked.");
    Assert(HasDiagnostic(forgedPreview.Diagnostics, Pcl2DiagnosticCode.InstanceContractMismatch), "A forged instance contract must have a structured blocker.");
    var forgedPreparation = previewer.PrepareSelection(forgedContract, target);
    Assert(forgedPreparation.IsBlocked && forgedPreparation.Session is null, "Selection preparation must reject a forged path contract.");
    Assert(HasDiagnostic(forgedPreparation.Diagnostics, Pcl2DiagnosticCode.InstanceContractMismatch), "A forged selection preparation must retain the structured proof blocker.");

    var selfConsistentForgery = new Pcl2Instance(
        source.Id,
        source.DisplayName,
        source.MinecraftRoot,
        source.InstanceRoot,
        source.GameRoot,
        source.InstanceJsonPath,
        source.SetupPath,
        source.Isolation,
        source.MinecraftVersion,
        source.ModLoaders,
        source.ModpackIdentity,
        source.HasUsableVersionMetadata,
        source.IsSelected,
        source.Diagnostics);
    var provenanceBlocked = previewer.Preview(selfConsistentForgery, target);
    Assert(provenanceBlocked.IsBlocked, "A field-consistent public record that did not come from discovery must still be blocked.");
    Assert(HasDiagnostic(provenanceBlocked.Diagnostics, Pcl2DiagnosticCode.InstanceContractMismatch), "A missing discovery proof must have a structured blocker.");
    var provenancePreparationBlocked = previewer.PrepareSelection(selfConsistentForgery, target);
    Assert(provenancePreparationBlocked.IsBlocked && provenancePreparationBlocked.Session is null, "Selection preparation must reject a record without discovery proof.");
    Assert(HasDiagnostic(provenancePreparationBlocked.Diagnostics, Pcl2DiagnosticCode.InstanceContractMismatch), "Selection preparation without proof must be diagnosed structurally.");

    var unresolvedRoot = previewer.Preview(missingSetup, target);
    Assert(unresolvedRoot.IsBlocked, "An unresolved gameRoot must block preview.");
    Assert(HasDiagnostic(unresolvedRoot.Diagnostics, Pcl2DiagnosticCode.GameRootUnresolved), "Unresolved preview paths must be diagnosed.");

    var missingTargetOptionsPath = Path.Combine(legacyIsolated.GameRoot!, "options.txt");
    Assert(!File.Exists(missingTargetOptionsPath), "The missing-target options fixture must start absent.");
    var unstartedTarget = previewer.Preview(source, legacyIsolated);
    Assert(!unstartedTarget.IsBlocked, "A missing target options.txt must still permit an unstarted-target dry-run.");
    Assert(HasDiagnostic(unstartedTarget.Diagnostics, Pcl2DiagnosticCode.TargetOptionsMissing), "A missing target options.txt must be reported.");
    Assert(unstartedTarget.MergeResult is not null && unstartedTarget.MergeResult.Changed, "The merge planner must receive an empty target for a dry-run.");
    Assert(Value(unstartedTarget.MergeResult!.Content, "version") == "3955", "An unstarted Minecraft 1.21.1 target preview must include data version 3955.");
    Assert(!File.Exists(missingTargetOptionsPath), "Dry-run must not create a missing target options.txt.");
    var missingTargetPreparation = previewer.PrepareSelection(source, legacyIsolated);
    Assert(missingTargetPreparation.Session is not null &&
           missingTargetPreparation.Session.Catalog.RequiredChanges.Single().Key == "version",
        "A missing-target session must prepare one automatic schema prerequisite without creating options.txt.");

    var missingSourceOptions = previewer.Preview(legacyIsolated, target);
    Assert(missingSourceOptions.IsBlocked, "A missing source options.txt must block a meaningless migration preview.");
    Assert(HasDiagnostic(missingSourceOptions.Diagnostics, Pcl2DiagnosticCode.SourceOptionsMissing), "A missing source options.txt must be diagnosed.");

    var noCandidates = discoveryService.Discover(
        Pcl2DiscoveryRequest.Create(Array.Empty<string>(), Array.Empty<string>()));
    Assert(noCandidates.Roots.Count == 0, "No supplied candidates must discover no roots.");
    Assert(HasDiagnostic(noCandidates.Diagnostics, Pcl2DiagnosticCode.Pcl2NotFound), "No candidates must report PCL2 not found.");

    var plainMinecraft = discoveryService.Discover(
        Pcl2DiscoveryRequest.Create([plainMinecraftRoot], Array.Empty<string>()));
    Assert(plainMinecraft.Roots.Count == 0, "A plain official-launcher root with versions but no PCL evidence must not be reported as PCL2.");
    Assert(HasDiagnostic(plainMinecraft.Diagnostics, Pcl2DiagnosticCode.Pcl2NotFound), "A plain non-PCL Minecraft root must report PCL2 not found.");

    var junctionInstanceRoot = Path.Combine(minecraftRootA, "versions", "Escaped Junction");
    Assert(
        TryCreateDirectoryJunction(junctionInstanceRoot, outsideInstanceRoot),
        "The Windows fixture must be able to create a temporary directory junction for the read-boundary regression.");
    try
    {
        var junctionDiscovery = discoveryService.Discover(
            Pcl2DiscoveryRequest.Create([minecraftRootA], Array.Empty<string>()));
        Assert(
            junctionDiscovery.Roots.Single().Instances.All(instance =>
                !instance.InstanceRoot.Equals(junctionInstanceRoot, StringComparison.OrdinalIgnoreCase)),
            "A versions child that is a junction must never be read as a PCL instance.");
        Assert(HasDiagnostic(junctionDiscovery.Diagnostics, Pcl2DiagnosticCode.ReparsePointRejected), "A rejected instance junction must have a structured diagnostic.");
    }
    finally
    {
        if (Directory.Exists(junctionInstanceRoot))
        {
            Directory.Delete(junctionInstanceRoot);
        }
    }

    var versionsJunctionPath = Path.Combine(versionsJunctionRoot, "versions");
    var modsJunctionPath = Path.Combine(reparseModsInstanceRoot, "mods");
    var savesJunctionPath = Path.Combine(reparseSavesInstanceRoot, "saves");
    try
    {
        Assert(
            TryCreateDirectoryJunction(versionsJunctionPath, Path.Combine(outsideMinecraftRoot, "versions")),
            "The fixture must create a temporary versions junction.");
        Assert(
            TryCreateDirectoryJunction(modsJunctionPath, outsideModsPath),
            "The fixture must create a temporary mods junction.");
        Assert(
            TryCreateDirectoryJunction(savesJunctionPath, outsideSavesPath),
            "The fixture must create a temporary saves junction.");

        var versionsJunctionDiscovery = discoveryService.Discover(
            Pcl2DiscoveryRequest.Create([versionsJunctionRoot], Array.Empty<string>()));
        Assert(versionsJunctionDiscovery.Roots.Count == 0, "A Minecraft root whose versions directory is a junction must be rejected before enumeration.");
        Assert(HasDiagnostic(versionsJunctionDiscovery.Diagnostics, Pcl2DiagnosticCode.ReparsePointRejected), "A rejected versions junction must have a structured diagnostic.");

        var evidenceJunctionDiscovery = discoveryService.Discover(
            Pcl2DiscoveryRequest.Create([minecraftRootA], Array.Empty<string>()));
        var modsEvidenceInstance = FindInstance(evidenceJunctionDiscovery.Roots.Single(), reparseModsName);
        var savesEvidenceInstance = FindInstance(evidenceJunctionDiscovery.Roots.Single(), reparseSavesName);
        Assert(modsEvidenceInstance.Isolation == Pcl2IsolationMode.Unknown, "A mods junction must not provide isolation evidence.");
        Assert(savesEvidenceInstance.Isolation == Pcl2IsolationMode.Unknown, "A saves junction must not provide isolation evidence.");
        Assert(HasDiagnostic(modsEvidenceInstance.Diagnostics, Pcl2DiagnosticCode.ReparsePointRejected), "A rejected mods junction must have a structured diagnostic.");
        Assert(HasDiagnostic(savesEvidenceInstance.Diagnostics, Pcl2DiagnosticCode.ReparsePointRejected), "A rejected saves junction must have a structured diagnostic.");
    }
    finally
    {
        foreach (var linkPath in new[] { versionsJunctionPath, modsJunctionPath, savesJunctionPath })
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }
        }
    }

    Assert(SnapshotTree(fixtureRoot) == beforeReadOnlyOperations, "Discovery and every options preview must remain byte-for-byte and tree-shape read-only.");

    var targetRootBackup = targetRoot + "-reparse-backup";
    Directory.Move(targetRoot, targetRootBackup);
    try
    {
        Assert(TryCreateDirectoryJunction(targetRoot, targetRootBackup), "The fixture must create a temporary target-root junction.");
        var beforeSelectionReparseCheck = SnapshotTree(fixtureRoot);
        var preparationReparseBlocked = previewer.PrepareSelection(source, target);
        Assert(preparationReparseBlocked.IsBlocked && preparationReparseBlocked.Session is null, "Selection preparation must block a reparse-point game root.");
        Assert(HasDiagnostic(preparationReparseBlocked.Diagnostics, Pcl2DiagnosticCode.ReparsePointRejected), "Selection preparation must retain the structured reparse blocker.");
        var selectionReparseBlocked = previewer.PreviewSelected(
            session,
            session.Catalog.SelectableDifferences.Select(item => item.Key).ToHashSet(StringComparer.Ordinal));
        Assert(selectionReparseBlocked.IsBlocked && !selectionReparseBlocked.IsStale, "Selected preview must block a reparse-point game root before stale planning.");
        Assert(HasDiagnostic(selectionReparseBlocked.Diagnostics, Pcl2DiagnosticCode.ReparsePointRejected), "Selected preview must retain the structured reparse blocker.");
        Assert(SnapshotTree(fixtureRoot) == beforeSelectionReparseCheck, "Selection reparse blocking must perform zero writes.");
    }
    finally
    {
        if (Directory.Exists(targetRoot))
        {
            Directory.Delete(targetRoot);
        }

        Directory.Move(targetRootBackup, targetRoot);
    }

    var originalTargetBytes = File.ReadAllBytes(session.TargetOptionsPath);
    File.AppendAllText(session.TargetOptionsPath, "fullscreen:true\n");
    var beforeTargetHashStaleCheck = SnapshotTree(fixtureRoot);
    var targetHashStale = previewer.PreviewSelected(
        session,
        session.Catalog.SelectableDifferences.Select(item => item.Key).ToHashSet(StringComparer.Ordinal));
    AssertExactStaleShape(targetHashStale, "changed present-target content hash");
    Assert(SnapshotTree(fixtureRoot) == beforeTargetHashStaleCheck, "present-target hash stale detection must perform zero writes.");
    File.WriteAllBytes(session.TargetOptionsPath, originalTargetBytes);

    Assert(!originalTargetBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), "The BOM regression fixture must begin as UTF-8 without a BOM.");
    byte[] bomTargetBytes = [.. Encoding.UTF8.Preamble, .. originalTargetBytes];
    Assert(DecodeBomAware(bomTargetBytes) == DecodeBomAware(originalTargetBytes), "Adding only a UTF-8 BOM must leave decoded options text unchanged.");
    File.WriteAllBytes(session.TargetOptionsPath, bomTargetBytes);
    var beforeBomStaleCheck = SnapshotTree(fixtureRoot);
    var bomStale = previewer.PreviewSelected(
        session,
        session.Catalog.SelectableDifferences.Select(item => item.Key).ToHashSet(StringComparer.Ordinal));
    AssertExactStaleShape(bomStale, "raw bytes changed by a UTF-8 BOM while decoded text stayed the same");
    Assert(SnapshotTree(fixtureRoot) == beforeBomStaleCheck, "raw-byte BOM stale detection must perform zero writes.");
    File.WriteAllBytes(session.TargetOptionsPath, originalTargetBytes);

    File.WriteAllText(missingTargetOptionsPath, string.Empty);
    var beforeMissingTargetStaleCheck = SnapshotTree(fixtureRoot);
    var missingTargetStale = previewer.PreviewSelected(
        missingTargetPreparation.Session!,
        missingTargetPreparation.Session!.Catalog.SelectableDifferences.Select(item => item.Key).ToHashSet(StringComparer.Ordinal));
    AssertExactStaleShape(missingTargetStale, "creating an empty target changed the existence fingerprint");
    Assert(SnapshotTree(fixtureRoot) == beforeMissingTargetStaleCheck, "existence stale detection must perform zero writes.");

    File.AppendAllText(session.SourceOptionsPath, "gamma:0.75\n");
    var beforeStalePreview = SnapshotTree(fixtureRoot);
    var stalePreview = previewer.PreviewSelected(
        session,
        session.Catalog.SelectableDifferences.Select(item => item.Key).ToHashSet(StringComparer.Ordinal));
    Assert(stalePreview.IsStale && stalePreview.IsBlocked, "changed source fingerprint must invalidate preview.");
    AssertExactStaleShape(stalePreview, "changed source content hash");
    Assert(SnapshotTree(fixtureRoot) == beforeStalePreview, "hash stale detection itself must perform zero writes.");

    Console.WriteLine("PASS: PCL2 manual/automatic root discovery, versions fixtures, isolation semantics, metadata identity, structured diagnostics, normalized path blockers, and read-only options dry-run");
}
finally
{
    fixture.Dispose();
}

static string CreateInstance(
    string minecraftRoot,
    string name,
    string? setupContent,
    string? versionJson,
    string? manifestJson,
    string? optionsContent,
    bool createModEvidence = false)
{
    var instanceRoot = Path.Combine(minecraftRoot, "versions", name);
    Directory.CreateDirectory(instanceRoot);
    if (setupContent is not null)
    {
        var pclDirectory = Path.Combine(instanceRoot, "PCL");
        Directory.CreateDirectory(pclDirectory);
        File.WriteAllText(Path.Combine(pclDirectory, "Setup.ini"), setupContent);
    }

    if (versionJson is not null)
    {
        File.WriteAllText(Path.Combine(instanceRoot, name + ".json"), versionJson);
    }

    if (manifestJson is not null)
    {
        File.WriteAllText(Path.Combine(instanceRoot, "manifest.json"), manifestJson);
    }

    if (optionsContent is not null)
    {
        File.WriteAllText(Path.Combine(instanceRoot, "options.txt"), optionsContent);
    }

    if (createModEvidence)
    {
        var modsPath = Path.Combine(instanceRoot, "mods");
        Directory.CreateDirectory(modsPath);
        File.WriteAllText(Path.Combine(modsPath, "fixture.jar.disabled"), "fixture-only");
    }

    return Pcl2PathNormalizer.Normalize(instanceRoot);
}

static void AssertExactStaleShape(Pcl2SelectedOptionsPreview preview, string scenario)
{
    Assert(preview.IsBlocked && preview.IsStale, $"{scenario} must return blocked and stale.");
    Assert(preview.Content is null, $"{scenario} must not return content.");
    Assert(preview.PlannedChanges.Count == 0, $"{scenario} must not return planned changes.");
    Assert(preview.SkippedDifferences.Count == 0, $"{scenario} must not return skipped differences.");
    Assert(preview.ProtectedDifferences.Count == 0, $"{scenario} must not return protected differences.");
    Assert(preview.TargetOnlyItems.Count == 0, $"{scenario} must not return target-only items.");
    Assert(
        preview.Diagnostics.Count == 1 &&
        preview.Diagnostics[0].Code == Pcl2DiagnosticCode.OptionsSnapshotChanged &&
        preview.Diagnostics[0].Severity == Pcl2DiagnosticSeverity.Error,
        $"{scenario} must return exactly one OptionsSnapshotChanged error.");
}

static string DecodeBomAware(byte[] bytes)
{
    using var stream = new MemoryStream(bytes, writable: false);
    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
    return reader.ReadToEnd();
}

static Pcl2Instance FindInstance(Pcl2MinecraftRoot root, string directoryName) =>
    root.Instances.Single(instance =>
        Path.GetFileName(instance.InstanceRoot).Equals(directoryName, StringComparison.Ordinal));

static bool HasDiagnostic(
    IEnumerable<Pcl2Diagnostic> diagnostics,
    Pcl2DiagnosticCode code) =>
    diagnostics.Any(diagnostic => diagnostic.Code == code);

static string? Value(string content, string key)
{
    var prefix = key + ':';
    var line = content.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
        .LastOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));
    return line?[prefix.Length..];
}

static string SnapshotTree(string root)
{
    var normalizedRoot = Pcl2PathNormalizer.Normalize(root);
    var entries = new List<string>();
    entries.AddRange(Directory.EnumerateDirectories(normalizedRoot, "*", SearchOption.AllDirectories)
        .Select(path => "D|" + Path.GetRelativePath(normalizedRoot, path))
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

    foreach (var path in Directory.EnumerateFiles(normalizedRoot, "*", SearchOption.AllDirectories)
                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
    {
        var info = new FileInfo(path);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        entries.Add($"F|{Path.GetRelativePath(normalizedRoot, path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{hash}");
    }

    return string.Join('\n', entries);
}

static bool TryCreateDirectoryJunction(string junctionPath, string targetPath)
{
    if (!OperatingSystem.IsWindows())
    {
        try
        {
            Directory.CreateSymbolicLink(junctionPath, targetPath);
            return (File.GetAttributes(junctionPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    var startInfo = new ProcessStartInfo
    {
        FileName = "cmd.exe",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    startInfo.ArgumentList.Add("/d");
    startInfo.ArgumentList.Add("/c");
    startInfo.ArgumentList.Add("mklink");
    startInfo.ArgumentList.Add("/J");
    startInfo.ArgumentList.Add(junctionPath);
    startInfo.ArgumentList.Add(targetPath);

    using var process = Process.Start(startInfo);
    if (process is null)
    {
        return false;
    }

    process.WaitForExit();
    return process.ExitCode == 0 &&
           Directory.Exists(junctionPath) &&
           (File.GetAttributes(junctionPath) & FileAttributes.ReparsePoint) != 0;
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class FixtureSandbox : IDisposable
{
    public FixtureSandbox()
    {
        RootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public void Dispose()
    {
        if (!Directory.Exists(RootPath))
        {
            return;
        }

        var normalized = Pcl2PathNormalizer.Normalize(RootPath);
        var expectedParent = Pcl2PathNormalizer.Normalize(Path.GetTempPath());
        var parent = Path.GetDirectoryName(normalized);
        var leaf = Path.GetFileName(normalized);
        if (!string.Equals(parent, expectedParent, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(leaf, "D", out _))
        {
            throw new InvalidOperationException($"Refusing to clean unexpected fixture path: {normalized}");
        }

        Directory.Delete(normalized, recursive: true);
    }
}
