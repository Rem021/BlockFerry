using System.Collections.ObjectModel;

namespace BlockFerry.Core.System;

public enum FileSystemOpenPurpose
{
    Discovery,
    AppStorage,
    MigrationSource,
    MigrationTarget,
}

public class CapabilityBoundaryException : IOException
{
    public CapabilityBoundaryException(string message)
        : base(message)
    {
    }

    public CapabilityBoundaryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class CapabilityLimitExceededException : CapabilityBoundaryException
{
    public CapabilityLimitExceededException(string message)
        : base(message)
    {
    }
}

public readonly record struct PhysicalDirectoryIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh);

public readonly record struct PhysicalFileIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh);

public sealed class NormalizedRelativePath
{
    private const int MaximumComponentUtf16Length = 255;
    private const int MaximumTotalUtf16Length = 32767;
    private static readonly HashSet<string> ReservedNames = BuildReservedNames();
    private readonly ReadOnlyCollection<string> segments;

    private NormalizedRelativePath(string value, string[] segments)
    {
        Value = value;
        this.segments = Array.AsReadOnly((string[])segments.Clone());
    }

    public string Value { get; }
    public IReadOnlyList<string> Segments => segments;

    public static bool TryCreate(
        string candidate,
        out NormalizedRelativePath? path,
        out string? rejection)
    {
        path = null;
        rejection = null;
        if (candidate is null)
        {
            rejection = "The relative path is null.";
            return false;
        }

        if (candidate.Length == 0)
        {
            path = new NormalizedRelativePath(string.Empty, []);
            return true;
        }

        if (candidate.Length > MaximumTotalUtf16Length)
        {
            rejection = $"The relative path exceeds {MaximumTotalUtf16Length} UTF-16 code units.";
            return false;
        }

        if (candidate.Contains('\0'))
        {
            rejection = "The relative path contains a null character.";
            return false;
        }

        var normalized = candidate.Replace('/', '\\');
        if (Path.IsPathRooted(normalized) ||
            (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':') ||
            normalized.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            normalized.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
            normalized.StartsWith("\\\\", StringComparison.Ordinal))
        {
            rejection = "Absolute, drive-relative, device, and UNC paths are not allowed.";
            return false;
        }

        var candidateSegments = normalized.Split('\\');
        foreach (var segment in candidateSegments)
        {
            if (segment.Length == 0)
            {
                rejection = "Empty path segments are not allowed.";
                return false;
            }

            if (segment.Length > MaximumComponentUtf16Length)
            {
                rejection = $"A path segment exceeds {MaximumComponentUtf16Length} UTF-16 code units.";
                return false;
            }

            if (segment is "." or "..")
            {
                rejection = "Dot path segments are not allowed.";
                return false;
            }

            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                rejection = "A path segment may not end with a dot or space.";
                return false;
            }

            if (segment.Contains(':', StringComparison.Ordinal))
            {
                rejection = "Alternate data stream syntax is not allowed.";
                return false;
            }

            if (segment.Any(character =>
                    character < ' ' || character is '<' or '>' or '"' or '|' or '?' or '*'))
            {
                rejection = "The relative path contains an invalid Windows filename character.";
                return false;
            }

            var deviceStem = segment.Split('.', 2)[0];
            if (ReservedNames.Contains(deviceStem))
            {
                rejection = "Reserved Windows device names are not allowed.";
                return false;
            }
        }

        path = new NormalizedRelativePath(
            string.Join('\\', candidateSegments),
            candidateSegments);
        return true;
    }

    public override string ToString() => Value;

    private static HashSet<string> BuildReservedNames()
    {
        var names = new HashSet<string>(
            [
                "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
                "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
            ],
            StringComparer.OrdinalIgnoreCase);
        for (var suffix = 1; suffix <= 9; suffix++)
        {
            names.Add($"COM{suffix}");
            names.Add($"LPT{suffix}");
        }

        return names;
    }
}

public sealed record VerifiedDirectorySnapshot(
    string CanonicalPath,
    PhysicalDirectoryIdentity Identity,
    bool IsLocalVolume,
    bool IsNetworkRedirected,
    bool IsReparseFree);

public sealed record FileSystemEntrySnapshot(
    NormalizedRelativePath RelativePath,
    bool IsDirectory,
    long Length,
    FileAttributes Attributes);

public sealed record FileReadLimits(long MaximumBytes);

public sealed record EnumerationLimits(int MaximumEntries);

public sealed record FileObjectMetadata(
    DateTimeOffset LastWriteTimeUtc,
    FileAttributes Attributes,
    PhysicalFileIdentity? Identity);

public sealed class BoundedFileSnapshot
{
    private readonly byte[] bytes;

    internal BoundedFileSnapshot(
        bool exists,
        byte[] bytes,
        string sha256,
        FileObjectMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(sha256);
        ArgumentNullException.ThrowIfNull(metadata);
        Exists = exists;
        this.bytes = (byte[])bytes.Clone();
        Length = this.bytes.LongLength;
        Sha256 = sha256;
        Metadata = metadata;
    }

    public bool Exists { get; }
    public long Length { get; }
    public string Sha256 { get; }
    public FileObjectMetadata Metadata { get; }

    public byte[] CopyBytes() => (byte[])bytes.Clone();

    internal static BoundedFileSnapshot Missing() =>
        new(
            exists: false,
            [],
            string.Empty,
            new FileObjectMetadata(DateTimeOffset.UnixEpoch, 0, null));
}

public sealed record VolumeCapabilitySnapshot(
    string RootPath,
    string FileSystemName,
    bool IsLocalVolume,
    bool IsNetworkRedirected,
    bool SupportsPersistentAcls);
