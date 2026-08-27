using System.Security.Cryptography;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Transactions;

public sealed class TransactionIntent
{
    private TransactionIntent(
        TransactionRecordKind kind,
        string opaqueObjectId,
        NormalizedRelativePath relativePath,
        string expectedBeforeSha256)
    {
        Kind = kind;
        OpaqueObjectId = opaqueObjectId;
        RelativePath = relativePath;
        ExpectedBeforeSha256 = expectedBeforeSha256;
    }

    public TransactionRecordKind Kind { get; }

    public string OpaqueObjectId { get; }

    internal NormalizedRelativePath RelativePath { get; }

    internal string ExpectedBeforeSha256 { get; }

    public static TransactionIntent Create(
        TransactionRecordKind kind,
        string opaqueObjectId,
        NormalizedRelativePath relativePath,
        string expectedBeforeSha256)
    {
        if (!TransactionStateMachine.IsIntent(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        TransactionValueValidation.RequireOpaqueId(opaqueObjectId, nameof(opaqueObjectId));
        ArgumentNullException.ThrowIfNull(relativePath);
        TransactionValueValidation.RequireSha256(expectedBeforeSha256, nameof(expectedBeforeSha256));
        if (!WritePathGuard.TryNormalize(relativePath.Value, out var normalized) ||
            normalized is null ||
            normalized.Value.Length == 0)
        {
            throw new ArgumentException("A normalized non-empty transaction path is required.", nameof(relativePath));
        }

        return new TransactionIntent(kind, opaqueObjectId, normalized, expectedBeforeSha256);
    }
}

public sealed class TransactionVerification
{
    private TransactionVerification(
        TransactionRecordKind kind,
        string opaqueObjectId,
        NormalizedRelativePath relativePath,
        string observedSha256)
    {
        Kind = kind;
        OpaqueObjectId = opaqueObjectId;
        RelativePath = relativePath;
        ObservedSha256 = observedSha256;
    }

    public TransactionRecordKind Kind { get; }

    public string OpaqueObjectId { get; }

    internal NormalizedRelativePath RelativePath { get; }

    internal string ObservedSha256 { get; }

    public static TransactionVerification Create(
        TransactionRecordKind kind,
        string opaqueObjectId,
        NormalizedRelativePath relativePath,
        string observedSha256)
    {
        if (!TransactionStateMachine.IsVerification(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        TransactionValueValidation.RequireOpaqueId(opaqueObjectId, nameof(opaqueObjectId));
        ArgumentNullException.ThrowIfNull(relativePath);
        TransactionValueValidation.RequireSha256(observedSha256, nameof(observedSha256));
        if (!WritePathGuard.TryNormalize(relativePath.Value, out var normalized) ||
            normalized is null ||
            normalized.Value.Length == 0)
        {
            throw new ArgumentException("A normalized non-empty transaction path is required.", nameof(relativePath));
        }

        return new TransactionVerification(kind, opaqueObjectId, normalized, observedSha256);
    }
}

public sealed class JournalMutationPermit
{
    private const int Issued = 0;
    private const int MutationConsumed = 1;
    private const int VerificationRecorded = 2;
    private int lifecycle;

    internal JournalMutationPermit(
        TransactionId transactionId,
        long intentSequence,
        TransactionRecordKind intentKind,
        string opaqueObjectId,
        NormalizedRelativePath relativePath,
        byte[] pathMac)
    {
        TransactionId = transactionId;
        IntentSequence = intentSequence;
        IntentKind = intentKind;
        OpaqueObjectId = opaqueObjectId;
        RelativePath = relativePath;
        PathMac = pathMac;
    }

    internal TransactionId TransactionId { get; }

    internal long IntentSequence { get; }

    internal TransactionRecordKind IntentKind { get; }

    internal string OpaqueObjectId { get; }

    internal NormalizedRelativePath RelativePath { get; }

    internal byte[] PathMac { get; }

    internal bool IsMutationConsumed => Volatile.Read(ref lifecycle) == MutationConsumed;

    internal void MarkMutationAlreadyObserved()
    {
        if (Interlocked.CompareExchange(ref lifecycle, MutationConsumed, Issued) != Issued)
        {
            throw new InvalidOperationException("The recovered journal mutation permit is not issuable.");
        }
    }

    internal void Consume(
        TransactionId transactionId,
        TransactionRecordKind intentKind,
        string opaqueObjectId,
        NormalizedRelativePath relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (transactionId != TransactionId ||
            intentKind != IntentKind ||
            !string.Equals(opaqueObjectId, OpaqueObjectId, StringComparison.Ordinal) ||
            !string.Equals(relativePath.Value, RelativePath.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The journal mutation permit does not authorize this operation.");
        }

        if (Interlocked.CompareExchange(ref lifecycle, MutationConsumed, Issued) != Issued)
        {
            throw new InvalidOperationException("The journal mutation permit has already been consumed.");
        }
    }

    internal void MarkVerificationRecorded()
    {
        if (Interlocked.CompareExchange(
                ref lifecycle,
                VerificationRecorded,
                MutationConsumed) != MutationConsumed)
        {
            throw new InvalidOperationException("The journal mutation permit is not awaiting verification.");
        }
    }
}

public sealed class TransactionJournal : IDisposable
{
    private const string JournalName = "journal.log";
    private readonly object gate = new();
    private readonly ITransactionStorageDirectory storage;
    private readonly byte[] key;
    private bool disposed;

    internal TransactionId TransactionId => storage.TransactionId;

    internal TransactionJournal(
        ITransactionStorageDirectory storage,
        ReadOnlySpan<byte> key)
    {
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        if (key.Length != 32)
        {
            throw new ArgumentException("A 256-bit journal key is required.", nameof(key));
        }

        this.key = key.ToArray();
    }

    public JournalMutationPermit AppendIntent(
        TransactionIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        lock (gate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var currentBytes = storage.ReadFile(
                JournalName,
                TransactionJournalCodec.MaximumJournalBytes,
                cancellationToken);
            var current = TransactionJournalCodec.DecodeAndVerify(
                currentBytes,
                storage.TransactionId,
                key);
            if (current.IsTerminal)
            {
                throw new InvalidOperationException("A terminal journal cannot issue another mutation permit.");
            }

            var sequence = checked(current.Records[^1].Sequence + 1);
            var pathMac = TransactionJournalCodec.ComputePathMac(
                storage.TransactionId,
                intent.RelativePath.Value,
                key);
            var digest = Convert.FromHexString(intent.ExpectedBeforeSha256);
            try
            {
                var encoded = TransactionJournalCodec.EncodeRecord(
                    storage.TransactionId,
                    sequence,
                    intent.Kind,
                    intent.OpaqueObjectId,
                    pathMac,
                    digest,
                    current.Records[^1].RecordMac,
                    key);
                ValidateProspective(currentBytes, encoded);
                storage.AppendAndFlush(
                    JournalName,
                    encoded,
                    TransactionJournalCodec.MaximumJournalBytes,
                    cancellationToken);
                return new JournalMutationPermit(
                    storage.TransactionId,
                    sequence,
                    intent.Kind,
                    intent.OpaqueObjectId,
                    intent.RelativePath,
                    pathMac.ToArray());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pathMac);
                CryptographicOperations.ZeroMemory(digest);
                CryptographicOperations.ZeroMemory(currentBytes);
            }
        }
    }

    public void AppendVerified(
        JournalMutationPermit permit,
        TransactionVerification verification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(permit);
        ArgumentNullException.ThrowIfNull(verification);
        lock (gate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (!permit.IsMutationConsumed ||
                permit.TransactionId != storage.TransactionId ||
                TransactionStateMachine.ExpectedVerification(permit.IntentKind) != verification.Kind ||
                !string.Equals(permit.OpaqueObjectId, verification.OpaqueObjectId, StringComparison.Ordinal) ||
                !string.Equals(permit.RelativePath.Value, verification.RelativePath.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The journal verification does not match a consumed mutation permit.");
            }

            var currentBytes = storage.ReadFile(
                JournalName,
                TransactionJournalCodec.MaximumJournalBytes,
                cancellationToken);
            var current = TransactionJournalCodec.DecodeAndVerify(
                currentBytes,
                storage.TransactionId,
                key);
            if (current.IsTerminal ||
                current.Records[^1].Sequence != permit.IntentSequence ||
                current.Records[^1].Kind != permit.IntentKind ||
                !current.Records[^1].PathMac.AsSpan().SequenceEqual(permit.PathMac))
            {
                throw new InvalidOperationException("The journal no longer ends at the permit's mutation intent.");
            }

            var digest = Convert.FromHexString(verification.ObservedSha256);
            try
            {
                var encoded = TransactionJournalCodec.EncodeRecord(
                    storage.TransactionId,
                    checked(permit.IntentSequence + 1),
                    verification.Kind,
                    verification.OpaqueObjectId,
                    permit.PathMac,
                    digest,
                    current.Records[^1].RecordMac,
                    key);
                ValidateProspective(currentBytes, encoded);
                storage.AppendAndFlush(
                    JournalName,
                    encoded,
                    TransactionJournalCodec.MaximumJournalBytes,
                    cancellationToken);
                permit.MarkVerificationRecorded();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
                CryptographicOperations.ZeroMemory(currentBytes);
            }
        }
    }

    internal JournalMutationPermit ResumeObservedIntent(
        TransactionRecordKind intentKind,
        string opaqueObjectId,
        NormalizedRelativePath relativePath,
        CancellationToken cancellationToken)
    {
        if (!TransactionStateMachine.IsIntent(intentKind))
        {
            throw new ArgumentOutOfRangeException(nameof(intentKind));
        }

        TransactionValueValidation.RequireOpaqueId(opaqueObjectId, nameof(opaqueObjectId));
        ArgumentNullException.ThrowIfNull(relativePath);
        lock (gate)
        {
            ThrowIfDisposed();
            var currentBytes = storage.ReadFile(
                JournalName,
                TransactionJournalCodec.MaximumJournalBytes,
                cancellationToken);
            try
            {
                var current = TransactionJournalCodec.DecodeAndVerify(
                    currentBytes,
                    storage.TransactionId,
                    key);
                var last = current.Records[^1];
                var pathMac = TransactionJournalCodec.ComputePathMac(
                    storage.TransactionId,
                    relativePath.Value,
                    key);
                try
                {
                    if (last.Kind != intentKind ||
                        !string.Equals(last.OpaqueObjectId, opaqueObjectId, StringComparison.Ordinal) ||
                        !last.PathMac.AsSpan().SequenceEqual(pathMac))
                    {
                        throw new InvalidOperationException("The journal does not end in the observed recovery intent.");
                    }

                    var permit = new JournalMutationPermit(
                        storage.TransactionId,
                        last.Sequence,
                        last.Kind,
                        last.OpaqueObjectId,
                        relativePath,
                        pathMac.ToArray());
                    permit.MarkMutationAlreadyObserved();
                    return permit;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pathMac);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(currentBytes);
            }
        }
    }

    internal void AppendIntentAborted(
        TransactionRecordKind intentKind,
        string opaqueObjectId,
        NormalizedRelativePath relativePath,
        CancellationToken cancellationToken)
    {
        if (!TransactionStateMachine.IsIntent(intentKind))
        {
            throw new ArgumentOutOfRangeException(nameof(intentKind));
        }

        TransactionValueValidation.RequireOpaqueId(opaqueObjectId, nameof(opaqueObjectId));
        ArgumentNullException.ThrowIfNull(relativePath);
        lock (gate)
        {
            ThrowIfDisposed();
            var currentBytes = storage.ReadFile(
                JournalName,
                TransactionJournalCodec.MaximumJournalBytes,
                cancellationToken);
            try
            {
                var current = TransactionJournalCodec.DecodeAndVerify(
                    currentBytes,
                    storage.TransactionId,
                    key);
                var last = current.Records[^1];
                var pathMac = TransactionJournalCodec.ComputePathMac(
                    storage.TransactionId,
                    relativePath.Value,
                    key);
                try
                {
                    if (last.Kind != intentKind ||
                        !string.Equals(last.OpaqueObjectId, opaqueObjectId, StringComparison.Ordinal) ||
                        !last.PathMac.AsSpan().SequenceEqual(pathMac))
                    {
                        throw new InvalidOperationException("The journal does not end in the abortable recovery intent.");
                    }

                    var encoded = TransactionJournalCodec.EncodeRecord(
                        storage.TransactionId,
                        checked(last.Sequence + 1),
                        TransactionRecordKind.IntentAborted,
                        opaqueObjectId,
                        pathMac,
                        new byte[32],
                        last.RecordMac,
                        key);
                    ValidateProspective(currentBytes, encoded);
                    storage.AppendAndFlush(
                        JournalName,
                        encoded,
                        TransactionJournalCodec.MaximumJournalBytes,
                        cancellationToken);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pathMac);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(currentBytes);
            }
        }
    }

    public VerifiedJournal ReadAndVerify(
        TransactionId transactionId,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (transactionId != storage.TransactionId)
            {
                throw new TransactionAuthenticationException("The requested transaction ID did not match the journal capability.");
            }

            var bytes = storage.ReadFile(
                JournalName,
                TransactionJournalCodec.MaximumJournalBytes,
                cancellationToken);
            try
            {
                return TransactionJournalCodec.DecodeAndVerify(bytes, transactionId, key);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    internal void AppendTerminal(
        TransactionRecordKind terminalKind,
        string contentSha256,
        CancellationToken cancellationToken)
    {
        if (!TransactionStateMachine.IsTerminalRecord(terminalKind))
        {
            throw new ArgumentOutOfRangeException(nameof(terminalKind));
        }

        TransactionValueValidation.RequireSha256(contentSha256, nameof(contentSha256));
        lock (gate)
        {
            ThrowIfDisposed();
            var currentBytes = storage.ReadFile(
                JournalName,
                TransactionJournalCodec.MaximumJournalBytes,
                cancellationToken);
            var current = TransactionJournalCodec.DecodeAndVerify(
                currentBytes,
                storage.TransactionId,
                key);
            if (current.IsTerminal ||
                terminalKind == TransactionRecordKind.RecoveryRequired &&
                current.Records[^1].Kind == TransactionRecordKind.RecoveryRequired)
            {
                throw new InvalidOperationException("The journal is already terminal.");
            }

            var digest = Convert.FromHexString(contentSha256);
            try
            {
                var encoded = TransactionJournalCodec.EncodeRecord(
                    storage.TransactionId,
                    checked(current.Records[^1].Sequence + 1),
                    terminalKind,
                    "transaction",
                    new byte[32],
                    digest,
                    current.Records[^1].RecordMac,
                    key);
                ValidateProspective(currentBytes, encoded);
                storage.AppendAndFlush(
                    JournalName,
                    encoded,
                    TransactionJournalCodec.MaximumJournalBytes,
                    cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
                CryptographicOperations.ZeroMemory(currentBytes);
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            CryptographicOperations.ZeroMemory(key);
        }
    }

    internal static byte[] CreatePreparedPayload(
        TransactionId transactionId,
        string planDigest,
        ReadOnlySpan<byte> key)
    {
        TransactionValueValidation.RequireSha256(planDigest, nameof(planDigest));
        var digest = Convert.FromHexString(planDigest);
        try
        {
            return TransactionJournalCodec.EncodeRecord(
                transactionId,
                1,
                TransactionRecordKind.Prepared,
                "transaction",
                new byte[32],
                digest,
                new byte[32],
                key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private void ValidateProspective(byte[] current, byte[] appended)
    {
        if (appended.Length > TransactionJournalCodec.MaximumJournalBytes - current.Length)
        {
            throw new InvalidOperationException("The journal would exceed its fixed bound.");
        }

        var prospective = new byte[current.Length + appended.Length];
        try
        {
            current.CopyTo(prospective, 0);
            appended.CopyTo(prospective, current.Length);
            _ = TransactionJournalCodec.DecodeAndVerify(prospective, storage.TransactionId, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(prospective);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}
