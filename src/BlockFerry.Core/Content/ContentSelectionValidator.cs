using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;

namespace BlockFerry.Core.Content;

public sealed class ValidatedContentSelection
{
    private ValidatedContentSelection(
        string adapterId,
        IReadOnlySet<ContentItemId> selectedItems,
        IReadOnlyDictionary<ContentItemId, ConflictResolution> conflictResolutions,
        string catalogDigest)
    {
        AdapterId = adapterId;
        SelectedItems = selectedItems;
        ConflictResolutions = conflictResolutions;
        CatalogDigest = catalogDigest;
    }

    public string AdapterId { get; }

    public IReadOnlySet<ContentItemId> SelectedItems { get; }

    public IReadOnlyDictionary<ContentItemId, ConflictResolution> ConflictResolutions { get; }

    internal string CatalogDigest { get; }

    internal bool IsBoundTo(ContentCatalog catalog) =>
        catalog is not null &&
        string.Equals(AdapterId, catalog.AdapterId, StringComparison.Ordinal) &&
        string.Equals(CatalogDigest, ContentCatalogDigest.Compute(catalog), StringComparison.Ordinal);

    internal static ValidatedContentSelection Create(
        ContentCatalog catalog,
        IEnumerable<ContentItemId> selectedItems,
        IEnumerable<KeyValuePair<ContentItemId, ConflictResolution>> conflictResolutions) =>
        new(
            catalog.AdapterId,
            new ReadOnlySet<ContentItemId>(selectedItems),
            conflictResolutions.ToFrozenDictionary(pair => pair.Key, pair => pair.Value),
            ContentCatalogDigest.Compute(catalog));
}

public static class ContentSelectionValidator
{
    public static bool TryCreateDefaults(
        ContentCatalog catalog,
        out ValidatedContentSelection? validated,
        out ContentDiagnostic? rejection)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        validated = null;
        rejection = null;
        var selected = new List<ContentItemId>();
        var resolutions = new List<KeyValuePair<ContentItemId, ConflictResolution>>();
        foreach (var item in catalog.Items)
        {
            if (item.Disposition is PlannedContentDisposition.Add or PlannedContentDisposition.Update &&
                item.IsSelectedByDefault)
            {
                return Reject(catalog.AdapterId, item.Id, out rejection);
            }

            if (item.Disposition == PlannedContentDisposition.Conflict)
            {
                if (item.IsSelectedByDefault ||
                    item.DefaultResolution is not (ConflictResolution.KeepTarget or ConflictResolution.Skip))
                {
                    return Reject(catalog.AdapterId, item.Id, out rejection);
                }

                resolutions.Add(new KeyValuePair<ContentItemId, ConflictResolution>(
                    item.Id,
                    item.DefaultResolution));
                continue;
            }

            if (item.IsSelectedByDefault)
            {
                selected.Add(item.Id);
            }
        }

        validated = ValidatedContentSelection.Create(catalog, selected, resolutions);
        return true;
    }

    public static bool TryValidateExplicit(
        ContentCatalog catalog,
        ContentSelection requested,
        out ValidatedContentSelection? validated,
        out ContentDiagnostic? rejection)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(requested);
        validated = null;
        rejection = null;

        var catalogItems = catalog.Items.ToDictionary(item => item.Id);
        foreach (var selectedId in requested.SelectedItems)
        {
            if (!catalogItems.TryGetValue(selectedId, out var item) || !item.IsSelectable)
            {
                return Reject(catalog.AdapterId, selectedId, out rejection);
            }
        }

        foreach (var (itemId, resolution) in requested.ConflictResolutions)
        {
            if (!catalogItems.TryGetValue(itemId, out var item) ||
                item.Disposition != PlannedContentDisposition.Conflict ||
                resolution == ConflictResolution.Unresolved)
            {
                return Reject(catalog.AdapterId, itemId, out rejection);
            }
        }

        foreach (var item in catalog.Items)
        {
            if (item.Disposition != PlannedContentDisposition.Conflict)
            {
                continue;
            }

            if (!requested.ConflictResolutions.TryGetValue(item.Id, out var resolution) ||
                resolution == ConflictResolution.Unresolved ||
                (resolution == ConflictResolution.UseSource && !requested.SelectedItems.Contains(item.Id)) ||
                (resolution != ConflictResolution.UseSource && requested.SelectedItems.Contains(item.Id)))
            {
                return Reject(catalog.AdapterId, item.Id, out rejection);
            }
        }

        validated = ValidatedContentSelection.Create(
            catalog,
            requested.SelectedItems,
            requested.ConflictResolutions);
        return true;
    }

    private static bool Reject(
        string adapterId,
        ContentItemId? itemId,
        out ContentDiagnostic? rejection)
    {
        var safeItemId = itemId is { } value &&
                         string.Equals(value.AdapterId, adapterId, StringComparison.Ordinal)
            ? itemId
            : null;
        rejection = ContentDiagnostic.Create(
            ContentDiagnosticCode.CapabilityRejected,
            ContentDiagnosticSeverity.Error,
            adapterId,
            safeItemId);
        return false;
    }
}

internal static class ContentCatalogDigest
{
    internal static string Compute(ContentCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendText(digest, catalog.AdapterId);
        AppendInt32(digest, catalog.Items.Count);
        foreach (var item in catalog.Items)
        {
            AppendText(digest, item.Id.AdapterId);
            AppendText(digest, item.Id.TechnicalKey);
            AppendInt32(digest, (int)item.Disposition);
            AppendInt32(digest, item.IsSelectable ? 1 : 0);
            AppendInt32(digest, item.IsSelectedByDefault ? 1 : 0);
            AppendInt32(digest, (int)item.DefaultResolution);
            AppendInt32(digest, item.DisabledReason is null ? -1 : (int)item.DisabledReason.Value);
        }

        return Convert.ToHexString(digest.GetHashAndReset());
    }

    private static void AppendText(IncrementalHash digest, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(digest, bytes.Length);
        digest.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash digest, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        digest.AppendData(bytes);
    }
}
