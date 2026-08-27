using System.Buffers.Binary;
using System.Text;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Discovery;

public sealed class WindowsShortcutTargetResolver : IShortcutTargetResolver
{
    private const int ShellLinkHeaderSize = 0x4c;
    private const uint HasLinkTargetIdList = 0x00000001;
    private const uint HasLinkInfo = 0x00000002;
    private const uint HasName = 0x00000004;
    private const uint HasRelativePath = 0x00000008;
    private const uint HasWorkingDirectory = 0x00000010;
    private const uint HasArguments = 0x00000020;
    private const uint HasIconLocation = 0x00000040;
    private const uint IsUnicode = 0x00000080;
    private const int MaximumShortcutBytes = 1024 * 1024;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeDevice = 0x00000040;
    private const uint KnownFileAttributeMask = 0x005EFFF7;
    private static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");

    public ShortcutResolution Parse(BoundedFileSnapshot shortcutBytes)
    {
        ArgumentNullException.ThrowIfNull(shortcutBytes);
        if (!shortcutBytes.Exists)
        {
            return ShortcutResolution.Rejected(
                DiscoveryDiagnosticCode.ShortcutMalformed,
                "The shortcut snapshot does not exist.");
        }

        if (shortcutBytes.Length > MaximumShortcutBytes)
        {
            return ShortcutResolution.Rejected(
                DiscoveryDiagnosticCode.ShortcutTooLarge,
                "The shortcut exceeds the one-megabyte parsing limit.");
        }

        var bytes = shortcutBytes.CopyBytes();
        var data = bytes.AsSpan();
        if (data.Length < ShellLinkHeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[..4]) != ShellLinkHeaderSize ||
            new Guid(data.Slice(4, 16)) != ShellLinkClassId)
        {
            return ShortcutResolution.Rejected(
                DiscoveryDiagnosticCode.ShortcutMalformed,
                "The Shell Link header is missing or invalid.");
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20, 4));
        var targetKind = ClassifyTargetKind(
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(24, 4)));
        if (targetKind == ShortcutTargetKind.Unknown)
        {
            return ShortcutResolution.Rejected(
                DiscoveryDiagnosticCode.ShortcutTargetKindUnknown,
                "The Shell Link header contains no trustworthy file-or-directory target evidence.");
        }

        var offset = ShellLinkHeaderSize;
        if ((flags & HasLinkTargetIdList) != 0)
        {
            if (!TryReadUInt16(data, offset, out var idListSize) ||
                !TryAdvance(data, ref offset, checked(2 + idListSize)))
            {
                return Malformed("The Shell Link target-ID list is truncated.");
            }
        }

        string? linkInfoTarget = null;
        if ((flags & HasLinkInfo) != 0)
        {
            if (!TryParseLinkInfo(data, offset, out linkInfoTarget, out var linkInfoSize) ||
                !TryAdvance(data, ref offset, linkInfoSize))
            {
                return Malformed("The Shell Link LinkInfo block is malformed or truncated.");
            }
        }

        string? relativeTarget = null;
        foreach (var field in new[]
                 {
                     (Flag: HasName, IsTarget: false),
                     (Flag: HasRelativePath, IsTarget: true),
                     (Flag: HasWorkingDirectory, IsTarget: false),
                     (Flag: HasArguments, IsTarget: false),
                     (Flag: HasIconLocation, IsTarget: false),
                 })
        {
            if ((flags & field.Flag) == 0)
            {
                continue;
            }

            if (!TryReadStringData(data, ref offset, (flags & IsUnicode) != 0, out var value))
            {
                return Malformed("The Shell Link StringData section is malformed or truncated.");
            }

            if (field.IsTarget)
            {
                relativeTarget = value;
            }
        }

        var target = !string.IsNullOrWhiteSpace(linkInfoTarget)
            ? linkInfoTarget
            : relativeTarget;
        return string.IsNullOrWhiteSpace(target)
            ? Malformed("The Shell Link contains no bounded target path.")
            : ShortcutResolution.Resolved(target, targetKind);
    }

    private static bool TryParseLinkInfo(
        ReadOnlySpan<byte> data,
        int offset,
        out string? target,
        out int size)
    {
        target = null;
        size = 0;
        if (!TryReadUInt32(data, offset, out var rawSize) ||
            rawSize < 0x1c ||
            rawSize > int.MaxValue)
        {
            return false;
        }

        size = checked((int)rawSize);
        if (offset < 0 || size > data.Length - offset)
        {
            return false;
        }

        var linkInfo = data.Slice(offset, size);
        if (!TryReadUInt32(linkInfo, 4, out var rawHeaderSize) ||
            rawHeaderSize < 0x1c ||
            rawHeaderSize > rawSize ||
            !TryReadUInt32(linkInfo, 16, out var localBasePathOffset))
        {
            return false;
        }

        uint unicodeOffset = 0;
        if (rawHeaderSize >= 0x24 && !TryReadUInt32(linkInfo, 28, out unicodeOffset))
        {
            return false;
        }

        if (unicodeOffset > 0 && unicodeOffset < rawSize &&
            TryReadNullTerminatedUnicode(linkInfo, checked((int)unicodeOffset), out target))
        {
            return true;
        }

        return localBasePathOffset > 0 &&
               localBasePathOffset < rawSize &&
               TryReadNullTerminatedAnsi(linkInfo, checked((int)localBasePathOffset), out target);
    }

    private static bool TryReadStringData(
        ReadOnlySpan<byte> data,
        ref int offset,
        bool unicode,
        out string value)
    {
        value = string.Empty;
        if (!TryReadUInt16(data, offset, out var characterCount))
        {
            return false;
        }

        offset += 2;
        var byteCount = unicode
            ? checked(characterCount * 2)
            : characterCount;
        if (byteCount > data.Length - offset)
        {
            return false;
        }

        value = unicode
            ? Encoding.Unicode.GetString(data.Slice(offset, byteCount))
            : Encoding.Latin1.GetString(data.Slice(offset, byteCount));
        offset += byteCount;
        return true;
    }

    private static bool TryReadNullTerminatedAnsi(
        ReadOnlySpan<byte> data,
        int offset,
        out string? value)
    {
        value = null;
        if (offset < 0 || offset >= data.Length)
        {
            return false;
        }

        var remaining = data[offset..];
        var terminator = remaining.IndexOf((byte)0);
        if (terminator < 0)
        {
            return false;
        }

        value = Encoding.Latin1.GetString(remaining[..terminator]);
        return true;
    }

    private static bool TryReadNullTerminatedUnicode(
        ReadOnlySpan<byte> data,
        int offset,
        out string? value)
    {
        value = null;
        if (offset < 0 || offset >= data.Length || (offset & 1) != 0)
        {
            return false;
        }

        var end = offset;
        while (end + 1 < data.Length)
        {
            if (data[end] == 0 && data[end + 1] == 0)
            {
                value = Encoding.Unicode.GetString(data.Slice(offset, end - offset));
                return true;
            }

            end += 2;
        }

        return false;
    }

    private static bool TryReadUInt16(ReadOnlySpan<byte> data, int offset, out ushort value)
    {
        if (offset < 0 || sizeof(ushort) > data.Length - offset)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, sizeof(ushort)));
        return true;
    }

    private static bool TryReadUInt32(ReadOnlySpan<byte> data, int offset, out uint value)
    {
        if (offset < 0 || sizeof(uint) > data.Length - offset)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, sizeof(uint)));
        return true;
    }

    private static bool TryAdvance(ReadOnlySpan<byte> data, ref int offset, int count)
    {
        if (count < 0 || offset < 0 || count > data.Length - offset)
        {
            return false;
        }

        offset += count;
        return true;
    }

    private static ShortcutResolution Malformed(string message) =>
        ShortcutResolution.Rejected(DiscoveryDiagnosticCode.ShortcutMalformed, message);

    private static ShortcutTargetKind ClassifyTargetKind(uint attributes)
    {
        if (attributes == 0 ||
            (attributes & ~KnownFileAttributeMask) != 0 ||
            (attributes & FileAttributeDevice) != 0)
        {
            return ShortcutTargetKind.Unknown;
        }

        return (attributes & FileAttributeDirectory) != 0
            ? ShortcutTargetKind.Directory
            : ShortcutTargetKind.File;
    }
}
