using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace BlockFerry.Core.Discovery;

internal sealed class PayloadLimitException : Exception
{
    public PayloadLimitException(string message)
        : base(message)
    {
    }
}

internal sealed class FixedCapacityBufferWriter : IBufferWriter<byte>, IDisposable
{
    private readonly byte[] buffer;

    public FixedCapacityBufferWriter(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        buffer = new byte[capacity];
    }

    public int Capacity => buffer.Length;

    public int WrittenCount { get; private set; }

    public ReadOnlySpan<byte> WrittenSpan => buffer.AsSpan(0, WrittenCount);

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > buffer.Length - WrittenCount)
        {
            throw new PayloadLimitException("The fixed-capacity JSON buffer was exceeded.");
        }

        WrittenCount += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureAvailable(sizeHint);
        return buffer.AsMemory(WrittenCount);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureAvailable(sizeHint);
        return buffer.AsSpan(WrittenCount);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(buffer);
    }

    private void EnsureAvailable(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        var required = Math.Max(sizeHint, 1);
        if (required > buffer.Length - WrittenCount)
        {
            throw new PayloadLimitException("The fixed-capacity JSON buffer was exceeded.");
        }
    }
}

internal static class DiscoveryRootPayloadCodec
{
    public static byte[] Serialize(
        RememberedDiscoveryRoots value,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        using var buffer = new FixedCapacityBufferWriter(checked(maximumBytes + 1));
        using (var writer = new Utf8JsonWriter(buffer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", value.SchemaVersion);
            writer.WritePropertyName("approvedRoots");
            writer.WriteStartArray();
            foreach (var root in value.ApprovedRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteStringValue(root);
            }

            writer.WriteEndArray();
            writer.WriteNull("lastSourceInstanceId");
            writer.WriteNull("lastTargetInstanceId");
            writer.WriteEndObject();
            writer.Flush();
        }

        if (buffer.WrittenCount > maximumBytes)
        {
            throw new PayloadLimitException("The JSON payload exceeded its fixed output bound.");
        }

        var result = new byte[buffer.WrittenCount];
        buffer.WrittenSpan.CopyTo(result);
        return result;
    }
}
