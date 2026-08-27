using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using BlockFerry.Core.System;
using Microsoft.Win32.SafeHandles;

namespace BlockFerry.Core.Transactions;

public enum AppStorageDiagnosticCode
{
    Unavailable,
    VolumeRejected,
    ReparseRejected,
    IdentityDrift,
    DaclRejected,
    IoFailure,
}

public sealed record AppStorageDiagnostic(
    AppStorageDiagnosticCode Code,
    string Message);

public sealed record AppStorageAuditEvent(
    string Operation,
    string OpaqueObject,
    bool IsMutation,
    bool WasCommitted);

public sealed partial class AppStorageGuard : IDisposable
{
    private const string AppRootName = "BlockFerry";
    private const string RememberedRootsName = "discovery-roots.json";
    private const int MaximumRecoveryEntries = 32;
    private const int MaximumRecoveryManifestCiphertextBytes = 4096;
    private const int MaximumRecoveryManifestPlaintextBytes = 1024;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileAddFile = 0x00000002;
    private const uint FileAddSubdirectory = 0x00000004;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileWriteAttributes = 0x00000100;
    private const uint FileTraverse = 0x00000020;
    private const uint FileDeleteChild = 0x00000040;
    private const uint Delete = 0x00010000;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint Synchronize = 0x00100000;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint MaximumAllowed = 0x02000000;
    private const uint FileAllAccess = 0x001F01FF;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint ObjectCaseInsensitive = 0x00000040;
    private const uint FileOpen = 1;
    private const uint FileCreate = 2;
    private const uint FileOpenIf = 3;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint FileWriteThrough = 0x00000002;
    private const uint FileDispositionFlagDelete = 0x00000001;
    private const uint MutexAllAccess = 0x001F0001;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitAbandoned = 0x00000080;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;
    private const ushort SecurityDescriptorDaclProtected = 0x1000;
    private const byte AccessAllowedAceType = 0;
    private const int StatusSuccess = 0;
    private const int StatusNoSuchFile = unchecked((int)0xC000000F);
    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
    private const int StatusObjectPathNotFound = unchecked((int)0xC000003A);
    private readonly object gate = new();
    private readonly IFileSystemCapability fileSystem;
    private readonly WindowsCurrentUserProtectedData recoveryProtectedData;
    private readonly string? localAppDataPath;
    private readonly string currentUserSid;
    private readonly IAppStorageInterleaving interleaving;
    private readonly List<AppStorageAuditEvent> auditLog = [];
    private IVerifiedDirectoryHandle? capabilityRoot;
    private SafeFileHandle? nativeLocalRoot;
    private SafeFileHandle? appRoot;
    private AppStorageSynchronization? storageMutex;
    private PhysicalDirectoryIdentity appRootIdentity;
    private string? appRootFinalPath;
    private bool disposed;
    private static readonly NormalizedRelativePath RememberedRootsRelativePath =
        CreateNormalizedPath(RememberedRootsName);

    public AppStorageGuard(
        IEnvironmentPaths environment,
        IFileSystemCapability fileSystem,
        CancellationToken cancellationToken = default)
        : this(environment, fileSystem, NullAppStorageInterleaving.Instance, cancellationToken)
    {
    }

    internal AppStorageGuard(
        IEnvironmentPaths environment,
        IFileSystemCapability fileSystem,
        IAppStorageInterleaving interleaving,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(fileSystem);
        this.fileSystem = fileSystem;
        recoveryProtectedData = new WindowsCurrentUserProtectedData();
        this.interleaving = interleaving ?? throw new ArgumentNullException(nameof(interleaving));
        localAppDataPath = environment.LocalAppData;
        currentUserSid = string.Empty;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Guarded app storage requires Windows handle APIs.");
            }

            currentUserSid = ReadCurrentUserSid();
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(localAppDataPath))
            {
                throw new StorageProofException(
                    AppStorageDiagnosticCode.Unavailable,
                    "The injected LocalAppData root was unavailable.");
            }

            capabilityRoot = fileSystem.OpenRoot(
                localAppDataPath,
                FileSystemOpenPurpose.AppStorage,
                cancellationToken);
            var volume = fileSystem.InspectVolume(capabilityRoot, cancellationToken);
            if (!volume.IsLocalVolume ||
                volume.IsNetworkRedirected ||
                !volume.SupportsPersistentAcls)
            {
                throw new StorageProofException(
                    AppStorageDiagnosticCode.VolumeRejected,
                    "Guarded app storage requires a local volume with persistent ACL support.");
            }

            nativeLocalRoot = OpenAbsoluteDirectory(localAppDataPath, cancellationToken);
            if (ReadDirectoryIdentity(nativeLocalRoot) != capabilityRoot.Identity ||
                !SameFinalPath(ReadFinalPath(nativeLocalRoot), capabilityRoot.FinalPath))
            {
                throw new StorageProofException(
                    AppStorageDiagnosticCode.IdentityDrift,
                    "The LocalAppData capability identity could not be retained.");
            }

            storageMutex = CreateStorageMutex(currentUserSid, capabilityRoot.Identity);
            using var serialization = AcquireStorageMutex(cancellationToken);
            using var securityDescriptor = CreateRestrictedSecurityDescriptor(currentUserSid);
            appRoot = OpenRelative(
                nativeLocalRoot,
                AppRootName,
                RelativeObjectKind.Directory,
                AppDirectoryAccess,
                FileShareRead | FileShareWrite | FileShareDelete,
                FileOpenIf,
                0,
                securityDescriptor.Pointer,
                allowMissing: false,
                out _,
                out var created) ??
                throw new StorageProofException(
                    AppStorageDiagnosticCode.IoFailure,
                    "The guarded app-storage directory could not be opened.");
            if (created)
            {
                Record("Create", "app-root", isMutation: true, wasCommitted: true);
            }

            if (created)
            {
                ValidateRestrictedDacl(appRoot);
            }
            else
            {
                EnsureRestrictedAppRootDacl(nativeLocalRoot, appRoot);
            }
            appRootIdentity = ReadDirectoryIdentity(appRoot);
            appRootFinalPath = ReadFinalPath(appRoot);
            IsAvailable = true;
            using var recoveryRoot = ValidateLiveStorage(
                RememberedRootsRelativePath,
                CancellationToken.None);
            var startupRecovery = RecoverStorageState(
                recoveryRoot,
                RememberedRootsRelativePath,
                serialization.WasAbandoned,
                CancellationToken.None);
            if (startupRecovery is AppStorageRecoveryState.Clean or
                AppStorageRecoveryState.Committed or
                AppStorageRecoveryState.RolledBack)
            {
                HardenKnownLegacyPreferenceLeaf(appRoot, "theme.txt");
                HardenKnownLegacyPreferenceLeaf(appRoot, RememberedRootsName);
            }
        }
        catch (OperationCanceledException)
        {
            Dispose();
            throw;
        }
        catch (StorageProofException exception)
        {
            SetUnavailable(exception.Code, exception.SafeMessage);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
        {
            SetUnavailable(
                AppStorageDiagnosticCode.IoFailure,
                "Guarded app storage could not be established through retained Windows handles.");
        }
    }

    public bool IsAvailable { get; private set; }
    public AppStorageDiagnostic? LastDiagnostic { get; private set; }

    public IReadOnlyList<AppStorageAuditEvent> AuditLog
    {
        get
        {
            lock (gate)
            {
                return Array.AsReadOnly(auditLog.ToArray());
            }
        }
    }

    internal AppStorageReadResult TryRead(
        NormalizedRelativePath relativePath,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);

        using var serialization = AcquireStorageMutex(cancellationToken);
        lock (gate)
        {
            try
            {
                using var liveRoot = ValidateLiveStorage(relativePath, cancellationToken);
                if (RecoverStorageState(
                        liveRoot,
                        relativePath,
                        serialization.WasAbandoned,
                        cancellationToken) == AppStorageRecoveryState.RecoveryRequired)
                {
                    return new AppStorageReadResult(AppStorageReadState.RecoveryRequired);
                }

                using var file = OpenLeaf(
                    liveRoot,
                    relativePath,
                    GenericRead | FileReadAttributes | ReadControl | Synchronize,
                    FileShareRead,
                    FileOpen,
                    0,
                    IntPtr.Zero,
                    allowMissing: true,
                    out var missing,
                    out _);
                if (missing)
                {
                    Record("ReadMissing", "remembered-roots", isMutation: false, wasCommitted: false);
                    return new AppStorageReadResult(AppStorageReadState.Missing);
                }

                EnsureRestrictedLeafDaclOrHardenOwnedLegacy(
                    liveRoot,
                    relativePath,
                    file!,
                    "preference-leaf");
                var length = RandomAccess.GetLength(file!);
                if (length < 0 || length > maximumBytes || length > int.MaxValue)
                {
                    Record("ReadLimit", "remembered-roots", isMutation: false, wasCommitted: false);
                    return new AppStorageReadResult(AppStorageReadState.LimitExceeded);
                }

                var bytes = ReadBounded(file!, maximumBytes, cancellationToken);
                Record("Read", "remembered-roots", isMutation: false, wasCommitted: false);
                return new AppStorageReadResult(AppStorageReadState.Read, bytes);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (StorageProofException exception)
            {
                SetUnavailable(exception.Code, exception.SafeMessage);
                return new AppStorageReadResult(AppStorageReadState.Unavailable);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or Win32Exception)
            {
                SetUnavailable(
                    AppStorageDiagnosticCode.IoFailure,
                    "The guarded app-storage payload could not be read safely.");
                return new AppStorageReadResult(AppStorageReadState.Unavailable);
            }
        }
    }

    internal AppStorageMutationResult TryAtomicReplace(
        NormalizedRelativePath relativePath,
        ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken) =>
        TryAtomicReplace(
            relativePath,
            bytes,
            NullAppStoragePrecommitAuthority.Instance,
            cancellationToken);

    internal AppStorageMutationResult TryAtomicReplace(
        NormalizedRelativePath relativePath,
        ReadOnlySpan<byte> bytes,
        IAppStoragePrecommitAuthority precommitAuthority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(precommitAuthority);
        using var serialization = AcquireStorageMutex(cancellationToken);
        lock (gate)
        {
            SafeFileHandle? staged = null;
            SafeFileHandle? target = null;
            SafeFileHandle? recoveryManifestHandle = null;
            IDisposable? authorityLease = null;
            byte[]? stagedHash = null;
            byte[]? oldHash = null;
            string? temporaryName = null;
            string? oldTombstoneName = null;
            string? recoveryManifestName = null;
            PhysicalFileIdentity? beforeIdentity = null;
            long oldLength = 0;
            var targetWasMissing = true;
            var targetDisplaced = false;
            var committed = false;
            var finalPayloadVerified = false;
            var committedVerified = false;
            try
            {
                using var liveRoot = ValidateLiveStorage(relativePath, cancellationToken);
                if (RecoverStorageState(
                        liveRoot,
                        relativePath,
                        serialization.WasAbandoned,
                        cancellationToken) == AppStorageRecoveryState.RecoveryRequired)
                {
                    return AppStorageMutationResult.RecoveryRequired(LastDiagnostic!);
                }

                target = OpenLeaf(
                    liveRoot,
                    relativePath,
                    GenericRead | GenericWrite | Delete | FileReadAttributes | ReadControl | Synchronize,
                    FileShareRead,
                    FileOpen,
                    0,
                    IntPtr.Zero,
                    allowMissing: true,
                    out targetWasMissing,
                    out _);
                if (!targetWasMissing)
                {
                    EnsureRestrictedLeafDaclOrHardenOwnedLegacy(
                        liveRoot,
                        relativePath,
                        target!,
                        "preference-leaf");
                    beforeIdentity = ReadFileIdentity(target!);
                    Flush(target!);
                    (oldLength, oldHash) = CreateContentProof(target!, cancellationToken);
                    interleaving.Reach(
                        AppStorageInterleavingPoint.TargetRetainedDurable,
                        new AppStorageInterleavingContext(relativePath.Segments[0]));
                }

                cancellationToken.ThrowIfCancellationRequested();

                var transactionId = Guid.NewGuid();
                temporaryName = RecoveryName(transactionId, ".tmp");
                oldTombstoneName = targetWasMissing
                    ? null
                    : RecoveryName(transactionId, ".old");
                recoveryManifestName = RecoveryName(transactionId, ".txn");
                using var securityDescriptor = CreateRestrictedSecurityDescriptor(currentUserSid);
                staged = OpenRelative(
                    liveRoot,
                    temporaryName,
                    RelativeObjectKind.File,
                    GenericRead | GenericWrite | Delete | FileReadAttributes | ReadControl | Synchronize,
                    FileShareRead,
                    FileCreate,
                    FileWriteThrough,
                    securityDescriptor.Pointer,
                    allowMissing: false,
                    out _,
                    out var created);
                if (staged is null || !created)
                {
                    throw new StorageProofException(
                        AppStorageDiagnosticCode.IoFailure,
                        "A unique sibling staging file could not be created.");
                }

                Record("StageCreate", "sibling-temp", isMutation: true, wasCommitted: false);
                interleaving.Reach(
                    AppStorageInterleavingPoint.StageCreated,
                    new AppStorageInterleavingContext(temporaryName));
                ValidateRestrictedDacl(staged);
                RandomAccess.Write(staged, bytes, 0);
                RandomAccess.FlushToDisk(staged);
                interleaving.Reach(
                    AppStorageInterleavingPoint.StageDurable,
                    new AppStorageInterleavingContext(temporaryName));
                var stagedBytes = ReadBounded(staged, bytes.Length, cancellationToken);
                if (!stagedBytes.AsSpan().SequenceEqual(bytes))
                {
                    throw new StorageProofException(
                        AppStorageDiagnosticCode.IoFailure,
                        "The staged app-storage payload failed byte verification.");
                }

                var stagedIdentity = ReadFileIdentity(staged);
                stagedHash = SHA256.HashData(bytes);
                var recoveryManifest = new AppStorageRecoveryManifest(
                    transactionId,
                    targetWasMissing
                        ? AppStorageRecoveryKind.ReplaceMissing
                        : AppStorageRecoveryKind.ReplaceExisting,
                    appRootIdentity,
                    stagedIdentity,
                    bytes.Length,
                    stagedHash,
                    beforeIdentity,
                    oldLength,
                    oldHash);
                recoveryManifestHandle = CreateRecoveryManifest(
                    liveRoot,
                    recoveryManifestName,
                    recoveryManifest);
                interleaving.Reach(
                    AppStorageInterleavingPoint.RecoveryManifestDurable,
                    new AppStorageInterleavingContext(recoveryManifestName));

                cancellationToken.ThrowIfCancellationRequested();
                using var revalidatedRoot = ValidateLiveStorage(relativePath, cancellationToken);
                if (target is not null &&
                    (ReadFileIdentity(target) != beforeIdentity ||
                     !FinalPathEndsWith(target, relativePath.Segments[0])))
                {
                    throw new StorageProofException(
                        AppStorageDiagnosticCode.IdentityDrift,
                        "The guarded payload identity changed before atomic replacement.");
                }

                authorityLease = precommitAuthority.Revalidate(cancellationToken) ??
                    throw new AppStoragePrecommitAuthorityException(
                        "The remembered-root precommit authority returned no retained proof lease.");

                interleaving.Reach(
                    AppStorageInterleavingPoint.BeforeCommitRename,
                    new AppStorageInterleavingContext(relativePath.Segments[0]));
                cancellationToken.ThrowIfCancellationRequested();
                if (target is not null)
                {
                    RenameRelative(target, revalidatedRoot, oldTombstoneName!, 0);
                    targetDisplaced = true;
                    Record("TargetTombstoneRename", "old-tombstone", isMutation: true, wasCommitted: false);
                    interleaving.Reach(
                        AppStorageInterleavingPoint.AfterTargetTombstoneRename,
                        new AppStorageInterleavingContext(oldTombstoneName!));
                    Flush(revalidatedRoot);
                    interleaving.Reach(
                        AppStorageInterleavingPoint.DirectoryDurableAfterTargetTombstone,
                        new AppStorageInterleavingContext(oldTombstoneName!));
                }

                RenameRelative(staged, revalidatedRoot, relativePath.Segments[0], 0);
                committed = true;
                Record("AtomicReplace", "remembered-roots", isMutation: true, wasCommitted: true);
                interleaving.Reach(
                    AppStorageInterleavingPoint.AfterCommitRename,
                    new AppStorageInterleavingContext(relativePath.Segments[0]));
                Flush(revalidatedRoot);
                interleaving.Reach(
                    AppStorageInterleavingPoint.DirectoryDurableAfterCommit,
                    new AppStorageInterleavingContext(relativePath.Segments[0]));

                if (ReadFileIdentity(staged) != stagedIdentity ||
                    !FinalPathEndsWith(staged, relativePath.Segments[0]))
                {
                    throw new StorageProofException(
                        AppStorageDiagnosticCode.IdentityDrift,
                        "The retained staged handle did not become the final app-storage name.");
                }

                ValidateRestrictedDacl(staged);
                Flush(staged);
                var finalBytes = ReadBounded(staged, bytes.Length, CancellationToken.None);
                if (!finalBytes.AsSpan().SequenceEqual(bytes))
                {
                    throw new StorageProofException(
                        AppStorageDiagnosticCode.IoFailure,
                        "The final app-storage payload failed byte verification.");
                }

                interleaving.Reach(
                    AppStorageInterleavingPoint.FinalDurable,
                    new AppStorageInterleavingContext(relativePath.Segments[0]));
                finalPayloadVerified = true;
                if (target is not null)
                {
                    SetDeleteDisposition(target, "old-tombstone");
                    target.Dispose();
                    target = null;
                    VerifyDeletion(revalidatedRoot, oldTombstoneName!, "old-tombstone");
                }

                DeleteRecoveryManifest(
                    revalidatedRoot,
                    recoveryManifestHandle!,
                    recoveryManifestName!);
                recoveryManifestHandle = null;
                committedVerified = true;
                if (oldTombstoneName is not null)
                {
                    interleaving.Reach(
                        AppStorageInterleavingPoint.OldTombstoneDeleted,
                        new AppStorageInterleavingContext(oldTombstoneName));
                }

                return AppStorageMutationResult.CommittedVerified();
            }
            catch (OperationCanceledException)
            {
                if (committedVerified)
                {
                    return AppStorageMutationResult.CommittedVerified();
                }

                if (finalPayloadVerified)
                {
                    return PreserveReplaceRecoveryRequired(
                        "The committed app-storage replacement requires cleanup recovery verification.");
                }

                if (recoveryManifestHandle is not null || targetDisplaced || committed)
                {
                    return RollbackReplace(
                        relativePath,
                        staged,
                        target,
                        recoveryManifestHandle,
                        recoveryManifestName,
                        temporaryName,
                        oldTombstoneName,
                        targetWasMissing,
                        beforeIdentity,
                        targetDisplaced,
                        committed);
                }

                return CleanupUncommittedStage(
                    relativePath,
                    staged,
                    temporaryName,
                    diagnostic: null);
            }
            catch (AppStorageRecoveryResidueException exception)
            {
                return PreserveReplaceRecoveryRequired(exception.SafeMessage);
            }
            catch (StorageProofException exception)
            {
                if (committedVerified)
                {
                    return AppStorageMutationResult.CommittedVerified();
                }

                var diagnostic = new AppStorageDiagnostic(exception.Code, exception.SafeMessage);
                if (finalPayloadVerified)
                {
                    return PreserveReplaceRecoveryRequired(diagnostic.Message);
                }

                if (recoveryManifestHandle is not null || targetDisplaced || committed)
                {
                    return RollbackReplace(
                        relativePath,
                        staged,
                        target,
                        recoveryManifestHandle,
                        recoveryManifestName,
                        temporaryName,
                        oldTombstoneName,
                        targetWasMissing,
                        beforeIdentity,
                        targetDisplaced,
                        committed,
                        diagnostic);
                }

                var cleanupResult = CleanupUncommittedStage(
                    relativePath,
                    staged,
                    temporaryName,
                    diagnostic);
                if (cleanupResult.State == AppStorageMutationState.NotCommitted)
                {
                    SetUnavailable(exception.Code, exception.SafeMessage);
                    return AppStorageMutationResult.NotCommitted(LastDiagnostic);
                }

                return cleanupResult;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
            {
                if (committedVerified)
                {
                    return AppStorageMutationResult.CommittedVerified();
                }

                var diagnostic = new AppStorageDiagnostic(
                    AppStorageDiagnosticCode.IoFailure,
                    "The guarded app-storage payload could not be replaced safely.");
                if (finalPayloadVerified)
                {
                    return PreserveReplaceRecoveryRequired(diagnostic.Message);
                }

                if (recoveryManifestHandle is not null || targetDisplaced || committed)
                {
                    return RollbackReplace(
                        relativePath,
                        staged,
                        target,
                        recoveryManifestHandle,
                        recoveryManifestName,
                        temporaryName,
                        oldTombstoneName,
                        targetWasMissing,
                        beforeIdentity,
                        targetDisplaced,
                        committed,
                        diagnostic);
                }

                var cleanupResult = CleanupUncommittedStage(
                    relativePath,
                    staged,
                    temporaryName,
                    diagnostic);
                if (cleanupResult.State == AppStorageMutationState.NotCommitted)
                {
                    SetUnavailable(diagnostic.Code, diagnostic.Message);
                    return AppStorageMutationResult.NotCommitted(LastDiagnostic);
                }

                return cleanupResult;
            }
            finally
            {
                staged?.Dispose();

                target?.Dispose();
                recoveryManifestHandle?.Dispose();
                authorityLease?.Dispose();
                if (stagedHash is not null)
                {
                    CryptographicOperations.ZeroMemory(stagedHash);
                }

                if (oldHash is not null)
                {
                    CryptographicOperations.ZeroMemory(oldHash);
                }
            }
        }
    }

    internal AppStorageMutationResult TryDelete(
        NormalizedRelativePath relativePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        using var serialization = AcquireStorageMutex(cancellationToken);
        lock (gate)
        {
            SafeFileHandle? file = null;
            SafeFileHandle? recoveryManifestHandle = null;
            byte[]? oldHash = null;
            string? recoveryManifestName = null;
            string? tombstoneName = null;
            var renamed = false;
            var deleteDispositionSet = false;
            var deletionVerified = false;
            var committedVerified = false;
            try
            {
                using var liveRoot = ValidateLiveStorage(relativePath, cancellationToken);
                if (RecoverStorageState(
                        liveRoot,
                        relativePath,
                        serialization.WasAbandoned,
                        cancellationToken) == AppStorageRecoveryState.RecoveryRequired)
                {
                    return AppStorageMutationResult.RecoveryRequired(LastDiagnostic!);
                }

                file = OpenLeaf(
                    liveRoot,
                    relativePath,
                    GenericRead | GenericWrite | Delete | FileReadAttributes | ReadControl | Synchronize,
                    FileShareRead,
                    FileOpen,
                    FileWriteThrough,
                    IntPtr.Zero,
                    allowMissing: true,
                    out var missing,
                    out _);
                if (missing)
                {
                    return AppStorageMutationResult.CommittedVerified();
                }

                EnsureRestrictedLeafDaclOrHardenOwnedLegacy(
                    liveRoot,
                    relativePath,
                    file!,
                    "preference-leaf");
                var identity = ReadFileIdentity(file!);
                Flush(file!);
                var (oldLength, contentHash) = CreateContentProof(file!, cancellationToken);
                oldHash = contentHash;
                var transactionId = Guid.NewGuid();
                recoveryManifestName = RecoveryName(transactionId, ".txn");
                tombstoneName = RecoveryName(transactionId, ".clear");
                recoveryManifestHandle = CreateRecoveryManifest(
                    liveRoot,
                    recoveryManifestName,
                    new AppStorageRecoveryManifest(
                        transactionId,
                        AppStorageRecoveryKind.Clear,
                        appRootIdentity,
                        null,
                        0,
                        null,
                        identity,
                        oldLength,
                        oldHash));
                interleaving.Reach(
                    AppStorageInterleavingPoint.ClearRecoveryManifestDurable,
                    new AppStorageInterleavingContext(recoveryManifestName));
                using var revalidatedRoot = ValidateLiveStorage(relativePath, cancellationToken);
                if (ReadFileIdentity(file!) != identity ||
                    !FinalPathEndsWith(file!, relativePath.Segments[0]))
                {
                    throw new StorageProofException(
                        AppStorageDiagnosticCode.IdentityDrift,
                        "The guarded payload identity changed before deletion.");
                }

                interleaving.Reach(
                    AppStorageInterleavingPoint.BeforeClearTombstoneRename,
                    new AppStorageInterleavingContext(relativePath.Segments[0]));
                cancellationToken.ThrowIfCancellationRequested();
                RenameRelative(file!, revalidatedRoot, tombstoneName!, 0);
                renamed = true;
                Record("ClearRename", "remembered-roots", isMutation: true, wasCommitted: true);
                interleaving.Reach(
                    AppStorageInterleavingPoint.AfterClearTombstoneRename,
                    new AppStorageInterleavingContext(tombstoneName));
                Flush(revalidatedRoot);
                interleaving.Reach(
                    AppStorageInterleavingPoint.ClearDirectoryDurableAfterTombstone,
                    new AppStorageInterleavingContext(tombstoneName));
                interleaving.Reach(
                    AppStorageInterleavingPoint.BeforeClearDelete,
                    new AppStorageInterleavingContext(tombstoneName));
                SetDeleteDisposition(file!, "clear-tombstone");
                deleteDispositionSet = true;
                file!.Dispose();
                file = null;
                VerifyDeletion(revalidatedRoot, tombstoneName!, "clear-tombstone");
                deletionVerified = true;
                interleaving.Reach(
                    AppStorageInterleavingPoint.AfterClearDelete,
                    new AppStorageInterleavingContext(tombstoneName));
                Flush(revalidatedRoot);
                DeleteRecoveryManifest(
                    revalidatedRoot,
                    recoveryManifestHandle!,
                    recoveryManifestName!);
                recoveryManifestHandle = null;
                committedVerified = true;
                interleaving.Reach(
                    AppStorageInterleavingPoint.ClearDirectoryDurableAfterDelete,
                    new AppStorageInterleavingContext(tombstoneName));
                return AppStorageMutationResult.CommittedVerified();
            }
            catch (OperationCanceledException)
            {
                if (committedVerified)
                {
                    return AppStorageMutationResult.CommittedVerified();
                }

                if (deletionVerified)
                {
                    return FinalizeCommittedClear(
                        relativePath,
                        recoveryManifestHandle,
                        recoveryManifestName);
                }

                if (deleteDispositionSet)
                {
                    return PreserveClearRecoveryRequired(
                        "The clear delete disposition requires namespace recovery verification.");
                }

                return recoveryManifestHandle is not null
                    ? RollbackClear(
                        relativePath,
                        file,
                        recoveryManifestHandle,
                        recoveryManifestName,
                        renamed)
                    : AppStorageMutationResult.NotCommitted();
            }
            catch (AppStorageRecoveryResidueException exception)
            {
                return PreserveClearRecoveryRequired(exception.SafeMessage);
            }
            catch (StorageProofException exception)
            {
                if (committedVerified)
                {
                    return AppStorageMutationResult.CommittedVerified();
                }

                var diagnostic = new AppStorageDiagnostic(exception.Code, exception.SafeMessage);
                if (deletionVerified)
                {
                    return FinalizeCommittedClear(
                        relativePath,
                        recoveryManifestHandle,
                        recoveryManifestName,
                        diagnostic);
                }

                if (deleteDispositionSet)
                {
                    return PreserveClearRecoveryRequired(diagnostic.Message);
                }

                if (recoveryManifestHandle is not null)
                {
                    return RollbackClear(
                        relativePath,
                        file,
                        recoveryManifestHandle,
                        recoveryManifestName,
                        renamed,
                        diagnostic);
                }

                SetUnavailable(exception.Code, exception.SafeMessage);
                return AppStorageMutationResult.NotCommitted(LastDiagnostic);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or Win32Exception)
            {
                if (committedVerified)
                {
                    return AppStorageMutationResult.CommittedVerified();
                }

                var diagnostic = new AppStorageDiagnostic(
                    AppStorageDiagnosticCode.IoFailure,
                    "The guarded app-storage payload could not be cleared safely.");
                if (deletionVerified)
                {
                    return FinalizeCommittedClear(
                        relativePath,
                        recoveryManifestHandle,
                        recoveryManifestName,
                        diagnostic);
                }

                if (deleteDispositionSet)
                {
                    return PreserveClearRecoveryRequired(diagnostic.Message);
                }

                if (recoveryManifestHandle is not null)
                {
                    return RollbackClear(
                        relativePath,
                        file,
                        recoveryManifestHandle,
                        recoveryManifestName,
                        renamed,
                        diagnostic);
                }

                SetUnavailable(diagnostic.Code, diagnostic.Message);
                return AppStorageMutationResult.NotCommitted(LastDiagnostic);
            }
            finally
            {
                file?.Dispose();
                recoveryManifestHandle?.Dispose();
                if (oldHash is not null)
                {
                    CryptographicOperations.ZeroMemory(oldHash);
                }
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
            IsAvailable = false;
            ReleaseHandles();
            storageMutex?.Dispose();
            storageMutex = null;
        }
    }

    private SafeFileHandle ValidateLiveStorage(
        NormalizedRelativePath relativePath,
        CancellationToken cancellationToken)
    {
        if (disposed ||
            !IsAvailable ||
            capabilityRoot is null ||
            nativeLocalRoot is null ||
            appRoot is null ||
            localAppDataPath is null ||
            appRootFinalPath is null)
        {
            throw new StorageProofException(
                AppStorageDiagnosticCode.Unavailable,
                "Guarded app storage is unavailable.");
        }

        if (relativePath.Segments.Count != 1)
        {
            throw new ArgumentException(
                "This guarded app-storage operation requires one normalized leaf name.",
                nameof(relativePath));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var retainedVolume = fileSystem.InspectVolume(capabilityRoot, cancellationToken);
        if (!retainedVolume.IsLocalVolume ||
            retainedVolume.IsNetworkRedirected ||
            !retainedVolume.SupportsPersistentAcls)
        {
            throw new StorageProofException(
                AppStorageDiagnosticCode.VolumeRejected,
                "The retained LocalAppData volume capability is no longer safe for storage.");
        }

        using var reopenedCapabilityRoot = fileSystem.OpenRoot(
            localAppDataPath,
            FileSystemOpenPurpose.AppStorage,
            cancellationToken);
        if (reopenedCapabilityRoot.Identity != capabilityRoot.Identity ||
            !SameFinalPath(reopenedCapabilityRoot.FinalPath, capabilityRoot.FinalPath) ||
            ReadDirectoryIdentity(nativeLocalRoot) != capabilityRoot.Identity ||
            !SameFinalPath(ReadFinalPath(nativeLocalRoot), capabilityRoot.FinalPath))
        {
            throw new StorageProofException(
                AppStorageDiagnosticCode.IdentityDrift,
                "The LocalAppData root identity changed.");
        }

        ValidateObject(appRoot, RelativeObjectKind.Directory);
        if (ReadDirectoryIdentity(appRoot) != appRootIdentity ||
            !SameFinalPath(ReadFinalPath(appRoot), appRootFinalPath))
        {
            throw new StorageProofException(
                AppStorageDiagnosticCode.IdentityDrift,
                "The retained app-storage root identity changed.");
        }

        ValidateRestrictedDacl(appRoot);
        var reopened = OpenRelative(
            nativeLocalRoot,
            AppRootName,
                RelativeObjectKind.Directory,
                AppDirectoryAccess,
                FileShareRead | FileShareWrite | FileShareDelete,
                FileOpen,
                0,
                IntPtr.Zero,
            allowMissing: false,
            out _,
            out _) ??
            throw new StorageProofException(
                AppStorageDiagnosticCode.IdentityDrift,
                "The app-storage root name no longer resolves.");
        try
        {
            if (ReadDirectoryIdentity(reopened) != appRootIdentity ||
                !SameFinalPath(ReadFinalPath(reopened), appRootFinalPath))
            {
                throw new StorageProofException(
                    AppStorageDiagnosticCode.IdentityDrift,
                    "The app-storage root name resolved to a different physical directory.");
            }

            ValidateRestrictedDacl(reopened);
            return Duplicate(reopened);
        }
        finally
        {
            reopened.Dispose();
        }
    }

    private PhysicalFileIdentity? ReadOptionalLeafIdentity(
        SafeFileHandle root,
        NormalizedRelativePath relativePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var existing = OpenLeaf(
            root,
            relativePath,
            GenericRead | FileReadAttributes | ReadControl | Synchronize,
            FileShareRead,
            FileOpen,
            0,
            IntPtr.Zero,
            allowMissing: true,
            out var missing,
            out _);
        if (missing)
        {
            return null;
        }

        ValidateRestrictedDacl(existing!);
        return ReadFileIdentity(existing!);
    }

    private static SafeFileHandle? OpenLeaf(
        SafeFileHandle root,
        NormalizedRelativePath relativePath,
        uint desiredAccess,
        uint shareAccess,
        uint disposition,
        uint additionalCreateOptions,
        IntPtr securityDescriptor,
        bool allowMissing,
        out bool missing,
        out bool created)
    {
        var kind = disposition == FileOpen
            ? RelativeObjectKind.Any
            : RelativeObjectKind.File;
        var handle = OpenRelative(
            root,
            relativePath.Segments[0],
            kind,
            desiredAccess,
            shareAccess,
            disposition,
            additionalCreateOptions,
            securityDescriptor,
            allowMissing,
            out missing,
            out created);
        if (handle is null)
        {
            return null;
        }

        if ((ReadBasicInformation(handle).FileAttributes & FileAttributeDirectory) != 0)
        {
            handle.Dispose();
            throw new StorageProofException(
                AppStorageDiagnosticCode.ReparseRejected,
                "A guarded app-storage file leaf resolved to a directory or reparse target.");
        }

        return handle;
    }

    private static SafeFileHandle OpenAbsoluteDirectory(
        string absolutePath,
        CancellationToken cancellationToken)
    {
        string normalized;
        try
        {
            normalized = Path.GetFullPath(absolutePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new StorageProofException(
                AppStorageDiagnosticCode.Unavailable,
                "The injected LocalAppData root path was invalid.",
                exception);
        }

        if (!Path.IsPathFullyQualified(normalized) ||
            normalized.StartsWith("\\\\", StringComparison.Ordinal) ||
            normalized.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            normalized.StartsWith("\\\\.\\", StringComparison.Ordinal))
        {
            throw new StorageProofException(
                AppStorageDiagnosticCode.VolumeRejected,
                "Guarded app storage requires a normal local filesystem path.");
        }

        var volumeRoot = Path.GetPathRoot(normalized) ??
            throw new StorageProofException(
                AppStorageDiagnosticCode.Unavailable,
                "The injected LocalAppData root had no volume root.");
        SafeFileHandle? current = null;
        try
        {
            current = CreateFile(
                volumeRoot,
                FileListDirectory | FileReadAttributes | Synchronize,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (current.IsInvalid)
            {
                throw NativeFailure("The LocalAppData volume root could not be opened.");
            }

            ValidateObject(current, RelativeObjectKind.Directory);
            var segments = normalized[volumeRoot.Length..]
                .TrimEnd('\\', '/')
                .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < segments.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var desiredAccess = index == segments.Length - 1
                    ? AppDirectoryAccess
                    : FileListDirectory | FileReadAttributes | Synchronize;
                var next = OpenRelative(
                    current,
                    segments[index],
                    RelativeObjectKind.Directory,
                    desiredAccess,
                    FileShareRead | FileShareWrite | FileShareDelete,
                    FileOpen,
                    0,
                    IntPtr.Zero,
                    allowMissing: false,
                    out _,
                    out _) ?? throw NativeFailure("A LocalAppData path segment could not be opened.");
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current?.Dispose();
            throw;
        }
    }

    private static SafeFileHandle? OpenRelative(
        SafeFileHandle parent,
        string name,
        RelativeObjectKind kind,
        uint desiredAccess,
        uint shareAccess,
        uint disposition,
        uint additionalCreateOptions,
        IntPtr securityDescriptor,
        bool allowMissing,
        out bool missing,
        out bool created)
    {
        missing = false;
        created = false;
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeUnicodeString>());
        var parentReferenceAdded = false;
        try
        {
            var unicodeString = new NativeUnicodeString
            {
                Length = checked((ushort)(name.Length * sizeof(char))),
                MaximumLength = checked((ushort)((name.Length + 1) * sizeof(char))),
                Buffer = nameBuffer,
            };
            Marshal.StructureToPtr(unicodeString, unicodeStringPointer, fDeleteOld: false);
            parent.DangerousAddRef(ref parentReferenceAdded);
            var objectAttributes = new NativeObjectAttributes
            {
                Length = checked((uint)Marshal.SizeOf<NativeObjectAttributes>()),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodeStringPointer,
                Attributes = ObjectCaseInsensitive,
                SecurityDescriptor = securityDescriptor,
            };
            var options = FileSynchronousIoNonAlert | FileOpenReparsePoint | additionalCreateOptions;
            if (kind == RelativeObjectKind.Directory)
            {
                options |= FileDirectoryFile;
            }
            else if (kind == RelativeObjectKind.File)
            {
                options |= FileNonDirectoryFile;
            }
            var status = NtCreateFile(
                out var rawHandle,
                desiredAccess,
                ref objectAttributes,
                out var ioStatus,
                IntPtr.Zero,
                FileAttributeNormal,
                shareAccess,
                disposition,
                options,
                IntPtr.Zero,
                0);
            if (status == StatusSuccess)
            {
                var handle = new SafeFileHandle(rawHandle, ownsHandle: true);
                try
                {
                    ValidateObject(handle, kind);
                    created = ioStatus.Information.ToUInt64() == 2;
                    return handle;
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            if (rawHandle != IntPtr.Zero && rawHandle != new IntPtr(-1))
            {
                using var unexpected = new SafeFileHandle(rawHandle, ownsHandle: true);
            }

            if (allowMissing &&
                status is StatusNoSuchFile or StatusObjectNameNotFound or StatusObjectPathNotFound)
            {
                missing = true;
                return null;
            }

            var error = RtlNtStatusToDosError(status);
            throw new StorageProofException(
                AppStorageDiagnosticCode.IoFailure,
                $"A retained-handle app-storage operation failed with Windows error {error}.",
                new Win32Exception(checked((int)error)));
        }
        finally
        {
            if (parentReferenceAdded)
            {
                parent.DangerousRelease();
            }

            Marshal.FreeHGlobal(unicodeStringPointer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static void ValidateObject(SafeFileHandle handle, RelativeObjectKind kind)
    {
        var information = ReadBasicInformation(handle);
        if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new StorageProofException(
                AppStorageDiagnosticCode.ReparseRejected,
                "A reparse point was rejected from guarded app storage.");
        }

        var isDirectory = (information.FileAttributes & FileAttributeDirectory) != 0;
        if ((kind == RelativeObjectKind.Directory && !isDirectory) ||
            (kind == RelativeObjectKind.File && isDirectory))
        {
            throw new StorageProofException(
                AppStorageDiagnosticCode.ReparseRejected,
                "A guarded app-storage object had an unexpected filesystem kind.");
        }

        _ = ReadFileId(handle);
    }

    private void ValidateRestrictedDacl(SafeFileHandle handle) =>
        ValidateRestrictedDacl(
            handle,
            SecurityObjectType.FileObject,
            FileAllAccess,
            "app-storage");

    private void HardenKnownLegacyPreferenceLeaf(
        SafeFileHandle root,
        string leafName)
    {
        if (!NormalizedRelativePath.TryCreate(leafName, out var relativePath, out _) ||
            relativePath is null)
        {
            throw new InvalidOperationException("A fixed legacy preference path was invalid.");
        }

        using var legacy = OpenLeaf(
            root,
            relativePath,
            WriteDac | ReadControl | FileReadAttributes | Synchronize,
            FileShareRead | FileShareWrite | FileShareDelete,
            FileOpen,
            0,
            IntPtr.Zero,
            allowMissing: true,
            out var missing,
            out _);
        if (missing)
        {
            return;
        }

        EnsureRestrictedDaclOrHardenOwnedLegacy(legacy!, "legacy-preference-leaf");
    }

    private void EnsureRestrictedLeafDaclOrHardenOwnedLegacy(
        SafeFileHandle root,
        NormalizedRelativePath relativePath,
        SafeFileHandle observed,
        string auditObject)
    {
        try
        {
            ValidateRestrictedDacl(observed);
            return;
        }
        catch (StorageProofException exception) when (
            exception.Code == AppStorageDiagnosticCode.DaclRejected)
        {
            // Reopen only DACL rights after proving the observed legacy object is owned.
        }

        ValidateCurrentUserOwner(observed);
        using var writableDacl = OpenLeaf(
            root,
            relativePath,
            WriteDac | ReadControl | FileReadAttributes | Synchronize,
            FileShareRead | FileShareWrite | FileShareDelete,
            FileOpen,
            0,
            IntPtr.Zero,
            allowMissing: false,
            out _,
            out _) ??
            throw new StorageProofException(
                AppStorageDiagnosticCode.IdentityDrift,
                "The owned legacy app-storage object could not be reopened for DACL hardening.");
        if (ReadFileIdentity(writableDacl) != ReadFileIdentity(observed) ||
            !FinalPathEndsWith(writableDacl, relativePath.Segments[0]))
        {
            throw new StorageProofException(
                AppStorageDiagnosticCode.IdentityDrift,
                "The owned legacy app-storage object changed before DACL hardening.");
        }

        EnsureRestrictedDaclOrHardenOwnedLegacy(writableDacl, auditObject);
        ValidateRestrictedDacl(observed);
    }

    private void EnsureRestrictedDaclOrHardenOwnedLegacy(
        SafeFileHandle handle,
        string auditObject)
    {
        try
        {
            ValidateRestrictedDacl(handle);
            return;
        }
        catch (StorageProofException exception) when (
            exception.Code == AppStorageDiagnosticCode.DaclRejected)
        {
            // Continue only for a normal object that is still owned by the current user.
        }

        ValidateCurrentUserOwner(handle);
        using var securityDescriptor = CreateRestrictedSecurityDescriptor(currentUserSid);
        if (!GetSecurityDescriptorDacl(
                securityDescriptor.Pointer,
                out var daclPresent,
                out var dacl,
                out _) ||
            !daclPresent ||
            dacl == IntPtr.Zero)
        {
            throw new StorageProofException(
                AppStorageDiagnosticCode.DaclRejected,
                "The restricted legacy app-storage DACL could not be prepared.");
        }

        var handleReferenceAdded = false;
        try
        {
            handle.DangerousAddRef(ref handleReferenceAdded);
            var status = SetSecurityInfo(
                handle.DangerousGetHandle(),
                SecurityObjectType.FileObject,
                DaclSecurityInformation | ProtectedDaclSecurityInformation,
                IntPtr.Zero,
                IntPtr.Zero,
                dacl,
                IntPtr.Zero);
            if (status != 0)
            {
                throw new StorageProofException(
                    AppStorageDiagnosticCode.DaclRejected,
                    "The owned legacy app-storage DACL could not be restricted.");
            }
        }
        finally
        {
            if (handleReferenceAdded)
            {
                handle.DangerousRelease();
            }
        }

        ValidateRestrictedDacl(handle);
        Record("HardenLegacyDacl", auditObject, isMutation: true, wasCommitted: true);
    }

    private void EnsureRestrictedAppRootDacl(
        SafeFileHandle parent,
        SafeFileHandle retainedRoot)
    {
        try
        {
            ValidateRestrictedDacl(retainedRoot);
            return;
        }
        catch (StorageProofException exception) when (
            exception.Code == AppStorageDiagnosticCode.DaclRejected)
        {
            // Continue only for an owned legacy root that needs a non-propagating DACL update.
        }

        ValidateCurrentUserOwner(retainedRoot);
        using var nonPropagatingRoot = OpenRelative(
            parent,
            AppRootName,
            RelativeObjectKind.Directory,
            MaximumAllowed | Synchronize,
            FileShareRead | FileShareWrite | FileShareDelete,
            FileOpen,
            0,
            IntPtr.Zero,
            allowMissing: false,
            out _,
            out _) ??
            throw new StorageProofException(
                AppStorageDiagnosticCode.IdentityDrift,
                "The legacy app-storage root could not be reopened for DACL hardening.");
        if (ReadDirectoryIdentity(nonPropagatingRoot) != ReadDirectoryIdentity(retainedRoot) ||
            !SameFinalPath(ReadFinalPath(nonPropagatingRoot), ReadFinalPath(retainedRoot)))
        {
            throw new StorageProofException(
                AppStorageDiagnosticCode.IdentityDrift,
                "The legacy app-storage root changed before DACL hardening.");
        }

        EnsureRestrictedDaclOrHardenOwnedLegacy(nonPropagatingRoot, "app-root");
        ValidateRestrictedDacl(retainedRoot);
    }

    private void ValidateCurrentUserOwner(SafeFileHandle handle)
    {
        var handleReferenceAdded = false;
        IntPtr securityDescriptor = IntPtr.Zero;
        try
        {
            handle.DangerousAddRef(ref handleReferenceAdded);
            var status = GetOwnerSecurityInfo(
                handle.DangerousGetHandle(),
                SecurityObjectType.FileObject,
                OwnerSecurityInformation,
                out var owner,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                out securityDescriptor);
            if (status != 0 || securityDescriptor == IntPtr.Zero || owner == IntPtr.Zero)
            {
                throw new StorageProofException(
                    AppStorageDiagnosticCode.DaclRejected,
                    "The legacy app-storage owner could not be verified.");
            }

            if (!string.Equals(
                    ReadSidString(owner),
                    currentUserSid,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new StorageProofException(
                    AppStorageDiagnosticCode.DaclRejected,
                    "The legacy app-storage object was not owned by the current user.");
            }
        }
        finally
        {
            if (securityDescriptor != IntPtr.Zero)
            {
                _ = LocalFree(securityDescriptor);
            }

            if (handleReferenceAdded)
            {
                handle.DangerousRelease();
            }
        }
    }

    private void ValidateRestrictedMutexDacl(SafeWaitHandle handle) =>
        ValidateRestrictedDacl(
            handle,
            SecurityObjectType.KernelObject,
            MutexAllAccess,
            "global storage mutex");

    private void ValidateRestrictedDacl(
        SafeHandle handle,
        SecurityObjectType objectType,
        uint expectedAccessMask,
        string safeObjectName)
    {
        var handleReferenceAdded = false;
        IntPtr securityDescriptor = IntPtr.Zero;
        try
        {
            handle.DangerousAddRef(ref handleReferenceAdded);
            var status = GetSecurityInfo(
                handle.DangerousGetHandle(),
                objectType,
                DaclSecurityInformation,
                IntPtr.Zero,
                IntPtr.Zero,
                out var dacl,
                IntPtr.Zero,
                out securityDescriptor);
            if (status != 0 || securityDescriptor == IntPtr.Zero || dacl == IntPtr.Zero)
            {
                throw new StorageProofException(
                    AppStorageDiagnosticCode.DaclRejected,
                    $"The {safeObjectName} DACL could not be verified.");
            }

            if (!GetSecurityDescriptorControl(securityDescriptor, out var control, out _) ||
                (control & SecurityDescriptorDaclProtected) == 0 ||
                !GetAclInformation(
                    dacl,
                    out var aclInformation,
                    checked((uint)Marshal.SizeOf<AclSizeInformation>()),
                    AclInformationClass.AclSizeInformation) ||
                aclInformation.AceCount != 2)
            {
                throw new StorageProofException(
                    AppStorageDiagnosticCode.DaclRejected,
                    $"The {safeObjectName} DACL was not the required protected two-principal ACL.");
            }

            var principals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; index < aclInformation.AceCount; index++)
            {
                if (!GetAce(dacl, index, out var ace) ||
                    Marshal.ReadByte(ace, 0) != AccessAllowedAceType ||
                    Marshal.ReadByte(ace, 1) != 0 ||
                    unchecked((uint)Marshal.ReadInt32(ace, 4)) != expectedAccessMask)
                {
                    throw new StorageProofException(
                        AppStorageDiagnosticCode.DaclRejected,
                        $"The {safeObjectName} DACL contained an unexpected access rule.");
                }

                var sid = ReadSidString(IntPtr.Add(ace, 8));
                if (!principals.Add(sid))
                {
                    throw new StorageProofException(
                        AppStorageDiagnosticCode.DaclRejected,
                        $"The {safeObjectName} DACL contained duplicate access rules.");
                }
            }

            if (!principals.SetEquals(["S-1-5-18", currentUserSid]))
            {
                throw new StorageProofException(
                    AppStorageDiagnosticCode.DaclRejected,
                    $"The {safeObjectName} DACL allowed an unexpected principal.");
            }
        }
        finally
        {
            if (securityDescriptor != IntPtr.Zero)
            {
                _ = LocalFree(securityDescriptor);
            }

            if (handleReferenceAdded)
            {
                handle.DangerousRelease();
            }
        }
    }

    private static byte[] ReadBounded(
        SafeFileHandle handle,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var length = RandomAccess.GetLength(handle);
        if (length < 0 || length > maximumBytes || length > int.MaxValue)
        {
            throw new StorageProofException(
                AppStorageDiagnosticCode.IoFailure,
                "The guarded app-storage payload exceeded its read bound.");
        }

        var bytes = new byte[checked((int)length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
            if (read == 0)
            {
                throw new StorageProofException(
                    AppStorageDiagnosticCode.IoFailure,
                    "The guarded app-storage payload ended before its verified length.");
            }

            offset += read;
        }

        return bytes;
    }

    private static void RenameRelative(
        SafeFileHandle source,
        SafeFileHandle destinationRoot,
        string destinationName,
        uint flags)
    {
        var nameBytes = checked(destinationName.Length * sizeof(char));
        var layout = AppStorageRenameLayout.Current;
        var bufferSize = layout.BufferSize(nameBytes);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        var rootReferenceAdded = false;
        try
        {
            Marshal.Copy(new byte[bufferSize], 0, buffer, bufferSize);
            destinationRoot.DangerousAddRef(ref rootReferenceAdded);
            Marshal.WriteInt32(buffer, layout.FlagsOffset, checked((int)flags));
            Marshal.WriteIntPtr(
                buffer,
                layout.RootDirectoryOffset,
                destinationRoot.DangerousGetHandle());
            Marshal.WriteInt32(buffer, layout.FileNameLengthOffset, nameBytes);
            Marshal.Copy(
                destinationName.ToCharArray(),
                0,
                IntPtr.Add(buffer, layout.FileNameOffset),
                destinationName.Length);
            var status = NtSetInformationFile(
                source,
                out _,
                buffer,
                checked((uint)bufferSize),
                NativeFileInformationClass.FileRenameInformationEx);
            if (status != StatusSuccess)
            {
                var error = RtlNtStatusToDosError(status);
                throw new StorageProofException(
                    AppStorageDiagnosticCode.IoFailure,
                    $"The sibling app-storage file could not be atomically renamed (Windows error {error}).");
            }
        }
        finally
        {
            if (rootReferenceAdded)
            {
                destinationRoot.DangerousRelease();
            }

            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void MarkDelete(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(buffer, checked((int)FileDispositionFlagDelete));
            if (!SetFileInformationByHandle(
                    handle,
                    FileInfoByHandleClass.FileDispositionInfoEx,
                    buffer,
                    sizeof(uint)))
            {
                throw NativeFailure("The guarded app-storage leaf could not be deleted.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void SetDeleteDisposition(SafeFileHandle handle, string opaqueObject)
    {
        MarkDelete(handle);
        Record(
            "DeleteDispositionSet",
            opaqueObject,
            isMutation: true,
            wasCommitted: false);
    }

    private void VerifyDeletion(
        SafeFileHandle root,
        string leafName,
        string opaqueObject)
    {
        Flush(root);
        VerifyLeafMissing(root, leafName);
        Record(
            "DeletionVerified",
            opaqueObject,
            isMutation: true,
            wasCommitted: true);
    }

    private static void Flush(SafeFileHandle handle)
    {
        if (!FlushFileBuffers(handle))
        {
            throw NativeFailure("A guarded app-storage handle could not be flushed durably.");
        }
    }

    private SafeFileHandle CreateRecoveryManifest(
        SafeFileHandle root,
        string manifestName,
        AppStorageRecoveryManifest manifest)
    {
        SafeFileHandle? handle = null;
        byte[]? plaintext = null;
        byte[]? entropy = null;
        byte[]? ciphertext = null;
        Exception? manifestFailure = null;
        try
        {
            using var securityDescriptor = CreateRestrictedSecurityDescriptor(currentUserSid);
            handle = OpenRelative(
                root,
                manifestName,
                RelativeObjectKind.File,
                GenericRead | GenericWrite | Delete | FileReadAttributes | ReadControl | WriteDac | Synchronize,
                FileShareRead,
                FileCreate,
                FileWriteThrough,
                securityDescriptor.Pointer,
                allowMissing: false,
                out _,
                out var created);
            if (handle is null || !created)
            {
                throw new StorageProofException(
                    AppStorageDiagnosticCode.IoFailure,
                    "A unique recovery manifest could not be created.");
            }

            ValidateRestrictedDacl(handle);
            plaintext = AppStorageRecoveryManifestCodec.Encode(manifest);
            entropy = CreateRecoveryEntropy();
            ciphertext = recoveryProtectedData.Protect(
                plaintext,
                entropy,
                MaximumRecoveryManifestCiphertextBytes);
            if (ciphertext.Length > MaximumRecoveryManifestCiphertextBytes)
            {
                throw new StorageProofException(
                    AppStorageDiagnosticCode.IoFailure,
                    "The recovery manifest ciphertext exceeded its fixed bound.");
            }

            RandomAccess.Write(handle, ciphertext, 0);
            RandomAccess.FlushToDisk(handle);
            var verified = ReadBounded(
                handle,
                MaximumRecoveryManifestCiphertextBytes,
                CancellationToken.None);
            try
            {
                if (!verified.AsSpan().SequenceEqual(ciphertext))
                {
                    throw new StorageProofException(
                        AppStorageDiagnosticCode.IoFailure,
                        "The recovery manifest failed durable byte verification.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(verified);
            }

            Flush(root);
            Record("RecoveryManifestCreate", "transaction-manifest", isMutation: true, wasCommitted: false);
            var result = handle;
            handle = null;
            return result;
        }
        catch (Exception exception)
        {
            manifestFailure = exception;
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            if (entropy is not null)
            {
                CryptographicOperations.ZeroMemory(entropy);
            }

            if (ciphertext is not null)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }
        }

        if (handle is not null)
        {
            try
            {
                SetDeleteDisposition(handle, "transaction-manifest");
                handle.Dispose();
                VerifyDeletion(root, manifestName, "transaction-manifest");
            }
            catch (Exception cleanupFailure)
            {
                throw new AppStorageRecoveryResidueException(
                    "A failed recovery manifest requires namespace recovery verification.",
                    cleanupFailure);
            }
            finally
            {
                handle.Dispose();
            }
        }

        ExceptionDispatchInfo.Capture(manifestFailure!).Throw();
        throw new InvalidOperationException("Unreachable recovery-manifest failure path.");
    }

    private void DeleteRecoveryManifest(
        SafeFileHandle root,
        SafeFileHandle manifest,
        string manifestName)
    {
        SetDeleteDisposition(manifest, "transaction-manifest");
        manifest.Dispose();
        VerifyDeletion(root, manifestName, "transaction-manifest");
    }

    private AppStorageRecoveryState RecoverStorageState(
        SafeFileHandle root,
        NormalizedRelativePath relativePath,
        bool abandonedMutex,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                relativePath.Segments[0],
                RememberedRootsName,
                StringComparison.OrdinalIgnoreCase))
        {
            return AppStorageRecoveryState.Clean;
        }

        if (abandonedMutex)
        {
            Record("RecoveryScan", "abandoned-mutex", isMutation: false, wasCommitted: false);
        }

        try
        {
            var names = WindowsFileSystemCapability.EnumerateChildNames(
                root,
                MaximumRecoveryEntries,
                cancellationToken);
            var recoveryEntries = new List<RecoveryEntry>();
            foreach (var name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!name.StartsWith(".bf-", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!TryParseRecoveryName(name, out var transactionId, out var kind))
                {
                    return RecoveryRequired(
                        "An unrecognized app-storage recovery leaf requires manual recovery verification.");
                }

                recoveryEntries.Add(new RecoveryEntry(name, transactionId, kind));
            }

            if (recoveryEntries.Count == 0)
            {
                return AppStorageRecoveryState.Clean;
            }

            Record("RecoveryScan", "transaction-residue", isMutation: false, wasCommitted: false);
            var manifests = recoveryEntries
                .Where(entry => entry.Kind == RecoveryLeafKind.Manifest)
                .ToArray();
            if (manifests.Length != 1 ||
                recoveryEntries.Any(entry => entry.TransactionId != manifests[0].TransactionId))
            {
                return RecoveryRequired(
                    "Multiple or unlinked app-storage recovery leaves require manual recovery verification.");
            }

            var manifestEntry = manifests[0];
            using var manifestHandle = OpenRecoveryLeaf(
                root,
                manifestEntry.Name,
                out var manifestMissing);
            if (manifestMissing || manifestHandle is null)
            {
                return RecoveryRequired(
                    "The linked app-storage recovery manifest disappeared during verification.");
            }

            ValidateRestrictedDacl(manifestHandle);
            var ciphertext = ReadBounded(
                manifestHandle,
                MaximumRecoveryManifestCiphertextBytes,
                cancellationToken);
            byte[]? entropy = null;
            byte[]? plaintext = null;
            try
            {
                entropy = CreateRecoveryEntropy();
                plaintext = recoveryProtectedData.Unprotect(
                    ciphertext,
                    entropy,
                    MaximumRecoveryManifestPlaintextBytes);
                var manifest = AppStorageRecoveryManifestCodec.Decode(
                    plaintext,
                    appRootIdentity);
                if (manifest.TransactionId != manifestEntry.TransactionId)
                {
                    return RecoveryRequired(
                        "The app-storage recovery manifest filename linkage was invalid.");
                }

                var allowedKinds = manifest.Kind switch
                {
                    AppStorageRecoveryKind.ReplaceExisting =>
                        new HashSet<RecoveryLeafKind>
                        {
                            RecoveryLeafKind.Manifest,
                            RecoveryLeafKind.Stage,
                            RecoveryLeafKind.Old,
                        },
                    AppStorageRecoveryKind.ReplaceMissing =>
                        new HashSet<RecoveryLeafKind>
                        {
                            RecoveryLeafKind.Manifest,
                            RecoveryLeafKind.Stage,
                        },
                    AppStorageRecoveryKind.Clear =>
                        new HashSet<RecoveryLeafKind>
                        {
                            RecoveryLeafKind.Manifest,
                            RecoveryLeafKind.Clear,
                        },
                    _ => [],
                };
                if (recoveryEntries.Any(entry => !allowedKinds.Contains(entry.Kind)) ||
                    recoveryEntries.GroupBy(entry => entry.Kind).Any(group => group.Count() > 1))
                {
                    return RecoveryRequired(
                        "The app-storage recovery transaction contained an unexpected leaf set.");
                }

                return manifest.Kind switch
                {
                    AppStorageRecoveryKind.ReplaceExisting or AppStorageRecoveryKind.ReplaceMissing =>
                        RecoverReplaceTransaction(
                            root,
                            relativePath,
                            manifestHandle,
                            manifestEntry.Name,
                            manifest,
                            recoveryEntries),
                    AppStorageRecoveryKind.Clear =>
                        RecoverClearTransaction(
                            root,
                            relativePath,
                            manifestHandle,
                            manifestEntry.Name,
                            manifest,
                            recoveryEntries),
                    _ => RecoveryRequired(
                        "The app-storage recovery transaction operation was unsupported."),
                };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ciphertext);
                if (entropy is not null)
                {
                    CryptographicOperations.ZeroMemory(entropy);
                }

                if (plaintext is not null)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is StorageProofException or
            CapabilityBoundaryException or
            CryptographicException or
            IOException or
            UnauthorizedAccessException or
            Win32Exception or
            ArgumentException)
        {
            return RecoveryRequired(
                "The app-storage recovery transaction could not be authenticated and proved.");
        }
    }

    private AppStorageRecoveryState RecoverReplaceTransaction(
        SafeFileHandle root,
        NormalizedRelativePath relativePath,
        SafeFileHandle manifestHandle,
        string manifestName,
        AppStorageRecoveryManifest manifest,
        IReadOnlyCollection<RecoveryEntry> entries)
    {
        var stageName = RecoveryName(manifest.TransactionId, ".tmp");
        var oldName = RecoveryName(manifest.TransactionId, ".old");
        var stageListed = entries.Any(entry => entry.Kind == RecoveryLeafKind.Stage);
        var oldListed = entries.Any(entry => entry.Kind == RecoveryLeafKind.Old);
        SafeFileHandle? target = null;
        SafeFileHandle? stage = null;
        SafeFileHandle? old = null;
        try
        {
            target = OpenRecoveryLeaf(root, relativePath.Segments[0], out var targetMissing);
            stage = OpenRecoveryLeaf(root, stageName, out var stageMissing);
            old = OpenRecoveryLeaf(root, oldName, out var oldMissing);
            if (stageListed == stageMissing || oldListed == oldMissing)
            {
                return RecoveryRequired(
                    "An app-storage recovery leaf changed during retained-handle acquisition.");
            }

            if (target is not null)
            {
                ValidateRestrictedDacl(target);
            }

            if (stage is not null)
            {
                ValidateRestrictedDacl(stage);
                if (!MatchesContentProof(
                        stage,
                        manifest.StagedIdentity!.Value,
                        manifest.StagedLength,
                        manifest.StagedSha256))
                {
                    return RecoveryRequired(
                        "The linked staged app-storage content did not match its authenticated manifest.");
                }
            }

            if (old is not null)
            {
                ValidateRestrictedDacl(old);
                if (manifest.OldIdentity is null ||
                    !MatchesContentProof(
                        old,
                        manifest.OldIdentity.Value,
                        manifest.OldLength,
                        manifest.OldSha256))
                {
                    return RecoveryRequired(
                        "The linked old app-storage content did not match its authenticated manifest.");
                }
            }

            var targetRole = targetMissing
                ? RecoveryTargetRole.Missing
                : MatchesContentProof(
                    target!,
                    manifest.StagedIdentity!.Value,
                    manifest.StagedLength,
                    manifest.StagedSha256)
                    ? RecoveryTargetRole.Stage
                    : manifest.OldIdentity is not null && MatchesContentProof(
                        target!,
                        manifest.OldIdentity.Value,
                        manifest.OldLength,
                        manifest.OldSha256)
                        ? RecoveryTargetRole.Old
                        : RecoveryTargetRole.Unknown;
            if (targetRole == RecoveryTargetRole.Unknown)
            {
                return RecoveryRequired(
                    "The final app-storage name was occupied by content outside the authenticated transaction.");
            }

            if (manifest.Kind == AppStorageRecoveryKind.ReplaceMissing)
            {
                if (old is not null || oldListed)
                {
                    return RecoveryRequired(
                        "A missing-target recovery transaction unexpectedly contained an old target.");
                }

                if (targetRole == RecoveryTargetRole.Missing && stage is not null)
                {
                    DeleteRecoveryLeaf(root, stage, stageName, "RecoveryStageDelete", committed: false);
                    stage = null;
                    DeleteRecoveryManifest(root, manifestHandle, manifestName);
                    Record("RecoveryRollback", "remembered-roots", isMutation: false, wasCommitted: false);
                    return AppStorageRecoveryState.RolledBack;
                }

                if (targetRole == RecoveryTargetRole.Stage && stage is null)
                {
                    DeleteRecoveryManifest(root, manifestHandle, manifestName);
                    Record("RecoveryCommit", "remembered-roots", isMutation: false, wasCommitted: true);
                    return AppStorageRecoveryState.Committed;
                }

                return RecoveryRequired(
                    "The missing-target recovery transaction had an ambiguous namespace state.");
            }

            if (targetRole == RecoveryTargetRole.Old && old is null)
            {
                if (stage is not null)
                {
                    DeleteRecoveryLeaf(root, stage, stageName, "RecoveryStageDelete", committed: false);
                    stage = null;
                }

                DeleteRecoveryManifest(root, manifestHandle, manifestName);
                Record("RecoveryRollback", "remembered-roots", isMutation: false, wasCommitted: false);
                return AppStorageRecoveryState.RolledBack;
            }

            if (targetRole == RecoveryTargetRole.Missing && old is not null)
            {
                RenameRelative(old, root, relativePath.Segments[0], 0);
                Flush(root);
                if (!FinalPathEndsWith(old, relativePath.Segments[0]))
                {
                    throw new StorageProofException(
                        AppStorageDiagnosticCode.IdentityDrift,
                        "The recovered old target did not become the final name.");
                }

                old.Dispose();
                old = null;
                if (stage is not null)
                {
                    DeleteRecoveryLeaf(root, stage, stageName, "RecoveryStageDelete", committed: false);
                    stage = null;
                }

                DeleteRecoveryManifest(root, manifestHandle, manifestName);
                Record("RecoveryRollback", "remembered-roots", isMutation: false, wasCommitted: false);
                return AppStorageRecoveryState.RolledBack;
            }

            if (targetRole == RecoveryTargetRole.Stage && stage is null)
            {
                if (old is not null)
                {
                    DeleteRecoveryLeaf(root, old, oldName, "RecoveryOldDelete", committed: true);
                    old = null;
                }

                DeleteRecoveryManifest(root, manifestHandle, manifestName);
                Record("RecoveryCommit", "remembered-roots", isMutation: false, wasCommitted: true);
                return AppStorageRecoveryState.Committed;
            }

            return RecoveryRequired(
                "The existing-target recovery transaction had an ambiguous namespace state.");
        }
        finally
        {
            target?.Dispose();
            stage?.Dispose();
            old?.Dispose();
        }
    }

    private AppStorageRecoveryState RecoverClearTransaction(
        SafeFileHandle root,
        NormalizedRelativePath relativePath,
        SafeFileHandle manifestHandle,
        string manifestName,
        AppStorageRecoveryManifest manifest,
        IReadOnlyCollection<RecoveryEntry> entries)
    {
        var clearName = RecoveryName(manifest.TransactionId, ".clear");
        var clearListed = entries.Any(entry => entry.Kind == RecoveryLeafKind.Clear);
        SafeFileHandle? target = null;
        SafeFileHandle? clear = null;
        try
        {
            target = OpenRecoveryLeaf(root, relativePath.Segments[0], out var targetMissing);
            clear = OpenRecoveryLeaf(root, clearName, out var clearMissing);
            if (clearListed == clearMissing)
            {
                return RecoveryRequired(
                    "The clear recovery tombstone changed during retained-handle acquisition.");
            }

            if (target is not null)
            {
                ValidateRestrictedDacl(target);
            }

            if (clear is not null)
            {
                ValidateRestrictedDacl(clear);
            }

            var targetMatches = target is not null && MatchesContentProof(
                target,
                manifest.OldIdentity!.Value,
                manifest.OldLength,
                manifest.OldSha256);
            var clearMatches = clear is not null && MatchesContentProof(
                clear,
                manifest.OldIdentity!.Value,
                manifest.OldLength,
                manifest.OldSha256);
            if ((!targetMissing && !targetMatches) || (!clearMissing && !clearMatches))
            {
                return RecoveryRequired(
                    "The clear recovery content did not match its authenticated manifest.");
            }

            if (targetMatches && clearMissing)
            {
                DeleteRecoveryManifest(root, manifestHandle, manifestName);
                Record("RecoveryRollback", "remembered-roots", isMutation: false, wasCommitted: false);
                return AppStorageRecoveryState.RolledBack;
            }

            if (targetMissing && (clearMatches || clearMissing))
            {
                if (clear is not null)
                {
                    DeleteRecoveryLeaf(root, clear, clearName, "RecoveryClearDelete", committed: true);
                    clear = null;
                }
                else
                {
                    Record(
                        "DeletionVerified",
                        "clear-tombstone",
                        isMutation: true,
                        wasCommitted: true);
                }

                DeleteRecoveryManifest(root, manifestHandle, manifestName);
                Record("RecoveryCommit", "remembered-roots", isMutation: false, wasCommitted: true);
                return AppStorageRecoveryState.Committed;
            }

            return RecoveryRequired(
                "The clear recovery transaction had an ambiguous namespace state.");
        }
        finally
        {
            target?.Dispose();
            clear?.Dispose();
        }
    }

    private void DeleteRecoveryLeaf(
        SafeFileHandle root,
        SafeFileHandle leaf,
        string leafName,
        string operation,
        bool committed)
    {
        SetDeleteDisposition(leaf, "recovery-leaf");
        leaf.Dispose();
        VerifyDeletion(root, leafName, "recovery-leaf");
        Record(operation, "recovery-leaf", isMutation: false, wasCommitted: committed);
    }

    private static bool MatchesContentProof(
        SafeFileHandle handle,
        PhysicalFileIdentity expectedIdentity,
        long expectedLength,
        ReadOnlySpan<byte> expectedHash)
    {
        if (ReadFileIdentity(handle) != expectedIdentity ||
            RandomAccess.GetLength(handle) != expectedLength)
        {
            return false;
        }

        var (_, actualHash) = CreateContentProof(handle, CancellationToken.None);
        try
        {
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualHash);
        }
    }

    private static (long Length, byte[] Hash) CreateContentProof(
        SafeFileHandle handle,
        CancellationToken cancellationToken)
    {
        var length = RandomAccess.GetLength(handle);
        if (length < 0)
        {
            throw new StorageProofException(
                AppStorageDiagnosticCode.IoFailure,
                "A recovery-linked file had an invalid length.");
        }

        var buffer = new byte[64 * 1024];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            long offset = 0;
            while (offset < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = checked((int)Math.Min(buffer.Length, length - offset));
                var read = RandomAccess.Read(handle, buffer.AsSpan(0, requested), offset);
                if (read == 0)
                {
                    throw new StorageProofException(
                        AppStorageDiagnosticCode.IoFailure,
                        "A recovery-linked file ended before its verified length.");
                }

                hash.AppendData(buffer, 0, read);
                offset += read;
            }

            return (length, hash.GetHashAndReset());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static SafeFileHandle? OpenRecoveryLeaf(
        SafeFileHandle root,
        string name,
        out bool missing) =>
        OpenRelative(
            root,
            name,
            RelativeObjectKind.File,
            GenericRead | Delete | FileReadAttributes | ReadControl | Synchronize,
            FileShareRead,
            FileOpen,
            0,
            IntPtr.Zero,
            allowMissing: true,
            out missing,
            out _);

    private static void VerifyLeafMissing(SafeFileHandle root, string name)
    {
        using var reopened = OpenRelative(
            root,
            name,
            RelativeObjectKind.File,
            FileReadAttributes | ReadControl | Synchronize,
            FileShareRead,
            FileOpen,
            0,
            IntPtr.Zero,
            allowMissing: true,
            out var missing,
            out _);
        if (!missing)
        {
            throw new StorageProofException(
                AppStorageDiagnosticCode.IdentityDrift,
                "A deleted app-storage recovery leaf remained in the verified namespace.");
        }
    }

    private byte[] CreateRecoveryEntropy() =>
        Encoding.UTF8.GetBytes(
            $"BlockFerry/app-storage-recovery/v1|{currentUserSid}|" +
            $"{appRootIdentity.VolumeSerialNumber:X16}|" +
            $"{appRootIdentity.FileIdHigh:X16}{appRootIdentity.FileIdLow:X16}");

    private AppStorageRecoveryState RecoveryRequired(string safeMessage)
    {
        LastDiagnostic = new AppStorageDiagnostic(
            AppStorageDiagnosticCode.IoFailure,
            safeMessage);
        Record("RecoveryRequired", "transaction-residue", isMutation: false, wasCommitted: true);
        return AppStorageRecoveryState.RecoveryRequired;
    }

    private static string RecoveryName(Guid transactionId, string suffix) =>
        $".bf-{transactionId:N}{suffix}";

    private static bool TryParseRecoveryName(
        string name,
        out Guid transactionId,
        out RecoveryLeafKind kind)
    {
        transactionId = default;
        kind = default;
        if (!name.StartsWith(".bf-", StringComparison.Ordinal) || name.Length < 40)
        {
            return false;
        }

        var identifier = name.AsSpan(4, 32);
        if (!Guid.TryParseExact(identifier, "N", out transactionId) ||
            !identifier.SequenceEqual(transactionId.ToString("N").AsSpan()))
        {
            return false;
        }

        kind = name[36..] switch
        {
            ".tmp" => RecoveryLeafKind.Stage,
            ".old" => RecoveryLeafKind.Old,
            ".clear" => RecoveryLeafKind.Clear,
            ".txn" => RecoveryLeafKind.Manifest,
            _ => RecoveryLeafKind.Unknown,
        };
        return kind != RecoveryLeafKind.Unknown;
    }

    private static NormalizedRelativePath CreateNormalizedPath(string value) =>
        NormalizedRelativePath.TryCreate(value, out var path, out var rejection)
            ? path!
            : throw new InvalidOperationException(
                $"A fixed app-storage path was invalid: {rejection}");

    private AppStorageMutationResult CleanupUncommittedStage(
        NormalizedRelativePath relativePath,
        SafeFileHandle? staged,
        string? stagedName,
        AppStorageDiagnostic? diagnostic)
    {
        if (staged is null || staged.IsClosed || staged.IsInvalid)
        {
            return AppStorageMutationResult.NotCommitted(diagnostic);
        }

        if (string.IsNullOrEmpty(stagedName))
        {
            return PreserveReplaceRecoveryRequired(
                "An uncommitted staging object lacked a verifiable recovery name.");
        }

        try
        {
            using var root = ValidateLiveStorage(relativePath, CancellationToken.None);
            SetDeleteDisposition(staged, "sibling-temp");
            staged.Dispose();
            VerifyDeletion(root, stagedName, "sibling-temp");
            return AppStorageMutationResult.NotCommitted(diagnostic);
        }
        catch (Exception exception) when (
            exception is StorageProofException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            return PreserveReplaceRecoveryRequired(
                "The uncommitted staging object requires namespace recovery verification.");
        }
    }

    private AppStorageMutationResult PreserveReplaceRecoveryRequired(string safeMessage)
    {
        LastDiagnostic = new AppStorageDiagnostic(
            AppStorageDiagnosticCode.IoFailure,
            safeMessage);
        Record("RecoveryRequired", "remembered-roots", isMutation: false, wasCommitted: true);
        return AppStorageMutationResult.RecoveryRequired(LastDiagnostic);
    }

    private AppStorageMutationResult PreserveClearRecoveryRequired(string safeMessage)
    {
        LastDiagnostic = new AppStorageDiagnostic(
            AppStorageDiagnosticCode.IoFailure,
            safeMessage);
        Record("RecoveryRequired", "clear-tombstone", isMutation: false, wasCommitted: true);
        return AppStorageMutationResult.RecoveryRequired(LastDiagnostic);
    }

    private AppStorageMutationResult RollbackReplace(
        NormalizedRelativePath relativePath,
        SafeFileHandle? staged,
        SafeFileHandle? target,
        SafeFileHandle? recoveryManifest,
        string? recoveryManifestName,
        string? stagedName,
        string? oldTombstoneName,
        bool targetWasMissing,
        PhysicalFileIdentity? beforeIdentity,
        bool targetDisplaced,
        bool committed,
        AppStorageDiagnostic? cause = null)
    {
        var recoveryDiagnostic = cause ?? new AppStorageDiagnostic(
            AppStorageDiagnosticCode.IoFailure,
            "The guarded app-storage commit requires recovery verification.");
        try
        {
            using var root = ValidateLiveStorage(relativePath, CancellationToken.None);
            if (committed && staged is not null)
            {
                var failedName = stagedName ??
                    throw new StorageProofException(
                        AppStorageDiagnosticCode.IdentityDrift,
                        "The recovery-linked staged name was unavailable during rollback.");
                RenameRelative(staged, root, failedName, 0);
                Record("RollbackNewTombstone", "sibling-temp", isMutation: true, wasCommitted: false);
                Flush(root);
            }

            if (targetDisplaced && target is not null)
            {
                RenameRelative(target, root, relativePath.Segments[0], 0);
                Record("RollbackTargetRestore", "remembered-roots", isMutation: true, wasCommitted: false);
                Flush(root);
            }

            if (staged is not null)
            {
                SetDeleteDisposition(staged, "sibling-temp");
                staged.Dispose();
                if (stagedName is not null)
                {
                    VerifyDeletion(root, stagedName, "sibling-temp");
                }
                else
                {
                    throw new StorageProofException(
                        AppStorageDiagnosticCode.IdentityDrift,
                        "The rollback staging name was unavailable for deletion verification.");
                }

                Record(
                    committed ? "RollbackNewDelete" : "RollbackStageDelete",
                    "sibling-temp",
                    isMutation: false,
                    wasCommitted: false);
            }

            if (targetDisplaced)
            {
                if (target is null ||
                    !FinalPathEndsWith(target, relativePath.Segments[0]))
                {
                    throw new StorageProofException(
                        AppStorageDiagnosticCode.IdentityDrift,
                        "The retained old target handle did not become the restored app-storage name.");
                }
            }
            else if (targetWasMissing)
            {
                using var restored = OpenLeaf(
                    root,
                    relativePath,
                    FileReadAttributes | ReadControl | Synchronize,
                    FileShareRead,
                    FileOpen,
                    0,
                    IntPtr.Zero,
                    allowMissing: true,
                    out var missing,
                    out _);
                if (!missing)
                {
                    throw new StorageProofException(
                        AppStorageDiagnosticCode.IdentityDrift,
                        "The failed missing-target commit unexpectedly left a final name.");
                }
            }
            else if (target is null ||
                     beforeIdentity is null ||
                     ReadFileIdentity(target) != beforeIdentity ||
                     !FinalPathEndsWith(target, relativePath.Segments[0]))
            {
                throw new StorageProofException(
                    AppStorageDiagnosticCode.IdentityDrift,
                    "The original target could not be proved after a precommit rollback.");
            }

            if (recoveryManifest is not null && recoveryManifestName is not null)
            {
                DeleteRecoveryManifest(root, recoveryManifest, recoveryManifestName);
            }

            return AppStorageMutationResult.NotCommitted(cause);
        }
        catch (Exception exception) when (
            exception is StorageProofException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            LastDiagnostic = recoveryDiagnostic;
            Record("RecoveryRequired", "remembered-roots", isMutation: false, wasCommitted: true);
            return AppStorageMutationResult.RecoveryRequired(recoveryDiagnostic);
        }
    }

    private AppStorageMutationResult RollbackClear(
        NormalizedRelativePath relativePath,
        SafeFileHandle? tombstone,
        SafeFileHandle recoveryManifest,
        string? recoveryManifestName,
        bool renamed,
        AppStorageDiagnostic? cause = null)
    {
        var recoveryDiagnostic = cause ?? new AppStorageDiagnostic(
            AppStorageDiagnosticCode.IoFailure,
            "The guarded app-storage clear requires recovery verification.");
        try
        {
            if (tombstone is null || tombstone.IsClosed || tombstone.IsInvalid)
            {
                throw new StorageProofException(
                    AppStorageDiagnosticCode.IdentityDrift,
                    "The guarded clear tombstone handle was unavailable for rollback.");
            }

            var identity = ReadFileIdentity(tombstone);
            using var root = ValidateLiveStorage(relativePath, CancellationToken.None);
            if (renamed)
            {
                RenameRelative(tombstone, root, relativePath.Segments[0], 0);
                Record("ClearRollbackRestore", "remembered-roots", isMutation: true, wasCommitted: false);
                Flush(root);
            }

            if (ReadFileIdentity(tombstone) != identity ||
                !FinalPathEndsWith(tombstone, relativePath.Segments[0]))
            {
                throw new StorageProofException(
                    AppStorageDiagnosticCode.IdentityDrift,
                    "The guarded clear rollback identity could not be proven.");
            }

            if (string.IsNullOrEmpty(recoveryManifestName))
            {
                throw new StorageProofException(
                    AppStorageDiagnosticCode.IdentityDrift,
                    "The guarded clear recovery manifest name was unavailable for rollback.");
            }

            DeleteRecoveryManifest(root, recoveryManifest, recoveryManifestName);

            return AppStorageMutationResult.NotCommitted(cause);
        }
        catch (Exception exception) when (
            exception is StorageProofException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            LastDiagnostic = recoveryDiagnostic;
            Record("RecoveryRequired", "clear-tombstone", isMutation: false, wasCommitted: true);
            return AppStorageMutationResult.RecoveryRequired(recoveryDiagnostic);
        }
    }

    private AppStorageMutationResult FinalizeCommittedClear(
        NormalizedRelativePath relativePath,
        SafeFileHandle? recoveryManifest,
        string? recoveryManifestName,
        AppStorageDiagnostic? diagnostic = null)
    {
        var recoveryDiagnostic = diagnostic ?? new AppStorageDiagnostic(
            AppStorageDiagnosticCode.IoFailure,
            "The guarded app-storage clear requires recovery verification.");
        try
        {
            if (recoveryManifest is null || string.IsNullOrEmpty(recoveryManifestName))
            {
                throw new StorageProofException(
                    AppStorageDiagnosticCode.IdentityDrift,
                    "The committed clear recovery manifest was unavailable for final verification.");
            }

            using var root = ValidateLiveStorage(relativePath, CancellationToken.None);
            DeleteRecoveryManifest(root, recoveryManifest, recoveryManifestName);
            return AppStorageMutationResult.CommittedVerified();
        }
        catch (Exception exception) when (
            exception is StorageProofException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            LastDiagnostic = recoveryDiagnostic;
            Record("RecoveryRequired", "clear-tombstone", isMutation: false, wasCommitted: true);
            return AppStorageMutationResult.RecoveryRequired(recoveryDiagnostic);
        }
    }

    private static bool FinalPathEndsWith(SafeFileHandle handle, string leafName)
    {
        var finalPath = ReadFinalPath(handle).TrimEnd('\\', '/');
        return string.Equals(
            Path.GetFileName(finalPath),
            leafName,
            StringComparison.OrdinalIgnoreCase);
    }

    private AppStorageSynchronization CreateStorageMutex(
        string currentSid,
        PhysicalDirectoryIdentity localRootIdentity)
    {
        var material = Encoding.UTF8.GetBytes(
            $"{currentSid}|{localRootIdentity.VolumeSerialNumber:X16}|" +
            $"{localRootIdentity.FileIdHigh:X16}{localRootIdentity.FileIdLow:X16}");
        try
        {
            var digest = SHA256.HashData(material);
            try
            {
                var name = "Global\\BlockFerry.AppStorage." + Convert.ToHexString(digest);
                using var securityDescriptor = CreateRestrictedMutexSecurityDescriptor(currentSid);
                var attributes = new NativeSecurityAttributes
                {
                    Length = checked((uint)Marshal.SizeOf<NativeSecurityAttributes>()),
                    SecurityDescriptor = securityDescriptor.Pointer,
                    InheritHandle = false,
                };
                var handle = CreateMutexEx(
                    ref attributes,
                    name,
                    0,
                    MutexAllAccess);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    throw NativeFailure("The global app-storage serialization mutex could not be created or opened.");
                }

                try
                {
                    ValidateRestrictedMutexDacl(handle);
                    return new AppStorageSynchronization(name, handle);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private StorageMutexLease AcquireStorageMutex(CancellationToken cancellationToken)
    {
        if (storageMutex is null)
        {
            return StorageMutexLease.Empty;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var waitResult = storageMutex.Wait(50);
            if (waitResult == WaitObject0)
            {
                return new StorageMutexLease(storageMutex, wasAbandoned: false);
            }

            if (waitResult == WaitAbandoned)
            {
                return new StorageMutexLease(storageMutex, wasAbandoned: true);
            }

            if (waitResult == WaitTimeout)
            {
                continue;
            }

            if (waitResult == WaitFailed)
            {
                throw NativeFailure("The global app-storage serialization mutex wait failed.");
            }

            throw new StorageProofException(
                AppStorageDiagnosticCode.IoFailure,
                $"The global app-storage serialization mutex returned unexpected wait status 0x{waitResult:X8}.");
        }
    }

    private static RestrictedSecurityDescriptor CreateRestrictedMutexSecurityDescriptor(string currentSid)
    {
        var sddl = $"D:P(A;;0x{MutexAllAccess:X8};;;SY)(A;;0x{MutexAllAccess:X8};;;{currentSid})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                sddl,
                1,
                out var securityDescriptor,
                out _))
        {
            throw NativeFailure("The restricted global-mutex security descriptor could not be created.");
        }

        return new RestrictedSecurityDescriptor(securityDescriptor);
    }

    private static RestrictedSecurityDescriptor CreateRestrictedSecurityDescriptor(string currentSid)
    {
        var sddl = $"D:P(A;;FA;;;SY)(A;;FA;;;{currentSid})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                sddl,
                1,
                out var securityDescriptor,
                out _))
        {
            throw NativeFailure("The restricted app-storage security descriptor could not be created.");
        }

        return new RestrictedSecurityDescriptor(securityDescriptor);
    }

    [SupportedOSPlatform("windows")]
    private static string ReadCurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return identity.User?.Value ??
            throw new StorageProofException(
                AppStorageDiagnosticCode.DaclRejected,
                "The current Windows user SID was unavailable.");
    }

    private static string ReadSidString(IntPtr sid)
    {
        if (!ConvertSidToStringSid(sid, out var text))
        {
            throw NativeFailure("An app-storage DACL principal could not be read.");
        }

        try
        {
            return Marshal.PtrToStringUni(text) ??
                throw new StorageProofException(
                    AppStorageDiagnosticCode.DaclRejected,
                    "An app-storage DACL principal was empty.");
        }
        finally
        {
            _ = LocalFree(text);
        }
    }

    private static SafeFileHandle Duplicate(SafeFileHandle source)
    {
        if (!DuplicateHandle(
                GetCurrentProcess(),
                source,
                GetCurrentProcess(),
                out var duplicate,
                0,
                false,
                0x00000002))
        {
            throw NativeFailure("A retained app-storage directory handle could not be duplicated.");
        }

        return duplicate;
    }

    private static ByHandleFileInformation ReadBasicInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw NativeFailure("App-storage filesystem metadata could not be read.");
        }

        return information;
    }

    private static FileIdInfo ReadFileId(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileIdInfo,
                out var information,
                checked((uint)Marshal.SizeOf<FileIdInfo>())) ||
            (information.FileId.LowPart == 0 && information.FileId.HighPart == 0))
        {
            throw new StorageProofException(
                AppStorageDiagnosticCode.IdentityDrift,
                "A full app-storage physical identity was unavailable.");
        }

        return information;
    }

    private static PhysicalDirectoryIdentity ReadDirectoryIdentity(SafeFileHandle handle)
    {
        var information = ReadFileId(handle);
        return new PhysicalDirectoryIdentity(
            information.VolumeSerialNumber,
            information.FileId.LowPart,
            information.FileId.HighPart);
    }

    private static PhysicalFileIdentity ReadFileIdentity(SafeFileHandle handle)
    {
        var information = ReadFileId(handle);
        return new PhysicalFileIdentity(
            information.VolumeSerialNumber,
            information.FileId.LowPart,
            information.FileId.HighPart);
    }

    private static string ReadFinalPath(SafeFileHandle handle)
    {
        var required = GetFinalPathNameByHandle(handle, IntPtr.Zero, 0, 0);
        if (required == 0)
        {
            throw NativeFailure("An app-storage final handle path could not be sized.");
        }

        var buffer = Marshal.AllocHGlobal(checked((int)(required + 1) * sizeof(char)));
        try
        {
            var written = GetFinalPathNameByHandle(handle, buffer, required + 1, 0);
            if (written == 0 || written > required)
            {
                throw NativeFailure("An app-storage final handle path could not be read consistently.");
            }

            var path = Marshal.PtrToStringUni(buffer, checked((int)written)) ??
                throw NativeFailure("An app-storage final handle path was empty.");
            return path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? path[4..] : path;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool SameFinalPath(string left, string right) =>
        string.Equals(
            left.TrimEnd('\\', '/'),
            right.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private void SetUnavailable(AppStorageDiagnosticCode code, string message)
    {
        IsAvailable = false;
        LastDiagnostic = new AppStorageDiagnostic(code, message);
        ReleaseHandles();
        Record("ProofRejected", "app-storage", isMutation: false, wasCommitted: false);
    }

    private void ReleaseHandles()
    {
        appRoot?.Dispose();
        nativeLocalRoot?.Dispose();
        capabilityRoot?.Dispose();
        appRoot = null;
        nativeLocalRoot = null;
        capabilityRoot = null;
    }

    private void Record(
        string operation,
        string opaqueObject,
        bool isMutation,
        bool wasCommitted) =>
        auditLog.Add(new AppStorageAuditEvent(operation, opaqueObject, isMutation, wasCommitted));

    private static StorageProofException NativeFailure(string safeMessage) =>
        new(
            AppStorageDiagnosticCode.IoFailure,
            safeMessage + $" (Windows error {Marshal.GetLastWin32Error()}).",
            new Win32Exception(Marshal.GetLastWin32Error()));

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Guarded app storage requires Windows handle APIs.");
        }
    }

    private static uint AppDirectoryAccess =>
        GenericWrite |
        FileListDirectory |
        FileAddFile |
        FileAddSubdirectory |
        FileTraverse |
        FileDeleteChild |
        FileReadAttributes |
        FileWriteAttributes |
        ReadControl |
        WriteDac |
        Synchronize;

    private enum RelativeObjectKind
    {
        Any,
        Directory,
        File,
    }

    private enum RecoveryLeafKind
    {
        Unknown,
        Manifest,
        Stage,
        Old,
        Clear,
    }

    private enum RecoveryTargetRole
    {
        Missing,
        Stage,
        Old,
        Unknown,
    }

    private sealed record RecoveryEntry(
        string Name,
        Guid TransactionId,
        RecoveryLeafKind Kind);

    private enum FileInfoByHandleClass
    {
        FileDispositionInfoEx = 21,
        FileIdInfo = 0x12,
    }

    private enum NativeFileInformationClass
    {
        FileRenameInformationEx = 65,
    }

    private enum SecurityObjectType
    {
        FileObject = 1,
        KernelObject = 6,
    }

    private enum AclInformationClass
    {
        AclSizeInformation = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSecurityAttributes
    {
        public uint Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeIoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public NativeFileId128 FileId;
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct NativeFileId128
    {
        public ulong LowPart;
        public ulong HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AclSizeInformation
    {
        public uint AceCount;
        public uint AclBytesInUse;
        public uint AclBytesFree;
    }

    private sealed class RestrictedSecurityDescriptor(IntPtr pointer) : IDisposable
    {
        public IntPtr Pointer { get; private set; } = pointer;

        public void Dispose()
        {
            if (Pointer == IntPtr.Zero)
            {
                return;
            }

            _ = LocalFree(Pointer);
            Pointer = IntPtr.Zero;
        }
    }

    private sealed class StorageProofException : IOException
    {
        public StorageProofException(
            AppStorageDiagnosticCode code,
            string safeMessage,
            Exception? innerException = null)
            : base(safeMessage, innerException)
        {
            Code = code;
            SafeMessage = safeMessage;
        }

        public AppStorageDiagnosticCode Code { get; }
        public string SafeMessage { get; }
    }

    private sealed class AppStorageRecoveryResidueException : IOException
    {
        public AppStorageRecoveryResidueException(string safeMessage, Exception innerException)
            : base(safeMessage, innerException)
        {
            SafeMessage = safeMessage;
        }

        public string SafeMessage { get; }
    }

    private sealed class AppStorageSynchronization(
        string name,
        SafeWaitHandle handle) : IDisposable
    {
        private SafeWaitHandle? handle = handle;

        public string Name { get; } = name;

        public uint Wait(uint milliseconds)
        {
            var retained = Volatile.Read(ref handle) ??
                throw new ObjectDisposedException(nameof(AppStorageSynchronization));
            var referenceAdded = false;
            try
            {
                retained.DangerousAddRef(ref referenceAdded);
                return WaitForSingleObject(retained.DangerousGetHandle(), milliseconds);
            }
            finally
            {
                if (referenceAdded)
                {
                    retained.DangerousRelease();
                }
            }
        }

        public void Release()
        {
            var retained = Volatile.Read(ref handle) ??
                throw new ObjectDisposedException(nameof(AppStorageSynchronization));
            var referenceAdded = false;
            try
            {
                retained.DangerousAddRef(ref referenceAdded);
                if (!ReleaseMutex(retained.DangerousGetHandle()))
                {
                    throw NativeFailure("The global app-storage serialization mutex could not be released.");
                }
            }
            finally
            {
                if (referenceAdded)
                {
                    retained.DangerousRelease();
                }
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref handle, null)?.Dispose();
        }
    }

    private sealed class StorageMutexLease(
        AppStorageSynchronization? synchronization,
        bool wasAbandoned) : IDisposable
    {
        private AppStorageSynchronization? synchronization = synchronization;

        public static StorageMutexLease Empty { get; } = new(null, wasAbandoned: false);

        public bool WasAbandoned { get; } = wasAbandoned;

        public void Dispose()
        {
            Interlocked.Exchange(ref synchronization, null)?.Release();
        }
    }

#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", EntryPoint = "CreateMutexExW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeWaitHandle CreateMutexEx(
        ref NativeSecurityAttributes mutexAttributes,
        string name,
        uint flags,
        uint desiredAccess);

    [DllImport("kernel32.dll", EntryPoint = "WaitForSingleObject", SetLastError = true, ExactSpelling = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", EntryPoint = "ReleaseMutex", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseMutex(IntPtr mutex);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("ntdll.dll", EntryPoint = "NtCreateFile", ExactSpelling = true)]
    private static extern int NtCreateFile(
        out IntPtr fileHandle,
        uint desiredAccess,
        ref NativeObjectAttributes objectAttributes,
        out NativeIoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll", EntryPoint = "NtSetInformationFile", ExactSpelling = true)]
    private static extern int NtSetInformationFile(
        SafeFileHandle fileHandle,
        out NativeIoStatusBlock ioStatusBlock,
        IntPtr fileInformation,
        uint length,
        NativeFileInformationClass fileInformationClass);

    [DllImport("ntdll.dll", EntryPoint = "RtlNtStatusToDosError", ExactSpelling = true)]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        out FileIdInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        IntPtr filePath,
        uint filePathCharacterCount,
        uint flags);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "FlushFileBuffers", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);

    [DllImport("kernel32.dll", EntryPoint = "DuplicateHandle", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcessHandle,
        SafeFileHandle sourceHandle,
        IntPtr targetProcessHandle,
        out SafeFileHandle targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSdRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorDacl", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorDacl(
        IntPtr securityDescriptor,
        [MarshalAs(UnmanagedType.Bool)] out bool daclPresent,
        out IntPtr dacl,
        [MarshalAs(UnmanagedType.Bool)] out bool daclDefaulted);

    [DllImport("advapi32.dll", EntryPoint = "GetSecurityInfo", ExactSpelling = true)]
    private static extern uint GetSecurityInfo(
        IntPtr handle,
        SecurityObjectType objectType,
        uint securityInfo,
        IntPtr owner,
        IntPtr group,
        out IntPtr dacl,
        IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", EntryPoint = "GetSecurityInfo", ExactSpelling = true)]
    private static extern uint GetOwnerSecurityInfo(
        IntPtr handle,
        SecurityObjectType objectType,
        uint securityInfo,
        out IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", EntryPoint = "SetSecurityInfo", ExactSpelling = true)]
    private static extern uint SetSecurityInfo(
        IntPtr handle,
        SecurityObjectType objectType,
        uint securityInfo,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl);

    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorControl", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorControl(
        IntPtr securityDescriptor,
        out ushort control,
        out uint revision);

    [DllImport("advapi32.dll", EntryPoint = "GetAclInformation", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAclInformation(
        IntPtr acl,
        out AclSizeInformation aclInformation,
        uint aclInformationLength,
        AclInformationClass aclInformationClass);

    [DllImport("advapi32.dll", EntryPoint = "GetAce", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAce(IntPtr acl, uint aceIndex, out IntPtr ace);

    [DllImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
#pragma warning restore SYSLIB1054
}
