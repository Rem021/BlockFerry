using BlockFerry.Core.Discovery;
using BlockFerry.Core.Pcl2;
using BlockFerry.Core.Transactions;

namespace BlockFerry.App.WinUI.Services;

internal sealed record RecoverySelectionResolution(
    VerifiedRecoverySelection? Selection,
    IReadOnlyList<Pcl2Diagnostic> Diagnostics);

internal sealed class RecoverySelectionResolver(
    InstanceCandidateResolver candidateResolver,
    Pcl2InstanceDiscovery instanceDiscovery,
    DiscoverySessionFactory sessionFactory,
    TransactionRecoveryService recoveryService)
{
    internal RecoverySelectionResolution Resolve(
        TransactionId transactionId,
        long generation,
        string selectedPath,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        cancellationToken.ThrowIfCancellationRequested();
        var resolution = candidateResolver.ResolveManualSelectionResult(
            selectedPath,
            "Windows recovery folder picker",
            cancellationToken);
        var discovery = instanceDiscovery.Discover(
            new Pcl2DiscoveryRequest(resolution.Candidates),
            cancellationToken);
        using var session = sessionFactory.Create(generation, discovery, cancellationToken);
        var verified = recoveryService.TryCreateVerifiedSelection(
            transactionId,
            session,
            cancellationToken);
        return new RecoverySelectionResolution(
            verified,
            discovery.Diagnostics.Take(64).ToArray());
    }
}
