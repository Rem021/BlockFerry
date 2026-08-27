using System.Buffers.Binary;

namespace BlockFerry.Core.System;

internal static class ZipArchivePreflight
{
    private const uint EndOfCentralDirectorySignature = 0x06054B50;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064B50;
    private const uint Zip64EndOfCentralDirectoryLocatorSignature = 0x07064B50;
    private const uint CentralDirectoryFileHeaderSignature = 0x02014B50;
    private const int EndOfCentralDirectoryLength = 22;
    private const int MaximumCommentLength = ushort.MaxValue;
    private const int Zip64LocatorLength = 20;
    private const int Zip64EndOfCentralDirectoryMinimumLength = 56;
    private const int CentralDirectoryFileHeaderLength = 46;
    private const ushort EncryptedEntryFlag = 0x0001;

    public static void Validate(
        Stream stream,
        long archiveLength,
        ZipReadLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new CapabilityBoundaryException("ZIP preflight requires the verified seekable archive handle.");
        }

        if (archiveLength > limits.MaximumArchiveBytes)
        {
            throw new CapabilityLimitExceededException(
                $"ZIP archive exceeded the {limits.MaximumArchiveBytes} byte archive limit.");
        }

        if (archiveLength < EndOfCentralDirectoryLength || stream.Length != archiveLength)
        {
            throw new CapabilityBoundaryException("The verified archive length cannot contain a complete ZIP EOCD record.");
        }

        var eocd = FindEndOfCentralDirectory(stream, archiveLength, cancellationToken);
        var layout = ReadLayout(stream, eocd, archiveLength, cancellationToken);
        EnforceLayoutBounds(layout, archiveLength, limits);
        ParseCentralDirectory(stream, layout, cancellationToken);
    }

    private static EndOfCentralDirectory FindEndOfCentralDirectory(
        Stream stream,
        long archiveLength,
        CancellationToken cancellationToken)
    {
        var tailLength = checked((int)Math.Min(
            archiveLength,
            EndOfCentralDirectoryLength + MaximumCommentLength));
        var tailOffset = archiveLength - tailLength;
        var tail = new byte[tailLength];
        ReadExactlyAt(stream, tailOffset, tail, cancellationToken);

        for (var index = tail.Length - EndOfCentralDirectoryLength; index >= 0; index--)
        {
            if ((index & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var candidate = tail.AsSpan(index);
            if (BinaryPrimitives.ReadUInt32LittleEndian(candidate) != EndOfCentralDirectorySignature)
            {
                continue;
            }

            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(candidate[20..]);
            if (index + EndOfCentralDirectoryLength + commentLength != tail.Length)
            {
                continue;
            }

            return new EndOfCentralDirectory(
                tailOffset + index,
                BinaryPrimitives.ReadUInt16LittleEndian(candidate[4..]),
                BinaryPrimitives.ReadUInt16LittleEndian(candidate[6..]),
                BinaryPrimitives.ReadUInt16LittleEndian(candidate[8..]),
                BinaryPrimitives.ReadUInt16LittleEndian(candidate[10..]),
                BinaryPrimitives.ReadUInt32LittleEndian(candidate[12..]),
                BinaryPrimitives.ReadUInt32LittleEndian(candidate[16..]));
        }

        throw new CapabilityBoundaryException("The bounded ZIP tail contains no complete EOCD record.");
    }

    private static ZipLayout ReadLayout(
        Stream stream,
        EndOfCentralDirectory eocd,
        long archiveLength,
        CancellationToken cancellationToken)
    {
        if (eocd.DiskNumber != 0 ||
            eocd.CentralDirectoryDisk != 0 ||
            eocd.EntriesOnDisk != eocd.TotalEntries)
        {
            throw new CapabilityBoundaryException("Multi-disk ZIP archives are not supported.");
        }

        var needsZip64 = eocd.TotalEntries == ushort.MaxValue ||
            eocd.CentralDirectoryBytes == uint.MaxValue ||
            eocd.CentralDirectoryOffset == uint.MaxValue;
        if (!needsZip64)
        {
            return new ZipLayout(
                eocd.TotalEntries,
                eocd.CentralDirectoryOffset,
                eocd.CentralDirectoryBytes,
                eocd.Offset);
        }

        var locatorOffset = eocd.Offset - Zip64LocatorLength;
        if (locatorOffset < 0)
        {
            throw new CapabilityBoundaryException("The ZIP64 EOCD locator is outside the verified archive.");
        }

        var locator = new byte[Zip64LocatorLength];
        ReadExactlyAt(stream, locatorOffset, locator, cancellationToken);
        if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64EndOfCentralDirectoryLocatorSignature ||
            BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(4)) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(16)) != 1)
        {
            throw new CapabilityBoundaryException("The ZIP64 EOCD locator is malformed or multi-disk.");
        }

        var zip64OffsetUnsigned = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
        if (zip64OffsetUnsigned > long.MaxValue)
        {
            throw new CapabilityBoundaryException("The ZIP64 EOCD offset exceeds supported integer bounds.");
        }

        var zip64Offset = checked((long)zip64OffsetUnsigned);
        if (zip64Offset > archiveLength - Zip64EndOfCentralDirectoryMinimumLength ||
            zip64Offset > locatorOffset - Zip64EndOfCentralDirectoryMinimumLength)
        {
            throw new CapabilityBoundaryException("The ZIP64 EOCD record lies outside its verified bounds.");
        }

        var zip64 = new byte[Zip64EndOfCentralDirectoryMinimumLength];
        ReadExactlyAt(stream, zip64Offset, zip64, cancellationToken);
        if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != Zip64EndOfCentralDirectorySignature)
        {
            throw new CapabilityBoundaryException("The ZIP64 EOCD signature is invalid.");
        }

        var recordPayloadBytes = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(4));
        if (recordPayloadBytes < 44 || recordPayloadBytes > long.MaxValue - 12)
        {
            throw new CapabilityBoundaryException("The ZIP64 EOCD length is outside supported bounds.");
        }

        var recordBytes = checked((long)recordPayloadBytes + 12);
        if (recordBytes != locatorOffset - zip64Offset ||
            BinaryPrimitives.ReadUInt32LittleEndian(zip64.AsSpan(16)) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(zip64.AsSpan(20)) != 0)
        {
            throw new CapabilityBoundaryException("The ZIP64 EOCD record is malformed or multi-disk.");
        }

        var entriesOnDisk = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(24));
        var totalEntries = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(32));
        if (entriesOnDisk != totalEntries)
        {
            throw new CapabilityBoundaryException("The ZIP64 entry counts describe a multi-disk archive.");
        }

        return new ZipLayout(
            totalEntries,
            BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(48)),
            BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(40)),
            zip64Offset);
    }

    private static void EnforceLayoutBounds(
        ZipLayout layout,
        long archiveLength,
        ZipReadLimits limits)
    {
        if (layout.EntryCount > checked((ulong)limits.MaximumEntries))
        {
            throw new CapabilityLimitExceededException(
                $"ZIP archive exceeded the {limits.MaximumEntries} entry limit.");
        }

        if (layout.CentralDirectoryBytes > checked((ulong)limits.MaximumCentralDirectoryBytes))
        {
            throw new CapabilityLimitExceededException(
                $"ZIP central directory exceeded the {limits.MaximumCentralDirectoryBytes} byte limit.");
        }

        if (layout.CentralDirectoryOffset > long.MaxValue ||
            layout.CentralDirectoryBytes > long.MaxValue)
        {
            throw new CapabilityBoundaryException("ZIP central-directory values exceed supported integer bounds.");
        }

        var offset = checked((long)layout.CentralDirectoryOffset);
        var bytes = checked((long)layout.CentralDirectoryBytes);
        if (offset < 0 ||
            offset > archiveLength ||
            bytes > archiveLength - offset ||
            offset > layout.StructureOffset ||
            bytes > layout.StructureOffset - offset)
        {
            throw new CapabilityBoundaryException("The ZIP central directory lies outside the verified archive bounds.");
        }
    }

    private static void ParseCentralDirectory(
        Stream stream,
        ZipLayout layout,
        CancellationToken cancellationToken)
    {
        var entryCount = checked((int)layout.EntryCount);
        var centralDirectoryBytes = checked((long)layout.CentralDirectoryBytes);
        var position = checked((long)layout.CentralDirectoryOffset);
        long consumed = 0;
        var header = new byte[CentralDirectoryFileHeaderLength];
        for (var entryIndex = 0; entryIndex < entryCount; entryIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (centralDirectoryBytes - consumed < CentralDirectoryFileHeaderLength)
            {
                throw new CapabilityBoundaryException("A ZIP central-directory record is truncated.");
            }

            ReadExactlyAt(stream, position + consumed, header, cancellationToken);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != CentralDirectoryFileHeaderSignature)
            {
                throw new CapabilityBoundaryException("A ZIP central-directory record signature is invalid.");
            }

            if ((BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8)) & EncryptedEntryFlag) != 0)
            {
                throw new CapabilityBoundaryException("Encrypted ZIP entries are not supported.");
            }

            var fileNameBytes = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
            var extraBytes = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30));
            var commentBytes = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
            var variableBytes = checked((long)fileNameBytes + extraBytes + commentBytes);
            var recordBytes = CentralDirectoryFileHeaderLength + variableBytes;
            if (recordBytes > centralDirectoryBytes - consumed)
            {
                throw new CapabilityBoundaryException("A ZIP central-directory variable field exceeds its bounded record.");
            }

            consumed += recordBytes;
        }

        if (consumed != centralDirectoryBytes)
        {
            throw new CapabilityBoundaryException("ZIP central-directory bytes do not match the declared entry records.");
        }
    }

    private static void ReadExactlyAt(
        Stream stream,
        long offset,
        byte[] destination,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || offset > stream.Length - destination.Length)
        {
            throw new CapabilityBoundaryException("A ZIP structure read lies outside the verified archive handle.");
        }

        stream.Position = offset;
        var read = 0;
        while (read < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = stream.Read(destination, read, destination.Length - read);
            if (count == 0)
            {
                throw new CapabilityBoundaryException("A ZIP structure ended before its declared length.");
            }

            read += count;
        }
    }

    private readonly record struct EndOfCentralDirectory(
        long Offset,
        ushort DiskNumber,
        ushort CentralDirectoryDisk,
        ushort EntriesOnDisk,
        ushort TotalEntries,
        uint CentralDirectoryBytes,
        uint CentralDirectoryOffset);

    private readonly record struct ZipLayout(
        ulong EntryCount,
        ulong CentralDirectoryOffset,
        ulong CentralDirectoryBytes,
        long StructureOffset);
}
