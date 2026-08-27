using BlockFerry.Core.System;

namespace BlockFerry.Core.Transactions;

internal interface ITransactionStoreProvider
{
    IProtectedData ProtectedData { get; }

    IReadOnlyList<TransactionId> List(CancellationToken cancellationToken);

    AuthenticatedTransactionStore Open(
        TransactionId transactionId,
        CancellationToken cancellationToken);

    AuthenticatedTransactionStore Create(
        RecoveryLocator locator,
        StoredMigrationPlan plan,
        CancellationToken cancellationToken);
}

internal sealed class AppStorageTransactionStoreProvider(
    AppStorageGuard appStorage,
    IProtectedData protectedData) : ITransactionStoreProvider
{
    private readonly AppStorageGuard appStorage =
        appStorage ?? throw new ArgumentNullException(nameof(appStorage));

    public IProtectedData ProtectedData { get; } =
        protectedData ?? throw new ArgumentNullException(nameof(protectedData));

    public IReadOnlyList<TransactionId> List(CancellationToken cancellationToken) =>
        appStorage.ListTransactionIds(cancellationToken);

    public AuthenticatedTransactionStore Open(
        TransactionId transactionId,
        CancellationToken cancellationToken)
    {
        var storage = appStorage.OpenTransactionStorage(transactionId, cancellationToken);
        try
        {
            return AuthenticatedTransactionStore.Open(
                storage,
                ProtectedData,
                cancellationToken);
        }
        catch
        {
            storage.Dispose();
            throw;
        }
    }

    public AuthenticatedTransactionStore Create(
        RecoveryLocator locator,
        StoredMigrationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(plan);
        var storage = appStorage.CreateTransactionStorage(locator.TransactionId, cancellationToken);
        try
        {
            return AuthenticatedTransactionStore.Bootstrap(
                storage,
                ProtectedData,
                locator,
                plan,
                cancellationToken);
        }
        catch
        {
            storage.Dispose();
            throw;
        }
    }
}
