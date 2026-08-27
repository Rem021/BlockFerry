using BlockFerry.Core.Pcl2;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Discovery;

public sealed class InstanceCandidateResolver
{
    private const int MaximumSelectionEntries = 4096;
    private readonly IFileSystemCapability fileSystem;
    private readonly Pcl2DiscoveryBudget? budget;
    private readonly ManualSelectionAuthority manualSelectionAuthority = new();

    public InstanceCandidateResolver(IFileSystemCapability fileSystem)
        : this(fileSystem, null)
    {
    }

    internal InstanceCandidateResolver(
        IFileSystemCapability fileSystem,
        Pcl2DiscoveryBudget? budget)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        this.fileSystem = fileSystem;
        this.budget = budget;
    }

    public IReadOnlyList<Pcl2RootCandidate> Resolve(
        DiscoveryCandidate candidate,
        CancellationToken cancellationToken = default) =>
        ResolveResult(candidate, cancellationToken).Candidates;

    internal IReadOnlyList<Pcl2RootCandidate> ResolveManualSelection(
        string selectedPath,
        string evidence,
        CancellationToken cancellationToken = default) =>
        ResolveManualSelectionResult(selectedPath, evidence, cancellationToken).Candidates;

    internal InstanceCandidateResolution ResolveManualSelectionResult(
        string selectedPath,
        string evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        return ResolveResultCore(
            new DiscoveryCandidate(selectedPath, Pcl2CandidateOrigin.Manual, evidence),
            manualSelection: true,
            cancellationToken);
    }

    public InstanceCandidateResolution ResolveResult(
        DiscoveryCandidate candidate,
        CancellationToken cancellationToken = default) =>
        ResolveResultCore(candidate, manualSelection: false, cancellationToken);

    private InstanceCandidateResolution ResolveResultCore(
        DiscoveryCandidate candidate,
        bool manualSelection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        var currentDiagnostics = new List<DiscoveryDiagnostic>();

        InstanceCandidateResolution Complete(IReadOnlyList<Pcl2RootCandidate> candidates) =>
            new(candidates, currentDiagnostics.AsReadOnly());

        if (!DiscoveryPathPolicy.TryNormalizeLocalAbsolute(
                candidate.CandidatePath,
                out var selectedPath,
                out var pathDiagnostic))
        {
            currentDiagnostics.Add(pathDiagnostic);
            return Complete([]);
        }

        try
        {
            using var selected = fileSystem.OpenRoot(
                selectedPath,
                FileSystemOpenPurpose.Discovery,
                cancellationToken);
            if (!IsEligibleLocalDirectory(selected))
            {
                currentDiagnostics.Add(NetworkRejected(selectedPath));
                return Complete([]);
            }

            var selectedEntries = Enumerate(selected, cancellationToken);
            if (ContainsDirectory(selectedEntries, "versions"))
            {
                using var versions = fileSystem.OpenDirectory(
                    selected,
                    MustRelative("versions"),
                    cancellationToken);
                var resolved = CreateResolved(
                    selectedPath,
                    selected.Identity,
                    MustRelative(string.Empty),
                    selectedPath,
                    selected.Identity,
                    candidate.Origin,
                    manualSelection);
                return Complete([resolved]);
            }

            if (ContainsDirectory(selectedEntries, ".minecraft"))
            {
                var minecraftRelative = MustRelative(".minecraft");
                using var minecraft = fileSystem.OpenDirectory(
                    selected,
                    minecraftRelative,
                    cancellationToken);
                if (!IsEligibleLocalDirectory(minecraft) ||
                    !ContainsDirectory(Enumerate(minecraft, cancellationToken), "versions"))
                {
                    currentDiagnostics.Add(new DiscoveryDiagnostic(
                        DiscoveryDiagnosticCode.CandidateNotRecognized,
                        "The .minecraft child does not prove an ordinary local versions directory.",
                        selectedPath));
                    return Complete([]);
                }

                var minecraftPath = Path.Combine(selectedPath, ".minecraft");
                var resolved = CreateResolved(
                    selectedPath,
                    selected.Identity,
                    minecraftRelative,
                    Pcl2PathNormalizer.Normalize(minecraftPath),
                    minecraft.Identity,
                    candidate.Origin,
                    manualSelection);
                return Complete([resolved]);
            }

            if (Path.GetFileName(selectedPath).Equals("versions", StringComparison.OrdinalIgnoreCase))
            {
                var minecraftPath = Path.GetDirectoryName(selectedPath);
                if (minecraftPath is not null &&
                    TryProveVersionsOwner(
                        minecraftPath,
                        selected,
                        manualSelection,
                        cancellationToken,
                        out var resolved))
                {
                    return Complete([resolved with { Origin = candidate.Origin }]);
                }
            }

            var versionsPath = Path.GetDirectoryName(selectedPath);
            var owningMinecraftPath = versionsPath is null
                ? null
                : Path.GetDirectoryName(versionsPath);
            if (versionsPath is not null &&
                owningMinecraftPath is not null &&
                Path.GetFileName(versionsPath).Equals("versions", StringComparison.OrdinalIgnoreCase) &&
                TryProveDirectInstance(
                    owningMinecraftPath,
                    versionsPath,
                    selectedPath,
                    selected,
                    manualSelection,
                    cancellationToken,
                    out var directResolved))
            {
                return Complete([directResolved with { Origin = candidate.Origin }]);
            }

            currentDiagnostics.Add(new DiscoveryDiagnostic(
                DiscoveryDiagnosticCode.CandidateNotRecognized,
                "The selection is not a proven PCL root, .minecraft root, versions directory, or direct versions child.",
                selectedPath));
        }
        catch (CapabilityBoundaryException exception)
        {
            currentDiagnostics.Add(new DiscoveryDiagnostic(
                exception is CapabilityLimitExceededException
                    ? DiscoveryDiagnosticCode.DiscoveryLimitReached
                    : DiscoveryDiagnosticCode.CandidateOutsideCapability,
                DiagnosticText.EscapeTechnicalValue(exception.Message),
                selectedPath));
        }

        return Complete([]);
    }

    private bool TryProveVersionsOwner(
        string minecraftPath,
        IVerifiedDirectoryHandle selectedVersions,
        bool manualSelection,
        CancellationToken cancellationToken,
        out Pcl2RootCandidate resolved)
    {
        resolved = null!;
        using var minecraft = fileSystem.OpenRoot(
            minecraftPath,
            FileSystemOpenPurpose.Discovery,
            cancellationToken);
        if (!IsEligibleLocalDirectory(minecraft))
        {
            return false;
        }

        using var versions = fileSystem.OpenDirectory(
            minecraft,
            MustRelative("versions"),
            cancellationToken);
        if (versions.Identity != selectedVersions.Identity)
        {
            return false;
        }

        resolved = CreateResolved(
            Pcl2PathNormalizer.Normalize(minecraftPath),
            minecraft.Identity,
            MustRelative(string.Empty),
            Pcl2PathNormalizer.Normalize(minecraftPath),
            minecraft.Identity,
            Pcl2CandidateOrigin.Manual,
            manualSelection);
        return true;
    }

    private bool TryProveDirectInstance(
        string minecraftPath,
        string versionsPath,
        string selectedPath,
        IVerifiedDirectoryHandle selectedInstance,
        bool manualSelection,
        CancellationToken cancellationToken,
        out Pcl2RootCandidate resolved)
    {
        resolved = null!;
        using var versions = fileSystem.OpenRoot(
            versionsPath,
            FileSystemOpenPurpose.Discovery,
            cancellationToken);
        using var reopenedInstance = fileSystem.OpenDirectory(
            versions,
            MustRelative(Path.GetFileName(selectedPath)),
            cancellationToken);
        if (reopenedInstance.Identity != selectedInstance.Identity)
        {
            return false;
        }

        using var minecraft = fileSystem.OpenRoot(
            minecraftPath,
            FileSystemOpenPurpose.Discovery,
            cancellationToken);
        if (!IsEligibleLocalDirectory(minecraft))
        {
            return false;
        }

        using var reopenedVersions = fileSystem.OpenDirectory(
            minecraft,
            MustRelative("versions"),
            cancellationToken);
        if (reopenedVersions.Identity != versions.Identity)
        {
            return false;
        }

        resolved = CreateResolved(
            Pcl2PathNormalizer.Normalize(minecraftPath),
            minecraft.Identity,
            MustRelative(string.Empty),
            Pcl2PathNormalizer.Normalize(minecraftPath),
            minecraft.Identity,
            Pcl2CandidateOrigin.Manual,
            manualSelection);
        return true;
    }

    private IReadOnlyList<FileSystemEntrySnapshot> Enumerate(
        IVerifiedDirectoryHandle directory,
        CancellationToken cancellationToken)
    {
        budget?.ReserveEnumeration();
        return fileSystem.EnumerateEntries(
            directory,
            MustRelative(string.Empty),
            new EnumerationLimits(MaximumSelectionEntries),
            cancellationToken);
    }

    private static bool ContainsDirectory(
        IEnumerable<FileSystemEntrySnapshot> entries,
        string name) =>
        entries.Any(entry =>
            entry.IsDirectory &&
            entry.RelativePath.Segments.Count == 1 &&
            entry.RelativePath.Segments[0].Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool IsEligibleLocalDirectory(IVerifiedDirectoryHandle handle) =>
        handle.IsLocalVolume && !handle.IsNetworkRedirected;

    private static DiscoveryDiagnostic NetworkRejected(string path) =>
        new(
            DiscoveryDiagnosticCode.NetworkOrDeviceTargetRejected,
            "The selection is not a verified local, non-redirected directory.",
            path);

    private Pcl2RootCandidate CreateResolved(
        string approvedRootPath,
        PhysicalDirectoryIdentity approvedRootIdentity,
        NormalizedRelativePath minecraftRootRelativePath,
        string minecraftRootPath,
        PhysicalDirectoryIdentity minecraftRootIdentity,
        Pcl2CandidateOrigin origin,
        bool manualSelection) =>
        new(minecraftRootPath, origin)
        {
            ResolvedAccess = new Pcl2ResolvedRootAccess(
                approvedRootPath,
                approvedRootIdentity,
                minecraftRootRelativePath,
                minecraftRootPath,
                minecraftRootIdentity,
                manualSelectionAuthority)
            {
                ProofFileSystem = fileSystem,
                ManualSelectionProvenance = manualSelection
                    ? new ManualSelectionProvenance(
                        manualSelectionAuthority,
                        approvedRootPath,
                        approvedRootIdentity)
                    : null,
            },
        };

    private static NormalizedRelativePath MustRelative(string value)
    {
        if (!NormalizedRelativePath.TryCreate(value, out var relative, out var rejection) || relative is null)
        {
            throw new InvalidOperationException(rejection ?? "A fixed resolver-relative path was rejected.");
        }

        return relative;
    }
}
