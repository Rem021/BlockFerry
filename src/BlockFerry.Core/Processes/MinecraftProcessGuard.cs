using System.Runtime.InteropServices;
using BlockFerry.Core.System;
using BlockFerry.Core.Transactions;
using Microsoft.Win32.SafeHandles;

namespace BlockFerry.Core.Processes;

internal interface IProcessPathIdentityResolver
{
    bool TryResolve(string path, out PhysicalDirectoryIdentity identity);
}

internal enum MinecraftProcessBlockReason
{
    RelatedGameRunning,
    UnreadableCandidate,
    PathCouldNotBeVerified,
    InventoryUnavailable,
}

internal sealed class MinecraftProcessBlockedException : InvalidOperationException
{
    internal MinecraftProcessBlockedException(MinecraftProcessBlockReason reason)
        : base(reason switch
        {
            MinecraftProcessBlockReason.RelatedGameRunning => "Close the source or target Minecraft game before synchronizing.",
            MinecraftProcessBlockReason.UnreadableCandidate => "A Java process could not be proven unrelated to Minecraft.",
            MinecraftProcessBlockReason.PathCouldNotBeVerified => "A Minecraft game directory could not be verified safely.",
            _ => "The running-process safety check is unavailable.",
        })
    {
        Reason = reason;
    }

    internal MinecraftProcessBlockReason Reason { get; }
}

internal readonly record struct MinecraftProcessEvaluation(
    bool IsSafe,
    MinecraftProcessBlockReason? BlockReason);

internal sealed class MinecraftProcessEvaluator(
    MinecraftCommandLineParser parser,
    IProcessPathIdentityResolver pathIdentityResolver)
{
    private readonly MinecraftCommandLineParser _parser =
        parser ?? throw new ArgumentNullException(nameof(parser));
    private readonly IProcessPathIdentityResolver _pathIdentityResolver =
        pathIdentityResolver ?? throw new ArgumentNullException(nameof(pathIdentityResolver));

    internal MinecraftProcessEvaluation Evaluate(
        ProcessInventorySnapshot snapshot,
        PhysicalDirectoryIdentity sourceIdentity,
        PhysicalDirectoryIdentity targetIdentity,
        IReadOnlyList<string> approvedArgumentFileRoots)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (var entry in snapshot.Entries)
        {
            var evidence = _parser.Parse(entry, approvedArgumentFileRoots);
            if (evidence.Classification == MinecraftProcessClassification.Unrelated)
            {
                continue;
            }

            if (evidence.Classification == MinecraftProcessClassification.UnsafeCandidate)
            {
                return new MinecraftProcessEvaluation(
                    false,
                    MinecraftProcessBlockReason.UnreadableCandidate);
            }

            if (evidence.GameDirectory is null ||
                !_pathIdentityResolver.TryResolve(evidence.GameDirectory, out var identity))
            {
                return new MinecraftProcessEvaluation(
                    false,
                    MinecraftProcessBlockReason.PathCouldNotBeVerified);
            }

            if (identity == sourceIdentity || identity == targetIdentity)
            {
                return new MinecraftProcessEvaluation(
                    false,
                    MinecraftProcessBlockReason.RelatedGameRunning);
            }
        }

        return new MinecraftProcessEvaluation(true, null);
    }
}

internal sealed class MinecraftProcessGuard
{
    private readonly IProcessInventory _inventory;
    private readonly MinecraftProcessEvaluator _evaluator;

    internal MinecraftProcessGuard()
        : this(
            new WindowsProcessInventory(),
            new MinecraftProcessEvaluator(
                new MinecraftCommandLineParser(new WindowsMinecraftArgumentFileReader()),
                new WindowsProcessPathIdentityResolver()))
    {
    }

    internal MinecraftProcessGuard(
        IProcessInventory inventory,
        MinecraftProcessEvaluator evaluator)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    }

    internal MinecraftProcessEvaluation Evaluate(
        PhysicalDirectoryIdentity sourceIdentity,
        PhysicalDirectoryIdentity targetIdentity,
        IReadOnlyList<string> approvedArgumentFileRoots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approvedArgumentFileRoots);
        try
        {
            return _evaluator.Evaluate(
                _inventory.Capture(cancellationToken),
                sourceIdentity,
                targetIdentity,
                approvedArgumentFileRoots);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new MinecraftProcessEvaluation(
                false,
                MinecraftProcessBlockReason.InventoryUnavailable);
        }
    }

    internal MinecraftProcessGuardSession Begin(
        MigrationTransactionCoordinator.ExecutionAuthority authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var pair = authority.CurrentPairEvidence;
        return new MinecraftProcessGuardSession(
            _inventory,
            _evaluator,
            pair.Source.GameRoot.Identity,
            pair.Target.GameRoot.Identity,
            [pair.Source.GameRoot.CanonicalPath, pair.Target.GameRoot.CanonicalPath],
            cancellationToken);
    }

    internal MinecraftProcessGuardSession Begin(
        RecoveryExecutionAuthority authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!authority.IsActive)
        {
            throw new InvalidOperationException("The recovery authority is no longer active.");
        }

        var locator = authority.Locator;
        return new MinecraftProcessGuardSession(
            _inventory,
            _evaluator,
            locator.TargetRootIdentity,
            locator.TargetRootIdentity,
            [locator.CanonicalTargetRoot],
            cancellationToken);
    }
}

internal sealed class MinecraftProcessGuardSession : IDisposable
{
    private readonly IProcessInventory _inventory;
    private readonly MinecraftProcessEvaluator _evaluator;
    private readonly PhysicalDirectoryIdentity _sourceIdentity;
    private readonly PhysicalDirectoryIdentity _targetIdentity;
    private readonly IReadOnlyList<string> _approvedArgumentFileRoots;
    private IProcessMonitor? _monitor;
    private int _blocked;
    private int _disposed;

    internal MinecraftProcessGuardSession(
        IProcessInventory inventory,
        MinecraftProcessEvaluator evaluator,
        PhysicalDirectoryIdentity sourceIdentity,
        PhysicalDirectoryIdentity targetIdentity,
        IReadOnlyList<string> approvedArgumentFileRoots,
        CancellationToken cancellationToken)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _sourceIdentity = sourceIdentity;
        _targetIdentity = targetIdentity;
        _approvedArgumentFileRoots = approvedArgumentFileRoots?.ToArray() ??
            throw new ArgumentNullException(nameof(approvedArgumentFileRoots));
        EnsureSafeSnapshot(_inventory.Capture(cancellationToken));
        _monitor = _inventory.StartMonitor();
        _monitor.InventoryChanged += Monitor_InventoryChanged;
        try
        {
            EnsureSafeSnapshot(_monitor.Capture(cancellationToken));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal bool IsBlocked => Volatile.Read(ref _blocked) != 0;

    internal void EnsureSafeBeforeMutation(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (IsBlocked)
        {
            throw new MinecraftProcessBlockedException(MinecraftProcessBlockReason.InventoryUnavailable);
        }

        try
        {
            EnsureSafeSnapshot(_monitor!.Capture(cancellationToken));
        }
        catch (MinecraftProcessBlockedException)
        {
            Interlocked.Exchange(ref _blocked, 1);
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Interlocked.Exchange(ref _blocked, 1);
            throw new MinecraftProcessBlockedException(MinecraftProcessBlockReason.InventoryUnavailable);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var monitor = Interlocked.Exchange(ref _monitor, null);
        if (monitor is not null)
        {
            monitor.InventoryChanged -= Monitor_InventoryChanged;
            monitor.Dispose();
        }
    }

    private void Monitor_InventoryChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            EnsureSafeSnapshot(_monitor!.Capture(CancellationToken.None));
        }
        catch
        {
            Interlocked.Exchange(ref _blocked, 1);
        }
    }

    private void EnsureSafeSnapshot(ProcessInventorySnapshot snapshot)
    {
        var evaluation = _evaluator.Evaluate(
            snapshot,
            _sourceIdentity,
            _targetIdentity,
            _approvedArgumentFileRoots);
        if (!evaluation.IsSafe)
        {
            throw new MinecraftProcessBlockedException(
                evaluation.BlockReason ?? MinecraftProcessBlockReason.InventoryUnavailable);
        }
    }
}

internal sealed partial class WindowsProcessPathIdentityResolver : IProcessPathIdentityResolver
{
    private const uint FileReadAttributes = 0x0080;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint ShareReadWriteDelete = 0x00000001 | 0x00000002 | 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    public bool TryResolve(string path, out PhysicalDirectoryIdentity identity)
    {
        identity = default;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            using var handle = CreateFile(
                Path.GetFullPath(path),
                FileReadAttributes,
                ShareReadWriteDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid ||
                !GetFileInformationByHandle(handle, out var basicInformation) ||
                (basicInformation.FileAttributes & FileAttributeReparsePoint) != 0 ||
                !GetFileInformationByHandleEx(
                    handle,
                    FileInfoByHandleClass.FileIdInfo,
                    out var information,
                    checked((uint)Marshal.SizeOf<FileIdInfo>())))
            {
                return false;
            }

            identity = new PhysicalDirectoryIdentity(
                information.VolumeSerialNumber,
                information.FileId.LowPart,
                information.FileId.HighPart);
            return identity != default;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private enum FileInfoByHandleClass
    {
        FileIdInfo = 0x12,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal NativeFileTime CreationTime;
        internal NativeFileTime LastAccessTime;
        internal NativeFileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        internal ulong VolumeSerialNumber;
        internal NativeFileId128 FileId;
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct NativeFileId128
    {
        internal ulong LowPart;
        internal ulong HighPart;
    }

#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        out FileIdInfo fileInformation,
        uint bufferSize);
#pragma warning restore SYSLIB1054
}
