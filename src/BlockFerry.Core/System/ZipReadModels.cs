namespace BlockFerry.Core.System;

public sealed record ZipReadLimits(
    int MaximumEntries,
    int MaximumEntryBytes,
    long MaximumTotalBytes,
    long MaximumArchiveBytes,
    long MaximumCentralDirectoryBytes);
