namespace BlockFerry.Core.Transactions;

internal interface ITargetContentStabilityGate
{
    bool WaitUntilStable(string targetRootPath, CancellationToken cancellationToken);
}

internal sealed class NoTargetContentStabilityGate : ITargetContentStabilityGate
{
    internal static NoTargetContentStabilityGate Instance { get; } = new();

    private NoTargetContentStabilityGate()
    {
    }

    public bool WaitUntilStable(string targetRootPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRootPath);
        cancellationToken.ThrowIfCancellationRequested();
        return true;
    }
}

internal sealed class WindowsTargetContentStabilityGate : ITargetContentStabilityGate
{
    private const int MaximumEntries = 100_000;
    private static readonly TimeSpan DefaultQuietWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DefaultMaximumWait = TimeSpan.FromSeconds(90);
    private readonly TimeSpan quietWindow;
    private readonly TimeSpan maximumWait;

    internal WindowsTargetContentStabilityGate()
        : this(DefaultQuietWindow, DefaultMaximumWait)
    {
    }

    internal WindowsTargetContentStabilityGate(TimeSpan quietWindow, TimeSpan maximumWait)
    {
        if (quietWindow <= TimeSpan.Zero || maximumWait < quietWindow)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quietWindow),
                "The stability windows must be positive and ordered.");
        }

        this.quietWindow = quietWindow;
        this.maximumWait = maximumWait;
    }

    public bool WaitUntilStable(string targetRootPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRootPath);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var fullPath = Path.GetFullPath(targetRootPath);
            var root = new DirectoryInfo(fullPath);
            if (!root.Exists ||
                string.Equals(
                    root.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    root.Root.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase) ||
                root.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            var lastActivityTicks = DateTimeOffset.MinValue.UtcTicks;
            var watcherFailed = 0;
            using var watcher = new FileSystemWatcher(root.FullName)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName |
                               NotifyFilters.DirectoryName |
                               NotifyFilters.CreationTime |
                               NotifyFilters.LastWrite |
                               NotifyFilters.Size,
            };
            FileSystemEventHandler changed = (_, _) =>
                RecordActivity(ref lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);
            RenamedEventHandler renamed = (_, _) =>
                RecordActivity(ref lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);
            ErrorEventHandler error = (_, _) => Interlocked.Exchange(ref watcherFailed, 1);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += renamed;
            watcher.Error += error;
            watcher.EnableRaisingEvents = true;

            var newest = FindNewestWrite(root, cancellationToken);
            // Do not let the initial scan replace a newer watcher event that arrived
            // while the tree was being enumerated.
            RecordActivity(ref lastActivityTicks, newest.UtcTicks);
            var deadline = DateTimeOffset.UtcNow + maximumWait;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref watcherFailed) != 0)
                {
                    return false;
                }

                var observed = new DateTimeOffset(
                    Interlocked.Read(ref lastActivityTicks),
                    TimeSpan.Zero);
                if (DateTimeOffset.UtcNow - observed >= quietWindow)
                {
                    return true;
                }

                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(250));
            }

            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or global::System.Security.SecurityException)
        {
            return false;
        }
    }

    private static void RecordActivity(ref long location, long candidate)
    {
        while (true)
        {
            var observed = Interlocked.Read(ref location);
            if (candidate <= observed)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref location, candidate, observed) == observed)
            {
                return;
            }
        }
    }

    private static DateTimeOffset FindNewestWrite(
        DirectoryInfo root,
        CancellationToken cancellationToken)
    {
        var newest = new DateTimeOffset(root.LastWriteTimeUtc, TimeSpan.Zero);
        var options = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
            MaxRecursionDepth = 64,
            RecurseSubdirectories = true,
            ReturnSpecialDirectories = false,
        };
        var count = 0;
        foreach (var entry in root.EnumerateFileSystemInfos("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
            if (count > MaximumEntries)
            {
                throw new IOException("The bounded target stability scan was exceeded.");
            }

            var write = new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero);
            if (write > newest)
            {
                newest = write;
            }
        }

        return newest;
    }
}
