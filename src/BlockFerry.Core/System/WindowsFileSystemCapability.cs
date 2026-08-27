using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace BlockFerry.Core.System;

/// <summary>
/// Provides bounded read-only Windows filesystem observations rooted in retained handles.
/// Every opened segment uses FILE_OPEN_REPARSE_POINT and full FileIdInfo identity.
/// </summary>
public sealed class WindowsFileSystemCapability : IFileSystemCapability
{
    private const uint FileListDirectory = 0x00000001;
    private const uint FileReadAttributes = 0x00000080;
    private const uint GenericRead = 0x80000000;
    private const uint Synchronize = 0x00100000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint ObjectCaseInsensitive = 0x00000040;
    private const uint FileOpen = 1;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint DuplicateSameAccess = 0x00000002;
    private const int ErrorNoMoreFiles = 18;
    private const int StatusSuccess = 0;
    private const int StatusNoSuchFile = unchecked((int)0xC000000F);
    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
    private const int StatusObjectPathNotFound = unchecked((int)0xC000003A);

    private readonly object owner = new();
    private readonly IWindowsHandleVolumeMetadataReader volumeMetadataReader;

    public WindowsFileSystemCapability()
        : this(new NativeWindowsHandleVolumeMetadataReader())
    {
    }

    internal WindowsFileSystemCapability(IWindowsHandleVolumeMetadataReader volumeMetadataReader)
    {
        ArgumentNullException.ThrowIfNull(volumeMetadataReader);
        this.volumeMetadataReader = volumeMetadataReader;
    }

    public IVerifiedDirectoryHandle OpenRoot(
        string absolutePath,
        FileSystemOpenPurpose purpose,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(absolutePath);
        if (string.IsNullOrWhiteSpace(absolutePath) ||
            !Path.IsPathFullyQualified(absolutePath) ||
            absolutePath.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            absolutePath.StartsWith("\\\\.\\", StringComparison.Ordinal))
        {
            throw new CapabilityBoundaryException("A normal fully-qualified filesystem root path is required.");
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(absolutePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CapabilityBoundaryException("The requested filesystem root path is invalid.", exception);
        }

        var volumeRoot = Path.GetPathRoot(normalized);
        if (string.IsNullOrEmpty(volumeRoot))
        {
            throw new CapabilityBoundaryException("The requested filesystem root has no volume root.");
        }

        SafeFileHandle? current = null;
        try
        {
            current = OpenAbsoluteVolumeRoot(volumeRoot);
            var remainder = normalized[volumeRoot.Length..].TrimEnd('\\', '/');
            if (remainder.Length > 0)
            {
                foreach (var segment in remainder.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var next = OpenRelative(
                        current,
                        segment,
                        RelativeObjectKind.Directory,
                        allowMissing: false,
                        out _);
                    current.Dispose();
                    current = next!;
                }
            }

            return CreateDirectoryHandle(current, volumeRoot);
        }
        catch
        {
            current?.Dispose();
            throw;
        }
    }

    public IVerifiedDirectoryHandle OpenDirectory(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var retainedRoot = RequireHandle(root);
        cancellationToken.ThrowIfCancellationRequested();
        var opened = OpenRelativeDirectory(retainedRoot, relativePath, cancellationToken);
        try
        {
            return CreateDirectoryHandle(opened, retainedRoot.VolumeRoot);
        }
        catch
        {
            opened.Dispose();
            throw;
        }
    }

    public IReadOnlyList<FileSystemEntrySnapshot> EnumerateEntries(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        EnumerationLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaximumEntries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "The enumeration limit cannot be negative.");
        }

        var retainedRoot = RequireHandle(root);
        using var directoryHandle = OpenRelativeDirectory(retainedRoot, relativePath, cancellationToken);
        var results = new List<FileSystemEntrySnapshot>();
        try
        {
            foreach (var name in EnumerateChildNames(
                         directoryHandle,
                         limits.MaximumEntries,
                         cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var entryHandle = OpenRelative(
                    directoryHandle,
                    name,
                    RelativeObjectKind.Any,
                    allowMissing: false,
                    out _)!;
                var information = ReadBasicInformation(entryHandle);
                var isDirectory = (information.FileAttributes & FileAttributeDirectory) != 0;
                var combined = relativePath.Value.Length == 0
                    ? name
                    : relativePath.Value + "\\" + name;
                if (!NormalizedRelativePath.TryCreate(combined, out var entryPath, out var rejection))
                {
                    throw new CapabilityBoundaryException(
                        $"An enumerated leaf could not be represented safely: {rejection}");
                }

                results.Add(new FileSystemEntrySnapshot(
                    entryPath!,
                    isDirectory,
                    isDirectory ? 0 : ReadLength(information),
                    (FileAttributes)information.FileAttributes));
            }
        }
        catch (CapabilityBoundaryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CapabilityBoundaryException("The retained directory could not be enumerated safely.", exception);
        }

        results.Sort((left, right) => StringComparer.Ordinal.Compare(
            left.RelativePath.Value,
            right.RelativePath.Value));
        return Array.AsReadOnly(results.ToArray());
    }

    internal static ReadOnlyCollection<string> EnumerateChildNames(
        SafeFileHandle directoryHandle,
        int maximumEntries,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 64 * 1024;
        var names = new List<string>();
        var buffer = Marshal.AllocHGlobal(bufferSize);
        var parsedBuffer = new byte[bufferSize];
        var informationClass = FileInfoByHandleClass.FileFullDirectoryRestartInfo;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!GetFileInformationByHandleEx(
                        directoryHandle,
                        informationClass,
                        buffer,
                        bufferSize))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreFiles)
                    {
                        break;
                    }

                    throw new CapabilityBoundaryException(
                        $"The retained directory handle could not be enumerated: {new Win32Exception(error).Message}");
                }

                informationClass = FileInfoByHandleClass.FileFullDirectoryInfo;
                Marshal.Copy(buffer, parsedBuffer, 0, bufferSize);
                foreach (var name in WindowsDirectoryRecordParser.Parse(
                             parsedBuffer,
                             maximumEntries - names.Count))
                {
                    names.Add(name);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        names.Sort(StringComparer.Ordinal);
        return Array.AsReadOnly(names.ToArray());
    }

    public BoundedFileSnapshot ReadFile(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        FileReadLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(limits);
        ValidateFileReadLimit(limits.MaximumBytes);
        var retainedRoot = RequireHandle(root);
        using var fileHandle = OpenRelativeFile(
            retainedRoot,
            relativePath,
            allowMissing: true,
            cancellationToken,
            out var isMissing);
        if (isMissing)
        {
            return BoundedFileSnapshot.Missing();
        }

        return ReadBoundedSnapshot(fileHandle!, limits.MaximumBytes, cancellationToken);
    }

    public IReadOnlyDictionary<string, BoundedFileSnapshot> ReadZipEntries(
        IVerifiedDirectoryHandle root,
        NormalizedRelativePath zipPath,
        IReadOnlySet<string> allowedEntryNames,
        ZipReadLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(zipPath);
        ArgumentNullException.ThrowIfNull(allowedEntryNames);
        ArgumentNullException.ThrowIfNull(limits);
        ValidateZipLimits(limits);
        var exactAllowedEntryNames = allowedEntryNames.ToHashSet(StringComparer.Ordinal);
        foreach (var allowedName in exactAllowedEntryNames)
        {
            if (!IsSafeZipDeclarationName(allowedName))
            {
                throw new CapabilityBoundaryException("The ZIP declaration allowlist contains an unsafe entry name.");
            }
        }

        var retainedRoot = RequireHandle(root);
        using var archiveHandle = OpenRelativeFile(
            retainedRoot,
            zipPath,
            allowMissing: true,
            cancellationToken,
            out var isMissing);
        if (isMissing)
        {
            return new ReadOnlyDictionary<string, BoundedFileSnapshot>(
                new Dictionary<string, BoundedFileSnapshot>(StringComparer.Ordinal));
        }

        var archiveIdentity = ReadFileIdentity(archiveHandle!);
        var archiveLength = ReadLength(ReadBasicInformation(archiveHandle!));
        var results = new Dictionary<string, BoundedFileSnapshot>(StringComparer.Ordinal);
        long totalBytes = 0;
        try
        {
            using var stream = new FileStream(archiveHandle!, FileAccess.Read);
            ZipArchivePreflight.Validate(stream, archiveLength, limits, cancellationToken);
            stream.Position = 0;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > limits.MaximumEntries)
            {
                throw new CapabilityLimitExceededException(
                    $"ZIP archive exceeded the {limits.MaximumEntries} entry limit.");
            }

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsZipDeclarationAlias(entry.FullName, exactAllowedEntryNames))
                {
                    throw new CapabilityBoundaryException(
                        "The ZIP archive contains a case or traversal alias of an allowlisted declaration.");
                }

                if (!exactAllowedEntryNames.Contains(entry.FullName))
                {
                    continue;
                }

                if (results.ContainsKey(entry.FullName))
                {
                    throw new CapabilityBoundaryException(
                        "The ZIP archive contains a duplicate allowlisted declaration name.");
                }

                if (entry.Length < 0 || entry.Length > limits.MaximumEntryBytes)
                {
                    throw new CapabilityLimitExceededException(
                        $"ZIP entry '{entry.FullName}' exceeded its byte limit.");
                }

                totalBytes = checked(totalBytes + entry.Length);
                if (totalBytes > limits.MaximumTotalBytes)
                {
                    throw new CapabilityLimitExceededException("ZIP allowlisted entries exceeded the total byte limit.");
                }

                using var entryStream = entry.Open();
                var bytes = ReadExactlyBounded(entryStream, entry.Length, limits.MaximumEntryBytes, cancellationToken);
                results.Add(
                    entry.FullName,
                    new BoundedFileSnapshot(
                        exists: true,
                        bytes,
                        Convert.ToHexString(SHA256.HashData(bytes)),
                        new FileObjectMetadata(
                            entry.LastWriteTime,
                            0,
                            archiveIdentity)));
            }
        }
        catch (CapabilityBoundaryException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException)
        {
            throw new CapabilityBoundaryException("The verified ZIP archive could not be read safely.", exception);
        }

        return new ReadOnlyDictionary<string, BoundedFileSnapshot>(results);
    }

    public VolumeCapabilitySnapshot InspectVolume(
        IVerifiedDirectoryHandle root,
        CancellationToken cancellationToken)
    {
        var retainedRoot = RequireHandle(root);
        cancellationToken.ThrowIfCancellationRequested();
        return CreateVolumeSnapshot(
            retainedRoot.VolumeRoot,
            volumeMetadataReader.Read(retainedRoot.NativeHandle));
    }

    private WindowsVerifiedDirectoryHandle CreateDirectoryHandle(
        SafeFileHandle handle,
        string volumeRoot)
    {
        var information = ReadBasicInformation(handle);
        if ((information.FileAttributes & FileAttributeDirectory) == 0)
        {
            throw new CapabilityBoundaryException("The opened root is not a directory.");
        }

        var identity = ReadDirectoryIdentity(handle);
        var finalPath = ReadFinalPath(handle);
        var volume = CreateVolumeSnapshot(volumeRoot, volumeMetadataReader.Read(handle));
        return new WindowsVerifiedDirectoryHandle(
            owner,
            handle,
            finalPath,
            identity,
            volumeRoot,
            volume.IsLocalVolume,
            volume.IsNetworkRedirected);
    }

    private static VolumeCapabilitySnapshot CreateVolumeSnapshot(
        string volumeRoot,
        WindowsHandleVolumeMetadata metadata)
    {
        var isNetworkRedirected =
            metadata.RemoteProtocol == WindowsRemoteProtocolDisposition.Remote;
        var isLocalVolume =
            metadata.VolumeInformationSucceeded &&
            metadata.RemoteProtocol == WindowsRemoteProtocolDisposition.Local;
        return new VolumeCapabilitySnapshot(
            volumeRoot,
            metadata.VolumeInformationSucceeded ? metadata.FileSystemName : string.Empty,
            isLocalVolume,
            isNetworkRedirected,
            isLocalVolume && metadata.SupportsPersistentAcls);
    }

    private static SafeFileHandle OpenAbsoluteVolumeRoot(string volumeRoot)
    {
        var handle = CreateFile(
            volumeRoot,
            FileReadAttributes | FileListDirectory | Synchronize,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new CapabilityBoundaryException(
                $"The volume root could not be opened read-only: {new Win32Exception(error).Message}");
        }

        try
        {
            ValidateOpenedObject(handle, RelativeObjectKind.Directory);
            _ = ReadDirectoryIdentity(handle);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenRelativeDirectory(
        WindowsVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        CancellationToken cancellationToken)
    {
        var current = Duplicate(root.NativeHandle);
        try
        {
            foreach (var segment in relativePath.Segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var next = OpenRelative(
                    current,
                    segment,
                    RelativeObjectKind.Directory,
                    allowMissing: false,
                    out _);
                current.Dispose();
                current = next!;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static SafeFileHandle? OpenRelativeFile(
        WindowsVerifiedDirectoryHandle root,
        NormalizedRelativePath relativePath,
        bool allowMissing,
        CancellationToken cancellationToken,
        out bool isMissing)
    {
        if (relativePath.Segments.Count == 0)
        {
            throw new CapabilityBoundaryException("A file read requires a non-empty relative path.");
        }

        isMissing = false;
        var current = Duplicate(root.NativeHandle);
        try
        {
            for (var index = 0; index < relativePath.Segments.Count - 1; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var next = OpenRelative(
                    current,
                    relativePath.Segments[index],
                    RelativeObjectKind.Directory,
                    allowMissing,
                    out var ancestorMissing);
                if (ancestorMissing)
                {
                    isMissing = true;
                    return null;
                }

                current.Dispose();
                current = next!;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var file = OpenRelative(
                current,
                relativePath.Segments[^1],
                RelativeObjectKind.File,
                allowMissing,
                out isMissing);
            return file;
        }
        finally
        {
            current.Dispose();
        }
    }

    private static SafeFileHandle? OpenRelative(
        SafeFileHandle parent,
        string name,
        RelativeObjectKind kind,
        bool allowMissing,
        out bool isMissing)
    {
        isMissing = false;
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeUnicodeString>());
        var parentReferenceAdded = false;
        try
        {
            var unicodeString = new NativeUnicodeString
            {
                Length = checked((ushort)(name.Length * sizeof(char))),
                MaximumLength = checked((ushort)((name.Length + 1) * sizeof(char))),
                Buffer = nameBuffer,
            };
            Marshal.StructureToPtr(unicodeString, unicodeStringPointer, fDeleteOld: false);
            parent.DangerousAddRef(ref parentReferenceAdded);
            var objectAttributes = new NativeObjectAttributes
            {
                Length = checked((uint)Marshal.SizeOf<NativeObjectAttributes>()),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodeStringPointer,
                Attributes = ObjectCaseInsensitive,
            };
            var desiredAccess = kind == RelativeObjectKind.File
                ? GenericRead | Synchronize
                : FileReadAttributes | FileListDirectory | Synchronize;
            var shareAccess = kind == RelativeObjectKind.File
                ? FileShareRead
                : FileShareRead | FileShareWrite | FileShareDelete;
            var createOptions = FileSynchronousIoNonAlert | FileOpenReparsePoint;
            if (kind == RelativeObjectKind.File)
            {
                createOptions |= FileNonDirectoryFile;
            }
            else if (kind == RelativeObjectKind.Directory)
            {
                createOptions |= FileDirectoryFile;
            }

            var status = NtCreateFile(
                out var rawHandle,
                desiredAccess,
                ref objectAttributes,
                out _,
                IntPtr.Zero,
                FileAttributeNormal,
                shareAccess,
                FileOpen,
                createOptions,
                IntPtr.Zero,
                0);
            if (status == StatusSuccess)
            {
                var handle = new SafeFileHandle(rawHandle, ownsHandle: true);
                try
                {
                    ValidateOpenedObject(handle, kind);
                    return handle;
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            if (rawHandle != IntPtr.Zero && rawHandle != new IntPtr(-1))
            {
                using var unexpectedHandle = new SafeFileHandle(rawHandle, ownsHandle: true);
            }

            if (allowMissing && status is StatusObjectNameNotFound or StatusNoSuchFile)
            {
                isMissing = true;
                return null;
            }

            var win32Error = RtlNtStatusToDosError(status);
            throw new CapabilityBoundaryException(
                $"A retained-handle relative read failed with NTSTATUS 0x{unchecked((uint)status):X8}: " +
                new Win32Exception(checked((int)win32Error)).Message);
        }
        finally
        {
            if (parentReferenceAdded)
            {
                parent.DangerousRelease();
            }

            Marshal.FreeHGlobal(unicodeStringPointer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static void ValidateOpenedObject(SafeFileHandle handle, RelativeObjectKind kind)
    {
        var information = ReadBasicInformation(handle);
        if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new CapabilityBoundaryException(
                "A symbolic link, junction, mount point, or other reparse point was rejected.");
        }

        var isDirectory = (information.FileAttributes & FileAttributeDirectory) != 0;
        if (kind == RelativeObjectKind.Directory && !isDirectory)
        {
            throw new CapabilityBoundaryException("A required directory segment resolved to a file.");
        }

        if (kind == RelativeObjectKind.File && isDirectory)
        {
            throw new CapabilityBoundaryException("A required file leaf resolved to a directory.");
        }

        _ = ReadFileId(handle);
    }

    private static BoundedFileSnapshot ReadBoundedSnapshot(
        SafeFileHandle handle,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var information = ReadBasicInformation(handle);
        var length = ReadLength(information);
        if (length > maximumBytes || length > int.MaxValue)
        {
            throw new CapabilityLimitExceededException(
                $"File length {length} exceeded the {maximumBytes} byte limit.");
        }

        var identity = ReadFileIdentity(handle);
        byte[] bytes;
        try
        {
            using var stream = new FileStream(handle, FileAccess.Read);
            bytes = ReadExactlyBounded(stream, length, maximumBytes, cancellationToken);
        }
        catch (CapabilityBoundaryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CapabilityBoundaryException("The verified file handle could not be read safely.", exception);
        }

        return new BoundedFileSnapshot(
            exists: true,
            bytes,
            Convert.ToHexString(SHA256.HashData(bytes)),
            new FileObjectMetadata(
                ReadLastWriteTime(information),
                (FileAttributes)information.FileAttributes,
                identity));
    }

    private static byte[] ReadExactlyBounded(
        Stream stream,
        long declaredLength,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (declaredLength < 0 || declaredLength > maximumBytes || declaredLength > int.MaxValue)
        {
            throw new CapabilityLimitExceededException("The declared stream length exceeds its byte limit.");
        }

        var bytes = new byte[checked((int)declaredLength)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new CapabilityBoundaryException("The verified stream ended before its declared length.");
            }

            offset += read;
        }

        if (stream.ReadByte() != -1)
        {
            throw new CapabilityLimitExceededException("The verified stream exceeded its declared or configured length.");
        }

        return bytes;
    }

    private static WindowsVerifiedDirectoryHandle ValidateLiveHandle(IVerifiedDirectoryHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle is not WindowsVerifiedDirectoryHandle retained || retained.IsDisposed)
        {
            throw new CapabilityBoundaryException("The directory handle is not a live Windows capability handle.");
        }

        return retained;
    }

    private WindowsVerifiedDirectoryHandle RequireHandleOwned(IVerifiedDirectoryHandle handle)
    {
        var retained = ValidateLiveHandle(handle);
        if (!ReferenceEquals(retained.Owner, owner))
        {
            throw new CapabilityBoundaryException("The directory handle belongs to a different filesystem capability.");
        }

        return retained;
    }

    private WindowsVerifiedDirectoryHandle RequireHandle(IVerifiedDirectoryHandle handle) =>
        RequireHandleOwned(handle);

    private static SafeFileHandle Duplicate(SafeFileHandle source)
    {
        if (!DuplicateHandle(
                GetCurrentProcess(),
                source,
                GetCurrentProcess(),
                out var duplicate,
                0,
                false,
                DuplicateSameAccess))
        {
            throw new CapabilityBoundaryException(
                $"The retained directory handle could not be duplicated: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }

        return duplicate;
    }

    private static ByHandleFileInformation ReadBasicInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new CapabilityBoundaryException(
                $"Filesystem object metadata could not be read: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }

        return information;
    }

    private static FileIdInfo ReadFileId(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileIdInfo,
                out var information,
                checked((uint)Marshal.SizeOf<FileIdInfo>())))
        {
            throw new CapabilityBoundaryException(
                $"The required full 128-bit FileIdInfo was unavailable: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }

        if (information.FileId.LowPart == 0 && information.FileId.HighPart == 0)
        {
            throw new CapabilityBoundaryException("The required full 128-bit FileIdInfo was empty.");
        }

        return information;
    }

    private static PhysicalDirectoryIdentity ReadDirectoryIdentity(SafeFileHandle handle)
    {
        var information = ReadFileId(handle);
        return new PhysicalDirectoryIdentity(
            information.VolumeSerialNumber,
            information.FileId.LowPart,
            information.FileId.HighPart);
    }

    private static PhysicalFileIdentity ReadFileIdentity(SafeFileHandle handle)
    {
        var information = ReadFileId(handle);
        return new PhysicalFileIdentity(
            information.VolumeSerialNumber,
            information.FileId.LowPart,
            information.FileId.HighPart);
    }

    private static string ReadFinalPath(SafeFileHandle handle)
    {
        var requiredLength = GetFinalPathNameByHandle(handle, IntPtr.Zero, 0, 0);
        if (requiredLength == 0)
        {
            throw new CapabilityBoundaryException(
                $"The final handle path could not be sized: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }

        var buffer = Marshal.AllocHGlobal(checked((int)(requiredLength + 1) * sizeof(char)));
        try
        {
            var written = GetFinalPathNameByHandle(handle, buffer, requiredLength + 1, 0);
            if (written == 0 || written > requiredLength)
            {
                throw new CapabilityBoundaryException("The final handle path could not be read consistently.");
            }

            var finalPath = Marshal.PtrToStringUni(buffer, checked((int)written))
                ?? throw new CapabilityBoundaryException("The final handle path was empty.");
            if (finalPath.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            {
                return "\\\\" + finalPath[8..];
            }

            return finalPath.StartsWith("\\\\?\\", StringComparison.Ordinal)
                ? finalPath[4..]
                : finalPath;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static long ReadLength(ByHandleFileInformation information)
    {
        var unsignedLength = ((ulong)information.FileSizeHigh << 32) | information.FileSizeLow;
        if (unsignedLength > long.MaxValue)
        {
            throw new CapabilityLimitExceededException("The filesystem object length exceeds supported bounds.");
        }

        return checked((long)unsignedLength);
    }

    private static DateTimeOffset ReadLastWriteTime(ByHandleFileInformation information)
    {
        var fileTime = ((long)information.LastWriteTime.HighDateTime << 32) |
            information.LastWriteTime.LowDateTime;
        return DateTimeOffset.FromFileTime(fileTime);
    }

    private static void ValidateFileReadLimit(long maximumBytes)
    {
        if (maximumBytes < 0 || maximumBytes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBytes),
                "A file-read limit must be between zero and Int32.MaxValue bytes.");
        }
    }

    private static void ValidateZipLimits(ZipReadLimits limits)
    {
        if (limits.MaximumEntries < 0 ||
            limits.MaximumEntryBytes < 0 ||
            limits.MaximumTotalBytes < 0 ||
            limits.MaximumArchiveBytes < 0 ||
            limits.MaximumCentralDirectoryBytes < 0 ||
            limits.MaximumTotalBytes > 512L * 1024 * 1024 ||
            limits.MaximumArchiveBytes > 512L * 1024 * 1024 ||
            limits.MaximumCentralDirectoryBytes > 64L * 1024 * 1024 ||
            limits.MaximumEntries > 65_536 ||
            limits.MaximumEntryBytes > 256 * 1024 * 1024 ||
            limits.MaximumCentralDirectoryBytes > limits.MaximumArchiveBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "ZIP limits must be non-negative and bounded.");
        }
    }

    private static bool IsSafeZipDeclarationName(string candidate)
    {
        if (string.IsNullOrEmpty(candidate) ||
            candidate[0] == '/' ||
            candidate.Contains('\\', StringComparison.Ordinal) ||
            candidate.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        return candidate.Split('/').All(segment =>
            segment.Length > 0 && segment is not "." and not "..");
    }

    private static bool IsZipDeclarationAlias(
        string candidate,
        HashSet<string> allowedEntryNames)
    {
        if (allowedEntryNames.Contains(candidate))
        {
            return false;
        }

        var slashNormalized = candidate.Replace('\\', '/');
        foreach (var allowedName in allowedEntryNames)
        {
            if (string.Equals(slashNormalized, allowedName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var hasTraversalSegment = slashNormalized
                .Split('/')
                .Any(segment => segment is "." or "..");
            if (hasTraversalSegment &&
                slashNormalized.EndsWith('/' + allowedName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows filesystem capabilities require Windows handle APIs.");
        }
    }

    private enum RelativeObjectKind
    {
        Any,
        Directory,
        File,
    }

    private sealed class WindowsVerifiedDirectoryHandle : IVerifiedDirectoryHandle
    {
        public WindowsVerifiedDirectoryHandle(
            object owner,
            SafeFileHandle nativeHandle,
            string finalPath,
            PhysicalDirectoryIdentity identity,
            string volumeRoot,
            bool isLocalVolume,
            bool isNetworkRedirected)
        {
            Owner = owner;
            NativeHandle = nativeHandle;
            FinalPath = finalPath;
            Identity = identity;
            VolumeRoot = volumeRoot;
            IsLocalVolume = isLocalVolume;
            IsNetworkRedirected = isNetworkRedirected;
        }

        public object Owner { get; }
        public SafeFileHandle NativeHandle { get; }
        public string VolumeRoot { get; }
        public bool IsDisposed { get; private set; }
        public string FinalPath { get; }
        public PhysicalDirectoryIdentity Identity { get; }
        public bool IsLocalVolume { get; }
        public bool IsNetworkRedirected { get; }

        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            NativeHandle.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private enum FileInfoByHandleClass
    {
        FileIdInfo = 0x12,
        FileFullDirectoryInfo = 14,
        FileFullDirectoryRestartInfo = 15,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public NativeFileId128 FileId;
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct NativeFileId128
    {
        public ulong LowPart;
        public ulong HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeIoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
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

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        IntPtr fileInformation,
        int bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        IntPtr filePath,
        uint filePathCharacterCount,
        uint flags);

    [DllImport("kernel32.dll", EntryPoint = "DuplicateHandle", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcessHandle,
        SafeFileHandle sourceHandle,
        IntPtr targetProcessHandle,
        out SafeFileHandle targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("ntdll.dll", EntryPoint = "NtCreateFile", ExactSpelling = true)]
    private static extern int NtCreateFile(
        out IntPtr fileHandle,
        uint desiredAccess,
        ref NativeObjectAttributes objectAttributes,
        out NativeIoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll", EntryPoint = "RtlNtStatusToDosError", ExactSpelling = true)]
    private static extern uint RtlNtStatusToDosError(int status);
#pragma warning restore SYSLIB1054
}
