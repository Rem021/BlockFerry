using BlockFerry.App.WinUI.Discovery;
using BlockFerry.Core.Content;
using BlockFerry.Core.Discovery;
using BlockFerry.Core.Mods;
using BlockFerry.Core.Pcl2;
using BlockFerry.Core.Processes;
using BlockFerry.Core.System;
using BlockFerry.Core.Transactions;

namespace BlockFerry.App.WinUI.Services;

internal sealed class BlockFerryCompositionRoot : IDisposable
{
    private readonly AppStorageGuard appStorage;
    private Task disposalCompletion = Task.CompletedTask;
    private bool disposed;

    private BlockFerryCompositionRoot(
        AppStorageGuard appStorage,
        MigrationWorkflowCoordinator workflow,
        IThemePreferenceStore themePreferences)
    {
        this.appStorage = appStorage;
        Workflow = workflow;
        ThemePreferences = themePreferences;
    }

    internal MigrationWorkflowCoordinator Workflow { get; }

    internal IThemePreferenceStore ThemePreferences { get; }

    internal Task DisposalCompletion => disposalCompletion;

    internal static BlockFerryCompositionRoot CreateProduction()
    {
        var environment = new WindowsEnvironmentPaths();
        var fileSystem = new WindowsFileSystemCapability();
        var appStorage = new AppStorageGuard(environment, fileSystem);
        var protectedData = new WindowsCurrentUserProtectedData();
        var discoverySessions = new DiscoverySessionFactory();
        var candidateResolver = new InstanceCandidateResolver(fileSystem);
        var instanceDiscovery = new Pcl2InstanceDiscovery(fileSystem);
        var rememberedRoots = new DiscoveryRootStore(appStorage, protectedData);
        var discoveryService = new CoreDiscoveryRequestService(
            fileSystem,
            appStorage,
            rememberedRoots,
            new AutomaticCandidateProvider(
                environment,
                fileSystem,
                new WindowsShortcutTargetResolver()),
            candidateResolver,
            instanceDiscovery,
            discoverySessions);
        var optionsPreviewer = discoveryService.OptionsPreviewer ??
            throw new InvalidOperationException("The production options previewer was unavailable.");
        var jeiAdapter = new JeiBookmarksAdapter(
            new JeiLanServerScopeHintProvider(new MinecraftServerStatusClient()));
        var adapters = new Dictionary<string, IContentAdapter>(StringComparer.Ordinal)
        {
            ["vanilla"] = new VanillaOptionsAdapter(optionsPreviewer),
            ["appearance"] = new DarkModeEverywhereAdapter(),
            ["jei"] = jeiAdapter,
            ["esm"] = new ExtremeSoundMufflerAdapter(),
        };
        var processGuard = new MinecraftProcessGuard();
        var targetMutexes = new TargetMutexFactory();
        var random = new CryptographicRandomSource();
        var transactionStores = new AppStorageTransactionStoreProvider(appStorage, protectedData);
        var modPresenceProbe = new ModPresenceProbe();
        var recoveryAuthorization = new RecoveryAuthorizationResolver(
            candidateResolver,
            instanceDiscovery,
            discoverySessions,
            new RecoveryCatalogContextFactory(fileSystem, modPresenceProbe),
            adapters);
        var recovery = new TransactionRecoveryService(
            transactionStores,
            fileSystem,
            processGuard,
            targetMutexes,
            random,
            recoveryAuthorization);
        var transactionCoordinator = new MigrationTransactionCoordinator(
            discoverySessions,
            adapters,
            new WindowsMigrationTransactionRuntimeFactory(appStorage, fileSystem, protectedData),
            processGuard,
            targetMutexes,
            new NoFaultInjector(),
            random,
            new WindowsTargetContentStabilityGate());
        var acceptedPlanFactory = new AcceptedMigrationPlanFactory(discoverySessions, adapters);
        var deferredJei = new DeferredJeiSyncCoordinator(
            jeiAdapter,
            acceptedPlanFactory,
            transactionCoordinator,
            new DeferredJeiSyncStore(appStorage, protectedData));
        var workflow = new MigrationWorkflowCoordinator(
            discoveryService,
            new CapabilityBoundInstanceAccessFactory(discoverySessions, fileSystem),
            new ContentCompatibilityProbe(modPresenceProbe),
            adapters,
            acceptedPlanFactory,
            transactionCoordinator,
            recovery,
            new RecoverySelectionResolver(
                candidateResolver,
                instanceDiscovery,
                discoverySessions,
                recovery),
            new CompletionSoundGate(new ElementSoundCompletionPlayer()),
            deferredJei,
            processGuard);
        return new BlockFerryCompositionRoot(
            appStorage,
            workflow,
            new ThemePreferenceStore(appStorage));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            Workflow.Dispose();
        }
        finally
        {
            disposalCompletion = DisposeAppStorageAfterWorkflowAsync();
        }
    }

    private async Task DisposeAppStorageAfterWorkflowAsync()
    {
        try
        {
            await Workflow.DisposalCompletion.ConfigureAwait(false);
        }
        finally
        {
            appStorage.Dispose();
        }
    }
}
