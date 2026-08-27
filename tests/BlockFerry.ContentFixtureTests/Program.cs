using System.Security.Cryptography;
using System.Text;
using BlockFerry.Core.Content;
using BlockFerry.Core.Discovery;
using BlockFerry.Core.Mods;
using BlockFerry.Core.Options;
using BlockFerry.Core.Pcl2;
using BlockFerry.Core.System;
using BlockFerry.TestSupport;

var requestedCase = ReadCase(args);
if (string.Equals(requestedCase, "access", StringComparison.Ordinal))
{
    AccessAuditLedger.Reset();
    ForgedPairCannotCreateAccessLease();
    ContextCannotBePubliclyConstructedOrCloned();
    CrossSessionAndGenerationReplayRejected();
    LeaseAndContextBindingCannotBeSubstituted();
    ReopenedLeaseSameSessionKeepsOpaqueIdsStable();
    DifferentSessionSameNumericGenerationRotatesOpaqueIds();
    NewGenerationRotatesOpaqueIds();
    DisposedSessionInvalidatesEveryLeaseAndContext();
    RevalidatesImmediatelyBeforeOpening();
    RootIdentityDriftRejected();
    RetainedHandlesBackEveryRead();
    ReadsAreBoundToRequestedRelativePath();
    MissingAncestorReadsAsMissing();
    VerifyRejectsMissingExtraOrRelabeledRereads();
    SharedBudgetFailsBeforeUnboundedMaterialization();
    PartialOpenFailureDisposesEveryHandle();
    DisposedLeaseRejectsAllUse();
    CapabilityAuditContainsNoMutationOrOutsideRootAccess();
    Assert(AccessAuditLedger.EventCount > 0 &&
           AccessAuditLedger.WriteCount == 0 &&
           AccessAuditLedger.OutsideRootCount == 0,
        "AccessAuditLedger");
    if (args.Contains("--show-audit", StringComparer.Ordinal))
    {
        Console.WriteLine(
            $"AUDIT: fixture-roots={AccessAuditLedger.RootCount}; " +
            $"events={AccessAuditLedger.EventCount}; " +
            $"writes={AccessAuditLedger.WriteCount}; " +
            $"outside-root={AccessAuditLedger.OutsideRootCount}");
    }

    Console.WriteLine("PASS: access");
    return;
}

if (string.Equals(requestedCase, "mods", StringComparison.Ordinal))
{
    ReadsOnlyAllowlistedDeclarations();
    RawJarEnumerationIsBoundedBeforeFilter();
    ArchiveAndCentralDirectoryLimitsFailClosed();
    ManifestIsReadOnlyForExactFileJarVersionSubstitution();
    NeoForgeTomlUnknownSyntaxFailsClosed();
    DuplicateDeclarationPropertiesFailClosed();
    CompatibilityContextUsesSameLiveLease();
    CompatibilityEvidenceBaselineIsLocked();
    JeiCompatibleMajorLineIsAccepted();
    EsmCompatibleMajorLineIsAccepted();
    OptionalUiModFamiliesAreVersionGated();
    DarkModeEverywhereRejectsWrongMinecraftPrefixes();
    EmiIsDetectedButAlwaysUnsupported();
    UnknownTargetFormatFamilyIsUnsupported();
    DeclarationCaseAndTraversalAliasesFailClosed();
    OfficialNeoForgeDependencyTablesAreAccepted();
    DuplicateModIdsAreUnsupported();
    MalformedAndZip64ArchivesFailClosed();
    AllSixModProbeLimitsAreEnforcedBeforeRead();
    LargeUnrelatedArchiveWithinBoundPreservesRequiredEvidence();
    RequestWideByteLimitInvalidatesPartialEvidence();
    ModProbeCancellationIsPropagated();
    EncryptedAndCompressedBombDeclarationsFailClosed();
    OperationalZipAndEntryLimitsFailClosed();
    MissingRequiredModsAreCompatibilityUnsupported();
    Console.WriteLine("PASS: mods");
    return;
}

if (string.Equals(requestedCase, "vanilla", StringComparison.Ordinal))
{
    AdapterFullSelectionMatchesPlanSelected();
    VanillaSeedsSchemaVersionBeforeFirstLaunch();
    VanillaRejectsInvalidSchemaVersions();
    AdapterRejectsChangedSnapshotAsStale();
    AdapterAcceptsOnlyCatalogBoundValidatedSelection();
    VanillaProtectsFixedAndCallerKeysAndPreservesRawTarget();
    DisposedVanillaContextIsStale();
    VanillaOptionsFourMiBBoundaryIsExact();
    VanillaSemanticNoOpStagesZeroMutations();
    VanillaStageAndVerifyRequireExactReread();
    VanillaGuiScaleCarriesFancyMenuFirstLaunchMarker();
    VanillaHalfInitializedNeoForgeTargetCarriesFancyMenuMarker();
    VanillaGuiScaleSkipsExistingFancyMenuMarker();
    VanillaGuiScaleRejectsUnverifiableFancyMenuMarker();
    UnsafeVanillaKeysFailClosedBeforeCatalogExposure();
    Console.WriteLine("PASS: vanilla");
    return;
}

if (string.Equals(requestedCase, "appearance", StringComparison.Ordinal))
{
    AccessAuditLedger.Reset();
    AppearanceSeedsValidatedConfigBeforeFirstLaunch();
    AppearanceMapsSelectedShaderByIdentityAndPreservesTargetBytes();
    AppearanceMapsDisabledModeAndRejectsAmbiguousShaders();
    AppearanceSemanticNoOpStagesZeroMutations();
    AppearanceRejectsMalformedSchemaAndIncompatibleVersions();
    AppearanceRejectsChangedSnapshotsAsStale();
    Assert(AccessAuditLedger.EventCount > 0 &&
           AccessAuditLedger.WriteCount == 0 &&
           AccessAuditLedger.OutsideRootCount == 0,
        "AppearanceAccessAudit");
    Console.WriteLine("PASS: appearance");
    return;
}

if (string.Equals(requestedCase, "jei", StringComparison.Ordinal))
{
    AccessAuditLedger.Reset();
    SameJeiScopeBuildsOpaqueCatalogItem();
    JeiSelectionIsCatalogBoundAndLocalWithoutEvidenceIsDisabled();
    MissingServerScopeSeedsDeterministicTargetBeforeFirstLaunch();
    LanServerStatusNamesTargetScopeBeforeFirstLaunch();
    LanServerStatusCompletesLegacyDeferredSeed();
    DeferredServerScopeBecomesReadyAfterTargetRuntimeScopeAppears();
    DeferredServerScopeRecognizesCompletedAndConflictingTargets();
    RenamedServerScopeMapsToUniqueTargetRuntimeScope();
    AmbiguousServerScopesFailClosed();
    ExactAndUnrelatedServerScopesFailClosed();
    ExactAndMultipleUnrelatedServerScopesFailClosed();
    SurplusTargetServerScopesInvalidateGlobalMapping();
    ExactAndRenamedServerScopesShareOneGlobalMapping();
    PriorSourceNamedCopyMapsToUniqueEmptyTargetScope();
    DifferentJeiTargetDefaultsToKeepThenAllowsUseSource();
    LegacyJeiIniIsDetectedButNeverCopied();
    JeiRequiresCompatibleMinecraftAndModVersions();
    JeiRejectsUnknownHeaderAndSchemaShapes();
    JeiRejectsDuplicateMalformedAndOversizedJson();
    JeiJsonRoadmapLimitsFailClosed();
    JeiJsonEqualityRulesDriveWholeFileConflicts();
    JeiSemanticNoOpStagesZeroMutations();
    JeiOpaqueIdsRespectSessionAndGenerationLifetime();
    JeiStageAndVerifyRequireExactScopePath();
    JeiPlanRejectsChangedSnapshotsAsStale();
    JeiPlanRejectsAddedTargetScopeAsStale();
    JeiPlanRejectsAddedSourceScopeAsStale();
    JeiCatalogOrderingAndPrivacyAreDeterministic();
    JeiExclusionPathsAreNeverCataloged();
    Assert(AccessAuditLedger.EventCount > 0 &&
           AccessAuditLedger.WriteCount == 0 &&
           AccessAuditLedger.OutsideRootCount == 0,
        "JeiAccessAudit");
    if (args.Contains("--show-audit", StringComparer.Ordinal))
    {
        Console.WriteLine(
            $"AUDIT: fixture-roots={AccessAuditLedger.RootCount}; " +
            $"events={AccessAuditLedger.EventCount}; " +
            $"writes={AccessAuditLedger.WriteCount}; " +
            $"outside-root={AccessAuditLedger.OutsideRootCount}");
    }

    Console.WriteLine("PASS: jei");
    return;
}

if (string.Equals(requestedCase, "esm", StringComparison.Ordinal))
{
    AccessAuditLedger.Reset();
    ValidResourceLocationsFollowMinecraft1211Equivalence();
    EsmCatalogBindingAndMissingTargetDefaultAreSafe();
    EsmCanonicalEquivalenceAndAliasCollisionsAreLocked();
    EsmRejectsInvalidResourceLocations();
    EsmRequiresCompatibleMinecraftAndModVersions();
    EsmFourMiBBoundaryIsExact();
    EsmNumericDomainAndEqualityAreLocked();
    EsmUnionPreservesTargetAndUsesExplicitSource();
    EsmConflictThreeChoicesAreSafe();
    EsmSemanticNoOpStagesZeroMutations();
    EsmStageVerifyAndSecondRunAreDeterministic();
    EsmRejectsStaleSnapshotsAndMalformedJson();
    EsmHardExclusionsAreNeverReadOrPlanned();
    Assert(AccessAuditLedger.EventCount > 0 &&
           AccessAuditLedger.WriteCount == 0 &&
           AccessAuditLedger.OutsideRootCount == 0,
        "EsmAccessAudit");
    if (args.Contains("--show-audit", StringComparer.Ordinal))
    {
        Console.WriteLine(
            $"AUDIT: fixture-roots={AccessAuditLedger.RootCount}; " +
            $"events={AccessAuditLedger.EventCount}; " +
            $"writes={AccessAuditLedger.WriteCount}; " +
            $"outside-root={AccessAuditLedger.OutsideRootCount}");
    }

    Console.WriteLine("PASS: esm");
    return;
}

if (!string.Equals(requestedCase, "contracts", StringComparison.Ordinal))
{
    throw new InvalidOperationException($"Unknown fixture case: {requestedCase}");
}

ImmutableByteBufferCopiesInputAndEveryReturnedCopy();
ContentCollectionsBoundBeforeCopyAndDetachCallerInputs();
ContentDiagnosticHasNoFreeFormOrPathSurface();
ContentRelativePathRejectsAbsoluteTraversalAdsAndReservedNames();
UnknownSelectionIsRejectedWithCatalogPresent();
DefaultSelectionNeverUsesSource();
ValidatedSelectionIsBoundToExactCatalog();
NonActionablePlanCannotCarryFileChanges();
DuplicateFinalPathIgnoringCaseIsRejected();
UnicodeEquivalentFinalPathIsRejected();
PathBoundSnapshotCannotBeRelabeled();
AdapterPlanRejectsDetachedEquivalentItems();
StrictJsonRejectsDuplicateProperties();
StrictJsonEqualityRulesAreLocked();
AdapterPlansAggregateInStableOrdinalIdOrder();
PureContractsContainNoSystemPclOrDiscoveryTypes();
VerificationRereadsAreExactlyPathBound();
RecoveryCatalogsUseOnlyCurrentTargetProof();

Console.WriteLine("PASS: contracts");

static void RecoveryCatalogsUseOnlyCurrentTargetProof()
{
    using (var fixture = AccessFixture.Create())
    {
        AddFabricMod(fixture, false, "jei", "19.44.0.401");
        AddFabricMod(fixture, false, "extremesoundmuffler", "3.56");
        AddFabricMod(fixture, false, "fancymenu", "3.9.9");
        AddFabricMod(fixture, false, "darkmodeeverywhere", "1.21.1-1.4.0");
        using var session = fixture.SessionFactory.Create(501, fixture.Discovery);
        fixture.MoveSourceRoot();
        using var context = new RecoveryCatalogContextFactory(
                fixture.AuditedCapability,
                new ModPresenceProbe())
            .Open(session, fixture.Target.Id, CancellationToken.None);
        Assert(context is not null,
            nameof(RecoveryCatalogsUseOnlyCurrentTargetProof));

        var options = MustContentPath("options.txt");
        var fancyMenuMarker = MustContentPath(@"fancymenu_data\default_scale_set.fm");
        var appearance = MustContentPath(@"config\darkmodeeverywhereshaders.json");
        var esmData = MustContentPath(@"ESM\soundsMuffled.dat");
        var jeiLocal = MustContentPath(@"config\jei\world\local\opaque-local\bookmarks.json");
        var jeiServer = MustContentPath(@"config\jei\world\server\opaque-server\bookmarks.json");
        var jeiWrongFile = MustContentPath(@"config\jei\world\server\opaque-server\bookmarks.ini");
        var jeiAlias = MustContentPath(@"config\jei\world\server\OPAQUE-SERVER\bookmarks.json");

        var vanilla = new VanillaOptionsAdapter(
            new Pcl2OptionsMigrationPreviewer(fixture.AuditedCapability));
        var vanillaCatalog = vanilla.RegenerateRecoveryAllowedPaths(
            context!,
            new HashSet<ContentRelativePath> { options, fancyMenuMarker, esmData },
            CancellationToken.None);
        var appearanceCatalog = new DarkModeEverywhereAdapter().RegenerateRecoveryAllowedPaths(
            context!,
            new HashSet<ContentRelativePath> { appearance, options },
            CancellationToken.None);
        var esmCatalog = new ExtremeSoundMufflerAdapter().RegenerateRecoveryAllowedPaths(
            context!,
            new HashSet<ContentRelativePath> { esmData, options },
            CancellationToken.None);
        var jeiCandidates = new HashSet<ContentRelativePath>
        {
            jeiLocal,
            jeiServer,
            jeiWrongFile,
            jeiAlias,
        };
        var jeiCatalog = new JeiBookmarksAdapter().RegenerateRecoveryAllowedPaths(
            context!,
            jeiCandidates,
            CancellationToken.None);

        Assert(vanillaCatalog.SetEquals([options, fancyMenuMarker]) &&
               appearanceCatalog.SetEquals([appearance]) &&
               esmCatalog.SetEquals([esmData]) &&
               jeiCatalog.Contains(jeiLocal) &&
               jeiCatalog.Contains(jeiServer) &&
               !jeiCatalog.Contains(jeiWrongFile) &&
               jeiCatalog.Count < jeiCandidates.Count,
            "Recovery catalogs must remain target-only after the source disappears, expose only exact adapter paths, and reject a JEI semantic scope alias instead of authorizing every stored candidate.");
    }

    using (var incompatible = AccessFixture.Create(targetMinecraftVersion: "1.20.1"))
    {
        AddFabricMod(incompatible, false, "jei", "19.44.0.401");
        AddFabricMod(incompatible, false, "extremesoundmuffler", "3.56");
        using var session = incompatible.SessionFactory.Create(502, incompatible.Discovery);
        incompatible.MoveSourceRoot();
        using var context = new RecoveryCatalogContextFactory(
                incompatible.AuditedCapability,
                new ModPresenceProbe())
            .Open(session, incompatible.Target.Id, CancellationToken.None);
        Assert(context is not null,
            nameof(RecoveryCatalogsUseOnlyCurrentTargetProof));
        var jeiPath = MustContentPath(@"config\jei\world\local\opaque-local\bookmarks.json");
        var esmPath = MustContentPath(@"ESM\soundsMuffled.dat");

        Assert(new JeiBookmarksAdapter().RegenerateRecoveryAllowedPaths(
                   context!,
                   new HashSet<ContentRelativePath> { jeiPath },
                   CancellationToken.None).Count == 0 &&
               new ExtremeSoundMufflerAdapter().RegenerateRecoveryAllowedPaths(
                   context!,
                   new HashSet<ContentRelativePath> { esmPath },
                   CancellationToken.None).Count == 0,
            "JEI and ESM recovery catalogs must reject current target Minecraft compatibility drift even when stored paths and mod versions look valid.");
    }
}

static void ForgedPairCannotCreateAccessLease()
{
    var factoryType = typeof(CapabilityBoundInstanceAccessFactory);
    var authorityMethods = factoryType
        .GetMethods(System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic)
        .Where(method => method.Name == "Open")
        .ToArray();
    Assert(authorityMethods.Length == 1,
        nameof(ForgedPairCannotCreateAccessLease));
    var parameters = authorityMethods[0].GetParameters();
    Assert(parameters.Length == 5 &&
           parameters[0].ParameterType == typeof(DiscoverySession) &&
           parameters[1].ParameterType == typeof(string) &&
           parameters[2].ParameterType == typeof(string) &&
           parameters.All(parameter =>
               parameter.ParameterType != typeof(DiscoveredInstancePair) &&
               parameter.ParameterType != typeof(DiscoveredInstanceChoice)),
        nameof(ForgedPairCannotCreateAccessLease));
}

static void ReadsOnlyAllowlistedDeclarations()
{
    using var fixture = AccessFixture.Create();
    fixture.AddModJar(
        source: true,
        "jei-source.jar",
        ("fabric.mod.json", "{\"id\":\"jei\",\"version\":\"19.44.0.401\"}"u8.ToArray()),
        ("assets/jei/private.bin", [0x41, 0x42, 0x43]));
    fixture.AddModJar(
        source: false,
        "jei-target.jar",
        ("META-INF/neoforge.mods.toml",
            "modLoader=\"javafml\"\nloaderVersion=\"[4,)\"\nlicense=\"MIT\"\n[[mods]]\nmodId=\"jei\"\nversion=\"${file.jarVersion}\"\n"u8.ToArray()),
        ("META-INF/MANIFEST.MF",
            "Manifest-Version: 1.0\r\nImplementation-Version: 19.44.0.401\r\n\r\n"u8.ToArray()),
        ("assets/jei/private.bin", [0x44, 0x45, 0x46]));

    var observing = new ZipAllowlistAuditCapability(fixture.AuditedCapability);
    var accessFactory = new CapabilityBoundInstanceAccessFactory(
        fixture.SessionFactory,
        observing);
    using var session = fixture.SessionFactory.Create(201, fixture.Discovery);
    var opened = accessFactory.Open(
        session,
        fixture.Source.Id,
        fixture.Target.Id,
        ContentAccessLimits.Beta3);
    Assert(opened.IsValid && opened.Lease is not null,
        nameof(ReadsOnlyAllowlistedDeclarations));
    using var lease = opened.Lease!;
    var probe = new ModPresenceProbe();
    var required = new HashSet<string>(["jei"], StringComparer.Ordinal);
    var source = probe.Probe(lease.Source, required, Beta3ModLimits());
    var target = probe.Probe(lease.Target, required, Beta3ModLimits());

    Assert(source.Evidence.Count == 1 &&
           source.Evidence[0].ModId == "jei" &&
           source.Evidence[0].Version == "19.44.0.401" &&
           target.Evidence.Count == 1 &&
           target.Evidence[0].ModId == "jei" &&
           target.Evidence[0].Version == "19.44.0.401" &&
           observing.ZipRequests.Count >= 4 &&
           observing.ZipRequests.All(request =>
               request.AllowedEntryNames.SetEquals(ModFixtureConstants.DeclarationNames) ||
               request.AllowedEntryNames.SetEquals(ModFixtureConstants.ManifestOnlyName)) &&
           observing.ZipRequests.Count(request =>
               request.AllowedEntryNames.SetEquals(ModFixtureConstants.ManifestOnlyName)) == 1 &&
           observing.ZipRequests.All(request =>
               !request.AllowedEntryNames.Contains("assets/jei/private.bin")),
        nameof(ReadsOnlyAllowlistedDeclarations));
}

static void AdapterFullSelectionMatchesPlanSelected()
{
    using var fixture = AccessFixture.Create();
    using var session = fixture.SessionFactory.Create(301, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateCompatibility(fixture));
    var adapter = new VanillaOptionsAdapter(
        new Pcl2OptionsMigrationPreviewer(fixture.AuditedCapability));
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var requested = ContentSelection.Create(
        catalog.Items.Where(item => item.IsSelectable).Select(item => item.Id),
        []);
    Assert(ContentSelectionValidator.TryValidateExplicit(
            catalog,
            requested,
            out var selection,
            out _),
        nameof(AdapterFullSelectionMatchesPlanSelected));
    var plan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    var staged = adapter.Stage(plan, CancellationToken.None);
    var expected = new OptionsMergePlanner().PlanSelected(
        "version:3955\nlang:en_us\nkey_key.jump:key.keyboard.space\n",
        "version:3955\nlang:zh_cn\nkey_key.jump:key.keyboard.j\n",
        new HashSet<string>(["lang", "key_key.jump"], StringComparer.Ordinal));

    Assert(plan.FileChanges.Count == 1 &&
           staged.Mutations.Count == 1 &&
           Encoding.UTF8.GetString(staged.Mutations[0].AfterBytes.CopyBytes()) == expected.Content &&
           plan.Items.Count(item =>
               item.Disposition is PlannedContentDisposition.Add or
                   PlannedContentDisposition.Update) == 2,
        nameof(AdapterFullSelectionMatchesPlanSelected));
}

static void SameJeiScopeBuildsOpaqueCatalogItem()
{
    using var fixture = AccessFixture.Create();
    fixture.SetJeiBookmarks(true, "local", "private-world", "[{\"version\":2},{\"type\":\"item\"}]"u8.ToArray());
    fixture.SetJeiBookmarks(false, "local", "private-world", "[{\"version\":2},{\"type\":\"item\"}]"u8.ToArray());
    using var session = fixture.SessionFactory.Create(401, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
    var catalog = new JeiBookmarksAdapter().BuildCatalog(context, CancellationToken.None);

    Assert(catalog.Items.Count == 1 &&
           catalog.Items[0].DisplayName == "单人收藏 1" &&
           catalog.Items[0].Id.TechnicalKey.Length == 43 &&
           !catalog.Items[0].Id.TechnicalKey.Contains("private-world", StringComparison.Ordinal) &&
           catalog.Items[0].Disposition == PlannedContentDisposition.Same,
        nameof(SameJeiScopeBuildsOpaqueCatalogItem));
}

static void ValidResourceLocationsFollowMinecraft1211Equivalence()
{
    Assert(ResourceLocationValidator.TryParse1211("sound", out var implicitNamespace) &&
           ResourceLocationValidator.TryParse1211("minecraft:sound", out var explicitNamespace) &&
           ResourceLocationValidator.TryParse1211(":sound", out var emptyNamespace) &&
           implicitNamespace.CanonicalValue == "minecraft:sound" &&
           implicitNamespace.CanonicalValue == explicitNamespace.CanonicalValue &&
           implicitNamespace.CanonicalValue == emptyNamespace.CanonicalValue &&
           ResourceLocationValidator.TryParse1211(
               "mod.id-1:path/to.sound_name-2",
               out var namespaced) &&
           namespaced.RawValue == "mod.id-1:path/to.sound_name-2" &&
           namespaced.CanonicalValue == "mod.id-1:path/to.sound_name-2",
        nameof(ValidResourceLocationsFollowMinecraft1211Equivalence));
}

static void EsmCatalogBindingAndMissingTargetDefaultAreSafe()
{
    var sourceBytes = "{\"sound\":0.5}"u8.ToArray();
    using var fixture = AccessFixture.Create();
    fixture.SetEsmMutes(true, sourceBytes);
    using var session = fixture.SessionFactory.Create(501, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateEsmCompatibility(fixture));
    var adapter = new ExtremeSoundMufflerAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var item = catalog.Items.Single();
    ValidatedContentSelection? defaults = null;
    Assert(item.DisplayName == "minecraft:sound" &&
           item.Disposition == PlannedContentDisposition.Add &&
           item.IsSelectable &&
           !item.IsSelectedByDefault &&
           ContentSelectionValidator.TryCreateDefaults(catalog, out defaults, out _),
        nameof(EsmCatalogBindingAndMissingTargetDefaultAreSafe));
    var defaultPlan = adapter.Plan(context, catalog, defaults!, CancellationToken.None);
    Assert(defaultPlan.FileChanges.Count == 0 &&
           adapter.Stage(defaultPlan, CancellationToken.None).Mutations.Count == 0,
        nameof(EsmCatalogBindingAndMissingTargetDefaultAreSafe));

    var foreign = ContentCatalog.Create("esm", [], []);
    Assert(ContentSelectionValidator.TryCreateDefaults(foreign, out var foreignSelection, out _),
        nameof(EsmCatalogBindingAndMissingTargetDefaultAreSafe));
    var rejected = adapter.Plan(context, catalog, foreignSelection!, CancellationToken.None);
    Assert(rejected.Diagnostics.Single().Code == ContentDiagnosticCode.CapabilityRejected,
        nameof(EsmCatalogBindingAndMissingTargetDefaultAreSafe));

    var request = ContentSelection.Create([item.Id], []);
    Assert(ContentSelectionValidator.TryValidateExplicit(catalog, request, out var selected, out _),
        nameof(EsmCatalogBindingAndMissingTargetDefaultAreSafe));
    var plan = adapter.Plan(context, catalog, selected!, CancellationToken.None);
    var staged = adapter.Stage(plan, CancellationToken.None);
    Assert(plan.FileChanges.Count == 1 &&
           staged.Mutations.Count == 1 &&
           Encoding.UTF8.GetString(staged.Mutations[0].AfterBytes.CopyBytes()) == "{\"sound\":0.5}" &&
           adapter.RegenerateAllowedPaths(context, CancellationToken.None)
               .Contains(plan.FileChanges[0].RelativePath),
        nameof(EsmCatalogBindingAndMissingTargetDefaultAreSafe));
}

static void EsmCanonicalEquivalenceAndAliasCollisionsAreLocked()
{
    using (var fixture = AccessFixture.Create())
    {
        fixture.SetEsmMutes(true, "{\"sound\":0.5}"u8.ToArray());
        fixture.SetEsmMutes(false, "{\":sound\":0.5}"u8.ToArray());
        using var session = fixture.SessionFactory.Create(502, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var catalog = new ExtremeSoundMufflerAdapter().BuildCatalog(
            lease.CreateProbeContext(CreateEsmCompatibility(fixture)),
            CancellationToken.None);
        Assert(catalog.Items.Single().Disposition == PlannedContentDisposition.Same &&
               catalog.Items.Single().DisplayName == "minecraft:sound",
            nameof(EsmCanonicalEquivalenceAndAliasCollisionsAreLocked));
    }

    using (var fixture = AccessFixture.Create())
    {
        fixture.SetEsmMutes(
            true,
            "{\"sound\":0.5,\"minecraft:sound\":0.5}"u8.ToArray());
        using var session = fixture.SessionFactory.Create(503, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var catalog = new ExtremeSoundMufflerAdapter().BuildCatalog(
            lease.CreateProbeContext(CreateEsmCompatibility(fixture)),
            CancellationToken.None);
        Assert(catalog.Items.Count == 0 &&
               catalog.Diagnostics.Single().Code == ContentDiagnosticCode.SemanticAliasCollision,
            nameof(EsmCanonicalEquivalenceAndAliasCollisionsAreLocked));
    }

    using (var fixture = AccessFixture.Create())
    {
        fixture.SetEsmMutes(true, "{\"sound\":0.5,\"sound\":0.4}"u8.ToArray());
        using var session = fixture.SessionFactory.Create(504, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var catalog = new ExtremeSoundMufflerAdapter().BuildCatalog(
            lease.CreateProbeContext(CreateEsmCompatibility(fixture)),
            CancellationToken.None);
        Assert(catalog.Items.Count == 0 &&
               catalog.Diagnostics.Single().Code == ContentDiagnosticCode.DuplicateJsonProperty,
            nameof(EsmCanonicalEquivalenceAndAliasCollisionsAreLocked));
    }
}

static void EsmRejectsInvalidResourceLocations()
{
    var invalid = new[]
    {
        "UPPER:sound",
        "minecraft:Bad",
        "minecraft:with space",
        "bad/ns:path",
        "minecraft:bad?path",
        "a:b:c",
        "minecraft:",
        ":",
        string.Empty,
        "minecraft:bad\u0001path",
    };
    for (var index = 0; index < invalid.Length; index++)
    {
        Assert(!ResourceLocationValidator.TryParse1211(invalid[index], out _),
            nameof(EsmRejectsInvalidResourceLocations));
        using var fixture = AccessFixture.Create();
        var key = global::System.Text.Json.JsonSerializer.Serialize(invalid[index]);
        fixture.SetEsmMutes(true, Encoding.UTF8.GetBytes("{" + key + ":0.5}"));
        using var session = fixture.SessionFactory.Create(505 + index, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var catalog = new ExtremeSoundMufflerAdapter().BuildCatalog(
            lease.CreateProbeContext(CreateEsmCompatibility(fixture)),
            CancellationToken.None);
        Assert(catalog.Items.Count == 0 &&
               catalog.Diagnostics.Single().Code == ContentDiagnosticCode.UnsupportedSchema,
            nameof(EsmRejectsInvalidResourceLocations));
    }
}

static void EsmRequiresCompatibleMinecraftAndModVersions()
{
    foreach (var (sourceVersion, targetVersion) in new[]
             {
                 ("3.55", "3.56"),
                 ("3.56", "3.99"),
             })
    {
        using var fixture = AccessFixture.Create();
        fixture.SetEsmMutes(true, "{\"minecraft:test\":0.5}"u8.ToArray());
        using var session = fixture.SessionFactory.Create(
            510 + int.Parse(sourceVersion.Split('.')[1], global::System.Globalization.CultureInfo.InvariantCulture),
            fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var context = lease.CreateProbeContext(CreateEsmCompatibility(
            fixture,
            sourceVersion,
            targetVersion));
        var adapter = new ExtremeSoundMufflerAdapter();
        var catalog = adapter.BuildCatalog(context, CancellationToken.None);
        Assert(catalog.Items.Count == 1 &&
               catalog.Diagnostics.All(item => item.Code != ContentDiagnosticCode.UnsupportedModVersion) &&
               adapter.Probe(context, CancellationToken.None).IsSupported &&
               adapter.RegenerateAllowedPaths(context, CancellationToken.None).Count == 1,
            nameof(EsmRequiresCompatibleMinecraftAndModVersions));
    }

    var rejectedCases = new[]
    {
        (SourceMinecraft: "1.21", TargetMinecraft: "1.21.1", SourceMod: "3.56", TargetMod: "3.56", Expected: ContentDiagnosticCode.UnsupportedMinecraftVersion),
        (SourceMinecraft: "1.21.1", TargetMinecraft: "1.21.2", SourceMod: "3.56", TargetMod: "3.56", Expected: ContentDiagnosticCode.UnsupportedMinecraftVersion),
        (SourceMinecraft: "1.21.1", TargetMinecraft: "1.21.1", SourceMod: "2.99", TargetMod: "3.56", Expected: ContentDiagnosticCode.UnsupportedModVersion),
        (SourceMinecraft: "1.21.1", TargetMinecraft: "1.21.1", SourceMod: "3.56", TargetMod: "4.0", Expected: ContentDiagnosticCode.UnsupportedModVersion),
        (SourceMinecraft: "1.21.1", TargetMinecraft: "1.21.1", SourceMod: "unknown", TargetMod: "unknown", Expected: ContentDiagnosticCode.UnsupportedModVersion),
    };
    for (var index = 0; index < rejectedCases.Length; index++)
    {
        var candidate = rejectedCases[index];
        using var fixture = AccessFixture.Create(candidate.SourceMinecraft, candidate.TargetMinecraft);
        fixture.SetEsmMutes(true, "{}"u8.ToArray());
        using var session = fixture.SessionFactory.Create(515 + index, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var context = lease.CreateProbeContext(CreateEsmCompatibility(
            fixture,
            candidate.SourceMod,
            candidate.TargetMod));
        var adapter = new ExtremeSoundMufflerAdapter();
        var catalog = adapter.BuildCatalog(context, CancellationToken.None);
        Assert(catalog.Items.Count == 0 &&
               catalog.Diagnostics.Single().Code == candidate.Expected &&
               !adapter.Probe(context, CancellationToken.None).IsSupported &&
               adapter.RegenerateAllowedPaths(context, CancellationToken.None).Count == 0,
            nameof(EsmRequiresCompatibleMinecraftAndModVersions));
    }

    using var missingFixture = AccessFixture.Create();
    missingFixture.SetEsmMutes(true, "{}"u8.ToArray());
    using var missingSession = missingFixture.SessionFactory.Create(520, missingFixture.Discovery);
    using var missingLease = OpenLease(missingFixture, missingSession);
    var missingContext = missingLease.CreateProbeContext(CreateEsmCompatibility(
        missingFixture,
        sourceEsmVersion: null,
        targetEsmVersion: "3.56"));
    var missingCatalog = new ExtremeSoundMufflerAdapter().BuildCatalog(
        missingContext,
        CancellationToken.None);
    Assert(missingCatalog.Diagnostics.Single().Code == ContentDiagnosticCode.UnsupportedModVersion,
        nameof(EsmRequiresCompatibleMinecraftAndModVersions));
}

static void EsmFourMiBBoundaryIsExact()
{
    var exact = new byte[EsmMuteDocument.MaximumFileBytes];
    exact.AsSpan().Fill((byte)' ');
    "{}"u8.CopyTo(exact);
    using (var fixture = AccessFixture.Create())
    {
        fixture.SetEsmMutes(true, exact);
        using var session = fixture.SessionFactory.Create(521, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var probe = new ExtremeSoundMufflerAdapter().Probe(
            lease.CreateProbeContext(CreateEsmCompatibility(fixture)),
            CancellationToken.None);
        Assert(probe.IsSupported,
            nameof(EsmFourMiBBoundaryIsExact));
    }

    var oversized = new byte[EsmMuteDocument.MaximumFileBytes + 1];
    oversized.AsSpan().Fill((byte)' ');
    "{}"u8.CopyTo(oversized);
    using (var fixture = AccessFixture.Create())
    {
        fixture.SetEsmMutes(true, oversized);
        using var session = fixture.SessionFactory.Create(522, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var probe = new ExtremeSoundMufflerAdapter().Probe(
            lease.CreateProbeContext(CreateEsmCompatibility(fixture)),
            CancellationToken.None);
        Assert(!probe.IsSupported && probe.DisabledReason == ContentDiagnosticCode.LimitExceeded,
            nameof(EsmFourMiBBoundaryIsExact));
    }
}

static void EsmNumericDomainAndEqualityAreLocked()
{
    using (var fixture = AccessFixture.Create())
    {
        fixture.SetEsmMutes(true, "{\"sound\":0.1}"u8.ToArray());
        fixture.SetEsmMutes(false, "{\":sound\":1e-1}"u8.ToArray());
        using var session = fixture.SessionFactory.Create(523, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var catalog = new ExtremeSoundMufflerAdapter().BuildCatalog(
            lease.CreateProbeContext(CreateEsmCompatibility(fixture)),
            CancellationToken.None);
        Assert(catalog.Items.Single().Disposition == PlannedContentDisposition.Same,
            nameof(EsmNumericDomainAndEqualityAreLocked));
    }

    using (var fixture = AccessFixture.Create())
    {
        fixture.SetEsmMutes(true, "{\"a\":0.0,\"b\":0.9,\"c\":-0}"u8.ToArray());
        using var session = fixture.SessionFactory.Create(524, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var catalog = new ExtremeSoundMufflerAdapter().BuildCatalog(
            lease.CreateProbeContext(CreateEsmCompatibility(fixture)),
            CancellationToken.None);
        Assert(catalog.Items.Count == 3 &&
               catalog.Items.All(item => item.Disposition == PlannedContentDisposition.Add),
            nameof(EsmNumericDomainAndEqualityAreLocked));
    }

    var invalidValues = new[]
    {
        "-0.0001", "0.9000001", "\"0.5\"", "true", "null", "[]", "{}", "1e9999", "NaN", "Infinity",
    };
    for (var index = 0; index < invalidValues.Length; index++)
    {
        using var fixture = AccessFixture.Create();
        fixture.SetEsmMutes(
            true,
            Encoding.UTF8.GetBytes("{\"sound\":" + invalidValues[index] + "}"));
        using var session = fixture.SessionFactory.Create(525 + index, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var catalog = new ExtremeSoundMufflerAdapter().BuildCatalog(
            lease.CreateProbeContext(CreateEsmCompatibility(fixture)),
            CancellationToken.None);
        Assert(catalog.Items.Count == 0 &&
               catalog.Diagnostics.Count == 1 &&
               catalog.Diagnostics[0].Code is
                   ContentDiagnosticCode.UnsupportedSchema or ContentDiagnosticCode.MalformedJson,
            nameof(EsmNumericDomainAndEqualityAreLocked));
    }
}

static void EsmUnionPreservesTargetAndUsesExplicitSource()
{
    using var fixture = AccessFixture.Create();
    fixture.SetEsmMutes(
        true,
        "{\"source:only\":0.2,\"minecraft:equal\":0.3,\"shared:conflict\":0.4}"u8.ToArray());
    fixture.SetEsmMutes(
        false,
        "{\"target:only\":0.5,\"equal\":3e-1,\"shared:conflict\":0.8}"u8.ToArray());
    using var session = fixture.SessionFactory.Create(535, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateEsmCompatibility(fixture));
    var adapter = new ExtremeSoundMufflerAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var add = catalog.Items.Single(item => item.DisplayName == "source:only");
    var conflict = catalog.Items.Single(item => item.DisplayName == "shared:conflict");
    Assert(catalog.Items.Count == 4 &&
           catalog.Items.Single(item => item.DisplayName == "target:only").Disposition == PlannedContentDisposition.Same &&
           catalog.Items.Single(item => item.DisplayName == "minecraft:equal").Disposition == PlannedContentDisposition.Same,
        nameof(EsmUnionPreservesTargetAndUsesExplicitSource));
    var request = ContentSelection.Create(
        [add.Id, conflict.Id],
        [new KeyValuePair<ContentItemId, ConflictResolution>(conflict.Id, ConflictResolution.UseSource)]);
    Assert(ContentSelectionValidator.TryValidateExplicit(catalog, request, out var selection, out _),
        nameof(EsmUnionPreservesTargetAndUsesExplicitSource));
    var staged = adapter.Stage(
        adapter.Plan(context, catalog, selection!, CancellationToken.None),
        CancellationToken.None);
    var output = Encoding.UTF8.GetString(staged.Mutations.Single().AfterBytes.CopyBytes());
    Assert(output ==
               "{\"equal\":0.3,\"shared:conflict\":0.4,\"source:only\":0.2,\"target:only\":0.5}",
        nameof(EsmUnionPreservesTargetAndUsesExplicitSource));
}

static void EsmConflictThreeChoicesAreSafe()
{
    using var fixture = AccessFixture.Create();
    fixture.SetEsmMutes(true, "{\"sound\":0.2}"u8.ToArray());
    fixture.SetEsmMutes(false, "{\"sound\":0.8}"u8.ToArray());
    using var session = fixture.SessionFactory.Create(536, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateEsmCompatibility(fixture));
    var adapter = new ExtremeSoundMufflerAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var item = catalog.Items.Single();
    ValidatedContentSelection? defaults = null;
    Assert(item.DefaultResolution == ConflictResolution.KeepTarget &&
           ContentSelectionValidator.TryCreateDefaults(catalog, out defaults, out _),
        nameof(EsmConflictThreeChoicesAreSafe));
    Assert(adapter.Plan(context, catalog, defaults!, CancellationToken.None).FileChanges.Count == 0,
        nameof(EsmConflictThreeChoicesAreSafe));

    var skipRequest = ContentSelection.Create(
        [],
        [new KeyValuePair<ContentItemId, ConflictResolution>(item.Id, ConflictResolution.Skip)]);
    Assert(ContentSelectionValidator.TryValidateExplicit(catalog, skipRequest, out var skip, out _) &&
           adapter.Plan(context, catalog, skip!, CancellationToken.None).FileChanges.Count == 0,
        nameof(EsmConflictThreeChoicesAreSafe));

    var sourceRequest = ContentSelection.Create(
        [item.Id],
        [new KeyValuePair<ContentItemId, ConflictResolution>(item.Id, ConflictResolution.UseSource)]);
    Assert(ContentSelectionValidator.TryValidateExplicit(catalog, sourceRequest, out var useSource, out _) &&
           adapter.Plan(context, catalog, useSource!, CancellationToken.None).FileChanges.Count == 1,
        nameof(EsmConflictThreeChoicesAreSafe));
}

static void EsmSemanticNoOpStagesZeroMutations()
{
    using var fixture = AccessFixture.Create();
    fixture.SetEsmMutes(true, "{\"minecraft:sound\":0.1}"u8.ToArray());
    fixture.SetEsmMutes(false, "{\"sound\":1e-1}"u8.ToArray());
    var before = fixture.SnapshotInstanceTrees();
    using var session = fixture.SessionFactory.Create(537, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateEsmCompatibility(fixture));
    var adapter = new ExtremeSoundMufflerAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    Assert(ContentSelectionValidator.TryCreateDefaults(catalog, out var defaults, out _),
        nameof(EsmSemanticNoOpStagesZeroMutations));
    var plan = adapter.Plan(context, catalog, defaults!, CancellationToken.None);
    Assert(plan.FileChanges.Count == 0 &&
           adapter.Stage(plan, CancellationToken.None).Mutations.Count == 0 &&
           fixture.SnapshotInstanceTrees() == before,
        nameof(EsmSemanticNoOpStagesZeroMutations));
}

static void EsmStageVerifyAndSecondRunAreDeterministic()
{
    using var fixture = AccessFixture.Create();
    fixture.SetEsmMutes(true, "{\"z:sound\":0.4,\"a:sound\":0.2}"u8.ToArray());
    using var session = fixture.SessionFactory.Create(538, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateEsmCompatibility(fixture));
    var adapter = new ExtremeSoundMufflerAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var request = ContentSelection.Create(catalog.Items.Select(item => item.Id), []);
    Assert(ContentSelectionValidator.TryValidateExplicit(catalog, request, out var selected, out _),
        nameof(EsmStageVerifyAndSecondRunAreDeterministic));
    var staged = adapter.Stage(
        adapter.Plan(context, catalog, selected!, CancellationToken.None),
        CancellationToken.None);
    var mutation = staged.Mutations.Single();
    var bytes = mutation.AfterBytes.CopyBytes();
    Assert(Encoding.UTF8.GetString(bytes) == "{\"a:sound\":0.2,\"z:sound\":0.4}",
        nameof(EsmStageVerifyAndSecondRunAreDeterministic));
    var exact = ContentFileSnapshot.Create(
        mutation.Change.RelativePath,
        true,
        bytes,
        DateTimeOffset.UnixEpoch,
        0,
        new ContentFileIdentity(3, 2, 1));
    ContentRelativePath? otherPath = null;
    Assert(adapter.Verify(staged, [exact], CancellationToken.None).IsValid &&
           ContentRelativePath.TryCreate(@"ESM\other.dat", out otherPath, out _),
        nameof(EsmStageVerifyAndSecondRunAreDeterministic));
    var relabeled = ContentFileSnapshot.Create(
        otherPath!,
        true,
        bytes,
        DateTimeOffset.UnixEpoch,
        0,
        new ContentFileIdentity(3, 2, 1));
    Assert(!adapter.Verify(staged, [relabeled], CancellationToken.None).IsValid,
        nameof(EsmStageVerifyAndSecondRunAreDeterministic));

    fixture.SetEsmMutes(false, bytes);
    var secondAdapter = new ExtremeSoundMufflerAdapter();
    var secondCatalog = secondAdapter.BuildCatalog(context, CancellationToken.None);
    Assert(ContentSelectionValidator.TryCreateDefaults(secondCatalog, out var defaults, out _),
        nameof(EsmStageVerifyAndSecondRunAreDeterministic));
    var secondPlan = secondAdapter.Plan(context, secondCatalog, defaults!, CancellationToken.None);
    Assert(secondPlan.FileChanges.Count == 0 &&
           secondAdapter.Stage(secondPlan, CancellationToken.None).Mutations.Count == 0,
        nameof(EsmStageVerifyAndSecondRunAreDeterministic));
}

static void EsmRejectsStaleSnapshotsAndMalformedJson()
{
    using (var fixture = AccessFixture.Create())
    {
        fixture.SetEsmMutes(true, "{\"sound\":0.2}"u8.ToArray());
        fixture.SetEsmMutes(false, "{\"sound\":0.8}"u8.ToArray());
        using var session = fixture.SessionFactory.Create(539, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var context = lease.CreateProbeContext(CreateEsmCompatibility(fixture));
        var adapter = new ExtremeSoundMufflerAdapter();
        var catalog = adapter.BuildCatalog(context, CancellationToken.None);
        var item = catalog.Items.Single();
        var request = ContentSelection.Create(
            [item.Id],
            [new KeyValuePair<ContentItemId, ConflictResolution>(item.Id, ConflictResolution.UseSource)]);
        Assert(ContentSelectionValidator.TryValidateExplicit(catalog, request, out var selected, out _),
            nameof(EsmRejectsStaleSnapshotsAndMalformedJson));
        fixture.SetEsmMutes(false, "{\"sound\":0.7}"u8.ToArray());
        var stale = adapter.Plan(context, catalog, selected!, CancellationToken.None);
        Assert(stale.FileChanges.Count == 0 &&
               stale.Diagnostics.Single().Code == ContentDiagnosticCode.StaleContext,
            nameof(EsmRejectsStaleSnapshotsAndMalformedJson));
    }

    var malformedCases = new[]
    {
        "[]",
        "{",
        "{\"sound\":0.5,}",
        "{\"sound\":0.5} trailing",
    };
    for (var index = 0; index < malformedCases.Length; index++)
    {
        using var fixture = AccessFixture.Create();
        fixture.SetEsmMutes(true, Encoding.UTF8.GetBytes(malformedCases[index]));
        using var session = fixture.SessionFactory.Create(540 + index, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var catalog = new ExtremeSoundMufflerAdapter().BuildCatalog(
            lease.CreateProbeContext(CreateEsmCompatibility(fixture)),
            CancellationToken.None);
        Assert(catalog.Items.Count == 0 && catalog.Diagnostics.Count == 1,
            nameof(EsmRejectsStaleSnapshotsAndMalformedJson));
    }
}

static void EsmHardExclusionsAreNeverReadOrPlanned()
{
    using var fixture = AccessFixture.Create();
    fixture.SetEsmMutes(true, "{\"sound\":0.5}"u8.ToArray());
    fixture.SetInstanceRelativeFile(true, @"ESM\private-world\anchors.dat", "{}"u8.ToArray());
    fixture.SetInstanceRelativeFile(true, @"config\extremesoundmuffler-client.toml", "fixture"u8.ToArray());
    using var session = fixture.SessionFactory.Create(544, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateEsmCompatibility(fixture));
    var adapter = new ExtremeSoundMufflerAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var readPaths = fixture.AuditedCapability.AuditLog
        .Where(entry => string.Equals(entry.Operation, "ReadFile", StringComparison.Ordinal))
        .Select(entry => entry.RequestedPath)
        .ToArray();
    Assert(catalog.Items.Count == 1 &&
           adapter.RegenerateAllowedPaths(context, CancellationToken.None).Count == 1 &&
           readPaths.All(path =>
               !path.EndsWith("anchors.dat", StringComparison.OrdinalIgnoreCase) &&
               !path.EndsWith("extremesoundmuffler-client.toml", StringComparison.OrdinalIgnoreCase)),
        nameof(EsmHardExclusionsAreNeverReadOrPlanned));
}

static void JeiSelectionIsCatalogBoundAndLocalWithoutEvidenceIsDisabled()
{
    using var fixture = AccessFixture.Create();
    fixture.SetJeiBookmarks(true, "local", "source-only-world", "[{\"version\":2}]"u8.ToArray());
    using var session = fixture.SessionFactory.Create(402, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
    var adapter = new JeiBookmarksAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var item = catalog.Items.Single();
    var malicious = ContentSelection.Create(
        [item.Id],
        [new KeyValuePair<ContentItemId, ConflictResolution>(item.Id, ConflictResolution.UseSource)]);
    Assert(!item.IsSelectable &&
           item.Disposition == PlannedContentDisposition.Unsupported &&
           item.DisabledReason == ContentDiagnosticCode.MissingTargetData &&
           !ContentSelectionValidator.TryValidateExplicit(catalog, malicious, out _, out _),
        nameof(JeiSelectionIsCatalogBoundAndLocalWithoutEvidenceIsDisabled));

    fixture.CreateJeiScope(false, "local", "source-only-world");
    var first = adapter.BuildCatalog(context, CancellationToken.None);
    var foreign = ContentCatalog.Create("jei", [], []);
    Assert(ContentSelectionValidator.TryCreateDefaults(foreign, out var foreignSelection, out _),
        nameof(JeiSelectionIsCatalogBoundAndLocalWithoutEvidenceIsDisabled));
    var replay = adapter.Plan(context, first, foreignSelection!, CancellationToken.None);
    Assert(replay.FileChanges.Count == 0 &&
           replay.Diagnostics.Single().Code == ContentDiagnosticCode.CapabilityRejected,
        nameof(JeiSelectionIsCatalogBoundAndLocalWithoutEvidenceIsDisabled));
}

static void MissingServerScopeSeedsDeterministicTargetBeforeFirstLaunch()
{
    var sourceBytes = "[{\"version\":2},{\"type\":\"recipe\",\"value\":\"fixture\"}]"u8.ToArray();
    using var fixture = AccessFixture.Create();
    fixture.SetJeiBookmarks(true, "server", "server-scope-secret", sourceBytes);
    using var session = fixture.SessionFactory.Create(403, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
    var adapter = new JeiBookmarksAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var item = catalog.Items.Single();
    Assert(item.Disposition == PlannedContentDisposition.Add &&
           item.IsSelectable &&
           !item.IsSelectedByDefault &&
           item.DisabledReason is null &&
           item.Description == "新增服务器收藏作用域 · 默认跳过",
        nameof(MissingServerScopeSeedsDeterministicTargetBeforeFirstLaunch));
    Assert(ContentSelectionValidator.TryValidateExplicit(
            catalog,
            ContentSelection.Create([item.Id], []),
            out var selection,
            out _),
        nameof(MissingServerScopeSeedsDeterministicTargetBeforeFirstLaunch));

    var plan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    var change = plan.FileChanges.Single();
    var staged = adapter.Stage(plan, CancellationToken.None);
    var deferred = adapter.GetDeferredSeeds(plan).Single();
    Assert(!change.TargetSnapshot.Exists &&
           change.RelativePath.Value.EndsWith(
               @"server\server-scope-secret\bookmarks.json",
               StringComparison.Ordinal) &&
           deferred.SourceRelativePath.Equals(change.SourceRelativePath) &&
           deferred.ProvisionalTargetRelativePath.Equals(change.RelativePath) &&
           string.Equals(deferred.SourceSha256, change.SourceSnapshot.Sha256, StringComparison.Ordinal) &&
           staged.Mutations.Single().AfterBytes.CopyBytes().SequenceEqual(sourceBytes) &&
           adapter.RegenerateAllowedPaths(context, CancellationToken.None)
               .SetEquals([MustContentPath(
                   @"config\jei\world\server\server-scope-secret\bookmarks.json")]),
        nameof(MissingServerScopeSeedsDeterministicTargetBeforeFirstLaunch));
}

static void LanServerStatusNamesTargetScopeBeforeFirstLaunch()
{
    const string sourceScope = "2026-08-03 Example Pack 7_3 (LAN connection)";
    const string targetScope = "Example Pack 8_0 r23 (LAN connection)";
    var sourceBytes = "[{\"version\":2},{\"type\":\"recipe\",\"value\":\"fixture\"}]"u8.ToArray();
    using var fixture = AccessFixture.Create();
    fixture.SetJeiBookmarks(true, "server", sourceScope, sourceBytes);
    fixture.SetInstanceRelativeFile(
        true,
        @"logs\latest.log",
        "[Render thread/INFO]: Connecting to 2001:db8::1, 25565\r\n"u8.ToArray());
    using var session = fixture.SessionFactory.Create(427, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
    var status = new RecordingMinecraftServerStatusClient("Example Pack 8.0 r23");
    var adapter = new JeiBookmarksAdapter(new JeiLanServerScopeHintProvider(status));
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var item = catalog.Items.Single();
    Assert(ContentSelectionValidator.TryValidateExplicit(
            catalog,
            ContentSelection.Create([item.Id], []),
            out var selection,
            out _),
        nameof(LanServerStatusNamesTargetScopeBeforeFirstLaunch));

    var plan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    var expected = MustContentPath($@"config\jei\world\server\{targetScope}\bookmarks.json");
    Assert(plan.FileChanges.Single().RelativePath.Equals(expected) &&
           adapter.GetDeferredSeeds(plan).Count == 0 &&
           adapter.RegenerateAllowedPaths(context, CancellationToken.None).SetEquals([expected]) &&
           status.CallCount == 1 &&
           status.LastAddress?.Equals(System.Net.IPAddress.Parse("2001:db8::1")) == true &&
           status.LastPort == 25565,
        nameof(LanServerStatusNamesTargetScopeBeforeFirstLaunch));
}

static void LanServerStatusCompletesLegacyDeferredSeed()
{
    const string sourceScope = "old server name (LAN connection)";
    var sourceBytes = "[{\"version\":2},{\"type\":\"recipe\",\"value\":\"fixture\"}]"u8.ToArray();
    using var fixture = AccessFixture.Create();
    fixture.SetJeiBookmarks(true, "server", sourceScope, sourceBytes);
    fixture.SetInstanceRelativeFile(
        true,
        @"logs\latest.log",
        "[Render thread/INFO]: Connecting to 192.0.2.10, 25566\n"u8.ToArray());
    using var session = fixture.SessionFactory.Create(428, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));

    var legacyAdapter = new JeiBookmarksAdapter();
    var legacyCatalog = legacyAdapter.BuildCatalog(context, CancellationToken.None);
    var legacyItem = legacyCatalog.Items.Single();
    Assert(ContentSelectionValidator.TryValidateExplicit(
            legacyCatalog,
            ContentSelection.Create([legacyItem.Id], []),
            out var legacySelection,
            out _),
        nameof(LanServerStatusCompletesLegacyDeferredSeed));
    var seed = legacyAdapter.GetDeferredSeeds(legacyAdapter.Plan(
        context,
        legacyCatalog,
        legacySelection!,
        CancellationToken.None)).Single();

    var status = new RecordingMinecraftServerStatusClient("new.server name");
    var adapter = new JeiBookmarksAdapter(new JeiLanServerScopeHintProvider(status));
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var resolution = adapter.ResolveDeferred(catalog, seed);
    Assert(resolution.Kind == DeferredJeiResolutionKind.Ready &&
           resolution.ItemId == catalog.Items.Single().Id,
        nameof(LanServerStatusCompletesLegacyDeferredSeed));
}

static void DeferredServerScopeBecomesReadyAfterTargetRuntimeScopeAppears()
{
    var sourceBytes = "[{\"version\":2},{\"type\":\"recipe\",\"value\":\"fixture\"}]"u8.ToArray();
    using var fixture = AccessFixture.Create();
    fixture.SetJeiBookmarks(true, "server", "source-runtime-name", sourceBytes);
    using var session = fixture.SessionFactory.Create(420, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
    var adapter = new JeiBookmarksAdapter();
    var initialCatalog = adapter.BuildCatalog(context, CancellationToken.None);
    Assert(ContentSelectionValidator.TryValidateExplicit(
            initialCatalog,
            ContentSelection.Create([initialCatalog.Items.Single().Id], []),
            out var initialSelection,
            out _),
        nameof(DeferredServerScopeBecomesReadyAfterTargetRuntimeScopeAppears));
    var initialPlan = adapter.Plan(
        context,
        initialCatalog,
        initialSelection!,
        CancellationToken.None);
    var seed = adapter.GetDeferredSeeds(initialPlan).Single();

    fixture.SetJeiBookmarks(false, "server", "source-runtime-name", sourceBytes);
    var pendingCatalog = adapter.BuildCatalog(context, CancellationToken.None);
    Assert(adapter.ResolveDeferred(pendingCatalog, seed).Kind ==
           DeferredJeiResolutionKind.PendingTargetScope,
        nameof(DeferredServerScopeBecomesReadyAfterTargetRuntimeScopeAppears));

    fixture.CreateJeiScope(false, "server", "actual-target-runtime-name");
    var readyCatalog = adapter.BuildCatalog(context, CancellationToken.None);
    var ready = adapter.ResolveDeferred(readyCatalog, seed);
    Assert(ready.Kind == DeferredJeiResolutionKind.Ready &&
           ready.ItemId == readyCatalog.Items.Single().Id,
        nameof(DeferredServerScopeBecomesReadyAfterTargetRuntimeScopeAppears));
}

static void DeferredServerScopeRecognizesCompletedAndConflictingTargets()
{
    foreach (var (targetText, expected, generation) in new[]
             {
                 (
                     "[{\"version\":2},{\"value\":\"source\"}]",
                     DeferredJeiResolutionKind.Complete,
                     421L),
                 (
                     "[{\"version\":2}]",
                     DeferredJeiResolutionKind.ReadyReplaceEmpty,
                     422L),
                 (
                     "[{\"version\":2},{\"value\":\"target\"}]",
                     DeferredJeiResolutionKind.Conflict,
                     423L),
             })
    {
        var sourceBytes = "[{\"version\":2},{\"value\":\"source\"}]"u8.ToArray();
        var targetBytes = Encoding.UTF8.GetBytes(targetText);
        using var fixture = AccessFixture.Create();
        fixture.SetJeiBookmarks(true, "server", "source-runtime-name", sourceBytes);
        using var session = fixture.SessionFactory.Create(generation, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
        var adapter = new JeiBookmarksAdapter();
        var initialCatalog = adapter.BuildCatalog(context, CancellationToken.None);
        Assert(ContentSelectionValidator.TryValidateExplicit(
                initialCatalog,
                ContentSelection.Create([initialCatalog.Items.Single().Id], []),
                out var initialSelection,
                out _),
            nameof(DeferredServerScopeRecognizesCompletedAndConflictingTargets));
        var seed = adapter.GetDeferredSeeds(adapter.Plan(
            context,
            initialCatalog,
            initialSelection!,
            CancellationToken.None)).Single();

        fixture.SetJeiBookmarks(false, "server", "source-runtime-name", sourceBytes);
        fixture.SetJeiBookmarks(false, "server", "actual-target-runtime-name", targetBytes);
        var targetCatalog = adapter.BuildCatalog(context, CancellationToken.None);
        var resolved = adapter.ResolveDeferred(targetCatalog, seed);
        Assert(resolved.Kind == expected,
            nameof(DeferredServerScopeRecognizesCompletedAndConflictingTargets));
        if (expected == DeferredJeiResolutionKind.ReadyReplaceEmpty)
        {
            var id = resolved.ItemId!.Value;
            Assert(ContentSelectionValidator.TryValidateExplicit(
                    targetCatalog,
                    ContentSelection.Create(
                        [id],
                        [new KeyValuePair<ContentItemId, ConflictResolution>(
                            id,
                            ConflictResolution.UseSource)]),
                    out var replaceEmpty,
                    out _),
                nameof(DeferredServerScopeRecognizesCompletedAndConflictingTargets));
            var followUp = adapter.Plan(
                context,
                targetCatalog,
                replaceEmpty!,
                CancellationToken.None);
            Assert(followUp.FileChanges.Single().RelativePath.Value.EndsWith(
                       @"server\actual-target-runtime-name\bookmarks.json",
                       StringComparison.Ordinal) &&
                   adapter.GetDeferredSeeds(followUp).Count == 0,
                nameof(DeferredServerScopeRecognizesCompletedAndConflictingTargets));
        }
    }
}

static void RenamedServerScopeMapsToUniqueTargetRuntimeScope()
{
    var sourceBytes = "[{\"version\":2},{\"type\":\"recipe\",\"value\":\"fixture\"}]"u8.ToArray();
    using var fixture = AccessFixture.Create();
    fixture.SetJeiBookmarks(true, "server", "source-runtime-name", sourceBytes);
    fixture.CreateJeiScope(false, "server", "target-runtime-name");
    using var session = fixture.SessionFactory.Create(413, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
    var adapter = new JeiBookmarksAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var item = catalog.Items.Single();
    var requested = ContentSelection.Create([item.Id], []);
    Assert(item.Disposition == PlannedContentDisposition.Add && item.IsSelectable,
        nameof(RenamedServerScopeMapsToUniqueTargetRuntimeScope));
    Assert(ContentSelectionValidator.TryValidateExplicit(catalog, requested, out var selection, out _),
        nameof(RenamedServerScopeMapsToUniqueTargetRuntimeScope));

    var plan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    var change = plan.FileChanges.Single();
    var staged = adapter.Stage(plan, CancellationToken.None);
    Assert(change.SourceSnapshot.RelativePath.Value.EndsWith(
               @"server\source-runtime-name\bookmarks.json",
               StringComparison.Ordinal) &&
           change.RelativePath.Value.EndsWith(
               @"server\target-runtime-name\bookmarks.json",
               StringComparison.Ordinal) &&
           staged.Mutations.Single().AfterBytes.CopyBytes().SequenceEqual(sourceBytes) &&
           adapter.RegenerateAllowedPaths(context, CancellationToken.None).SetEquals([change.RelativePath]),
        nameof(RenamedServerScopeMapsToUniqueTargetRuntimeScope));
}

static void AmbiguousServerScopesFailClosed()
{
    using var fixture = AccessFixture.Create();
    fixture.SetJeiBookmarks(true, "server", "source-runtime-name", "[{\"version\":2}]"u8.ToArray());
    fixture.CreateJeiScope(false, "server", "target-one");
    fixture.CreateJeiScope(false, "server", "target-two");
    using var session = fixture.SessionFactory.Create(414, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
    var adapter = new JeiBookmarksAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var item = catalog.Items.Single();

    Assert(item.Disposition == PlannedContentDisposition.Unsupported &&
           !item.IsSelectable &&
           item.DisabledReason == ContentDiagnosticCode.MissingTargetData &&
           adapter.RegenerateAllowedPaths(context, CancellationToken.None).Count == 0,
        nameof(AmbiguousServerScopesFailClosed));
}

static void ExactAndUnrelatedServerScopesFailClosed()
{
    using var fixture = AccessFixture.Create();
    fixture.SetJeiBookmarks(
        true,
        "server",
        "source-runtime-name",
        "[{\"version\":2},{\"value\":\"source\"}]"u8.ToArray());
    fixture.SetJeiBookmarks(
        false,
        "server",
        "source-runtime-name",
        "[{\"version\":2},{\"value\":\"different-target\"}]"u8.ToArray());
    fixture.CreateJeiScope(false, "server", "unrelated-target-runtime-name");
    using var session = fixture.SessionFactory.Create(416, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
    var adapter = new JeiBookmarksAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var item = catalog.Items.Single();

    Assert(item.Disposition == PlannedContentDisposition.Unsupported &&
           !item.IsSelectable &&
           item.DisabledReason == ContentDiagnosticCode.MissingTargetData &&
           adapter.RegenerateAllowedPaths(context, CancellationToken.None).Count == 0,
        nameof(ExactAndUnrelatedServerScopesFailClosed));
}

static void ExactAndMultipleUnrelatedServerScopesFailClosed()
{
    using var fixture = AccessFixture.Create();
    fixture.SetJeiBookmarks(
        true,
        "server",
        "source-runtime-name",
        "[{\"version\":2},{\"value\":\"source\"}]"u8.ToArray());
    fixture.SetJeiBookmarks(
        false,
        "server",
        "source-runtime-name",
        "[{\"version\":2},{\"value\":\"different-target\"}]"u8.ToArray());
    fixture.CreateJeiScope(false, "server", "unrelated-target-one");
    fixture.CreateJeiScope(false, "server", "unrelated-target-two");
    using var session = fixture.SessionFactory.Create(418, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
    var adapter = new JeiBookmarksAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var item = catalog.Items.Single();

    Assert(item.Disposition == PlannedContentDisposition.Unsupported &&
           !item.IsSelectable &&
           item.DisabledReason == ContentDiagnosticCode.MissingTargetData &&
           adapter.RegenerateAllowedPaths(context, CancellationToken.None).Count == 0,
        nameof(ExactAndMultipleUnrelatedServerScopesFailClosed));
}

static void SurplusTargetServerScopesInvalidateGlobalMapping()
{
    var exactBytes = "[{\"version\":2},{\"value\":\"exact\"}]"u8.ToArray();
    using var fixture = AccessFixture.Create();
    fixture.SetJeiBookmarks(true, "server", "exact-runtime-name", exactBytes);
    fixture.SetJeiBookmarks(false, "server", "exact-runtime-name", exactBytes);
    fixture.SetJeiBookmarks(
        true,
        "server",
        "renamed-source-runtime-name",
        "[{\"version\":2},{\"value\":\"renamed\"}]"u8.ToArray());
    fixture.CreateJeiScope(false, "server", "candidate-target-one");
    fixture.CreateJeiScope(false, "server", "candidate-target-two");
    using var session = fixture.SessionFactory.Create(419, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
    var adapter = new JeiBookmarksAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);

    Assert(catalog.Items.Count == 2 &&
           catalog.Items.All(item =>
               item.Disposition == PlannedContentDisposition.Unsupported &&
               item.DisabledReason == ContentDiagnosticCode.MissingTargetData &&
               !item.IsSelectable) &&
           adapter.RegenerateAllowedPaths(context, CancellationToken.None).Count == 0,
        nameof(SurplusTargetServerScopesInvalidateGlobalMapping));
}

static void ExactAndRenamedServerScopesShareOneGlobalMapping()
{
    var exactBytes = "[{\"version\":2},{\"value\":\"exact\"}]"u8.ToArray();
    using var fixture = AccessFixture.Create();
    fixture.SetJeiBookmarks(true, "server", "exact-runtime-name", exactBytes);
    fixture.SetJeiBookmarks(false, "server", "exact-runtime-name", exactBytes);
    fixture.SetJeiBookmarks(
        true,
        "server",
        "renamed-source-runtime-name",
        "[{\"version\":2},{\"value\":\"renamed\"}]"u8.ToArray());
    fixture.CreateJeiScope(false, "server", "renamed-target-runtime-name");
    using var session = fixture.SessionFactory.Create(417, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
    var adapter = new JeiBookmarksAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var allowed = adapter.RegenerateAllowedPaths(context, CancellationToken.None);

    Assert(catalog.Items.Count == 2 &&
           catalog.Items.Count(item => item.Disposition == PlannedContentDisposition.Same) == 1 &&
           catalog.Items.Count(item => item.Disposition == PlannedContentDisposition.Add) == 1 &&
           catalog.Items.All(item => item.DisabledReason is null) &&
           allowed.SetEquals([
               MustContentPath(@"config\jei\world\server\exact-runtime-name\bookmarks.json"),
               MustContentPath(@"config\jei\world\server\renamed-target-runtime-name\bookmarks.json")
           ]),
        nameof(ExactAndRenamedServerScopesShareOneGlobalMapping));
}

static void PriorSourceNamedCopyMapsToUniqueEmptyTargetScope()
{
    var sourceBytes = "[{\"version\":2},{\"type\":\"item\"}]"u8.ToArray();
    using var fixture = AccessFixture.Create();
    fixture.SetJeiBookmarks(true, "server", "source-runtime-name", sourceBytes);
    fixture.SetJeiBookmarks(false, "server", "source-runtime-name", sourceBytes);
    fixture.CreateJeiScope(false, "server", "actual-target-runtime-name");
    using var session = fixture.SessionFactory.Create(415, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
    var adapter = new JeiBookmarksAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var item = catalog.Items.Single();
    var request = ContentSelection.Create([item.Id], []);
    Assert(item.Disposition == PlannedContentDisposition.Add && item.IsSelectable,
        nameof(PriorSourceNamedCopyMapsToUniqueEmptyTargetScope));
    Assert(ContentSelectionValidator.TryValidateExplicit(catalog, request, out var selection, out _),
        nameof(PriorSourceNamedCopyMapsToUniqueEmptyTargetScope));

    var plan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    Assert(plan.FileChanges.Single().RelativePath.Value.EndsWith(
               @"server\actual-target-runtime-name\bookmarks.json",
               StringComparison.Ordinal),
        nameof(PriorSourceNamedCopyMapsToUniqueEmptyTargetScope));
}

static void DifferentJeiTargetDefaultsToKeepThenAllowsUseSource()
{
    using var fixture = AccessFixture.Create();
    fixture.SetJeiBookmarks(true, "server", "conflict-scope", "[{\"version\":2},{\"value\":[1,2]}]"u8.ToArray());
    fixture.SetJeiBookmarks(false, "server", "conflict-scope", "[{\"version\":2},{\"value\":[2,1]}]"u8.ToArray());
    using var session = fixture.SessionFactory.Create(404, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
    var adapter = new JeiBookmarksAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var item = catalog.Items.Single();
    ValidatedContentSelection? defaults = null;
    Assert(item.Disposition == PlannedContentDisposition.Conflict &&
           item.DefaultResolution == ConflictResolution.KeepTarget &&
           ContentSelectionValidator.TryCreateDefaults(catalog, out defaults, out _),
        nameof(DifferentJeiTargetDefaultsToKeepThenAllowsUseSource));
    var defaultPlan = adapter.Plan(context, catalog, defaults!, CancellationToken.None);
    Assert(defaultPlan.FileChanges.Count == 0 &&
           defaultPlan.Items.Single().Resolution == ConflictResolution.KeepTarget,
        nameof(DifferentJeiTargetDefaultsToKeepThenAllowsUseSource));

    var request = ContentSelection.Create(
        [item.Id],
        [new KeyValuePair<ContentItemId, ConflictResolution>(item.Id, ConflictResolution.UseSource)]);
    Assert(ContentSelectionValidator.TryValidateExplicit(catalog, request, out var selected, out _),
        nameof(DifferentJeiTargetDefaultsToKeepThenAllowsUseSource));
    var selectedPlan = adapter.Plan(context, catalog, selected!, CancellationToken.None);
    Assert(selectedPlan.FileChanges.Count == 1 &&
           selectedPlan.Items.Single().Disposition == PlannedContentDisposition.Conflict &&
           selectedPlan.Items.Single().Resolution == ConflictResolution.UseSource &&
           adapter.Stage(selectedPlan, CancellationToken.None).Mutations.Count == 1,
        nameof(DifferentJeiTargetDefaultsToKeepThenAllowsUseSource));
}

static void LegacyJeiIniIsDetectedButNeverCopied()
{
    using var fixture = AccessFixture.Create();
    fixture.SetJeiLegacy(true, "server", "legacy-private-scope", "legacy"u8.ToArray());
    using var session = fixture.SessionFactory.Create(405, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
    var adapter = new JeiBookmarksAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var item = catalog.Items.Single();
    ValidatedContentSelection? defaults = null;
    Assert(item.Disposition == PlannedContentDisposition.Unsupported &&
           !item.IsSelectable &&
           item.Description == "旧版收藏暂不支持" &&
           ContentSelectionValidator.TryCreateDefaults(catalog, out defaults, out _),
        nameof(LegacyJeiIniIsDetectedButNeverCopied));
    var plan = adapter.Plan(context, catalog, defaults!, CancellationToken.None);
    Assert(plan.FileChanges.Count == 0 &&
           adapter.Stage(plan, CancellationToken.None).Mutations.Count == 0 &&
           adapter.RegenerateAllowedPaths(context, CancellationToken.None).Count == 0,
        nameof(LegacyJeiIniIsDetectedButNeverCopied));
}

static void JeiRequiresCompatibleMinecraftAndModVersions()
{
    foreach (var (sourceVersion, targetVersion) in new[]
             {
                 ("19.43.0.392", "19.44.0.401"),
                 ("19.44.0.401", "19.99.0.1"),
             })
    {
        using var fixture = AccessFixture.Create();
        fixture.SetJeiBookmarks(
            true,
            "server",
            "compatible-family",
            "[{\"version\":2},{\"type\":\"item\"}]"u8.ToArray());
        fixture.CreateJeiScope(false, "server", "compatible-family");
        using var session = fixture.SessionFactory.Create(
            400 + int.Parse(sourceVersion.Split('.')[1], global::System.Globalization.CultureInfo.InvariantCulture),
            fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var context = lease.CreateProbeContext(CreateJeiCompatibility(
            fixture,
            sourceVersion,
            targetVersion));
        var adapter = new JeiBookmarksAdapter();
        var catalog = adapter.BuildCatalog(context, CancellationToken.None);
        Assert(catalog.Items.Count == 1 &&
               catalog.Diagnostics.All(item => item.Code != ContentDiagnosticCode.UnsupportedModVersion) &&
               adapter.Probe(context, CancellationToken.None).IsSupported &&
               adapter.RegenerateAllowedPaths(context, CancellationToken.None).Count == 1,
            nameof(JeiRequiresCompatibleMinecraftAndModVersions));
    }

    var rejectedCases = new[]
    {
        (SourceMinecraft: "1.21", TargetMinecraft: "1.21.1", SourceJei: "19.44.0.401", TargetJei: "19.44.0.401", Expected: ContentDiagnosticCode.UnsupportedMinecraftVersion),
        (SourceMinecraft: "1.21.1", TargetMinecraft: "1.21.2", SourceJei: "19.44.0.401", TargetJei: "19.44.0.401", Expected: ContentDiagnosticCode.UnsupportedMinecraftVersion),
        (SourceMinecraft: "1.21.1", TargetMinecraft: "1.21.1", SourceJei: "18.99.0.1", TargetJei: "19.44.0.401", Expected: ContentDiagnosticCode.UnsupportedModVersion),
        (SourceMinecraft: "1.21.1", TargetMinecraft: "1.21.1", SourceJei: "19.44.0.401", TargetJei: "20.0.0.1", Expected: ContentDiagnosticCode.UnsupportedModVersion),
        (SourceMinecraft: "1.21.1", TargetMinecraft: "1.21.1", SourceJei: "unknown", TargetJei: "unknown", Expected: ContentDiagnosticCode.UnsupportedModVersion),
    };
    for (var index = 0; index < rejectedCases.Length; index++)
    {
        var candidate = rejectedCases[index];
        using var fixture = AccessFixture.Create(candidate.SourceMinecraft, candidate.TargetMinecraft);
        using var session = fixture.SessionFactory.Create(406 + index, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var context = lease.CreateProbeContext(CreateJeiCompatibility(
            fixture,
            candidate.SourceJei,
            candidate.TargetJei));
        var adapter = new JeiBookmarksAdapter();
        var catalog = adapter.BuildCatalog(context, CancellationToken.None);
        var probe = adapter.Probe(context, CancellationToken.None);
        Assert(catalog.Items.Count == 0 &&
               catalog.Diagnostics.Single().Code == candidate.Expected &&
               !probe.IsSupported &&
               probe.DisabledReason == candidate.Expected &&
               adapter.RegenerateAllowedPaths(context, CancellationToken.None).Count == 0,
            nameof(JeiRequiresCompatibleMinecraftAndModVersions));
    }
}

static void JeiRejectsUnknownHeaderAndSchemaShapes()
{
    var invalid = new[]
    {
        "[]",
        "[{\"version\":3}]",
        "[{\"schema\":2}]",
        "[{\"version\":2,\"extra\":1}]",
        "[{\"type\":\"item\"},{\"version\":2}]",
        "[{\"version\":2},1]",
    };
    using var fixture = AccessFixture.Create();
    for (var index = 0; index < invalid.Length; index++)
    {
        fixture.SetJeiBookmarks(
            true,
            "server",
            $"schema-{index}",
            Encoding.UTF8.GetBytes(invalid[index]));
    }

    using var session = fixture.SessionFactory.Create(411, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var catalog = new JeiBookmarksAdapter().BuildCatalog(
        lease.CreateProbeContext(CreateJeiCompatibility(fixture)),
        CancellationToken.None);
    Assert(catalog.Items.Count == invalid.Length &&
           catalog.Items.All(item =>
               item.Disposition == PlannedContentDisposition.Unsupported &&
               item.DisabledReason == ContentDiagnosticCode.UnsupportedSchema &&
               !item.IsSelectable),
        nameof(JeiRejectsUnknownHeaderAndSchemaShapes));
}

static void JeiRejectsDuplicateMalformedAndOversizedJson()
{
    using var fixture = AccessFixture.Create();
    fixture.SetJeiBookmarks(
        true,
        "server",
        "duplicate",
        "[{\"version\":2},{\"value\":1,\"value\":2}]"u8.ToArray());
    fixture.SetJeiBookmarks(true, "server", "malformed", "[{\"version\":2},"u8.ToArray());
    var oversized = new byte[JeiBookmarkDocument.MaximumFileBytes + 1];
    oversized.AsSpan().Fill((byte)' ');
    "[{\"version\":2}]"u8.CopyTo(oversized);
    fixture.SetJeiBookmarks(true, "server", "oversized", oversized);
    using var session = fixture.SessionFactory.Create(412, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var catalog = new JeiBookmarksAdapter().BuildCatalog(
        lease.CreateProbeContext(CreateJeiCompatibility(fixture)),
        CancellationToken.None);

    Assert(catalog.Items.Count == 3 &&
           catalog.Items.All(item => item.Disposition == PlannedContentDisposition.Unsupported) &&
           catalog.Items.Any(item => item.DisabledReason == ContentDiagnosticCode.DuplicateJsonProperty) &&
           catalog.Items.Any(item => item.DisabledReason == ContentDiagnosticCode.MalformedJson) &&
           catalog.Items.Any(item => item.DisabledReason == ContentDiagnosticCode.LimitExceeded),
        nameof(JeiRejectsDuplicateMalformedAndOversizedJson));
}

static void JeiJsonRoadmapLimitsFailClosed()
{
    using var fixture = AccessFixture.Create();
    var exactString = new string('x', 32 * 1024);
    var longString = new string('x', 32 * 1024 + 1);
    fixture.SetJeiBookmarks(
        true,
        "server",
        "exact-string",
        Encoding.UTF8.GetBytes($"[{{\"version\":2}},{{\"value\":\"{exactString}\"}}]"));
    fixture.CreateJeiScope(false, "server", "exact-string");
    fixture.SetJeiBookmarks(
        true,
        "server",
        "long-string",
        Encoding.UTF8.GetBytes($"[{{\"version\":2}},{{\"value\":\"{longString}\"}}]"));
    var deep = "[{\"version\":2},{\"value\":" + new string('[', 70) + "0" + new string(']', 70) + "}]";
    fixture.SetJeiBookmarks(true, "server", "deep", Encoding.UTF8.GetBytes(deep));

    var exactArray = new StringBuilder("[{\"version\":2}");
    for (var index = 1; index < 250_000; index++)
    {
        exactArray.Append(",{}");
    }

    exactArray.Append(']');
    fixture.SetJeiBookmarks(
        true,
        "server",
        "exact-array",
        Encoding.UTF8.GetBytes(exactArray.ToString()));
    fixture.CreateJeiScope(false, "server", "exact-array");
    exactArray.Insert(exactArray.Length - 1, ",{}");
    fixture.SetJeiBookmarks(
        true,
        "server",
        "long-array",
        Encoding.UTF8.GetBytes(exactArray.ToString()));

    var tokenHeavy = new StringBuilder("[{\"version\":2}");
    for (var index = 1; index < 250_000; index++)
    {
        tokenHeavy.Append(",{\"a\":0}");
    }

    tokenHeavy.Append(']');
    fixture.SetJeiBookmarks(
        true,
        "server",
        "token-heavy",
        Encoding.UTF8.GetBytes(tokenHeavy.ToString()));
    using var session = fixture.SessionFactory.Create(422, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
    var catalog = new JeiBookmarksAdapter().BuildCatalog(context, CancellationToken.None);
    var exactStringId = context.CreateGenerationBoundOpaqueId("jei", "server", "exact-string".AsSpan());
    var exactArrayId = context.CreateGenerationBoundOpaqueId("jei", "server", "exact-array".AsSpan());
    HashSet<ContentItemId> expectedRejected =
    [
        context.CreateGenerationBoundOpaqueId("jei", "server", "long-string".AsSpan()),
        context.CreateGenerationBoundOpaqueId("jei", "server", "deep".AsSpan()),
        context.CreateGenerationBoundOpaqueId("jei", "server", "long-array".AsSpan()),
        context.CreateGenerationBoundOpaqueId("jei", "server", "token-heavy".AsSpan()),
    ];
    Assert(catalog.Items.Single(item => item.Id == exactStringId).Disposition == PlannedContentDisposition.Add &&
           catalog.Items.Single(item => item.Id == exactArrayId).Disposition == PlannedContentDisposition.Add &&
           catalog.Items.Where(item => expectedRejected.Contains(item.Id)).All(item =>
               item.Disposition == PlannedContentDisposition.Unsupported &&
               item.DisabledReason == ContentDiagnosticCode.LimitExceeded),
        nameof(JeiJsonRoadmapLimitsFailClosed));
}

static void JeiJsonEqualityRulesDriveWholeFileConflicts()
{
    using var fixture = AccessFixture.Create();
    fixture.SetJeiBookmarks(true, "server", "object-order", "[{\"version\":2},{\"a\":1,\"b\":2}]"u8.ToArray());
    fixture.SetJeiBookmarks(false, "server", "object-order", "[{\"version\":2},{\"b\":2,\"a\":1}]"u8.ToArray());
    fixture.SetJeiBookmarks(true, "server", "array-order", "[{\"version\":2},{\"a\":[1,2]}]"u8.ToArray());
    fixture.SetJeiBookmarks(false, "server", "array-order", "[{\"version\":2},{\"a\":[2,1]}]"u8.ToArray());
    fixture.SetJeiBookmarks(true, "server", "number-token", "[{\"version\":2},{\"a\":1}]"u8.ToArray());
    fixture.SetJeiBookmarks(false, "server", "number-token", "[{\"version\":2},{\"a\":1.0}]"u8.ToArray());
    using var session = fixture.SessionFactory.Create(413, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
    var catalog = new JeiBookmarksAdapter().BuildCatalog(context, CancellationToken.None);
    var objectOrderId = context.CreateGenerationBoundOpaqueId("jei", "server", "object-order".AsSpan());
    var arrayOrderId = context.CreateGenerationBoundOpaqueId("jei", "server", "array-order".AsSpan());
    var numberTokenId = context.CreateGenerationBoundOpaqueId("jei", "server", "number-token".AsSpan());

    Assert(catalog.Items.Single(item => item.Id == objectOrderId).Disposition == PlannedContentDisposition.Same &&
           catalog.Items.Single(item => item.Id == arrayOrderId).Disposition == PlannedContentDisposition.Conflict &&
           catalog.Items.Single(item => item.Id == numberTokenId).Disposition == PlannedContentDisposition.Conflict,
        nameof(JeiJsonEqualityRulesDriveWholeFileConflicts));
}

static void JeiSemanticNoOpStagesZeroMutations()
{
    using var fixture = AccessFixture.Create();
    fixture.SetJeiBookmarks(true, "local", "same-world", "[{\"version\":2},{\"a\":1,\"b\":2}]"u8.ToArray());
    fixture.SetJeiBookmarks(false, "local", "same-world", "[{\"version\":2},{\"b\":2,\"a\":1}]"u8.ToArray());
    var before = fixture.SnapshotInstanceTrees();
    using var session = fixture.SessionFactory.Create(414, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
    var adapter = new JeiBookmarksAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    Assert(ContentSelectionValidator.TryCreateDefaults(catalog, out var selection, out _),
        nameof(JeiSemanticNoOpStagesZeroMutations));
    var plan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    var staged = adapter.Stage(plan, CancellationToken.None);
    Assert(plan.FileChanges.Count == 0 &&
           staged.Mutations.Count == 0 &&
           fixture.SnapshotInstanceTrees() == before,
        nameof(JeiSemanticNoOpStagesZeroMutations));
}

static void JeiOpaqueIdsRespectSessionAndGenerationLifetime()
{
    const string secret = "private-server-address.invalid_25565";
    using var fixture = AccessFixture.Create();
    fixture.SetJeiBookmarks(true, "server", secret, "[{\"version\":2}]"u8.ToArray());
    fixture.SetJeiBookmarks(false, "server", secret, "[{\"version\":2}]"u8.ToArray());
    using var firstSession = fixture.SessionFactory.Create(415, fixture.Discovery);
    ContentItemId firstId;
    using (var firstLease = OpenLease(fixture, firstSession))
    {
        var firstContext = firstLease.CreateProbeContext(CreateJeiCompatibility(fixture));
        firstId = new JeiBookmarksAdapter()
            .BuildCatalog(firstContext, CancellationToken.None)
            .Items.Single().Id;
    }

    using (var reopened = OpenLease(fixture, firstSession))
    {
        var reopenedContext = reopened.CreateProbeContext(CreateJeiCompatibility(fixture));
        var reopenedId = new JeiBookmarksAdapter()
            .BuildCatalog(reopenedContext, CancellationToken.None)
            .Items.Single().Id;
        Assert(firstId == reopenedId,
            nameof(JeiOpaqueIdsRespectSessionAndGenerationLifetime));
    }

    using var otherSession = fixture.SessionFactory.Create(415, fixture.Discovery);
    using var otherLease = OpenLease(fixture, otherSession);
    var otherId = new JeiBookmarksAdapter()
        .BuildCatalog(
            otherLease.CreateProbeContext(CreateJeiCompatibility(fixture)),
            CancellationToken.None)
        .Items.Single().Id;
    using var nextGeneration = fixture.SessionFactory.Create(416, fixture.Discovery);
    using var nextLease = OpenLease(fixture, nextGeneration);
    var nextId = new JeiBookmarksAdapter()
        .BuildCatalog(
            nextLease.CreateProbeContext(CreateJeiCompatibility(fixture)),
            CancellationToken.None)
        .Items.Single().Id;
    Assert(firstId != otherId && firstId != nextId,
        nameof(JeiOpaqueIdsRespectSessionAndGenerationLifetime));

    var expiringSession = fixture.SessionFactory.Create(417, fixture.Discovery);
    using var expiringLease = OpenLease(fixture, expiringSession);
    var expiringContext = expiringLease.CreateProbeContext(CreateJeiCompatibility(fixture));
    expiringSession.Dispose();
    var expired = new JeiBookmarksAdapter().BuildCatalog(expiringContext, CancellationToken.None);
    Assert(expired.Items.Count == 0 &&
           expired.Diagnostics.Single().Code == ContentDiagnosticCode.StaleContext,
        nameof(JeiOpaqueIdsRespectSessionAndGenerationLifetime));
}

static void JeiStageAndVerifyRequireExactScopePath()
{
    var sourceBytes = "[{\"version\":2},{\"type\":\"item\",\"value\":\"x\"}]"u8.ToArray();
    using var fixture = AccessFixture.Create();
    fixture.SetJeiBookmarks(true, "server", "verify-scope", sourceBytes);
    fixture.CreateJeiScope(false, "server", "verify-scope");
    using var session = fixture.SessionFactory.Create(418, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
    var adapter = new JeiBookmarksAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var item = catalog.Items.Single();
    var request = ContentSelection.Create([item.Id], []);
    Assert(ContentSelectionValidator.TryValidateExplicit(catalog, request, out var selected, out _),
        nameof(JeiStageAndVerifyRequireExactScopePath));
    var staged = adapter.Stage(
        adapter.Plan(context, catalog, selected!, CancellationToken.None),
        CancellationToken.None);
    var path = staged.Mutations.Single().Change.RelativePath;
    var exact = ContentFileSnapshot.Create(
        path,
        true,
        sourceBytes,
        DateTimeOffset.UnixEpoch,
        0,
        new ContentFileIdentity(9, 8, 7));
    var valid = adapter.Verify(staged, [exact], CancellationToken.None);
    Assert(ContentRelativePath.TryCreate(
            @"config\jei\world\server\other-scope\bookmarks.json",
            out var otherPath,
            out _),
        nameof(JeiStageAndVerifyRequireExactScopePath));
    var relabeled = ContentFileSnapshot.Create(
        otherPath!,
        true,
        sourceBytes,
        DateTimeOffset.UnixEpoch,
        0,
        new ContentFileIdentity(9, 8, 7));
    var malformed = ContentFileSnapshot.Create(
        path,
        true,
        "not-json"u8,
        DateTimeOffset.UnixEpoch,
        0,
        new ContentFileIdentity(9, 8, 7));
    Assert(valid.IsValid &&
           !adapter.Verify(staged, [relabeled], CancellationToken.None).IsValid &&
           !adapter.Verify(staged, [malformed], CancellationToken.None).IsValid,
        nameof(JeiStageAndVerifyRequireExactScopePath));
}

static void JeiPlanRejectsChangedSnapshotsAsStale()
{
    using var fixture = AccessFixture.Create();
    fixture.SetJeiBookmarks(true, "server", "stale-scope", "[{\"version\":2},{\"a\":1}]"u8.ToArray());
    fixture.SetJeiBookmarks(false, "server", "stale-scope", "[{\"version\":2},{\"a\":2}]"u8.ToArray());
    using var session = fixture.SessionFactory.Create(419, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
    var adapter = new JeiBookmarksAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var item = catalog.Items.Single();
    var request = ContentSelection.Create(
        [item.Id],
        [new KeyValuePair<ContentItemId, ConflictResolution>(item.Id, ConflictResolution.UseSource)]);
    Assert(ContentSelectionValidator.TryValidateExplicit(catalog, request, out var selection, out _),
        nameof(JeiPlanRejectsChangedSnapshotsAsStale));
    fixture.SetJeiBookmarks(false, "server", "stale-scope", "[{\"version\":2},{\"a\":3}]"u8.ToArray());
    var plan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    Assert(plan.FileChanges.Count == 0 &&
           plan.Diagnostics.Single().Code == ContentDiagnosticCode.StaleContext,
        nameof(JeiPlanRejectsChangedSnapshotsAsStale));
}

static void JeiPlanRejectsAddedTargetScopeAsStale()
{
    using var fixture = AccessFixture.Create();
    fixture.SetJeiBookmarks(
        true,
        "server",
        "source-scope",
        "[{\"version\":2},{\"a\":1}]"u8.ToArray());
    using var session = fixture.SessionFactory.Create(422, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
    var adapter = new JeiBookmarksAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var item = catalog.Items.Single();
    Assert(ContentSelectionValidator.TryValidateExplicit(
            catalog,
            ContentSelection.Create([item.Id], []),
            out var selection,
            out _),
        nameof(JeiPlanRejectsAddedTargetScopeAsStale));

    fixture.CreateJeiScope(false, "server", "unrelated-target-scope");
    var plan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    Assert(plan.FileChanges.Count == 0 &&
           plan.Diagnostics.Single().Code == ContentDiagnosticCode.StaleContext,
        nameof(JeiPlanRejectsAddedTargetScopeAsStale));
}

static void JeiPlanRejectsAddedSourceScopeAsStale()
{
    using var fixture = AccessFixture.Create();
    fixture.SetJeiBookmarks(
        true,
        "server",
        "source-scope",
        "[{\"version\":2},{\"a\":1}]"u8.ToArray());
    using var session = fixture.SessionFactory.Create(423, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
    var adapter = new JeiBookmarksAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var item = catalog.Items.Single();
    Assert(ContentSelectionValidator.TryValidateExplicit(
            catalog,
            ContentSelection.Create([item.Id], []),
            out var selection,
            out _),
        nameof(JeiPlanRejectsAddedSourceScopeAsStale));

    fixture.SetJeiBookmarks(
        true,
        "server",
        "new-source-scope",
        "[{\"version\":2},{\"a\":2}]"u8.ToArray());
    var plan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    Assert(plan.FileChanges.Count == 0 &&
           plan.Diagnostics.Single().Code == ContentDiagnosticCode.StaleContext,
        nameof(JeiPlanRejectsAddedSourceScopeAsStale));
}

static void JeiCatalogOrderingAndPrivacyAreDeterministic()
{
    var scopes = new[]
    {
        (Kind: "local", Scope: "z-private-world"),
        (Kind: "server", Scope: "b-private-server"),
        (Kind: "local", Scope: "a-private-world"),
        (Kind: "server", Scope: "a-private-server"),
    };
    using var fixture = AccessFixture.Create();
    foreach (var scope in scopes)
    {
        fixture.SetJeiBookmarks(true, scope.Kind, scope.Scope, "[{\"version\":2}]"u8.ToArray());
        fixture.SetJeiBookmarks(false, scope.Kind, scope.Scope, "[{\"version\":2}]"u8.ToArray());
    }

    using var session = fixture.SessionFactory.Create(420, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
    var catalog = new JeiBookmarksAdapter().BuildCatalog(context, CancellationToken.None);
    var expectedIds = new[]
    {
        context.CreateGenerationBoundOpaqueId("jei", "local", "a-private-world".AsSpan()),
        context.CreateGenerationBoundOpaqueId("jei", "local", "z-private-world".AsSpan()),
        context.CreateGenerationBoundOpaqueId("jei", "server", "a-private-server".AsSpan()),
        context.CreateGenerationBoundOpaqueId("jei", "server", "b-private-server".AsSpan()),
    };
    var serialized = global::System.Text.Json.JsonSerializer.Serialize(catalog) +
                     global::System.Text.Json.JsonSerializer.Serialize(catalog.Diagnostics);
    Assert(catalog.Items.Select(item => item.Id).SequenceEqual(expectedIds) &&
           catalog.Items.Select(item => item.DisplayName).SequenceEqual(
               ["单人收藏 1", "单人收藏 2", "多人收藏 1", "多人收藏 2"]) &&
           scopes.All(scope => !serialized.Contains(scope.Scope, StringComparison.Ordinal)) &&
           catalog.Items.All(item => item.Id.TechnicalKey.Length == 43),
        nameof(JeiCatalogOrderingAndPrivacyAreDeterministic));
}

static void JeiExclusionPathsAreNeverCataloged()
{
    using var fixture = AccessFixture.Create();
    fixture.SetInstanceRelativeFile(true, @"config\jei\blacklist.json", "{}"u8.ToArray());
    fixture.SetInstanceRelativeFile(true, @"config\jei\jei-client.ini", "fixture"u8.ToArray());
    fixture.SetInstanceRelativeFile(true, @"config\jei\world\server\excluded\blacklist.json", "{}"u8.ToArray());
    fixture.SetInstanceRelativeFile(true, @"config\jei\world\server\excluded\lookupHistory.json", "[]"u8.ToArray());
    fixture.SetInstanceRelativeFile(true, @"config\jei\world\server\excluded\emi.json", "[]"u8.ToArray());
    using var session = fixture.SessionFactory.Create(421, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateJeiCompatibility(fixture));
    var adapter = new JeiBookmarksAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var excludedNames = new[]
    {
        "blacklist.json",
        "jei-client.ini",
        "lookupHistory.json",
        "emi.json",
    };
    Assert(catalog.Items.Count == 0 &&
           adapter.RegenerateAllowedPaths(context, CancellationToken.None).Count == 0 &&
           fixture.AuditedCapability.AuditLog
               .Where(entry => string.Equals(entry.Operation, "ReadFile", StringComparison.Ordinal))
               .All(entry => excludedNames.All(name =>
                   !entry.RequestedPath.EndsWith(name, StringComparison.OrdinalIgnoreCase))),
        nameof(JeiExclusionPathsAreNeverCataloged));
}

static void AdapterRejectsChangedSnapshotAsStale()
{
    using var fixture = AccessFixture.Create();
    using var session = fixture.SessionFactory.Create(302, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateCompatibility(fixture));
    var adapter = new VanillaOptionsAdapter(
        new Pcl2OptionsMigrationPreviewer(fixture.AuditedCapability));
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var requested = ContentSelection.Create(
        catalog.Items.Where(item => item.IsSelectable).Select(item => item.Id),
        []);
    Assert(ContentSelectionValidator.TryValidateExplicit(
            catalog,
            requested,
            out var selection,
            out _),
        nameof(AdapterRejectsChangedSnapshotAsStale));

    fixture.SetOptions(
        source: false,
        "lang:de_de\nkey_key.jump:key.keyboard.k\n"u8.ToArray());
    var plan = adapter.Plan(context, catalog, selection!, CancellationToken.None);

    Assert(plan.FileChanges.Count == 0 &&
           plan.Diagnostics.Count == 1 &&
           plan.Diagnostics[0].Code == ContentDiagnosticCode.StaleContext,
        nameof(AdapterRejectsChangedSnapshotAsStale));
}

static void VanillaSeedsSchemaVersionBeforeFirstLaunch()
{
    const string sourceText =
        "version:3955\n" +
        "lang:zh_cn\n" +
        "key_key.jump:key.keyboard.space\n" +
        "resourcePacks:[\"source\"]\n";
    const string unstartedTargetText =
        "lang:en_us\n" +
        "resourcePacks:[\"target\"]\n";
    using var fixture = AccessFixture.Create();
    fixture.SetOptions(true, Encoding.UTF8.GetBytes(sourceText));
    fixture.SetOptions(false, Encoding.UTF8.GetBytes(unstartedTargetText));
    using var session = fixture.SessionFactory.Create(311, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateCompatibility(fixture));
    var adapter = new VanillaOptionsAdapter(
        new Pcl2OptionsMigrationPreviewer(fixture.AuditedCapability));

    var probe = adapter.Probe(context, CancellationToken.None);
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var schemaItem = catalog.Items.Single(item => item.Id.TechnicalKey == "version");
    var requested = ContentSelection.Create(
        catalog.Items
            .Where(item => item.IsSelectable)
            .Select(item => item.Id),
        []);
    Assert(ContentSelectionValidator.TryValidateExplicit(
            catalog,
            requested,
            out var selection,
            out _),
        nameof(VanillaSeedsSchemaVersionBeforeFirstLaunch));
    var plan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    var staged = adapter.Stage(plan, CancellationToken.None);
    var output = Encoding.UTF8.GetString(staged.Mutations.Single().AfterBytes.CopyBytes());

    Assert(probe.IsSupported &&
           schemaItem.Disposition == PlannedContentDisposition.Add &&
           !schemaItem.IsSelectable &&
           plan.Items.Single(item => item.Id.TechnicalKey == "version").Disposition ==
               PlannedContentDisposition.Add &&
           output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
               .Count(line => line.StartsWith("version:", StringComparison.Ordinal)) == 1 &&
           output.Contains("version:3955", StringComparison.Ordinal) &&
           output.Contains("lang:zh_cn", StringComparison.Ordinal) &&
           output.Contains("key_key.jump:key.keyboard.space", StringComparison.Ordinal) &&
           output.Contains("resourcePacks:[\"target\"]", StringComparison.Ordinal),
        nameof(VanillaSeedsSchemaVersionBeforeFirstLaunch));
}

static void VanillaRejectsInvalidSchemaVersions()
{
    var cases = new (string Source, string Target)[]
    {
        ("lang:zh_cn\nkey_key.jump:key.keyboard.space\n", "lang:en_us\n"),
        ("version:not-a-number\nlang:zh_cn\n", "lang:en_us\n"),
        ("version:0\nlang:zh_cn\n", "lang:en_us\n"),
        ("version:-1\nlang:zh_cn\n", "lang:en_us\n"),
        ("version:3955\nlang:zh_cn\n", "version:not-a-number\nlang:en_us\n"),
        ("version:3955\nversion:3955\nlang:zh_cn\n", "lang:en_us\n"),
        ("version:3955\nlang:zh_cn\n", "version:3955\nversion:3955\nlang:en_us\n"),
    };
    foreach (var (sourceOptions, targetOptions) in cases)
    {
        using var fixture = AccessFixture.Create();
        fixture.SetOptions(true, Encoding.UTF8.GetBytes(sourceOptions));
        fixture.SetOptions(false, Encoding.UTF8.GetBytes(targetOptions));
        using var session = fixture.SessionFactory.Create(312, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var context = lease.CreateProbeContext(CreateCompatibility(fixture));
        var adapter = new VanillaOptionsAdapter(
            new Pcl2OptionsMigrationPreviewer(fixture.AuditedCapability));
        var probe = adapter.Probe(context, CancellationToken.None);
        var catalog = adapter.BuildCatalog(context, CancellationToken.None);

        Assert(!probe.IsSupported &&
               probe.DisabledReason == ContentDiagnosticCode.UnsupportedSchema &&
               catalog.Items.Count == 0 &&
               catalog.Diagnostics.Single().Code == ContentDiagnosticCode.UnsupportedSchema,
            nameof(VanillaRejectsInvalidSchemaVersions));
    }
}

static void AdapterAcceptsOnlyCatalogBoundValidatedSelection()
{
    using var fixture = AccessFixture.Create();
    using var session = fixture.SessionFactory.Create(303, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateCompatibility(fixture));
    var adapter = new VanillaOptionsAdapter(
        new Pcl2OptionsMigrationPreviewer(fixture.AuditedCapability));
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var foreignCatalog = ContentCatalog.Create("vanilla", [], []);
    Assert(ContentSelectionValidator.TryCreateDefaults(
            foreignCatalog,
            out var foreignSelection,
            out _),
        nameof(AdapterAcceptsOnlyCatalogBoundValidatedSelection));
    var rejected = adapter.Plan(
        context,
        catalog,
        foreignSelection!,
        CancellationToken.None);
    var planMethod = typeof(VanillaOptionsAdapter).GetMethod(nameof(IContentAdapter.Plan));

    Assert(rejected.FileChanges.Count == 0 &&
           rejected.Diagnostics.Single().Code == ContentDiagnosticCode.CapabilityRejected &&
           planMethod is not null &&
           planMethod.GetParameters()[2].ParameterType == typeof(ValidatedContentSelection),
        nameof(AdapterAcceptsOnlyCatalogBoundValidatedSelection));
}

static void VanillaProtectsFixedAndCallerKeysAndPreservesRawTarget()
{
    const string sourceText =
        "lang:en_us\n" +
        "key_key.jump:key.keyboard.space\n" +
        "gamma:1.0\n" +
        "resourcePacks:[\"source\"]\n" +
        "version:3600\n";
    const string targetText =
        "# target comment\r\n" +
        "lang:zh_cn\r\n" +
        "key_key.jump:key.keyboard.j\r\n" +
        "gamma:0.5\r\n" +
        "resourcePacks:[\"target\"]\r\n" +
        "version:3700\r\n" +
        "targetOnly:keep\r\n" +
        "malformed target line\r\n";
    using var fixture = AccessFixture.Create();
    fixture.SetOptions(true, Encoding.UTF8.GetBytes(sourceText));
    fixture.SetOptions(false, Encoding.UTF8.GetBytes(targetText));
    var before = fixture.SnapshotInstanceTrees();
    using var session = fixture.SessionFactory.Create(304, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateCompatibility(fixture));
    var planner = new OptionsMergePlanner(
        new HashSet<string>(["lang"], StringComparer.Ordinal));
    var adapter = new VanillaOptionsAdapter(
        new Pcl2OptionsMigrationPreviewer(fixture.AuditedCapability, planner));
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var jump = catalog.Items.Single(item => item.Id.TechnicalKey == "key_key.jump");
    var requested = ContentSelection.Create([jump.Id], []);
    Assert(ContentSelectionValidator.TryValidateExplicit(
            catalog,
            requested,
            out var selection,
            out _),
        nameof(VanillaProtectsFixedAndCallerKeysAndPreservesRawTarget));
    var plan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    var staged = adapter.Stage(plan, CancellationToken.None);
    var expected = planner.PlanSelected(
        sourceText,
        targetText,
        new HashSet<string>(["key_key.jump"], StringComparer.Ordinal));
    var actual = Encoding.UTF8.GetString(staged.Mutations.Single().AfterBytes.CopyBytes());

    Assert(catalog.Items.Single(item => item.Id.TechnicalKey == "lang").Disposition ==
               PlannedContentDisposition.Protected &&
           catalog.Items.Single(item => item.Id.TechnicalKey == "resourcePacks").Disposition ==
               PlannedContentDisposition.Protected &&
           catalog.Items.Single(item => item.Id.TechnicalKey == "version").Disposition ==
               PlannedContentDisposition.Protected &&
           catalog.Items.Single(item => item.Id.TechnicalKey == "targetOnly").Disposition ==
               PlannedContentDisposition.Same &&
           actual == expected.Content &&
           actual.Contains("# target comment\r\n", StringComparison.Ordinal) &&
           actual.Contains("lang:zh_cn\r\n", StringComparison.Ordinal) &&
           actual.Contains("gamma:0.5\r\n", StringComparison.Ordinal) &&
           actual.Contains("resourcePacks:[\"target\"]\r\n", StringComparison.Ordinal) &&
           actual.Contains("targetOnly:keep\r\n", StringComparison.Ordinal) &&
           actual.Contains("malformed target line\r\n", StringComparison.Ordinal) &&
           fixture.SnapshotInstanceTrees() == before,
        nameof(VanillaProtectsFixedAndCallerKeysAndPreservesRawTarget));
}

static void DisposedVanillaContextIsStale()
{
    using var fixture = AccessFixture.Create();
    using var session = fixture.SessionFactory.Create(305, fixture.Discovery);
    var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateCompatibility(fixture));
    var adapter = new VanillaOptionsAdapter(
        new Pcl2OptionsMigrationPreviewer(fixture.AuditedCapability));
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    Assert(ContentSelectionValidator.TryCreateDefaults(catalog, out var selection, out _),
        nameof(DisposedVanillaContextIsStale));
    lease.Dispose();
    var plan = adapter.Plan(context, catalog, selection!, CancellationToken.None);

    Assert(plan.FileChanges.Count == 0 &&
           plan.Diagnostics.Single().Code == ContentDiagnosticCode.StaleContext,
        nameof(DisposedVanillaContextIsStale));
}

static void VanillaOptionsFourMiBBoundaryIsExact()
{
    var exact = Enumerable.Repeat((byte)' ', 4 * 1024 * 1024).ToArray();
    "version:3955\nlang:en_us\n"u8.CopyTo(exact);
    using (var fixture = AccessFixture.Create())
    {
        fixture.SetOptions(true, exact);
        using var session = fixture.SessionFactory.Create(306, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var context = lease.CreateProbeContext(CreateCompatibility(fixture));
        var adapter = new VanillaOptionsAdapter(
            new Pcl2OptionsMigrationPreviewer(fixture.AuditedCapability));
        var probe = adapter.Probe(context, CancellationToken.None);
        Assert(probe.IsSupported &&
               probe.Diagnostics.All(item => item.Code != ContentDiagnosticCode.LimitExceeded),
            nameof(VanillaOptionsFourMiBBoundaryIsExact));
    }

    var oversized = new byte[4 * 1024 * 1024 + 1];
    "lang:en_us\n"u8.CopyTo(oversized);
    using (var fixture = AccessFixture.Create())
    {
        fixture.SetOptions(true, oversized);
        using var session = fixture.SessionFactory.Create(307, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var context = lease.CreateProbeContext(CreateCompatibility(fixture));
        var adapter = new VanillaOptionsAdapter(
            new Pcl2OptionsMigrationPreviewer(fixture.AuditedCapability));
        var probe = adapter.Probe(context, CancellationToken.None);
        Assert(!probe.IsSupported &&
               probe.DisabledReason == ContentDiagnosticCode.LimitExceeded &&
               probe.Diagnostics.Single().Code == ContentDiagnosticCode.LimitExceeded,
            nameof(VanillaOptionsFourMiBBoundaryIsExact));
    }
}

static void VanillaSemanticNoOpStagesZeroMutations()
{
    const string same = "# keep\r\nversion:3955\r\nlang:en_us\r\nkey_key.jump:key.keyboard.space\r\n";
    using var fixture = AccessFixture.Create();
    fixture.SetOptions(true, Encoding.UTF8.GetBytes(same));
    fixture.SetOptions(false, Encoding.UTF8.GetBytes(same));
    using var session = fixture.SessionFactory.Create(308, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateCompatibility(fixture));
    var adapter = new VanillaOptionsAdapter(
        new Pcl2OptionsMigrationPreviewer(fixture.AuditedCapability));
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    Assert(ContentSelectionValidator.TryCreateDefaults(catalog, out var selection, out _),
        nameof(VanillaSemanticNoOpStagesZeroMutations));
    var plan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    var staged = adapter.Stage(plan, CancellationToken.None);
    var verified = adapter.Verify(staged, [], CancellationToken.None);

    Assert(plan.FileChanges.Count == 0 &&
           staged.Mutations.Count == 0 &&
           verified.IsValid &&
           adapter.RegenerateAllowedPaths(context, CancellationToken.None)
               .SetEquals([MustContentPath("options.txt")]),
        nameof(VanillaSemanticNoOpStagesZeroMutations));
}

static void VanillaStageAndVerifyRequireExactReread()
{
    using var fixture = AccessFixture.Create();
    using var session = fixture.SessionFactory.Create(309, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateCompatibility(fixture));
    var adapter = new VanillaOptionsAdapter(
        new Pcl2OptionsMigrationPreviewer(fixture.AuditedCapability));
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var requested = ContentSelection.Create(
        catalog.Items.Where(item => item.IsSelectable).Select(item => item.Id),
        []);
    Assert(ContentSelectionValidator.TryValidateExplicit(
            catalog,
            requested,
            out var selection,
            out _),
        nameof(VanillaStageAndVerifyRequireExactReread));
    var staged = adapter.Stage(
        adapter.Plan(context, catalog, selection!, CancellationToken.None),
        CancellationToken.None);
    var mutation = staged.Mutations.Single();
    var correct = ContentFileSnapshot.Create(
        mutation.Change.RelativePath,
        true,
        mutation.AfterBytes.CopyBytes(),
        DateTimeOffset.UtcNow,
        0,
        mutation.Change.TargetSnapshot.Identity);
    var mismatching = ContentFileSnapshot.Create(
        mutation.Change.RelativePath,
        true,
        "different"u8,
        DateTimeOffset.UtcNow,
        0,
        mutation.Change.TargetSnapshot.Identity);
    var relabeled = ContentFileSnapshot.Create(
        MustContentPath("other.txt"),
        true,
        mutation.AfterBytes.CopyBytes(),
        DateTimeOffset.UtcNow,
        0,
        mutation.Change.TargetSnapshot.Identity);

    Assert(adapter.Verify(staged, [correct], CancellationToken.None).IsValid &&
           !adapter.Verify(staged, [mismatching], CancellationToken.None).IsValid &&
           !adapter.Verify(staged, [relabeled], CancellationToken.None).IsValid,
        nameof(VanillaStageAndVerifyRequireExactReread));
}

static void VanillaGuiScaleCarriesFancyMenuFirstLaunchMarker()
{
    var markerBytes = "You're not supposed to be here! Shoo!"u8.ToArray();
    using var fixture = AccessFixture.Create();
    fixture.SetOptions(true, "version:3955\nguiScale:3\n"u8.ToArray());
    fixture.SetOptions(false, "version:3955\nguiScale:2\n"u8.ToArray());
    fixture.SetInstanceRelativeFile(true, @"fancymenu_data\default_scale_set.fm", markerBytes);
    using var session = fixture.SessionFactory.Create(313, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateVanillaCompatibility(fixture, "3.9.9", "3.9.9"));
    var adapter = new VanillaOptionsAdapter(
        new Pcl2OptionsMigrationPreviewer(fixture.AuditedCapability));
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var guiScale = catalog.Items.Single(item => item.Id.TechnicalKey == "guiScale");
    var request = ContentSelection.Create([guiScale.Id], []);
    Assert(ContentSelectionValidator.TryValidateExplicit(catalog, request, out var selection, out _),
        nameof(VanillaGuiScaleCarriesFancyMenuFirstLaunchMarker));

    var plan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    var staged = adapter.Stage(plan, CancellationToken.None);
    var markerPath = MustContentPath(@"fancymenu_data\default_scale_set.fm");
    var markerMutation = staged.Mutations.Single(item => item.Change.RelativePath.Equals(markerPath));
    var rereads = staged.Mutations
        .Select(mutation => ContentFileSnapshot.Create(
            mutation.Change.RelativePath,
            true,
            mutation.AfterBytes.CopyBytes(),
            DateTimeOffset.UtcNow,
            0,
            mutation.Change.TargetSnapshot.Identity))
        .ToArray();
    Assert(plan.FileChanges.Count == 2 &&
           staged.Mutations.Count == 2 &&
           markerMutation.AfterBytes.CopyBytes().SequenceEqual(markerBytes) &&
           adapter.Verify(staged, rereads, CancellationToken.None).IsValid &&
           !adapter.Verify(staged, rereads.Take(1).ToArray(), CancellationToken.None).IsValid &&
           adapter.RegenerateAllowedPaths(context, CancellationToken.None)
               .SetEquals([MustContentPath("options.txt"), markerPath]),
        nameof(VanillaGuiScaleCarriesFancyMenuFirstLaunchMarker));
}

static void VanillaHalfInitializedNeoForgeTargetCarriesFancyMenuMarker()
{
    var markerBytes = "You're not supposed to be here! Shoo!"u8.ToArray();
    using var fixture = AccessFixture.Create();
    fixture.SetOptions(true, "version:3955\nguiScale:3\nlang:zh_cn\n"u8.ToArray());
    fixture.SetOptions(
        false,
        "lang:zh_cn\nresourcePacks:[\"vanilla\",\"mod_resources\"]\n"u8.ToArray());
    fixture.SetInstanceRelativeFile(true, @"fancymenu_data\default_scale_set.fm", markerBytes);
    AddExactCompatibleMods(fixture);
    AddFancyMenuNeoForgeMod(fixture, true);
    AddFancyMenuNeoForgeMod(fixture, false);
    using var session = fixture.SessionFactory.Create(318, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = new ContentCompatibilityProbe(new ModPresenceProbe())
        .ProbeAndCreateContext(lease, Beta3ModLimits());
    var adapter = new VanillaOptionsAdapter(
        new Pcl2OptionsMigrationPreviewer(fixture.AuditedCapability));
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var guiScale = catalog.Items.Single(item => item.Id.TechnicalKey == "guiScale");
    var request = ContentSelection.Create([guiScale.Id], []);
    Assert(ContentSelectionValidator.TryValidateExplicit(catalog, request, out var selection, out _),
        nameof(VanillaHalfInitializedNeoForgeTargetCarriesFancyMenuMarker));

    var plan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    var staged = adapter.Stage(plan, CancellationToken.None);
    Assert(context.Compatibility.SourceModVersions.GetValueOrDefault("fancymenu") == "3.9.9" &&
           context.Compatibility.TargetModVersions.GetValueOrDefault("fancymenu") == "3.9.9" &&
           plan.FileChanges.Select(change => change.RelativePath.Value).ToHashSet(StringComparer.Ordinal)
               .SetEquals(["options.txt", @"fancymenu_data\default_scale_set.fm"]) &&
           staged.Mutations.Count == 2,
        nameof(VanillaHalfInitializedNeoForgeTargetCarriesFancyMenuMarker));
}

static void VanillaGuiScaleSkipsExistingFancyMenuMarker()
{
    var markerBytes = "You're not supposed to be here! Shoo!"u8.ToArray();
    using var fixture = AccessFixture.Create();
    fixture.SetOptions(true, "version:3955\nguiScale:3\n"u8.ToArray());
    fixture.SetOptions(false, "version:3955\nguiScale:2\n"u8.ToArray());
    fixture.SetInstanceRelativeFile(true, @"fancymenu_data\default_scale_set.fm", markerBytes);
    fixture.SetInstanceRelativeFile(false, @"fancymenu_data\default_scale_set.fm", markerBytes);
    using var session = fixture.SessionFactory.Create(314, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateVanillaCompatibility(fixture, "3.9.9", "3.9.9"));
    var adapter = new VanillaOptionsAdapter(
        new Pcl2OptionsMigrationPreviewer(fixture.AuditedCapability));
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var guiScale = catalog.Items.Single(item => item.Id.TechnicalKey == "guiScale");
    var request = ContentSelection.Create([guiScale.Id], []);
    Assert(ContentSelectionValidator.TryValidateExplicit(catalog, request, out var selection, out _),
        nameof(VanillaGuiScaleSkipsExistingFancyMenuMarker));

    var plan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    Assert(plan.FileChanges.Count == 1 &&
           plan.FileChanges.Single().RelativePath.Equals(MustContentPath("options.txt")),
        nameof(VanillaGuiScaleSkipsExistingFancyMenuMarker));
}

static void VanillaGuiScaleRejectsUnverifiableFancyMenuMarker()
{
    using var fixture = AccessFixture.Create();
    fixture.SetOptions(true, "version:3955\nguiScale:3\n"u8.ToArray());
    fixture.SetOptions(false, "version:3955\nguiScale:2\n"u8.ToArray());
    fixture.SetInstanceRelativeFile(true, @"fancymenu_data\default_scale_set.fm", "unknown"u8.ToArray());
    using var session = fixture.SessionFactory.Create(315, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateVanillaCompatibility(fixture, "3.9.9", "3.9.9"));
    var adapter = new VanillaOptionsAdapter(
        new Pcl2OptionsMigrationPreviewer(fixture.AuditedCapability));
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var guiScale = catalog.Items.Single(item => item.Id.TechnicalKey == "guiScale");
    var request = ContentSelection.Create([guiScale.Id], []);
    Assert(ContentSelectionValidator.TryValidateExplicit(catalog, request, out var selection, out _),
        nameof(VanillaGuiScaleRejectsUnverifiableFancyMenuMarker));

    var plan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    Assert(plan.FileChanges.Count == 0 &&
           plan.Diagnostics.Any(item => item.Code == ContentDiagnosticCode.UnsupportedSchema),
        nameof(VanillaGuiScaleRejectsUnverifiableFancyMenuMarker));
}

static void AppearanceSeedsValidatedConfigBeforeFirstLaunch()
{
    var source = "{\n  \"shaders\": [null, {\"id\":\"dark\"}],\n  \"version\": 2,\n  \"selectedShaderIndex\": 1\n}\n"u8.ToArray();
    using var fixture = AccessFixture.Create();
    fixture.SetInstanceRelativeFile(
        true,
        @"config\darkmodeeverywhereshaders.json",
        source);
    using var session = fixture.SessionFactory.Create(600, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateAppearanceCompatibility(fixture));
    var adapter = new DarkModeEverywhereAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var item = catalog.Items.Single();
    Assert(item.Disposition == PlannedContentDisposition.Add &&
           item.IsSelectable &&
           !item.IsSelectedByDefault,
        nameof(AppearanceSeedsValidatedConfigBeforeFirstLaunch));
    Assert(item.Description == "目标尚未初始化 · 可创建配置 · 默认跳过",
        nameof(AppearanceSeedsValidatedConfigBeforeFirstLaunch));
    Assert(ContentSelectionValidator.TryValidateExplicit(
            catalog,
            ContentSelection.Create([item.Id], []),
            out var selection,
            out _),
        nameof(AppearanceSeedsValidatedConfigBeforeFirstLaunch));

    var plan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    var change = plan.FileChanges.Single();
    var staged = adapter.Stage(plan, CancellationToken.None);
    var mutation = staged.Mutations.Single();
    var reread = ContentFileSnapshot.Create(
        mutation.Change.RelativePath,
        true,
        mutation.AfterBytes.CopyBytes(),
        DateTimeOffset.UtcNow,
        0,
        null);
    Assert(!change.TargetSnapshot.Exists &&
           mutation.AfterBytes.CopyBytes().SequenceEqual(source) &&
           adapter.Verify(staged, [reread], CancellationToken.None).IsValid &&
           adapter.RegenerateAllowedPaths(context, CancellationToken.None)
               .SetEquals([MustContentPath(@"config\darkmodeeverywhereshaders.json")]),
        nameof(AppearanceSeedsValidatedConfigBeforeFirstLaunch));
}

static void AppearanceMapsSelectedShaderByIdentityAndPreservesTargetBytes()
{
    const string sourceText =
        "{\n  \"shaders\": [null, {\"id\":\"light\"}, {\"id\":\"toasted\"}],\n" +
        "  \"version\": 2,\n  \"selectedShaderIndex\": 2\n}\n";
    const string targetText =
        "{\r\n  \"version\": 2,\r\n  \"shaders\": [ null, { \"id\": \"toasted\" }, { \"id\": \"light\" } ],\r\n" +
        "  \"selectedShaderIndex\": 0\r\n}\r\n";
    const string expectedText =
        "{\r\n  \"version\": 2,\r\n  \"shaders\": [ null, { \"id\": \"toasted\" }, { \"id\": \"light\" } ],\r\n" +
        "  \"selectedShaderIndex\": 1\r\n}\r\n";
    using var fixture = AccessFixture.Create();
    fixture.SetInstanceRelativeFile(true, @"config\darkmodeeverywhereshaders.json", Encoding.UTF8.GetBytes(sourceText));
    fixture.SetInstanceRelativeFile(false, @"config\darkmodeeverywhereshaders.json", Encoding.UTF8.GetBytes(targetText));
    using var session = fixture.SessionFactory.Create(601, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateAppearanceCompatibility(fixture));
    var adapter = new DarkModeEverywhereAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var item = catalog.Items.Single();
    var request = ContentSelection.Create([item.Id], []);
    Assert(item.Disposition == PlannedContentDisposition.Update && item.IsSelectable,
        nameof(AppearanceMapsSelectedShaderByIdentityAndPreservesTargetBytes));
    Assert(ContentSelectionValidator.TryValidateExplicit(catalog, request, out var selection, out _),
        nameof(AppearanceMapsSelectedShaderByIdentityAndPreservesTargetBytes));

    var plan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    var staged = adapter.Stage(plan, CancellationToken.None);
    var mutation = staged.Mutations.Single();
    var reread = ContentFileSnapshot.Create(
        mutation.Change.RelativePath,
        true,
        mutation.AfterBytes.CopyBytes(),
        DateTimeOffset.UtcNow,
        0,
        mutation.Change.TargetSnapshot.Identity);
    Assert(plan.FileChanges.Single().RelativePath.Equals(
               MustContentPath(@"config\darkmodeeverywhereshaders.json")) &&
           Encoding.UTF8.GetString(mutation.AfterBytes.CopyBytes()) == expectedText &&
           adapter.Verify(staged, [reread], CancellationToken.None).IsValid,
        nameof(AppearanceMapsSelectedShaderByIdentityAndPreservesTargetBytes));
}

static void AppearanceMapsDisabledModeAndRejectsAmbiguousShaders()
{
    using (var fixture = AccessFixture.Create())
    {
        fixture.SetInstanceRelativeFile(
            true,
            @"config\darkmodeeverywhereshaders.json",
            "{\"shaders\":[null,{\"id\":\"dark\"}],\"version\":2,\"selectedShaderIndex\":0}"u8.ToArray());
        fixture.SetInstanceRelativeFile(
            false,
            @"config\darkmodeeverywhereshaders.json",
            "{\"shaders\":[{\"id\":\"dark\"},null],\"version\":2,\"selectedShaderIndex\":0}"u8.ToArray());
        using var session = fixture.SessionFactory.Create(606, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var context = lease.CreateProbeContext(CreateAppearanceCompatibility(fixture));
        var adapter = new DarkModeEverywhereAdapter();
        var catalog = adapter.BuildCatalog(context, CancellationToken.None);
        var item = catalog.Items.Single();
        Assert(ContentSelectionValidator.TryValidateExplicit(
                catalog,
                ContentSelection.Create([item.Id], []),
                out var selection,
                out _),
            nameof(AppearanceMapsDisabledModeAndRejectsAmbiguousShaders));
        var staged = adapter.Stage(
            adapter.Plan(context, catalog, selection!, CancellationToken.None),
            CancellationToken.None);
        Assert(Encoding.UTF8.GetString(staged.Mutations.Single().AfterBytes.CopyBytes()) ==
               "{\"shaders\":[{\"id\":\"dark\"},null],\"version\":2,\"selectedShaderIndex\":1}",
            nameof(AppearanceMapsDisabledModeAndRejectsAmbiguousShaders));
    }

    var rejectedPairs = new (byte[] Source, byte[] Target)[]
    {
        (
            "{\"shaders\":[null,{\"id\":\"dark\"}],\"version\":2,\"selectedShaderIndex\":1}"u8.ToArray(),
            "{\"shaders\":[null,{\"id\":\"light\"}],\"version\":2,\"selectedShaderIndex\":0}"u8.ToArray()),
        (
            "{\"shaders\":[null,{\"id\":\"dark\"}],\"version\":2,\"selectedShaderIndex\":1}"u8.ToArray(),
            "{\"shaders\":[null,{\"id\":\"dark\"},{\"id\":\"dark\"}],\"version\":2,\"selectedShaderIndex\":0}"u8.ToArray()),
        (
            "{\"shaders\":[null,{\"id\":\"dark\"}],\"version\":2,\"selectedShaderIndex\":0}"u8.ToArray(),
            "{\"shaders\":[null,null,{\"id\":\"dark\"}],\"version\":2,\"selectedShaderIndex\":2}"u8.ToArray()),
    };
    var generation = 607L;
    foreach (var (source, target) in rejectedPairs)
    {
        using var fixture = AccessFixture.Create();
        fixture.SetInstanceRelativeFile(true, @"config\darkmodeeverywhereshaders.json", source);
        fixture.SetInstanceRelativeFile(false, @"config\darkmodeeverywhereshaders.json", target);
        using var session = fixture.SessionFactory.Create(generation++, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var catalog = new DarkModeEverywhereAdapter().BuildCatalog(
            lease.CreateProbeContext(CreateAppearanceCompatibility(fixture)),
            CancellationToken.None);
        Assert(catalog.Items.Count == 0 &&
               catalog.Diagnostics.Single().Code == ContentDiagnosticCode.UnsupportedSchema,
            nameof(AppearanceMapsDisabledModeAndRejectsAmbiguousShaders));
    }

    foreach (var (source, expected) in new (byte[] Source, ContentDiagnosticCode Expected)[]
             {
                 ([0xFF], ContentDiagnosticCode.MalformedUtf8),
                 (new byte[512 * 1024 + 1], ContentDiagnosticCode.LimitExceeded),
             })
    {
        using var fixture = AccessFixture.Create();
        fixture.SetInstanceRelativeFile(true, @"config\darkmodeeverywhereshaders.json", source);
        fixture.SetInstanceRelativeFile(
            false,
            @"config\darkmodeeverywhereshaders.json",
            "{\"shaders\":[null],\"version\":2,\"selectedShaderIndex\":0}"u8.ToArray());
        using var session = fixture.SessionFactory.Create(generation++, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var catalog = new DarkModeEverywhereAdapter().BuildCatalog(
            lease.CreateProbeContext(CreateAppearanceCompatibility(fixture)),
            CancellationToken.None);
        Assert(catalog.Items.Count == 0 && catalog.Diagnostics.Single().Code == expected,
            nameof(AppearanceMapsDisabledModeAndRejectsAmbiguousShaders));
    }
}

static void AppearanceSemanticNoOpStagesZeroMutations()
{
    using var fixture = AccessFixture.Create();
    fixture.SetInstanceRelativeFile(
        true,
        @"config\darkmodeeverywhereshaders.json",
        "{\"shaders\":[null,{\"id\":\"a\"},{\"id\":\"b\"}],\"version\":2,\"selectedShaderIndex\":1}"u8.ToArray());
    fixture.SetInstanceRelativeFile(
        false,
        @"config\darkmodeeverywhereshaders.json",
        "{\"version\":2,\"shaders\":[null,{\"id\":\"b\"},{\"id\":\"a\"}],\"selectedShaderIndex\":2}"u8.ToArray());
    using var session = fixture.SessionFactory.Create(602, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateAppearanceCompatibility(fixture));
    var adapter = new DarkModeEverywhereAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    Assert(catalog.Items.Single().Disposition == PlannedContentDisposition.Same,
        nameof(AppearanceSemanticNoOpStagesZeroMutations));
    Assert(ContentSelectionValidator.TryCreateDefaults(catalog, out var selection, out _),
        nameof(AppearanceSemanticNoOpStagesZeroMutations));
    var plan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    Assert(plan.FileChanges.Count == 0 &&
           adapter.Stage(plan, CancellationToken.None).Mutations.Count == 0 &&
           adapter.RegenerateAllowedPaths(context, CancellationToken.None)
               .SetEquals([MustContentPath(@"config\darkmodeeverywhereshaders.json")]),
        nameof(AppearanceSemanticNoOpStagesZeroMutations));
}

static void AppearanceRejectsMalformedSchemaAndIncompatibleVersions()
{
    var invalidDocuments = new byte[][]
    {
        "{\"shaders\":[null],\"version\":3,\"selectedShaderIndex\":0}"u8.ToArray(),
        "{\"shaders\":[null],\"version\":2,\"selectedShaderIndex\":0,\"selectedShaderIndex\":0}"u8.ToArray(),
        "{\"shaders\":[null],\"version\":2,\"selectedShaderIndex\":4}"u8.ToArray(),
        "{\"shaders\":[],\"version\":2,\"selectedShaderIndex\":0}"u8.ToArray(),
    };
    foreach (var invalid in invalidDocuments)
    {
        using var fixture = AccessFixture.Create();
        fixture.SetInstanceRelativeFile(true, @"config\darkmodeeverywhereshaders.json", invalid);
        fixture.SetInstanceRelativeFile(
            false,
            @"config\darkmodeeverywhereshaders.json",
            "{\"shaders\":[null],\"version\":2,\"selectedShaderIndex\":0}"u8.ToArray());
        using var session = fixture.SessionFactory.Create(603, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var catalog = new DarkModeEverywhereAdapter().BuildCatalog(
            lease.CreateProbeContext(CreateAppearanceCompatibility(fixture)),
            CancellationToken.None);
        Assert(catalog.Items.Count == 0 && catalog.Diagnostics.Count == 1,
            nameof(AppearanceRejectsMalformedSchemaAndIncompatibleVersions));
    }

    using (var fixture = AccessFixture.Create())
    {
        var valid = "{\"shaders\":[null],\"version\":2,\"selectedShaderIndex\":0}"u8.ToArray();
        fixture.SetInstanceRelativeFile(true, @"config\darkmodeeverywhereshaders.json", valid);
        fixture.SetInstanceRelativeFile(false, @"config\darkmodeeverywhereshaders.json", valid);
        using var session = fixture.SessionFactory.Create(604, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var catalog = new DarkModeEverywhereAdapter().BuildCatalog(
            lease.CreateProbeContext(CreateAppearanceCompatibility(fixture, "1.21.1-1.4.0", "2.0.0")),
            CancellationToken.None);
        Assert(catalog.Items.Count == 0 &&
               catalog.Diagnostics.Single().Code == ContentDiagnosticCode.UnsupportedModVersion,
            nameof(AppearanceRejectsMalformedSchemaAndIncompatibleVersions));
    }
}

static void AppearanceRejectsChangedSnapshotsAsStale()
{
    var source = "{\"shaders\":[null,{\"id\":\"dark\"}],\"version\":2,\"selectedShaderIndex\":1}"u8.ToArray();
    var target = "{\"shaders\":[null,{\"id\":\"dark\"}],\"version\":2,\"selectedShaderIndex\":0}"u8.ToArray();
    using var fixture = AccessFixture.Create();
    fixture.SetInstanceRelativeFile(true, @"config\darkmodeeverywhereshaders.json", source);
    fixture.SetInstanceRelativeFile(false, @"config\darkmodeeverywhereshaders.json", target);
    using var session = fixture.SessionFactory.Create(605, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateAppearanceCompatibility(fixture));
    var adapter = new DarkModeEverywhereAdapter();
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);
    var item = catalog.Items.Single();
    Assert(ContentSelectionValidator.TryValidateExplicit(
            catalog,
            ContentSelection.Create([item.Id], []),
            out var selection,
            out _),
        nameof(AppearanceRejectsChangedSnapshotsAsStale));
    fixture.SetInstanceRelativeFile(
        false,
        @"config\darkmodeeverywhereshaders.json",
        "{\"shaders\":[null,{\"id\":\"dark\"}],\"version\":2,\"selectedShaderIndex\":1}"u8.ToArray());

    var plan = adapter.Plan(context, catalog, selection!, CancellationToken.None);
    Assert(plan.FileChanges.Count == 0 &&
           plan.Diagnostics.Single().Code == ContentDiagnosticCode.StaleContext,
        nameof(AppearanceRejectsChangedSnapshotsAsStale));
}

static void UnsafeVanillaKeysFailClosedBeforeCatalogExposure()
{
    using var fixture = AccessFixture.Create();
    fixture.SetOptions(
        true,
        Encoding.UTF8.GetBytes(new string('x', 300) + ":source\n"));
    fixture.SetOptions(false, ""u8.ToArray());
    using var session = fixture.SessionFactory.Create(310, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateCompatibility(fixture));
    var adapter = new VanillaOptionsAdapter(
        new Pcl2OptionsMigrationPreviewer(fixture.AuditedCapability));
    var probe = adapter.Probe(context, CancellationToken.None);
    var catalog = adapter.BuildCatalog(context, CancellationToken.None);

    Assert(!probe.IsSupported &&
           probe.DisabledReason == ContentDiagnosticCode.UnsupportedSchema &&
           catalog.Items.Count == 0 &&
           catalog.Diagnostics.Single().Code == ContentDiagnosticCode.UnsupportedSchema,
        nameof(UnsafeVanillaKeysFailClosedBeforeCatalogExposure));
}

static ContentRelativePath MustContentPath(string value)
{
    Assert(ContentRelativePath.TryCreate(value, out var path, out _), nameof(MustContentPath));
    return path!;
}

static ModProbeLimits Beta3ModLimits() => new(
    MaximumJarFiles: 2_048,
    MaximumZipEntries: 4_096,
    MaximumEntryBytes: 2 * 1024 * 1024,
    MaximumTotalBytes: 32L * 1024 * 1024,
    MaximumArchiveBytes: 256L * 1024 * 1024,
    MaximumCentralDirectoryBytes: 32L * 1024 * 1024);

static void RawJarEnumerationIsBoundedBeforeFilter()
{
    using var fixture = AccessFixture.Create();
    fixture.AddModFile(source: true, "000-not-a-jar.txt", "ignored"u8.ToArray());
    using var session = fixture.SessionFactory.Create(202, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var limits = Beta3ModLimits() with { MaximumJarFiles = 1 };
    var result = new ModPresenceProbe().Probe(
        lease.Source,
        new HashSet<string>(["jei"], StringComparer.Ordinal),
        limits);

    Assert(result.Evidence.Count == 0 &&
           result.Diagnostics.Any(diagnostic =>
               diagnostic.Code == ContentDiagnosticCode.LimitExceeded),
        nameof(RawJarEnumerationIsBoundedBeforeFilter));
}

static void ArchiveAndCentralDirectoryLimitsFailClosed()
{
    using var fixture = AccessFixture.Create();
    using var session = fixture.SessionFactory.Create(203, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var required = new HashSet<string>(["fixture"], StringComparer.Ordinal);
    var probe = new ModPresenceProbe();

    var archiveLimited = probe.Probe(
        lease.Source,
        required,
        Beta3ModLimits() with
        {
            MaximumArchiveBytes = 1,
            MaximumCentralDirectoryBytes = 1,
        });
    var directoryLimited = probe.Probe(
        lease.Source,
        required,
        Beta3ModLimits() with { MaximumCentralDirectoryBytes = 1 });

    Assert(IsLimitOnlyFailure(archiveLimited) && IsLimitOnlyFailure(directoryLimited),
        nameof(ArchiveAndCentralDirectoryLimitsFailClosed));
}

static bool IsLimitOnlyFailure(ModPresenceResult result) =>
    result.Evidence.Count == 0 &&
    result.Diagnostics.Count > 0 &&
    result.Diagnostics.All(diagnostic =>
        diagnostic.Code == ContentDiagnosticCode.LimitExceeded);

static void ManifestIsReadOnlyForExactFileJarVersionSubstitution()
{
    using var fixture = AccessFixture.Create();
    fixture.AddModJar(
        true,
        "manifest-exact.jar",
        ("META-INF/neoforge.mods.toml", ModToml("manifestexact", "${file.jarVersion}")),
        ("META-INF/MANIFEST.MF", Manifest("1.2.3")));
    fixture.AddModJar(
        true,
        "literal.jar",
        ("META-INF/mods.toml", ModToml("literalversion", "4.5.6")),
        ("META-INF/MANIFEST.MF", [0xFF, 0xFE]));
    fixture.AddModJar(
        true,
        "wrong-substitution.jar",
        ("META-INF/neoforge.mods.toml", ModToml("wrongsub", "${file.version}")),
        ("META-INF/MANIFEST.MF", Manifest("9.9.9")));
    fixture.AddModJar(
        true,
        "duplicate-manifest.jar",
        ("META-INF/neoforge.mods.toml", ModToml("dupmanifest", "${file.jarVersion}")),
        ("META-INF/MANIFEST.MF",
            "Manifest-Version: 1.0\r\nImplementation-Version: 1.0.0\r\nImplementation-Version: 2.0.0\r\n\r\n"u8.ToArray()));

    var observing = new ZipAllowlistAuditCapability(fixture.AuditedCapability);
    var factory = new CapabilityBoundInstanceAccessFactory(fixture.SessionFactory, observing);
    using var session = fixture.SessionFactory.Create(204, fixture.Discovery);
    using var lease = OpenLeaseWithFactory(factory, fixture, session);
    var required = new HashSet<string>(
        ["manifestexact", "literalversion", "wrongsub", "dupmanifest"],
        StringComparer.Ordinal);
    var result = new ModPresenceProbe().Probe(
        lease.Source,
        required,
        Beta3ModLimits());
    var manifestReads = observing.ZipRequests
        .Where(request => request.AllowedEntryNames.SetEquals(ModFixtureConstants.ManifestOnlyName))
        .Select(request => request.ZipPath)
        .ToArray();

    Assert(result.Evidence.Count == 2 &&
           result.Evidence.Any(item => item.ModId == "manifestexact" && item.Version == "1.2.3") &&
           result.Evidence.Any(item => item.ModId == "literalversion" && item.Version == "4.5.6") &&
           manifestReads.Length == 2 &&
           manifestReads.Any(path => path.EndsWith("manifest-exact.jar", StringComparison.Ordinal)) &&
           manifestReads.Any(path => path.EndsWith("duplicate-manifest.jar", StringComparison.Ordinal)) &&
           manifestReads.All(path => !path.EndsWith("literal.jar", StringComparison.Ordinal) &&
                                     !path.EndsWith("wrong-substitution.jar", StringComparison.Ordinal)),
        nameof(ManifestIsReadOnlyForExactFileJarVersionSubstitution));
}

static byte[] ModToml(string modId, string version) => Encoding.UTF8.GetBytes(
    $"modLoader=\"javafml\"\nloaderVersion=\"[4,)\"\nlicense=\"MIT\"\n[[mods]]\nmodId=\"{modId}\"\nversion=\"{version}\"\n");

static byte[] Manifest(string version) => Encoding.UTF8.GetBytes(
    $"Manifest-Version: 1.0\r\nImplementation-Version: {version}\r\n\r\n");

static void AddFancyMenuNeoForgeMod(AccessFixture fixture, bool source) =>
    fixture.AddModJar(
        source,
        source ? "fancymenu-source.jar" : "fancymenu-target.jar",
        ("META-INF/neoforge.mods.toml", Encoding.UTF8.GetBytes(
            "modLoader=\"javafml\"\n" +
            "loaderVersion=\"[2,)\"\n" +
            "license=\"All Rights Reserved\"\n" +
            "[[mods]]\n" +
            "modId=\"fancymenu\"\n" +
            "version=\"3.9.9\"\n" +
            "displayName=\"FancyMenu\"\n" +
            "[[mixins]]\n" +
            "config=\"fancymenu.mixins.json\"\n" +
            "[[mixins]]\n" +
            "config=\"fancymenu-neoforge.mixins.json\"\n" +
            "[[accessTransformers]]\n" +
            "file=\"META-INF/accesstransformer.cfg\"\n" +
            "[[dependencies.fancymenu]]\n" +
            "modId=\"neoforge\"\n" +
            "versionRange=\"[21.1.47,)\"\n")));

static void NeoForgeTomlUnknownSyntaxFailsClosed()
{
    var unknownInlineTable = ImmutableByteBuffer.CopyFrom(
        "modLoader=\"javafml\"\nlicense=\"MIT\"\ndisplayTest={ value=\"MATCH_VERSION\" }\n[[mods]]\nmodId=\"jei\"\nversion=\"19.44.0.401\"\n"u8);
    var unknownLiteralString = ImmutableByteBuffer.CopyFrom(
        "modLoader='javafml'\nlicense=\"MIT\"\n[[mods]]\nmodId=\"jei\"\nversion=\"19.44.0.401\"\n"u8);

    Assert(!StrictModTomlParser.TryParse(unknownInlineTable, out _) &&
           !StrictModTomlParser.TryParse(unknownLiteralString, out _),
        nameof(NeoForgeTomlUnknownSyntaxFailsClosed));
}

static void DuplicateDeclarationPropertiesFailClosed()
{
    using var fixture = AccessFixture.Create();
    fixture.AddModJar(
        true,
        "duplicate-fabric.jar",
        ("fabric.mod.json",
            "{\"id\":\"jei\",\"id\":\"jei\",\"version\":\"19.44.0.401\"}"u8.ToArray()));
    fixture.AddModJar(
        false,
        "duplicate-quilt.jar",
        ("quilt.mod.json",
            "{\"quilt_loader\":{\"id\":\"jei\",\"version\":\"19.44.0.401\",\"version\":\"19.44.0.401\"}}"u8.ToArray()));
    using var session = fixture.SessionFactory.Create(205, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var required = new HashSet<string>(["jei"], StringComparer.Ordinal);
    var probe = new ModPresenceProbe();
    var source = probe.Probe(lease.Source, required, Beta3ModLimits());
    var target = probe.Probe(lease.Target, required, Beta3ModLimits());

    Assert(source.Evidence.Count == 0 && target.Evidence.Count == 0 &&
           source.Diagnostics.Any(item => item.Code == ContentDiagnosticCode.UnsupportedSchema) &&
           target.Diagnostics.Any(item => item.Code == ContentDiagnosticCode.UnsupportedSchema),
        nameof(DuplicateDeclarationPropertiesFailClosed));
}

static void CompatibilityContextUsesSameLiveLease()
{
    using var fixture = AccessFixture.Create();
    AddExactCompatibleMods(fixture);
    using var session = fixture.SessionFactory.Create(206, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = new ContentCompatibilityProbe(new ModPresenceProbe())
        .ProbeAndCreateContext(lease, Beta3ModLimits());

    Assert(context.IsOwnedBy(lease) &&
           ReferenceEquals(context.Source, lease.Source) &&
           ReferenceEquals(context.Target, lease.Target) &&
           context.Generation == lease.Generation,
        nameof(CompatibilityContextUsesSameLiveLease));

    lease.Dispose();
    Assert(!context.IsOwnedBy(lease),
        nameof(CompatibilityContextUsesSameLiveLease));
    AssertThrows<ObjectDisposedException>(
        () => new ContentCompatibilityProbe(new ModPresenceProbe())
            .ProbeAndCreateContext(lease, Beta3ModLimits()),
        nameof(CompatibilityContextUsesSameLiveLease));
}

static void CompatibilityEvidenceBaselineIsLocked()
{
    using var fixture = AccessFixture.Create();
    AddExactCompatibleMods(fixture);
    using var session = fixture.SessionFactory.Create(207, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var compatibility = new ContentCompatibilityProbe(new ModPresenceProbe())
        .ProbeAndCreateContext(lease, Beta3ModLimits())
        .Compatibility;

    Assert(compatibility.SourceMinecraftVersion == "1.21.1" &&
           compatibility.TargetMinecraftVersion == "1.21.1" &&
           compatibility.SourceModVersions.Count == 2 &&
           compatibility.TargetModVersions.Count == 2 &&
           compatibility.SourceModVersions["jei"] == "19.44.0.401" &&
           compatibility.TargetModVersions["jei"] == "19.44.0.401" &&
           compatibility.SourceModVersions["extremesoundmuffler"] == "3.56" &&
           compatibility.TargetModVersions["extremesoundmuffler"] == "3.56" &&
           compatibility.DetectedUnsupportedModIds.Count == 0,
        nameof(CompatibilityEvidenceBaselineIsLocked));
}

static void JeiCompatibleMajorLineIsAccepted()
{
    using var fixture = AccessFixture.Create();
    AddFabricMod(fixture, true, "jei", "19.44.0.401");
    AddFabricMod(fixture, false, "jei", "19.99.0.1");
    AddFabricMod(fixture, true, "extremesoundmuffler", "3.56");
    AddFabricMod(fixture, false, "extremesoundmuffler", "3.56");
    using var session = fixture.SessionFactory.Create(208, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var compatibility = new ContentCompatibilityProbe(new ModPresenceProbe())
        .ProbeAndCreateContext(lease, Beta3ModLimits())
        .Compatibility;

    Assert(compatibility.TargetModVersions["jei"] == "19.99.0.1" &&
           compatibility.DetectedUnsupportedModIds.Count == 0,
        nameof(JeiCompatibleMajorLineIsAccepted));
}

static void EsmCompatibleMajorLineIsAccepted()
{
    using var fixture = AccessFixture.Create();
    AddFabricMod(fixture, true, "jei", "19.44.0.401");
    AddFabricMod(fixture, false, "jei", "19.44.0.401");
    AddFabricMod(fixture, true, "extremesoundmuffler", "3.56");
    AddFabricMod(fixture, false, "extremesoundmuffler", "3.99");
    using var session = fixture.SessionFactory.Create(209, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var compatibility = new ContentCompatibilityProbe(new ModPresenceProbe())
        .ProbeAndCreateContext(lease, Beta3ModLimits())
        .Compatibility;

    Assert(compatibility.TargetModVersions["extremesoundmuffler"] == "3.99" &&
           compatibility.DetectedUnsupportedModIds.Count == 0,
        nameof(EsmCompatibleMajorLineIsAccepted));
}

static void OptionalUiModFamiliesAreVersionGated()
{
    using (var fixture = AccessFixture.Create())
    {
        AddExactCompatibleMods(fixture);
        AddFabricMod(fixture, true, "fancymenu", "3.9.9");
        AddFabricMod(fixture, false, "fancymenu", "3.10.0");
        AddFabricMod(fixture, true, "darkmodeeverywhere", "1.21.1-1.4.0");
        AddFabricMod(fixture, false, "darkmodeeverywhere", "1.21.1-1.5.0");
        using var session = fixture.SessionFactory.Create(218, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var compatibility = new ContentCompatibilityProbe(new ModPresenceProbe())
            .ProbeAndCreateContext(lease, Beta3ModLimits())
            .Compatibility;
        Assert(compatibility.DetectedUnsupportedModIds.Count == 0 &&
               compatibility.SourceModVersions["fancymenu"] == "3.9.9" &&
               compatibility.TargetModVersions["darkmodeeverywhere"] == "1.21.1-1.5.0",
            nameof(OptionalUiModFamiliesAreVersionGated));
    }

    using (var fixture = AccessFixture.Create())
    {
        AddExactCompatibleMods(fixture);
        AddFabricMod(fixture, true, "fancymenu", "3.9.9");
        AddFabricMod(fixture, false, "fancymenu", "4.0.0");
        AddFabricMod(fixture, true, "darkmodeeverywhere", "1.21.1-1.4.0");
        AddFabricMod(fixture, false, "darkmodeeverywhere", "1.21.1-2.0.0");
        using var session = fixture.SessionFactory.Create(219, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var compatibility = new ContentCompatibilityProbe(new ModPresenceProbe())
            .ProbeAndCreateContext(lease, Beta3ModLimits())
            .Compatibility;
        Assert(compatibility.DetectedUnsupportedModIds.SetEquals(
                ["fancymenu", "darkmodeeverywhere"]),
            nameof(OptionalUiModFamiliesAreVersionGated));
    }
}

static void DarkModeEverywhereRejectsWrongMinecraftPrefixes()
{
    foreach (var targetVersion in new[] { "1.20.1-2.0", "1.21.2-2.0" })
    {
        using var fixture = AccessFixture.Create();
        AddExactCompatibleMods(fixture);
        AddFabricMod(fixture, true, "darkmodeeverywhere", "1.21.1-1.4.0");
        AddFabricMod(fixture, false, "darkmodeeverywhere", targetVersion);
        using var session = fixture.SessionFactory.Create(220, fixture.Discovery);
        using var lease = OpenLease(fixture, session);
        var compatibility = new ContentCompatibilityProbe(new ModPresenceProbe())
            .ProbeAndCreateContext(lease, Beta3ModLimits())
            .Compatibility;

        Assert(compatibility.DetectedUnsupportedModIds.Contains("darkmodeeverywhere"),
            $"{nameof(DarkModeEverywhereRejectsWrongMinecraftPrefixes)}:{targetVersion}");
    }
}

static void EmiIsDetectedButAlwaysUnsupported()
{
    using var fixture = AccessFixture.Create();
    AddExactCompatibleMods(fixture);
    AddFabricMod(fixture, true, "emi", "1.1.22+1.21.1");
    var observing = new ZipAllowlistAuditCapability(fixture.AuditedCapability);
    var factory = new CapabilityBoundInstanceAccessFactory(fixture.SessionFactory, observing);
    using var session = fixture.SessionFactory.Create(210, fixture.Discovery);
    using var lease = OpenLeaseWithFactory(factory, fixture, session);
    var compatibility = new ContentCompatibilityProbe(new ModPresenceProbe())
        .ProbeAndCreateContext(lease, Beta3ModLimits())
        .Compatibility;

    Assert(compatibility.SourceModVersions["emi"] == "1.1.22+1.21.1" &&
           compatibility.DetectedUnsupportedModIds.SetEquals(["emi"]) &&
           observing.ZipRequests.All(request =>
               request.AllowedEntryNames.SetEquals(ModFixtureConstants.DeclarationNames) ||
               request.AllowedEntryNames.SetEquals(ModFixtureConstants.ManifestOnlyName)) &&
           observing.ZipRequests.All(request =>
               !request.AllowedEntryNames.Contains("emi.json")),
        nameof(EmiIsDetectedButAlwaysUnsupported));
}

static void UnknownTargetFormatFamilyIsUnsupported()
{
    using var fixture = AccessFixture.Create(targetMinecraftVersion: null);
    AddExactCompatibleMods(fixture);
    using var session = fixture.SessionFactory.Create(211, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var compatibility = new ContentCompatibilityProbe(new ModPresenceProbe())
        .ProbeAndCreateContext(lease, Beta3ModLimits())
        .Compatibility;

    Assert(compatibility.SourceMinecraftVersion == "1.21.1" &&
           compatibility.TargetMinecraftVersion is null &&
           compatibility.DetectedUnsupportedModIds.SetEquals(
               ["jei", "extremesoundmuffler"]),
        nameof(UnknownTargetFormatFamilyIsUnsupported));
}

static void DeclarationCaseAndTraversalAliasesFailClosed()
{
    using var fixture = AccessFixture.Create();
    var declaration = "{\"id\":\"jei\",\"version\":\"19.44.0.401\"}"u8.ToArray();
    fixture.AddModJar(
        true,
        "alias.jar",
        ("fabric.mod.json", declaration),
        ("FABRIC.MOD.JSON", declaration),
        ("../fabric.mod.json", declaration));
    using var session = fixture.SessionFactory.Create(212, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var result = new ModPresenceProbe().Probe(
        lease.Source,
        new HashSet<string>(["jei"], StringComparer.Ordinal),
        Beta3ModLimits());

    Assert(result.Evidence.Count == 0 &&
           result.Diagnostics.Any(item => item.Code == ContentDiagnosticCode.UnsupportedSchema),
        nameof(DeclarationCaseAndTraversalAliasesFailClosed));
}

static void OfficialNeoForgeDependencyTablesAreAccepted()
{
    var bytes = ImmutableByteBuffer.CopyFrom(Encoding.UTF8.GetBytes(
        "modLoader=\"javafml\"\n" +
        "loaderVersion=\"[4,)\"\n" +
        "license=\"MIT\"\n" +
        "[[mods]]\n" +
        "modId=\"jei\"\n" +
        "version=\"19.44.0.401\"\n" +
        "displayName=\"Just Enough Items\"\n" +
        "description='''\n" +
        "JEI is an item and recipe viewing mod.\n" +
        "Built for stability and performance.\n" +
        "'''\n" +
        "[[mixins]]\n" +
        "config=\"jei.mixins.json\"\n" +
        "[[mixins]]\n" +
        "config=\"jei-neoforge.mixins.json\"\n" +
        "[[dependencies.jei]]\n" +
        "modId=\"neoforge\"\n" +
        "mandatory=true\n" +
        "versionRange=\"[21.1,)\"\n" +
        "ordering=\"NONE\"\n" +
        "side=\"BOTH\"\n" +
        "[[dependencies.jei]]\n" +
        "modId=\"minecraft\"\n" +
        "mandatory=true\n" +
        "versionRange=\"[1.21.1,1.22)\"\n" +
        "ordering=\"NONE\"\n" +
        "side=\"BOTH\"\n" +
        "[modproperties.jei]\n" +
        "catalogueImageIcon=\"jei-icon.png\"\n"));

    Assert(StrictModTomlParser.TryParse(bytes, out var declaration) &&
           declaration is not null &&
           declaration.ModId == "jei" &&
           declaration.Version == "19.44.0.401" &&
           !declaration.RequiresManifestVersion,
        nameof(OfficialNeoForgeDependencyTablesAreAccepted));
}

static void DuplicateModIdsAreUnsupported()
{
    using var fixture = AccessFixture.Create();
    fixture.AddModJar(
        true,
        "jei-first.jar",
        ("fabric.mod.json", "{\"id\":\"jei\",\"version\":\"19.44.0.401\"}"u8.ToArray()));
    fixture.AddModJar(
        true,
        "jei-second.jar",
        ("quilt.mod.json",
            "{\"quilt_loader\":{\"id\":\"jei\",\"version\":\"19.44.0.400\"}}"u8.ToArray()));
    using var session = fixture.SessionFactory.Create(213, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var result = new ModPresenceProbe().Probe(
        lease.Source,
        new HashSet<string>(["jei"], StringComparer.Ordinal),
        Beta3ModLimits());

    Assert(result.Evidence.Count == 0 &&
           result.Diagnostics.Count(item =>
               item.Code == ContentDiagnosticCode.UnsupportedModVersion &&
               item.ItemId?.TechnicalKey == "jei") == 1,
        nameof(DuplicateModIdsAreUnsupported));
}

static void MalformedAndZip64ArchivesFailClosed()
{
    using var fixture = AccessFixture.Create();
    fixture.AddModFile(true, "malformed.jar", [0x50, 0x4B, 0x03, 0x04]);
    fixture.AddModFile(
        true,
        "malformed-zip64.jar",
        [
            0x50, 0x4B, 0x05, 0x06,
            0x00, 0x00, 0x00, 0x00,
            0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF,
            0x00, 0x00,
        ]);
    using var session = fixture.SessionFactory.Create(214, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var result = new ModPresenceProbe().Probe(
        lease.Source,
        new HashSet<string>(["jei"], StringComparer.Ordinal),
        Beta3ModLimits());

    Assert(result.Evidence.Count == 0 &&
           result.Diagnostics.Count(item =>
               item.Code == ContentDiagnosticCode.UnsupportedSchema) >= 2,
        nameof(MalformedAndZip64ArchivesFailClosed));
}

static void AllSixModProbeLimitsAreEnforcedBeforeRead()
{
    var cases = new[]
    {
        Beta3ModLimits() with { MaximumJarFiles = 0 },
        Beta3ModLimits() with { MaximumJarFiles = 2_049 },
        Beta3ModLimits() with { MaximumZipEntries = 0 },
        Beta3ModLimits() with { MaximumZipEntries = 65_537 },
        Beta3ModLimits() with { MaximumEntryBytes = 0 },
        Beta3ModLimits() with { MaximumEntryBytes = 2 * 1024 * 1024 + 1 },
        Beta3ModLimits() with { MaximumTotalBytes = 0 },
        Beta3ModLimits() with { MaximumTotalBytes = 32L * 1024 * 1024 + 1 },
        Beta3ModLimits() with { MaximumArchiveBytes = 0 },
        Beta3ModLimits() with { MaximumArchiveBytes = 256L * 1024 * 1024 + 1 },
        Beta3ModLimits() with { MaximumCentralDirectoryBytes = 0 },
        Beta3ModLimits() with { MaximumCentralDirectoryBytes = 32L * 1024 * 1024 + 1 },
    };
    var access = new NoReadInstanceAccess();
    var required = new HashSet<string>(["jei"], StringComparer.Ordinal);
    foreach (var limits in cases)
    {
        var result = new ModPresenceProbe().Probe(access, required, limits);
        Assert(IsLimitOnlyFailure(result),
            nameof(AllSixModProbeLimitsAreEnforcedBeforeRead));
    }

    Assert(access.EnumerateCalls == 0,
        nameof(AllSixModProbeLimitsAreEnforcedBeforeRead));
}

static void LargeUnrelatedArchiveWithinBoundPreservesRequiredEvidence()
{
    using var fixture = AccessFixture.Create();
    var unrelatedEntries = Enumerable.Range(0, 4_097)
        .Select(index => ($"payload/{index:D5}.bin", Array.Empty<byte>()))
        .ToArray();
    fixture.AddModJar(true, "000-large-unrelated.jar", unrelatedEntries);
    AddFabricMod(fixture, true, "jei", "19.44.0.401");
    using var session = fixture.SessionFactory.Create(220, fixture.Discovery);
    using var lease = OpenLease(fixture, session);

    var result = new ModPresenceProbe().Probe(
        lease.Source,
        new HashSet<string>(["jei"], StringComparer.Ordinal),
        Beta3ModLimits() with { MaximumZipEntries = 65_536 });

    Assert(result.Evidence.Count == 1 &&
           result.Evidence[0].ModId == "jei" &&
           result.Evidence[0].Version == "19.44.0.401" &&
           result.Diagnostics.All(item => item.Code != ContentDiagnosticCode.LimitExceeded),
        nameof(LargeUnrelatedArchiveWithinBoundPreservesRequiredEvidence));
}

static void RequestWideByteLimitInvalidatesPartialEvidence()
{
    using var fixture = AccessFixture.Create();
    AddFabricMod(fixture, true, "a", "1");
    AddFabricMod(fixture, true, "b", "1");
    using var session = fixture.SessionFactory.Create(215, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var result = new ModPresenceProbe().Probe(
        lease.Source,
        new HashSet<string>(["a", "b"], StringComparer.Ordinal),
        Beta3ModLimits() with
        {
            MaximumEntryBytes = 32,
            MaximumTotalBytes = 64,
        });

    Assert(result.Evidence.Count == 0 &&
           result.Diagnostics.Any(item => item.Code == ContentDiagnosticCode.LimitExceeded),
        nameof(RequestWideByteLimitInvalidatesPartialEvidence));
}

static void ModProbeCancellationIsPropagated()
{
    using var fixture = AccessFixture.Create();
    using var session = fixture.SessionFactory.Create(216, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    AssertThrows<OperationCanceledException>(
        () => new ModPresenceProbe().Probe(
            lease.Source,
            new HashSet<string>(["jei"], StringComparer.Ordinal),
            Beta3ModLimits(),
            cancellation.Token),
        nameof(ModProbeCancellationIsPropagated));
}

static void EncryptedAndCompressedBombDeclarationsFailClosed()
{
    using var fixture = AccessFixture.Create();
    fixture.AddModJar(
        true,
        "encrypted.jar",
        ("fabric.mod.json", "{\"id\":\"jei\",\"version\":\"19.44.0.401\"}"u8.ToArray()));
    fixture.MarkModJarEncrypted(true, "encrypted.jar");

    var bomb = Enumerable.Repeat((byte)' ', 2 * 1024 * 1024 + 1).ToArray();
    var prefix = "{\"id\":\"jei\",\"version\":\"19.44.0.401\"}"u8;
    prefix.CopyTo(bomb);
    fixture.AddCompressedModJar(
        true,
        "compressed-bomb.jar",
        ("fabric.mod.json", bomb));

    using var session = fixture.SessionFactory.Create(217, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var result = new ModPresenceProbe().Probe(
        lease.Source,
        new HashSet<string>(["jei"], StringComparer.Ordinal),
        Beta3ModLimits());

    Assert(result.Evidence.Count == 0 &&
           result.Diagnostics.Any(item => item.Code == ContentDiagnosticCode.LimitExceeded) &&
           result.Diagnostics.Any(item => item.Code == ContentDiagnosticCode.UnsupportedSchema),
        nameof(EncryptedAndCompressedBombDeclarationsFailClosed));
}

static void OperationalZipAndEntryLimitsFailClosed()
{
    using var fixture = AccessFixture.Create();
    using var session = fixture.SessionFactory.Create(218, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var required = new HashSet<string>(["fixture"], StringComparer.Ordinal);
    var probe = new ModPresenceProbe();
    var zipEntryLimited = probe.Probe(
        lease.Source,
        required,
        Beta3ModLimits() with { MaximumZipEntries = 1 });
    var declarationByteLimited = probe.Probe(
        lease.Source,
        required,
        Beta3ModLimits() with
        {
            MaximumEntryBytes = 8,
            MaximumTotalBytes = 64,
        });

    Assert(IsLimitOnlyFailure(zipEntryLimited) &&
           IsLimitOnlyFailure(declarationByteLimited),
        nameof(OperationalZipAndEntryLimitsFailClosed));
}

static void MissingRequiredModsAreCompatibilityUnsupported()
{
    using var fixture = AccessFixture.Create();
    using var session = fixture.SessionFactory.Create(219, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var compatibility = new ContentCompatibilityProbe(new ModPresenceProbe())
        .ProbeAndCreateContext(lease, Beta3ModLimits())
        .Compatibility;

    Assert(!compatibility.SourceModVersions.ContainsKey("jei") &&
           !compatibility.TargetModVersions.ContainsKey("jei") &&
           compatibility.DetectedUnsupportedModIds.SetEquals(
               ["jei", "extremesoundmuffler"]),
        nameof(MissingRequiredModsAreCompatibilityUnsupported));
}

static void AddExactCompatibleMods(AccessFixture fixture)
{
    AddFabricMod(fixture, true, "jei", "19.44.0.401");
    AddFabricMod(fixture, false, "jei", "19.44.0.401");
    AddFabricMod(fixture, true, "extremesoundmuffler", "3.56");
    AddFabricMod(fixture, false, "extremesoundmuffler", "3.56");
}

static void AddFabricMod(
    AccessFixture fixture,
    bool source,
    string modId,
    string version) =>
    fixture.AddModJar(
        source,
        $"{modId}.jar",
        ("fabric.mod.json", Encoding.UTF8.GetBytes(
            $"{{\"id\":\"{modId}\",\"version\":\"{version}\"}}")));

static void ContextCannotBePubliclyConstructedOrCloned()
{
    var type = typeof(ContentProbeContext);
    Assert(type.IsSealed && !type.IsRecordLike(),
        nameof(ContextCannotBePubliclyConstructedOrCloned));
    Assert(type.GetConstructors().Length == 0,
        nameof(ContextCannotBePubliclyConstructedOrCloned));
    var constructors = type.GetConstructors(
        System.Reflection.BindingFlags.Instance |
        System.Reflection.BindingFlags.NonPublic);
    Assert(constructors.Length == 1 && constructors[0].IsPrivate,
        nameof(ContextCannotBePubliclyConstructedOrCloned));
    var parameters = constructors[0].GetParameters();
    Assert(parameters.Length == 2 &&
           parameters[0].ParameterType == typeof(ContentAccessLease) &&
           parameters[1].ParameterType == typeof(AdapterCompatibilityEvidence),
        nameof(ContextCannotBePubliclyConstructedOrCloned));

    var forbiddenNames = new[] { "Handle", "Path", "Root", "Token", "Pair", "Key" };
    Assert(type.GetProperties(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic)
        .All(property =>
            property.SetMethod is null &&
            forbiddenNames.All(fragment =>
                !property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase))),
        nameof(ContextCannotBePubliclyConstructedOrCloned));
}

static void CrossSessionAndGenerationReplayRejected()
{
    using var fixture = AccessFixture.Create();
    using var foreign = AccessFixture.Create();
    using var session = fixture.SessionFactory.Create(101, fixture.Discovery);
    using var foreignSession = foreign.SessionFactory.Create(101, foreign.Discovery);

    var accepted = fixture.AccessFactory.Open(
        session,
        fixture.Source.Id,
        fixture.Target.Id,
        ContentAccessLimits.Beta3);
    Assert(accepted.IsValid && !accepted.IsStale && accepted.Lease is not null,
        nameof(CrossSessionAndGenerationReplayRejected));
    accepted.Lease!.Dispose();

    var crossSessionIds = fixture.AccessFactory.Open(
        session,
        foreign.Source.Id,
        foreign.Target.Id,
        ContentAccessLimits.Beta3);
    Assert(!crossSessionIds.IsValid && crossSessionIds.Lease is null,
        nameof(CrossSessionAndGenerationReplayRejected));

    var oldSession = fixture.SessionFactory.Create(100, fixture.Discovery);
    oldSession.Dispose();
    var disposedGeneration = fixture.AccessFactory.Open(
        oldSession,
        fixture.Source.Id,
        fixture.Target.Id,
        ContentAccessLimits.Beta3);
    Assert(!disposedGeneration.IsValid && disposedGeneration.Lease is null,
        nameof(CrossSessionAndGenerationReplayRejected));
}

static void LeaseAndContextBindingCannotBeSubstituted()
{
    using var fixture = AccessFixture.Create();
    using var session = fixture.SessionFactory.Create(102, fixture.Discovery);
    using var otherSession = fixture.SessionFactory.Create(102, fixture.Discovery);
    using var first = OpenLease(fixture, session);
    using var sibling = OpenLease(fixture, session);
    var context = first.CreateProbeContext(CreateCompatibility(fixture));

    Assert(first.IsBoundTo(session, fixture.Source.Id, fixture.Target.Id) &&
           !first.IsBoundTo(session, fixture.Target.Id, fixture.Source.Id) &&
           !first.IsBoundTo(session, fixture.Source.Id.ToUpperInvariant(), fixture.Target.Id) &&
           !first.IsBoundTo(otherSession, fixture.Source.Id, fixture.Target.Id) &&
           context.IsOwnedBy(first) &&
           !context.IsOwnedBy(sibling),
        nameof(LeaseAndContextBindingCannotBeSubstituted));

    sibling.Dispose();
    Assert(context.IsOwnedBy(first),
        nameof(LeaseAndContextBindingCannotBeSubstituted));
    first.Dispose();
    Assert(!first.IsBoundTo(session, fixture.Source.Id, fixture.Target.Id) &&
           !context.IsOwnedBy(first),
        nameof(LeaseAndContextBindingCannotBeSubstituted));
}

static ContentAccessLease OpenLease(AccessFixture fixture, DiscoverySession session)
{
    return OpenLeaseWithFactory(fixture.AccessFactory, fixture, session);
}

static ContentAccessLease OpenLeaseWithFactory(
    CapabilityBoundInstanceAccessFactory factory,
    AccessFixture fixture,
    DiscoverySession session)
{
    var result = factory.Open(
        session,
        fixture.Source.Id,
        fixture.Target.Id,
        ContentAccessLimits.Beta3);
    Assert(result.IsValid && result.Lease is not null && result.Diagnostics.Count == 0,
        nameof(OpenLease));
    return result.Lease!;
}

static void ReopenedLeaseSameSessionKeepsOpaqueIdsStable()
{
    using var fixture = AccessFixture.Create();
    using var session = fixture.SessionFactory.Create(103, fixture.Discovery);
    ContentItemId firstId;
    using (var first = OpenLease(fixture, session))
    {
        var firstContext = first.CreateProbeContext(CreateCompatibility(fixture));
        firstId = firstContext.CreateGenerationBoundOpaqueId(
            "jei",
            "server",
            "play.example.invalid:25565".AsSpan());
    }

    using var reopened = OpenLease(fixture, session);
    var reopenedContext = reopened.CreateProbeContext(CreateCompatibility(fixture));
    var secondId = reopenedContext.CreateGenerationBoundOpaqueId(
        "jei",
        "server",
        "play.example.invalid:25565".AsSpan());
    Assert(firstId == secondId &&
           firstId.IsValid &&
           firstId.AdapterId == "jei" &&
           firstId.TechnicalKey.Length == 43,
        nameof(ReopenedLeaseSameSessionKeepsOpaqueIdsStable));
}

static void DifferentSessionSameNumericGenerationRotatesOpaqueIds()
{
    using var fixture = AccessFixture.Create();
    using var firstSession = fixture.SessionFactory.Create(104, fixture.Discovery);
    using var secondSession = fixture.SessionFactory.Create(104, fixture.Discovery);
    using var firstLease = OpenLease(fixture, firstSession);
    using var secondLease = OpenLease(fixture, secondSession);
    var firstContext = firstLease.CreateProbeContext(CreateCompatibility(fixture));
    var secondContext = secondLease.CreateProbeContext(CreateCompatibility(fixture));
    var firstId = firstContext.CreateGenerationBoundOpaqueId(
        "jei", "local", "world-fixture".AsSpan());
    var secondId = secondContext.CreateGenerationBoundOpaqueId(
        "jei", "local", "world-fixture".AsSpan());

    Assert(firstId != secondId &&
           firstContext.IsOwnedBy(firstLease) &&
           !firstContext.IsOwnedBy(secondLease) &&
           !firstLease.IsBoundTo(secondSession, fixture.Source.Id, fixture.Target.Id),
        nameof(DifferentSessionSameNumericGenerationRotatesOpaqueIds));
}

static void NewGenerationRotatesOpaqueIds()
{
    using var fixture = AccessFixture.Create();
    using var firstSession = fixture.SessionFactory.Create(105, fixture.Discovery);
    using var nextSession = fixture.SessionFactory.Create(106, fixture.Discovery);
    using var firstLease = OpenLease(fixture, firstSession);
    using var nextLease = OpenLease(fixture, nextSession);
    var firstId = firstLease
        .CreateProbeContext(CreateCompatibility(fixture))
        .CreateGenerationBoundOpaqueId("jei", "local", "same-world".AsSpan());
    var nextId = nextLease
        .CreateProbeContext(CreateCompatibility(fixture))
        .CreateGenerationBoundOpaqueId("jei", "local", "same-world".AsSpan());

    Assert(firstLease.Generation == 105 &&
           nextLease.Generation == 106 &&
           firstId != nextId,
        nameof(NewGenerationRotatesOpaqueIds));
}

static void DisposedSessionInvalidatesEveryLeaseAndContext()
{
    using var fixture = AccessFixture.Create();
    var session = fixture.SessionFactory.Create(107, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    var context = lease.CreateProbeContext(CreateCompatibility(fixture));
    _ = context.CreateGenerationBoundOpaqueId(
        "jei", "server", "dispose-fixture".AsSpan());
    Assert(ContentRelativePath.TryCreate("options.txt", out var optionsPath, out _),
        nameof(DisposedSessionInvalidatesEveryLeaseAndContext));

    var authorityTypes = new[] { typeof(ContentAccessLease), typeof(ContentProbeContext) };
    Assert(authorityTypes.All(type => type
            .GetFields(System.Reflection.BindingFlags.Instance |
                       System.Reflection.BindingFlags.Public |
                       System.Reflection.BindingFlags.NonPublic)
            .All(field => field.FieldType != typeof(byte[]) &&
                          !field.Name.Contains("key", StringComparison.OrdinalIgnoreCase))),
        nameof(DisposedSessionInvalidatesEveryLeaseAndContext));

    session.Dispose();
    Assert(!lease.IsBoundTo(session, fixture.Source.Id, fixture.Target.Id) &&
           !context.IsOwnedBy(lease),
        nameof(DisposedSessionInvalidatesEveryLeaseAndContext));
    AssertThrows<ObjectDisposedException>(
        () => context.CreateGenerationBoundOpaqueId(
            "jei", "server", "dispose-fixture".AsSpan()),
        nameof(DisposedSessionInvalidatesEveryLeaseAndContext));
    AssertThrows<ObjectDisposedException>(
        () => lease.Source.Read(
            optionsPath!,
            new ContentReadLimits(1024),
            CancellationToken.None),
        nameof(DisposedSessionInvalidatesEveryLeaseAndContext));

    var reopen = fixture.AccessFactory.Open(
        session,
        fixture.Source.Id,
        fixture.Target.Id,
        ContentAccessLimits.Beta3);
    Assert(!reopen.IsValid && reopen.Lease is null,
        nameof(DisposedSessionInvalidatesEveryLeaseAndContext));
}

static void RevalidatesImmediatelyBeforeOpening()
{
    using var fixture = AccessFixture.Create();
    using var session = fixture.SessionFactory.Create(108, fixture.Discovery);
    var before = fixture.AuditedCapability.AuditLog.Count;
    using var lease = OpenLease(fixture, session);
    var accessEvents = fixture.AuditedCapability.AuditLog.Skip(before).ToArray();
    var firstRootOpen = Array.FindIndex(
        accessEvents,
        entry => entry.Operation == "OpenRoot");
    Assert(firstRootOpen > 0 &&
           accessEvents.Take(firstRootOpen).Any(entry => entry.Operation == "OpenDirectory") &&
           accessEvents.Take(firstRootOpen).Any(entry => entry.Operation == "InspectVolume") &&
           accessEvents[^4].Operation == "OpenRoot" &&
           accessEvents[^3].Operation == "InspectVolume" &&
           accessEvents[^2].Operation == "OpenRoot" &&
           accessEvents[^1].Operation == "InspectVolume",
        nameof(RevalidatesImmediatelyBeforeOpening));

    using var cancellation = new CancellationTokenSource();
    var cancelingCapability = new CancelAfterSourceOpenCapability(
        fixture.AuditedCapability,
        cancellation);
    var cancelingFactory = new CapabilityBoundInstanceAccessFactory(
        fixture.SessionFactory,
        cancelingCapability);
    var canceled = cancelingFactory.Open(
        session,
        fixture.Source.Id,
        fixture.Target.Id,
        ContentAccessLimits.Beta3,
        cancellation.Token);
    Assert(!canceled.IsValid && canceled.Lease is null &&
           cancelingCapability.SourceOpenCount == 1 &&
           cancelingCapability.TargetOpenCount == 0,
        nameof(RevalidatesImmediatelyBeforeOpening));
}

static void RootIdentityDriftRejected()
{
    using var fixture = AccessFixture.Create();
    using var session = fixture.SessionFactory.Create(109, fixture.Discovery);
    foreach (var mutation in Enum.GetValues<RootOpenMutation>())
    {
        var altered = new RootOverrideCapability(fixture.AuditedCapability, mutation);
        var factory = new CapabilityBoundInstanceAccessFactory(
            fixture.SessionFactory,
            altered);
        var result = factory.Open(
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            ContentAccessLimits.Beta3);
        Assert(!result.IsValid && result.IsStale && result.Lease is null,
            $"{nameof(RootIdentityDriftRejected)}:{mutation}");
    }

    var sameRoot = fixture.AccessFactory.Open(
        session,
        fixture.Source.Id,
        fixture.Source.Id,
        ContentAccessLimits.Beta3);
    Assert(!sameRoot.IsValid && !sameRoot.IsStale && sameRoot.Lease is null,
        nameof(RootIdentityDriftRejected));
}

static void RetainedHandlesBackEveryRead()
{
    using var fixture = AccessFixture.Create();
    using var session = fixture.SessionFactory.Create(110, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    Assert(ContentRelativePath.TryCreate("options.txt", out var optionsPath, out _),
        nameof(RetainedHandlesBackEveryRead));
    var before = fixture.AuditedCapability.AuditLog.Count;
    var initial = lease.Source.Read(
        optionsPath!,
        new ContentReadLimits(1024),
        CancellationToken.None);
    fixture.MoveSourceRoot();
    var afterRename = lease.Source.Read(
        optionsPath!,
        new ContentReadLimits(1024),
        CancellationToken.None);
    var readEvents = fixture.AuditedCapability.AuditLog.Skip(before).ToArray();

    Assert(initial.Exists && afterRename.Exists &&
           initial.RelativePath.Equals(optionsPath) &&
           afterRename.RelativePath.Equals(optionsPath) &&
           initial.Sha256 == afterRename.Sha256 &&
           readEvents.Count(entry => entry.Operation == "ReadFile") == 2 &&
           readEvents.All(entry => entry.Operation != "OpenRoot"),
        nameof(RetainedHandlesBackEveryRead));
}

static void ReadsAreBoundToRequestedRelativePath()
{
    using var fixture = AccessFixture.Create();
    using var session = fixture.SessionFactory.Create(111, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    Assert(ContentRelativePath.TryCreate("options.txt", out var optionsPath, out _),
        nameof(ReadsAreBoundToRequestedRelativePath));
    Assert(ContentRelativePath.TryCreate("config", out var configPath, out _),
        nameof(ReadsAreBoundToRequestedRelativePath));
    Assert(ContentRelativePath.TryCreate("mods\\fixture.jar", out var zipPath, out _),
        nameof(ReadsAreBoundToRequestedRelativePath));

    var file = lease.Source.Read(
        optionsPath!,
        new ContentReadLimits(4096),
        CancellationToken.None);
    var callerBytes = file.Bytes.CopyBytes();
    callerBytes[0] ^= 0xFF;
    Assert(file.RelativePath.Equals(optionsPath) &&
           !file.Bytes.CopyBytes().SequenceEqual(callerBytes),
        nameof(ReadsAreBoundToRequestedRelativePath));

    var entries = lease.Source.Enumerate(
        configPath!,
        new ContentEnumerationLimits(8),
        CancellationToken.None);
    Assert(entries.Count == 1 &&
           entries[0].RelativePath.Value == "config\\child.txt" &&
           entries is not IList<ContentDirectoryEntry>,
        nameof(ReadsAreBoundToRequestedRelativePath));

    var allowed = new HashSet<string>(
        ["fabric.mod.json", "META-INF/MANIFEST.MF"],
        StringComparer.Ordinal);
    var archive = lease.Source.ReadZipEntries(
        zipPath!,
        allowed,
        new ContentZipReadLimits(8, 4096, 8192, 64 * 1024, 8192),
        CancellationToken.None);
    allowed.Clear();
    string[] expectedEntryNames = ["META-INF/MANIFEST.MF", "fabric.mod.json"];
    Assert(archive.Count == 2 &&
           archive.Keys.Order(StringComparer.Ordinal).SequenceEqual(
               expectedEntryNames) &&
           archive.Values.All(snapshot => snapshot.RelativePath.Equals(zipPath)) &&
           archive is not IDictionary<string, ContentFileSnapshot>,
        nameof(ReadsAreBoundToRequestedRelativePath));
}

static void MissingAncestorReadsAsMissing()
{
    using var fixture = AccessFixture.Create();
    using var session = fixture.SessionFactory.Create(117, fixture.Discovery);
    using var lease = OpenLease(fixture, session);
    Assert(ContentRelativePath.TryCreate(
            @"ESM\soundsMuffled.dat",
            out var missingPath,
            out _),
        nameof(MissingAncestorReadsAsMissing));

    var snapshot = lease.Target.Read(
        missingPath!,
        new ContentReadLimits(4 * 1024 * 1024),
        CancellationToken.None);

    Assert(!snapshot.Exists &&
           snapshot.RelativePath.Equals(missingPath) &&
           snapshot.Length == 0 &&
           snapshot.Bytes.Length == 0 &&
           snapshot.LastWriteTimeUtc == DateTimeOffset.UnixEpoch &&
           snapshot.WindowsFileAttributes == 0 &&
           snapshot.Identity is null,
        nameof(MissingAncestorReadsAsMissing));
}

static void VerifyRejectsMissingExtraOrRelabeledRereads()
{
    VerificationRereadsAreExactlyPathBound();
    var (_, change) = CreateActionableChange(
        "jei",
        "unicode-verify",
        "config\\caf\u00E9.json");
    var staged = ContentStageResult.Create(
        "jei",
        [StagedFileMutation.Create(change, "after"u8)]);
    Assert(ContentRelativePath.TryCreate(
            "config\\cafe\u0301.json",
            out var alias,
            out _),
        nameof(VerifyRejectsMissingExtraOrRelabeledRereads));
    var relabeled = ContentFileSnapshot.Create(
        alias!,
        true,
        "after"u8,
        DateTimeOffset.UnixEpoch,
        0,
        new ContentFileIdentity(1, 2, 3));
    Assert(!ContentPlanCoordinator.TryBindVerificationRereads(
               staged,
               [relabeled],
               out var bound,
               out var rejection) &&
           bound.Count == 0 && rejection is not null,
        nameof(VerifyRejectsMissingExtraOrRelabeledRereads));
}

static void SharedBudgetFailsBeforeUnboundedMaterialization()
{
    using (var fixture = AccessFixture.Create())
    using (var session = fixture.SessionFactory.Create(112, fixture.Discovery))
    {
        var tightened = new ContentAccessLimits(4, 2, 2, 100);
        var opened = fixture.AccessFactory.Open(
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            tightened);
        Assert(opened.IsValid && opened.Lease is not null,
            nameof(SharedBudgetFailsBeforeUnboundedMaterialization));
        using var lease = opened.Lease!;
        Assert(ContentRelativePath.TryCreate("options.txt", out var optionsPath, out _),
            nameof(SharedBudgetFailsBeforeUnboundedMaterialization));
        _ = lease.Source.Read(
            optionsPath!,
            new ContentReadLimits(60),
            CancellationToken.None);
        var beforeRejectedTargetRead = fixture.AuditedCapability.AuditLog.Count;
        AssertThrows<CapabilityLimitExceededException>(
            () => lease.Target.Read(
                optionsPath!,
                new ContentReadLimits(60),
                CancellationToken.None),
            nameof(SharedBudgetFailsBeforeUnboundedMaterialization));
        Assert(fixture.AuditedCapability.AuditLog.Count == beforeRejectedTargetRead,
            nameof(SharedBudgetFailsBeforeUnboundedMaterialization));

        Assert(ContentRelativePath.TryCreate("config", out var configPath, out _),
            nameof(SharedBudgetFailsBeforeUnboundedMaterialization));
        _ = lease.Source.Enumerate(
            configPath!,
            new ContentEnumerationLimits(1),
            CancellationToken.None);
        var beforeRejectedTargetEnumeration = fixture.AuditedCapability.AuditLog.Count;
        AssertThrows<CapabilityLimitExceededException>(
            () => lease.Target.Enumerate(
                configPath!,
                new ContentEnumerationLimits(2),
                CancellationToken.None),
            nameof(SharedBudgetFailsBeforeUnboundedMaterialization));
        Assert(fixture.AuditedCapability.AuditLog.Count == beforeRejectedTargetEnumeration,
            nameof(SharedBudgetFailsBeforeUnboundedMaterialization));
    }

    using (var fixture = AccessFixture.Create())
    using (var session = fixture.SessionFactory.Create(113, fixture.Discovery))
    {
        var countingCapability = new CountingResultCapability(fixture.AuditedCapability);
        var factory = new CapabilityBoundInstanceAccessFactory(
            fixture.SessionFactory,
            countingCapability);
        var opened = factory.Open(
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            new ContentAccessLimits(4, 2, 4, 16 * 1024));
        Assert(opened.IsValid && opened.Lease is not null,
            nameof(SharedBudgetFailsBeforeUnboundedMaterialization));
        using var lease = opened.Lease!;
        Assert(ContentRelativePath.TryCreate("config", out var configPath, out _),
            nameof(SharedBudgetFailsBeforeUnboundedMaterialization));
        Assert(ContentRelativePath.TryCreate("mods\\fixture.jar", out var zipPath, out _),
            nameof(SharedBudgetFailsBeforeUnboundedMaterialization));
        AssertThrows<CapabilityLimitExceededException>(
            () => lease.Source.Enumerate(
                configPath!,
                new ContentEnumerationLimits(1),
                CancellationToken.None),
            nameof(SharedBudgetFailsBeforeUnboundedMaterialization));
        Assert(countingCapability.EnumerationObservations == 2,
            nameof(SharedBudgetFailsBeforeUnboundedMaterialization));
        AssertThrows<CapabilityLimitExceededException>(
            () => lease.Source.ReadZipEntries(
                zipPath!,
                new HashSet<string>(["fabric.mod.json"], StringComparer.Ordinal),
                new ContentZipReadLimits(2, 4096, 4096, 64 * 1024, 8192),
                CancellationToken.None),
            nameof(SharedBudgetFailsBeforeUnboundedMaterialization));
        Assert(countingCapability.ZipObservations == 3,
            nameof(SharedBudgetFailsBeforeUnboundedMaterialization));
    }

    using (var fixture = AccessFixture.Create())
    using (var session = fixture.SessionFactory.Create(114, fixture.Discovery))
    {
        var before = fixture.AuditedCapability.AuditLog.Count;
        foreach (var invalid in new[]
                 {
                     new ContentAccessLimits(0, 1, 1, 1),
                     new ContentAccessLimits(20_001, 512, 500_000, 512L * 1024 * 1024),
                     new ContentAccessLimits(1, 513, 1, 1),
                     new ContentAccessLimits(1, 1, 500_001, 1),
                     new ContentAccessLimits(1, 1, 1, 512L * 1024 * 1024 + 1),
                 })
        {
            var rejected = fixture.AccessFactory.Open(
                session,
                fixture.Source.Id,
                fixture.Target.Id,
                invalid);
            Assert(!rejected.IsValid && rejected.Lease is null,
                nameof(SharedBudgetFailsBeforeUnboundedMaterialization));
        }

        Assert(fixture.AuditedCapability.AuditLog.Count == before,
            nameof(SharedBudgetFailsBeforeUnboundedMaterialization));
    }
}

static void PartialOpenFailureDisposesEveryHandle()
{
    using var fixture = AccessFixture.Create();
    using var session = fixture.SessionFactory.Create(115, fixture.Discovery);
    foreach (var failurePoint in Enum.GetValues<TrackedOpenFailurePoint>())
    {
        using var tracking = new TrackingFailureCapability(
            fixture.AuditedCapability,
            failurePoint);
        var factory = new CapabilityBoundInstanceAccessFactory(
            fixture.SessionFactory,
            tracking);
        var result = factory.Open(
            session,
            fixture.Source.Id,
            fixture.Target.Id,
            ContentAccessLimits.Beta3,
            tracking.CancellationToken);
        result.Lease?.Dispose();
        Assert(!result.IsValid && result.Lease is null &&
               tracking.LiveHandleCount == 0 &&
               tracking.SourceOpenCount == 1,
            $"{nameof(PartialOpenFailureDisposesEveryHandle)}:{failurePoint}");
    }
}

static void DisposedLeaseRejectsAllUse()
{
    using var fixture = AccessFixture.Create();
    using var session = fixture.SessionFactory.Create(116, fixture.Discovery);
    using var tracking = new TrackingFailureCapability(
        fixture.AuditedCapability,
        failurePoint: null);
    var factory = new CapabilityBoundInstanceAccessFactory(
        fixture.SessionFactory,
        tracking);
    var opened = factory.Open(
        session,
        fixture.Source.Id,
        fixture.Target.Id,
        ContentAccessLimits.Beta3);
    Assert(opened.IsValid && opened.Lease is not null && tracking.LiveHandleCount == 2,
        nameof(DisposedLeaseRejectsAllUse));
    var lease = opened.Lease!;
    var oldAccess = lease.Source;
    var oldContext = lease.CreateProbeContext(CreateCompatibility(fixture));
    var before = oldContext.CreateGenerationBoundOpaqueId(
        "jei", "server", "stable-after-lease-dispose".AsSpan());
    Assert(ContentRelativePath.TryCreate("options.txt", out var optionsPath, out _),
        nameof(DisposedLeaseRejectsAllUse));

    lease.Dispose();
    lease.Dispose();
    Assert(tracking.LiveHandleCount == 0 &&
           session.IsActive &&
           !lease.IsBoundTo(session, fixture.Source.Id, fixture.Target.Id) &&
           !oldContext.IsOwnedBy(lease),
        nameof(DisposedLeaseRejectsAllUse));
    AssertThrows<ObjectDisposedException>(
        () => oldAccess.Read(
            optionsPath!,
            new ContentReadLimits(1024),
            CancellationToken.None),
        nameof(DisposedLeaseRejectsAllUse));
    AssertThrows<ObjectDisposedException>(
        () => oldContext.CreateGenerationBoundOpaqueId(
            "jei", "server", "stable-after-lease-dispose".AsSpan()),
        nameof(DisposedLeaseRejectsAllUse));
    AssertThrows<ObjectDisposedException>(
        () => lease.CreateProbeContext(CreateCompatibility(fixture)),
        nameof(DisposedLeaseRejectsAllUse));

    var reopened = factory.Open(
        session,
        fixture.Source.Id,
        fixture.Target.Id,
        ContentAccessLimits.Beta3);
    Assert(reopened.IsValid && reopened.Lease is not null && tracking.LiveHandleCount == 2,
        nameof(DisposedLeaseRejectsAllUse));
    using var reopenedLease = reopened.Lease!;
    var after = reopenedLease
        .CreateProbeContext(CreateCompatibility(fixture))
        .CreateGenerationBoundOpaqueId(
            "jei", "server", "stable-after-lease-dispose".AsSpan());
    Assert(before == after,
        nameof(DisposedLeaseRejectsAllUse));
}

static void CapabilityAuditContainsNoMutationOrOutsideRootAccess()
{
    using var fixture = AccessFixture.Create();
    using var session = fixture.SessionFactory.Create(117, fixture.Discovery);
    using (var lease = OpenLease(fixture, session))
    {
        Assert(ContentRelativePath.TryCreate("options.txt", out var optionsPath, out _),
            nameof(CapabilityAuditContainsNoMutationOrOutsideRootAccess));
        Assert(ContentRelativePath.TryCreate("config", out var configPath, out _),
            nameof(CapabilityAuditContainsNoMutationOrOutsideRootAccess));
        Assert(ContentRelativePath.TryCreate("mods\\fixture.jar", out var zipPath, out _),
            nameof(CapabilityAuditContainsNoMutationOrOutsideRootAccess));
        _ = lease.Source.Read(
            optionsPath!,
            new ContentReadLimits(4096),
            CancellationToken.None);
        _ = lease.Target.Read(
            optionsPath!,
            new ContentReadLimits(4096),
            CancellationToken.None);
        _ = lease.Source.Enumerate(
            configPath!,
            new ContentEnumerationLimits(8),
            CancellationToken.None);
        _ = lease.Source.ReadZipEntries(
            zipPath!,
            new HashSet<string>(
                ["fabric.mod.json", "META-INF/MANIFEST.MF"],
                StringComparer.Ordinal),
            new ContentZipReadLimits(8, 4096, 8192, 64 * 1024, 8192),
            CancellationToken.None);
    }

    var audit = fixture.AuditedCapability.AuditLog;
    var summary = CapabilityAuditSummary.From(audit);
    var mutationWords = new[] { "Write", "Delete", "Create", "Rename", "Replace" };
    Assert(summary.EventCount > 0 &&
           summary.WriteCount == 0 &&
           summary.RealRootAccessCount == 0 &&
           audit.All(entry => !entry.IsMutation) &&
           audit.All(entry => entry.WasRejected ||
                              entry.RootId is not null &&
                              fixture.AuditedCapability.AllowedRootIds.Contains(entry.RootId.Value)) &&
           audit.All(entry => mutationWords.All(word =>
               !entry.Operation.Contains(word, StringComparison.OrdinalIgnoreCase))),
        nameof(CapabilityAuditContainsNoMutationOrOutsideRootAccess));
}

static AdapterCompatibilityEvidence CreateCompatibility(AccessFixture fixture) =>
    AdapterCompatibilityEvidence.Create(
        fixture.Source.MinecraftVersion,
        fixture.Target.MinecraftVersion,
        [],
        [],
        []);

static AdapterCompatibilityEvidence CreateVanillaCompatibility(
    AccessFixture fixture,
    string sourceFancyMenuVersion,
    string targetFancyMenuVersion) =>
    AdapterCompatibilityEvidence.Create(
        fixture.Source.MinecraftVersion,
        fixture.Target.MinecraftVersion,
        [new KeyValuePair<string, string>("fancymenu", sourceFancyMenuVersion)],
        [new KeyValuePair<string, string>("fancymenu", targetFancyMenuVersion)],
        []);

static AdapterCompatibilityEvidence CreateAppearanceCompatibility(
    AccessFixture fixture,
    string sourceVersion = "1.21.1-1.4.0",
    string targetVersion = "1.21.1-1.4.0") =>
    AdapterCompatibilityEvidence.Create(
        fixture.Source.MinecraftVersion,
        fixture.Target.MinecraftVersion,
        [new KeyValuePair<string, string>("darkmodeeverywhere", sourceVersion)],
        [new KeyValuePair<string, string>("darkmodeeverywhere", targetVersion)],
        []);

static AdapterCompatibilityEvidence CreateJeiCompatibility(
    AccessFixture fixture,
    string sourceJeiVersion = "19.44.0.401",
    string targetJeiVersion = "19.44.0.401") =>
    AdapterCompatibilityEvidence.Create(
        fixture.Source.MinecraftVersion,
        fixture.Target.MinecraftVersion,
        [new KeyValuePair<string, string>("jei", sourceJeiVersion)],
        [new KeyValuePair<string, string>("jei", targetJeiVersion)],
        []);

static AdapterCompatibilityEvidence CreateEsmCompatibility(
    AccessFixture fixture,
    string? sourceEsmVersion = "3.56",
    string? targetEsmVersion = "3.56")
{
    var source = sourceEsmVersion is null
        ? Array.Empty<KeyValuePair<string, string>>()
        : [new KeyValuePair<string, string>("extremesoundmuffler", sourceEsmVersion)];
    var target = targetEsmVersion is null
        ? Array.Empty<KeyValuePair<string, string>>()
        : [new KeyValuePair<string, string>("extremesoundmuffler", targetEsmVersion)];
    return AdapterCompatibilityEvidence.Create(
        fixture.Source.MinecraftVersion,
        fixture.Target.MinecraftVersion,
        source,
        target,
        []);
}

static void ImmutableByteBufferCopiesInputAndEveryReturnedCopy()
{
    var input = new byte[] { 0x01, 0x02, 0xFE, 0xFF };
    var expected = input.ToArray();
    var expectedHash = Convert.ToHexString(SHA256.HashData(expected));
    var buffer = ImmutableByteBuffer.CopyFrom(input);

    input[0] = 0x99;
    var first = buffer.CopyBytes();
    var second = buffer.CopyBytes();
    first[1] = 0x88;
    second[2] = 0x77;

    Assert(buffer.Length == expected.Length, nameof(ImmutableByteBufferCopiesInputAndEveryReturnedCopy));
    Assert(buffer.Sha256 == expectedHash, nameof(ImmutableByteBufferCopiesInputAndEveryReturnedCopy));
    Assert(buffer.CopyBytes().SequenceEqual(expected), nameof(ImmutableByteBufferCopiesInputAndEveryReturnedCopy));
    Assert(!ReferenceEquals(first, second), nameof(ImmutableByteBufferCopiesInputAndEveryReturnedCopy));
}

static void ContentCollectionsBoundBeforeCopyAndDetachCallerInputs()
{
    Assert(ContentItemId.TryCreate("vanilla", "lang", out var id),
        nameof(ContentCollectionsBoundBeforeCopyAndDetachCallerInputs));
    var item = ContentCatalogItem.Create(
        id,
        "语言",
        "Minecraft 语言设置",
        PlannedContentDisposition.Same,
        isSelectable: false,
        isSelectedByDefault: false,
        ConflictResolution.Skip,
        disabledReason: null);
    var diagnostic = ContentDiagnostic.Create(
        ContentDiagnosticCode.MissingTargetData,
        ContentDiagnosticSeverity.Information,
        "vanilla",
        id,
        safeCount: 1);
    var mutableItems = new List<ContentCatalogItem> { item };
    var mutableDiagnostics = new List<ContentDiagnostic> { diagnostic };
    var catalog = ContentCatalog.Create("vanilla", mutableItems, mutableDiagnostics);

    var mutableSelected = new List<ContentItemId> { id };
    var mutableResolutions = new List<KeyValuePair<ContentItemId, ConflictResolution>>
    {
        new(id, ConflictResolution.KeepTarget),
    };
    var selection = ContentSelection.Create(mutableSelected, mutableResolutions);

    var mutableModVersions = new List<KeyValuePair<string, string>>
    {
        new("jei", "19.44.0.401"),
    };
    var mutableUnsupportedMods = new List<string> { "emi" };
    var compatibility = AdapterCompatibilityEvidence.Create(
        "1.21.1",
        "1.21.1",
        mutableModVersions,
        mutableModVersions,
        mutableUnsupportedMods);

    mutableItems.Clear();
    mutableDiagnostics.Clear();
    mutableSelected.Clear();
    mutableResolutions.Clear();
    mutableModVersions.Clear();
    mutableUnsupportedMods.Clear();

    Assert(catalog.Items.Count == 1 && catalog.Diagnostics.Count == 1,
        nameof(ContentCollectionsBoundBeforeCopyAndDetachCallerInputs));
    Assert(selection.SelectedItems.Count == 1 && selection.ConflictResolutions.Count == 1,
        nameof(ContentCollectionsBoundBeforeCopyAndDetachCallerInputs));
    Assert(compatibility.SourceModVersions.Count == 1 &&
           compatibility.TargetModVersions.Count == 1 &&
           compatibility.DetectedUnsupportedModIds.Count == 1,
        nameof(ContentCollectionsBoundBeforeCopyAndDetachCallerInputs));
    Assert(catalog.Items is not IList<ContentCatalogItem> catalogList || catalogList.IsReadOnly,
        nameof(ContentCollectionsBoundBeforeCopyAndDetachCallerInputs));
    Assert(selection.SelectedItems is not ISet<ContentItemId>,
        nameof(ContentCollectionsBoundBeforeCopyAndDetachCallerInputs));
    Assert(selection.ConflictResolutions is not IDictionary<ContentItemId, ConflictResolution> dictionary ||
           dictionary.IsReadOnly,
        nameof(ContentCollectionsBoundBeforeCopyAndDetachCallerInputs));

    var counting = new CountingEnumerable<ContentCatalogItem>(
        ContentContractLimits.MaximumCatalogItems + 2,
        _ => item);
    AssertThrows<ArgumentException>(
        () => ContentCatalog.Create("vanilla", counting, Array.Empty<ContentDiagnostic>()),
        nameof(ContentCollectionsBoundBeforeCopyAndDetachCallerInputs));
    Assert(counting.Observations == ContentContractLimits.MaximumCatalogItems + 1,
        nameof(ContentCollectionsBoundBeforeCopyAndDetachCallerInputs));
}

static void ContentDiagnosticHasNoFreeFormOrPathSurface()
{
    var type = typeof(ContentDiagnostic);
    var publicProperties = type.GetProperties(
        System.Reflection.BindingFlags.Instance |
        System.Reflection.BindingFlags.Public);
    var forbiddenFragments = new[] { "Message", "Detail", "Path", "Exception", "Scope", "Server", "World" };
    Assert(publicProperties.All(property =>
            property.SetMethod is null &&
            forbiddenFragments.All(fragment =>
                !property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase))),
        nameof(ContentDiagnosticHasNoFreeFormOrPathSurface));
    Assert(type.GetConstructors().Length == 0,
        nameof(ContentDiagnosticHasNoFreeFormOrPathSurface));
    AssertThrows<ArgumentOutOfRangeException>(
        () => ContentDiagnostic.Create(
            (ContentDiagnosticCode)int.MaxValue,
            ContentDiagnosticSeverity.Error,
            "vanilla"),
        nameof(ContentDiagnosticHasNoFreeFormOrPathSurface));
    AssertThrows<ArgumentOutOfRangeException>(
        () => ContentDiagnostic.Create(
            ContentDiagnosticCode.CapabilityRejected,
            (ContentDiagnosticSeverity)int.MaxValue,
            "vanilla"),
        nameof(ContentDiagnosticHasNoFreeFormOrPathSurface));
}

static void ContentRelativePathRejectsAbsoluteTraversalAdsAndReservedNames()
{
    var rejected = new[]
    {
        @"C:\config\a.json",
        @"C:config\a.json",
        @"\\server\share\a.json",
        @"\\?\C:\config\a.json",
        @"\\.\C:\config\a.json",
        @"\rooted\a.json",
        @"config\.\a.json",
        @"config\..\a.json",
        @"config\\a.json",
        @"config\a.json:stream",
        "config\\a\u0001.json",
        @"config\a<.json",
        @"config\trailing. ",
        @"config\CON",
        @"config\con.txt",
        @"config\CONIN$",
        @"config\COM1.json",
        @"config\COM¹.json",
        @"config\LPT²",
        new string('a', 256),
    };
    foreach (var candidate in rejected)
    {
        Assert(!ContentRelativePath.TryCreate(candidate, out var path, out var rejection) &&
               path is null &&
               rejection == ContentDiagnosticCode.InvalidRelativePath,
            nameof(ContentRelativePathRejectsAbsoluteTraversalAdsAndReservedNames));
    }

    var exactTotal = string.Join('\\', Enumerable.Repeat("a", 16_384));
    Assert(exactTotal.Length == 32_767,
        nameof(ContentRelativePathRejectsAbsoluteTraversalAdsAndReservedNames));
    Assert(ContentRelativePath.TryCreate(string.Empty, out var root, out var rootRejection) &&
           root is not null && root.Value.Length == 0 && rootRejection is null,
        nameof(ContentRelativePathRejectsAbsoluteTraversalAdsAndReservedNames));
    Assert(ContentRelativePath.TryCreate("config/jei/世界/bookmarks.json", out var valid, out var validRejection) &&
           valid is not null && valid.Value == @"config\jei\世界\bookmarks.json" && validRejection is null,
        nameof(ContentRelativePathRejectsAbsoluteTraversalAdsAndReservedNames));
    Assert(ContentRelativePath.TryCreate(new string('a', 255), out _, out _),
        nameof(ContentRelativePathRejectsAbsoluteTraversalAdsAndReservedNames));
    Assert(ContentRelativePath.TryCreate(exactTotal, out _, out _),
        nameof(ContentRelativePathRejectsAbsoluteTraversalAdsAndReservedNames));
    Assert(!ContentRelativePath.TryCreate(exactTotal + "a", out _, out var totalRejection) &&
           totalRejection == ContentDiagnosticCode.InvalidRelativePath,
        nameof(ContentRelativePathRejectsAbsoluteTraversalAdsAndReservedNames));
}

static void UnknownSelectionIsRejectedWithCatalogPresent()
{
    Assert(ContentItemId.TryCreate("vanilla", "lang", out var addId),
        nameof(UnknownSelectionIsRejectedWithCatalogPresent));
    Assert(ContentItemId.TryCreate("vanilla", "sound", out var conflictId),
        nameof(UnknownSelectionIsRejectedWithCatalogPresent));
    Assert(ContentItemId.TryCreate("vanilla", "resourcePacks", out var protectedId),
        nameof(UnknownSelectionIsRejectedWithCatalogPresent));
    Assert(ContentItemId.TryCreate("vanilla", "unknown", out var unknownId),
        nameof(UnknownSelectionIsRejectedWithCatalogPresent));
    Assert(ContentItemId.TryCreate("jei", "lang", out var crossAdapterId),
        nameof(UnknownSelectionIsRejectedWithCatalogPresent));
    var catalog = ContentCatalog.Create(
        "vanilla",
        [
            ContentCatalogItem.Create(
                addId, "语言", "采用来源语言", PlannedContentDisposition.Add,
                true, false, ConflictResolution.Skip, null),
            ContentCatalogItem.Create(
                conflictId, "静音", "来源与目标不同", PlannedContentDisposition.Conflict,
                true, false, ConflictResolution.KeepTarget, null),
            ContentCatalogItem.Create(
                protectedId, "资源包", "整合包保护", PlannedContentDisposition.Protected,
                false, false, ConflictResolution.Skip, ContentDiagnosticCode.CapabilityRejected),
        ],
        []);

    var valid = ContentSelection.Create(
        [addId, conflictId],
        [new(conflictId, ConflictResolution.UseSource)]);
    Assert(ContentSelectionValidator.TryValidateExplicit(catalog, valid, out var accepted, out var noRejection) &&
           accepted is not null && noRejection is null,
        nameof(UnknownSelectionIsRejectedWithCatalogPresent));

    AssertSelectionRejected(catalog, ContentSelection.Create([unknownId], []));
    AssertSelectionRejected(catalog, ContentSelection.Create([crossAdapterId], []));
    AssertSelectionRejected(catalog, ContentSelection.Create([protectedId], []));
    AssertSelectionRejected(
        catalog,
        ContentSelection.Create(
            [addId],
            [new(addId, ConflictResolution.KeepTarget), new(conflictId, ConflictResolution.KeepTarget)]));
    AssertSelectionRejected(catalog, ContentSelection.Create([addId], []));
    AssertSelectionRejected(
        catalog,
        ContentSelection.Create([addId], [new(conflictId, ConflictResolution.Unresolved)]));
    AssertSelectionRejected(
        catalog,
        ContentSelection.Create([addId], [new(conflictId, ConflictResolution.UseSource)]));

    AssertThrows<ArgumentException>(
        () => ContentSelection.Create([addId, addId], []),
        nameof(UnknownSelectionIsRejectedWithCatalogPresent));
    AssertThrows<ArgumentException>(
        () => ContentSelection.Create(
            [],
            [
                new(conflictId, ConflictResolution.KeepTarget),
                new(conflictId, ConflictResolution.Skip),
            ]),
        nameof(UnknownSelectionIsRejectedWithCatalogPresent));

    var overLimit = new CountingEnumerable<ContentItemId>(
        ContentContractLimits.MaximumCatalogItems + 2,
        index => ContentItemId.TryCreate("vanilla", $"item-{index}", out var value) ? value : default);
    AssertThrows<ArgumentException>(
        () => ContentSelection.Create(overLimit, []),
        nameof(UnknownSelectionIsRejectedWithCatalogPresent));
    Assert(overLimit.Observations == ContentContractLimits.MaximumCatalogItems + 1,
        nameof(UnknownSelectionIsRejectedWithCatalogPresent));
}

static void AssertSelectionRejected(ContentCatalog catalog, ContentSelection selection)
{
    Assert(!ContentSelectionValidator.TryValidateExplicit(
               catalog,
               selection,
               out var validated,
               out var rejection) &&
           validated is null &&
           rejection is not null &&
           rejection.AdapterId == catalog.AdapterId,
        nameof(UnknownSelectionIsRejectedWithCatalogPresent));
}

static void DefaultSelectionNeverUsesSource()
{
    Assert(ContentItemId.TryCreate("vanilla", "add", out var addId),
        nameof(DefaultSelectionNeverUsesSource));
    Assert(ContentItemId.TryCreate("vanilla", "update", out var updateId),
        nameof(DefaultSelectionNeverUsesSource));
    Assert(ContentItemId.TryCreate("vanilla", "conflict-keep", out var keepId),
        nameof(DefaultSelectionNeverUsesSource));
    Assert(ContentItemId.TryCreate("vanilla", "conflict-skip", out var skipId),
        nameof(DefaultSelectionNeverUsesSource));
    var catalog = ContentCatalog.Create(
        "vanilla",
        [
            ContentCatalogItem.Create(
                addId, "新增", "可显式选择", PlannedContentDisposition.Add,
                true, false, ConflictResolution.Skip, null),
            ContentCatalogItem.Create(
                updateId, "更新", "可显式选择", PlannedContentDisposition.Update,
                true, false, ConflictResolution.Skip, null),
            ContentCatalogItem.Create(
                keepId, "冲突一", "默认保留目标", PlannedContentDisposition.Conflict,
                true, false, ConflictResolution.KeepTarget, null),
            ContentCatalogItem.Create(
                skipId, "冲突二", "默认跳过", PlannedContentDisposition.Conflict,
                true, false, ConflictResolution.Skip, null),
        ],
        []);
    Assert(ContentSelectionValidator.TryCreateDefaults(catalog, out var defaults, out var rejection) &&
           defaults is not null && rejection is null &&
           defaults.SelectedItems.Count == 0 &&
           defaults.ConflictResolutions[keepId] == ConflictResolution.KeepTarget &&
           defaults.ConflictResolutions[skipId] == ConflictResolution.Skip &&
           defaults.ConflictResolutions.Values.All(value => value != ConflictResolution.UseSource),
        nameof(DefaultSelectionNeverUsesSource));

    AssertDefaultCatalogRejected(ContentCatalog.Create(
        "vanilla",
        [ContentCatalogItem.Create(
            addId, "新增", "错误默认", PlannedContentDisposition.Add,
            true, true, ConflictResolution.Skip, null)],
        []));
    AssertDefaultCatalogRejected(ContentCatalog.Create(
        "vanilla",
        [ContentCatalogItem.Create(
            updateId, "更新", "错误默认", PlannedContentDisposition.Update,
            true, true, ConflictResolution.Skip, null)],
        []));
    AssertDefaultCatalogRejected(ContentCatalog.Create(
        "vanilla",
        [ContentCatalogItem.Create(
            keepId, "冲突", "错误采用来源", PlannedContentDisposition.Conflict,
            true, false, ConflictResolution.UseSource, null)],
        []));
    AssertDefaultCatalogRejected(ContentCatalog.Create(
        "vanilla",
        [ContentCatalogItem.Create(
            keepId, "冲突", "错误未解决", PlannedContentDisposition.Conflict,
            true, false, ConflictResolution.Unresolved, null)],
        []));
}

static void AssertDefaultCatalogRejected(ContentCatalog catalog)
{
    Assert(!ContentSelectionValidator.TryCreateDefaults(
               catalog,
               out var defaults,
               out var rejection) &&
           defaults is null && rejection is not null,
        nameof(DefaultSelectionNeverUsesSource));
}

static void ValidatedSelectionIsBoundToExactCatalog()
{
    Assert(ContentItemId.TryCreate("vanilla", "a", out var firstId),
        nameof(ValidatedSelectionIsBoundToExactCatalog));
    Assert(ContentItemId.TryCreate("vanilla", "b", out var secondId),
        nameof(ValidatedSelectionIsBoundToExactCatalog));
    var first = ContentCatalogItem.Create(
        firstId, "第一项", "说明一", PlannedContentDisposition.Add,
        true, false, ConflictResolution.Skip, null);
    var second = ContentCatalogItem.Create(
        secondId, "第二项", "说明二", PlannedContentDisposition.Update,
        true, false, ConflictResolution.Skip, null);
    var catalog = ContentCatalog.Create("vanilla", [first, second], []);
    var raw = ContentSelection.Create([firstId], []);
    Assert(ContentSelectionValidator.TryValidateExplicit(catalog, raw, out var validated, out _) &&
           validated is not null && validated.IsBoundTo(catalog),
        nameof(ValidatedSelectionIsBoundToExactCatalog));
    var boundSelection = validated!;

    var presentationOnly = ContentCatalog.Create(
        "vanilla",
        [
            ContentCatalogItem.Create(
                firstId, "改名但同合同", "另一句说明", PlannedContentDisposition.Add,
                true, false, ConflictResolution.Skip, null),
            second,
        ],
        []);
    var reordered = ContentCatalog.Create("vanilla", [second, first], []);
    var changedFlags = ContentCatalog.Create(
        "vanilla",
        [
            ContentCatalogItem.Create(
                firstId, "第一项", "说明一", PlannedContentDisposition.Add,
                false, false, ConflictResolution.Skip, ContentDiagnosticCode.CapabilityRejected),
            second,
        ],
        []);
    Assert(boundSelection.IsBoundTo(presentationOnly),
        nameof(ValidatedSelectionIsBoundToExactCatalog));
    Assert(!boundSelection.IsBoundTo(reordered) && !boundSelection.IsBoundTo(changedFlags),
        nameof(ValidatedSelectionIsBoundToExactCatalog));

    Assert(ContentItemId.TryCreate("vanilla", "conflict", out var conflictId),
        nameof(ValidatedSelectionIsBoundToExactCatalog));
    var conflictCatalog = ContentCatalog.Create(
        "vanilla",
        [ContentCatalogItem.Create(
            conflictId, "冲突", "必须显式采用来源", PlannedContentDisposition.Conflict,
            true, false, ConflictResolution.KeepTarget, null)],
        []);
    Assert(ContentSelectionValidator.TryCreateDefaults(conflictCatalog, out var defaults, out _) &&
           defaults is not null &&
           defaults.ConflictResolutions[conflictId] == ConflictResolution.KeepTarget,
        nameof(ValidatedSelectionIsBoundToExactCatalog));
    var explicitSource = ContentSelection.Create(
        [conflictId],
        [new(conflictId, ConflictResolution.UseSource)]);
    Assert(ContentSelectionValidator.TryValidateExplicit(
               conflictCatalog,
               explicitSource,
               out var sourceSelection,
               out _) &&
           sourceSelection is not null &&
           sourceSelection.ConflictResolutions[conflictId] == ConflictResolution.UseSource,
        nameof(ValidatedSelectionIsBoundToExactCatalog));
}

static void NonActionablePlanCannotCarryFileChanges()
{
    Assert(ContentRelativePath.TryCreate("options.txt", out var path, out _),
        nameof(NonActionablePlanCannotCarryFileChanges));
    var source = ContentFileSnapshot.Create(
        path!,
        true,
        "lang:en_us\n"u8,
        DateTimeOffset.UnixEpoch.AddSeconds(1),
        0,
        new ContentFileIdentity(1, 2, 3));
    var target = ContentFileSnapshot.Create(
        path!,
        true,
        "lang:zh_cn\n"u8,
        DateTimeOffset.UnixEpoch.AddSeconds(2),
        0,
        new ContentFileIdentity(1, 4, 5));

    foreach (var disposition in new[]
             {
                 PlannedContentDisposition.Same,
                 PlannedContentDisposition.Unselected,
                 PlannedContentDisposition.Protected,
                 PlannedContentDisposition.Skipped,
             })
    {
        Assert(ContentItemId.TryCreate("vanilla", disposition.ToString(), out var id),
            nameof(NonActionablePlanCannotCarryFileChanges));
        var item = ContentPlanItem.Create(id, disposition, ConflictResolution.Skip, "不写入");
        var change = PlannedFileChange.Create("vanilla", path!, source, target, [item]);
        var adapterPlan = ContentAdapterPlan.Create("vanilla", [item], [change], []);
        Assert(!ContentPlanCoordinator.TryCreateMigrationPlan(
                   1,
                   "source-instance",
                   "target-instance",
                   [adapterPlan],
                   out var rejectedPlan,
                   out var rejection) &&
               rejectedPlan is null && rejection is not null,
            nameof(NonActionablePlanCannotCarryFileChanges));
    }

    foreach (var (disposition, resolution) in new[]
             {
                 (PlannedContentDisposition.Add, ConflictResolution.Skip),
                 (PlannedContentDisposition.Update, ConflictResolution.Skip),
                 (PlannedContentDisposition.Conflict, ConflictResolution.UseSource),
             })
    {
        Assert(ContentItemId.TryCreate(
                "vanilla",
                $"{disposition}-{resolution}",
                out var id),
            nameof(NonActionablePlanCannotCarryFileChanges));
        var item = ContentPlanItem.Create(id, disposition, resolution, "将采用来源");
        var change = PlannedFileChange.Create("vanilla", path!, source, target, [item]);
        var adapterPlan = ContentAdapterPlan.Create("vanilla", [item], [change], []);
        Assert(ContentPlanCoordinator.TryCreateMigrationPlan(
                   1,
                   "source-instance",
                   "target-instance",
                   [adapterPlan],
                   out var acceptedPlan,
                   out var rejection) &&
               acceptedPlan is not null && rejection is null &&
               acceptedPlan.FileChanges.Count == 1,
            nameof(NonActionablePlanCannotCarryFileChanges));
    }

    Assert(ContentItemId.TryCreate("vanilla", "conflict-keep", out var keepId),
        nameof(NonActionablePlanCannotCarryFileChanges));
    var keepItem = ContentPlanItem.Create(
        keepId,
        PlannedContentDisposition.Conflict,
        ConflictResolution.KeepTarget,
        "保留目标");
    var keepChange = PlannedFileChange.Create("vanilla", path!, source, target, [keepItem]);
    var keepPlan = ContentAdapterPlan.Create("vanilla", [keepItem], [keepChange], []);
    Assert(!ContentPlanCoordinator.TryCreateMigrationPlan(
               1,
               "source-instance",
               "target-instance",
               [keepPlan],
               out _,
               out _),
        nameof(NonActionablePlanCannotCarryFileChanges));
}

static void DuplicateFinalPathIgnoringCaseIsRejected()
{
    var (firstItem, firstChange) = CreateActionableChange("vanilla", "first", @"Config\A.json");
    var (secondItem, secondChange) = CreateActionableChange("jei", "second", @"config\a.JSON");
    var vanilla = ContentAdapterPlan.Create("vanilla", [firstItem], [firstChange], []);
    var jei = ContentAdapterPlan.Create("jei", [secondItem], [secondChange], []);

    Assert(!ContentPlanCoordinator.TryCreateMigrationPlan(
               1,
               "source-instance",
               "target-instance",
               [vanilla, jei],
               out var plan,
               out var rejection) &&
           plan is null && rejection?.Code == ContentDiagnosticCode.PathConflict,
        nameof(DuplicateFinalPathIgnoringCaseIsRejected));
}

static void AdapterPlanRejectsDetachedEquivalentItems()
{
    var (retainedItem, originalChange) = CreateActionableChange(
        "vanilla",
        "retained-item",
        "options.txt");
    var detachedItem = ContentPlanItem.Create(
        retainedItem.Id,
        retainedItem.Disposition,
        retainedItem.Resolution,
        retainedItem.Summary);
    var detachedChange = PlannedFileChange.Create(
        originalChange.AdapterId,
        originalChange.RelativePath,
        originalChange.SourceSnapshot,
        originalChange.TargetSnapshot,
        [detachedItem]);

    AssertThrows<ArgumentException>(
        () => ContentAdapterPlan.Create(
            "vanilla",
            [retainedItem],
            [detachedChange],
            []),
        nameof(AdapterPlanRejectsDetachedEquivalentItems));
}

static void UnicodeEquivalentFinalPathIsRejected()
{
    var (firstItem, firstChange) = CreateActionableChange(
        "vanilla",
        "composed",
        "config\\caf\u00E9.json");
    var (secondItem, secondChange) = CreateActionableChange(
        "jei",
        "decomposed",
        "config\\cafe\u0301.json");
    Assert(firstChange.RelativePath.Value != secondChange.RelativePath.Value,
        nameof(UnicodeEquivalentFinalPathIsRejected));
    var vanilla = ContentAdapterPlan.Create("vanilla", [firstItem], [firstChange], []);
    var jei = ContentAdapterPlan.Create("jei", [secondItem], [secondChange], []);

    Assert(!ContentPlanCoordinator.TryCreateMigrationPlan(
               1,
               "source-instance",
               "target-instance",
               [vanilla, jei],
               out var plan,
               out var rejection) &&
           plan is null && rejection?.Code == ContentDiagnosticCode.PathConflict,
        nameof(UnicodeEquivalentFinalPathIsRejected));
}

static void PathBoundSnapshotCannotBeRelabeled()
{
    Assert(ContentRelativePath.TryCreate("config\\a.json", out var firstPath, out _),
        nameof(PathBoundSnapshotCannotBeRelabeled));
    Assert(ContentRelativePath.TryCreate("config\\b.json", out var secondPath, out _),
        nameof(PathBoundSnapshotCannotBeRelabeled));
    var firstSnapshot = ContentFileSnapshot.Create(
        firstPath!,
        true,
        "a"u8,
        DateTimeOffset.UnixEpoch,
        0,
        new ContentFileIdentity(1, 2, 3));
    var secondSnapshot = ContentFileSnapshot.Create(
        secondPath!,
        true,
        "b"u8,
        DateTimeOffset.UnixEpoch,
        0,
        new ContentFileIdentity(1, 4, 5));
    Assert(ContentItemId.TryCreate("vanilla", "path-bound", out var id),
        nameof(PathBoundSnapshotCannotBeRelabeled));
    var item = ContentPlanItem.Create(
        id,
        PlannedContentDisposition.Update,
        ConflictResolution.Skip,
        "将更新");

    AssertThrows<ArgumentException>(
        () => PlannedFileChange.Create(
            "vanilla", firstPath!, firstSnapshot, secondSnapshot, [item]),
        nameof(PathBoundSnapshotCannotBeRelabeled));
    AssertThrows<ArgumentException>(
        () => PlannedFileChange.Create(
            "vanilla", secondPath!, firstSnapshot, secondSnapshot, [item]),
        nameof(PathBoundSnapshotCannotBeRelabeled));

    var type = typeof(ContentFileSnapshot);
    Assert(!type.IsRecordLike() &&
           type.GetConstructors().Length == 0 &&
           type.GetProperties().All(property => property.SetMethod is null),
        nameof(PathBoundSnapshotCannotBeRelabeled));
}

static void StrictJsonRejectsDuplicateProperties()
{
    var limits = new StrictJsonLimits(64, 1_000_000, 32 * 1024, 250_000, 250_000);
    foreach (var json in new[]
             {
                 "{\"a\":1,\"a\":2}",
                 "{\"outer\":{\"a\":1,\"a\":2}}",
                 "{\"a\":1,\"\\u0061\":2}",
                 "{\"a\":1,\"middle\":0,\"a\":2}",
             })
    {
        var buffer = ImmutableByteBuffer.CopyFrom(System.Text.Encoding.UTF8.GetBytes(json));
        Assert(!StrictJsonEquivalence.TryCompare(
                   buffer,
                   buffer,
                   limits,
                   out var equivalent,
                   out var rejection) &&
               !equivalent &&
               rejection == ContentDiagnosticCode.DuplicateJsonProperty,
            nameof(StrictJsonRejectsDuplicateProperties));
    }
}

static void StrictJsonEqualityRulesAreLocked()
{
    var limits = new StrictJsonLimits(64, 1_000_000, 32 * 1024, 250_000, 250_000);
    AssertJsonComparison("{\"a\":1,\"b\":2}", "{\"b\":2,\"a\":1}", limits, true);
    AssertJsonComparison("[1,2]", "[2,1]", limits, false);
    AssertJsonComparison("\"value\"", "\"Value\"", limits, false);
    AssertJsonComparison("1", "1.0", limits, false);
    AssertJsonComparison("1", "1e0", limits, false);
    AssertJsonComparison("{\"nested\":{\"x\":true}}", "{\"nested\":{\"x\":true}}", limits, true);

    foreach (var malformed in new[] { "", "{", "[1,]", "{/*comment*/\"a\":1}" })
    {
        var buffer = ImmutableByteBuffer.CopyFrom(System.Text.Encoding.UTF8.GetBytes(malformed));
        Assert(!StrictJsonEquivalence.TryCompare(
                   buffer,
                   buffer,
                   limits,
                   out var equivalent,
                   out var rejection) &&
               !equivalent && rejection == ContentDiagnosticCode.MalformedJson,
            nameof(StrictJsonEqualityRulesAreLocked));
    }

    var invalidUtf8 = ImmutableByteBuffer.CopyFrom([0x22, 0xC3, 0x28, 0x22]);
    Assert(!StrictJsonEquivalence.TryCompare(
               invalidUtf8,
               invalidUtf8,
               limits,
               out _,
               out var utf8Rejection) &&
           utf8Rejection == ContentDiagnosticCode.MalformedUtf8,
        nameof(StrictJsonEqualityRulesAreLocked));

    AssertJsonRejectedByLimit("{\"a\":{\"b\":1}}", new StrictJsonLimits(1, 100, 100, 10, 10));
    AssertJsonRejectedByLimit("[1,2]", new StrictJsonLimits(64, 2, 100, 10, 10));
    AssertJsonRejectedByLimit("\"abcd\"", new StrictJsonLimits(64, 100, 3, 10, 10));
    AssertJsonRejectedByLimit("[1,2]", new StrictJsonLimits(64, 100, 100, 1, 10));
    AssertJsonRejectedByLimit("{\"a\":1,\"b\":2}", new StrictJsonLimits(64, 100, 100, 10, 1));
    AssertJsonRejectedByLimit(
        new string('[', 65) + "0" + new string(']', 65),
        new StrictJsonLimits(64, 1_000, 100, 100, 10));
    AssertJsonRejectedByLimit("null", new StrictJsonLimits(65, 100, 100, 10, 10));
}

static void AssertJsonComparison(
    string left,
    string right,
    StrictJsonLimits limits,
    bool expectedEquivalent)
{
    var leftBuffer = ImmutableByteBuffer.CopyFrom(System.Text.Encoding.UTF8.GetBytes(left));
    var rightBuffer = ImmutableByteBuffer.CopyFrom(System.Text.Encoding.UTF8.GetBytes(right));
    Assert(StrictJsonEquivalence.TryCompare(
               leftBuffer,
               rightBuffer,
               limits,
               out var equivalent,
               out var rejection) &&
           equivalent == expectedEquivalent && rejection is null,
        nameof(StrictJsonEqualityRulesAreLocked));
}

static void AssertJsonRejectedByLimit(string json, StrictJsonLimits limits)
{
    var buffer = ImmutableByteBuffer.CopyFrom(System.Text.Encoding.UTF8.GetBytes(json));
    Assert(!StrictJsonEquivalence.TryCompare(
               buffer,
               buffer,
               limits,
               out var equivalent,
               out var rejection) &&
           !equivalent && rejection == ContentDiagnosticCode.LimitExceeded,
        nameof(StrictJsonEqualityRulesAreLocked));
}

static void AdapterPlansAggregateInStableOrdinalIdOrder()
{
    var (vanillaItem, vanillaChange) = CreateActionableChange(
        "vanilla", "options", "options.txt");
    var (jeiItem, jeiChange) = CreateActionableChange(
        "jei", "bookmarks", "config\\jei\\bookmarks.json");
    var (esmItem, esmChange) = CreateActionableChange(
        "esm", "mutes", "ESM\\soundsMuffled.dat");
    var vanilla = ContentAdapterPlan.Create("vanilla", [vanillaItem], [vanillaChange], []);
    var jei = ContentAdapterPlan.Create("jei", [jeiItem], [jeiChange], []);
    var esm = ContentAdapterPlan.Create("esm", [esmItem], [esmChange], []);

    var mutableInput = new List<ContentAdapterPlan> { vanilla, esm, jei };
    var first = MigrationContentPlan.Create(
        7,
        "source-instance",
        "target-instance",
        mutableInput);
    var second = MigrationContentPlan.Create(
        7,
        "source-instance",
        "target-instance",
        [jei, vanilla, esm]);
    mutableInput.Clear();

    var expectedAdapters = new[] { "esm", "jei", "vanilla" };
    Assert(first.AdapterPlans.Select(plan => plan.AdapterId).SequenceEqual(expectedAdapters) &&
           second.AdapterPlans.Select(plan => plan.AdapterId).SequenceEqual(expectedAdapters),
        nameof(AdapterPlansAggregateInStableOrdinalIdOrder));
    Assert(first.Items.Select(item => item.Id.AdapterId)
               .SequenceEqual(second.Items.Select(item => item.Id.AdapterId)) &&
           first.FileChanges.Select(change => change.RelativePath.Value)
               .SequenceEqual(second.FileChanges.Select(change => change.RelativePath.Value)),
        nameof(AdapterPlansAggregateInStableOrdinalIdOrder));
    Assert(first.AdapterPlans.Count == 3 && first.Items.Count == 3 && first.FileChanges.Count == 3,
        nameof(AdapterPlansAggregateInStableOrdinalIdOrder));
    Assert(first.AdapterPlans is not IList<ContentAdapterPlan> list || list.IsReadOnly,
        nameof(AdapterPlansAggregateInStableOrdinalIdOrder));
    Assert(first.Items[0] == first.AdapterPlans[0].Items[0] &&
           first.FileChanges[0] == first.AdapterPlans[0].FileChanges[0],
        nameof(AdapterPlansAggregateInStableOrdinalIdOrder));
    var createOverloads = typeof(MigrationContentPlan)
        .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(method => method.Name == nameof(MigrationContentPlan.Create))
        .ToArray();
    Assert(createOverloads.Length == 1 && createOverloads[0].GetParameters().Length == 4,
        nameof(AdapterPlansAggregateInStableOrdinalIdOrder));
}

static void PureContractsContainNoSystemPclOrDiscoveryTypes()
{
    var assembly = typeof(ContentCatalog).Assembly;
    var publicContentTypes = assembly.GetExportedTypes()
        .Where(type => type.Namespace == "BlockFerry.Core.Content")
        .ToArray();
    foreach (var type in publicContentTypes)
    {
        foreach (var constructor in type.GetConstructors())
        {
            Assert(constructor.GetParameters().All(parameter => !TouchesForbiddenNamespace(parameter.ParameterType)),
                nameof(PureContractsContainNoSystemPclOrDiscoveryTypes));
        }

        foreach (var method in type.GetMethods(
                     System.Reflection.BindingFlags.Public |
                     System.Reflection.BindingFlags.Instance |
                     System.Reflection.BindingFlags.Static |
                     System.Reflection.BindingFlags.DeclaredOnly))
        {
            Assert(!TouchesForbiddenNamespace(method.ReturnType) &&
                   method.GetParameters().All(parameter => !TouchesForbiddenNamespace(parameter.ParameterType)),
                nameof(PureContractsContainNoSystemPclOrDiscoveryTypes));
        }

        foreach (var property in type.GetProperties())
        {
            Assert(!TouchesForbiddenNamespace(property.PropertyType),
                nameof(PureContractsContainNoSystemPclOrDiscoveryTypes));
        }
    }

    var testProject = Path.GetDirectoryName(CurrentTestSource())!;
    var repositoryRoot = Path.GetFullPath(Path.Combine(testProject, "..", ".."));
    var files = new[]
    {
        "ContentModels.cs",
        "ContentSelectionValidator.cs",
        "ContentPlanCoordinator.cs",
        "StrictJsonEquivalence.cs",
    };
    var forbiddenSource = new[]
    {
        "BlockFerry.Core.System",
        "BlockFerry.Core.Pcl2",
        "BlockFerry.Core.Discovery",
        "BoundedFileSnapshot",
        "DiscoverySession",
        "DiscoveredInstancePair",
        "DiscoveredInstanceChoice",
        "IFileSystemCapability",
        "File.",
        "Directory.",
        "Environment.",
        "Stream",
        "Action<",
        "Func<",
    };
    foreach (var file in files)
    {
        var source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "BlockFerry.Core",
            "Content",
            file));
        Assert(forbiddenSource.All(value => !source.Contains(value, StringComparison.Ordinal)),
            nameof(PureContractsContainNoSystemPclOrDiscoveryTypes));
    }
}

static bool TouchesForbiddenNamespace(Type type)
{
    if (type.IsByRef || type.IsPointer || type.IsArray)
    {
        return TouchesForbiddenNamespace(type.GetElementType()!);
    }

    if (type.IsGenericType && type.GetGenericArguments().Any(TouchesForbiddenNamespace))
    {
        return true;
    }

    return type.Namespace is not null &&
           (type.Namespace.StartsWith("BlockFerry.Core.System", StringComparison.Ordinal) ||
            type.Namespace.StartsWith("BlockFerry.Core.Pcl2", StringComparison.Ordinal) ||
            type.Namespace.StartsWith("BlockFerry.Core.Discovery", StringComparison.Ordinal));
}

static string CurrentTestSource(
    [System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

static void VerificationRereadsAreExactlyPathBound()
{
    var (firstItem, firstChange) = CreateActionableChange(
        "vanilla", "verify-a", "config\\A.json");
    var (secondItem, secondChange) = CreateActionableChange(
        "vanilla", "verify-b", "config\\B.json");
    var staged = ContentStageResult.Create(
        "vanilla",
        [
            StagedFileMutation.Create(firstChange, "after-a"u8),
            StagedFileMutation.Create(secondChange, "after-b"u8),
        ]);
    var firstReread = ContentFileSnapshot.Create(
        firstChange.RelativePath,
        true,
        "after-a"u8,
        DateTimeOffset.UnixEpoch,
        0,
        new ContentFileIdentity(1, 10, 11));
    var secondReread = ContentFileSnapshot.Create(
        secondChange.RelativePath,
        true,
        "after-b"u8,
        DateTimeOffset.UnixEpoch,
        0,
        new ContentFileIdentity(1, 12, 13));
    Assert(ContentPlanCoordinator.TryBindVerificationRereads(
               staged,
               [secondReread, firstReread],
               out var bound,
               out var rejection) &&
           rejection is null &&
           bound.Count == 2 &&
           ReferenceEquals(bound[0], firstReread) &&
           ReferenceEquals(bound[1], secondReread),
        nameof(VerificationRereadsAreExactlyPathBound));

    AssertRereadsRejected(staged, [firstReread]);
    AssertRereadsRejected(staged, [firstReread, secondReread, secondReread]);
    AssertRereadsRejected(staged, [firstReread, firstReread]);

    Assert(ContentRelativePath.TryCreate("config\\a.JSON", out var aliasPath, out _),
        nameof(VerificationRereadsAreExactlyPathBound));
    var relabeled = ContentFileSnapshot.Create(
        aliasPath!,
        true,
        firstReread.Bytes.CopyBytes(),
        firstReread.LastWriteTimeUtc,
        firstReread.WindowsFileAttributes,
        firstReread.Identity);
    AssertRereadsRejected(staged, [relabeled, secondReread]);

    var counting = new CountingEnumerable<ContentFileSnapshot>(
        ContentContractLimits.MaximumFileChanges + 2,
        _ => firstReread);
    AssertRereadsRejected(staged, counting);
    Assert(counting.Observations == ContentContractLimits.MaximumFileChanges + 1,
        nameof(VerificationRereadsAreExactlyPathBound));
    _ = firstItem;
    _ = secondItem;
}

static void AssertRereadsRejected(
    ContentStageResult staged,
    IEnumerable<ContentFileSnapshot> rereads)
{
    Assert(!ContentPlanCoordinator.TryBindVerificationRereads(
               staged,
               rereads,
               out var bound,
               out var rejection) &&
           bound.Count == 0 && rejection is not null,
        nameof(VerificationRereadsAreExactlyPathBound));
}

static (ContentPlanItem Item, PlannedFileChange Change) CreateActionableChange(
    string adapterId,
    string technicalKey,
    string relativePath)
{
    Assert(ContentItemId.TryCreate(adapterId, technicalKey, out var id), technicalKey);
    Assert(ContentRelativePath.TryCreate(relativePath, out var path, out _), technicalKey);
    var bytes = System.Text.Encoding.UTF8.GetBytes(technicalKey);
    var source = ContentFileSnapshot.Create(
        path!,
        true,
        bytes,
        DateTimeOffset.UnixEpoch.AddSeconds(1),
        0,
        new ContentFileIdentity(1, 2, (ulong)technicalKey.Length));
    var target = ContentFileSnapshot.Create(
        path!,
        false,
        [],
        DateTimeOffset.UnixEpoch,
        0,
        null);
    var item = ContentPlanItem.Create(
        id,
        PlannedContentDisposition.Add,
        ConflictResolution.Skip,
        "将新增");
    return (item, PlannedFileChange.Create(adapterId, path!, source, target, [item]));
}

static string ReadCase(string[] arguments)
{
    for (var index = 0; index < arguments.Length - 1; index++)
    {
        if (string.Equals(arguments[index], "--case", StringComparison.Ordinal))
        {
            return arguments[index + 1];
        }
    }

    return "contracts";
}

static void Assert(bool condition, string caseName)
{
    if (!condition)
    {
        throw new InvalidOperationException(caseName);
    }
}

static void AssertThrows<TException>(Action action, string caseName)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(caseName);
}

internal sealed class CountingEnumerable<T>(int totalCount, Func<int, T> valueFactory) : IEnumerable<T>
{
    public int Observations { get; private set; }

    public IEnumerator<T> GetEnumerator()
    {
        for (var index = 0; index < totalCount; index++)
        {
            Observations++;
            yield return valueFactory(index);
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class ReflectionTestExtensions
{
    internal static bool IsRecordLike(this Type type) =>
        type.GetMethod("<Clone>$", System.Reflection.BindingFlags.Instance |
                                   System.Reflection.BindingFlags.Public |
                                   System.Reflection.BindingFlags.NonPublic) is not null;
}

internal sealed class AccessFixture : IDisposable
{
    private AccessFixture(
        FixtureSandbox sandbox,
        AuditedFileSystemCapability auditedCapability,
        string sourceRootPath,
        string targetRootPath,
        Pcl2DiscoveryResult discovery,
        Pcl2Instance source,
        Pcl2Instance target)
    {
        Sandbox = sandbox;
        AuditedCapability = auditedCapability;
        SourceRootPath = sourceRootPath;
        TargetRootPath = targetRootPath;
        Discovery = discovery;
        Source = source;
        Target = target;
        SessionFactory = new DiscoverySessionFactory();
        AccessFactory = new CapabilityBoundInstanceAccessFactory(
            SessionFactory,
            AuditedCapability);
    }

    internal FixtureSandbox Sandbox { get; }

    internal AuditedFileSystemCapability AuditedCapability { get; }

    internal string SourceRootPath { get; private set; }

    internal string TargetRootPath { get; }

    internal Pcl2DiscoveryResult Discovery { get; }

    internal Pcl2Instance Source { get; }

    internal Pcl2Instance Target { get; }

    internal DiscoverySessionFactory SessionFactory { get; }

    internal CapabilityBoundInstanceAccessFactory AccessFactory { get; }

    internal void MoveSourceRoot()
    {
        var destination = Sandbox.AllocateGuidPath();
        Sandbox.MoveDirectory(SourceRootPath, destination);
        SourceRootPath = destination;
    }

    internal void AddModJar(
        bool source,
        string fileName,
        params (string Name, byte[] Bytes)[] entries)
    {
        var root = source ? SourceRootPath : TargetRootPath;
        var absolutePath = Path.Combine(root, "mods", fileName);
        var relativePath = Path.GetRelativePath(Sandbox.RootPath, absolutePath);
        Sandbox.CreateZip(relativePath, entries);
    }

    internal void AddCompressedModJar(
        bool source,
        string fileName,
        params (string Name, byte[] Bytes)[] entries)
    {
        var relativePath = ModJarRelativePath(source, fileName);
        Sandbox.CreateCompressedZip(relativePath, entries);
    }

    internal void MarkModJarEncrypted(bool source, string fileName) =>
        Sandbox.SetZipEncryptedFlagsForTest(ModJarRelativePath(source, fileName));

    internal void AddModFile(bool source, string fileName, byte[] bytes)
    {
        var root = source ? SourceRootPath : TargetRootPath;
        var absolutePath = Path.Combine(root, "mods", fileName);
        var relativePath = Path.GetRelativePath(Sandbox.RootPath, absolutePath);
        Sandbox.WriteBytes(relativePath, bytes);
    }

    internal void SetOptions(bool source, byte[] bytes)
    {
        var root = source ? SourceRootPath : TargetRootPath;
        var relativePath = Path.GetRelativePath(
            Sandbox.RootPath,
            Path.Combine(root, "options.txt"));
        Sandbox.WriteBytes(relativePath, bytes);
    }

    internal void SetJeiBookmarks(
        bool source,
        string scopeKind,
        string scope,
        byte[] bytes)
    {
        var root = source ? SourceRootPath : TargetRootPath;
        var relativePath = Path.GetRelativePath(
            Sandbox.RootPath,
            Path.Combine(root, "config", "jei", "world", scopeKind, scope, "bookmarks.json"));
        Sandbox.WriteBytes(relativePath, bytes);
    }

    internal void SetJeiLegacy(
        bool source,
        string scopeKind,
        string scope,
        byte[] bytes)
    {
        var root = source ? SourceRootPath : TargetRootPath;
        var relativePath = Path.GetRelativePath(
            Sandbox.RootPath,
            Path.Combine(root, "config", "jei", "world", scopeKind, scope, "bookmarks.ini"));
        Sandbox.WriteBytes(relativePath, bytes);
    }

    internal void CreateJeiScope(bool source, string scopeKind, string scope)
    {
        var root = source ? SourceRootPath : TargetRootPath;
        var relativePath = Path.GetRelativePath(
            Sandbox.RootPath,
            Path.Combine(root, "config", "jei", "world", scopeKind, scope));
        Sandbox.CreateDirectory(relativePath);
    }

    internal void SetInstanceRelativeFile(bool source, string relativePath, byte[] bytes)
    {
        var root = source ? SourceRootPath : TargetRootPath;
        Sandbox.WriteBytes(
            Path.GetRelativePath(Sandbox.RootPath, Path.Combine(root, relativePath)),
            bytes);
    }

    internal void SetEsmMutes(bool source, byte[] bytes) =>
        SetInstanceRelativeFile(source, @"ESM\soundsMuffled.dat", bytes);

    internal void CreateEsmDirectory(bool source)
    {
        var root = source ? SourceRootPath : TargetRootPath;
        Sandbox.CreateDirectory(Path.GetRelativePath(Sandbox.RootPath, Path.Combine(root, "ESM")));
    }

    internal string SnapshotInstanceTrees() =>
        Sandbox.SnapshotTree(SourceRootPath) + "\n---TARGET---\n" +
        Sandbox.SnapshotTree(TargetRootPath);

    private string ModJarRelativePath(bool source, string fileName)
    {
        var root = source ? SourceRootPath : TargetRootPath;
        return Path.GetRelativePath(
            Sandbox.RootPath,
            Path.Combine(root, "mods", fileName));
    }

    internal static AccessFixture Create(
        string? sourceMinecraftVersion = "1.21.1",
        string? targetMinecraftVersion = "1.21.1")
    {
        var sandbox = FixtureSandbox.Create();
        try
        {
            var minecraftRoot = sandbox.CreateGuidDirectory();
            var minecraftRelative = Path.GetRelativePath(sandbox.RootPath, minecraftRoot);
            WriteInstance(
                sandbox,
                minecraftRelative,
                "Source",
                "source",
                sourceMinecraftVersion);
            WriteInstance(
                sandbox,
                minecraftRelative,
                "Target",
                "target",
                targetMinecraftVersion);
            sandbox.WriteBytes(
                Path.Combine(minecraftRelative, "PCL.ini"),
                Encoding.UTF8.GetBytes("Version:Source\r\n"));
            sandbox.WriteBytes(
                Path.Combine(minecraftRelative, "versions", "Source", "options.txt"),
                Encoding.UTF8.GetBytes("version:3955\nlang:en_us\nkey_key.jump:key.keyboard.space\n"));
            sandbox.WriteBytes(
                Path.Combine(minecraftRelative, "versions", "Target", "options.txt"),
                Encoding.UTF8.GetBytes("version:3955\nlang:zh_cn\nkey_key.jump:key.keyboard.j\n"));
            sandbox.WriteBytes(
                Path.Combine(
                    minecraftRelative,
                    "versions",
                    "Source",
                    "config",
                    "child.txt"),
                "fixture-config"u8.ToArray());
            sandbox.CreateZip(
                Path.Combine(
                    minecraftRelative,
                    "versions",
                    "Source",
                    "mods",
                    "fixture.jar"),
                ("fabric.mod.json", "{\"id\":\"fixture\",\"version\":\"1.0.0\"}"u8.ToArray()),
                ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\nImplementation-Version: 1.0.0\r\n\r\n"u8.ToArray()));

            var sourceRoot = Path.Combine(minecraftRoot, "versions", "Source");
            var targetRoot = Path.Combine(minecraftRoot, "versions", "Target");
            var audited = new AuditedFileSystemCapability(
                [
                    sandbox.GetRootProof(minecraftRoot),
                    sandbox.AuthorizeExistingDirectory(sourceRoot),
                    sandbox.AuthorizeExistingDirectory(targetRoot),
                ]);
            var discovery = new Pcl2InstanceDiscovery(audited).Discover(
                Pcl2DiscoveryRequest.Create([minecraftRoot], []));
            var source = discovery.Instances.Single(instance =>
                string.Equals(Path.GetFileName(instance.InstanceRoot), "Source", StringComparison.Ordinal));
            var target = discovery.Instances.Single(instance =>
                string.Equals(Path.GetFileName(instance.InstanceRoot), "Target", StringComparison.Ordinal));
            return new AccessFixture(
                sandbox,
                audited,
                sourceRoot,
                targetRoot,
                discovery,
                source,
                target);
        }
        catch
        {
            sandbox.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        var audit = AuditedCapability.AuditLog;
        if (audit.Any(entry => entry.IsMutation) ||
            audit.Any(entry => !entry.WasRejected &&
                               entry.RootId is not null &&
                               !AuditedCapability.AllowedRootIds.Contains(entry.RootId.Value)))
        {
            throw new InvalidOperationException(
                "Content access escaped its GUID fixture allowlist or mutated fixture state.");
        }

        AccessAuditLedger.Record(AuditedCapability.AllowedRootIds, audit);
        Sandbox.Dispose();
    }

    private static void WriteInstance(
        FixtureSandbox sandbox,
        string minecraftRelative,
        string directoryName,
        string instanceId,
        string? minecraftVersion)
    {
        sandbox.WriteBytes(
            Path.Combine(
                minecraftRelative,
                "versions",
                directoryName,
                directoryName + ".json"),
            Encoding.UTF8.GetBytes(
                minecraftVersion is null
                    ? $"{{\"id\":\"{instanceId}\",\"mainClass\":\"net.minecraft.client.main.Main\"}}"
                    : $"{{\"id\":\"{instanceId}\",\"minecraftVersion\":\"{minecraftVersion}\",\"mainClass\":\"net.minecraft.client.main.Main\"}}"));
        sandbox.WriteBytes(
            Path.Combine(
                minecraftRelative,
                "versions",
                directoryName,
                "PCL",
                "Setup.ini"),
            Encoding.UTF8.GetBytes("VersionArgumentIndieV2:true\r\n"));
    }
}

internal static class AccessAuditLedger
{
    private static readonly object Gate = new();
    private static readonly HashSet<Guid> Roots = [];
    private static int eventCount;
    private static int writeCount;
    private static int outsideRootCount;

    internal static int RootCount
    {
        get
        {
            lock (Gate)
            {
                return Roots.Count;
            }
        }
    }

    internal static int EventCount => Volatile.Read(ref eventCount);

    internal static int WriteCount => Volatile.Read(ref writeCount);

    internal static int OutsideRootCount => Volatile.Read(ref outsideRootCount);

    internal static void Reset()
    {
        lock (Gate)
        {
            Roots.Clear();
            eventCount = 0;
            writeCount = 0;
            outsideRootCount = 0;
        }
    }

    internal static void Record(
        IReadOnlySet<Guid> allowedRoots,
        IReadOnlyList<CapabilityAuditEvent> audit)
    {
        lock (Gate)
        {
            Roots.UnionWith(allowedRoots);
            eventCount += audit.Count;
            writeCount += audit.Count(entry => entry.IsMutation);
            outsideRootCount += audit.Count(entry =>
                !entry.WasRejected &&
                (entry.RootId is null || !allowedRoots.Contains(entry.RootId.Value)));
        }
    }
}

internal static class ModFixtureConstants
{
    internal static IReadOnlySet<string> DeclarationNames { get; } = new HashSet<string>(
        [
            "fabric.mod.json",
            "quilt.mod.json",
            "META-INF/mods.toml",
            "META-INF/neoforge.mods.toml",
        ],
        StringComparer.Ordinal);

    internal static IReadOnlySet<string> ManifestOnlyName { get; } = new HashSet<string>(
        ["META-INF/MANIFEST.MF"],
        StringComparer.Ordinal);
}

internal sealed class NoReadInstanceAccess : IReadOnlyInstanceAccess
{
    internal int EnumerateCalls { get; private set; }

    public ContentInstanceIdentity Identity { get; } = new(
        "no-read",
        "1.21.1",
        new ContentFileIdentity(1, 2, 3));

    public ContentFileSnapshot Read(
        ContentRelativePath relativePath,
        ContentReadLimits limits,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The invalid-limit test must not read a file.");

    public IReadOnlyList<ContentDirectoryEntry> Enumerate(
        ContentRelativePath relativeDirectory,
        ContentEnumerationLimits limits,
        CancellationToken cancellationToken)
    {
        EnumerateCalls++;
        throw new InvalidOperationException("The invalid-limit test must not enumerate.");
    }

    public IReadOnlyDictionary<string, ContentFileSnapshot> ReadZipEntries(
        ContentRelativePath zipPath,
        IReadOnlySet<string> allowedEntryNames,
        ContentZipReadLimits limits,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The invalid-limit test must not read an archive.");
}

internal sealed class RecordingMinecraftServerStatusClient(string? description) :
    IMinecraftServerStatusClient
{
    internal int CallCount { get; private set; }

    internal System.Net.IPAddress? LastAddress { get; private set; }

    internal int LastPort { get; private set; }

    public string? TryGetDescription(
        System.Net.IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        LastAddress = address;
        LastPort = port;
        return description;
    }
}

internal sealed record ZipAllowlistRequest(
    string ZipPath,
    HashSet<string> AllowedEntryNames,
    ZipReadLimits Limits);

internal sealed class ZipAllowlistAuditCapability(IFileSystemCapability inner)
    : IFileSystemCapability
{
    private readonly List<ZipAllowlistRequest> zipRequests = [];

    internal IReadOnlyList<ZipAllowlistRequest> ZipRequests => zipRequests.AsReadOnly();

    public IVerifiedDirectoryHandle OpenRoot(
        string absolutePath,
        FileSystemOpenPurpose purpose,
        CancellationToken cancellationToken) =>
        inner.OpenRoot(absolutePath, purpose, cancellationToken);

    public IVerifiedDirectoryHandle OpenDirectory(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        CancellationToken cancellationToken) =>
        inner.OpenDirectory(root, relativePath, cancellationToken);

    public IReadOnlyList<FileSystemEntrySnapshot> EnumerateEntries(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        EnumerationLimits limits,
        CancellationToken cancellationToken) =>
        inner.EnumerateEntries(root, relativePath, limits, cancellationToken);

    public BoundedFileSnapshot ReadFile(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        FileReadLimits limits,
        CancellationToken cancellationToken) =>
        inner.ReadFile(root, relativePath, limits, cancellationToken);

    public IReadOnlyDictionary<string, BoundedFileSnapshot> ReadZipEntries(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath zipPath,
        IReadOnlySet<string> allowedEntryNames,
        ZipReadLimits limits,
        CancellationToken cancellationToken)
    {
        zipRequests.Add(new ZipAllowlistRequest(
            zipPath.Value,
            new HashSet<string>(allowedEntryNames, StringComparer.Ordinal),
            limits));
        return inner.ReadZipEntries(root, zipPath, allowedEntryNames, limits, cancellationToken);
    }

    public VolumeCapabilitySnapshot InspectVolume(
        IVerifiedDirectoryHandle root,
        CancellationToken cancellationToken) =>
        inner.InspectVolume(root, cancellationToken);
}

internal sealed class CancelAfterSourceOpenCapability(
    IFileSystemCapability inner,
    CancellationTokenSource cancellation) : IFileSystemCapability
{
    internal int SourceOpenCount { get; private set; }

    internal int TargetOpenCount { get; private set; }

    public IVerifiedDirectoryHandle OpenRoot(
        string absolutePath,
        FileSystemOpenPurpose purpose,
        CancellationToken cancellationToken)
    {
        var handle = inner.OpenRoot(absolutePath, purpose, cancellationToken);
        if (purpose == FileSystemOpenPurpose.MigrationSource)
        {
            SourceOpenCount++;
            cancellation.Cancel();
        }
        else if (purpose == FileSystemOpenPurpose.MigrationTarget)
        {
            TargetOpenCount++;
        }

        return handle;
    }

    public IVerifiedDirectoryHandle OpenDirectory(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        CancellationToken cancellationToken) =>
        inner.OpenDirectory(root, relativePath, cancellationToken);

    public IReadOnlyList<FileSystemEntrySnapshot> EnumerateEntries(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        EnumerationLimits limits,
        CancellationToken cancellationToken) =>
        inner.EnumerateEntries(root, relativePath, limits, cancellationToken);

    public BoundedFileSnapshot ReadFile(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        FileReadLimits limits,
        CancellationToken cancellationToken) =>
        inner.ReadFile(root, relativePath, limits, cancellationToken);

    public IReadOnlyDictionary<string, BoundedFileSnapshot> ReadZipEntries(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath zipPath,
        IReadOnlySet<string> allowedEntryNames,
        ZipReadLimits limits,
        CancellationToken cancellationToken) =>
        inner.ReadZipEntries(root, zipPath, allowedEntryNames, limits, cancellationToken);

    public VolumeCapabilitySnapshot InspectVolume(
        IVerifiedDirectoryHandle root,
        CancellationToken cancellationToken) =>
        inner.InspectVolume(root, cancellationToken);
}

internal enum RootOpenMutation
{
    VolumeSerial,
    FileIdLow,
    FileIdHigh,
    NonLocalHandle,
    RedirectedHandle,
    FinalPath,
    NonLocalVolume,
    RedirectedVolume,
    UnknownVolume,
}

internal sealed class RootOverrideCapability(
    IFileSystemCapability inner,
    RootOpenMutation mutation) : IFileSystemCapability
{
    public IVerifiedDirectoryHandle OpenRoot(
        string absolutePath,
        FileSystemOpenPurpose purpose,
        CancellationToken cancellationToken)
    {
        var opened = inner.OpenRoot(absolutePath, purpose, cancellationToken);
        return purpose == FileSystemOpenPurpose.MigrationSource
            ? new OverrideHandle(opened, mutation)
            : opened;
    }

    public IVerifiedDirectoryHandle OpenDirectory(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        CancellationToken cancellationToken) =>
        inner.OpenDirectory(Unwrap(root), relativePath, cancellationToken);

    public IReadOnlyList<FileSystemEntrySnapshot> EnumerateEntries(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        EnumerationLimits limits,
        CancellationToken cancellationToken) =>
        inner.EnumerateEntries(Unwrap(root), relativePath, limits, cancellationToken);

    public BoundedFileSnapshot ReadFile(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        FileReadLimits limits,
        CancellationToken cancellationToken) =>
        inner.ReadFile(Unwrap(root), relativePath, limits, cancellationToken);

    public IReadOnlyDictionary<string, BoundedFileSnapshot> ReadZipEntries(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath zipPath,
        IReadOnlySet<string> allowedEntryNames,
        ZipReadLimits limits,
        CancellationToken cancellationToken) =>
        inner.ReadZipEntries(
            Unwrap(root),
            zipPath,
            allowedEntryNames,
            limits,
            cancellationToken);

    public VolumeCapabilitySnapshot InspectVolume(
        IVerifiedDirectoryHandle root,
        CancellationToken cancellationToken)
    {
        var snapshot = inner.InspectVolume(Unwrap(root), cancellationToken);
        return mutation switch
        {
            RootOpenMutation.NonLocalVolume => snapshot with { IsLocalVolume = false },
            RootOpenMutation.RedirectedVolume => snapshot with { IsNetworkRedirected = true },
            RootOpenMutation.UnknownVolume => snapshot with
            {
                FileSystemName = string.Empty,
                SupportsPersistentAcls = false,
            },
            _ => snapshot,
        };
    }

    private static IVerifiedDirectoryHandle Unwrap(IVerifiedDirectoryHandle root) =>
        root is OverrideHandle overridden ? overridden.Inner : root;

    private sealed class OverrideHandle(
        IVerifiedDirectoryHandle inner,
        RootOpenMutation mutation) : IVerifiedDirectoryHandle
    {
        internal IVerifiedDirectoryHandle Inner { get; } = inner;

        public string FinalPath => mutation == RootOpenMutation.FinalPath
            ? Inner.FinalPath + "-replacement"
            : Inner.FinalPath;

        public PhysicalDirectoryIdentity Identity => mutation switch
        {
            RootOpenMutation.VolumeSerial => Inner.Identity with
            {
                VolumeSerialNumber = Inner.Identity.VolumeSerialNumber ^ 1UL,
            },
            RootOpenMutation.FileIdLow => Inner.Identity with
            {
                FileIdLow = Inner.Identity.FileIdLow ^ 1UL,
            },
            RootOpenMutation.FileIdHigh => Inner.Identity with
            {
                FileIdHigh = Inner.Identity.FileIdHigh ^ 1UL,
            },
            _ => Inner.Identity,
        };

        public bool IsLocalVolume =>
            mutation != RootOpenMutation.NonLocalHandle && Inner.IsLocalVolume;

        public bool IsNetworkRedirected =>
            mutation == RootOpenMutation.RedirectedHandle || Inner.IsNetworkRedirected;

        public void Dispose() => Inner.Dispose();
    }
}

internal sealed class CountingResultCapability(IFileSystemCapability inner) : IFileSystemCapability
{
    internal int EnumerationObservations { get; private set; }

    internal int ZipObservations { get; private set; }

    public IVerifiedDirectoryHandle OpenRoot(
        string absolutePath,
        FileSystemOpenPurpose purpose,
        CancellationToken cancellationToken) =>
        inner.OpenRoot(absolutePath, purpose, cancellationToken);

    public IVerifiedDirectoryHandle OpenDirectory(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        CancellationToken cancellationToken) =>
        inner.OpenDirectory(root, relativePath, cancellationToken);

    public IReadOnlyList<FileSystemEntrySnapshot> EnumerateEntries(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        EnumerationLimits limits,
        CancellationToken cancellationToken)
    {
        var value = inner.EnumerateEntries(
            root,
            relativePath,
            limits,
            cancellationToken).Single();
        return new CountingReadOnlyList<FileSystemEntrySnapshot>(
            limits.MaximumEntries + 2,
            value,
            () => EnumerationObservations++);
    }

    public BoundedFileSnapshot ReadFile(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        FileReadLimits limits,
        CancellationToken cancellationToken) =>
        inner.ReadFile(root, relativePath, limits, cancellationToken);

    public IReadOnlyDictionary<string, BoundedFileSnapshot> ReadZipEntries(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath zipPath,
        IReadOnlySet<string> allowedEntryNames,
        ZipReadLimits limits,
        CancellationToken cancellationToken)
    {
        var value = inner.ReadZipEntries(
            root,
            zipPath,
            allowedEntryNames,
            limits,
            cancellationToken).Single();
        return new CountingReadOnlyDictionary<string, BoundedFileSnapshot>(
            limits.MaximumEntries + 2,
            value,
            StringComparer.Ordinal,
            () => ZipObservations++);
    }

    public VolumeCapabilitySnapshot InspectVolume(
        IVerifiedDirectoryHandle root,
        CancellationToken cancellationToken) =>
        inner.InspectVolume(root, cancellationToken);
}

internal sealed class CountingReadOnlyList<T>(
    int totalCount,
    T value,
    Action observed) : IReadOnlyList<T>
{
    public int Count => totalCount;

    public T this[int index] => index >= 0 && index < totalCount
        ? value
        : throw new ArgumentOutOfRangeException(nameof(index));

    public IEnumerator<T> GetEnumerator()
    {
        for (var index = 0; index < totalCount; index++)
        {
            observed();
            yield return value;
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class CountingReadOnlyDictionary<TKey, TValue>(
    int totalCount,
    KeyValuePair<TKey, TValue> value,
    IEqualityComparer<TKey> comparer,
    Action observed) : IReadOnlyDictionary<TKey, TValue>
    where TKey : notnull
{
    public int Count => totalCount;

    public IEnumerable<TKey> Keys => Enumerable.Repeat(value.Key, totalCount);

    public IEnumerable<TValue> Values => Enumerable.Repeat(value.Value, totalCount);

    public TValue this[TKey key] => comparer.Equals(key, value.Key)
        ? value.Value
        : throw new KeyNotFoundException();

    public bool ContainsKey(TKey key) => comparer.Equals(key, value.Key);

    public bool TryGetValue(TKey key, out TValue found)
    {
        if (ContainsKey(key))
        {
            found = value.Value;
            return true;
        }

        found = default!;
        return false;
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        for (var index = 0; index < totalCount; index++)
        {
            observed();
            yield return value;
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

internal enum TrackedOpenFailurePoint
{
    BeforeTargetOpen,
    AfterTargetOpen,
    CancelAfterSourceOpen,
    CancelAfterTargetOpen,
    CancelAfterTargetInspect,
}

internal sealed class TrackingFailureCapability : IFileSystemCapability, IDisposable
{
    private readonly IFileSystemCapability inner;
    private readonly TrackedOpenFailurePoint? failurePoint;
    private readonly CancellationTokenSource cancellation = new();
    private int liveHandleCount;

    internal TrackingFailureCapability(
        IFileSystemCapability inner,
        TrackedOpenFailurePoint? failurePoint)
    {
        this.inner = inner;
        this.failurePoint = failurePoint;
    }

    internal CancellationToken CancellationToken => cancellation.Token;

    internal int LiveHandleCount => Volatile.Read(ref liveHandleCount);

    internal int SourceOpenCount { get; private set; }

    public void Dispose() => cancellation.Dispose();

    public IVerifiedDirectoryHandle OpenRoot(
        string absolutePath,
        FileSystemOpenPurpose purpose,
        CancellationToken cancellationToken)
    {
        if (purpose == FileSystemOpenPurpose.MigrationTarget &&
            failurePoint == TrackedOpenFailurePoint.BeforeTargetOpen)
        {
            throw new CapabilityBoundaryException("Injected target-open failure.");
        }

        var opened = inner.OpenRoot(absolutePath, purpose, cancellationToken);
        var tracked = new TrackedHandle(this, opened, purpose);
        if (purpose == FileSystemOpenPurpose.MigrationSource)
        {
            SourceOpenCount++;
            if (failurePoint == TrackedOpenFailurePoint.CancelAfterSourceOpen)
            {
                cancellation.Cancel();
            }
        }
        else if (purpose == FileSystemOpenPurpose.MigrationTarget &&
                 failurePoint == TrackedOpenFailurePoint.CancelAfterTargetOpen)
        {
            cancellation.Cancel();
        }

        return tracked;
    }

    public IVerifiedDirectoryHandle OpenDirectory(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        CancellationToken cancellationToken) =>
        inner.OpenDirectory(Unwrap(root), relativePath, cancellationToken);

    public IReadOnlyList<FileSystemEntrySnapshot> EnumerateEntries(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        EnumerationLimits limits,
        CancellationToken cancellationToken) =>
        inner.EnumerateEntries(Unwrap(root), relativePath, limits, cancellationToken);

    public BoundedFileSnapshot ReadFile(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        FileReadLimits limits,
        CancellationToken cancellationToken) =>
        inner.ReadFile(Unwrap(root), relativePath, limits, cancellationToken);

    public IReadOnlyDictionary<string, BoundedFileSnapshot> ReadZipEntries(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath zipPath,
        IReadOnlySet<string> allowedEntryNames,
        ZipReadLimits limits,
        CancellationToken cancellationToken) =>
        inner.ReadZipEntries(
            Unwrap(root),
            zipPath,
            allowedEntryNames,
            limits,
            cancellationToken);

    public VolumeCapabilitySnapshot InspectVolume(
        IVerifiedDirectoryHandle root,
        CancellationToken cancellationToken)
    {
        var tracked = (TrackedHandle)root;
        if (tracked.Purpose == FileSystemOpenPurpose.MigrationTarget &&
            failurePoint == TrackedOpenFailurePoint.AfterTargetOpen)
        {
            throw new CapabilityBoundaryException("Injected post-target-open failure.");
        }

        var snapshot = inner.InspectVolume(tracked.Inner, cancellationToken);
        if (tracked.Purpose == FileSystemOpenPurpose.MigrationTarget &&
            failurePoint == TrackedOpenFailurePoint.CancelAfterTargetInspect)
        {
            cancellation.Cancel();
        }

        return snapshot;
    }

    private static IVerifiedDirectoryHandle Unwrap(IVerifiedDirectoryHandle root) =>
        root is TrackedHandle tracked ? tracked.Inner : root;

    private sealed class TrackedHandle : IVerifiedDirectoryHandle
    {
        private readonly TrackingFailureCapability owner;
        private int active = 1;

        internal TrackedHandle(
            TrackingFailureCapability owner,
            IVerifiedDirectoryHandle inner,
            FileSystemOpenPurpose purpose)
        {
            this.owner = owner;
            Inner = inner;
            Purpose = purpose;
            Interlocked.Increment(ref owner.liveHandleCount);
        }

        internal IVerifiedDirectoryHandle Inner { get; }

        internal FileSystemOpenPurpose Purpose { get; }

        public string FinalPath => Inner.FinalPath;

        public PhysicalDirectoryIdentity Identity => Inner.Identity;

        public bool IsLocalVolume => Inner.IsLocalVolume;

        public bool IsNetworkRedirected => Inner.IsNetworkRedirected;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref active, 0) == 0)
            {
                return;
            }

            try
            {
                Inner.Dispose();
            }
            finally
            {
                Interlocked.Decrement(ref owner.liveHandleCount);
            }
        }
    }
}
