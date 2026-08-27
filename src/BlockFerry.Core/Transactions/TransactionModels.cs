using System.Collections.ObjectModel;
using BlockFerry.Core.Content;
using BlockFerry.Core.Discovery;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Transactions;

public readonly record struct TransactionId(Guid Value);

internal readonly record struct DiscoveryPairFingerprint(
    long Generation,
    string SourceInstanceId,
    string TargetInstanceId,
    PhysicalDirectoryIdentity SourceRootIdentity,
    PhysicalDirectoryIdentity TargetRootIdentity);

internal sealed class AcceptedMigrationPlan
{
    internal AcceptedMigrationPlan(
        DiscoveryPairFingerprint acceptedFingerprint,
        MigrationContentPlan contentPlan,
        IReadOnlyDictionary<string, ContentStageResult> adapterStages,
        IReadOnlyDictionary<string, IReadOnlySet<NormalizedRelativePath>> regeneratedAdapterAllowlists,
        IReadOnlySet<NormalizedRelativePath> writeAllowlist,
        string integrityDigest,
        DiscoverySession session,
        ContentAccessLease contentLease,
        ContentProbeContext contentContext)
    {
        if (acceptedFingerprint.Generation <= 0 ||
            string.IsNullOrWhiteSpace(acceptedFingerprint.SourceInstanceId) ||
            string.IsNullOrWhiteSpace(acceptedFingerprint.TargetInstanceId) ||
            acceptedFingerprint.SourceRootIdentity == acceptedFingerprint.TargetRootIdentity)
        {
            throw new ArgumentException("The accepted discovery fingerprint is invalid.", nameof(acceptedFingerprint));
        }

        ArgumentNullException.ThrowIfNull(contentPlan);
        ArgumentNullException.ThrowIfNull(adapterStages);
        ArgumentNullException.ThrowIfNull(regeneratedAdapterAllowlists);
        ArgumentNullException.ThrowIfNull(writeAllowlist);
        ArgumentException.ThrowIfNullOrWhiteSpace(integrityDigest);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(contentLease);
        ArgumentNullException.ThrowIfNull(contentContext);
        AcceptedFingerprint = acceptedFingerprint;
        ContentPlan = contentPlan;
        AdapterStages = new ReadOnlyDictionary<string, ContentStageResult>(
            new Dictionary<string, ContentStageResult>(adapterStages, StringComparer.Ordinal));
        RegeneratedAdapterAllowlists = new ReadOnlyDictionary<string, IReadOnlySet<NormalizedRelativePath>>(
            new Dictionary<string, IReadOnlySet<NormalizedRelativePath>>(
                regeneratedAdapterAllowlists,
                StringComparer.Ordinal));
        WriteAllowlist = new BlockFerry.Core.Content.ReadOnlySet<NormalizedRelativePath>(
            writeAllowlist,
            NormalizedRelativePathComparer.Instance);
        IntegrityDigest = integrityDigest;
        Session = session;
        ContentLease = contentLease;
        ContentContext = contentContext;
    }

    internal long Generation => AcceptedFingerprint.Generation;

    internal string SourceInstanceId => AcceptedFingerprint.SourceInstanceId;

    internal string TargetInstanceId => AcceptedFingerprint.TargetInstanceId;

    internal DiscoveryPairFingerprint AcceptedFingerprint { get; }

    internal MigrationContentPlan ContentPlan { get; }

    internal IReadOnlyDictionary<string, ContentStageResult> AdapterStages { get; }

    internal IReadOnlyDictionary<string, IReadOnlySet<NormalizedRelativePath>> RegeneratedAdapterAllowlists { get; }

    internal IReadOnlySet<NormalizedRelativePath> WriteAllowlist { get; }

    internal string IntegrityDigest { get; }

    internal DiscoverySession Session { get; }

    internal ContentAccessLease ContentLease { get; }

    internal ContentProbeContext ContentContext { get; }
}

internal sealed class AcceptedMigrationPlanCreationResult
{
    private AcceptedMigrationPlanCreationResult(
        bool isAccepted,
        AcceptedMigrationPlan? plan,
        IReadOnlyList<ContentDiagnostic> diagnostics)
    {
        IsAccepted = isAccepted;
        Plan = plan;
        Diagnostics = diagnostics;
    }

    internal bool IsAccepted { get; }

    internal AcceptedMigrationPlan? Plan { get; }

    internal IReadOnlyList<ContentDiagnostic> Diagnostics { get; }

    internal static AcceptedMigrationPlanCreationResult Accepted(AcceptedMigrationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new AcceptedMigrationPlanCreationResult(
            true,
            plan,
            Array.Empty<ContentDiagnostic>());
    }

    internal static AcceptedMigrationPlanCreationResult Rejected(
        IEnumerable<ContentDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var copy = diagnostics.Take(ContentContractLimits.MaximumDiagnostics + 1).ToArray();
        if (copy.Length is 0 or > ContentContractLimits.MaximumDiagnostics ||
            copy.Any(diagnostic => diagnostic is null))
        {
            throw new ArgumentException("A bounded diagnostic is required.", nameof(diagnostics));
        }

        return new AcceptedMigrationPlanCreationResult(
            false,
            null,
            Array.AsReadOnly(copy));
    }
}

internal sealed partial class MigrationTransactionCoordinator
{
    private static readonly object ExecutionAuthoritySeal = new();

    internal sealed class ExecutionAuthority
    {
        private ExecutionAuthority(
            object seal,
            AcceptedMigrationPlan plan,
            DiscoveredInstancePair currentPairEvidence,
            IReadOnlyDictionary<string, IReadOnlySet<NormalizedRelativePath>> currentRegeneratedAdapterAllowlists)
        {
            if (!ReferenceEquals(seal, ExecutionAuthoritySeal))
            {
                throw new InvalidOperationException("Execution authority can only be issued by the transaction coordinator.");
            }

            ArgumentNullException.ThrowIfNull(plan);
            ArgumentNullException.ThrowIfNull(currentPairEvidence);
            ArgumentNullException.ThrowIfNull(currentRegeneratedAdapterAllowlists);
            Plan = plan;
            CurrentPairEvidence = currentPairEvidence;
            CurrentRegeneratedAdapterAllowlists = currentRegeneratedAdapterAllowlists;
        }

        internal static ExecutionAuthority Issue(
            object seal,
            AcceptedMigrationPlan plan,
            DiscoveredInstancePair currentPairEvidence,
            IReadOnlyDictionary<string, IReadOnlySet<NormalizedRelativePath>> currentRegeneratedAdapterAllowlists) =>
            new(seal, plan, currentPairEvidence, currentRegeneratedAdapterAllowlists);

        internal AcceptedMigrationPlan Plan { get; }

        internal DiscoveredInstancePair CurrentPairEvidence { get; }

        internal IReadOnlyDictionary<string, IReadOnlySet<NormalizedRelativePath>>
            CurrentRegeneratedAdapterAllowlists
        { get; }
    }
}
