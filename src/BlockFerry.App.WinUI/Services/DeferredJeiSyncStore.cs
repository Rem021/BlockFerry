using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlockFerry.Core.Content;
using BlockFerry.Core.System;
using BlockFerry.Core.Transactions;

namespace BlockFerry.App.WinUI.Services;

internal sealed record DeferredJeiSyncRecord(
    string SourceInstanceId,
    string TargetInstanceId,
    TransactionId OriginalTransactionId,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<DeferredJeiSeed> Seeds);

internal interface IDeferredJeiSyncStore
{
    IReadOnlyList<DeferredJeiSyncRecord> Load(CancellationToken cancellationToken = default);

    bool Upsert(DeferredJeiSyncRecord record, CancellationToken cancellationToken = default);

    bool Remove(TransactionId originalTransactionId, CancellationToken cancellationToken = default);
}

internal sealed class DeferredJeiSyncStore(
    AppStorageGuard storage,
    IProtectedData protectedData) : IDeferredJeiSyncStore
{
    private const int MaximumPlaintextBytes = 256 * 1024;
    private const int MaximumCiphertextBytes = 1024 * 1024;
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("BlockFerry/deferred-jei-sync/schema-1/current-user");
    private static readonly NormalizedRelativePath PayloadPath = CreatePayloadPath();
    private readonly object gate = new();
    private IReadOnlyList<DeferredJeiSyncRecord> memory = Array.Empty<DeferredJeiSyncRecord>();

    internal string? LastDiagnostic { get; private set; }

    public IReadOnlyList<DeferredJeiSyncRecord> Load(
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!storage.IsAvailable)
            {
                LastDiagnostic = "待复核记录只能保留到本次运行结束。";
                return memory;
            }

            var read = storage.TryRead(PayloadPath, MaximumCiphertextBytes, cancellationToken);
            if (read.State == AppStorageReadState.Missing)
            {
                memory = Array.Empty<DeferredJeiSyncRecord>();
                LastDiagnostic = null;
                return memory;
            }

            if (read.State != AppStorageReadState.Read || read.Bytes is null)
            {
                LastDiagnostic = "待复核记录未能通过受保护存储检查。";
                return memory;
            }

            byte[]? plaintext = null;
            try
            {
                plaintext = protectedData.Unprotect(
                    read.Bytes,
                    Entropy,
                    MaximumPlaintextBytes);
                memory = DeferredJeiSyncPayloadCodec.Parse(plaintext);
                LastDiagnostic = null;
            }
            catch (Exception exception) when (
                exception is CryptographicException or
                    ArgumentException or
                    InvalidOperationException or
                    JsonException)
            {
                memory = Array.Empty<DeferredJeiSyncRecord>();
                LastDiagnostic = "待复核记录无法验证，已停止自动写入。";
            }
            finally
            {
                if (plaintext is not null)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }

            return memory;
        }
    }

    public bool Upsert(
        DeferredJeiSyncRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeferredJeiSyncPayloadCodec.ValidateRecord(record);
            var next = memory
                .Where(candidate =>
                    candidate.OriginalTransactionId != record.OriginalTransactionId &&
                    !(string.Equals(
                          candidate.SourceInstanceId,
                          record.SourceInstanceId,
                          StringComparison.Ordinal) &&
                      string.Equals(
                          candidate.TargetInstanceId,
                          record.TargetInstanceId,
                          StringComparison.Ordinal)))
                .Append(record)
                .OrderByDescending(candidate => candidate.CreatedUtc)
                .Take(DeferredJeiSyncPayloadCodec.MaximumRecords)
                .ToArray();
            memory = Array.AsReadOnly(next);
            return Persist(cancellationToken);
        }
    }

    public bool Remove(
        TransactionId originalTransactionId,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = memory
                .Where(record => record.OriginalTransactionId != originalTransactionId)
                .ToArray();
            memory = Array.AsReadOnly(next);
            if (!storage.IsAvailable)
            {
                LastDiagnostic = "待复核记录只能保留到本次运行结束。";
                return false;
            }

            if (next.Length == 0)
            {
                var deleted = storage.TryDelete(PayloadPath, cancellationToken);
                LastDiagnostic = deleted.State == AppStorageMutationState.CommittedVerified
                    ? null
                    : "待复核记录未能从受保护存储清除。";
                return deleted.State == AppStorageMutationState.CommittedVerified;
            }

            return Persist(cancellationToken);
        }
    }

    private bool Persist(CancellationToken cancellationToken)
    {
        byte[]? plaintext = null;
        byte[]? ciphertext = null;
        try
        {
            plaintext = DeferredJeiSyncPayloadCodec.Serialize(memory);
            ciphertext = protectedData.Protect(
                plaintext,
                Entropy,
                MaximumCiphertextBytes);
            if (!storage.IsAvailable)
            {
                LastDiagnostic = "待复核记录只能保留到本次运行结束。";
                return false;
            }

            var written = storage.TryAtomicReplace(PayloadPath, ciphertext, cancellationToken);
            LastDiagnostic = written.State == AppStorageMutationState.CommittedVerified
                ? null
                : "待复核记录未能写入受保护存储。";
            return written.State == AppStorageMutationState.CommittedVerified;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is CryptographicException or
                ArgumentException or
                InvalidOperationException or
                JsonException)
        {
            LastDiagnostic = "待复核记录未能通过保护或格式检查。";
            return false;
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            if (ciphertext is not null)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }
        }
    }

    private static NormalizedRelativePath CreatePayloadPath()
    {
        if (!NormalizedRelativePath.TryCreate(
                "deferred-jei-sync.dpapi",
                out var path,
                out _))
        {
            throw new InvalidOperationException("The deferred JEI payload path is invalid.");
        }

        return path!;
    }
}

internal static class DeferredJeiSyncPayloadCodec
{
    internal const int MaximumRecords = 16;
    private const int CurrentSchemaVersion = 1;
    private const int MaximumSeedsPerRecord = 64;
    private const int MaximumInstanceIdLength = 1024;
    private const int MaximumPlaintextBytes = 256 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        MaxDepth = 12,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static byte[] Serialize(IEnumerable<DeferredJeiSyncRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var copy = records.Take(MaximumRecords + 1).ToArray();
        if (copy.Length > MaximumRecords)
        {
            throw new ArgumentException("Too many deferred JEI records.", nameof(records));
        }

        foreach (var record in copy)
        {
            ValidateRecord(record);
        }

        var dto = new PayloadDto(
            CurrentSchemaVersion,
            copy.Select(record => new RecordDto(
                    record.SourceInstanceId,
                    record.TargetInstanceId,
                    record.OriginalTransactionId.Value.ToString("N"),
                    record.CreatedUtc.ToUnixTimeSeconds(),
                    record.Seeds.Select(seed => new SeedDto(
                            seed.SourceRelativePath.Value,
                            seed.ProvisionalTargetRelativePath.Value,
                            seed.SourceSha256))
                        .ToArray()))
                .ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, Options);
        if (bytes.Length > MaximumPlaintextBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException("The deferred JEI payload is too large.", nameof(records));
        }

        return bytes;
    }

    internal static IReadOnlyList<DeferredJeiSyncRecord> Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumPlaintextBytes)
        {
            throw new JsonException("The deferred JEI payload length is invalid.");
        }

        var dto = JsonSerializer.Deserialize<PayloadDto>(bytes, Options) ??
            throw new JsonException("The deferred JEI payload is missing.");
        if (dto.SchemaVersion != CurrentSchemaVersion ||
            dto.Records is null ||
            dto.Records.Length > MaximumRecords)
        {
            throw new JsonException("The deferred JEI payload schema is invalid.");
        }

        var records = new List<DeferredJeiSyncRecord>(dto.Records.Length);
        var transactions = new HashSet<Guid>();
        foreach (var record in dto.Records)
        {
            if (record is null ||
                !Guid.TryParseExact(record.OriginalTransactionId, "N", out var transaction) ||
                transaction == Guid.Empty ||
                record.Seeds is null ||
                record.Seeds.Length is 0 or > MaximumSeedsPerRecord ||
                !transactions.Add(transaction))
            {
                throw new JsonException("A deferred JEI record is invalid.");
            }

            var seeds = new List<DeferredJeiSeed>(record.Seeds.Length);
            foreach (var seed in record.Seeds)
            {
                if (seed is null ||
                    !TryServerBookmarkPath(seed.SourceRelativePath, out var sourcePath) ||
                    !TryServerBookmarkPath(seed.ProvisionalTargetRelativePath, out var targetPath) ||
                    !IsSha256(seed.SourceSha256))
                {
                    throw new JsonException("A deferred JEI seed is invalid.");
                }

                seeds.Add(new DeferredJeiSeed(
                    sourcePath!,
                    targetPath!,
                    seed.SourceSha256));
            }

            DateTimeOffset created;
            try
            {
                created = DateTimeOffset.FromUnixTimeSeconds(record.CreatedUnixSeconds);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new JsonException("A deferred JEI timestamp is invalid.", exception);
            }

            var parsed = new DeferredJeiSyncRecord(
                record.SourceInstanceId,
                record.TargetInstanceId,
                new TransactionId(transaction),
                created,
                Array.AsReadOnly(seeds.ToArray()));
            ValidateRecord(parsed);
            records.Add(parsed);
        }

        return Array.AsReadOnly(records.ToArray());
    }

    internal static void ValidateRecord(DeferredJeiSyncRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!IsInstanceId(record.SourceInstanceId) ||
            !IsInstanceId(record.TargetInstanceId) ||
            string.Equals(record.SourceInstanceId, record.TargetInstanceId, StringComparison.Ordinal) ||
            record.OriginalTransactionId.Value == Guid.Empty ||
            record.Seeds is null ||
            record.Seeds.Count is 0 or > MaximumSeedsPerRecord)
        {
            throw new ArgumentException("The deferred JEI record is invalid.", nameof(record));
        }

        var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seed in record.Seeds)
        {
            if (seed is null ||
                !TryServerBookmarkPath(seed.SourceRelativePath.Value, out _) ||
                !TryServerBookmarkPath(seed.ProvisionalTargetRelativePath.Value, out _) ||
                !IsSha256(seed.SourceSha256) ||
                !sourcePaths.Add(seed.SourceRelativePath.Value))
            {
                throw new ArgumentException("The deferred JEI seed is invalid.", nameof(record));
            }
        }
    }

    private static bool IsInstanceId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumInstanceIdLength &&
        !value.Any(char.IsControl);

    private static bool TryServerBookmarkPath(
        string? value,
        out ContentRelativePath? path)
    {
        path = null;
        if (value is null ||
            !ContentRelativePath.TryCreate(value, out var candidate, out _) ||
            candidate is null)
        {
            return false;
        }

        var segments = candidate.Value.Split('\\');
        if (segments.Length != 6 ||
            !string.Equals(segments[0], "config", StringComparison.Ordinal) ||
            !string.Equals(segments[1], "jei", StringComparison.Ordinal) ||
            !string.Equals(segments[2], "world", StringComparison.Ordinal) ||
            !string.Equals(segments[3], "server", StringComparison.Ordinal) ||
            string.IsNullOrEmpty(segments[4]) ||
            !string.Equals(segments[5], "bookmarks.json", StringComparison.Ordinal))
        {
            return false;
        }

        path = candidate;
        return true;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or
                >= 'A' and <= 'F' or
                >= 'a' and <= 'f');

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record PayloadDto(int SchemaVersion, RecordDto[] Records);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record RecordDto(
        string SourceInstanceId,
        string TargetInstanceId,
        string OriginalTransactionId,
        long CreatedUnixSeconds,
        SeedDto[] Seeds);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record SeedDto(
        string SourceRelativePath,
        string ProvisionalTargetRelativePath,
        string SourceSha256);
}
