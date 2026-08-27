using System.Collections.ObjectModel;
using BlockFerry.Core.Content;
using BlockFerry.Core.Discovery;
using BlockFerry.Core.Pcl2;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Transactions;

internal sealed class RecoveryAuthorizationResolver
{
    private readonly InstanceCandidateResolver candidateResolver;
    private readonly Pcl2InstanceDiscovery instanceDiscovery;
    private readonly DiscoverySessionFactory sessionFactory;
    private readonly RecoveryCatalogContextFactory catalogContextFactory;
    private readonly ReadOnlyDictionary<string, IContentAdapter> adapters;
    private long generation;

    internal RecoveryAuthorizationResolver(
        InstanceCandidateResolver candidateResolver,
        Pcl2InstanceDiscovery instanceDiscovery,
        DiscoverySessionFactory sessionFactory,
        RecoveryCatalogContextFactory catalogContextFactory,
        IReadOnlyDictionary<string, IContentAdapter> adapters)
    {
        this.candidateResolver = candidateResolver ?? throw new ArgumentNullException(nameof(candidateResolver));
        this.instanceDiscovery = instanceDiscovery ?? throw new ArgumentNullException(nameof(instanceDiscovery));
        this.sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        this.catalogContextFactory = catalogContextFactory ??
            throw new ArgumentNullException(nameof(catalogContextFactory));
        ArgumentNullException.ThrowIfNull(adapters);
        var copy = new Dictionary<string, IContentAdapter>(StringComparer.Ordinal);
        foreach (var pair in adapters)
        {
            if (pair.Value is null ||
                !string.Equals(pair.Key, pair.Value.Id, StringComparison.Ordinal) ||
                !copy.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException(
                    "Recovery adapters must be non-null and keyed by their exact IDs.",
                    nameof(adapters));
            }
        }

        this.adapters = new ReadOnlyDictionary<string, IContentAdapter>(copy);
    }

    internal RecoveryExecutionAuthority? Resolve(
        RecoveryLocator recordedLocator,
        StoredMigrationPlan storedPlan,
        string candidatePath,
        CancellationToken cancellationToken) =>
        ResolveCore(
            recordedLocator,
            storedPlan,
            candidatePath,
            static (locator, paths, context, session) =>
                new RecoveryExecutionAuthority(locator, paths, context, session),
            cancellationToken);

    internal RecoveryReadOnlyContext? ResolveReadOnly(
        RecoveryLocator recordedLocator,
        StoredMigrationPlan storedPlan,
        string candidatePath,
        CancellationToken cancellationToken) =>
        ResolveCore(
            recordedLocator,
            storedPlan,
            candidatePath,
            static (locator, paths, context, session) =>
                new RecoveryReadOnlyContext(locator, paths, context, session),
            cancellationToken);

    private TContext? ResolveCore<TContext>(
        RecoveryLocator recordedLocator,
        StoredMigrationPlan storedPlan,
        string candidatePath,
        Func<
            RecoveryLocator,
            IReadOnlySet<NormalizedRelativePath>,
            RecoveryCatalogContext,
            DiscoverySession,
            TContext> createContext,
        CancellationToken cancellationToken)
        where TContext : class
    {
        ArgumentNullException.ThrowIfNull(recordedLocator);
        ArgumentNullException.ThrowIfNull(storedPlan);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        ArgumentNullException.ThrowIfNull(createContext);
        cancellationToken.ThrowIfCancellationRequested();
        if (recordedLocator.TransactionId != storedPlan.TransactionId)
        {
            throw new TransactionAuthenticationException(
                "Recovery locator and plan transaction identities did not match.");
        }

        DiscoverySession? session = null;
        RecoveryCatalogContext? context = null;
        try
        {
            var resolution = candidateResolver.ResolveManualSelectionResult(
                candidatePath,
                "BlockFerry recovery locator",
                cancellationToken);
            var discovery = instanceDiscovery.Discover(
                new Pcl2DiscoveryRequest(resolution.Candidates),
                cancellationToken);
            var nextGeneration = checked(Interlocked.Increment(ref generation));
            session = sessionFactory.Create(nextGeneration, discovery, cancellationToken);
            context = catalogContextFactory.Open(
                session,
                recordedLocator.TargetInstanceId,
                cancellationToken);
            if (context is null ||
                !string.Equals(
                    context.TargetChoice.Instance.Id,
                    recordedLocator.TargetInstanceId,
                    StringComparison.Ordinal) ||
                context.TargetChoice.GameRoot.Identity != recordedLocator.TargetRootIdentity)
            {
                return null;
            }

            var allowlist = RegenerateAndValidateCatalogs(
                storedPlan,
                context,
                cancellationToken);
            var authorizedLocator = RecoveryLocator.Create(
                recordedLocator.TransactionId,
                recordedLocator.TargetInstanceId,
                context.TargetChoice.GameRoot.CanonicalPath,
                recordedLocator.TargetRootIdentity);
            var result = createContext(
                authorizedLocator,
                allowlist,
                context,
                session);
            context = null;
            session = null;
            return result;
        }
        finally
        {
            context?.Dispose();
            session?.Dispose();
        }
    }

    private BlockFerry.Core.Content.ReadOnlySet<NormalizedRelativePath> RegenerateAndValidateCatalogs(
        StoredMigrationPlan storedPlan,
        RecoveryCatalogContext context,
        CancellationToken cancellationToken)
    {
        var allStoredPaths = new HashSet<NormalizedRelativePath>(NormalizedRelativePathComparer.Instance);
        foreach (var group in storedPlan.Paths.GroupBy(path => path.AdapterId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!adapters.TryGetValue(group.Key, out var adapter))
            {
                throw new RecoveryCatalogRejectedException();
            }

            var storedContentPaths = new List<ContentRelativePath>();
            var storedNormalizedPaths = new List<NormalizedRelativePath>();
            foreach (var stored in group)
            {
                if (!WritePathGuard.TryNormalize(stored.RelativePath.Value, out var normalized) ||
                    normalized is null ||
                    !ContentRelativePath.TryCreate(normalized.Value, out var contentPath, out _) ||
                    contentPath is null ||
                    !allStoredPaths.Add(normalized))
                {
                    throw new TransactionAuthenticationException(
                        "The protected recovery plan contained an unsafe or duplicate path.");
                }

                storedContentPaths.Add(contentPath);
                storedNormalizedPaths.Add(normalized);
            }

            var candidates = new BlockFerry.Core.Content.ReadOnlySet<ContentRelativePath>(storedContentPaths);
            var regenerated = adapter.RegenerateRecoveryAllowedPaths(
                context,
                candidates,
                cancellationToken);
            if (regenerated is null || regenerated.Count > ContentContractLimits.MaximumFileChanges)
            {
                throw new RecoveryCatalogRejectedException();
            }

            var allowed = new HashSet<NormalizedRelativePath>(NormalizedRelativePathComparer.Instance);
            foreach (var path in regenerated)
            {
                if (path is null ||
                    !WritePathGuard.TryNormalize(path.Value, out var normalized) ||
                    normalized is null ||
                    !allowed.Add(normalized))
                {
                    throw new RecoveryCatalogRejectedException();
                }
            }

            if (storedNormalizedPaths.Any(path => !allowed.Contains(path)))
            {
                throw new RecoveryCatalogRejectedException();
            }
        }

        if (allStoredPaths.Count != storedPlan.Paths.Count || allStoredPaths.Count == 0)
        {
            throw new TransactionAuthenticationException(
                "The protected recovery plan path set was invalid.");
        }

        return new BlockFerry.Core.Content.ReadOnlySet<NormalizedRelativePath>(
            allStoredPaths,
            NormalizedRelativePathComparer.Instance);
    }
}

internal sealed class RecoveryCatalogRejectedException : IOException;
