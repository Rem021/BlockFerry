using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using BlockFerry.Core.Content;
using BlockFerry.Core.Discovery;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Transactions;

internal sealed class AcceptedMigrationPlanFactory(
    DiscoverySessionFactory discoverySessions,
    IReadOnlyDictionary<string, IContentAdapter> adapters)
{
    private readonly DiscoverySessionFactory _discoverySessions =
        discoverySessions ?? throw new ArgumentNullException(nameof(discoverySessions));
    private readonly ReadOnlyDictionary<string, IContentAdapter> _adapters =
        CopyAdapters(adapters);

    internal AcceptedMigrationPlanCreationResult Create(
        DiscoverySession session,
        string sourceId,
        string targetId,
        ContentAccessLease contentLease,
        ContentProbeContext contentContext,
        MigrationContentPlan contentPlan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (session is null ||
            contentLease is null ||
            contentContext is null ||
            contentPlan is null ||
            !session.IsActive ||
            !contentLease.IsBoundTo(session, sourceId, targetId) ||
            !contentContext.IsOwnedBy(contentLease) ||
            contentPlan.DiscoveryGeneration != session.Generation ||
            !string.Equals(contentPlan.SourceInstanceId, sourceId, StringComparison.Ordinal) ||
            !string.Equals(contentPlan.TargetInstanceId, targetId, StringComparison.Ordinal))
        {
            return Reject(ContentDiagnosticCode.CapabilityRejected);
        }

        DiscoveryPairValidation validation;
        try
        {
            validation = _discoverySessions.Revalidate(
                session,
                sourceId,
                targetId,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return Reject(ContentDiagnosticCode.StaleContext);
        }

        if (!validation.IsValid ||
            validation.Pair is not { } pair ||
            pair.Generation != session.Generation ||
            pair.Source.GameRoot.Identity != ToPhysical(contentLease.Source.Identity.GameRootIdentity) ||
            pair.Target.GameRoot.Identity != ToPhysical(contentLease.Target.Identity.GameRootIdentity) ||
            pair.Source.GameRoot.Identity == pair.Target.GameRoot.Identity)
        {
            return Reject(validation.IsStale
                ? ContentDiagnosticCode.StaleContext
                : ContentDiagnosticCode.CapabilityRejected);
        }

        try
        {
            return BuildAcceptedPlan(
                session,
                pair,
                contentLease,
                contentContext,
                contentPlan,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AcceptanceException exception)
        {
            return Reject(exception.Code, exception.AdapterId);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return Reject(ContentDiagnosticCode.CapabilityRejected);
        }
    }

    private AcceptedMigrationPlanCreationResult BuildAcceptedPlan(
        DiscoverySession session,
        DiscoveredInstancePair pair,
        ContentAccessLease contentLease,
        ContentProbeContext contentContext,
        MigrationContentPlan contentPlan,
        CancellationToken cancellationToken)
    {
        var adapterPlans = contentPlan.AdapterPlans.ToArray();
        if (adapterPlans.Length is 0 or > ContentContractLimits.MaximumAdapters ||
            !adapterPlans.Select(plan => plan.AdapterId).SequenceEqual(
                adapterPlans.Select(plan => plan.AdapterId).Order(StringComparer.Ordinal),
                StringComparer.Ordinal) ||
            adapterPlans.Select(plan => plan.AdapterId).Distinct(StringComparer.Ordinal).Count() != adapterPlans.Length)
        {
            throw new AcceptanceException("content", ContentDiagnosticCode.CapabilityRejected);
        }

        RecheckPlanAggregates(contentPlan, adapterPlans);
        var stages = new Dictionary<string, ContentStageResult>(StringComparer.Ordinal);
        var regeneratedByAdapter = new Dictionary<string, IReadOnlySet<NormalizedRelativePath>>(StringComparer.Ordinal);
        var writePaths = new HashSet<NormalizedRelativePath>(NormalizedRelativePathComparer.Instance);
        foreach (var adapterPlan in adapterPlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_adapters.TryGetValue(adapterPlan.AdapterId, out var adapter) ||
                !string.Equals(adapter.Id, adapterPlan.AdapterId, StringComparison.Ordinal))
            {
                throw new AcceptanceException(adapterPlan.AdapterId, ContentDiagnosticCode.CapabilityRejected);
            }

            RecheckAdapterPlan(adapterPlan);
            var staged = adapter.Stage(adapterPlan, cancellationToken);
            ValidateStage(adapterPlan, staged, writePaths);
            var regenerated = adapter.RegenerateAllowedPaths(contentContext, cancellationToken);
            var normalizedRegenerated = NormalizeRegeneratedSet(adapterPlan.AdapterId, regenerated);
            foreach (var mutation in staged.Mutations)
            {
                if (!WritePathGuard.TryNormalize(mutation.Change.RelativePath, out var normalized) ||
                    normalized is null ||
                    !normalizedRegenerated.Contains(normalized))
                {
                    throw new AcceptanceException(adapterPlan.AdapterId, ContentDiagnosticCode.PathConflict);
                }
            }

            stages.Add(adapterPlan.AdapterId, staged);
            regeneratedByAdapter.Add(adapterPlan.AdapterId, normalizedRegenerated);
        }

        if (writePaths.Count == 0 || writePaths.Count > ContentContractLimits.MaximumFileChanges)
        {
            throw new AcceptanceException("content", ContentDiagnosticCode.CapabilityRejected);
        }

        var fingerprint = new DiscoveryPairFingerprint(
            pair.Generation,
            pair.Source.Instance.Id,
            pair.Target.Instance.Id,
            pair.Source.GameRoot.Identity,
            pair.Target.GameRoot.Identity);
        var readOnlyStages = new ReadOnlyDictionary<string, ContentStageResult>(stages);
        var readOnlyRegenerated = new ReadOnlyDictionary<string, IReadOnlySet<NormalizedRelativePath>>(
            regeneratedByAdapter);
        var readOnlyWritePaths = new BlockFerry.Core.Content.ReadOnlySet<NormalizedRelativePath>(
            writePaths,
            NormalizedRelativePathComparer.Instance);
        var digest = AcceptedMigrationPlanDigest.Compute(
            fingerprint,
            contentPlan,
            readOnlyStages,
            readOnlyRegenerated,
            readOnlyWritePaths);
        return AcceptedMigrationPlanCreationResult.Accepted(new AcceptedMigrationPlan(
            fingerprint,
            contentPlan,
            readOnlyStages,
            readOnlyRegenerated,
            readOnlyWritePaths,
            digest,
            session,
            contentLease,
            contentContext));
    }

    private static void RecheckPlanAggregates(
        MigrationContentPlan contentPlan,
        IReadOnlyList<ContentAdapterPlan> adapterPlans)
    {
        var expectedItems = adapterPlans.SelectMany(plan => plan.Items).ToArray();
        var expectedChanges = adapterPlans.SelectMany(plan => plan.FileChanges).ToArray();
        var expectedDiagnostics = adapterPlans.SelectMany(plan => plan.Diagnostics).ToArray();
        if (!contentPlan.Items.SequenceEqual(expectedItems, ReferenceEqualityComparer.Instance) ||
            !contentPlan.FileChanges.SequenceEqual(expectedChanges, ReferenceEqualityComparer.Instance) ||
            !contentPlan.Diagnostics.SequenceEqual(expectedDiagnostics, ReferenceEqualityComparer.Instance))
        {
            throw new AcceptanceException("content", ContentDiagnosticCode.CapabilityRejected);
        }
    }

    private static void RecheckAdapterPlan(ContentAdapterPlan plan)
    {
        if (plan.Items.Count > ContentContractLimits.MaximumCatalogItems ||
            plan.FileChanges.Count is 0 or > ContentContractLimits.MaximumFileChanges ||
            plan.Diagnostics.Count > ContentContractLimits.MaximumDiagnostics ||
            plan.Items.Any(item =>
                item is null ||
                !string.Equals(item.Id.AdapterId, plan.AdapterId, StringComparison.Ordinal) ||
                item.Resolution == ConflictResolution.Unresolved) ||
            plan.Items.Select(item => item.Id).Distinct().Count() != plan.Items.Count)
        {
            throw new AcceptanceException(plan.AdapterId, ContentDiagnosticCode.CapabilityRejected);
        }

        foreach (var change in plan.FileChanges)
        {
            if (change is null ||
                !string.Equals(change.AdapterId, plan.AdapterId, StringComparison.Ordinal) ||
                !WritePathGuard.TryNormalize(change.RelativePath, out _) ||
                !change.SourceRelativePath.Equals(change.SourceSnapshot.RelativePath) ||
                !WritePathGuard.TryNormalize(change.SourceRelativePath, out _) ||
                !change.RelativePath.Equals(change.TargetSnapshot.RelativePath) ||
                !change.Items.Any(IsActionable) ||
                change.Items.Any(item => !plan.Items.Contains(item, ReferenceEqualityComparer.Instance)))
            {
                throw new AcceptanceException(plan.AdapterId, ContentDiagnosticCode.CapabilityRejected);
            }
        }
    }

    private static void ValidateStage(
        ContentAdapterPlan plan,
        ContentStageResult staged,
        HashSet<NormalizedRelativePath> writePaths)
    {
        if (staged is null ||
            !string.Equals(staged.AdapterId, plan.AdapterId, StringComparison.Ordinal) ||
            staged.Mutations.Count != plan.FileChanges.Count)
        {
            throw new AcceptanceException(plan.AdapterId, ContentDiagnosticCode.CapabilityRejected);
        }

        var expectedChanges = new HashSet<PlannedFileChange>(
            plan.FileChanges,
            ReferenceEqualityComparer.Instance);
        var observed = new HashSet<PlannedFileChange>(ReferenceEqualityComparer.Instance);
        foreach (var mutation in staged.Mutations)
        {
            if (mutation is null ||
                !expectedChanges.Contains(mutation.Change) ||
                !observed.Add(mutation.Change) ||
                !WritePathGuard.TryNormalize(mutation.Change.RelativePath, out var normalized) ||
                normalized is null ||
                !writePaths.Add(normalized))
            {
                throw new AcceptanceException(plan.AdapterId, ContentDiagnosticCode.PathConflict);
            }
        }
    }

    private static BlockFerry.Core.Content.ReadOnlySet<NormalizedRelativePath> NormalizeRegeneratedSet(
        string adapterId,
        IReadOnlySet<ContentRelativePath> regenerated)
    {
        if (regenerated is null)
        {
            throw new AcceptanceException(adapterId, ContentDiagnosticCode.CapabilityRejected);
        }

        var normalized = new HashSet<NormalizedRelativePath>(NormalizedRelativePathComparer.Instance);
        var observed = 0;
        foreach (var contentPath in regenerated)
        {
            observed++;
            if (observed > ContentContractLimits.MaximumFileChanges ||
                !WritePathGuard.TryNormalize(contentPath, out var path) ||
                path is null ||
                !normalized.Add(path))
            {
                throw new AcceptanceException(adapterId, ContentDiagnosticCode.PathConflict);
            }
        }

        if (observed != regenerated.Count)
        {
            throw new AcceptanceException(adapterId, ContentDiagnosticCode.CapabilityRejected);
        }

        return new BlockFerry.Core.Content.ReadOnlySet<NormalizedRelativePath>(
            normalized,
            NormalizedRelativePathComparer.Instance);
    }

    private static bool IsActionable(ContentPlanItem item) =>
        item.Disposition is PlannedContentDisposition.Add or PlannedContentDisposition.Update ||
        item.Disposition == PlannedContentDisposition.Conflict &&
        item.Resolution == ConflictResolution.UseSource;

    private static PhysicalDirectoryIdentity ToPhysical(ContentFileIdentity identity) =>
        new(identity.VolumeSerialNumber, identity.FileIdLow, identity.FileIdHigh);

    private static ReadOnlyDictionary<string, IContentAdapter> CopyAdapters(
        IReadOnlyDictionary<string, IContentAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        if (adapters.Count is 0 or > ContentContractLimits.MaximumAdapters)
        {
            throw new ArgumentException("A bounded adapter registry is required.", nameof(adapters));
        }

        var copy = new Dictionary<string, IContentAdapter>(StringComparer.Ordinal);
        foreach (var (id, adapter) in adapters)
        {
            if (adapter is null ||
                !string.Equals(id, adapter.Id, StringComparison.Ordinal) ||
                !copy.TryAdd(id, adapter))
            {
                throw new ArgumentException("Adapter registry entries must be unique and ID-bound.", nameof(adapters));
            }
        }

        return new ReadOnlyDictionary<string, IContentAdapter>(copy);
    }

    private static AcceptedMigrationPlanCreationResult Reject(
        ContentDiagnosticCode code,
        string adapterId = "content") =>
        AcceptedMigrationPlanCreationResult.Rejected(
        [
            ContentDiagnostic.Create(
                code,
                ContentDiagnosticSeverity.Error,
                adapterId),
        ]);

    private static bool IsRecoverable(Exception exception) => exception is
        ArgumentException or
        InvalidOperationException or
        ObjectDisposedException or
        IOException or
        UnauthorizedAccessException or
        CapabilityBoundaryException;

    private sealed class AcceptanceException(
        string adapterId,
        ContentDiagnosticCode code) : Exception
    {
        internal string AdapterId { get; } = adapterId;

        internal ContentDiagnosticCode Code { get; } = code;
    }
}

internal static class AcceptedMigrationPlanDigest
{
    internal static string Compute(
        DiscoveryPairFingerprint fingerprint,
        MigrationContentPlan contentPlan,
        IReadOnlyDictionary<string, ContentStageResult> stages,
        IReadOnlyDictionary<string, IReadOnlySet<NormalizedRelativePath>> regenerated,
        IReadOnlySet<NormalizedRelativePath> writeAllowlist)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendInt64(hash, fingerprint.Generation);
        AppendText(hash, fingerprint.SourceInstanceId);
        AppendText(hash, fingerprint.TargetInstanceId);
        AppendIdentity(hash, fingerprint.SourceRootIdentity);
        AppendIdentity(hash, fingerprint.TargetRootIdentity);
        AppendInt32(hash, contentPlan.AdapterPlans.Count);
        foreach (var adapterPlan in contentPlan.AdapterPlans)
        {
            AppendText(hash, adapterPlan.AdapterId);
            AppendInt32(hash, adapterPlan.Items.Count);
            foreach (var item in adapterPlan.Items)
            {
                AppendText(hash, item.Id.TechnicalKey);
                AppendInt32(hash, (int)item.Disposition);
                AppendInt32(hash, (int)item.Resolution);
            }

            var stage = stages[adapterPlan.AdapterId];
            AppendInt32(hash, stage.Mutations.Count);
            foreach (var mutation in stage.Mutations.OrderBy(
                         item => WritePathGuard.CollisionKey(
                             NormalizeRequired(item.Change.RelativePath)),
                         StringComparer.OrdinalIgnoreCase))
            {
                AppendText(hash, NormalizeRequired(mutation.Change.RelativePath).Value);
                AppendText(hash, NormalizeRequired(mutation.Change.SourceRelativePath).Value);
                AppendText(hash, mutation.Change.SourceSnapshot.Sha256);
                AppendText(hash, mutation.Change.TargetSnapshot.Sha256);
                AppendText(hash, mutation.AfterBytes.Sha256);
            }

            var allowed = regenerated[adapterPlan.AdapterId]
                .OrderBy(path => WritePathGuard.CollisionKey(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            AppendInt32(hash, allowed.Length);
            foreach (var path in allowed)
            {
                AppendText(hash, path.Value);
            }
        }

        var writes = writeAllowlist
            .OrderBy(path => WritePathGuard.CollisionKey(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        AppendInt32(hash, writes.Length);
        foreach (var path in writes)
        {
            AppendText(hash, path.Value);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static NormalizedRelativePath NormalizeRequired(ContentRelativePath path)
    {
        if (!WritePathGuard.TryNormalize(path, out var normalized) || normalized is null)
        {
            throw new InvalidOperationException("The accepted path is no longer valid.");
        }

        return normalized;
    }

    private static void AppendIdentity(IncrementalHash hash, PhysicalDirectoryIdentity identity)
    {
        AppendUInt64(hash, identity.VolumeSerialNumber);
        AppendUInt64(hash, identity.FileIdLow);
        AppendUInt64(hash, identity.FileIdHigh);
    }

    private static void AppendText(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            AppendInt32(hash, bytes.Length);
            hash.AppendData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendUInt64(IncrementalHash hash, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
