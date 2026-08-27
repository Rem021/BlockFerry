using System.Collections.ObjectModel;

namespace BlockFerry.Core.System;

public interface IProcessInventory
{
    ProcessInventorySnapshot Capture(CancellationToken cancellationToken);

    IProcessMonitor StartMonitor();
}

public interface IProcessMonitor : IDisposable
{
    event EventHandler? InventoryChanged;

    ProcessInventorySnapshot Capture(CancellationToken cancellationToken);
}

public sealed class ProcessInventoryEntry
{
    private const int MaximumCommandLineUtf16Length = 1_048_576;
    private readonly string? commandLine;

    private ProcessInventoryEntry(
        int processId,
        string imageName,
        bool isCommandLineReadable,
        string? commandLine)
    {
        ProcessId = processId;
        ImageName = imageName;
        IsCommandLineReadable = isCommandLineReadable;
        this.commandLine = commandLine;
    }

    public int ProcessId { get; }

    public string ImageName { get; }

    public bool IsCommandLineReadable { get; }

    internal string? CommandLine => commandLine;

    public static ProcessInventoryEntry Readable(
        int processId,
        string imageName,
        string commandLine)
    {
        Validate(processId, imageName);
        ArgumentNullException.ThrowIfNull(commandLine);
        if (commandLine.Length > MaximumCommandLineUtf16Length)
        {
            throw new ArgumentOutOfRangeException(nameof(commandLine));
        }

        return new ProcessInventoryEntry(processId, imageName, true, commandLine);
    }

    public static ProcessInventoryEntry Unreadable(int processId, string imageName)
    {
        Validate(processId, imageName);
        return new ProcessInventoryEntry(processId, imageName, false, null);
    }

    public override string ToString() =>
        $"Process {ProcessId}: {ImageName}; command line {(IsCommandLineReadable ? "readable" : "unreadable")}";

    private static void Validate(int processId, string imageName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageName);
        if (imageName.Length > 64 || imageName.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(nameof(imageName));
        }
    }
}

public sealed class ProcessInventorySnapshot
{
    private ProcessInventorySnapshot(IReadOnlyList<ProcessInventoryEntry> entries)
    {
        Entries = entries;
    }

    public IReadOnlyList<ProcessInventoryEntry> Entries { get; }

    public static ProcessInventorySnapshot Create(IEnumerable<ProcessInventoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var copy = entries.Take(4_097).ToArray();
        if (copy.Length > 4_096 || copy.Any(entry => entry is null))
        {
            throw new ArgumentException("The process inventory exceeds its bound.", nameof(entries));
        }

        Array.Sort(copy, static (left, right) => left.ProcessId.CompareTo(right.ProcessId));
        return new ProcessInventorySnapshot(
            new ReadOnlyCollection<ProcessInventoryEntry>(copy));
    }

    public override string ToString() => $"Process inventory: {Entries.Count} bounded candidates";
}
