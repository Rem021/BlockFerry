using BlockFerry.Core.Discovery;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Pcl2;

public enum Pcl2CandidateOrigin
{
    Manual,
    Automatic,
}

public enum Pcl2IsolationMode
{
    Unknown,
    Isolated,
    SharedMinecraftRoot,
}

public enum Pcl2ModLoaderKind
{
    Unknown,
    Vanilla,
    Forge,
    NeoForge,
    Fabric,
    Quilt,
    LiteLoader,
    OptiFine,
}

public enum Pcl2IdentityConfidence
{
    Unknown,
    Low,
    Medium,
    High,
}

public enum Pcl2IdentitySource
{
    Unknown,
    DirectoryName,
    InstanceJson,
    ClientNote,
    Manifest,
}

public sealed record Pcl2RootCandidate(string CandidatePath, Pcl2CandidateOrigin Origin)
{
    internal Pcl2ResolvedRootAccess? ResolvedAccess { get; init; }
}

/// <summary>
/// Contains an untrusted stream of caller-supplied paths. Discovery observes at
/// most 64 raw candidates plus one overflow value before filtering or exact-input
/// deduplication and deliberately performs no implicit environment, registry,
/// drive, or user-profile scan.
/// </summary>
public sealed record Pcl2DiscoveryRequest(IEnumerable<Pcl2RootCandidate> Candidates)
{
    private const int MaximumRawCandidates = 64;

    public Pcl2DiscoveryLimits Limits { get; init; } = new();
    internal bool CandidateInputLimitReached { get; init; }

    public static Pcl2DiscoveryRequest Create(
        IEnumerable<string>? manualCandidatePaths,
        IEnumerable<string>? automaticCandidatePaths)
    {
        var candidates = new List<Pcl2RootCandidate>();
        var exactInputs = new HashSet<(string? CandidatePath, Pcl2CandidateOrigin Origin)>();
        var manualPrefix = Pcl2RawInput.Take(
            manualCandidatePaths ?? [],
            MaximumRawCandidates);
        if (manualPrefix.Failure is not null)
        {
            throw new ArgumentException(
                $"The manual candidate input could not be enumerated: {DiagnosticText.EscapeTechnicalValue(manualPrefix.Failure.Message)}",
                nameof(manualCandidatePaths),
                manualPrefix.Failure);
        }

        AddCandidates(
            manualPrefix.Values,
            Pcl2CandidateOrigin.Manual,
            candidates,
            exactInputs);

        var limitReached = manualPrefix.LimitReached;
        if (!limitReached)
        {
            var automaticPrefix = Pcl2RawInput.Take(
                automaticCandidatePaths ?? [],
                MaximumRawCandidates - manualPrefix.Values.Count);
            if (automaticPrefix.Failure is not null)
            {
                throw new ArgumentException(
                    $"The automatic candidate input could not be enumerated: {DiagnosticText.EscapeTechnicalValue(automaticPrefix.Failure.Message)}",
                    nameof(automaticCandidatePaths),
                    automaticPrefix.Failure);
            }

            AddCandidates(
                automaticPrefix.Values,
                Pcl2CandidateOrigin.Automatic,
                candidates,
                exactInputs);
            limitReached = automaticPrefix.LimitReached;
        }

        return new Pcl2DiscoveryRequest(candidates.AsReadOnly())
        {
            CandidateInputLimitReached = limitReached,
        };
    }

    private static void AddCandidates(
        IEnumerable<string?> rawPaths,
        Pcl2CandidateOrigin origin,
        List<Pcl2RootCandidate> candidates,
        HashSet<(string? CandidatePath, Pcl2CandidateOrigin Origin)> exactInputs)
    {
        foreach (var path in rawPaths)
        {
            if (!exactInputs.Add((path, origin)))
            {
                continue;
            }

            candidates.Add(new Pcl2RootCandidate(path!, origin));
        }
    }
}

internal sealed record Pcl2RawInputPrefix<T>(
    IReadOnlyList<T> Values,
    bool LimitReached,
    Exception? Failure);

internal static class Pcl2RawInput
{
    public static Pcl2RawInputPrefix<T> Take<T>(
        IEnumerable<T> inputs,
        int maximumInputs,
        CancellationToken cancellationToken = default)
    {
        var values = new List<T>(maximumInputs);
        IEnumerator<T>? enumerator = null;
        Exception? failure = null;
        var limitReached = false;
        try
        {
            enumerator = inputs.GetEnumerator();
            while (values.Count < maximumInputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!enumerator.MoveNext())
                {
                    break;
                }

                values.Add(enumerator.Current);
            }

            if (values.Count == maximumInputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                limitReached = enumerator.MoveNext();
            }
        }
        catch (Exception exception) when (IsRecoverableEnumerationFailure(exception, cancellationToken))
        {
            failure = exception;
        }
        finally
        {
            if (enumerator is not null)
            {
                try
                {
                    enumerator.Dispose();
                }
                catch (Exception exception) when (IsRecoverableEnumerationFailure(exception, cancellationToken))
                {
                    failure ??= exception;
                }
            }
        }

        return new Pcl2RawInputPrefix<T>(
            values.AsReadOnly(),
            limitReached,
            failure);
    }

    private static bool IsRecoverableEnumerationFailure(
        Exception exception,
        CancellationToken cancellationToken) =>
        exception is not OutOfMemoryException &&
        !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested);
}

/// <summary>
/// Conservative per-request bounds for read-only PCL discovery. Values above
/// the documented defaults are clamped to those defaults by production.
/// </summary>
public sealed record Pcl2DiscoveryLimits
{
    public int MaximumCandidates { get; init; } = 64;
    public int MaximumInstances { get; init; } = 512;
    public int MaximumEnumerationOperations { get; init; } = 2048;
    public int MaximumReadOperations { get; init; } = 2048;
    public long MaximumTotalReadBytes { get; init; } = 128L * 1024 * 1024;
    public long MaximumFileReadBytes { get; init; } = 4L * 1024 * 1024;

    internal Pcl2DiscoveryLimits Normalize() => new()
    {
        MaximumCandidates = Math.Clamp(MaximumCandidates, 0, 64),
        MaximumInstances = Math.Clamp(MaximumInstances, 0, 512),
        MaximumEnumerationOperations = Math.Clamp(MaximumEnumerationOperations, 0, 2048),
        MaximumReadOperations = Math.Clamp(MaximumReadOperations, 0, 2048),
        MaximumTotalReadBytes = Math.Clamp(MaximumTotalReadBytes, 0, 128L * 1024 * 1024),
        MaximumFileReadBytes = Math.Clamp(MaximumFileReadBytes, 0, 4L * 1024 * 1024),
    };
}

public sealed record Pcl2ModLoader(
    Pcl2ModLoaderKind Kind,
    string? Version,
    string Evidence);

public sealed record Pcl2ModpackIdentity(
    string Name,
    string? Version,
    Pcl2IdentityConfidence Confidence,
    Pcl2IdentitySource Source,
    string Evidence);

public sealed record Pcl2Instance(
    string Id,
    string DisplayName,
    string MinecraftRoot,
    string InstanceRoot,
    string? GameRoot,
    string? InstanceJsonPath,
    string SetupPath,
    Pcl2IsolationMode Isolation,
    string? MinecraftVersion,
    IReadOnlyList<Pcl2ModLoader> ModLoaders,
    Pcl2ModpackIdentity ModpackIdentity,
    bool HasUsableVersionMetadata,
    bool IsSelected,
    IReadOnlyList<Pcl2Diagnostic> Diagnostics)
{
    internal string? DiscoveryProof { get; init; }
    internal Pcl2InstanceCapabilityAccess? CapabilityAccess { get; init; }
}

public sealed record Pcl2MinecraftRoot(
    string RootPath,
    IReadOnlyList<Pcl2CandidateOrigin> Origins,
    string? SelectedInstanceName,
    IReadOnlyList<Pcl2Instance> Instances,
    IReadOnlyList<Pcl2Diagnostic> Diagnostics);

public sealed record Pcl2DiscoveryResult(
    IReadOnlyList<Pcl2MinecraftRoot> Roots,
    IReadOnlyList<Pcl2Diagnostic> Diagnostics)
{
    public IReadOnlyList<Pcl2Instance> Instances =>
        Roots.SelectMany(root => root.Instances).ToArray();
}

internal sealed record Pcl2ResolvedRootAccess(
    string ApprovedRootPath,
    PhysicalDirectoryIdentity ApprovedRootIdentity,
    NormalizedRelativePath MinecraftRootRelativePath,
    string MinecraftRootPath,
    PhysicalDirectoryIdentity MinecraftRootIdentity,
    ManualSelectionAuthority ResolverAuthority)
{
    internal Guid CandidateProofId { get; } = Guid.NewGuid();
    internal IFileSystemCapability? ProofFileSystem { get; init; }
    internal ManualSelectionProvenance? ManualSelectionProvenance { get; init; }
}

internal sealed class ManualSelectionAuthority;

internal sealed class ManualSelectionProvenance(
    ManualSelectionAuthority authority,
    string approvedRootPath,
    PhysicalDirectoryIdentity approvedRootIdentity)
{
    public bool Validates(Pcl2ResolvedRootAccess access) =>
        ReferenceEquals(authority, access.ResolverAuthority) &&
        string.Equals(
            approvedRootPath,
            access.ApprovedRootPath,
            StringComparison.OrdinalIgnoreCase) &&
        approvedRootIdentity == access.ApprovedRootIdentity;
}

internal sealed record Pcl2InstanceCapabilityAccess(
    Pcl2ResolvedRootAccess RootAccess,
    NormalizedRelativePath InstanceRootRelativePath,
    PhysicalDirectoryIdentity InstanceRootIdentity,
    NormalizedRelativePath? GameRootRelativePath,
    PhysicalDirectoryIdentity? GameRootIdentity);

internal sealed class Pcl2DiscoveryBudget(Pcl2DiscoveryLimits requestedLimits)
{
    private readonly Pcl2DiscoveryLimits limits = requestedLimits.Normalize();
    private int enumerationOperations;
    private int readOperations;
    private int instances;
    private long readBytes;

    public int MaximumCandidates => limits.MaximumCandidates;
    public int RemainingInstances => limits.MaximumInstances - instances;

    public void ReserveEnumeration()
    {
        if (enumerationOperations >= limits.MaximumEnumerationOperations)
        {
            throw Limit("The PCL discovery enumeration-operation budget was exhausted.");
        }

        enumerationOperations++;
    }

    public long ReserveRead(long requestedMaximumBytes)
    {
        if (readOperations >= limits.MaximumReadOperations)
        {
            throw Limit("The PCL discovery metadata-read operation budget was exhausted.");
        }

        var remainingBytes = limits.MaximumTotalReadBytes - readBytes;
        if (remainingBytes <= 0 || limits.MaximumFileReadBytes <= 0)
        {
            throw Limit("The PCL discovery aggregate metadata byte budget was exhausted.");
        }

        readOperations++;
        return Math.Min(
            Math.Min(requestedMaximumBytes, limits.MaximumFileReadBytes),
            remainingBytes);
    }

    public void CommitRead(long actualBytes)
    {
        if (actualBytes < 0 || actualBytes > limits.MaximumTotalReadBytes - readBytes)
        {
            throw Limit("The PCL discovery aggregate metadata byte budget was exceeded.");
        }

        readBytes += actualBytes;
    }

    public void ConsumeInstances(int count)
    {
        if (count < 0 || count > RemainingInstances)
        {
            throw Limit("The PCL discovery instance budget was exceeded.");
        }

        instances += count;
    }

    private static CapabilityLimitExceededException Limit(string message) => new(message);
}
