using BlockFerry.Core.Pcl2;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Discovery;

public sealed class DiscoverySessionFactory
{
    private const int MaximumRoots = 64;
    private const int MaximumInstances = 512;

    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The reviewed public composition contract requires an instance factory.")]
    public DiscoverySession Create(
        long generation,
        Pcl2DiscoveryResult discovery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generation),
                "A discovery generation must be positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var accepted = new List<DiscoverySessionEntry>();
        var rejected = new Dictionary<string, Pcl2Diagnostic>(StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var roots = Pcl2RawInput.Take(
                discovery.Roots,
                MaximumRoots,
                cancellationToken);
            var remaining = MaximumInstances;
            foreach (var root in roots.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (root is null || root.Instances is null || remaining == 0)
                {
                    continue;
                }

                var instances = Pcl2RawInput.Take(
                    root.Instances,
                    remaining,
                    cancellationToken);
                if (instances.Failure is not null)
                {
                    continue;
                }

                remaining -= instances.Values.Count;
                foreach (var instance in instances.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (instance is null || !IsBoundedInstanceId(instance.Id))
                    {
                        continue;
                    }

                    if (!seenIds.Add(instance.Id))
                    {
                        var previous = accepted.FirstOrDefault(entry =>
                            string.Equals(
                                entry.Instance.Id,
                                instance.Id,
                                StringComparison.Ordinal));
                        if (previous is not null)
                        {
                            accepted.Remove(previous);
                            previous.Dispose();
                        }

                        rejected[instance.Id] = Diagnostic(
                            Pcl2DiagnosticCode.DiscoveryProofInvalid,
                            "The discovery result contains a duplicate instance identity.",
                            instance.Id);
                        continue;
                    }

                    if (TryCreateEntry(
                            instance,
                            cancellationToken,
                            out var entry,
                            out var diagnostic))
                    {
                        accepted.Add(entry!);
                    }
                    else
                    {
                        rejected[instance.Id] = diagnostic!;
                    }
                }
            }

            var session = new DiscoverySession(
                generation,
                accepted.AsReadOnly(),
                rejected);
            accepted.Clear();
            return session;
        }
        catch
        {
            foreach (var entry in accepted)
            {
                entry.Dispose();
            }

            throw;
        }
    }

    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The reviewed public composition contract requires an instance factory.")]
    public DiscoveryPairValidation Revalidate(
        DiscoverySession session,
        string sourceId,
        string targetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Revalidate(sourceId, targetId, cancellationToken);
    }

    private static bool TryCreateEntry(
        Pcl2Instance instance,
        CancellationToken cancellationToken,
        out DiscoverySessionEntry? entry,
        out Pcl2Diagnostic? diagnostic)
    {
        entry = null;
        diagnostic = null;
        if (!Pcl2InstanceProof.IsValid(instance) ||
            instance.CapabilityAccess is not { } capabilityAccess ||
            capabilityAccess.RootAccess.ProofFileSystem is not { } fileSystem)
        {
            diagnostic = Diagnostic(
                Pcl2DiagnosticCode.DiscoveryProofInvalid,
                "The instance does not carry a current capability-bound discovery proof.",
                instance.Id);
            return false;
        }

        if (instance.Isolation != Pcl2IsolationMode.Isolated)
        {
            diagnostic = Diagnostic(
                Pcl2DiagnosticCode.NonIsolatedInstance,
                "Only explicitly isolated PCL2 instances can be selected for synchronization.",
                instance.Id);
            return false;
        }

        if (instance.GameRoot is null ||
            capabilityAccess.GameRootRelativePath is null ||
            capabilityAccess.GameRootIdentity is not { } gameRootIdentity ||
            gameRootIdentity != capabilityAccess.InstanceRootIdentity ||
            !string.Equals(
                capabilityAccess.GameRootRelativePath.Value,
                capabilityAccess.InstanceRootRelativePath.Value,
                StringComparison.Ordinal) ||
            !HasIdentity(gameRootIdentity))
        {
            diagnostic = Diagnostic(
                Pcl2DiagnosticCode.GameRootUnresolved,
                "The isolated instance has no complete physical game-root proof.",
                instance.Id);
            return false;
        }

        Pcl2ReadPathGuard? retainedGuard = null;
        IVerifiedDirectoryHandle? retainedGameRoot = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            retainedGuard = new Pcl2ReadPathGuard(
                fileSystem,
                capabilityAccess.RootAccess,
                cancellationToken);
            retainedGameRoot = retainedGuard.OpenMinecraftDirectory(
                capabilityAccess.GameRootRelativePath,
                cancellationToken);
            var volume = fileSystem.InspectVolume(
                retainedGameRoot,
                cancellationToken);
            if (retainedGameRoot.Identity != gameRootIdentity ||
                !Pcl2PathNormalizer.AreEquivalent(
                    retainedGameRoot.FinalPath,
                    instance.GameRoot))
            {
                diagnostic = Diagnostic(
                    Pcl2DiagnosticCode.DiscoveryRootStale,
                    "The instance game root changed while the discovery session was created.",
                    instance.Id);
                return false;
            }

            if (!retainedGameRoot.IsLocalVolume ||
                retainedGameRoot.IsNetworkRedirected ||
                !volume.IsLocalVolume ||
                volume.IsNetworkRedirected ||
                !volume.SupportsPersistentAcls ||
                string.IsNullOrWhiteSpace(volume.FileSystemName))
            {
                diagnostic = Diagnostic(
                    Pcl2DiagnosticCode.UnsupportedGameRootVolume,
                    "This game-root volume is not supported for safe synchronization.",
                    instance.Id);
                return false;
            }

            var snapshot = new VerifiedDirectorySnapshot(
                retainedGameRoot.FinalPath,
                retainedGameRoot.Identity,
                volume.IsLocalVolume,
                volume.IsNetworkRedirected,
                true);
            entry = new DiscoverySessionEntry(
                instance,
                capabilityAccess,
                fileSystem,
                retainedGuard,
                retainedGameRoot,
                snapshot);
            retainedGuard = null;
            retainedGameRoot = null;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableCapabilityFailure(exception))
        {
            diagnostic = Diagnostic(
                Pcl2DiagnosticCode.DiscoveryRootStale,
                "The instance game root could not be revalidated safely.",
                instance.Id);
            return false;
        }
        finally
        {
            retainedGameRoot?.Dispose();
            retainedGuard?.Dispose();
        }
    }

    private static bool HasIdentity(PhysicalDirectoryIdentity identity) =>
        identity.VolumeSerialNumber != 0 &&
        (identity.FileIdLow != 0 || identity.FileIdHigh != 0);

    private static bool IsBoundedInstanceId(string? instanceId) =>
        !string.IsNullOrEmpty(instanceId) && instanceId.Length <= 128;

    private static bool IsRecoverableCapabilityFailure(Exception exception) =>
        exception is CapabilityBoundaryException or
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            InvalidOperationException or
            ObjectDisposedException;

    private static Pcl2Diagnostic Diagnostic(
        Pcl2DiagnosticCode code,
        string message,
        string? instanceId) =>
        new(
            code,
            Pcl2DiagnosticSeverity.Error,
            message,
            null,
            IsSafeInstanceId(instanceId) ? instanceId : null);

    private static bool IsSafeInstanceId(string? value) =>
        value is { Length: > 0 and <= 64 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
}
