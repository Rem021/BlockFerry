using BlockFerry.Core.System;
using Microsoft.Win32.SafeHandles;

namespace BlockFerry.Core.Transactions;

public sealed partial class AppStorageGuard
{
    private const string TransactionsDirectoryName = "transactions";
    private const int MaximumTransactionDirectoryEntries = 100_000;
    private static readonly NormalizedRelativePath TransactionsRelativePath =
        CreateNormalizedPath(TransactionsDirectoryName);

    internal IReadOnlyList<TransactionId> ListTransactionIds(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            using var serialization = AcquireStorageMutex(cancellationToken);
            using var liveAppRoot = ValidateLiveStorage(TransactionsRelativePath, cancellationToken);
            using var transactions = OpenRelative(
                liveAppRoot,
                TransactionsDirectoryName,
                RelativeObjectKind.Directory,
                AppDirectoryAccess,
                FileShareRead | FileShareWrite | FileShareDelete,
                FileOpen,
                0,
                IntPtr.Zero,
                allowMissing: true,
                out var missing,
                out _);
            if (missing || transactions is null)
            {
                return Array.Empty<TransactionId>();
            }

            ValidateRestrictedDacl(transactions);
            var names = WindowsFileSystemCapability.EnumerateChildNames(
                transactions,
                MaximumTransactionDirectoryEntries,
                cancellationToken);
            var result = new List<TransactionId>(names.Count);
            foreach (var name in names.Order(StringComparer.Ordinal))
            {
                if (!Guid.TryParseExact(name, "N", out var value) || value == Guid.Empty)
                {
                    throw new TransactionAuthenticationException(
                        "The protected transaction directory contained an invalid entry.");
                }

                result.Add(new TransactionId(value));
            }

            return Array.AsReadOnly(result.ToArray());
        }
    }

    internal ITransactionStorageDirectory CreateTransactionStorage(
        TransactionId transactionId,
        CancellationToken cancellationToken)
    {
        TransactionValueValidation.RequireId(transactionId);
        lock (gate)
        {
            using var serialization = AcquireStorageMutex(cancellationToken);
            using var liveAppRoot = ValidateLiveStorage(TransactionsRelativePath, cancellationToken);
            using var securityDescriptor = CreateRestrictedSecurityDescriptor(currentUserSid);
            var transactions = OpenRelative(
                liveAppRoot,
                TransactionsDirectoryName,
                RelativeObjectKind.Directory,
                AppDirectoryAccess | Delete,
                FileShareRead | FileShareWrite | FileShareDelete,
                FileOpenIf,
                0,
                securityDescriptor.Pointer,
                allowMissing: false,
                out _,
                out var transactionsCreated) ??
                throw new IOException("The authenticated transaction parent could not be created.");
            try
            {
                ValidateRestrictedDacl(transactions);
                if (transactionsCreated)
                {
                    Flush(liveAppRoot);
                    Record("Create", "transactions-root", isMutation: true, wasCommitted: true);
                }

                var transactionName = transactionId.Value.ToString("N");
                var transaction = OpenRelative(
                    transactions,
                    transactionName,
                    RelativeObjectKind.Directory,
                    AppDirectoryAccess | Delete,
                    FileShareRead | FileShareWrite | FileShareDelete,
                    FileCreate,
                    0,
                    securityDescriptor.Pointer,
                    allowMissing: false,
                    out _,
                    out var transactionCreated) ??
                    throw new IOException("The authenticated transaction directory could not be created.");
                try
                {
                    if (!transactionCreated)
                    {
                        throw new IOException("The transaction directory already exists.");
                    }

                    ValidateRestrictedDacl(transaction);
                    Flush(transactions);
                    Record("Create", "transaction-root", isMutation: true, wasCommitted: false);
                    return new NativeTransactionStorageDirectory(
                        this,
                        transactionId,
                        Duplicate(transactions),
                        ReadDirectoryIdentity(transactions),
                        ReadFinalPath(transactions),
                        Duplicate(transaction),
                        ReadDirectoryIdentity(transaction),
                        ReadFinalPath(transaction));
                }
                finally
                {
                    transaction.Dispose();
                }
            }
            finally
            {
                transactions.Dispose();
            }
        }
    }

    internal ITransactionStorageDirectory OpenTransactionStorage(
        TransactionId transactionId,
        CancellationToken cancellationToken)
    {
        TransactionValueValidation.RequireId(transactionId);
        lock (gate)
        {
            using var serialization = AcquireStorageMutex(cancellationToken);
            using var liveAppRoot = ValidateLiveStorage(TransactionsRelativePath, cancellationToken);
            using var transactions = OpenRelative(
                liveAppRoot,
                TransactionsDirectoryName,
                RelativeObjectKind.Directory,
                AppDirectoryAccess | Delete,
                FileShareRead | FileShareWrite | FileShareDelete,
                FileOpen,
                0,
                IntPtr.Zero,
                allowMissing: false,
                out _,
                out _) ?? throw new IOException("The authenticated transaction parent was unavailable.");
            ValidateRestrictedDacl(transactions);
            using var transaction = OpenRelative(
                transactions,
                transactionId.Value.ToString("N"),
                RelativeObjectKind.Directory,
                AppDirectoryAccess | Delete,
                FileShareRead | FileShareWrite | FileShareDelete,
                FileOpen,
                0,
                IntPtr.Zero,
                allowMissing: false,
                out _,
                out _) ?? throw new IOException("The authenticated transaction directory was unavailable.");
            ValidateRestrictedDacl(transaction);
            return new NativeTransactionStorageDirectory(
                this,
                transactionId,
                Duplicate(transactions),
                ReadDirectoryIdentity(transactions),
                ReadFinalPath(transactions),
                Duplicate(transaction),
                ReadDirectoryIdentity(transaction),
                ReadFinalPath(transaction));
        }
    }

    private sealed class NativeTransactionStorageDirectory : ITransactionStorageDirectory
    {
        private const uint TransactionFileAccess =
            GenericRead |
            GenericWrite |
            Delete |
            FileReadAttributes |
            FileWriteAttributes |
            ReadControl |
            WriteDac |
            Synchronize;
        private readonly AppStorageGuard owner;
        private readonly SafeFileHandle transactionsRoot;
        private readonly PhysicalDirectoryIdentity transactionsIdentity;
        private readonly string transactionsFinalPath;
        private readonly SafeFileHandle transactionRoot;
        private readonly PhysicalDirectoryIdentity transactionIdentity;
        private readonly string transactionFinalPath;
        private bool disposed;

        internal NativeTransactionStorageDirectory(
            AppStorageGuard owner,
            TransactionId transactionId,
            SafeFileHandle transactionsRoot,
            PhysicalDirectoryIdentity transactionsIdentity,
            string transactionsFinalPath,
            SafeFileHandle transactionRoot,
            PhysicalDirectoryIdentity transactionIdentity,
            string transactionFinalPath)
        {
            this.owner = owner;
            TransactionId = transactionId;
            this.transactionsRoot = transactionsRoot;
            this.transactionsIdentity = transactionsIdentity;
            this.transactionsFinalPath = transactionsFinalPath;
            this.transactionRoot = transactionRoot;
            this.transactionIdentity = transactionIdentity;
            this.transactionFinalPath = transactionFinalPath;
        }

        public TransactionId TransactionId { get; }

        public long GetAvailableBytes(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!GetDiskFreeSpaceEx(
                    transactionFinalPath,
                    out var available,
                    out _,
                    out _))
            {
                throw NativeFailure("The transaction-storage free-space budget could not be read.");
            }

            return available > long.MaxValue ? long.MaxValue : checked((long)available);
        }

        public IReadOnlyList<string> ListNames(CancellationToken cancellationToken) =>
            WithValidatedRoot(
                root => WindowsFileSystemCapability.EnumerateChildNames(
                    root,
                    MaximumTransactionDirectoryEntries,
                    cancellationToken),
                cancellationToken);

        public void CreateDirectory(string opaqueName, CancellationToken cancellationToken)
        {
            RequireLeafName(opaqueName);
            WithValidatedRoot(root =>
            {
                using var securityDescriptor = CreateRestrictedSecurityDescriptor(owner.currentUserSid);
                using var created = OpenRelative(
                    root,
                    opaqueName,
                    RelativeObjectKind.Directory,
                    AppDirectoryAccess | Delete,
                    FileShareRead | FileShareWrite | FileShareDelete,
                    FileCreate,
                    0,
                    securityDescriptor.Pointer,
                    allowMissing: false,
                    out _,
                    out var wasCreated) ?? throw new IOException("A transaction subdirectory could not be created.");
                if (!wasCreated)
                {
                    throw new IOException("A transaction subdirectory already exists.");
                }

                owner.ValidateRestrictedDacl(created);
                Flush(root);
                owner.Record("Create", "transaction-directory", isMutation: true, wasCommitted: false);
            }, cancellationToken);
        }

        public void CreateNewFile(
            string opaqueName,
            ReadOnlySpan<byte> bytes,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            RequireLeafName(opaqueName);
            ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
            if (bytes.Length > maximumBytes)
            {
                throw new IOException("The transaction file exceeded its fixed bound.");
            }

            var retainedBytes = bytes.ToArray();
            try
            {
                WithValidatedRoot(root =>
                {
                    using var securityDescriptor = CreateRestrictedSecurityDescriptor(owner.currentUserSid);
                    using var file = OpenRelative(
                        root,
                        opaqueName,
                        RelativeObjectKind.File,
                        TransactionFileAccess,
                        FileShareRead,
                        FileCreate,
                        FileWriteThrough,
                        securityDescriptor.Pointer,
                        allowMissing: false,
                        out _,
                        out var wasCreated) ?? throw new IOException("A transaction file could not be created.");
                    if (!wasCreated)
                    {
                        throw new IOException("A transaction file already exists.");
                    }

                    owner.ValidateRestrictedDacl(file);
                    RandomAccess.Write(file, retainedBytes, 0);
                    RandomAccess.FlushToDisk(file);
                    Flush(root);
                    owner.Record("Create", "transaction-file", isMutation: true, wasCommitted: false);
                }, cancellationToken);
            }
            finally
            {
                global::System.Security.Cryptography.CryptographicOperations.ZeroMemory(retainedBytes);
            }
        }

        public void AppendAndFlush(
            string opaqueName,
            ReadOnlySpan<byte> bytes,
            int maximumTotalBytes,
            CancellationToken cancellationToken)
        {
            RequireLeafName(opaqueName);
            ArgumentOutOfRangeException.ThrowIfNegative(maximumTotalBytes);
            var retainedBytes = bytes.ToArray();
            try
            {
                WithValidatedRoot(root =>
                {
                    using var file = OpenRelative(
                        root,
                        opaqueName,
                        RelativeObjectKind.File,
                        TransactionFileAccess,
                        FileShareRead,
                        FileOpen,
                        FileWriteThrough,
                        IntPtr.Zero,
                        allowMissing: false,
                        out _,
                        out _) ?? throw new IOException("The transaction log file was unavailable.");
                    owner.ValidateRestrictedDacl(file);
                    var length = RandomAccess.GetLength(file);
                    if (length < 0 || retainedBytes.Length > maximumTotalBytes - length)
                    {
                        throw new IOException("The transaction log exceeded its fixed bound.");
                    }

                    RandomAccess.Write(file, retainedBytes, length);
                    RandomAccess.FlushToDisk(file);
                    Flush(root);
                    owner.Record("Append", "transaction-log", isMutation: true, wasCommitted: false);
                }, cancellationToken);
            }
            finally
            {
                global::System.Security.Cryptography.CryptographicOperations.ZeroMemory(retainedBytes);
            }
        }

        public byte[] ReadFile(
            string opaqueName,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            RequireLeafName(opaqueName);
            ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
            return WithValidatedRoot(root =>
            {
                using var file = OpenRelative(
                    root,
                    opaqueName,
                    RelativeObjectKind.File,
                    GenericRead | FileReadAttributes | ReadControl | Synchronize,
                    FileShareRead,
                    FileOpen,
                    0,
                    IntPtr.Zero,
                    allowMissing: false,
                    out _,
                    out _) ?? throw new IOException("The transaction file was unavailable.");
                owner.ValidateRestrictedDacl(file);
                return ReadBounded(file, maximumBytes, cancellationToken);
            }, cancellationToken);
        }

        public void CreateNewFileInDirectory(
            string directoryName,
            string opaqueName,
            ReadOnlySpan<byte> bytes,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            RequireLeafName(directoryName);
            RequireLeafName(opaqueName);
            ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
            if (bytes.Length > maximumBytes)
            {
                throw new IOException("The scoped transaction file exceeded its fixed bound.");
            }

            var retainedBytes = bytes.ToArray();
            try
            {
                WithValidatedRoot(root =>
                {
                    using var directory = OpenRelative(
                        root,
                        directoryName,
                        RelativeObjectKind.Directory,
                        AppDirectoryAccess | Delete,
                        FileShareRead | FileShareWrite | FileShareDelete,
                        FileOpen,
                        0,
                        IntPtr.Zero,
                        allowMissing: false,
                        out _,
                        out _) ?? throw new IOException("The scoped transaction directory was unavailable.");
                    owner.ValidateRestrictedDacl(directory);
                    using var securityDescriptor = CreateRestrictedSecurityDescriptor(owner.currentUserSid);
                    using var file = OpenRelative(
                        directory,
                        opaqueName,
                        RelativeObjectKind.File,
                        TransactionFileAccess,
                        FileShareRead,
                        FileCreate,
                        FileWriteThrough,
                        securityDescriptor.Pointer,
                        allowMissing: false,
                        out _,
                        out var wasCreated) ?? throw new IOException("A scoped transaction file could not be created.");
                    if (!wasCreated)
                    {
                        throw new IOException("A scoped transaction file already exists.");
                    }

                    owner.ValidateRestrictedDacl(file);
                    RandomAccess.Write(file, retainedBytes, 0);
                    RandomAccess.FlushToDisk(file);
                    Flush(directory);
                    owner.Record("Create", "transaction-scoped-file", isMutation: true, wasCommitted: false);
                }, cancellationToken);
            }
            finally
            {
                global::System.Security.Cryptography.CryptographicOperations.ZeroMemory(retainedBytes);
            }
        }

        public byte[] ReadFileInDirectory(
            string directoryName,
            string opaqueName,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            RequireLeafName(directoryName);
            RequireLeafName(opaqueName);
            ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
            return WithValidatedRoot(root =>
            {
                using var directory = OpenRelative(
                    root,
                    directoryName,
                    RelativeObjectKind.Directory,
                    AppDirectoryAccess | Delete,
                    FileShareRead | FileShareWrite | FileShareDelete,
                    FileOpen,
                    0,
                    IntPtr.Zero,
                    allowMissing: false,
                    out _,
                    out _) ?? throw new IOException("The scoped transaction directory was unavailable.");
                owner.ValidateRestrictedDacl(directory);
                using var file = OpenRelative(
                    directory,
                    opaqueName,
                    RelativeObjectKind.File,
                    GenericRead | FileReadAttributes | ReadControl | Synchronize,
                    FileShareRead,
                    FileOpen,
                    0,
                    IntPtr.Zero,
                    allowMissing: false,
                    out _,
                    out _) ?? throw new IOException("The scoped transaction file was unavailable.");
                owner.ValidateRestrictedDacl(file);
                return ReadBounded(file, maximumBytes, cancellationToken);
            }, cancellationToken);
        }

        public void DeleteBootstrapArtifacts(CancellationToken cancellationToken)
        {
            WithValidatedRoot(root =>
            {
                var names = WindowsFileSystemCapability.EnumerateChildNames(
                    root,
                    16,
                    cancellationToken);
                var allowed = new HashSet<string>(
                    [
                        "key.dpapi",
                        "recovery-locator.dpapi",
                        "plan.dpapi",
                        "journal.log",
                        "manifest.log",
                        "before",
                    ],
                    StringComparer.Ordinal);
                if (names.Any(name => !allowed.Contains(name)))
                {
                    throw new IOException("Fresh transaction cleanup found an unexpected artifact.");
                }

                foreach (var name in names.Where(name => name != "before"))
                {
                    using var file = OpenRelative(
                        root,
                        name,
                        RelativeObjectKind.File,
                        Delete | FileReadAttributes | ReadControl | Synchronize,
                        FileShareRead | FileShareWrite | FileShareDelete,
                        FileOpen,
                        0,
                        IntPtr.Zero,
                        allowMissing: false,
                        out _,
                        out _) ?? throw new IOException("A bootstrap artifact could not be reopened for cleanup.");
                    owner.ValidateRestrictedDacl(file);
                    owner.SetDeleteDisposition(file, "transaction-bootstrap-file");
                }

                if (names.Contains("before", StringComparer.Ordinal))
                {
                    using var before = OpenRelative(
                        root,
                        "before",
                        RelativeObjectKind.Directory,
                        AppDirectoryAccess | Delete,
                        FileShareRead | FileShareWrite | FileShareDelete,
                        FileOpen,
                        0,
                        IntPtr.Zero,
                        allowMissing: false,
                        out _,
                        out _) ?? throw new IOException("The bootstrap backup directory could not be reopened.");
                    owner.ValidateRestrictedDacl(before);
                    if (WindowsFileSystemCapability.EnumerateChildNames(before, 1, cancellationToken).Count != 0)
                    {
                        throw new IOException("The fresh bootstrap backup directory was unexpectedly non-empty.");
                    }

                    owner.SetDeleteDisposition(before, "transaction-bootstrap-before");
                }

                Flush(root);
                owner.SetDeleteDisposition(root, "transaction-bootstrap-root");
                Flush(transactionsRoot);
            }, cancellationToken);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            transactionRoot.Dispose();
            transactionsRoot.Dispose();
        }

        private TResult WithValidatedRoot<TResult>(
            Func<SafeFileHandle, TResult> action,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(action);
            lock (owner.gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                using var serialization = owner.AcquireStorageMutex(cancellationToken);
                using var appRoot = owner.ValidateLiveStorage(TransactionsRelativePath, cancellationToken);
                ValidateRetainedDirectory(
                    appRoot,
                    TransactionsDirectoryName,
                    transactionsRoot,
                    transactionsIdentity,
                    transactionsFinalPath);
                ValidateRetainedDirectory(
                    transactionsRoot,
                    TransactionId.Value.ToString("N"),
                    transactionRoot,
                    transactionIdentity,
                    transactionFinalPath);
                return action(transactionRoot);
            }
        }

        private void WithValidatedRoot(
            Action<SafeFileHandle> action,
            CancellationToken cancellationToken) =>
            WithValidatedRoot(root =>
            {
                action(root);
                return true;
            }, cancellationToken);

        private void ValidateRetainedDirectory(
            SafeFileHandle parent,
            string name,
            SafeFileHandle retained,
            PhysicalDirectoryIdentity expectedIdentity,
            string expectedFinalPath)
        {
            ValidateObject(retained, RelativeObjectKind.Directory);
            owner.ValidateRestrictedDacl(retained);
            if (ReadDirectoryIdentity(retained) != expectedIdentity ||
                !SameFinalPath(ReadFinalPath(retained), expectedFinalPath))
            {
                throw new IOException("An authenticated transaction directory changed identity.");
            }

            using var reopened = OpenRelative(
                parent,
                name,
                RelativeObjectKind.Directory,
                AppDirectoryAccess | Delete,
                FileShareRead | FileShareWrite | FileShareDelete,
                FileOpen,
                0,
                IntPtr.Zero,
                allowMissing: false,
                out _,
                out _) ?? throw new IOException("An authenticated transaction directory name no longer resolved.");
            owner.ValidateRestrictedDacl(reopened);
            if (ReadDirectoryIdentity(reopened) != expectedIdentity ||
                !SameFinalPath(ReadFinalPath(reopened), expectedFinalPath))
            {
                throw new IOException("An authenticated transaction directory name resolved to another object.");
            }
        }

        private static void RequireLeafName(string name)
        {
            if (!NormalizedRelativePath.TryCreate(name, out var path, out _) ||
                path is null ||
                path.Segments.Count != 1 ||
                !string.Equals(name, path.Value, StringComparison.Ordinal))
            {
                throw new ArgumentException("A single normalized opaque storage name is required.", nameof(name));
            }
        }
    }

#pragma warning disable SYSLIB1054
    [global::System.Runtime.InteropServices.DllImport(
        "kernel32.dll",
        EntryPoint = "GetDiskFreeSpaceExW",
        SetLastError = true,
        CharSet = global::System.Runtime.InteropServices.CharSet.Unicode,
        ExactSpelling = true)]
    [return: global::System.Runtime.InteropServices.MarshalAs(
        global::System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);
#pragma warning restore SYSLIB1054
}
