using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text;

namespace BlockFerry.Core.System;

internal static class WindowsDirectoryRecordParser
{
    private const int FixedHeaderBytes = 68;
    private const int FileNameLengthOffset = 60;
    private const int FileNameOffset = 68;
    private const int NativeRecordAlignment = 8;

    public static ReadOnlyCollection<string> Parse(
        ReadOnlySpan<byte> buffer,
        int maximumEntries)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumEntries);

        var names = new List<string>();
        var recordOffset = 0;
        while (true)
        {
            var remaining = buffer.Length - recordOffset;
            if (remaining < FixedHeaderBytes)
            {
                throw new CapabilityBoundaryException(
                    "A directory-information record is shorter than its fixed header.");
            }

            var record = buffer[recordOffset..];
            var nextEntryOffset = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var fileNameBytes = BinaryPrimitives.ReadUInt32LittleEndian(record[FileNameLengthOffset..]);
            if (fileNameBytes == 0 ||
                (fileNameBytes & 1) != 0 ||
                fileNameBytes > int.MaxValue ||
                fileNameBytes > checked((uint)(remaining - FileNameOffset)))
            {
                throw new CapabilityBoundaryException(
                    "A directory-information filename exceeds its checked record span.");
            }

            var fileNameLength = checked((int)fileNameBytes);
            var recordContentEnd = FileNameOffset + fileNameLength;
            var name = Encoding.Unicode.GetString(record.Slice(FileNameOffset, fileNameLength));
            if (string.IsNullOrEmpty(name))
            {
                throw new CapabilityBoundaryException(
                    "A directory-information record contains an empty filename.");
            }

            if (name is not "." and not "..")
            {
                if (names.Count >= maximumEntries)
                {
                    throw new CapabilityLimitExceededException(
                        $"Directory enumeration exceeded the {maximumEntries} entry limit.");
                }

                names.Add(name);
            }

            if (nextEntryOffset == 0)
            {
                break;
            }

            if (nextEntryOffset > int.MaxValue ||
                (nextEntryOffset & (NativeRecordAlignment - 1)) != 0)
            {
                throw new CapabilityBoundaryException(
                    "A directory-information NextEntryOffset is unaligned or exceeds integer bounds.");
            }

            var alignedContentEnd = checked(
                (recordContentEnd + NativeRecordAlignment - 1) & ~(NativeRecordAlignment - 1));
            var nextOffset = checked((int)nextEntryOffset);
            if (nextOffset < FixedHeaderBytes ||
                nextOffset < alignedContentEnd ||
                nextOffset > remaining ||
                remaining - nextOffset < FixedHeaderBytes)
            {
                throw new CapabilityBoundaryException(
                    "A directory-information NextEntryOffset overlaps or truncates a record.");
            }

            recordOffset += nextOffset;
        }

        return Array.AsReadOnly(names.ToArray());
    }
}
