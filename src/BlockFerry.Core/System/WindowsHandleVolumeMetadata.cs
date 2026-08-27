using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BlockFerry.Core.System;

internal enum WindowsRemoteProtocolDisposition
{
    Unknown,
    Local,
    Remote,
}

internal readonly record struct WindowsHandleVolumeMetadata(
    bool VolumeInformationSucceeded,
    string FileSystemName,
    bool SupportsPersistentAcls,
    WindowsRemoteProtocolDisposition RemoteProtocol);

internal interface IWindowsHandleVolumeMetadataReader
{
    WindowsHandleVolumeMetadata Read(SafeFileHandle handle);
}

internal sealed class NativeWindowsHandleVolumeMetadataReader : IWindowsHandleVolumeMetadataReader
{
    private const uint FilePersistentAcls = 0x00000008;
    private const int ErrorInvalidParameter = 87;
    private const int FileRemoteProtocolInfo = 13;
    private const int RemoteProtocolInfoSize = 116;

    public WindowsHandleVolumeMetadata Read(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var fileSystemName = new StringBuilder(64);
        var volumeInformationSucceeded = GetVolumeInformationByHandle(
            handle,
            null,
            0,
            out _,
            out _,
            out var fileSystemFlags,
            fileSystemName,
            fileSystemName.Capacity);

        return new WindowsHandleVolumeMetadata(
            volumeInformationSucceeded,
            volumeInformationSucceeded ? fileSystemName.ToString() : string.Empty,
            volumeInformationSucceeded && (fileSystemFlags & FilePersistentAcls) != 0,
            ReadRemoteProtocolDisposition(handle));
    }

    private static WindowsRemoteProtocolDisposition ReadRemoteProtocolDisposition(
        SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(RemoteProtocolInfoSize);
        try
        {
            if (GetFileInformationByHandleEx(
                    handle,
                    FileRemoteProtocolInfo,
                    buffer,
                    RemoteProtocolInfoSize))
            {
                return WindowsRemoteProtocolDisposition.Remote;
            }

            return Marshal.GetLastWin32Error() == ErrorInvalidParameter
                ? WindowsRemoteProtocolDisposition.Local
                : WindowsRemoteProtocolDisposition.Unknown;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

#pragma warning disable SYSLIB1054
#pragma warning disable CA1838
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandle(
        SafeFileHandle file,
        StringBuilder? volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer,
        int fileSystemNameSize);
#pragma warning restore CA1838

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        IntPtr fileInformation,
        int bufferSize);
#pragma warning restore SYSLIB1054
}
