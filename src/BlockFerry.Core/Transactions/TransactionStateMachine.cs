using System.Collections.ObjectModel;

namespace BlockFerry.Core.Transactions;

public enum TransactionRecordKind
{
    Prepared,
    BackupIntent,
    BackupVerified,
    DirectoryIntent,
    DirectoryCreated,
    StageIntent,
    StageCreated,
    StageVerified,
    CommitIntent,
    CommitVerified,
    RollbackIntent,
    RollbackVerified,
    CleanupIntent,
    CleanupVerified,
    IntentAborted,
    Committed,
    RolledBack,
    RecoveryRequired,
}

public sealed class TransactionJournalRecord
{
    internal TransactionJournalRecord(
        long sequence,
        TransactionRecordKind kind,
        string opaqueObjectId,
        byte[] pathMac,
        byte[] contentDigest,
        byte[] previousMac,
        byte[] recordMac)
    {
        Sequence = sequence;
        Kind = kind;
        OpaqueObjectId = opaqueObjectId;
        PathMac = pathMac;
        ContentDigest = contentDigest;
        PreviousMac = previousMac;
        RecordMac = recordMac;
    }

    public long Sequence { get; }

    public TransactionRecordKind Kind { get; }

    public string OpaqueObjectId { get; }

    internal byte[] PathMac { get; }

    internal byte[] ContentDigest { get; }

    internal byte[] PreviousMac { get; }

    internal byte[] RecordMac { get; }

    public override string ToString() => $"Journal record {Sequence}: {Kind}; {OpaqueObjectId}";
}

public sealed class VerifiedJournal
{
    internal VerifiedJournal(
        TransactionId transactionId,
        IReadOnlyList<TransactionJournalRecord> records)
    {
        TransactionId = transactionId;
        Records = records;
    }

    public TransactionId TransactionId { get; }

    public IReadOnlyList<TransactionJournalRecord> Records { get; }

    public bool IsTerminal => Records.Count > 0 && TransactionStateMachine.IsTerminal(Records[^1].Kind);

    public TransactionRecordKind? TerminalKind => IsTerminal ? Records[^1].Kind : null;
}

internal static class TransactionStateMachine
{
    internal static void Validate(IReadOnlyList<TransactionJournalRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0 || records.Count > TransactionJournalCodec.MaximumRecords)
        {
            throw new TransactionAuthenticationException("The journal record count was invalid.");
        }

        TransactionRecordKind? pendingIntent = null;
        var phase = TransactionPhase.Prepared;
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (record.Sequence != index + 1L)
            {
                throw new TransactionAuthenticationException("The journal sequence was not strictly monotonic.");
            }

            if (index == 0)
            {
                if (record.Kind != TransactionRecordKind.Prepared)
                {
                    throw new TransactionAuthenticationException("The journal did not begin in Prepared state.");
                }

                continue;
            }

            if (IsTerminal(records[index - 1].Kind))
            {
                throw new TransactionAuthenticationException("The journal contained records after a terminal state.");
            }

            if (record.Kind == TransactionRecordKind.RecoveryRequired)
            {
                if (pendingIntent is not null)
                {
                    throw new TransactionAuthenticationException(
                        "RecoveryRequired cannot hide an unresolved mutation intent.");
                }

                phase = TransactionPhase.Rollback;
                continue;
            }

            if (IsTerminal(record.Kind))
            {
                if (pendingIntent is not null ||
                    record.Kind == TransactionRecordKind.Committed && phase < TransactionPhase.Commit ||
                    record.Kind == TransactionRecordKind.RolledBack && phase < TransactionPhase.Rollback)
                {
                    throw new TransactionAuthenticationException("The journal entered an invalid terminal state.");
                }

                continue;
            }

            if (IsIntent(record.Kind))
            {
                if (pendingIntent is not null)
                {
                    throw new TransactionAuthenticationException("The journal began a mutation before verifying the previous one.");
                }

                var nextPhase = PhaseOf(record.Kind);
                if (nextPhase < phase && nextPhase != TransactionPhase.Rollback ||
                    phase == TransactionPhase.Rollback && nextPhase != TransactionPhase.Rollback &&
                    nextPhase != TransactionPhase.Cleanup)
                {
                    throw new TransactionAuthenticationException("The journal mutation phase regressed.");
                }

                phase = nextPhase;
                pendingIntent = record.Kind;
                continue;
            }

            if (record.Kind == TransactionRecordKind.StageCreated)
            {
                if (pendingIntent != TransactionRecordKind.StageIntent)
                {
                    throw new TransactionAuthenticationException("StageCreated did not follow StageIntent.");
                }

                continue;
            }

            if (record.Kind == TransactionRecordKind.IntentAborted)
            {
                if (pendingIntent is null)
                {
                    throw new TransactionAuthenticationException("IntentAborted did not follow a pending intent.");
                }

                var abortedIntent = records.Take(index).Last(item => item.Kind == pendingIntent.Value);
                if (!string.Equals(
                        abortedIntent.OpaqueObjectId,
                        record.OpaqueObjectId,
                        StringComparison.Ordinal) ||
                    !abortedIntent.PathMac.AsSpan().SequenceEqual(record.PathMac))
                {
                    throw new TransactionAuthenticationException("IntentAborted was bound to another object or path.");
                }

                pendingIntent = null;
                continue;
            }

            if (pendingIntent is null || ExpectedVerification(pendingIntent.Value) != record.Kind)
            {
                throw new TransactionAuthenticationException("A journal verification did not match its mutation intent.");
            }

            var intentRecord = records.Take(index).Last(item => item.Kind == pendingIntent.Value);
            if (!string.Equals(intentRecord.OpaqueObjectId, record.OpaqueObjectId, StringComparison.Ordinal) ||
                !intentRecord.PathMac.AsSpan().SequenceEqual(record.PathMac))
            {
                throw new TransactionAuthenticationException("A journal verification was bound to another object or path.");
            }

            pendingIntent = null;
        }
    }

    internal static bool IsIntent(TransactionRecordKind kind) => kind is
        TransactionRecordKind.BackupIntent or
        TransactionRecordKind.DirectoryIntent or
        TransactionRecordKind.StageIntent or
        TransactionRecordKind.CommitIntent or
        TransactionRecordKind.RollbackIntent or
        TransactionRecordKind.CleanupIntent;

    internal static bool IsVerification(TransactionRecordKind kind) => kind is
        TransactionRecordKind.BackupVerified or
        TransactionRecordKind.DirectoryCreated or
        TransactionRecordKind.StageVerified or
        TransactionRecordKind.CommitVerified or
        TransactionRecordKind.RollbackVerified or
        TransactionRecordKind.CleanupVerified;

    internal static bool IsTerminal(TransactionRecordKind kind) => kind is
        TransactionRecordKind.Committed or
        TransactionRecordKind.RolledBack;

    internal static bool IsTerminalRecord(TransactionRecordKind kind) =>
        IsTerminal(kind) || kind == TransactionRecordKind.RecoveryRequired;

    internal static TransactionRecordKind ExpectedVerification(TransactionRecordKind intent) => intent switch
    {
        TransactionRecordKind.BackupIntent => TransactionRecordKind.BackupVerified,
        TransactionRecordKind.DirectoryIntent => TransactionRecordKind.DirectoryCreated,
        TransactionRecordKind.StageIntent => TransactionRecordKind.StageVerified,
        TransactionRecordKind.CommitIntent => TransactionRecordKind.CommitVerified,
        TransactionRecordKind.RollbackIntent => TransactionRecordKind.RollbackVerified,
        TransactionRecordKind.CleanupIntent => TransactionRecordKind.CleanupVerified,
        _ => throw new ArgumentOutOfRangeException(nameof(intent)),
    };

    internal static ReadOnlyCollection<TransactionJournalRecord> Retain(
        IEnumerable<TransactionJournalRecord> records) =>
        new(records.ToArray());

    private static TransactionPhase PhaseOf(TransactionRecordKind kind) => kind switch
    {
        TransactionRecordKind.BackupIntent => TransactionPhase.Backup,
        TransactionRecordKind.DirectoryIntent => TransactionPhase.Directory,
        TransactionRecordKind.StageIntent => TransactionPhase.Stage,
        TransactionRecordKind.CommitIntent => TransactionPhase.Commit,
        TransactionRecordKind.RollbackIntent => TransactionPhase.Rollback,
        TransactionRecordKind.CleanupIntent => TransactionPhase.Cleanup,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private enum TransactionPhase
    {
        Prepared,
        Backup,
        Directory,
        Stage,
        Commit,
        Rollback,
        Cleanup,
    }
}
