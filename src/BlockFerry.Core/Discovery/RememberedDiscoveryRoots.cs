using System.Collections.ObjectModel;

namespace BlockFerry.Core.Discovery;

public sealed record RememberedDiscoveryRoots
{
    private const int MaximumApprovedRoots = 64;
    private readonly IReadOnlyList<string> approvedRoots;

    public RememberedDiscoveryRoots(
        int schemaVersion,
        IReadOnlyList<string> approvedRoots,
        string? lastSourceInstanceId,
        string? lastTargetInstanceId)
    {
        ArgumentNullException.ThrowIfNull(approvedRoots);
        SchemaVersion = schemaVersion;
        this.approvedRoots = SnapshotApprovedRoots(approvedRoots);
        LastSourceInstanceId = lastSourceInstanceId;
        LastTargetInstanceId = lastTargetInstanceId;
    }

    public static RememberedDiscoveryRoots Empty { get; } =
        new(1, [], null, null);

    public int SchemaVersion { get; }
    public IReadOnlyList<string> ApprovedRoots => approvedRoots;
    public string? LastSourceInstanceId { get; }
    public string? LastTargetInstanceId { get; }

    internal static RememberedDiscoveryRoots CreateFrozen(
        int schemaVersion,
        IReadOnlyList<string> approvedRoots,
        string? lastSourceInstanceId,
        string? lastTargetInstanceId) =>
        new(schemaVersion, approvedRoots, lastSourceInstanceId, lastTargetInstanceId);

    private static ReadOnlyCollection<string> SnapshotApprovedRoots(
        IReadOnlyList<string> approvedRoots)
    {
        var snapshot = new List<string>(MaximumApprovedRoots);
        using var enumerator = approvedRoots.GetEnumerator();
        while (snapshot.Count < MaximumApprovedRoots && enumerator.MoveNext())
        {
            snapshot.Add(enumerator.Current);
        }

        if (snapshot.Count == MaximumApprovedRoots && enumerator.MoveNext())
        {
            throw new ArgumentOutOfRangeException(
                nameof(approvedRoots),
                $"At most {MaximumApprovedRoots} remembered roots may be supplied.");
        }

        return Array.AsReadOnly(snapshot.ToArray());
    }
}
