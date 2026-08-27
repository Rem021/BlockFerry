using BlockFerry.Core.Discovery;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Content;

internal sealed record ContentAccessLimits(
    int MaximumReadOperations,
    int MaximumEnumerationOperations,
    int MaximumEnumeratedEntries,
    long MaximumTotalBytes)
{
    internal static ContentAccessLimits Beta3 { get; } =
        new(20_000, 512, 500_000, 512L * 1024 * 1024);
}

internal sealed class ContentAccessOpenResult
{
    private ContentAccessOpenResult(
        bool isValid,
        bool isStale,
        ContentAccessLease? lease,
        IReadOnlyList<ContentDiagnostic> diagnostics)
    {
        IsValid = isValid;
        IsStale = isStale;
        Lease = lease;
        Diagnostics = diagnostics;
    }

    internal bool IsValid { get; }

    internal bool IsStale { get; }

    internal ContentAccessLease? Lease { get; }

    internal IReadOnlyList<ContentDiagnostic> Diagnostics { get; }

    internal static ContentAccessOpenResult Success(ContentAccessLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return new ContentAccessOpenResult(
            true,
            false,
            lease,
            Array.Empty<ContentDiagnostic>());
    }

    internal static ContentAccessOpenResult Failure(
        bool isStale,
        IEnumerable<ContentDiagnostic> diagnostics)
    {
        var copy = ContentEnumerable.CopyBounded(
            diagnostics,
            ContentContractLimits.MaximumDiagnostics,
            nameof(diagnostics));
        if (copy.Count == 0 || copy.Any(diagnostic => diagnostic is null))
        {
            throw new ArgumentException("A failure requires bounded diagnostics.", nameof(diagnostics));
        }

        return new ContentAccessOpenResult(
            false,
            isStale,
            null,
            Array.AsReadOnly(copy.ToArray()));
    }
}

internal sealed class CapabilityBoundInstanceAccessFactory(
    DiscoverySessionFactory discoverySessions,
    IFileSystemCapability fileSystem)
{
    internal ContentAccessOpenResult Open(
        DiscoverySession session,
        string sourceId,
        string targetId,
        ContentAccessLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(limits);
        if (!LimitsAreValid(limits))
        {
            return Failure(false, ContentDiagnosticCode.LimitExceeded);
        }

        IVerifiedDirectoryHandle? sourceRoot = null;
        IVerifiedDirectoryHandle? targetRoot = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var validation = discoverySessions.Revalidate(
                session,
                sourceId,
                targetId,
                cancellationToken);
            if (!validation.IsValid ||
                validation.Pair is not { } pair ||
                pair.Generation != session.Generation ||
                !ChoiceIsEligible(pair.Source) ||
                !ChoiceIsEligible(pair.Target) ||
                pair.Source.GameRoot.Identity == pair.Target.GameRoot.Identity)
            {
                return Failure(
                    validation.IsStale,
                    validation.IsStale
                        ? ContentDiagnosticCode.StaleContext
                        : ContentDiagnosticCode.CapabilityRejected);
            }

            sourceRoot = fileSystem.OpenRoot(
                pair.Source.GameRoot.CanonicalPath,
                FileSystemOpenPurpose.MigrationSource,
                cancellationToken);
            if (!RootMatches(sourceRoot, pair.Source.GameRoot, cancellationToken))
            {
                return Failure(true, ContentDiagnosticCode.StaleContext);
            }

            targetRoot = fileSystem.OpenRoot(
                pair.Target.GameRoot.CanonicalPath,
                FileSystemOpenPurpose.MigrationTarget,
                cancellationToken);
            if (!RootMatches(targetRoot, pair.Target.GameRoot, cancellationToken) ||
                sourceRoot.Identity == targetRoot.Identity)
            {
                return Failure(true, ContentDiagnosticCode.StaleContext);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var lifetime = new ContentAccessLifetime(session);
            var budget = new ContentAccessBudget(limits);
            var sourceIdentity = ToContentIdentity(sourceRoot.Identity);
            var targetIdentity = ToContentIdentity(targetRoot.Identity);
            var source = new CapabilityBoundInstanceAccess(
                fileSystem,
                sourceRoot,
                new ContentInstanceIdentity(
                    pair.Source.Instance.Id,
                    pair.Source.Instance.MinecraftVersion,
                    sourceIdentity),
                lifetime,
                budget);
            var target = new CapabilityBoundInstanceAccess(
                fileSystem,
                targetRoot,
                new ContentInstanceIdentity(
                    pair.Target.Instance.Id,
                    pair.Target.Instance.MinecraftVersion,
                    targetIdentity),
                lifetime,
                budget);
            var lease = new ContentAccessLease(
                session,
                pair.Source.Instance.Id,
                pair.Target.Instance.Id,
                sourceIdentity,
                targetIdentity,
                source,
                target,
                sourceRoot,
                targetRoot,
                lifetime);
            sourceRoot = null;
            targetRoot = null;
            return ContentAccessOpenResult.Success(lease);
        }
        catch (OperationCanceledException)
        {
            return Failure(false, ContentDiagnosticCode.CapabilityRejected);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return Failure(true, ContentDiagnosticCode.StaleContext);
        }
        finally
        {
            targetRoot?.Dispose();
            sourceRoot?.Dispose();
        }
    }

    private bool RootMatches(
        IVerifiedDirectoryHandle opened,
        VerifiedDirectorySnapshot expected,
        CancellationToken cancellationToken)
    {
        var volume = fileSystem.InspectVolume(opened, cancellationToken);
        return expected.IsReparseFree &&
               expected.IsLocalVolume &&
               !expected.IsNetworkRedirected &&
               opened.Identity == expected.Identity &&
               opened.IsLocalVolume &&
               !opened.IsNetworkRedirected &&
               volume.IsLocalVolume &&
               !volume.IsNetworkRedirected &&
               volume.SupportsPersistentAcls &&
               !string.IsNullOrWhiteSpace(volume.FileSystemName) &&
               string.Equals(
                   opened.FinalPath.TrimEnd('\\'),
                   expected.CanonicalPath.TrimEnd('\\'),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool ChoiceIsEligible(DiscoveredInstanceChoice choice) =>
        choice is not null &&
        choice.Instance is not null &&
        choice.GameRoot is not null &&
        choice.GameRoot.Identity.VolumeSerialNumber != 0 &&
        (choice.GameRoot.Identity.FileIdLow != 0 ||
         choice.GameRoot.Identity.FileIdHigh != 0);

    private static ContentFileIdentity ToContentIdentity(PhysicalDirectoryIdentity identity) =>
        new(identity.VolumeSerialNumber, identity.FileIdLow, identity.FileIdHigh);

    private static bool LimitsAreValid(ContentAccessLimits limits) =>
        limits.MaximumReadOperations is > 0 and <= 20_000 &&
        limits.MaximumEnumerationOperations is > 0 and <= 512 &&
        limits.MaximumEnumeratedEntries is > 0 and <= 500_000 &&
        limits.MaximumTotalBytes is > 0 and <= 512L * 1024 * 1024;

    private static bool IsRecoverable(Exception exception) =>
        exception is CapabilityBoundaryException or
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            InvalidOperationException or
            ObjectDisposedException;

    private static ContentAccessOpenResult Failure(
        bool isStale,
        ContentDiagnosticCode code) =>
        ContentAccessOpenResult.Failure(
            isStale,
            [ContentDiagnostic.Create(
                code,
                ContentDiagnosticSeverity.Error,
                "content")]);
}
