using System.Globalization;
using System.Text;
using System.Text.Json;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Content;

internal sealed class DarkModeEverywhereDocument
{
    internal const int MaximumFileBytes = 512 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly StrictJsonLimits Limits = new(
        MaximumDepth: 32,
        MaximumTokens: 32_768,
        MaximumStringUtf8Bytes: 8 * 1024,
        MaximumArrayElements: 64,
        MaximumObjectProperties: 256);

    private DarkModeEverywhereDocument(
        ImmutableByteBuffer original,
        IReadOnlyList<ImmutableByteBuffer?> shaders,
        int selectedShaderIndex,
        int selectedTokenStart,
        int selectedTokenLength)
    {
        Original = original;
        Shaders = shaders;
        SelectedShaderIndex = selectedShaderIndex;
        SelectedTokenStart = selectedTokenStart;
        SelectedTokenLength = selectedTokenLength;
    }

    internal ImmutableByteBuffer Original { get; }

    internal IReadOnlyList<ImmutableByteBuffer?> Shaders { get; }

    internal int SelectedShaderIndex { get; }

    private int SelectedTokenStart { get; }

    private int SelectedTokenLength { get; }

    internal static bool TryParse(
        ImmutableByteBuffer bytes,
        out DarkModeEverywhereDocument? document,
        out ContentDiagnosticCode? rejection)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        document = null;
        rejection = null;
        if (bytes.Length > MaximumFileBytes)
        {
            rejection = ContentDiagnosticCode.LimitExceeded;
            return false;
        }

        if (!StrictJsonEquivalence.TryCompare(
                bytes,
                bytes,
                Limits,
                out _,
                out rejection))
        {
            return false;
        }

        var raw = bytes.CopyBytes();
        try
        {
            _ = StrictUtf8.GetCharCount(raw);
            using var parsed = JsonDocument.Parse(
                raw,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = Limits.MaximumDepth,
                });
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                !string.Equals(version.GetRawText(), "2", StringComparison.Ordinal) ||
                !root.TryGetProperty("shaders", out var shadersElement) ||
                shadersElement.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("selectedShaderIndex", out var selectedElement) ||
                selectedElement.ValueKind != JsonValueKind.Number ||
                !selectedElement.TryGetInt32(out var selectedIndex))
            {
                rejection = ContentDiagnosticCode.UnsupportedSchema;
                return false;
            }

            var shaders = new List<ImmutableByteBuffer?>();
            foreach (var shader in shadersElement.EnumerateArray())
            {
                if (shaders.Count == Limits.MaximumArrayElements ||
                    shader.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object))
                {
                    rejection = ContentDiagnosticCode.UnsupportedSchema;
                    return false;
                }

                shaders.Add(shader.ValueKind == JsonValueKind.Null
                    ? null
                    : ImmutableByteBuffer.CopyFrom(Encoding.UTF8.GetBytes(shader.GetRawText())));
            }

            if (shaders.Count == 0 || selectedIndex < 0 || selectedIndex >= shaders.Count ||
                !TryLocateSelectedIndexToken(raw, out var tokenStart, out var tokenLength))
            {
                rejection = ContentDiagnosticCode.UnsupportedSchema;
                return false;
            }

            document = new DarkModeEverywhereDocument(
                bytes,
                shaders.AsReadOnly(),
                selectedIndex,
                tokenStart,
                tokenLength);
            return true;
        }
        catch (DecoderFallbackException)
        {
            rejection = ContentDiagnosticCode.MalformedUtf8;
        }
        catch (JsonException)
        {
            rejection = ContentDiagnosticCode.MalformedJson;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or OverflowException or ArgumentException)
        {
            rejection = ContentDiagnosticCode.UnsupportedSchema;
        }

        return false;
    }

    internal bool TryMapSelectedShader(
        DarkModeEverywhereDocument target,
        out int targetIndex,
        out ContentDiagnosticCode? rejection)
    {
        ArgumentNullException.ThrowIfNull(target);
        targetIndex = -1;
        rejection = null;
        var sourceShader = Shaders[SelectedShaderIndex];
        var matches = new List<int>();
        for (var index = 0; index < target.Shaders.Count; index++)
        {
            var candidate = target.Shaders[index];
            if (sourceShader is null || candidate is null)
            {
                if (sourceShader is null && candidate is null)
                {
                    matches.Add(index);
                }

                continue;
            }

            if (!StrictJsonEquivalence.TryCompare(
                    sourceShader,
                    candidate,
                    Limits,
                    out var equivalent,
                    out rejection))
            {
                return false;
            }

            if (equivalent)
            {
                matches.Add(index);
            }
        }

        if (matches.Count != 1)
        {
            rejection = ContentDiagnosticCode.UnsupportedSchema;
            return false;
        }

        targetIndex = matches[0];
        return true;
    }

    internal ImmutableByteBuffer ReplaceSelectedShaderIndex(int selectedShaderIndex)
    {
        if (selectedShaderIndex < 0 || selectedShaderIndex >= Shaders.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(selectedShaderIndex));
        }

        var replacement = Encoding.UTF8.GetBytes(
            selectedShaderIndex.ToString(CultureInfo.InvariantCulture));
        var raw = Original.CopyBytes();
        var outputLength = checked(raw.Length - SelectedTokenLength + replacement.Length);
        if (outputLength > MaximumFileBytes)
        {
            throw new CapabilityLimitExceededException("The dark-mode document exceeds its output bound.");
        }

        var output = new byte[outputLength];
        raw.AsSpan(0, SelectedTokenStart).CopyTo(output);
        replacement.CopyTo(output.AsSpan(SelectedTokenStart));
        raw.AsSpan(SelectedTokenStart + SelectedTokenLength)
            .CopyTo(output.AsSpan(SelectedTokenStart + replacement.Length));
        return ImmutableByteBuffer.CopyFrom(output);
    }

    private static bool TryLocateSelectedIndexToken(
        byte[] raw,
        out int tokenStart,
        out int tokenLength)
    {
        tokenStart = 0;
        tokenLength = 0;
        var reader = new Utf8JsonReader(
            raw,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = Limits.MaximumDepth,
            });
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName ||
                reader.CurrentDepth != 1 ||
                !reader.ValueTextEquals("selectedShaderIndex"u8) ||
                !reader.Read() ||
                reader.TokenType != JsonTokenType.Number)
            {
                continue;
            }

            tokenStart = checked((int)reader.TokenStartIndex);
            tokenLength = checked((int)(reader.BytesConsumed - reader.TokenStartIndex));
            return tokenLength > 0;
        }

        return false;
    }
}
