using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Content;

internal interface IJeiServerScopeHintProvider
{
    string? TryResolveTargetScope(
        IReadOnlyInstanceAccess source,
        string sourceScope,
        CancellationToken cancellationToken);
}

internal sealed class NoJeiServerScopeHintProvider : IJeiServerScopeHintProvider
{
    internal static NoJeiServerScopeHintProvider Instance { get; } = new();

    private NoJeiServerScopeHintProvider()
    {
    }

    public string? TryResolveTargetScope(
        IReadOnlyInstanceAccess source,
        string sourceScope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceScope);
        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }
}

internal interface IMinecraftServerStatusClient
{
    string? TryGetDescription(
        IPAddress address,
        int port,
        CancellationToken cancellationToken);
}

internal sealed class JeiLanServerScopeHintProvider(
    IMinecraftServerStatusClient statusClient) : IJeiServerScopeHintProvider
{
    private const int MaximumLogBytes = 16 * 1024 * 1024;
    private const int MaximumCachedScopes = 32;
    private const string LanSuffix = " (LAN connection)";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly ContentRelativePath LatestLogPath = CreateLatestLogPath();
    private static readonly char[] InvalidFileNameCharacters = Path.GetInvalidFileNameChars();
    private readonly object cacheGate = new();
    private readonly Dictionary<CacheKey, string> cache = [];

    public string? TryResolveTargetScope(
        IReadOnlyInstanceAccess source,
        string sourceScope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceScope);
        cancellationToken.ThrowIfCancellationRequested();
        if (!sourceScope.EndsWith(LanSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        ContentFileSnapshot logSnapshot;
        try
        {
            logSnapshot = source.Read(
                LatestLogPath,
                new ContentReadLimits(MaximumLogBytes),
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is CapabilityLimitExceededException or CapabilityBoundaryException)
        {
            return null;
        }

        if (!logSnapshot.Exists || logSnapshot.Length == 0)
        {
            return null;
        }

        string logText;
        try
        {
            logText = StrictUtf8.GetString(logSnapshot.Bytes.CopyBytes());
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        if (!TryParseLastEndpoint(logText, out var address, out var port))
        {
            return null;
        }

        var cacheKey = new CacheKey(source.Identity, sourceScope, address.ToString(), port);
        lock (cacheGate)
        {
            if (cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var description = statusClient.TryGetDescription(address, port, cancellationToken);
        var resolved = TryCreateLanScope(description);
        if (resolved is null)
        {
            return null;
        }

        lock (cacheGate)
        {
            if (cache.Count >= MaximumCachedScopes)
            {
                cache.Clear();
            }

            cache[cacheKey] = resolved;
        }

        return resolved;
    }

    private static bool TryParseLastEndpoint(
        string logText,
        out IPAddress address,
        out int port)
    {
        const string marker = "Connecting to ";
        address = IPAddress.None;
        port = 0;
        var markerIndex = logText.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return false;
        }

        var valueStart = markerIndex + marker.Length;
        var valueEnd = logText.IndexOfAny(['\r', '\n'], valueStart);
        if (valueEnd < 0)
        {
            valueEnd = logText.Length;
        }

        var endpoint = logText[valueStart..valueEnd].Trim();
        var separator = endpoint.LastIndexOf(", ", StringComparison.Ordinal);
        if (separator <= 0 ||
            !int.TryParse(
                endpoint[(separator + 2)..],
                global::System.Globalization.NumberStyles.None,
                global::System.Globalization.CultureInfo.InvariantCulture,
                out port) ||
            port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            return false;
        }

        var host = endpoint[..separator].Trim();
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
        {
            host = host[1..^1];
        }

        return IPAddress.TryParse(host, out address!);
    }

    private static string? TryCreateLanScope(string? description)
    {
        if (string.IsNullOrWhiteSpace(description) || description.Length > 160)
        {
            return null;
        }

        var fullName = description + LanSuffix;
        var sanitized = new StringBuilder(fullName.Length);
        foreach (var character in fullName)
        {
            if (character is '"' or '.' or '/' or '\\' ||
                character == '\u00a7' ||
                character < ' ' ||
                character == '\u007f')
            {
                sanitized.Append('_');
                continue;
            }

            if (InvalidFileNameCharacters.Contains(character))
            {
                return null;
            }

            sanitized.Append(character);
        }

        var value = sanitized.ToString().Trim();
        if (value.Length == 0 || value.Length > 220)
        {
            return null;
        }

        return IsWindowsDeviceName(value) ? $"_{value}_" : value;
    }

    private static bool IsWindowsDeviceName(string value)
    {
        var stem = value.Split('.', 2)[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               stem.Length == 4 &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               stem[3] is >= '1' and <= '9';
    }

    private static ContentRelativePath CreateLatestLogPath()
    {
        if (!ContentRelativePath.TryCreate(@"logs\latest.log", out var path, out _))
        {
            throw new InvalidOperationException("The JEI log path constant was invalid.");
        }

        return path!;
    }

    private readonly record struct CacheKey(
        ContentInstanceIdentity SourceIdentity,
        string SourceScope,
        string Address,
        int Port);
}

internal sealed class MinecraftServerStatusClient : IMinecraftServerStatusClient
{
    private const int ProtocolVersion = 767;
    private const int MaximumPacketBytes = 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public string? TryGetDescription(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        cancellationToken.ThrowIfCancellationRequested();
        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            return null;
        }

        try
        {
            return QueryAsync(address, port, cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (
            exception is IOException or SocketException or JsonException or InvalidDataException or
            ObjectDisposedException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task<string?> QueryAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var client = new TcpClient(address.AddressFamily)
        {
            NoDelay = true,
        };
        await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
        await using var stream = client.GetStream();

        var handshake = BuildHandshake(address.ToString(), port);
        await stream.WriteAsync(handshake, timeout.Token).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { 1, 0 }, timeout.Token).ConfigureAwait(false);

        var packetLength = await ReadVarIntAsync(stream, timeout.Token).ConfigureAwait(false);
        if (packetLength is <= 0 or > MaximumPacketBytes)
        {
            throw new InvalidDataException("The Minecraft status packet length was invalid.");
        }

        var packet = new byte[packetLength];
        await ReadExactlyAsync(stream, packet, timeout.Token).ConfigureAwait(false);
        var offset = 0;
        if (ReadVarInt(packet, ref offset) != 0)
        {
            throw new InvalidDataException("The Minecraft status packet identifier was invalid.");
        }

        var jsonLength = ReadVarInt(packet, ref offset);
        if (jsonLength < 0 || jsonLength > MaximumPacketBytes || offset + jsonLength != packet.Length)
        {
            throw new InvalidDataException("The Minecraft status JSON length was invalid.");
        }

        using var document = JsonDocument.Parse(
            packet.AsMemory(offset, jsonLength),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
        var root = document.RootElement;
        if (!root.TryGetProperty("version", out var version) ||
            !version.TryGetProperty("protocol", out var protocol) ||
            !protocol.TryGetInt32(out var protocolNumber) ||
            protocolNumber != ProtocolVersion ||
            !root.TryGetProperty("description", out var description) ||
            description.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return description.GetString();
    }

    private static byte[] BuildHandshake(string host, int port)
    {
        var hostBytes = Encoding.UTF8.GetBytes(host);
        using var payload = new MemoryStream();
        WriteVarInt(payload, 0);
        WriteVarInt(payload, ProtocolVersion);
        WriteVarInt(payload, hostBytes.Length);
        payload.Write(hostBytes);
        Span<byte> portBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBytes, checked((ushort)port));
        payload.Write(portBytes);
        WriteVarInt(payload, 1);

        using var packet = new MemoryStream();
        WriteVarInt(packet, checked((int)payload.Length));
        payload.Position = 0;
        payload.CopyTo(packet);
        return packet.ToArray();
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        var remaining = unchecked((uint)value);
        while (true)
        {
            if ((remaining & ~0x7Fu) == 0)
            {
                stream.WriteByte((byte)remaining);
                return;
            }

            stream.WriteByte((byte)((remaining & 0x7F) | 0x80));
            remaining >>= 7;
        }
    }

    private static async Task<int> ReadVarIntAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var result = 0;
        var oneByte = new byte[1];
        for (var index = 0; index < 5; index++)
        {
            if (await stream.ReadAsync(oneByte, cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new IOException("The Minecraft status response ended early.");
            }

            var value = oneByte[0];
            result |= (value & 0x7F) << (7 * index);
            if ((value & 0x80) == 0)
            {
                return result;
            }
        }

        throw new InvalidDataException("The Minecraft status VarInt was too long.");
    }

    private static int ReadVarInt(ReadOnlySpan<byte> bytes, ref int offset)
    {
        var result = 0;
        for (var index = 0; index < 5; index++)
        {
            if (offset >= bytes.Length)
            {
                throw new InvalidDataException("The Minecraft status response ended early.");
            }

            var value = bytes[offset++];
            result |= (value & 0x7F) << (7 * index);
            if ((value & 0x80) == 0)
            {
                return result;
            }
        }

        throw new InvalidDataException("The Minecraft status VarInt was too long.");
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(destination[offset..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The Minecraft status response ended early.");
            }

            offset += read;
        }
    }
}
