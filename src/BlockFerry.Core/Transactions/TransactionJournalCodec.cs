using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BlockFerry.Core.Transactions;

public sealed class TransactionAuthenticationException : IOException
{
    public TransactionAuthenticationException(string message)
        : base(message)
    {
    }

    public TransactionAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal static class TransactionJournalCodec
{
    internal const int MaximumRecords = 100_000;
    internal const int MaximumJournalBytes = 64 * 1024 * 1024;
    private const int SchemaVersion = 1;
    private const int MacLength = 32;
    private const int DigestLength = 32;
    private static ReadOnlySpan<byte> Magic => "BFJNL001"u8;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] EncodeRecord(
        TransactionId transactionId,
        long sequence,
        TransactionRecordKind kind,
        string opaqueObjectId,
        ReadOnlySpan<byte> pathMac,
        ReadOnlySpan<byte> contentDigest,
        ReadOnlySpan<byte> previousMac,
        ReadOnlySpan<byte> key)
    {
        TransactionValueValidation.RequireId(transactionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        TransactionValueValidation.RequireOpaqueId(opaqueObjectId, nameof(opaqueObjectId));
        RequireLength(pathMac, MacLength, nameof(pathMac));
        RequireLength(contentDigest, DigestLength, nameof(contentDigest));
        RequireLength(previousMac, MacLength, nameof(previousMac));
        RequireLength(key, MacLength, nameof(key));
        var objectBytes = StrictUtf8.GetBytes(opaqueObjectId);
        try
        {
            var signedLength = checked(
                Magic.Length +
                sizeof(int) +
                16 +
                sizeof(long) +
                sizeof(int) +
                sizeof(int) +
                objectBytes.Length +
                MacLength +
                DigestLength +
                MacLength);
            var result = new byte[checked(signedLength + MacLength)];
            var writer = new SpanWriter(result);
            writer.Write(Magic);
            writer.WriteInt32(SchemaVersion);
            writer.Write(transactionId.Value.ToByteArray());
            writer.WriteInt64(sequence);
            writer.WriteInt32((int)kind);
            writer.WriteInt32(objectBytes.Length);
            writer.Write(objectBytes);
            writer.Write(pathMac);
            writer.Write(contentDigest);
            writer.Write(previousMac);
            var mac = HMACSHA256.HashData(key, result.AsSpan(0, signedLength));
            try
            {
                writer.Write(mac);
                return result;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(mac);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(objectBytes);
        }
    }

    internal static VerifiedJournal DecodeAndVerify(
        ReadOnlySpan<byte> payload,
        TransactionId expectedTransactionId,
        ReadOnlySpan<byte> key)
    {
        TransactionValueValidation.RequireId(expectedTransactionId);
        RequireLength(key, MacLength, nameof(key));
        if (payload.Length == 0 || payload.Length > MaximumJournalBytes)
        {
            throw new TransactionAuthenticationException("The journal length was invalid.");
        }

        var records = new List<TransactionJournalRecord>();
        var offset = 0;
        var expectedPreviousMac = new byte[MacLength];
        try
        {
            while (offset < payload.Length)
            {
                if (records.Count == MaximumRecords)
                {
                    throw new TransactionAuthenticationException("The journal record count exceeded its bound.");
                }

                var recordStart = offset;
                Require(payload, ref offset, Magic);
                var schema = ReadInt32(payload, ref offset);
                if (schema != SchemaVersion)
                {
                    throw new TransactionAuthenticationException("The journal schema is unsupported.");
                }

                var transactionId = new TransactionId(new Guid(Read(payload, ref offset, 16)));
                if (transactionId != expectedTransactionId)
                {
                    throw new TransactionAuthenticationException("The journal belongs to another transaction.");
                }

                var sequence = ReadInt64(payload, ref offset);
                var kindValue = ReadInt32(payload, ref offset);
                if (!Enum.IsDefined(typeof(TransactionRecordKind), kindValue))
                {
                    throw new TransactionAuthenticationException("The journal contained an unknown record kind.");
                }

                var objectLength = ReadInt32(payload, ref offset);
                if (objectLength is <= 0 or > 128)
                {
                    throw new TransactionAuthenticationException("The journal object identifier exceeded its bound.");
                }

                string opaqueObjectId;
                try
                {
                    opaqueObjectId = StrictUtf8.GetString(Read(payload, ref offset, objectLength));
                    TransactionValueValidation.RequireOpaqueId(opaqueObjectId, nameof(opaqueObjectId));
                }
                catch (Exception exception) when (exception is DecoderFallbackException or ArgumentException)
                {
                    throw new TransactionAuthenticationException("The journal object identifier was invalid.", exception);
                }

                var pathMac = Read(payload, ref offset, MacLength).ToArray();
                var contentDigest = Read(payload, ref offset, DigestLength).ToArray();
                var previousMac = Read(payload, ref offset, MacLength).ToArray();
                if (!CryptographicOperations.FixedTimeEquals(previousMac, expectedPreviousMac))
                {
                    throw new TransactionAuthenticationException("The journal previous-MAC chain was invalid.");
                }

                var signedLength = offset - recordStart;
                var recordMac = Read(payload, ref offset, MacLength).ToArray();
                var computed = HMACSHA256.HashData(key, payload.Slice(recordStart, signedLength));
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(recordMac, computed))
                    {
                        throw new TransactionAuthenticationException("The journal record MAC was invalid.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(computed);
                }

                records.Add(new TransactionJournalRecord(
                    sequence,
                    (TransactionRecordKind)kindValue,
                    opaqueObjectId,
                    pathMac,
                    contentDigest,
                    previousMac,
                    recordMac));
                CryptographicOperations.ZeroMemory(expectedPreviousMac);
                expectedPreviousMac = recordMac.ToArray();
            }

            var retained = TransactionStateMachine.Retain(records);
            TransactionStateMachine.Validate(retained);
            return new VerifiedJournal(expectedTransactionId, retained);
        }
        catch (TransactionAuthenticationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new TransactionAuthenticationException("The journal binary structure was invalid.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedPreviousMac);
        }
    }

    internal static byte[] ComputePathMac(
        TransactionId transactionId,
        string normalizedRelativePath,
        ReadOnlySpan<byte> key)
    {
        TransactionValueValidation.RequireId(transactionId);
        RequireLength(key, MacLength, nameof(key));
        ArgumentNullException.ThrowIfNull(normalizedRelativePath);
        var pathBytes = StrictUtf8.GetBytes(normalizedRelativePath.Normalize(NormalizationForm.FormC));
        var material = new byte[checked(16 + pathBytes.Length)];
        try
        {
            transactionId.Value.TryWriteBytes(material);
            pathBytes.CopyTo(material, 16);
            return HMACSHA256.HashData(key, material);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pathBytes);
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private static ReadOnlySpan<byte> Read(ReadOnlySpan<byte> source, ref int offset, int count)
    {
        if (count < 0 || count > source.Length - offset)
        {
            throw new TransactionAuthenticationException("The journal was truncated.");
        }

        var result = source.Slice(offset, count);
        offset += count;
        return result;
    }

    private static void Require(ReadOnlySpan<byte> source, ref int offset, ReadOnlySpan<byte> expected)
    {
        if (!Read(source, ref offset, expected.Length).SequenceEqual(expected))
        {
            throw new TransactionAuthenticationException("The journal record header was invalid.");
        }
    }

    private static int ReadInt32(ReadOnlySpan<byte> source, ref int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(Read(source, ref offset, sizeof(int)));

    private static long ReadInt64(ReadOnlySpan<byte> source, ref int offset) =>
        BinaryPrimitives.ReadInt64LittleEndian(Read(source, ref offset, sizeof(long)));

    private static void RequireLength(ReadOnlySpan<byte> value, int expected, string parameterName)
    {
        if (value.Length != expected)
        {
            throw new ArgumentException($"Exactly {expected} bytes are required.", parameterName);
        }
    }

    private ref struct SpanWriter(Span<byte> destination)
    {
        private readonly Span<byte> destination = destination;
        private int offset;

        internal void Write(ReadOnlySpan<byte> value)
        {
            value.CopyTo(destination[offset..]);
            offset += value.Length;
        }

        internal void WriteInt32(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination[offset..], value);
            offset += sizeof(int);
        }

        internal void WriteInt64(long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(destination[offset..], value);
            offset += sizeof(long);
        }
    }
}
