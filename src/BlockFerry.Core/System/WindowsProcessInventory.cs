using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BlockFerry.Core.System;

public sealed class WindowsProcessInventory : IProcessInventory
{
    private const int ProcessCommandLineInformation = 60;
    private const int MaximumCommandLineBytes = 128 * 1024;

    public ProcessInventorySnapshot Capture(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Process inventory requires Windows.");
        }

        var entries = new List<ProcessInventoryEntry>();
        foreach (var process in Process.GetProcesses().OrderBy(process => process.Id))
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string imageName;
                try
                {
                    imageName = process.ProcessName;
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    continue;
                }

                if (!IsJavaImage(imageName))
                {
                    continue;
                }

                if (TryReadCommandLine(process, out var commandLine))
                {
                    entries.Add(ProcessInventoryEntry.Readable(process.Id, imageName, commandLine));
                }
                else
                {
                    entries.Add(ProcessInventoryEntry.Unreadable(process.Id, imageName));
                }
            }
        }

        return ProcessInventorySnapshot.Create(entries);
    }

    public IProcessMonitor StartMonitor() => new WindowsProcessMonitor(this);

    internal static bool IsJavaImage(string imageName) =>
        string.Equals(imageName, "java", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(imageName, "javaw", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(imageName, "java.exe", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(imageName, "javaw.exe", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadCommandLine(Process process, out string commandLine)
    {
        commandLine = string.Empty;
        try
        {
            using SafeProcessHandle handle = process.SafeHandle;
            _ = NtQueryInformationProcess(
                handle,
                ProcessCommandLineInformation,
                IntPtr.Zero,
                0,
                out var requiredBytes);
            if (requiredBytes == 0 || requiredBytes > MaximumCommandLineBytes)
            {
                return false;
            }

            var buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
            try
            {
                var status = NtQueryInformationProcess(
                    handle,
                    ProcessCommandLineInformation,
                    buffer,
                    requiredBytes,
                    out var returnedBytes);
                if (status != 0 || returnedBytes > requiredBytes)
                {
                    return false;
                }

                var value = Marshal.PtrToStructure<UnicodeString>(buffer);
                if (value.Length == 0 ||
                    value.Length > value.MaximumLength ||
                    (value.Length & 1) != 0 ||
                    !BufferContains(buffer, requiredBytes, value.Buffer, value.Length))
                {
                    return false;
                }

                commandLine = Marshal.PtrToStringUni(value.Buffer, value.Length / 2) ?? string.Empty;
                return commandLine.Length > 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return false;
        }
    }

    private static bool BufferContains(
        IntPtr buffer,
        uint bufferLength,
        IntPtr candidate,
        ushort candidateLength)
    {
        var start = buffer.ToInt64();
        var end = checked(start + bufferLength);
        var candidateStart = candidate.ToInt64();
        var candidateEnd = checked(candidateStart + candidateLength);
        return candidateStart >= start && candidateEnd <= end;
    }

    private static bool IsRecoverable(Exception exception) => exception is
        InvalidOperationException or
        global::System.ComponentModel.Win32Exception or
        NotSupportedException or
        UnauthorizedAccessException or
        IOException;

#pragma warning disable SYSLIB1054
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryInformationProcess(
        SafeProcessHandle processHandle,
        int processInformationClass,
        IntPtr processInformation,
        uint processInformationLength,
        out uint returnLength);
#pragma warning restore SYSLIB1054

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct UnicodeString
    {
        internal readonly ushort Length;
        internal readonly ushort MaximumLength;
        internal readonly IntPtr Buffer;
    }
}

internal sealed class WindowsProcessMonitor : IProcessMonitor
{
    private readonly WindowsProcessInventory _inventory;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private string _fingerprint;
    private bool _capturing;
    private bool _disposed;

    internal WindowsProcessMonitor(WindowsProcessInventory inventory)
    {
        _inventory = inventory;
        _fingerprint = CaptureFingerprint(CancellationToken.None);
        _timer = new Timer(Poll, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
    }

    public event EventHandler? InventoryChanged;

    public ProcessInventorySnapshot Capture(CancellationToken cancellationToken) =>
        _inventory.Capture(cancellationToken);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Dispose();
        }
    }

    private void Poll(object? state)
    {
        lock (_gate)
        {
            if (_disposed || _capturing)
            {
                return;
            }

            _capturing = true;
        }

        var changed = false;
        try
        {
            var next = CaptureFingerprint(CancellationToken.None);
            lock (_gate)
            {
                if (!_disposed && !string.Equals(_fingerprint, next, StringComparison.Ordinal))
                {
                    _fingerprint = next;
                    changed = true;
                }
            }
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException or
            global::System.ComponentModel.Win32Exception or
            NotSupportedException)
        {
            changed = true;
        }
        finally
        {
            lock (_gate)
            {
                _capturing = false;
            }
        }

        if (changed)
        {
            InventoryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private string CaptureFingerprint(CancellationToken cancellationToken)
    {
        var snapshot = _inventory.Capture(cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in snapshot.Entries)
        {
            Append(hash, entry.ProcessId.ToString(global::System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, entry.ImageName);
            Append(hash, entry.IsCommandLineReadable ? "1" : "0");
            if (entry.CommandLine is { } commandLine)
            {
                Append(hash, commandLine);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            hash.AppendData(bytes);
            hash.AppendData([0]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
