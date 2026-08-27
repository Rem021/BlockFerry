using System.Collections.Frozen;
using System.Security.Cryptography;

namespace BlockFerry.Core.Content;

public enum ConflictResolution
{
    Unresolved,
    KeepTarget,
    UseSource,
    Skip,
}

public enum PlannedContentDisposition
{
    Add,
    Update,
    Same,
    Unselected,
    Protected,
    Unsupported,
    Conflict,
    Skipped,
}

public enum ContentDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public enum ContentDiagnosticCode
{
    MissingSourceData,
    MissingTargetData,
    UnsupportedMinecraftVersion,
    UnsupportedModVersion,
    UnsupportedSchema,
    UnsupportedEmiState,
    MalformedUtf8,
    MalformedJson,
    DuplicateJsonProperty,
    SemanticAliasCollision,
    LimitExceeded,
    StaleContext,
    CapabilityRejected,
    InvalidRelativePath,
    PathConflict,
}

public static class ContentContractLimits
{
    public const int MaximumAdapters = 32;
    public const int MaximumCatalogItems = 250_000;
    public const int MaximumFileChanges = 10_000;
    public const int MaximumDiagnostics = 4_096;
    public const int MaximumTechnicalIdUtf16Length = 256;
    public const int MaximumVisibleTextUtf16Length = 512;
}

public readonly struct ContentItemId : IEquatable<ContentItemId>
{
    private readonly string? adapterId;
    private readonly string? technicalKey;

    private ContentItemId(string adapterId, string technicalKey)
    {
        this.adapterId = adapterId;
        this.technicalKey = technicalKey;
    }

    public string AdapterId => adapterId ?? string.Empty;

    public string TechnicalKey => technicalKey ?? string.Empty;

    internal bool IsValid =>
        ContentValueValidation.IsTechnicalId(adapterId) &&
        ContentValueValidation.IsTechnicalId(technicalKey);

    public static bool TryCreate(
        string adapterId,
        string technicalKey,
        out ContentItemId id)
    {
        if (!ContentValueValidation.IsTechnicalId(adapterId) ||
            !ContentValueValidation.IsTechnicalId(technicalKey))
        {
            id = default;
            return false;
        }

        id = new ContentItemId(adapterId, technicalKey);
        return true;
    }

    public bool Equals(ContentItemId other) =>
        string.Equals(AdapterId, other.AdapterId, StringComparison.Ordinal) &&
        string.Equals(TechnicalKey, other.TechnicalKey, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ContentItemId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.Ordinal.GetHashCode(AdapterId),
        StringComparer.Ordinal.GetHashCode(TechnicalKey));

    public static bool operator ==(ContentItemId left, ContentItemId right) => left.Equals(right);

    public static bool operator !=(ContentItemId left, ContentItemId right) => !left.Equals(right);
}

public sealed class ContentDiagnostic
{
    private ContentDiagnostic(
        ContentDiagnosticCode code,
        ContentDiagnosticSeverity severity,
        string adapterId,
        ContentItemId? itemId,
        int? safeCount)
    {
        Code = code;
        Severity = severity;
        AdapterId = adapterId;
        ItemId = itemId;
        SafeCount = safeCount;
    }

    public ContentDiagnosticCode Code { get; }

    public ContentDiagnosticSeverity Severity { get; }

    public string AdapterId { get; }

    public ContentItemId? ItemId { get; }

    public int? SafeCount { get; }

    public static ContentDiagnostic Create(
        ContentDiagnosticCode code,
        ContentDiagnosticSeverity severity,
        string adapterId,
        ContentItemId? itemId = null,
        int? safeCount = null)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        if (!Enum.IsDefined(severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity));
        }

        ContentValueValidation.RequireTechnicalId(adapterId, nameof(adapterId));
        if (itemId is { } value &&
            (!value.IsValid || !string.Equals(value.AdapterId, adapterId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("The diagnostic item must belong to the adapter.", nameof(itemId));
        }

        if (safeCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(safeCount));
        }

        return new ContentDiagnostic(code, severity, adapterId, itemId, safeCount);
    }
}

public sealed class ImmutableByteBuffer
{
    private readonly byte[] bytes;

    private ImmutableByteBuffer(byte[] bytes)
    {
        this.bytes = bytes;
        Sha256 = Convert.ToHexString(SHA256.HashData(bytes));
    }

    public int Length => bytes.Length;

    public string Sha256 { get; }

    public byte[] CopyBytes() => bytes.ToArray();

    public static ImmutableByteBuffer CopyFrom(ReadOnlySpan<byte> source) =>
        new(source.ToArray());
}

public sealed class ContentRelativePath : IEquatable<ContentRelativePath>
{
    private const int MaximumComponentUtf16Length = 255;
    private const int MaximumTotalUtf16Length = 32_767;
    private static readonly HashSet<string> ReservedNames = BuildReservedNames();

    private ContentRelativePath(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static bool TryCreate(
        string candidate,
        out ContentRelativePath? path,
        out ContentDiagnosticCode? rejection)
    {
        path = null;
        rejection = ContentDiagnosticCode.InvalidRelativePath;
        if (candidate is null || candidate.Length > MaximumTotalUtf16Length)
        {
            return false;
        }

        if (candidate.Length == 0)
        {
            path = new ContentRelativePath(string.Empty);
            rejection = null;
            return true;
        }

        var normalized = candidate.Replace('/', '\\');
        if (normalized.StartsWith('\\') ||
            (normalized.Length >= 2 && IsAsciiLetter(normalized[0]) && normalized[1] == ':'))
        {
            return false;
        }

        var segments = normalized.Split('\\');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 ||
                segment.Length > MaximumComponentUtf16Length ||
                segment is "." or ".." ||
                segment.EndsWith(' ') ||
                segment.EndsWith('.') ||
                segment.Contains(':', StringComparison.Ordinal) ||
                segment.Any(character =>
                    character < ' ' || character is '<' or '>' or '"' or '|' or '?' or '*'))
            {
                return false;
            }

            var separator = segment.IndexOf('.', StringComparison.Ordinal);
            var deviceStem = separator < 0 ? segment : segment[..separator];
            if (ReservedNames.Contains(deviceStem))
            {
                return false;
            }
        }

        path = new ContentRelativePath(string.Join('\\', segments));
        rejection = null;
        return true;
    }

    public bool Equals(ContentRelativePath? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ContentRelativePath other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

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

public readonly record struct ContentFileIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh);

public sealed class ContentFileSnapshot
{
    private ContentFileSnapshot(
        ContentRelativePath relativePath,
        bool exists,
        ImmutableByteBuffer bytes,
        DateTimeOffset lastWriteTimeUtc,
        uint windowsFileAttributes,
        ContentFileIdentity? identity)
    {
        RelativePath = relativePath;
        Exists = exists;
        Bytes = bytes;
        Length = bytes.Length;
        Sha256 = bytes.Sha256;
        LastWriteTimeUtc = lastWriteTimeUtc;
        WindowsFileAttributes = windowsFileAttributes;
        Identity = identity;
    }

    public ContentRelativePath RelativePath { get; }

    public bool Exists { get; }

    public long Length { get; }

    public string Sha256 { get; }

    public DateTimeOffset LastWriteTimeUtc { get; }

    public uint WindowsFileAttributes { get; }

    public ContentFileIdentity? Identity { get; }

    public ImmutableByteBuffer Bytes { get; }

    internal static ContentFileSnapshot Create(
        ContentRelativePath relativePath,
        bool exists,
        ReadOnlySpan<byte> bytes,
        DateTimeOffset lastWriteTimeUtc,
        uint windowsFileAttributes,
        ContentFileIdentity? identity)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (!exists &&
            (bytes.Length != 0 ||
             identity is not null ||
             lastWriteTimeUtc != DateTimeOffset.UnixEpoch ||
             windowsFileAttributes != 0))
        {
            throw new ArgumentException("A missing snapshot must have empty bytes and no metadata.", nameof(exists));
        }

        return new ContentFileSnapshot(
            relativePath,
            exists,
            ImmutableByteBuffer.CopyFrom(bytes),
            lastWriteTimeUtc,
            windowsFileAttributes,
            identity);
    }
}

public sealed class ContentPlanItem
{
    private ContentPlanItem(
        ContentItemId id,
        PlannedContentDisposition disposition,
        ConflictResolution resolution,
        string summary)
    {
        Id = id;
        Disposition = disposition;
        Resolution = resolution;
        Summary = summary;
    }

    public ContentItemId Id { get; }

    public PlannedContentDisposition Disposition { get; }

    public ConflictResolution Resolution { get; }

    public string Summary { get; }

    public static ContentPlanItem Create(
        ContentItemId id,
        PlannedContentDisposition disposition,
        ConflictResolution resolution,
        string summary)
    {
        if (!id.IsValid)
        {
            throw new ArgumentException("A valid item ID is required.", nameof(id));
        }

        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        if (!Enum.IsDefined(resolution) ||
            (disposition == PlannedContentDisposition.Conflict && resolution == ConflictResolution.Unresolved) ||
            (disposition != PlannedContentDisposition.Conflict && resolution != ConflictResolution.Skip))
        {
            throw new ArgumentException("The resolution does not match the plan disposition.", nameof(resolution));
        }

        ContentValueValidation.RequireVisibleText(summary, nameof(summary), allowEmpty: false);
        return new ContentPlanItem(id, disposition, resolution, summary);
    }
}

public sealed class PlannedFileChange
{
    private PlannedFileChange(
        string adapterId,
        ContentRelativePath relativePath,
        ContentFileSnapshot sourceSnapshot,
        ContentFileSnapshot targetSnapshot,
        IReadOnlyList<ContentPlanItem> items)
    {
        AdapterId = adapterId;
        RelativePath = relativePath;
        SourceSnapshot = sourceSnapshot;
        TargetSnapshot = targetSnapshot;
        Items = items;
    }

    public string AdapterId { get; }

    public ContentRelativePath RelativePath { get; }

    public ContentRelativePath SourceRelativePath => SourceSnapshot.RelativePath;

    public ContentFileSnapshot SourceSnapshot { get; }

    public ContentFileSnapshot TargetSnapshot { get; }

    public IReadOnlyList<ContentPlanItem> Items { get; }

    public static PlannedFileChange Create(
        string adapterId,
        ContentRelativePath relativePath,
        ContentFileSnapshot sourceSnapshot,
        ContentFileSnapshot targetSnapshot,
        IEnumerable<ContentPlanItem> items) =>
        CreateCore(
            adapterId,
            relativePath,
            sourceSnapshot,
            targetSnapshot,
            items,
            allowMappedSource: false);

    internal static PlannedFileChange CreateMapped(
        string adapterId,
        ContentRelativePath relativePath,
        ContentFileSnapshot sourceSnapshot,
        ContentFileSnapshot targetSnapshot,
        IEnumerable<ContentPlanItem> items) =>
        CreateCore(
            adapterId,
            relativePath,
            sourceSnapshot,
            targetSnapshot,
            items,
            allowMappedSource: true);

    private static PlannedFileChange CreateCore(
        string adapterId,
        ContentRelativePath relativePath,
        ContentFileSnapshot sourceSnapshot,
        ContentFileSnapshot targetSnapshot,
        IEnumerable<ContentPlanItem> items,
        bool allowMappedSource)
    {
        ContentValueValidation.RequireTechnicalId(adapterId, nameof(adapterId));
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(sourceSnapshot);
        ArgumentNullException.ThrowIfNull(targetSnapshot);
        if (relativePath.Value.Length == 0 ||
            sourceSnapshot.RelativePath.Value.Length == 0 ||
            (!allowMappedSource && !relativePath.Equals(sourceSnapshot.RelativePath)) ||
            !relativePath.Equals(targetSnapshot.RelativePath))
        {
            throw new ArgumentException("Every snapshot must be bound to the exact final path.", nameof(relativePath));
        }

        var itemCopy = ContentEnumerable.CopyBounded(
            items,
            ContentContractLimits.MaximumCatalogItems,
            nameof(items));
        if (itemCopy.Count == 0 ||
            itemCopy.Any(item =>
                item is null ||
                !string.Equals(item.Id.AdapterId, adapterId, StringComparison.Ordinal)) ||
            itemCopy.Select(item => item.Id).Distinct().Count() != itemCopy.Count)
        {
            throw new ArgumentException("File-change items must be non-empty, unique, and adapter-bound.", nameof(items));
        }

        return new PlannedFileChange(
            adapterId,
            relativePath,
            sourceSnapshot,
            targetSnapshot,
            Array.AsReadOnly(itemCopy.ToArray()));
    }
}

public sealed class StagedFileMutation
{
    private StagedFileMutation(PlannedFileChange change, ImmutableByteBuffer afterBytes)
    {
        Change = change;
        AfterBytes = afterBytes;
    }

    public PlannedFileChange Change { get; }

    public ImmutableByteBuffer AfterBytes { get; }

    public static StagedFileMutation Create(
        PlannedFileChange change,
        ReadOnlySpan<byte> afterBytes)
    {
        ArgumentNullException.ThrowIfNull(change);
        return new StagedFileMutation(change, ImmutableByteBuffer.CopyFrom(afterBytes));
    }
}

public sealed class ContentCatalogItem
{
    private ContentCatalogItem(
        ContentItemId id,
        string displayName,
        string description,
        PlannedContentDisposition disposition,
        bool isSelectable,
        bool isSelectedByDefault,
        ConflictResolution defaultResolution,
        ContentDiagnosticCode? disabledReason)
    {
        Id = id;
        DisplayName = displayName;
        Description = description;
        Disposition = disposition;
        IsSelectable = isSelectable;
        IsSelectedByDefault = isSelectedByDefault;
        DefaultResolution = defaultResolution;
        DisabledReason = disabledReason;
    }

    public ContentItemId Id { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public PlannedContentDisposition Disposition { get; }

    public bool IsSelectable { get; }

    public bool IsSelectedByDefault { get; }

    public ConflictResolution DefaultResolution { get; }

    public ContentDiagnosticCode? DisabledReason { get; }

    public static ContentCatalogItem Create(
        ContentItemId id,
        string displayName,
        string description,
        PlannedContentDisposition disposition,
        bool isSelectable,
        bool isSelectedByDefault,
        ConflictResolution defaultResolution,
        ContentDiagnosticCode? disabledReason)
    {
        if (!id.IsValid)
        {
            throw new ArgumentException("A valid item ID is required.", nameof(id));
        }

        ContentValueValidation.RequireVisibleText(displayName, nameof(displayName), allowEmpty: false);
        ContentValueValidation.RequireVisibleText(description, nameof(description), allowEmpty: true);
        if (!Enum.IsDefined(disposition) ||
            disposition is PlannedContentDisposition.Unselected or PlannedContentDisposition.Skipped)
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        if (!Enum.IsDefined(defaultResolution))
        {
            throw new ArgumentOutOfRangeException(nameof(defaultResolution));
        }

        if (disabledReason is { } reason && !Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(disabledReason));
        }

        if (isSelectedByDefault && !isSelectable)
        {
            throw new ArgumentException("A disabled item cannot be selected by default.", nameof(isSelectedByDefault));
        }

        if (disposition != PlannedContentDisposition.Conflict && defaultResolution != ConflictResolution.Skip)
        {
            throw new ArgumentException("Non-conflict items use the Skip resolution sentinel.", nameof(defaultResolution));
        }

        return new ContentCatalogItem(
            id,
            displayName,
            description,
            disposition,
            isSelectable,
            isSelectedByDefault,
            defaultResolution,
            disabledReason);
    }
}

public sealed class ContentCatalog
{
    private ContentCatalog(
        string adapterId,
        IReadOnlyList<ContentCatalogItem> items,
        IReadOnlyList<ContentDiagnostic> diagnostics)
    {
        AdapterId = adapterId;
        Items = items;
        Diagnostics = diagnostics;
    }

    public string AdapterId { get; }

    public IReadOnlyList<ContentCatalogItem> Items { get; }

    public IReadOnlyList<ContentDiagnostic> Diagnostics { get; }

    public static ContentCatalog Create(
        string adapterId,
        IEnumerable<ContentCatalogItem> items,
        IEnumerable<ContentDiagnostic> diagnostics)
    {
        ContentValueValidation.RequireTechnicalId(adapterId, nameof(adapterId));
        var itemCopy = ContentEnumerable.CopyBounded(
            items,
            ContentContractLimits.MaximumCatalogItems,
            nameof(items));
        var diagnosticCopy = ContentEnumerable.CopyBounded(
            diagnostics,
            ContentContractLimits.MaximumDiagnostics,
            nameof(diagnostics));
        if (itemCopy.Any(item =>
                item is null ||
                !string.Equals(item.Id.AdapterId, adapterId, StringComparison.Ordinal)) ||
            itemCopy.Select(item => item.Id).Distinct().Count() != itemCopy.Count)
        {
            throw new ArgumentException("Catalog items must be non-null, unique, and adapter-bound.", nameof(items));
        }

        if (diagnosticCopy.Any(diagnostic =>
                diagnostic is null ||
                !string.Equals(diagnostic.AdapterId, adapterId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Catalog diagnostics must be non-null and adapter-bound.", nameof(diagnostics));
        }

        return new ContentCatalog(
            adapterId,
            Array.AsReadOnly(itemCopy.ToArray()),
            Array.AsReadOnly(diagnosticCopy.ToArray()));
    }
}

public sealed class ContentSelection
{
    private ContentSelection(
        IReadOnlySet<ContentItemId> selectedItems,
        IReadOnlyDictionary<ContentItemId, ConflictResolution> conflictResolutions)
    {
        SelectedItems = selectedItems;
        ConflictResolutions = conflictResolutions;
    }

    public IReadOnlySet<ContentItemId> SelectedItems { get; }

    public IReadOnlyDictionary<ContentItemId, ConflictResolution> ConflictResolutions { get; }

    public static ContentSelection Create(
        IEnumerable<ContentItemId> selectedItems,
        IEnumerable<KeyValuePair<ContentItemId, ConflictResolution>> conflictResolutions)
    {
        var selectedCopy = ContentEnumerable.CopyBounded(
            selectedItems,
            ContentContractLimits.MaximumCatalogItems,
            nameof(selectedItems));
        var resolutionCopy = ContentEnumerable.CopyBounded(
            conflictResolutions,
            ContentContractLimits.MaximumCatalogItems,
            nameof(conflictResolutions));
        if (selectedCopy.Any(id => !id.IsValid) || selectedCopy.Distinct().Count() != selectedCopy.Count)
        {
            throw new ArgumentException("Selected IDs must be valid and unique.", nameof(selectedItems));
        }

        if (resolutionCopy.Any(pair => !pair.Key.IsValid || !Enum.IsDefined(pair.Value)) ||
            resolutionCopy.Select(pair => pair.Key).Distinct().Count() != resolutionCopy.Count)
        {
            throw new ArgumentException("Conflict resolutions must have valid unique IDs.", nameof(conflictResolutions));
        }

        return new ContentSelection(
            new ReadOnlySet<ContentItemId>(selectedCopy),
            resolutionCopy.ToFrozenDictionary(pair => pair.Key, pair => pair.Value));
    }
}

public sealed class ContentAdapterPlan
{
    private ContentAdapterPlan(
        string adapterId,
        IReadOnlyList<ContentPlanItem> items,
        IReadOnlyList<PlannedFileChange> fileChanges,
        IReadOnlyList<ContentDiagnostic> diagnostics)
    {
        AdapterId = adapterId;
        Items = items;
        FileChanges = fileChanges;
        Diagnostics = diagnostics;
    }

    public string AdapterId { get; }

    public IReadOnlyList<ContentPlanItem> Items { get; }

    public IReadOnlyList<PlannedFileChange> FileChanges { get; }

    public IReadOnlyList<ContentDiagnostic> Diagnostics { get; }

    public static ContentAdapterPlan Create(
        string adapterId,
        IEnumerable<ContentPlanItem> items,
        IEnumerable<PlannedFileChange> fileChanges,
        IEnumerable<ContentDiagnostic> diagnostics)
    {
        ContentValueValidation.RequireTechnicalId(adapterId, nameof(adapterId));
        var itemCopy = ContentEnumerable.CopyBounded(
            items,
            ContentContractLimits.MaximumCatalogItems,
            nameof(items));
        var changeCopy = ContentEnumerable.CopyBounded(
            fileChanges,
            ContentContractLimits.MaximumFileChanges,
            nameof(fileChanges));
        var diagnosticCopy = ContentEnumerable.CopyBounded(
            diagnostics,
            ContentContractLimits.MaximumDiagnostics,
            nameof(diagnostics));
        if (itemCopy.Any(item =>
                item is null ||
                !string.Equals(item.Id.AdapterId, adapterId, StringComparison.Ordinal)) ||
            itemCopy.Select(item => item.Id).Distinct().Count() != itemCopy.Count)
        {
            throw new ArgumentException("Plan items must be non-null, unique, and adapter-bound.", nameof(items));
        }

        var retainedItems = new HashSet<ContentPlanItem>(
            itemCopy,
            ReferenceEqualityComparer.Instance);
        if (changeCopy.Any(change =>
                change is null ||
                !string.Equals(change.AdapterId, adapterId, StringComparison.Ordinal) ||
                change.Items.Any(item => !retainedItems.Contains(item))))
        {
            throw new ArgumentException("File changes must be adapter-bound and reference retained plan items.", nameof(fileChanges));
        }

        if (diagnosticCopy.Any(diagnostic =>
                diagnostic is null ||
                !string.Equals(diagnostic.AdapterId, adapterId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Diagnostics must be non-null and adapter-bound.", nameof(diagnostics));
        }

        return new ContentAdapterPlan(
            adapterId,
            Array.AsReadOnly(itemCopy.ToArray()),
            Array.AsReadOnly(changeCopy.ToArray()),
            Array.AsReadOnly(diagnosticCopy.ToArray()));
    }
}

public sealed class ContentProbeResult
{
    private ContentProbeResult(
        bool isSupported,
        ContentDiagnosticCode? disabledReason,
        IReadOnlyList<ContentDiagnostic> diagnostics)
    {
        IsSupported = isSupported;
        DisabledReason = disabledReason;
        Diagnostics = diagnostics;
    }

    public bool IsSupported { get; }

    public ContentDiagnosticCode? DisabledReason { get; }

    public IReadOnlyList<ContentDiagnostic> Diagnostics { get; }

    public static ContentProbeResult Create(
        bool isSupported,
        ContentDiagnosticCode? disabledReason,
        IEnumerable<ContentDiagnostic> diagnostics)
    {
        if (isSupported == (disabledReason is not null) ||
            disabledReason is { } reason && !Enum.IsDefined(reason))
        {
            throw new ArgumentException("Supported results have no disabled reason; unsupported results require one.", nameof(disabledReason));
        }

        var diagnosticCopy = ContentEnumerable.CopyBounded(
            diagnostics,
            ContentContractLimits.MaximumDiagnostics,
            nameof(diagnostics));
        if (diagnosticCopy.Any(diagnostic => diagnostic is null))
        {
            throw new ArgumentException("Diagnostics cannot contain null.", nameof(diagnostics));
        }

        return new ContentProbeResult(
            isSupported,
            disabledReason,
            Array.AsReadOnly(diagnosticCopy.ToArray()));
    }
}

public sealed class ContentStageResult
{
    private ContentStageResult(string adapterId, IReadOnlyList<StagedFileMutation> mutations)
    {
        AdapterId = adapterId;
        Mutations = mutations;
    }

    public string AdapterId { get; }

    public IReadOnlyList<StagedFileMutation> Mutations { get; }

    public static ContentStageResult Create(
        string adapterId,
        IEnumerable<StagedFileMutation> mutations)
    {
        ContentValueValidation.RequireTechnicalId(adapterId, nameof(adapterId));
        var copy = ContentEnumerable.CopyBounded(
            mutations,
            ContentContractLimits.MaximumFileChanges,
            nameof(mutations));
        if (copy.Any(mutation =>
                mutation is null ||
                !string.Equals(mutation.Change.AdapterId, adapterId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Staged mutations must be non-null and adapter-bound.", nameof(mutations));
        }

        return new ContentStageResult(adapterId, Array.AsReadOnly(copy.ToArray()));
    }
}

public sealed class ContentVerificationResult
{
    private ContentVerificationResult(
        bool isValid,
        IReadOnlyList<ContentDiagnostic> diagnostics)
    {
        IsValid = isValid;
        Diagnostics = diagnostics;
    }

    public bool IsValid { get; }

    public IReadOnlyList<ContentDiagnostic> Diagnostics { get; }

    public static ContentVerificationResult Create(
        bool isValid,
        IEnumerable<ContentDiagnostic> diagnostics)
    {
        var copy = ContentEnumerable.CopyBounded(
            diagnostics,
            ContentContractLimits.MaximumDiagnostics,
            nameof(diagnostics));
        if (copy.Any(diagnostic => diagnostic is null))
        {
            throw new ArgumentException("Diagnostics cannot contain null.", nameof(diagnostics));
        }

        return new ContentVerificationResult(isValid, Array.AsReadOnly(copy.ToArray()));
    }
}

public sealed class MigrationContentPlan
{
    internal MigrationContentPlan(
        long discoveryGeneration,
        string sourceInstanceId,
        string targetInstanceId,
        IReadOnlyList<ContentAdapterPlan> adapterPlans,
        IReadOnlyList<ContentPlanItem> items,
        IReadOnlyList<PlannedFileChange> fileChanges,
        IReadOnlyList<ContentDiagnostic> diagnostics)
    {
        DiscoveryGeneration = discoveryGeneration;
        SourceInstanceId = sourceInstanceId;
        TargetInstanceId = targetInstanceId;
        AdapterPlans = adapterPlans;
        Items = items;
        FileChanges = fileChanges;
        Diagnostics = diagnostics;
    }

    public long DiscoveryGeneration { get; }

    public string SourceInstanceId { get; }

    public string TargetInstanceId { get; }

    public IReadOnlyList<ContentAdapterPlan> AdapterPlans { get; }

    public IReadOnlyList<ContentPlanItem> Items { get; }

    public IReadOnlyList<PlannedFileChange> FileChanges { get; }

    public IReadOnlyList<ContentDiagnostic> Diagnostics { get; }

    public static MigrationContentPlan Create(
        long discoveryGeneration,
        string sourceInstanceId,
        string targetInstanceId,
        IEnumerable<ContentAdapterPlan> adapterPlans) =>
        ContentPlanCoordinator.CreateValidated(
            discoveryGeneration,
            sourceInstanceId,
            targetInstanceId,
            adapterPlans);
}

public sealed record StrictJsonLimits(
    int MaximumDepth,
    long MaximumTokens,
    int MaximumStringUtf8Bytes,
    int MaximumArrayElements,
    int MaximumObjectProperties);

internal sealed class AdapterCompatibilityEvidence
{
    private AdapterCompatibilityEvidence(
        string? sourceMinecraftVersion,
        string? targetMinecraftVersion,
        IReadOnlyDictionary<string, string> sourceModVersions,
        IReadOnlyDictionary<string, string> targetModVersions,
        IReadOnlySet<string> detectedUnsupportedModIds)
    {
        SourceMinecraftVersion = sourceMinecraftVersion;
        TargetMinecraftVersion = targetMinecraftVersion;
        SourceModVersions = sourceModVersions;
        TargetModVersions = targetModVersions;
        DetectedUnsupportedModIds = detectedUnsupportedModIds;
    }

    internal string? SourceMinecraftVersion { get; }

    internal string? TargetMinecraftVersion { get; }

    internal IReadOnlyDictionary<string, string> SourceModVersions { get; }

    internal IReadOnlyDictionary<string, string> TargetModVersions { get; }

    internal IReadOnlySet<string> DetectedUnsupportedModIds { get; }

    internal static AdapterCompatibilityEvidence Create(
        string? sourceMinecraftVersion,
        string? targetMinecraftVersion,
        IEnumerable<KeyValuePair<string, string>> sourceModVersions,
        IEnumerable<KeyValuePair<string, string>> targetModVersions,
        IEnumerable<string> detectedUnsupportedModIds)
    {
        ContentValueValidation.RequireOptionalTechnicalValue(
            sourceMinecraftVersion,
            nameof(sourceMinecraftVersion));
        ContentValueValidation.RequireOptionalTechnicalValue(
            targetMinecraftVersion,
            nameof(targetMinecraftVersion));
        var sourceCopy = CopyModVersions(sourceModVersions, nameof(sourceModVersions));
        var targetCopy = CopyModVersions(targetModVersions, nameof(targetModVersions));
        var unsupportedCopy = ContentEnumerable.CopyBounded(
            detectedUnsupportedModIds,
            ContentContractLimits.MaximumCatalogItems,
            nameof(detectedUnsupportedModIds));
        if (unsupportedCopy.Any(value => !ContentValueValidation.IsTechnicalId(value)) ||
            unsupportedCopy.Distinct(StringComparer.Ordinal).Count() != unsupportedCopy.Count)
        {
            throw new ArgumentException("Unsupported mod IDs must be valid and unique.", nameof(detectedUnsupportedModIds));
        }

        return new AdapterCompatibilityEvidence(
            sourceMinecraftVersion,
            targetMinecraftVersion,
            sourceCopy.ToFrozenDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            targetCopy.ToFrozenDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            new ReadOnlySet<string>(unsupportedCopy, StringComparer.Ordinal));
    }

    private static List<KeyValuePair<string, string>> CopyModVersions(
        IEnumerable<KeyValuePair<string, string>> source,
        string parameterName)
    {
        var copy = ContentEnumerable.CopyBounded(
            source,
            ContentContractLimits.MaximumCatalogItems,
            parameterName);
        if (copy.Any(pair =>
                !ContentValueValidation.IsTechnicalId(pair.Key) ||
                !ContentValueValidation.IsOptionalTechnicalValue(pair.Value)) ||
            copy.Select(pair => pair.Key).Distinct(StringComparer.Ordinal).Count() != copy.Count)
        {
            throw new ArgumentException("Mod-version evidence must be valid and unique.", parameterName);
        }

        return copy;
    }
}

internal static class ContentValueValidation
{
    internal static bool IsTechnicalId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= ContentContractLimits.MaximumTechnicalIdUtf16Length &&
        value.All(character => !char.IsControl(character));

    internal static bool IsOptionalTechnicalValue(string? value) =>
        value is not null &&
        value.Length > 0 &&
        value.Length <= ContentContractLimits.MaximumVisibleTextUtf16Length &&
        value.All(character => !char.IsControl(character));

    internal static void RequireTechnicalId(string? value, string parameterName)
    {
        if (!IsTechnicalId(value))
        {
            throw new ArgumentException("A bounded non-control technical ID is required.", parameterName);
        }
    }

    internal static void RequireOptionalTechnicalValue(string? value, string parameterName)
    {
        if (value is not null && !IsOptionalTechnicalValue(value))
        {
            throw new ArgumentException("A bounded non-control technical value is required.", parameterName);
        }
    }

    internal static void RequireVisibleText(string? value, string parameterName, bool allowEmpty)
    {
        if (value is null ||
            (!allowEmpty && string.IsNullOrWhiteSpace(value)) ||
            value.Length > ContentContractLimits.MaximumVisibleTextUtf16Length ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded non-control visible value is required.", parameterName);
        }
    }
}

internal static class ContentEnumerable
{
    internal static List<T> CopyBounded<T>(
        IEnumerable<T> source,
        int maximum,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        var copy = new List<T>(Math.Min(maximum, 256));
        using var enumerator = source.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (copy.Count == maximum)
            {
                throw new ArgumentException($"The collection exceeds its limit of {maximum}.", parameterName);
            }

            copy.Add(enumerator.Current);
        }

        return copy;
    }
}

internal sealed class ReadOnlySet<T> : IReadOnlySet<T>
{
    private readonly HashSet<T> values;

    internal ReadOnlySet(IEnumerable<T> source, IEqualityComparer<T>? comparer = null)
    {
        values = new HashSet<T>(source, comparer);
    }

    public int Count => values.Count;

    public bool Contains(T item) => values.Contains(item);

    public bool IsProperSubsetOf(IEnumerable<T> other) => values.IsProperSubsetOf(other);

    public bool IsProperSupersetOf(IEnumerable<T> other) => values.IsProperSupersetOf(other);

    public bool IsSubsetOf(IEnumerable<T> other) => values.IsSubsetOf(other);

    public bool IsSupersetOf(IEnumerable<T> other) => values.IsSupersetOf(other);

    public bool Overlaps(IEnumerable<T> other) => values.Overlaps(other);

    public bool SetEquals(IEnumerable<T> other) => values.SetEquals(other);

    public IEnumerator<T> GetEnumerator() => values.GetEnumerator();

    global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
