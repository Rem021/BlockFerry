using BlockFerry.Core.Discovery;
using BlockFerry.Core.Content;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Transactions;

public sealed record VerifiedRecoverySelection(
    DiscoveredInstanceChoice Target,
    PhysicalDirectoryIdentity RecordedTargetIdentity);

public enum MigrationRecoveryStatus
{
    Recovered,
    AlreadyTerminal,
    TargetReselectionRequired,
    Blocked,
    AuthenticationFailed,
    CurrentStateChanged,
    RecoveryRequired,
}

public sealed record PendingRecovery(
    TransactionId TransactionId,
    string TargetInstanceId,
    bool TargetPathAvailable,
    MigrationRecoveryStatus? AttentionStatus = null);

public sealed record MigrationRecoveryResult(
    MigrationRecoveryStatus Status,
    TransactionId TransactionId,
    int RestoredFileCount,
    string Message)
{
    public bool IsRecovered =>
        Status is MigrationRecoveryStatus.Recovered or MigrationRecoveryStatus.AlreadyTerminal;
}

public sealed record MigrationUndoResult(
    MigrationRecoveryStatus Status,
    TransactionId? UndoTransactionId,
    int RestoredFileCount,
    string Message)
{
    public bool IsUndone => Status == MigrationRecoveryStatus.Recovered;
}

internal sealed class RecoveryReadOnlyContext : IDisposable
{
    private readonly RecoveryCatalogContext catalogContext;
    private readonly DiscoverySession discoverySession;
    private int active = 1;

    internal RecoveryReadOnlyContext(
        RecoveryLocator locator,
        IReadOnlySet<NormalizedRelativePath> authorizedPaths,
        RecoveryCatalogContext catalogContext,
        DiscoverySession discoverySession)
    {
        Locator = locator ?? throw new ArgumentNullException(nameof(locator));
        AuthorizedPaths = authorizedPaths ?? throw new ArgumentNullException(nameof(authorizedPaths));
        if (authorizedPaths.Count == 0)
        {
            throw new ArgumentException(
                "Undo eligibility requires a non-empty authenticated read allowlist.",
                nameof(authorizedPaths));
        }

        this.catalogContext = catalogContext ?? throw new ArgumentNullException(nameof(catalogContext));
        this.discoverySession = discoverySession ??
            throw new ArgumentNullException(nameof(discoverySession));
    }

    internal RecoveryLocator Locator { get; }

    internal IReadOnlySet<NormalizedRelativePath> AuthorizedPaths { get; }

    internal bool IsActive => Volatile.Read(ref active) == 1;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref active, 0) == 0)
        {
            return;
        }

        try
        {
            catalogContext.Dispose();
        }
        finally
        {
            discoverySession.Dispose();
        }
    }
}

internal sealed class RecoveryExecutionAuthority : IDisposable
{
    private readonly RecoveryCatalogContext? catalogContext;
    private readonly DiscoverySession? discoverySession;
    private int active = 1;

    internal RecoveryExecutionAuthority(
        RecoveryLocator locator,
        IReadOnlySet<NormalizedRelativePath> writeAllowlist,
        RecoveryCatalogContext? catalogContext = null,
        DiscoverySession? discoverySession = null)
    {
        Locator = locator ?? throw new ArgumentNullException(nameof(locator));
        WriteAllowlist = writeAllowlist ?? throw new ArgumentNullException(nameof(writeAllowlist));
        if (writeAllowlist.Count == 0)
        {
            throw new ArgumentException("Recovery requires a non-empty authenticated write allowlist.", nameof(writeAllowlist));
        }

        if ((catalogContext is null) != (discoverySession is null))
        {
            throw new ArgumentException("Recovery discovery proof ownership must be complete.");
        }

        this.catalogContext = catalogContext;
        this.discoverySession = discoverySession;
    }

    internal RecoveryLocator Locator { get; }

    internal IReadOnlySet<NormalizedRelativePath> WriteAllowlist { get; }

    internal bool IsActive => Volatile.Read(ref active) == 1;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref active, 0) == 0)
        {
            return;
        }

        try
        {
            catalogContext?.Dispose();
        }
        finally
        {
            discoverySession?.Dispose();
        }
    }
}
