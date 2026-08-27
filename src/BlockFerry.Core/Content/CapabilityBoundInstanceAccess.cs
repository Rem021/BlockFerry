using BlockFerry.Core.Discovery;
using BlockFerry.Core.System;
using System.Security.Cryptography;

namespace BlockFerry.Core.Content;

internal interface IReadOnlyInstanceAccess
{
    ContentInstanceIdentity Identity { get; }

    ContentFileSnapshot Read(
        ContentRelativePath relativePath,
        ContentReadLimits limits,
        CancellationToken cancellationToken);

    IReadOnlyList<ContentDirectoryEntry> Enumerate(
        ContentRelativePath relativeDirectory,
        ContentEnumerationLimits limits,
        CancellationToken cancellationToken);

    IReadOnlyDictionary<string, ContentFileSnapshot> ReadZipEntries(
        ContentRelativePath zipPath,
        IReadOnlySet<string> allowedEntryNames,
        ContentZipReadLimits limits,
        CancellationToken cancellationToken);
}

internal readonly record struct ContentInstanceIdentity(
    string InstanceId,
    string? MinecraftVersion,
    ContentFileIdentity GameRootIdentity);

internal sealed record ContentReadLimits(long MaximumBytes);

internal sealed record ContentEnumerationLimits(int MaximumEntries);

internal sealed record ContentZipReadLimits(
    int MaximumEntries,
    int MaximumEntryBytes,
    long MaximumTotalBytes,
    long MaximumArchiveBytes,
    long MaximumCentralDirectoryBytes);

internal sealed record ContentDirectoryEntry(
    ContentRelativePath RelativePath,
    bool IsDirectory,
    long Length,
    uint WindowsFileAttributes);

internal sealed class ContentAccessLease : IDisposable
{
    private readonly ContentAccessLifetime lifetime;
    private readonly string sourceId;
    private readonly string targetId;
    private readonly ContentFileIdentity sourceRootIdentity;
    private readonly ContentFileIdentity targetRootIdentity;
    private IVerifiedDirectoryHandle? sourceRoot;
    private IVerifiedDirectoryHandle? targetRoot;

    internal ContentAccessLease(
        DiscoverySession session,
        string sourceId,
        string targetId,
        ContentFileIdentity sourceRootIdentity,
        ContentFileIdentity targetRootIdentity,
        IReadOnlyInstanceAccess source,
        IReadOnlyInstanceAccess target,
        IVerifiedDirectoryHandle sourceRoot,
        IVerifiedDirectoryHandle targetRoot,
        ContentAccessLifetime lifetime)
    {
        Session = session;
        this.sourceId = sourceId;
        this.targetId = targetId;
        this.sourceRootIdentity = sourceRootIdentity;
        this.targetRootIdentity = targetRootIdentity;
        Source = source;
        Target = target;
        this.sourceRoot = sourceRoot;
        this.targetRoot = targetRoot;
        this.lifetime = lifetime;
    }

    internal DiscoverySession Session { get; }

    internal long Generation => Session.Generation;

    internal IReadOnlyInstanceAccess Source { get; }

    internal IReadOnlyInstanceAccess Target { get; }

    internal bool IsActive => lifetime.IsActive && Session.IsActive;

    internal bool IsBoundTo(
        DiscoverySession session,
        string sourceId,
        string targetId) =>
        IsActive &&
        ReferenceEquals(Session, session) &&
        string.Equals(this.sourceId, sourceId, StringComparison.Ordinal) &&
        string.Equals(this.targetId, targetId, StringComparison.Ordinal) &&
        Source.Identity.GameRootIdentity == sourceRootIdentity &&
        Target.Identity.GameRootIdentity == targetRootIdentity;

    internal ContentProbeContext CreateProbeContext(
        AdapterCompatibilityEvidence compatibility)
    {
        ThrowIfUnavailable();
        return ContentProbeContext.Create(this, compatibility);
    }

    internal void ThrowIfUnavailable() => lifetime.ThrowIfUnavailable();

    public void Dispose()
    {
        if (!lifetime.TryDeactivate())
        {
            return;
        }

        var target = Interlocked.Exchange(ref targetRoot, null);
        var source = Interlocked.Exchange(ref sourceRoot, null);
        Exception? failure = null;
        try
        {
            target?.Dispose();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            source?.Dispose();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        if (failure is not null)
        {
            global::System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(failure)
                .Throw();
        }
    }
}

internal sealed class ContentAccessLifetime(DiscoverySession session)
{
    private int active = 1;

    internal bool IsActive => Volatile.Read(ref active) == 1;

    internal bool TryDeactivate() => Interlocked.Exchange(ref active, 0) == 1;

    internal void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(!IsActive, typeof(ContentAccessLease));
        ObjectDisposedException.ThrowIf(!session.IsActive, session);
    }
}

internal sealed class CapabilityBoundInstanceAccess : IReadOnlyInstanceAccess
{
    private readonly IFileSystemCapability fileSystem;
    private readonly IVerifiedDirectoryHandle root;
    private readonly ContentAccessLifetime lifetime;
    private readonly ContentAccessBudget budget;

    internal CapabilityBoundInstanceAccess(
        IFileSystemCapability fileSystem,
        IVerifiedDirectoryHandle root,
        ContentInstanceIdentity identity,
        ContentAccessLifetime lifetime,
        ContentAccessBudget budget)
    {
        this.fileSystem = fileSystem;
        this.root = root;
        Identity = identity;
        this.lifetime = lifetime;
        this.budget = budget;
    }

    public ContentInstanceIdentity Identity { get; }

    public ContentFileSnapshot Read(
        ContentRelativePath relativePath,
        ContentReadLimits limits,
        CancellationToken cancellationToken)
    {
        lifetime.ThrowIfUnavailable();
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();
        var systemPath = RoundTripPath(relativePath, allowEmpty: false);
        using var reservation = budget.ReserveRead(limits.MaximumBytes);
        var snapshot = fileSystem.ReadFile(
            root,
            systemPath,
            new FileReadLimits(limits.MaximumBytes),
            cancellationToken);
        var converted = ConvertSnapshot(relativePath, snapshot, limits.MaximumBytes);
        reservation.Commit(converted.Length, 0);
        return converted;
    }

    public IReadOnlyList<ContentDirectoryEntry> Enumerate(
        ContentRelativePath relativeDirectory,
        ContentEnumerationLimits limits,
        CancellationToken cancellationToken)
    {
        lifetime.ThrowIfUnavailable();
        ArgumentNullException.ThrowIfNull(relativeDirectory);
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();
        var systemPath = RoundTripPath(relativeDirectory, allowEmpty: true);
        using var reservation = budget.ReserveEnumeration(limits.MaximumEntries);
        var rawEntries = fileSystem.EnumerateEntries(
            root,
            systemPath,
            new EnumerationLimits(limits.MaximumEntries),
            cancellationToken);
        var copied = CopyBounded(
            rawEntries,
            limits.MaximumEntries,
            "The capability returned too many directory entries.");
        var converted = new ContentDirectoryEntry[copied.Count];
        for (var index = 0; index < copied.Count; index++)
        {
            var entry = copied[index] ??
                throw new CapabilityBoundaryException(
                    "The capability returned a null directory entry.");
            if (entry.Length < 0 || entry.IsDirectory && entry.Length != 0 ||
                !ContentRelativePath.TryCreate(
                    entry.RelativePath.Value,
                    out var entryPath,
                    out _) ||
                !string.Equals(
                    entryPath!.Value,
                    entry.RelativePath.Value,
                    StringComparison.Ordinal) ||
                !IsImmediateChild(relativeDirectory, entryPath))
            {
                throw new CapabilityBoundaryException(
                    "The capability returned an invalid or relabeled directory entry.");
            }

            converted[index] = new ContentDirectoryEntry(
                entryPath,
                entry.IsDirectory,
                entry.Length,
                (uint)entry.Attributes);
        }

        reservation.Commit(0, converted.Length);
        return new ContentReadOnlyList<ContentDirectoryEntry>(converted);
    }

    public IReadOnlyDictionary<string, ContentFileSnapshot> ReadZipEntries(
        ContentRelativePath zipPath,
        IReadOnlySet<string> allowedEntryNames,
        ContentZipReadLimits limits,
        CancellationToken cancellationToken)
    {
        lifetime.ThrowIfUnavailable();
        ArgumentNullException.ThrowIfNull(zipPath);
        ArgumentNullException.ThrowIfNull(allowedEntryNames);
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();
        var systemPath = RoundTripPath(zipPath, allowEmpty: false);
        ValidateZipLimits(limits);
        using var reservation = budget.ReserveZip(
            limits.MaximumEntries,
            limits.MaximumTotalBytes);
        var allowedCopy = CopyBounded(
            allowedEntryNames,
            limits.MaximumEntries,
            "The ZIP allowlist exceeded its entry bound.");
        if (allowedCopy.Count == 0 ||
            allowedCopy.Any(name => !IsSafeZipEntryName(name)) ||
            allowedCopy.Distinct(StringComparer.Ordinal).Count() != allowedCopy.Count)
        {
            throw new CapabilityBoundaryException("The ZIP allowlist is invalid.");
        }

        var detachedAllowed = new ReadOnlySet<string>(allowedCopy, StringComparer.Ordinal);
        var rawEntries = fileSystem.ReadZipEntries(
            root,
            systemPath,
            detachedAllowed,
            new ZipReadLimits(
                limits.MaximumEntries,
                limits.MaximumEntryBytes,
                limits.MaximumTotalBytes,
                limits.MaximumArchiveBytes,
                limits.MaximumCentralDirectoryBytes),
            cancellationToken);
        var copiedEntries = CopyBounded(
            rawEntries,
            limits.MaximumEntries,
            "The capability returned too many ZIP entries.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var converted = new List<KeyValuePair<string, ContentFileSnapshot>>(copiedEntries.Count);
        long totalBytes = 0;
        foreach (var (entryName, snapshot) in copiedEntries)
        {
            if (!detachedAllowed.Contains(entryName) ||
                !seen.Add(entryName) ||
                snapshot is null ||
                !snapshot.Exists)
            {
                throw new CapabilityBoundaryException(
                    "The capability returned an undeclared or duplicate ZIP entry.");
            }

            var contentSnapshot = ConvertSnapshot(
                zipPath,
                snapshot,
                limits.MaximumEntryBytes);
            totalBytes = checked(totalBytes + contentSnapshot.Length);
            if (totalBytes > limits.MaximumTotalBytes)
            {
                throw new CapabilityLimitExceededException(
                    "The ZIP result exceeded its total byte bound.");
            }

            converted.Add(new KeyValuePair<string, ContentFileSnapshot>(
                entryName,
                contentSnapshot));
        }

        converted.Sort((left, right) =>
            StringComparer.Ordinal.Compare(left.Key, right.Key));
        reservation.Commit(totalBytes, converted.Count);
        return new ContentReadOnlyDictionary<string, ContentFileSnapshot>(
            converted,
            StringComparer.Ordinal);
    }

    private static NormalizedRelativePath RoundTripPath(
        ContentRelativePath contentPath,
        bool allowEmpty)
    {
        if ((!allowEmpty && contentPath.Value.Length == 0) ||
            !NormalizedRelativePath.TryCreate(
                contentPath.Value,
                out var systemPath,
                out _) ||
            !string.Equals(
                systemPath!.Value,
                contentPath.Value,
                StringComparison.Ordinal))
        {
            throw new CapabilityBoundaryException(
                "The content path could not be represented exactly at the capability boundary.");
        }

        return systemPath;
    }

    private static ContentFileSnapshot ConvertSnapshot(
        ContentRelativePath relativePath,
        BoundedFileSnapshot snapshot,
        long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var bytes = snapshot.CopyBytes();
        if (snapshot.Length != bytes.LongLength ||
            snapshot.Length < 0 ||
            snapshot.Length > maximumBytes ||
            snapshot.Exists && !string.Equals(
                snapshot.Sha256,
                Convert.ToHexString(SHA256.HashData(bytes)),
                StringComparison.Ordinal))
        {
            throw new CapabilityBoundaryException(
                "The capability returned an inconsistent bounded snapshot.");
        }

        var identity = snapshot.Metadata.Identity is { } fileIdentity
            ? new ContentFileIdentity(
                fileIdentity.VolumeSerialNumber,
                fileIdentity.FileIdLow,
                fileIdentity.FileIdHigh)
            : (ContentFileIdentity?)null;
        return ContentFileSnapshot.Create(
            relativePath,
            snapshot.Exists,
            bytes,
            snapshot.Metadata.LastWriteTimeUtc,
            (uint)snapshot.Metadata.Attributes,
            identity);
    }

    private static bool IsImmediateChild(
        ContentRelativePath directory,
        ContentRelativePath child)
    {
        if (directory.Value.Length == 0)
        {
            return !child.Value.Contains('\\', StringComparison.Ordinal);
        }

        var prefix = directory.Value + "\\";
        return child.Value.StartsWith(prefix, StringComparison.Ordinal) &&
               !child.Value.AsSpan(prefix.Length).Contains('\\');
    }

    private static List<T> CopyBounded<T>(
        IEnumerable<T> source,
        int maximum,
        string failureMessage)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copy = new List<T>(Math.Min(maximum, 256));
        using var enumerator = source.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (copy.Count == maximum)
            {
                throw new CapabilityLimitExceededException(failureMessage);
            }

            copy.Add(enumerator.Current);
        }

        return copy;
    }

    private static void ValidateZipLimits(ContentZipReadLimits limits)
    {
        if (limits.MaximumEntries is <= 0 or > 65_536 ||
            limits.MaximumEntryBytes is <= 0 or > 256 * 1024 * 1024 ||
            limits.MaximumTotalBytes is <= 0 or > 512L * 1024 * 1024 ||
            limits.MaximumArchiveBytes is <= 0 or > 512L * 1024 * 1024 ||
            limits.MaximumCentralDirectoryBytes is <= 0 or > 64L * 1024 * 1024 ||
            limits.MaximumEntryBytes > limits.MaximumTotalBytes ||
            limits.MaximumCentralDirectoryBytes > limits.MaximumArchiveBytes)
        {
            throw new CapabilityLimitExceededException("The ZIP limits are invalid.");
        }
    }

    private static bool IsSafeZipEntryName(string? candidate) =>
        candidate is { Length: > 0 and <= 32_767 } &&
        candidate[0] != '/' &&
        !candidate.Contains('\\', StringComparison.Ordinal) &&
        !candidate.Contains(':', StringComparison.Ordinal) &&
        candidate.Split('/').All(segment =>
            segment.Length is > 0 and <= 255 &&
            segment is not "." and not ".." &&
            segment.All(character => !char.IsControl(character)));
}

internal sealed class ContentAccessBudget(ContentAccessLimits limits)
{
    private readonly object gate = new();
    private int readOperations;
    private int enumerationOperations;
    private int returnedEntries;
    private int reservedEntries;
    private long returnedBytes;
    private long reservedBytes;

    internal ContentBudgetReservation ReserveRead(long maximumBytes)
    {
        lock (gate)
        {
            if (maximumBytes <= 0 ||
                maximumBytes > int.MaxValue ||
                readOperations >= limits.MaximumReadOperations ||
                maximumBytes > limits.MaximumTotalBytes - returnedBytes - reservedBytes)
            {
                throw new CapabilityLimitExceededException(
                    "The shared content read budget was exhausted.");
            }

            readOperations++;
            reservedBytes = checked(reservedBytes + maximumBytes);
            return new ContentBudgetReservation(this, maximumBytes, 0);
        }
    }

    internal ContentBudgetReservation ReserveEnumeration(int maximumEntries)
    {
        lock (gate)
        {
            if (maximumEntries <= 0 ||
                enumerationOperations >= limits.MaximumEnumerationOperations ||
                maximumEntries > limits.MaximumEnumeratedEntries - returnedEntries - reservedEntries)
            {
                throw new CapabilityLimitExceededException(
                    "The shared content enumeration budget was exhausted.");
            }

            enumerationOperations++;
            reservedEntries = checked(reservedEntries + maximumEntries);
            return new ContentBudgetReservation(this, 0, maximumEntries);
        }
    }

    internal ContentBudgetReservation ReserveZip(int maximumEntries, long maximumBytes)
    {
        lock (gate)
        {
            if (maximumEntries <= 0 ||
                maximumBytes <= 0 ||
                readOperations >= limits.MaximumReadOperations ||
                maximumEntries > limits.MaximumEnumeratedEntries - returnedEntries - reservedEntries ||
                maximumBytes > limits.MaximumTotalBytes - returnedBytes - reservedBytes)
            {
                throw new CapabilityLimitExceededException(
                    "The shared content ZIP budget was exhausted.");
            }

            readOperations++;
            reservedEntries = checked(reservedEntries + maximumEntries);
            reservedBytes = checked(reservedBytes + maximumBytes);
            return new ContentBudgetReservation(this, maximumBytes, maximumEntries);
        }
    }

    private void Complete(
        long byteReservation,
        int entryReservation,
        long actualBytes,
        int actualEntries)
    {
        lock (gate)
        {
            reservedBytes -= byteReservation;
            reservedEntries -= entryReservation;
            if (actualBytes < 0 || actualBytes > byteReservation ||
                actualEntries < 0 || actualEntries > entryReservation)
            {
                throw new CapabilityBoundaryException(
                    "The capability exceeded its reserved content budget.");
            }

            returnedBytes = checked(returnedBytes + actualBytes);
            returnedEntries = checked(returnedEntries + actualEntries);
        }
    }

    private void Release(long byteReservation, int entryReservation)
    {
        lock (gate)
        {
            reservedBytes -= byteReservation;
            reservedEntries -= entryReservation;
        }
    }

    internal sealed class ContentBudgetReservation(
        ContentAccessBudget owner,
        long maximumBytes,
        int maximumEntries) : IDisposable
    {
        private int active = 1;

        internal void Commit(long actualBytes, int actualEntries)
        {
            ObjectDisposedException.ThrowIf(
                Interlocked.Exchange(ref active, 0) == 0,
                this);
            owner.Complete(maximumBytes, maximumEntries, actualBytes, actualEntries);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref active, 0) == 1)
            {
                owner.Release(maximumBytes, maximumEntries);
            }
        }
    }
}

internal sealed class ContentReadOnlyList<T>(IEnumerable<T> source) : IReadOnlyList<T>
{
    private readonly T[] values = source.ToArray();

    public int Count => values.Length;

    public T this[int index] => values[index];

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)values).GetEnumerator();

    global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() =>
        GetEnumerator();
}

internal sealed class ContentReadOnlyDictionary<TKey, TValue> : IReadOnlyDictionary<TKey, TValue>
    where TKey : notnull
{
    private readonly KeyValuePair<TKey, TValue>[] entries;
    private readonly Dictionary<TKey, TValue> lookup;

    internal ContentReadOnlyDictionary(
        IEnumerable<KeyValuePair<TKey, TValue>> source,
        IEqualityComparer<TKey> comparer)
    {
        entries = source.ToArray();
        lookup = new Dictionary<TKey, TValue>(entries.Length, comparer);
        foreach (var entry in entries)
        {
            lookup.Add(entry.Key, entry.Value);
        }
    }

    public int Count => entries.Length;

    public IEnumerable<TKey> Keys => entries.Select(entry => entry.Key);

    public IEnumerable<TValue> Values => entries.Select(entry => entry.Value);

    public TValue this[TKey key] => lookup[key];

    public bool ContainsKey(TKey key) => lookup.ContainsKey(key);

    public bool TryGetValue(TKey key, out TValue value) => lookup.TryGetValue(key, out value!);

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() =>
        ((IEnumerable<KeyValuePair<TKey, TValue>>)entries).GetEnumerator();

    global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() =>
        GetEnumerator();
}
