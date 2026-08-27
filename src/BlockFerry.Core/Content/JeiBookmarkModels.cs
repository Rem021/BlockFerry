using System.Text;
using System.Text.Json;

namespace BlockFerry.Core.Content;

internal enum JeiBookmarkScopeKind
{
    Local,
    Server,
}

internal readonly record struct JeiBookmarkScopeTopologyEntry(
    JeiBookmarkScopeKind Kind,
    string Scope);

internal sealed record DeferredJeiSeed(
    ContentRelativePath SourceRelativePath,
    ContentRelativePath ProvisionalTargetRelativePath,
    string SourceSha256);

internal enum DeferredJeiResolutionKind
{
    PendingTargetScope,
    Ready,
    ReadyReplaceEmpty,
    Complete,
    Conflict,
    Rejected,
}

internal sealed record DeferredJeiResolution(
    DeferredJeiResolutionKind Kind,
    ContentItemId? ItemId = null);

internal sealed record DeferredJeiPlanMetadata(
    IReadOnlyList<DeferredJeiSeed> Seeds);

internal sealed class JeiBookmarkPreparedItem
{
    internal JeiBookmarkPreparedItem(
        ContentItemId id,
        JeiBookmarkScopeKind scopeKind,
        string rawScope,
        string targetRawScope,
        ContentRelativePath sourceRelativePath,
        ContentRelativePath relativePath,
        bool targetScopeExists,
        bool targetScopeConfirmed,
        ContentFileSnapshot? sourceSnapshot,
        ContentFileSnapshot? targetSnapshot,
        PlannedContentDisposition disposition,
        ContentDiagnosticCode? disabledReason,
        bool isLegacy)
    {
        Id = id;
        ScopeKind = scopeKind;
        RawScope = rawScope;
        TargetRawScope = targetRawScope;
        SourceRelativePath = sourceRelativePath;
        RelativePath = relativePath;
        TargetScopeExists = targetScopeExists;
        TargetScopeConfirmed = targetScopeConfirmed;
        SourceSnapshot = sourceSnapshot;
        TargetSnapshot = targetSnapshot;
        Disposition = disposition;
        DisabledReason = disabledReason;
        IsLegacy = isLegacy;
    }

    internal ContentItemId Id { get; }

    internal JeiBookmarkScopeKind ScopeKind { get; }

    internal string RawScope { get; }

    internal string TargetRawScope { get; }

    internal ContentRelativePath SourceRelativePath { get; }

    internal ContentRelativePath RelativePath { get; }

    internal bool TargetScopeExists { get; }

    internal bool TargetScopeConfirmed { get; }

    internal ContentFileSnapshot? SourceSnapshot { get; }

    internal ContentFileSnapshot? TargetSnapshot { get; }

    internal PlannedContentDisposition Disposition { get; }

    internal ContentDiagnosticCode? DisabledReason { get; }

    internal bool IsLegacy { get; }

    internal bool IsSupported => DisabledReason is null && SourceSnapshot is not null && TargetSnapshot is not null;
}

internal sealed class JeiBookmarkPreparedSession
{
    private readonly IReadOnlyInstanceAccess sourceAccess;
    private readonly IReadOnlyInstanceAccess targetAccess;

    internal JeiBookmarkPreparedSession(
        ContentProbeContext context,
        IEnumerable<JeiBookmarkPreparedItem> items,
        IEnumerable<ContentDiagnostic> diagnostics,
        IEnumerable<JeiBookmarkScopeTopologyEntry> sourceScopes,
        IEnumerable<JeiBookmarkScopeTopologyEntry> targetScopes)
    {
        Generation = context.Generation;
        sourceAccess = context.Source;
        targetAccess = context.Target;
        SourceIdentity = context.Source.Identity;
        TargetIdentity = context.Target.Identity;
        Items = Array.AsReadOnly(items.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        SourceScopes = Array.AsReadOnly(sourceScopes.ToArray());
        TargetScopes = Array.AsReadOnly(targetScopes.ToArray());
    }

    internal long Generation { get; }

    internal ContentInstanceIdentity SourceIdentity { get; }

    internal ContentInstanceIdentity TargetIdentity { get; }

    internal IReadOnlyList<JeiBookmarkPreparedItem> Items { get; }

    internal IReadOnlyList<ContentDiagnostic> Diagnostics { get; }

    internal IReadOnlyList<JeiBookmarkScopeTopologyEntry> SourceScopes { get; }

    internal IReadOnlyList<JeiBookmarkScopeTopologyEntry> TargetScopes { get; }

    internal bool IsBoundTo(ContentProbeContext context) =>
        context.Generation == Generation &&
        ReferenceEquals(context.Source, sourceAccess) &&
        ReferenceEquals(context.Target, targetAccess) &&
        context.Source.Identity == SourceIdentity &&
        context.Target.Identity == TargetIdentity;

    internal bool HasSameScopeTopology(
        IReadOnlyList<JeiBookmarkScopeTopologyEntry> sourceScopes,
        IReadOnlyList<JeiBookmarkScopeTopologyEntry> targetScopes) =>
        SourceScopes.SequenceEqual(sourceScopes) &&
        TargetScopes.SequenceEqual(targetScopes);
}

internal static class JeiBookmarkDocument
{
    internal const int MaximumFileBytes = 16 * 1024 * 1024;
    internal static StrictJsonLimits Limits { get; } = new(
        MaximumDepth: 64,
        MaximumTokens: 1_000_000,
        MaximumStringUtf8Bytes: 32 * 1024,
        MaximumArrayElements: 250_000,
        MaximumObjectProperties: 250_000);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool TryValidate(
        ImmutableByteBuffer bytes,
        out ContentDiagnosticCode? rejection)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        rejection = null;
        if (bytes.Length > MaximumFileBytes)
        {
            rejection = ContentDiagnosticCode.LimitExceeded;
            return false;
        }

        var raw = bytes.CopyBytes();
        try
        {
            _ = StrictUtf8.GetCharCount(raw);
        }
        catch (DecoderFallbackException)
        {
            rejection = ContentDiagnosticCode.MalformedUtf8;
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(
                raw,
                new JsonReaderOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    // Keep parser headroom so our own exact depth check owns the
                    // structured LimitExceeded classification.
                    MaxDepth = 128,
                });
            var state = new ParseState();
            ParseDocument(ref reader, state);
            return true;
        }
        catch (DuplicatePropertyException)
        {
            rejection = ContentDiagnosticCode.DuplicateJsonProperty;
        }
        catch (JsonLimitException)
        {
            rejection = ContentDiagnosticCode.LimitExceeded;
        }
        catch (JeiSchemaException)
        {
            rejection = ContentDiagnosticCode.UnsupportedSchema;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or OverflowException)
        {
            rejection = ContentDiagnosticCode.MalformedJson;
        }

        return false;
    }

    internal static bool TryCompare(
        ImmutableByteBuffer left,
        ImmutableByteBuffer right,
        out bool equivalent,
        out ContentDiagnosticCode? rejection) =>
        StrictJsonEquivalence.TryCompare(left, right, Limits, out equivalent, out rejection);

    internal static bool IsEmpty(ImmutableByteBuffer bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (!TryValidate(bytes, out _))
        {
            return false;
        }

        var reader = new Utf8JsonReader(bytes.CopyBytes());
        return reader.Read() && reader.TokenType == JsonTokenType.StartArray &&
               reader.Read() && reader.TokenType == JsonTokenType.StartObject &&
               reader.TrySkip() &&
               reader.Read() && reader.TokenType == JsonTokenType.EndArray;
    }

    private static void ParseDocument(ref Utf8JsonReader reader, ParseState state)
    {
        if (!ReadNext(ref reader, state) || reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JeiSchemaException();
        }

        if (!ReadNext(ref reader, state) || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JeiSchemaException();
        }

        var elements = 1;
        ParseHeader(ref reader, state);
        while (ReadNext(ref reader, state))
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                if (ReadNext(ref reader, state))
                {
                    throw new JsonException("Trailing JSON is not allowed.");
                }

                return;
            }

            if (elements == Limits.MaximumArrayElements)
            {
                throw new JsonLimitException();
            }

            elements++;
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JeiSchemaException();
            }

            ParseObject(ref reader, state);
        }

        throw new JsonException("The root array is incomplete.");
    }

    private static void ParseHeader(ref Utf8JsonReader reader, ParseState state)
    {
        var properties = new HashSet<string>(StringComparer.Ordinal);
        var foundVersion = false;
        while (ReadNext(ref reader, state))
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (!foundVersion || properties.Count != 1)
                {
                    throw new JeiSchemaException();
                }

                return;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("A header property was expected.");
            }

            AddProperty(ref reader, properties, out var name);
            if (!ReadNext(ref reader, state))
            {
                throw new JsonException("A header value was expected.");
            }

            if (!string.Equals(name, "version", StringComparison.Ordinal) ||
                reader.TokenType != JsonTokenType.Number ||
                !reader.ValueSpan.SequenceEqual("2"u8))
            {
                throw new JeiSchemaException();
            }

            foundVersion = true;
        }

        throw new JsonException("The header is incomplete.");
    }

    private static void ParseObject(ref Utf8JsonReader reader, ParseState state)
    {
        var properties = new HashSet<string>(StringComparer.Ordinal);
        while (ReadNext(ref reader, state))
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("An object property was expected.");
            }

            AddProperty(ref reader, properties, out _);
            if (!ReadNext(ref reader, state))
            {
                throw new JsonException("An object value was expected.");
            }

            ParseValue(ref reader, state);
        }

        throw new JsonException("The object is incomplete.");
    }

    private static void ParseArray(ref Utf8JsonReader reader, ParseState state)
    {
        var elements = 0;
        while (ReadNext(ref reader, state))
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return;
            }

            if (elements == Limits.MaximumArrayElements)
            {
                throw new JsonLimitException();
            }

            elements++;
            ParseValue(ref reader, state);
        }

        throw new JsonException("The array is incomplete.");
    }

    private static void ParseValue(ref Utf8JsonReader reader, ParseState state)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                ParseObject(ref reader, state);
                return;
            case JsonTokenType.StartArray:
                ParseArray(ref reader, state);
                return;
            case JsonTokenType.String:
                _ = ReadBoundedString(ref reader);
                return;
            case JsonTokenType.Number:
                RequireBoundedRawToken(ref reader);
                return;
            case JsonTokenType.True:
            case JsonTokenType.False:
            case JsonTokenType.Null:
                return;
            default:
                throw new JsonException("A JSON value was expected.");
        }
    }

    private static void AddProperty(
        ref Utf8JsonReader reader,
        HashSet<string> properties,
        out string name)
    {
        if (properties.Count == Limits.MaximumObjectProperties)
        {
            throw new JsonLimitException();
        }

        name = ReadBoundedString(ref reader);
        if (!properties.Add(name))
        {
            throw new DuplicatePropertyException();
        }
    }

    private static string ReadBoundedString(ref Utf8JsonReader reader)
    {
        var rawLength = reader.ValueSpan.Length;
        if ((!reader.ValueIsEscaped && rawLength > Limits.MaximumStringUtf8Bytes) ||
            rawLength > checked(Limits.MaximumStringUtf8Bytes * 6))
        {
            throw new JsonLimitException();
        }

        var value = reader.GetString() ?? throw new JsonException("A string was expected.");
        if (StrictUtf8.GetByteCount(value) > Limits.MaximumStringUtf8Bytes)
        {
            throw new JsonLimitException();
        }

        return value;
    }

    private static void RequireBoundedRawToken(ref Utf8JsonReader reader)
    {
        if (reader.ValueSpan.Length > Limits.MaximumStringUtf8Bytes)
        {
            throw new JsonLimitException();
        }
    }

    private static bool ReadNext(ref Utf8JsonReader reader, ParseState state)
    {
        if (!reader.Read())
        {
            return false;
        }

        state.TokenCount++;
        if (state.TokenCount > Limits.MaximumTokens || reader.CurrentDepth > Limits.MaximumDepth)
        {
            throw new JsonLimitException();
        }

        return true;
    }

    private sealed class ParseState
    {
        internal long TokenCount { get; set; }
    }

    private sealed class DuplicatePropertyException : Exception;

    private sealed class JsonLimitException : Exception;

    private sealed class JeiSchemaException : Exception;
}
