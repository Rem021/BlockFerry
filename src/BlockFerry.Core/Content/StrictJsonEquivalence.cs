using System.Buffers;
using System.Text;
using System.Text.Json;

namespace BlockFerry.Core.Content;

public static class StrictJsonEquivalence
{
    private const int MaximumDepth = 64;
    private const long MaximumTokens = 1_000_000;
    private const int MaximumStringUtf8Bytes = 32 * 1024;
    private const int MaximumArrayElements = 250_000;
    private const int MaximumObjectProperties = 250_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool TryCompare(
        ImmutableByteBuffer left,
        ImmutableByteBuffer right,
        StrictJsonLimits limits,
        out bool equivalent,
        out ContentDiagnosticCode? rejection)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(limits);
        equivalent = false;
        rejection = null;
        if (!LimitsAreValid(limits))
        {
            rejection = ContentDiagnosticCode.LimitExceeded;
            return false;
        }

        if (!TryParse(left.CopyBytes(), limits, out var leftNode, out rejection) ||
            !TryParse(right.CopyBytes(), limits, out var rightNode, out rejection))
        {
            return false;
        }

        equivalent = NodesEqual(leftNode!, rightNode!);
        return true;
    }

    private static bool TryParse(
        byte[] utf8,
        StrictJsonLimits limits,
        out JsonNode? node,
        out ContentDiagnosticCode? rejection)
    {
        node = null;
        rejection = null;
        try
        {
            _ = StrictUtf8.GetCharCount(utf8);
        }
        catch (DecoderFallbackException)
        {
            rejection = ContentDiagnosticCode.MalformedUtf8;
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(
                utf8,
                new JsonReaderOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    // Leave one parser level of headroom so our own bounded-reader
                    // check classifies excessive nesting as a limit violation.
                    MaxDepth = MaximumDepth + 1,
                });
            var state = new ParseState(limits);
            if (!ReadNext(ref reader, state))
            {
                rejection = ContentDiagnosticCode.MalformedJson;
                return false;
            }

            node = ParseValue(ref reader, state);
            if (ReadNext(ref reader, state))
            {
                rejection = ContentDiagnosticCode.MalformedJson;
                node = null;
                return false;
            }

            return true;
        }
        catch (DuplicatePropertyException)
        {
            rejection = ContentDiagnosticCode.DuplicateJsonProperty;
            return false;
        }
        catch (JsonLimitException)
        {
            rejection = ContentDiagnosticCode.LimitExceeded;
            return false;
        }
        catch (JsonException)
        {
            rejection = ContentDiagnosticCode.MalformedJson;
            return false;
        }
        catch (InvalidOperationException)
        {
            rejection = ContentDiagnosticCode.MalformedJson;
            return false;
        }
    }

    private static JsonNode ParseValue(ref Utf8JsonReader reader, ParseState state) =>
        reader.TokenType switch
        {
            JsonTokenType.StartObject => ParseObject(ref reader, state),
            JsonTokenType.StartArray => ParseArray(ref reader, state),
            JsonTokenType.String => new JsonStringNode(ReadBoundedString(ref reader, state)),
            JsonTokenType.Number => new JsonNumberNode(ReadBoundedToken(ref reader, state)),
            JsonTokenType.True => JsonBooleanNode.True,
            JsonTokenType.False => JsonBooleanNode.False,
            JsonTokenType.Null => JsonNullNode.Instance,
            _ => throw new JsonException("A JSON value was expected."),
        };

    private static JsonObjectNode ParseObject(ref Utf8JsonReader reader, ParseState state)
    {
        var properties = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        while (ReadNext(ref reader, state))
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new JsonObjectNode(properties);
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("An object property was expected.");
            }

            if (properties.Count == state.Limits.MaximumObjectProperties)
            {
                throw new JsonLimitException();
            }

            var name = ReadBoundedString(ref reader, state);
            if (properties.ContainsKey(name))
            {
                throw new DuplicatePropertyException();
            }

            if (!ReadNext(ref reader, state))
            {
                throw new JsonException("An object property value was expected.");
            }

            properties.Add(name, ParseValue(ref reader, state));
        }

        throw new JsonException("The object is incomplete.");
    }

    private static JsonArrayNode ParseArray(ref Utf8JsonReader reader, ParseState state)
    {
        var items = new List<JsonNode>();
        while (ReadNext(ref reader, state))
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return new JsonArrayNode(items);
            }

            if (items.Count == state.Limits.MaximumArrayElements)
            {
                throw new JsonLimitException();
            }

            items.Add(ParseValue(ref reader, state));
        }

        throw new JsonException("The array is incomplete.");
    }

    private static string ReadBoundedString(ref Utf8JsonReader reader, ParseState state)
    {
        var rawLength = reader.HasValueSequence
            ? checked((int)reader.ValueSequence.Length)
            : reader.ValueSpan.Length;
        var maximumEscapedLength = checked(state.Limits.MaximumStringUtf8Bytes * 6);
        if ((!reader.ValueIsEscaped && rawLength > state.Limits.MaximumStringUtf8Bytes) ||
            rawLength > maximumEscapedLength)
        {
            throw new JsonLimitException();
        }

        var value = reader.GetString() ?? throw new JsonException("A string value was expected.");
        if (StrictUtf8.GetByteCount(value) > state.Limits.MaximumStringUtf8Bytes)
        {
            throw new JsonLimitException();
        }

        return value;
    }

    private static byte[] ReadBoundedToken(ref Utf8JsonReader reader, ParseState state)
    {
        var length = reader.HasValueSequence
            ? checked((int)reader.ValueSequence.Length)
            : reader.ValueSpan.Length;
        if (length > state.Limits.MaximumStringUtf8Bytes)
        {
            throw new JsonLimitException();
        }

        if (!reader.HasValueSequence)
        {
            return reader.ValueSpan.ToArray();
        }

        var token = new byte[length];
        reader.ValueSequence.CopyTo(token);
        return token;
    }

    private static bool ReadNext(ref Utf8JsonReader reader, ParseState state)
    {
        if (!reader.Read())
        {
            return false;
        }

        state.TokenCount++;
        if (state.TokenCount > state.Limits.MaximumTokens ||
            reader.CurrentDepth > state.Limits.MaximumDepth)
        {
            throw new JsonLimitException();
        }

        return true;
    }

    private static bool NodesEqual(JsonNode left, JsonNode right)
    {
        if (left.GetType() != right.GetType())
        {
            return false;
        }

        return (left, right) switch
        {
            (JsonObjectNode leftObject, JsonObjectNode rightObject) =>
                ObjectsEqual(leftObject, rightObject),
            (JsonArrayNode leftArray, JsonArrayNode rightArray) =>
                leftArray.Items.Count == rightArray.Items.Count &&
                leftArray.Items.Zip(rightArray.Items).All(pair => NodesEqual(pair.First, pair.Second)),
            (JsonStringNode leftString, JsonStringNode rightString) =>
                string.Equals(leftString.Value, rightString.Value, StringComparison.Ordinal),
            (JsonNumberNode leftNumber, JsonNumberNode rightNumber) =>
                leftNumber.Token.AsSpan().SequenceEqual(rightNumber.Token),
            (JsonBooleanNode leftBoolean, JsonBooleanNode rightBoolean) =>
                leftBoolean.Value == rightBoolean.Value,
            (JsonNullNode, JsonNullNode) => true,
            _ => false,
        };
    }

    private static bool ObjectsEqual(JsonObjectNode left, JsonObjectNode right)
    {
        if (left.Properties.Count != right.Properties.Count)
        {
            return false;
        }

        foreach (var (name, leftValue) in left.Properties)
        {
            if (!right.Properties.TryGetValue(name, out var rightValue) ||
                !NodesEqual(leftValue, rightValue))
            {
                return false;
            }
        }

        return true;
    }

    private static bool LimitsAreValid(StrictJsonLimits limits) =>
        limits.MaximumDepth is > 0 and <= MaximumDepth &&
        limits.MaximumTokens is > 0 and <= MaximumTokens &&
        limits.MaximumStringUtf8Bytes is > 0 and <= MaximumStringUtf8Bytes &&
        limits.MaximumArrayElements is > 0 and <= MaximumArrayElements &&
        limits.MaximumObjectProperties is > 0 and <= MaximumObjectProperties;

    private sealed class ParseState(StrictJsonLimits limits)
    {
        internal StrictJsonLimits Limits { get; } = limits;

        internal long TokenCount { get; set; }
    }

    private abstract class JsonNode
    {
    }

    private sealed class JsonObjectNode(Dictionary<string, JsonNode> properties) : JsonNode
    {
        internal Dictionary<string, JsonNode> Properties { get; } = properties;
    }

    private sealed class JsonArrayNode(List<JsonNode> items) : JsonNode
    {
        internal List<JsonNode> Items { get; } = items;
    }

    private sealed class JsonStringNode(string value) : JsonNode
    {
        internal string Value { get; } = value;
    }

    private sealed class JsonNumberNode(byte[] token) : JsonNode
    {
        internal byte[] Token { get; } = token;
    }

    private sealed class JsonBooleanNode(bool value) : JsonNode
    {
        internal static JsonBooleanNode True { get; } = new(true);
        internal static JsonBooleanNode False { get; } = new(false);

        internal bool Value { get; } = value;
    }

    private sealed class JsonNullNode : JsonNode
    {
        internal static JsonNullNode Instance { get; } = new();
    }

    private sealed class DuplicatePropertyException : Exception
    {
    }

    private sealed class JsonLimitException : Exception
    {
    }
}
