using System.Security.Cryptography;
using System.Text;

namespace BlockFerry.Core.Content;

internal sealed class ContentProbeContext
{
    private const int MaximumOpaquePayloadBytes = 1024 * 1024;
    private const int MaximumRawScopeUtf16Length = 262_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ContentAccessLease owner;

    private ContentProbeContext(
        ContentAccessLease lease,
        AdapterCompatibilityEvidence compatibility)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(compatibility);
        lease.ThrowIfUnavailable();
        owner = lease;
        Generation = lease.Generation;
        Source = lease.Source;
        Target = lease.Target;
        Compatibility = compatibility;
    }

    internal long Generation { get; }

    internal IReadOnlyInstanceAccess Source { get; }

    internal IReadOnlyInstanceAccess Target { get; }

    internal AdapterCompatibilityEvidence Compatibility { get; }

    internal static ContentProbeContext Create(
        ContentAccessLease lease,
        AdapterCompatibilityEvidence compatibility) =>
        new(lease, compatibility);

    internal bool IsOwnedBy(ContentAccessLease lease) =>
        lease is not null &&
        ReferenceEquals(owner, lease) &&
        owner.IsActive;

    internal void ThrowIfUnavailable() => owner.ThrowIfUnavailable();

    internal ContentItemId CreateGenerationBoundOpaqueId(
        string adapterId,
        string scopeKind,
        ReadOnlySpan<char> rawScope)
    {
        owner.ThrowIfUnavailable();
        ContentValueValidation.RequireTechnicalId(adapterId, nameof(adapterId));
        ContentValueValidation.RequireTechnicalId(scopeKind, nameof(scopeKind));
        if (rawScope.IsEmpty || rawScope.Length > MaximumRawScopeUtf16Length)
        {
            throw new ArgumentOutOfRangeException(nameof(rawScope));
        }

        var scopeKindBytes = StrictUtf8.GetByteCount(scopeKind);
        var rawScopeBytes = StrictUtf8.GetByteCount(rawScope);
        var payloadLength = checked(scopeKindBytes + 1 + rawScopeBytes);
        if (payloadLength > MaximumOpaquePayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(rawScope));
        }

        var domain = $"blockferry.content.{adapterId}.scope.v1";
        if (StrictUtf8.GetByteCount(domain) > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(adapterId));
        }

        var payload = new byte[payloadLength];
        try
        {
            var written = StrictUtf8.GetBytes(scopeKind, payload);
            payload[written] = 0;
            written += 1 + StrictUtf8.GetBytes(rawScope, payload.AsSpan(written + 1));
            if (written != payload.Length)
            {
                throw new InvalidOperationException("The opaque payload length changed during encoding.");
            }

            var tag = owner.Session.CreateGenerationOpaqueTag(domain, payload);
            if (tag.Length != 43 ||
                tag.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) &&
                    character is not '-' and not '_') ||
                !ContentItemId.TryCreate(adapterId, tag, out var id))
            {
                throw new InvalidOperationException("The session returned an invalid opaque identifier.");
            }

            return id;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }
}
