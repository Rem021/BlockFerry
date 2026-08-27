using BlockFerry.App.WinUI.Services;
using BlockFerry.Core.Discovery;
using BlockFerry.Core.Pcl2;
using BlockFerry.Core.System;
using BlockFerry.Core.Transactions;
using System.Collections.ObjectModel;

namespace BlockFerry.App.WinUI.Discovery;

internal sealed record MainPageNavigationContext(
    Func<IFolderPickerService> FolderPickerFactory);

internal interface IDiscoverySessionHandle : IDisposable
{
    long Generation { get; }
    bool IsActive { get; }
    IReadOnlyList<Pcl2Instance> Instances { get; }

    bool CanPair(string sourceId, string targetId);
}

internal sealed record DiscoveryRequestResult(
    IDiscoverySessionHandle? Session,
    IReadOnlyList<Pcl2Diagnostic> Diagnostics,
    string StatusText);

internal interface IDiscoveryRequestService : IDisposable
{
    Pcl2OptionsMigrationPreviewer? OptionsPreviewer { get; }

    DiscoveryRequestResult DiscoverAutomatically(
        long generation,
        CancellationToken cancellationToken);

    DiscoveryRequestResult DiscoverManual(
        long generation,
        string selectedPath,
        CancellationToken cancellationToken);
}

internal sealed class DiscoveryViewModel : IDisposable
{
    private readonly object gate = new();
    private readonly IFolderPickerService folderPicker;
    private readonly Func<IDiscoveryRequestService> discoveryFactory;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private IDiscoveryRequestService? discoveryService;
    private IDiscoverySessionHandle? activeSession;
    private MigrationViewState state = MigrationViewState.AwaitingDiscovery;
    private IReadOnlyList<Pcl2Instance> instances = [];
    private IReadOnlyList<Pcl2Diagnostic> diagnostics = [];
    private string statusText = "请选择自动探测或明确的文件夹；发现阶段只读。";
    private string? sourceInstanceId;
    private string? targetInstanceId;
    private long generation;
    private long acceptanceRevision;
    private bool disposed;

    internal DiscoveryViewModel(
        IFolderPickerService folderPicker,
        Func<IDiscoveryRequestService> discoveryFactory)
    {
        this.folderPicker = folderPicker ?? throw new ArgumentNullException(nameof(folderPicker));
        this.discoveryFactory = discoveryFactory ?? throw new ArgumentNullException(nameof(discoveryFactory));
    }

    internal event EventHandler? StateChanged;

    internal MigrationViewState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    internal IReadOnlyList<Pcl2Instance> Instances
    {
        get
        {
            lock (gate)
            {
                return instances;
            }
        }
    }

    internal IReadOnlyList<Pcl2Diagnostic> Diagnostics
    {
        get
        {
            lock (gate)
            {
                return diagnostics;
            }
        }
    }

    internal string StatusText
    {
        get
        {
            lock (gate)
            {
                return statusText;
            }
        }
    }

    internal string? SourceInstanceId
    {
        get
        {
            lock (gate)
            {
                return sourceInstanceId;
            }
        }
    }

    internal string? TargetInstanceId
    {
        get
        {
            lock (gate)
            {
                return targetInstanceId;
            }
        }
    }

    internal long Generation => Interlocked.Read(ref generation);

    internal IDiscoverySessionHandle? ActiveSession
    {
        get
        {
            lock (gate)
            {
                return activeSession;
            }
        }
    }

    internal DiscoverySession? ActiveCoreSession
    {
        get
        {
            lock (gate)
            {
                return (activeSession as CoreDiscoverySessionHandle)?.Session;
            }
        }
    }

    internal Pcl2OptionsMigrationPreviewer? OptionsPreviewer
    {
        get
        {
            lock (gate)
            {
                return discoveryService?.OptionsPreviewer;
            }
        }
    }

    internal static DiscoveryViewModel CreateProduction(IFolderPickerService folderPicker) =>
        new(folderPicker, CoreDiscoveryRequestService.Create);

    internal Task DiscoverAutomaticallyAsync(CancellationToken cancellationToken) =>
        RunDiscoveryAsync(
            static (service, requestedGeneration, _, token) =>
                service.DiscoverAutomatically(requestedGeneration, token),
            selectedPath: null,
            cancellationToken);

    internal async Task ChooseFolderAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var selectedPath = await folderPicker.PickFolderAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            lock (gate)
            {
                if (!disposed)
                {
                    statusText = "已取消选择；当前实例与来源/目标保持不变。";
                }
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        await RunDiscoveryAsync(
            static (service, requestedGeneration, path, token) =>
                service.DiscoverManual(requestedGeneration, path!, token),
            selectedPath,
            cancellationToken);
    }

    internal bool SelectPair(string sourceId, string targetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        lock (gate)
        {
            ThrowIfDisposedLocked();
            if (activeSession is null ||
                !activeSession.IsActive ||
                !activeSession.CanPair(sourceId, targetId))
            {
                return false;
            }

            var source = activeSession.Instances.FirstOrDefault(instance =>
                string.Equals(instance.Id, sourceId, StringComparison.Ordinal));
            var target = activeSession.Instances.FirstOrDefault(instance =>
                string.Equals(instance.Id, targetId, StringComparison.Ordinal));
            if (source is null || target is null)
            {
                return false;
            }

            sourceInstanceId = source.Id;
            targetInstanceId = target.Id;
            state = CreateRealState(source, target);
            statusText = $"已发现 {activeSession.Instances.Count} 个实例；来源与目标已通过只读身份检查。";
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal void EnterDemo()
    {
        IDiscoverySessionHandle? previous;
        lock (gate)
        {
            ThrowIfDisposedLocked();
            _ = Interlocked.Increment(ref acceptanceRevision);
            previous = activeSession;
            activeSession = null;
            instances = [];
            diagnostics = [];
            sourceInstanceId = null;
            targetInstanceId = null;
            state = MigrationViewState.Demo;
            statusText = "演示使用内存中的固定数据，不会访问 Minecraft/PCL 文件。";
        }

        previous?.Dispose();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        IDiscoverySessionHandle? previous;
        IDiscoveryRequestService? service;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            _ = Interlocked.Increment(ref acceptanceRevision);
            previous = activeSession;
            activeSession = null;
            instances = [];
            sourceInstanceId = null;
            targetInstanceId = null;
        }

        lifetime.Cancel();
        previous?.Dispose();
        requestGate.Wait();
        try
        {
            lock (gate)
            {
                service = discoveryService;
                discoveryService = null;
            }

            service?.Dispose();
        }
        finally
        {
            requestGate.Release();
            requestGate.Dispose();
            lifetime.Dispose();
        }
    }

    private async Task RunDiscoveryAsync(
        Func<IDiscoveryRequestService, long, string?, CancellationToken, DiscoveryRequestResult> request,
        string? selectedPath,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var requestedGeneration = checked(Interlocked.Increment(ref generation));
        var requestedRevision = Interlocked.Increment(ref acceptanceRevision);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetime.Token);
        try
        {
            var result = await Task.Run(
                () =>
                {
                    requestGate.Wait(linkedCancellation.Token);
                    try
                    {
                        linkedCancellation.Token.ThrowIfCancellationRequested();
                        return request(
                            GetOrCreateDiscoveryService(),
                            requestedGeneration,
                            selectedPath,
                            linkedCancellation.Token);
                    }
                    finally
                    {
                        requestGate.Release();
                    }
                },
                linkedCancellation.Token);

            AcceptResult(requestedGeneration, requestedRevision, result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Lifetime disposal or a superseding operation leaves the accepted session untouched.
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            PreserveSessionAfterFailure(requestedGeneration, requestedRevision);
        }
    }

    private void AcceptResult(
        long requestedGeneration,
        long requestedRevision,
        DiscoveryRequestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var replacement = result.Session;
        var suggestedPair = replacement is null
            ? null
            : FindSuggestedPair(replacement);
        if (replacement is null ||
            !replacement.IsActive ||
            replacement.Generation != requestedGeneration ||
            suggestedPair is null)
        {
            replacement?.Dispose();
            lock (gate)
            {
                if (!disposed &&
                    requestedGeneration == generation &&
                    requestedRevision == acceptanceRevision)
                {
                    diagnostics = Snapshot(result.Diagnostics);
                    statusText = result.StatusText;
                }
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        IDiscoverySessionHandle? previous;
        lock (gate)
        {
            if (disposed ||
                requestedGeneration != generation ||
                requestedRevision != acceptanceRevision)
            {
                replacement.Dispose();
                return;
            }

            var source = replacement.Instances.First(instance =>
                string.Equals(instance.Id, suggestedPair.Value.SourceId, StringComparison.Ordinal));
            var target = replacement.Instances.First(instance =>
                string.Equals(instance.Id, suggestedPair.Value.TargetId, StringComparison.Ordinal));
            previous = activeSession;
            activeSession = replacement;
            instances = Array.AsReadOnly(replacement.Instances.ToArray());
            diagnostics = Snapshot(result.Diagnostics);
            sourceInstanceId = source.Id;
            targetInstanceId = target.Id;
            state = CreateRealState(source, target);
            statusText = result.StatusText;
        }

        previous?.Dispose();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PreserveSessionAfterFailure(long requestedGeneration, long requestedRevision)
    {
        lock (gate)
        {
            if (disposed ||
                requestedGeneration != generation ||
                requestedRevision != acceptanceRevision)
            {
                return;
            }

            diagnostics =
            [
                new Pcl2Diagnostic(
                    Pcl2DiagnosticCode.CandidateEnumerationFailed,
                    Pcl2DiagnosticSeverity.Error,
                    "实例发现未完成；当前已接受的实例选择保持不变。"),
            ];
            statusText = "实例发现失败；当前实例与来源/目标保持不变。";
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private IDiscoveryRequestService GetOrCreateDiscoveryService()
    {
        lock (gate)
        {
            ThrowIfDisposedLocked();
            discoveryService ??= discoveryFactory();
            return discoveryService;
        }
    }

    private static (string SourceId, string TargetId)? FindSuggestedPair(
        IDiscoverySessionHandle session)
    {
        var targets = session.Instances
            .OrderByDescending(instance => instance.IsSelected)
            .ThenByDescending(instance => instance.Id, StringComparer.Ordinal)
            .ToArray();
        foreach (var target in targets)
        {
            var preferredSources = session.Instances
                .Where(instance => !ReferenceEquals(instance, target))
                .OrderByDescending(instance =>
                    target.ModpackIdentity.Confidence == Pcl2IdentityConfidence.High &&
                    instance.ModpackIdentity.Confidence == Pcl2IdentityConfidence.High &&
                    string.Equals(
                        instance.ModpackIdentity.Name,
                        target.ModpackIdentity.Name,
                        StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(instance => instance.Id, StringComparer.Ordinal);
            foreach (var source in preferredSources)
            {
                if (session.CanPair(source.Id, target.Id))
                {
                    return (source.Id, target.Id);
                }
            }
        }

        return null;
    }

    private static MigrationViewState CreateRealState(Pcl2Instance source, Pcl2Instance target) =>
        new(
            ModeLabel: "真实数据 · 只读发现",
            MinecraftRoot: target.MinecraftRoot,
            SourceVersion: InstanceVersion(source, "来源"),
            TargetVersion: InstanceVersion(target, "目标"),
            SourceInstance: source.DisplayName,
            TargetInstance: target.DisplayName,
            PackName: target.ModpackIdentity.Name,
            LauncherName: "PCL 2",
            IsDemo: false,
            CanStart: true);

    private static string InstanceVersion(Pcl2Instance instance, string fallback) =>
        instance.ModpackIdentity.Version ?? instance.MinecraftVersion ?? fallback;

    private static ReadOnlyCollection<Pcl2Diagnostic> Snapshot(
        IReadOnlyList<Pcl2Diagnostic> source) =>
        Array.AsReadOnly((source ?? []).Take(256).ToArray());

    private void ThrowIfDisposed()
    {
        lock (gate)
        {
            ThrowIfDisposedLocked();
        }
    }

    private void ThrowIfDisposedLocked() =>
        ObjectDisposedException.ThrowIf(disposed, this);
}

internal sealed class CoreDiscoverySessionHandle : IDiscoverySessionHandle
{
    private DiscoverySession? session;
    private readonly IReadOnlyList<Pcl2Instance> instances;

    internal CoreDiscoverySessionHandle(DiscoverySession session)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        Generation = session.Generation;
        instances = Array.AsReadOnly(session.Instances.Select(choice => choice.Instance).ToArray());
    }

    public long Generation { get; }
    public bool IsActive => Volatile.Read(ref session)?.IsActive == true;
    public IReadOnlyList<Pcl2Instance> Instances => instances;
    internal DiscoverySession? Session => Volatile.Read(ref session);

    public bool CanPair(string sourceId, string targetId)
    {
        var current = Volatile.Read(ref session);
        return current is not null && current.TryGetPair(sourceId, targetId, out _);
    }

    public void Dispose() => Interlocked.Exchange(ref session, null)?.Dispose();
}

internal sealed class CoreDiscoveryRequestService : IDiscoveryRequestService
{
    private readonly AppStorageGuard appStorage;
    private readonly DiscoveryRootStore rememberedRoots;
    private readonly AutomaticCandidateProvider automaticCandidates;
    private readonly InstanceCandidateResolver candidateResolver;
    private readonly Pcl2InstanceDiscovery instanceDiscovery;
    private readonly DiscoverySessionFactory sessionFactory;
    private bool disposed;

    internal CoreDiscoveryRequestService(
        IFileSystemCapability fileSystem,
        AppStorageGuard appStorage,
        DiscoveryRootStore rememberedRoots,
        AutomaticCandidateProvider automaticCandidates,
        InstanceCandidateResolver candidateResolver,
        Pcl2InstanceDiscovery instanceDiscovery,
        DiscoverySessionFactory sessionFactory,
        bool ownsAppStorage = false)
    {
        this.appStorage = appStorage;
        this.rememberedRoots = rememberedRoots;
        this.automaticCandidates = automaticCandidates;
        this.candidateResolver = candidateResolver;
        this.instanceDiscovery = instanceDiscovery;
        this.sessionFactory = sessionFactory;
        this.ownsAppStorage = ownsAppStorage;
        OptionsPreviewer = new Pcl2OptionsMigrationPreviewer(fileSystem);
    }

    private readonly bool ownsAppStorage;

    public Pcl2OptionsMigrationPreviewer? OptionsPreviewer { get; }

    internal static CoreDiscoveryRequestService Create()
    {
        var environment = new WindowsEnvironmentPaths();
        var fileSystem = new WindowsFileSystemCapability();
        var appStorage = new AppStorageGuard(environment, fileSystem);
        var rootStore = new DiscoveryRootStore(
            appStorage,
            new WindowsCurrentUserProtectedData());
        return new CoreDiscoveryRequestService(
            fileSystem,
            appStorage,
            rootStore,
            new AutomaticCandidateProvider(
                environment,
                fileSystem,
                new WindowsShortcutTargetResolver()),
            new InstanceCandidateResolver(fileSystem),
            new Pcl2InstanceDiscovery(fileSystem),
            new DiscoverySessionFactory(),
            ownsAppStorage: true);
    }

    public DiscoveryRequestResult DiscoverAutomatically(
        long generation,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var remembered = rememberedRoots.Load(cancellationToken);
        var automatic = automaticCandidates.GetCandidateResult(
            new AutomaticCandidateRequest(remembered.ApprovedRoots),
            cancellationToken);
        var diagnostics = automatic.Diagnostics.Select(ToPclDiagnostic).ToList();
        var resolved = ResolveCandidates(automatic.Candidates, diagnostics, cancellationToken);
        return CreateSessionResult(
            generation,
            resolved,
            diagnostics,
            manualCandidates: null,
            cancellationToken);
    }

    public DiscoveryRequestResult DiscoverManual(
        long generation,
        string selectedPath,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var resolution = candidateResolver.ResolveManualSelectionResult(
            selectedPath,
            "Windows folder picker",
            cancellationToken);
        var diagnostics = resolution.Diagnostics.Select(ToPclDiagnostic).ToList();
        return CreateSessionResult(
            generation,
            resolution.Candidates,
            diagnostics,
            resolution.Candidates,
            cancellationToken);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (ownsAppStorage)
        {
            appStorage.Dispose();
        }
    }

    private ReadOnlyCollection<Pcl2RootCandidate> ResolveCandidates(
        IReadOnlyList<DiscoveryCandidate> candidates,
        List<Pcl2Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var resolved = new List<Pcl2RootCandidate>();
        foreach (var candidate in candidates.Take(64))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolution = candidateResolver.ResolveResult(candidate, cancellationToken);
            resolved.AddRange(resolution.Candidates);
            diagnostics.AddRange(resolution.Diagnostics.Select(ToPclDiagnostic));
        }

        return resolved.AsReadOnly();
    }

    private DiscoveryRequestResult CreateSessionResult(
        long generation,
        IReadOnlyList<Pcl2RootCandidate> candidates,
        List<Pcl2Diagnostic> diagnostics,
        IReadOnlyList<Pcl2RootCandidate>? manualCandidates,
        CancellationToken cancellationToken)
    {
        var discovery = instanceDiscovery.Discover(
            new Pcl2DiscoveryRequest(candidates),
            cancellationToken);
        diagnostics.AddRange(discovery.Diagnostics);
        var session = sessionFactory.Create(generation, discovery, cancellationToken);
        var handle = new CoreDiscoverySessionHandle(session);
        if (!HasPair(handle))
        {
            handle.Dispose();
            return new DiscoveryRequestResult(
                null,
                Array.AsReadOnly(diagnostics.Take(256).ToArray()),
                "没有发现两个可安全配对的隔离实例；当前选择保持不变。");
        }

        if (manualCandidates is not null)
        {
            try
            {
                RememberManualSelection(manualCandidates, cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                diagnostics.Add(new Pcl2Diagnostic(
                    Pcl2DiagnosticCode.CandidateEnumerationFailed,
                    Pcl2DiagnosticSeverity.Warning,
                    "实例已发现，但此文件夹未能保存到受保护的最近位置。"));
            }
        }

        return new DiscoveryRequestResult(
            handle,
            Array.AsReadOnly(diagnostics.Take(256).ToArray()),
            $"已发现 {handle.Instances.Count} 个可用实例；发现阶段未修改 Minecraft/PCL 文件。");
    }

    private void RememberManualSelection(
        IReadOnlyList<Pcl2RootCandidate> currentCandidates,
        CancellationToken cancellationToken)
    {
        var currentApproval = currentCandidates
            .Select(candidate => rememberedRoots.ApproveManualRoot(candidate, cancellationToken))
            .FirstOrDefault(approval => approval is not null);
        if (currentApproval is null)
        {
            return;
        }

        var existing = rememberedRoots.Load(cancellationToken).ApprovedRoots
            .Where(path => !string.Equals(
                path,
                currentApproval.CanonicalPath,
                StringComparison.OrdinalIgnoreCase))
            .Take(63)
            .ToArray();
        var approvals = new List<ManualRootApprovalToken>(existing.Length + 1);
        var approvedPaths = new List<string>(existing.Length + 1);
        foreach (var existingPath in existing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolution = candidateResolver.ResolveManualSelectionResult(
                existingPath,
                "previously approved root",
                cancellationToken);
            var approval = resolution.Candidates
                .Select(candidate => rememberedRoots.ApproveManualRoot(candidate, cancellationToken))
                .FirstOrDefault(token => token is not null);
            if (approval is null || approvedPaths.Contains(
                    approval.CanonicalPath,
                    StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            approvals.Add(approval);
            approvedPaths.Add(approval.CanonicalPath);
        }

        approvals.Add(currentApproval);
        approvedPaths.Add(currentApproval.CanonicalPath);
        _ = rememberedRoots.Save(
            new RememberedDiscoveryRoots(1, approvedPaths, null, null),
            approvals,
            cancellationToken);
    }

    private static bool HasPair(CoreDiscoverySessionHandle session)
    {
        foreach (var source in session.Instances)
        {
            foreach (var target in session.Instances)
            {
                if (session.CanPair(source.Id, target.Id))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static Pcl2Diagnostic ToPclDiagnostic(DiscoveryDiagnostic diagnostic) =>
        new(
            diagnostic.Code switch
            {
                DiscoveryDiagnosticCode.DiscoveryLimitReached or
                DiscoveryDiagnosticCode.CandidateLimitReached or
                DiscoveryDiagnosticCode.ShortcutEnumerationLimitReached =>
                    Pcl2DiagnosticCode.DiscoveryLimitReached,
                DiscoveryDiagnosticCode.CandidateEnumerationFailed =>
                    Pcl2DiagnosticCode.CandidateEnumerationFailed,
                _ => Pcl2DiagnosticCode.CandidatePathInvalid,
            },
            Pcl2DiagnosticSeverity.Warning,
            diagnostic.Message,
            diagnostic.Path);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}
