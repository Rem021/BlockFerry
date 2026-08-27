using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using BlockFerry.Core.System;

namespace BlockFerry.TestSupport;

public sealed class FixtureSandbox : IDisposable
{
    private bool disposed;
    private readonly object ownerToken = new();
    private readonly Dictionary<string, FixtureRootProof> issuedRoots =
        new(StringComparer.OrdinalIgnoreCase);

    private FixtureSandbox(string rootPath)
    {
        RootPath = rootPath;
        Directory.CreateDirectory(rootPath);
        RootProof = IssueRootProof(rootPath);
    }

    public string RootPath { get; }
    public FixtureRootProof RootProof { get; }

    public static FixtureSandbox Create()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString("D"));
        return new FixtureSandbox(rootPath);
    }

    public string CreateGuidDirectory()
    {
        var path = AllocateGuidPath();
        Directory.CreateDirectory(path);
        _ = IssueRootProof(path);
        return path;
    }

    public FixtureRootProof GetRootProof(string absolutePath)
    {
        ArgumentNullException.ThrowIfNull(absolutePath);
        var normalized = NormalizeAbsolutePath(absolutePath);
        if (!issuedRoots.TryGetValue(normalized, out var proof) ||
            !ReferenceEquals(proof.OwnerToken, ownerToken))
        {
            throw new InvalidOperationException(
                "The directory was not issued as an authorized root by this fixture sandbox.");
        }

        return proof;
    }

    public FixtureRootProof AuthorizeExistingDirectory(string absolutePath)
    {
        ArgumentNullException.ThrowIfNull(absolutePath);
        EnsureContained(absolutePath);
        var normalized = NormalizeAbsolutePath(absolutePath);
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException(
                "Only an existing directory inside this GUID sandbox can become a fixture root.");
        }

        return issuedRoots.TryGetValue(normalized, out var existing)
            ? existing
            : IssueRootProof(normalized);
    }

    public string AllocateGuidPath() =>
        Path.Combine(RootPath, Guid.NewGuid().ToString("D"));

    public string CreateDirectory(string relativePath)
    {
        var path = Resolve(relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    public void MoveDirectory(string sourcePath, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(destinationPath);
        EnsureContained(sourcePath);
        EnsureContained(destinationPath);
        if (!Directory.Exists(sourcePath) ||
            Directory.Exists(destinationPath) ||
            File.Exists(destinationPath))
        {
            throw new InvalidOperationException(
                "The fixture move requires one existing source and one absent destination.");
        }

        Directory.Move(sourcePath, destinationPath);
    }

    public string WriteBytes(string relativePath, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var path = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public string SnapshotTree(string absoluteRoot)
    {
        ArgumentNullException.ThrowIfNull(absoluteRoot);
        EnsureContained(absoluteRoot);
        var normalizedRoot = NormalizeAbsolutePath(absoluteRoot);
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException("The fixture tree root does not exist.");
        }

        var entries = new List<string>();
        foreach (var path in Directory.EnumerateDirectories(
                     normalizedRoot,
                     "*",
                     SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            var info = new DirectoryInfo(path);
            entries.Add(
                $"D|{Path.GetRelativePath(normalizedRoot, path)}|{(uint)info.Attributes}|{info.LastWriteTimeUtc.Ticks}");
        }

        foreach (var path in Directory.EnumerateFiles(
                     normalizedRoot,
                     "*",
                     SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(path);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            entries.Add(
                $"F|{Path.GetRelativePath(normalizedRoot, path)}|{info.Length}|{(uint)info.Attributes}|{info.LastWriteTimeUtc.Ticks}|{hash}");
        }

        return string.Join('\n', entries);
    }

    public string CreateZip(
        string relativePath,
        params (string Name, byte[] Bytes)[] entries) =>
        CreateZipCore(relativePath, CompressionLevel.NoCompression, entries);

    public string CreateCompressedZip(
        string relativePath,
        params (string Name, byte[] Bytes)[] entries) =>
        CreateZipCore(relativePath, CompressionLevel.SmallestSize, entries);

    public void SetZipEncryptedFlagsForTest(string relativePath)
    {
        var path = Resolve(relativePath);
        var bytes = File.ReadAllBytes(path);
        var localHeaders = 0;
        var centralHeaders = 0;
        for (var index = 0; index <= bytes.Length - 10; index++)
        {
            if (bytes[index] == 0x50 &&
                bytes[index + 1] == 0x4B &&
                bytes[index + 2] == 0x03 &&
                bytes[index + 3] == 0x04)
            {
                bytes[index + 6] |= 0x01;
                localHeaders++;
            }
            else if (bytes[index] == 0x50 &&
                     bytes[index + 1] == 0x4B &&
                     bytes[index + 2] == 0x01 &&
                     bytes[index + 3] == 0x02)
            {
                bytes[index + 8] |= 0x01;
                centralHeaders++;
            }
        }

        if (localHeaders == 0 || localHeaders != centralHeaders)
        {
            throw new InvalidOperationException(
                "The fixture ZIP did not contain matching local and central headers.");
        }

        File.WriteAllBytes(path, bytes);
    }

    private string CreateZipCore(
        string relativePath,
        CompressionLevel compressionLevel,
        params (string Name, byte[] Bytes)[] entries)
    {
        var path = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var entry in entries)
        {
            var created = archive.CreateEntry(entry.Name, compressionLevel);
            using var destination = created.Open();
            destination.Write(entry.Bytes);
        }

        return path;
    }

    public bool TryCreateDirectoryJunction(string junctionPath, string targetPath)
    {
        EnsureContained(junctionPath);
        EnsureContained(targetPath);

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                Directory.CreateSymbolicLink(junctionPath, targetPath);
                return IsReparsePoint(junctionPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return false;
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junctionPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        process.WaitForExit();
        return process.ExitCode == 0 && IsReparsePoint(junctionPath);
    }

    public void DeleteLink(string linkPath)
    {
        EnsureContained(linkPath);
        if (Directory.Exists(linkPath))
        {
            Directory.Delete(linkPath);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (!Directory.Exists(RootPath))
        {
            return;
        }

        var normalized = Path.GetFullPath(RootPath).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(normalized);
        var leaf = Path.GetFileName(normalized);
        var expectedParent = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(parent, expectedParent, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(leaf, "D", out _))
        {
            throw new InvalidOperationException($"Refusing to clean unexpected fixture root: {normalized}");
        }

        Directory.Delete(normalized, recursive: true);
    }

    private string Resolve(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var path = Path.GetFullPath(Path.Combine(RootPath, relativePath));
        EnsureContained(path);
        return path;
    }

    private void EnsureContained(string path)
    {
        var normalizedRoot = Path.GetFullPath(RootPath).TrimEnd(Path.DirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);
        if (!normalizedPath.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Fixture path escaped its GUID root: {normalizedPath}");
        }
    }

    private static bool IsReparsePoint(string path) =>
        Directory.Exists(path) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private FixtureRootProof IssueRootProof(string absolutePath)
    {
        var normalized = NormalizeAbsolutePath(absolutePath);
        var capability = new WindowsFileSystemCapability();
        using var handle = capability.OpenRoot(
            normalized,
            FileSystemOpenPurpose.Discovery,
            CancellationToken.None);
        var proof = new FixtureRootProof(
            ownerToken,
            normalized,
            Guid.NewGuid(),
            handle.FinalPath,
            handle.Identity);
        issuedRoots.Add(normalized, proof);
        return proof;
    }

    private static string NormalizeAbsolutePath(string path) =>
        Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
}
