using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using BlockFerry.Core.Content;
using BlockFerry.Core.System;
using Microsoft.Win32.SafeHandles;

namespace BlockFerry.Core.Transactions;

internal sealed partial class WindowsTransactionFileOperations : ITransactionFileOperations
{
    private const long SafetyMarginBytes = 16L * 1024 * 1024;
    private const int MaximumFileBytes = 256 * 1024 * 1024;
    private const int MaximumStreamInformationBytes = 64 * 1024;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint Delete = 0x00010000;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint Synchronize = 0x00100000;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileAddFile = 0x00000002;
    private const uint FileAddSubdirectory = 0x00000004;
    private const uint FileTraverse = 0x00000020;
    private const uint FileDeleteChild = 0x00000040;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileWriteAttributes = 0x00000100;
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
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint FileWriteThrough = 0x00000002;
    private const uint FileDispositionFlagDelete = 0x00000001;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int StatusSuccess = 0;
    private const int StatusNoSuchFile = unchecked((int)0xC000000F);
    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
    private const int StatusObjectPathNotFound = unchecked((int)0xC000003A);
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint GroupSecurityInformation = 0x00000002;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;
    private const uint UnprotectedDaclSecurityInformation = 0x20000000;
    private const ushort SecurityDescriptorDaclProtected = 0x1000;
    private readonly IFileSystemCapability fileSystem;
    private readonly BackupStore backupStore;
    private readonly ITransactionRaceBoundaryHook raceBoundaryHook;

    internal WindowsTransactionFileOperations(
        IFileSystemCapability fileSystem,
        BackupStore backupStore,
        ITransactionRaceBoundaryHook? raceBoundaryHook = null)
    {
        this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        this.backupStore = backupStore ?? throw new ArgumentNullException(nameof(backupStore));
        this.raceBoundaryHook = raceBoundaryHook ?? NullTransactionRaceBoundaryHook.Instance;
    }

    public TransactionRootLease OpenTargetRoot(
        MigrationTransactionCoordinator.ExecutionAuthority authority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var plan = authority.Plan;
        if (!plan.Session.IsActive ||
            !plan.ContentLease.IsActive ||
            !plan.ContentLease.IsBoundTo(plan.Session, plan.SourceInstanceId, plan.TargetInstanceId) ||
            !plan.ContentContext.IsOwnedBy(plan.ContentLease))
        {
            throw new InvalidOperationException("The original migration authority is no longer active.");
        }

        var evidence = authority.CurrentPairEvidence.Target.GameRoot;
        using var capability = fileSystem.OpenRoot(
            evidence.CanonicalPath,
            FileSystemOpenPurpose.MigrationTarget,
            cancellationToken);
        var volume = fileSystem.InspectVolume(capability, cancellationToken);
        if (!capability.IsLocalVolume ||
            capability.IsNetworkRedirected ||
            !volume.IsLocalVolume ||
            volume.IsNetworkRedirected ||
            capability.Identity != evidence.Identity)
        {
            throw new IOException("The target root is not the retained local physical directory.");
        }

        var root = CreateFile(
            evidence.CanonicalPath,
            RootDirectoryAccess,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (root.IsInvalid)
        {
            root.Dispose();
            throw NativeFailure("The target root could not be opened through a retained Windows handle.");
        }

        try
        {
            ValidateObject(root, directory: true);
            var identity = ReadDirectoryIdentity(root);
            if (identity != evidence.Identity ||
                !SameFinalPath(ReadFinalPath(root), capability.FinalPath))
            {
                throw new IOException("The target root identity changed while opening the writer.");
            }

            return new TransactionRootLease(
                () => AuthorityIsActive(authority),
                authority.Plan.WriteAllowlist,
                Duplicate(root),
                identity,
                ReadFinalPath(root));
        }
        finally
        {
            root.Dispose();
        }
    }

    public TransactionRootLease OpenRecoveryTargetRoot(
        RecoveryExecutionAuthority authority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!authority.IsActive)
        {
            throw new InvalidOperationException("The recovery authority is no longer active.");
        }

        var locator = authority.Locator;
        using var capability = fileSystem.OpenRoot(
            locator.CanonicalTargetRoot,
            FileSystemOpenPurpose.MigrationTarget,
            cancellationToken);
        var volume = fileSystem.InspectVolume(capability, cancellationToken);
        if (!capability.IsLocalVolume ||
            capability.IsNetworkRedirected ||
            !volume.IsLocalVolume ||
            volume.IsNetworkRedirected ||
            capability.Identity != locator.TargetRootIdentity)
        {
            throw new IOException("The recovery target is not the authenticated local physical directory.");
        }

        var root = CreateFile(
            locator.CanonicalTargetRoot,
            RootDirectoryAccess,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (root.IsInvalid)
        {
            root.Dispose();
            throw NativeFailure("The recovery target root could not be retained.");
        }

        try
        {
            ValidateObject(root, directory: true);
            var identity = ReadDirectoryIdentity(root);
            if (identity != locator.TargetRootIdentity ||
                !SameFinalPath(ReadFinalPath(root), capability.FinalPath))
            {
                throw new IOException("The recovery target root changed while opening.");
            }

            return new TransactionRootLease(
                () => authority.IsActive,
                authority.WriteAllowlist,
                Duplicate(root),
                identity,
                ReadFinalPath(root));
        }
        finally
        {
            root.Dispose();
        }
    }

    internal TransactionRootLease OpenReadOnlyTargetRoot(
        RecoveryReadOnlyContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsActive)
        {
            throw new InvalidOperationException("The read-only recovery context is no longer active.");
        }

        var locator = context.Locator;
        using var capability = fileSystem.OpenRoot(
            locator.CanonicalTargetRoot,
            FileSystemOpenPurpose.MigrationTarget,
            cancellationToken);
        var volume = fileSystem.InspectVolume(capability, cancellationToken);
        if (!capability.IsLocalVolume ||
            capability.IsNetworkRedirected ||
            !volume.IsLocalVolume ||
            volume.IsNetworkRedirected ||
            capability.Identity != locator.TargetRootIdentity)
        {
            throw new IOException("The eligibility target is not the authenticated local physical directory.");
        }

        var root = CreateFile(
            locator.CanonicalTargetRoot,
            ReadOnlyDirectoryAccess,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (root.IsInvalid)
        {
            root.Dispose();
            throw NativeFailure("The eligibility target root could not be retained read-only.");
        }

        try
        {
            ValidateObject(root, directory: true);
            var identity = ReadDirectoryIdentity(root);
            if (identity != locator.TargetRootIdentity ||
                !SameFinalPath(ReadFinalPath(root), capability.FinalPath))
            {
                throw new IOException("The eligibility target root changed while opening.");
            }

            return new TransactionRootLease(
                () => context.IsActive,
                context.AuthorizedPaths,
                Duplicate(root),
                identity,
                ReadFinalPath(root));
        }
        finally
        {
            root.Dispose();
        }
    }

    public IReadOnlyList<NormalizedRelativePath> FindMissingParentDirectories(
        TransactionRootLease target,
        NormalizedRelativePath filePath,
        CancellationToken cancellationToken)
    {
        ValidateLease(target);
        var normalized = RequireNormalized(filePath);
        EnsureWriteAllowed(target, normalized);
        var missing = new List<NormalizedRelativePath>();
        var current = Duplicate(target.RootHandle);
        try
        {
            for (var index = 0; index < normalized.Segments.Count - 1; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var prefix = string.Join('\\', normalized.Segments.Take(index + 1));
                if (missing.Count > 0)
                {
                    missing.Add(NormalizeRequired(prefix));
                    continue;
                }

                var next = OpenRelative(
                    current,
                    normalized.Segments[index],
                    directory: true,
                    TraversalDirectoryAccess,
                    FileShareRead | FileShareWrite | FileShareDelete,
                    FileOpen,
                    allowMissing: true,
                    out var segmentMissing);
                if (segmentMissing || next is null)
                {
                    missing.Add(NormalizeRequired(prefix));
                    continue;
                }

                current.Dispose();
                current = next;
            }

            return Array.AsReadOnly(missing.ToArray());
        }
        finally
        {
            current.Dispose();
        }
    }

    public CreatedDirectory? TryOpenDirectory(
        TransactionRootLease target,
        NormalizedRelativePath directory,
        string opaqueObjectId,
        CancellationToken cancellationToken)
    {
        ValidateLease(target);
        var normalized = RequireNormalized(directory);
        EnsureDirectoryAllowed(target, normalized);
        TransactionValueValidation.RequireOpaqueId(opaqueObjectId, nameof(opaqueObjectId));
        using var parent = OpenParent(target.RootHandle, normalized, out var leaf, cancellationToken);
        var handle = OpenRelative(
            parent,
            leaf,
            directory: true,
            CreatedDirectoryAccess,
            FileShareRead | FileShareWrite,
            FileOpen,
            allowMissing: true,
            out var missing);
        if (missing || handle is null)
        {
            return null;
        }

        try
        {
            var result = new CreatedDirectory(
                opaqueObjectId,
                normalized,
                ReadFinalPath(handle),
                handle,
                ReadDirectoryIdentity(handle));
            handle = null!;
            return result;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    public CreatedDirectory CreateDirectory(
        TransactionRootLease target,
        NormalizedRelativePath directory,
        JournalMutationPermit directoryIntent,
        CancellationToken cancellationToken)
    {
        ValidateLease(target);
        var normalized = RequireNormalized(directory);
        EnsureDirectoryAllowed(target, normalized);
        using var parent = OpenParent(target.RootHandle, normalized, out var leaf, cancellationToken);
        using (var existing = OpenRelative(
                   parent,
                   leaf,
                   directory: true,
                   DirectoryAccess,
                   FileShareRead | FileShareWrite | FileShareDelete,
                   FileOpen,
                   allowMissing: true,
                   out var missing))
        {
            if (!missing || existing is not null)
            {
                throw new IOException("A planned target directory appeared before create-new.");
            }
        }

        target.ConsumePermit(
            directoryIntent,
            TransactionRecordKind.DirectoryIntent,
            directoryIntent.OpaqueObjectId,
            normalized);
        var created = OpenRelative(
            parent,
            leaf,
            directory: true,
            CreatedDirectoryAccess,
            FileShareRead | FileShareWrite,
            FileCreate,
            allowMissing: false,
            out _) ?? throw new IOException("The target directory could not be created.");
        try
        {
            MarkDelete(created);
            Flush(parent);
            var retainedPath = ReadFinalPath(created);
            raceBoundaryHook.Hit(
                TransactionRaceBoundary.DirectoryNamespaceCreated,
                retainedPath);
            return new CreatedDirectory(
                directoryIntent.OpaqueObjectId,
                normalized,
                retainedPath,
                created,
                ReadDirectoryIdentity(created));
        }
        catch
        {
            created.Dispose();
            throw;
        }
    }

    public void PersistCreatedDirectory(
        TransactionRootLease target,
        CreatedDirectory created,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(created);
        ValidateLease(target);
        cancellationToken.ThrowIfCancellationRequested();
        var path = RequireNormalized(created.RelativePath);
        EnsureDirectoryAllowed(target, path);
        if (ReadDirectoryIdentity(created.Handle) != created.Identity ||
            !SameFinalPath(ReadFinalPath(created.Handle), created.RetainedPath) ||
            WindowsFileSystemCapability.EnumerateChildNames(
                created.Handle,
                1,
                cancellationToken).Count != 0)
        {
            throw new IOException(
                "The provisional transaction directory changed before persistence.");
        }

        ClearDelete(created.Handle);
    }

    public BackupObject BackupExisting(
        TransactionRootLease target,
        PlannedFileChange change,
        JournalMutationPermit backupIntent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        ValidateLease(target);
        var path = RequireNormalized(change.RelativePath);
        EnsureWriteAllowed(target, path);
        using var file = OpenFileForInspection(target.RootHandle, path, allowMissing: false, out _);
        var captured = CaptureFile(file, out var bytes, cancellationToken);
        try
        {
            RequireMatchesPlannedSnapshot(change.TargetSnapshot, captured);
            target.ConsumePermit(
                backupIntent,
                TransactionRecordKind.BackupIntent,
                backupIntent.OpaqueObjectId,
                path);
            backupStore.WriteVerified(
                backupIntent.OpaqueObjectId,
                bytes,
                captured,
                cancellationToken);
            return new BackupObject(backupIntent.OpaqueObjectId, path, captured);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public StagedObject Stage(
        TransactionRootLease target,
        StagedFileMutation mutation,
        JournalMutationPermit stageIntent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ValidateLease(target);
        var path = RequireNormalized(mutation.Change.RelativePath);
        EnsureWriteAllowed(target, path);
        var bytes = mutation.AfterBytes.CopyBytes();
        try
        {
            EnsureTargetSpace(target, bytes.Length);
            using var parent = OpenParent(target.RootHandle, path, out _, cancellationToken);
            var stageName = TemporaryName(
                stageIntent.TransactionId,
                stageIntent.OpaqueObjectId,
                "stage");
            target.ConsumePermit(
                stageIntent,
                TransactionRecordKind.StageIntent,
                stageIntent.OpaqueObjectId,
                path);
            var stagedWriter = OpenRelative(
                parent,
                stageName,
                directory: false,
                MutableFileAccess,
                FileShareRead,
                FileCreate,
                allowMissing: false,
                out _) ?? throw new IOException("The same-directory stage object could not be created.");
            try
            {
                RandomAccess.Write(stagedWriter, bytes, 0);
                RandomAccess.FlushToDisk(stagedWriter);
                Flush(parent);
                var provisionalMetadata = CaptureFile(stagedWriter, out var provisionalBytes, cancellationToken);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(bytes, provisionalBytes))
                    {
                        throw new IOException("The staged object did not reread exactly.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(provisionalBytes);
                }

                var retainedPath = ReadFinalPath(stagedWriter);
                stagedWriter.Dispose();
                var staged = OpenAbsoluteFile(
                    retainedPath,
                    RetainedFileAccess,
                    FileShareRead);
                try
                {
                    var metadata = CaptureFile(staged, out var reread, cancellationToken);
                    try
                    {
                        if (metadata.Identity != provisionalMetadata.Identity ||
                            !CryptographicOperations.FixedTimeEquals(bytes, reread) ||
                            !SameFinalPath(ReadFinalPath(staged), retainedPath))
                        {
                            throw new IOException(
                                "The staged object changed while its write-finalized handle was reopened.");
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(reread);
                    }

                    return new StagedObject(
                        stageIntent.OpaqueObjectId,
                        path,
                        retainedPath,
                        staged,
                        metadata);
                }
                catch
                {
                    staged.Dispose();
                    throw;
                }
            }
            finally
            {
                stagedWriter.Dispose();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public ReplaceOutcome ReplaceExisting(
        TransactionRootLease target,
        StagedObject staged,
        ExpectedTargetObject expected,
        JournalMutationPermit commitIntent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(expected);
        ValidateLease(target);
        var path = RequireNormalized(staged.RelativePath);
        if (!string.Equals(path.Value, expected.RelativePath.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The staged and expected target paths did not match.");
        }

        EnsureWriteAllowed(target, path);
        using var parent = OpenParent(target.RootHandle, path, out _, cancellationToken);
        using var current = OpenFileForInspection(
            target.RootHandle,
            path,
            allowMissing: false,
            out _,
            shareMode: FileShareRead | FileShareWrite | FileShareDelete,
            desiredAccess: MutableFileAccess);
        var currentMetadata = CaptureFile(current, out var currentBytes, cancellationToken);
        CryptographicOperations.ZeroMemory(currentBytes);
        if (currentMetadata.Identity != expected.Metadata.Identity ||
            !currentMetadata.SemanticallyEquals(expected.Metadata))
        {
            throw new IOException("The target file changed after backup verification.");
        }

        var stagePath = staged.RetainedPath;
        var stagedMetadata = staged.Metadata;
        var stageIdentity = stagedMetadata.Identity;
        using var retainedStageIdentity = OpenAbsoluteFile(
            stagePath,
            IdentityFileAccess,
            FileShareRead | FileShareWrite | FileShareDelete);
        if (ReadFileIdentity(staged.Handle) != stageIdentity ||
            ReadFileIdentity(retainedStageIdentity) != stageIdentity ||
            !SameFinalPath(ReadFinalPath(retainedStageIdentity), stagePath))
        {
            throw new IOException("The retained stage identity changed before replacement.");
        }

        staged.Dispose();
        using var operationStage = OpenAbsoluteFile(
            stagePath,
            MutableFileAccess,
            FileShareRead | FileShareWrite | FileShareDelete);
        var operationStageMetadata = CaptureFile(operationStage, out var stageBytes, cancellationToken);
        CryptographicOperations.ZeroMemory(stageBytes);
        if (operationStageMetadata.Identity != stageIdentity ||
            !operationStageMetadata.SemanticallyEquals(stagedMetadata) ||
            !SameFinalPath(ReadFinalPath(operationStage), stagePath))
        {
            throw new IOException("The retained stage object changed before replacement.");
        }

        var displacedPath = Path.Combine(
            Path.GetDirectoryName(currentMetadataPath(current))!,
            TemporaryName(
                commitIntent.TransactionId,
                commitIntent.OpaqueObjectId,
                "displaced"));
        var currentPath = currentMetadataPath(current);
        var replacementPath = currentMetadataPath(operationStage);
        var expectedPostReplaceMetadata = CreateExpectedPostReplaceMetadata(
            stagedMetadata,
            currentMetadata);
        operationStage.Dispose();
        target.ConsumePermit(
            commitIntent,
            TransactionRecordKind.CommitIntent,
            staged.OpaqueObjectId,
            path);
        if (!ReplaceFile(
                currentPath,
                replacementPath,
                displacedPath,
                0,
                IntPtr.Zero,
                IntPtr.Zero))
        {
            throw NativeFailure("The existing target file could not be atomically replaced.");
        }

        Flush(parent);
        raceBoundaryHook.Hit(
            TransactionRaceBoundary.NormalReplaceBeforeMetadataAuthentication,
            currentPath);
        current.Dispose();
        operationStage.Dispose();
        using var mutableFinal = OpenAuthenticatedReplacementForMetadata(
            retainedStageIdentity,
            currentPath,
            expectedPostReplaceMetadata,
            cancellationToken,
            MetadataFileAccess,
            currentMetadata);
        ApplyMetadata(mutableFinal, expected.Metadata);
        RandomAccess.FlushToDisk(mutableFinal);

        var finalHandle = ReopenFile(
            mutableFinal,
            RetainedFileAccess,
            FileShareRead,
            0);
        if (finalHandle.IsInvalid)
        {
            finalHandle.Dispose();
            throw NativeFailure("The authenticated final object could not be retained read-only.");
        }

        ValidateObject(finalHandle, directory: false);
        var displacedHandle = OpenAbsoluteFile(
            displacedPath,
            RetainedFileAccess,
            FileShareRead);
        try
        {
            var finalMetadata = CaptureFile(finalHandle, out var finalBytes, cancellationToken);
            var displacedMetadata = CaptureFile(displacedHandle, out var displacedBytes, cancellationToken);
            try
            {
                var identityMatches =
                    finalMetadata.Identity == stageIdentity &&
                    finalMetadata.Identity == ReadFileIdentity(retainedStageIdentity) &&
                    finalMetadata.Identity == ReadFileIdentity(mutableFinal) &&
                    SameFinalPath(ReadFinalPath(mutableFinal), currentPath) &&
                    SameFinalPath(ReadFinalPath(finalHandle), currentPath);
                var contentMatches = string.Equals(
                    finalMetadata.Sha256,
                    stagedMetadata.Sha256,
                    StringComparison.Ordinal);
                var metadataMatches = MetadataWasPreserved(expected.Metadata, finalMetadata);
                if (!identityMatches || !contentMatches || !metadataMatches)
                {
                    throw new IOException(
                        "The replacement object failed final verification " +
                        $"(identity={identityMatches}, content={contentMatches}, metadata={metadataMatches}, " +
                        $"creation={expected.Metadata.CreationTimeUtc == finalMetadata.CreationTimeUtc}, " +
                        $"access={expected.Metadata.LastAccessTimeUtc == finalMetadata.LastAccessTimeUtc}, " +
                        $"write={expected.Metadata.LastWriteTimeUtc == finalMetadata.LastWriteTimeUtc}, " +
                        $"attributes={expected.Metadata.Attributes == finalMetadata.Attributes}, " +
                        $"links={expected.Metadata.LinkCount == finalMetadata.LinkCount}, " +
                        $"security={FileMetadataSnapshot.SecurityDescriptorsSemanticallyEqual(expected.Metadata.SecurityDescriptor, finalMetadata.SecurityDescriptor)}, " +
                        $"security-detail={FileMetadataSnapshot.DescribeSecurityDescriptorDifference(expected.Metadata.SecurityDescriptor, finalMetadata.SecurityDescriptor)}, " +
                        $"streams={expected.Metadata.StreamNames.SequenceEqual(finalMetadata.StreamNames, StringComparer.Ordinal)}).");
                }

                var replacement = new CommittedObject(
                    staged.OpaqueObjectId,
                    path,
                    ReadFinalPath(finalHandle),
                    finalHandle,
                    finalMetadata);
                var displaced = new DisplacedObject(
                    staged.OpaqueObjectId,
                    path,
                ReadFinalPath(displacedHandle),
                replacement.RetainedPath,
                displacedHandle,
                displacedMetadata,
                finalMetadata);
                var displacedMatches =
                    displacedMetadata.Identity == expected.Metadata.Identity &&
                    displacedMetadata.SemanticallyEquals(expected.Metadata);
                finalHandle = null!;
                displacedHandle = null!;
                return new ReplaceOutcome(replacement, displaced, displacedMatches);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(finalBytes);
                CryptographicOperations.ZeroMemory(displacedBytes);
            }
        }
        finally
        {
            finalHandle?.Dispose();
            displacedHandle?.Dispose();
        }
    }

    public CommittedObject CreateMissing(
        TransactionRootLease target,
        StagedObject staged,
        JournalMutationPermit commitIntent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ValidateLease(target);
        var path = RequireNormalized(staged.RelativePath);
        EnsureWriteAllowed(target, path);
        using var parent = OpenParent(target.RootHandle, path, out var leaf, cancellationToken);
        using (var existing = OpenRelative(
                   parent,
                   leaf,
                   directory: false,
                   GenericRead | FileReadAttributes | Synchronize,
                   FileShareRead | FileShareWrite | FileShareDelete,
                   FileOpen,
                   allowMissing: true,
                   out var missing))
        {
            if (!missing || existing is not null)
            {
                throw new IOException("A planned missing target appeared before create-new.");
            }
        }

        var stagePath = staged.RetainedPath;
        var stageIdentity = staged.Metadata.Identity;
        staged.Dispose();
        using var operationStage = OpenAbsoluteFile(
            stagePath,
            MutableFileAccess,
            FileShareRead | FileShareWrite | FileShareDelete);
        var stageMetadata = CaptureFile(operationStage, out var stageBytes, cancellationToken);
        CryptographicOperations.ZeroMemory(stageBytes);
        if (stageMetadata.Identity != stageIdentity ||
            !string.Equals(stageMetadata.Sha256, staged.Metadata.Sha256, StringComparison.Ordinal))
        {
            throw new IOException("The retained stage object changed before create-new.");
        }

        target.ConsumePermit(
            commitIntent,
            TransactionRecordKind.CommitIntent,
            staged.OpaqueObjectId,
            path);
        RenameRelative(operationStage, parent, leaf);
        ApplyMetadata(operationStage, staged.Metadata);
        Flush(parent);
        operationStage.Dispose();
        var final = OpenFileByRelativePath(
            target.RootHandle,
            path,
            RetainedFileAccess,
            FileShareRead);
        try
        {
            var metadata = CaptureFile(final, out var bytes, cancellationToken);
            CryptographicOperations.ZeroMemory(bytes);
            if (metadata.Identity != stageIdentity ||
                !metadata.StableStateEquals(staged.Metadata))
            {
                throw new IOException(
                    "The create-new final name did not retain the authenticated staged state.");
            }

            var result = new CommittedObject(
                staged.OpaqueObjectId,
                path,
                ReadFinalPath(final),
                final,
                metadata);
            final = null!;
            return result;
        }
        finally
        {
            final?.Dispose();
        }
    }

    public VerifiedObject Reread(
        TransactionRootLease target,
        NormalizedRelativePath path,
        CancellationToken cancellationToken) =>
        RereadCore(target, path, readOnlyTraversal: false, cancellationToken);

    internal static VerifiedObject RereadReadOnly(
        TransactionRootLease target,
        NormalizedRelativePath path,
        CancellationToken cancellationToken) =>
        RereadCore(target, path, readOnlyTraversal: true, cancellationToken);

    private static VerifiedObject RereadCore(
        TransactionRootLease target,
        NormalizedRelativePath path,
        bool readOnlyTraversal,
        CancellationToken cancellationToken)
    {
        ValidateLease(target);
        var normalized = RequireNormalized(path);
        EnsureWriteAllowed(target, normalized);
        var handle = OpenFileByRelativePath(
            target.RootHandle,
            normalized,
            RetainedFileAccess,
            FileShareRead,
            readOnlyTraversal);
        try
        {
            var metadata = CaptureFile(handle, out var bytes, cancellationToken);
            CryptographicOperations.ZeroMemory(bytes);
            var opaqueMaterial = Encoding.UTF8.GetBytes(normalized.Value);
            string opaque;
            try
            {
                opaque = "reread-" + Convert.ToHexString(SHA256.HashData(opaqueMaterial))[..16];
            }
            finally
            {
                CryptographicOperations.ZeroMemory(opaqueMaterial);
            }

            var result = new VerifiedObject(
                opaque,
                normalized,
                ReadFinalPath(handle),
                handle,
                metadata);
            handle = null!;
            return result;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    public VerifiedObject? TryOpenTemporary(
        TransactionRootLease target,
        NormalizedRelativePath finalPath,
        TransactionId transactionId,
        string opaqueObjectId,
        string suffix,
        CancellationToken cancellationToken)
    {
        ValidateLease(target);
        var normalized = RequireNormalized(finalPath);
        EnsureWriteAllowed(target, normalized);
        SafeFileHandle parent;
        try
        {
            parent = OpenParent(target.RootHandle, normalized, out _, cancellationToken);
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        using (parent)
        {
            var name = TemporaryName(transactionId, opaqueObjectId, suffix);
            var handle = OpenRelative(
                parent,
                name,
                directory: false,
                RetainedFileAccess,
                FileShareRead,
                FileOpen,
                allowMissing: true,
                out var missing);
            if (missing || handle is null)
            {
                return null;
            }

            try
            {
                var metadata = CaptureFile(handle, out var bytes, cancellationToken);
                CryptographicOperations.ZeroMemory(bytes);
                var result = new VerifiedObject(
                    opaqueObjectId,
                    normalized,
                    ReadFinalPath(handle),
                    handle,
                    metadata);
                handle = null!;
                return result;
            }
            finally
            {
                handle?.Dispose();
            }
        }
    }

    public void RestoreDisplaced(
        TransactionRootLease target,
        DisplacedObject displaced,
        JournalMutationPermit rollbackIntent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(displaced);
        ValidateLease(target);
        var path = RequireNormalized(displaced.RelativePath);
        EnsureWriteAllowed(target, path);
        var displacedPath = displaced.RetainedPath;
        var expected = displaced.Metadata;
        using var retainedDisplacedIdentity = OpenAbsoluteFile(
            displacedPath,
            IdentityFileAccess,
            FileShareRead | FileShareWrite | FileShareDelete);
        if (ReadFileIdentity(displaced.Handle) != expected.Identity ||
            ReadFileIdentity(retainedDisplacedIdentity) != expected.Identity)
        {
            throw new IOException("The retained displaced identity changed before rollback.");
        }

        using var retainedCurrentFinalIdentity = OpenAbsoluteFile(
            displaced.FinalPath,
            IdentityFileAccess,
            FileShareRead | FileShareWrite | FileShareDelete);
        if (ReadFileIdentity(displaced.LinkedReplacementHandle) != displaced.ExpectedFinalMetadata.Identity ||
            ReadFileIdentity(retainedCurrentFinalIdentity) != displaced.ExpectedFinalMetadata.Identity)
        {
            throw new IOException("The retained replacement identity changed before rollback.");
        }

        displaced.ReleaseRetainedObjectsForRollback();
        using var retainedDisplaced = OpenAbsoluteFile(
            displacedPath,
            MutableFileAccess,
            FileShareRead | FileShareWrite | FileShareDelete);
        var currentDisplaced = CaptureFile(retainedDisplaced, out var displacedBytes, cancellationToken);
        CryptographicOperations.ZeroMemory(displacedBytes);
        if (currentDisplaced.Identity != expected.Identity ||
            !currentDisplaced.SemanticallyEquals(expected))
        {
            throw new IOException("The displaced object changed before rollback.");
        }

        using var parent = OpenParent(target.RootHandle, path, out _, cancellationToken);
        using var currentFinal = OpenFileByRelativePath(
            target.RootHandle,
            path,
            MutableFileAccess,
            FileShareRead | FileShareWrite | FileShareDelete);
        var observedFinal = CaptureFile(currentFinal, out var finalBytes, cancellationToken);
        CryptographicOperations.ZeroMemory(finalBytes);
        if (observedFinal.Identity != displaced.ExpectedFinalMetadata.Identity ||
            !observedFinal.SemanticallyEquals(displaced.ExpectedFinalMetadata))
        {
            throw new IOException("The rollback target changed after commit verification.");
        }

        var currentFinalPath = ReadFinalPath(currentFinal);
        var replacementPath = ReadFinalPath(retainedDisplaced);
        target.ConsumePermit(
            rollbackIntent,
            TransactionRecordKind.RollbackIntent,
            displaced.OpaqueObjectId,
            path);
        currentFinal.Dispose();
        raceBoundaryHook.Hit(
            TransactionRaceBoundary.RestoreDisplacedAfterComparison,
            currentFinalPath);
        retainedDisplaced.Dispose();
        var capturedCurrent = ReplaceAndAuthenticateDisplaced(
            parent,
            currentFinalPath,
            replacementPath,
            retainedDisplacedIdentity,
            expected,
            displaced.ExpectedFinalMetadata,
            rollbackIntent.TransactionId,
            rollbackIntent.OpaqueObjectId,
            "rollback-displaced",
            out var authenticatedReplacementMetadata);

        using (capturedCurrent)
        {
            raceBoundaryHook.Hit(
                TransactionRaceBoundary.RestoreDisplacedBeforeMetadataApplication,
                currentFinalPath);
            using var restored = OpenAuthenticatedReplacementForMetadata(
                retainedDisplacedIdentity,
                currentFinalPath,
                authenticatedReplacementMetadata,
                cancellationToken);
            ApplyMetadata(restored, expected);
            RandomAccess.FlushToDisk(restored);
            var restoredMetadata = CaptureFile(
                restored,
                out var restoredBytes,
                cancellationToken);
            CryptographicOperations.ZeroMemory(restoredBytes);
            if (restoredMetadata.Identity != expected.Identity ||
                restoredMetadata.Identity != ReadFileIdentity(retainedDisplacedIdentity) ||
                !restoredMetadata.SemanticallyEquals(expected) ||
                !SameFinalPath(ReadFinalPath(restored), currentFinalPath))
            {
                throw new IOException("The restored target object did not match its authenticated before-state.");
            }

            raceBoundaryHook.Hit(
                TransactionRaceBoundary.RestoreDisplacedCaptureBeforeDelete,
                ReadFinalPath(capturedCurrent));
            var capturedCurrentPath = ReadFinalPath(capturedCurrent);
            capturedCurrent.Dispose();
            DeleteAuthenticatedFile(
                retainedCurrentFinalIdentity,
                capturedCurrentPath,
                displaced.ExpectedFinalMetadata,
                cancellationToken);
        }

        Flush(parent);
    }

    public void DeleteCreatedFile(
        TransactionRootLease target,
        CommittedObject created,
        JournalMutationPermit rollbackIntent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(created);
        ValidateLease(target);
        var path = RequireNormalized(created.RelativePath);
        var expected = created.Metadata;
        using var retainedIdentity = OpenAbsoluteFile(
            created.RetainedPath,
            IdentityFileAccess,
            FileShareRead | FileShareWrite | FileShareDelete);
        if (ReadFileIdentity(created.Handle) != expected.Identity ||
            ReadFileIdentity(retainedIdentity) != expected.Identity)
        {
            throw new IOException("The retained transaction-created identity changed before delete.");
        }

        created.Dispose();
        using var file = OpenFileByRelativePath(
            target.RootHandle,
            path,
            MutableFileAccess,
            FileShareRead | FileShareWrite | FileShareDelete);
        var current = CaptureFile(file, out var bytes, cancellationToken);
        CryptographicOperations.ZeroMemory(bytes);
        if (current.Identity != expected.Identity || !current.SemanticallyEquals(expected))
        {
            throw new IOException("The transaction-created file changed before rollback.");
        }

        var finalPath = ReadFinalPath(file);
        target.ConsumePermit(
            rollbackIntent,
            TransactionRecordKind.RollbackIntent,
            created.OpaqueObjectId,
            path);
        file.Dispose();
        raceBoundaryHook.Hit(
            TransactionRaceBoundary.DeleteCreatedAfterComparison,
            finalPath);
        DeleteAuthenticatedFile(
            retainedIdentity,
            finalPath,
            expected,
            cancellationToken);
        using var parent = OpenParent(target.RootHandle, path, out _, cancellationToken);
        Flush(parent);
        RequireMissing(target.RootHandle, path);
    }

    public void RemoveCreatedDirectory(
        TransactionRootLease target,
        CreatedDirectory created,
        JournalMutationPermit rollbackIntent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(created);
        ValidateLease(target);
        var path = RequireNormalized(created.RelativePath);
        EnsureDirectoryAllowed(target, path);
        if (ReadDirectoryIdentity(created.Handle) != created.Identity ||
            !SameFinalPath(ReadFinalPath(created.Handle), created.RetainedPath) ||
            WindowsFileSystemCapability.EnumerateChildNames(
                created.Handle,
                1,
                cancellationToken).Count != 0)
        {
            throw new IOException("The transaction-created directory changed or is not empty.");
        }

        target.ConsumePermit(
            rollbackIntent,
            TransactionRecordKind.RollbackIntent,
            created.OpaqueObjectId,
            path);
        MarkDelete(created.Handle);
        created.Dispose();
        using var parent = OpenParent(target.RootHandle, path, out _, cancellationToken);
        Flush(parent);
        RequireDirectoryMissing(target.RootHandle, path);
    }

    public void DeleteStagedOrDisplaced(
        TransactionRootLease target,
        VerifiedTransactionObject temporary,
        JournalMutationPermit cleanupIntent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(temporary);
        ValidateLease(target);
        var path = RequireNormalized(temporary.RelativePath);
        var retainedPath = temporary.RetainedPath;
        var expected = temporary.Metadata;
        using var retainedIdentity = OpenAbsoluteFile(
            retainedPath,
            IdentityFileAccess,
            FileShareRead | FileShareWrite | FileShareDelete);
        if (ReadFileIdentity(temporary.Handle) != expected.Identity ||
            ReadFileIdentity(retainedIdentity) != expected.Identity)
        {
            throw new IOException("The retained temporary identity changed before cleanup.");
        }

        temporary.Dispose();
        using (var file = OpenAbsoluteFile(
            retainedPath,
            MutableFileAccess,
            FileShareRead | FileShareWrite | FileShareDelete))
        {
            var current = CaptureFile(file, out var bytes, cancellationToken);
            CryptographicOperations.ZeroMemory(bytes);
            if (current.Identity != expected.Identity || !current.SemanticallyEquals(expected))
            {
                throw new IOException("The temporary transaction object changed before cleanup.");
            }
        }

        target.ConsumePermit(
            cleanupIntent,
            TransactionRecordKind.CleanupIntent,
            temporary.OpaqueObjectId,
            path);
        DeleteAuthenticatedFile(
            retainedIdentity,
            retainedPath,
            expected,
            cancellationToken);
        using var parent = OpenParent(target.RootHandle, path, out _, cancellationToken);
        Flush(parent);
    }

    public void CleanupDisplacedAfterCommit(
        TransactionRootLease target,
        DisplacedObject displaced,
        MigrationTransactionCoordinator.PostCommitCleanupAuthority cleanupAuthority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(displaced);
        ArgumentNullException.ThrowIfNull(cleanupAuthority);
        ValidateLease(target);
        var path = RequireNormalized(displaced.RelativePath);
        EnsureWriteAllowed(target, path);
        var retainedPath = displaced.RetainedPath;
        var expected = displaced.Metadata;
        using var retainedIdentity = OpenAbsoluteFile(
            retainedPath,
            IdentityFileAccess,
            FileShareRead | FileShareWrite | FileShareDelete);
        if (ReadFileIdentity(displaced.Handle) != expected.Identity ||
            ReadFileIdentity(retainedIdentity) != expected.Identity)
        {
            throw new IOException(
                "The retained displaced identity changed before post-commit cleanup.");
        }

        target.ConsumePostCommitCleanupAuthority(cleanupAuthority, displaced);
        displaced.Dispose();
        DeleteAuthenticatedFile(
            retainedIdentity,
            retainedPath,
            expected,
            cancellationToken);
        using var parent = OpenParent(target.RootHandle, path, out _, cancellationToken);
        Flush(parent);
    }

    public void RestoreBackup(
        TransactionRootLease target,
        NormalizedRelativePath path,
        BackupPayload backup,
        VerifiedObject current,
        JournalMutationPermit rollbackIntent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(current);
        ValidateLease(target);
        var normalized = RequireNormalized(path);
        EnsureWriteAllowed(target, normalized);
        if (!string.Equals(
                current.RelativePath.Value,
                normalized.Value,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The captured recovery object belonged to another path.");
        }

        var expectedCurrent = current.Metadata;
        using var retainedCurrentIdentity = OpenAbsoluteFile(
            current.RetainedPath,
            IdentityFileAccess,
            FileShareRead | FileShareWrite | FileShareDelete);
        if (ReadFileIdentity(current.Handle) != expectedCurrent.Identity ||
            ReadFileIdentity(retainedCurrentIdentity) != expectedCurrent.Identity)
        {
            throw new IOException("The authenticated recovery target identity changed before rollback.");
        }

        current.Dispose();
        using var parent = OpenParent(target.RootHandle, normalized, out _, cancellationToken);
        using var currentHandle = OpenFileByRelativePath(
            target.RootHandle,
            normalized,
            MutableFileAccess,
            FileShareRead | FileShareWrite | FileShareDelete);
        var observedCurrent = CaptureFile(currentHandle, out var currentBytes, cancellationToken);
        CryptographicOperations.ZeroMemory(currentBytes);
        if (observedCurrent.Identity != expectedCurrent.Identity ||
            !observedCurrent.SemanticallyEquals(expectedCurrent))
        {
            throw new IOException("The recovery target changed after its authenticated comparison.");
        }

        var currentPath = ReadFinalPath(currentHandle);
        var stageName = TemporaryName(
            rollbackIntent.TransactionId,
            rollbackIntent.OpaqueObjectId,
            "recovery");
        target.ConsumePermit(
            rollbackIntent,
            TransactionRecordKind.RollbackIntent,
            rollbackIntent.OpaqueObjectId,
            normalized);
        var stage = OpenRelative(
            parent,
            stageName,
            directory: false,
            MutableFileAccess,
            FileShareRead | FileShareWrite | FileShareDelete,
            FileCreate,
            allowMissing: false,
            out _) ?? throw new IOException("The recovery stage could not be created.");
        var stagePath = string.Empty;
        FileMetadataSnapshot? stagedMetadata = null;
        SafeFileHandle? retainedStageIdentity = null;
        try
        {
            RandomAccess.Write(stage, backup.Bytes, 0);
            ApplyMetadata(stage, backup.Metadata);
            RandomAccess.FlushToDisk(stage);
            stagePath = ReadFinalPath(stage);
            stagedMetadata = CaptureFile(stage, out var stagedBytes, cancellationToken);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(stagedBytes, backup.Bytes) ||
                    !stagedMetadata.SemanticallyEquals(backup.Metadata))
                {
                    throw new IOException("The recovery stage failed exact reread verification.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(stagedBytes);
            }

            retainedStageIdentity = OpenAbsoluteFile(
                stagePath,
                IdentityFileAccess,
                FileShareRead | FileShareWrite | FileShareDelete);
            if (ReadFileIdentity(retainedStageIdentity) != stagedMetadata.Identity)
            {
                throw new IOException("The retained recovery stage identity changed before replacement.");
            }

            raceBoundaryHook.Hit(
                TransactionRaceBoundary.RecoveryStageReady,
                stagePath);

            currentHandle.Dispose();
            raceBoundaryHook.Hit(
                TransactionRaceBoundary.RestoreBackupAfterComparison,
                currentPath);
            stage.Dispose();
            var capturedCurrent = ReplaceAndAuthenticateDisplaced(
                parent,
                currentPath,
                stagePath,
                retainedStageIdentity,
                stagedMetadata,
                expectedCurrent,
                rollbackIntent.TransactionId,
                rollbackIntent.OpaqueObjectId,
                "recovery-displaced",
                out var authenticatedReplacementMetadata);
            using (capturedCurrent)
            {
                raceBoundaryHook.Hit(
                    TransactionRaceBoundary.RestoreBackupBeforeMetadataApplication,
                    currentPath);
                using var restored = OpenAuthenticatedReplacementForMetadata(
                    retainedStageIdentity,
                    currentPath,
                    authenticatedReplacementMetadata,
                    cancellationToken);
                ApplyMetadata(restored, backup.Metadata);
                RandomAccess.FlushToDisk(restored);
                var restoredMetadata = CaptureFile(restored, out var restoredBytes, cancellationToken);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(restoredBytes, backup.Bytes) ||
                        restoredMetadata.Identity != stagedMetadata.Identity ||
                        restoredMetadata.Identity != ReadFileIdentity(retainedStageIdentity) ||
                        !restoredMetadata.SemanticallyEquals(backup.Metadata) ||
                        !SameFinalPath(ReadFinalPath(restored), currentPath))
                    {
                        throw new IOException("The recovered file did not match its authenticated before-state.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(restoredBytes);
                }

                raceBoundaryHook.Hit(
                    TransactionRaceBoundary.RestoreBackupCaptureBeforeDelete,
                    ReadFinalPath(capturedCurrent));
                var capturedCurrentPath = ReadFinalPath(capturedCurrent);
                capturedCurrent.Dispose();
                DeleteAuthenticatedFile(
                    retainedCurrentIdentity,
                    capturedCurrentPath,
                    expectedCurrent,
                    cancellationToken);
            }

            Flush(parent);
        }
        catch
        {
            stage.Dispose();
            if (!string.IsNullOrEmpty(stagePath) &&
                stagedMetadata is not null &&
                retainedStageIdentity is not null)
            {
                try
                {
                    raceBoundaryHook.Hit(
                        TransactionRaceBoundary.RecoveryStageBeforeDelete,
                        stagePath);
                    DeleteAuthenticatedFile(
                        retainedStageIdentity,
                        stagePath,
                        stagedMetadata,
                        cancellationToken);
                }
                catch (Exception cleanupException)
                    when (cleanupException is IOException or UnauthorizedAccessException)
                {
                    // The authenticated journal remains nonterminal when exact cleanup cannot be proven.
                }
            }

            throw;
        }
        finally
        {
            stage.Dispose();
            retainedStageIdentity?.Dispose();
        }
    }

    public VerifiedObject RestoreMissingBackup(
        TransactionRootLease target,
        NormalizedRelativePath path,
        BackupPayload backup,
        string opaqueObjectId,
        JournalMutationPermit rollbackIntent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ValidateLease(target);
        var normalized = RequireNormalized(path);
        EnsureWriteAllowed(target, normalized);
        TransactionValueValidation.RequireOpaqueId(opaqueObjectId, nameof(opaqueObjectId));
        using var parent = OpenParent(target.RootHandle, normalized, out var leaf, cancellationToken);
        using (var existing = OpenRelative(
                   parent,
                   leaf,
                   directory: false,
                   GenericRead | FileReadAttributes | Synchronize,
                   FileShareRead | FileShareWrite | FileShareDelete,
                   FileOpen,
                   allowMissing: true,
                   out var missing))
        {
            if (!missing || existing is not null)
            {
                throw new IOException("A file appeared at the missing recovery target.");
            }
        }

        target.ConsumePermit(
            rollbackIntent,
            TransactionRecordKind.RollbackIntent,
            opaqueObjectId,
            normalized);
        var stageName = TemporaryName(
            rollbackIntent.TransactionId,
            opaqueObjectId,
            "recovery");
        var stage = OpenRelative(
            parent,
            stageName,
            directory: false,
            MutableFileAccess,
            FileShareRead,
            FileCreate,
            allowMissing: false,
            out _) ?? throw new IOException("The missing-file recovery stage could not be created.");
        try
        {
            RandomAccess.Write(stage, backup.Bytes, 0);
            ApplyMetadata(stage, backup.Metadata);
            RandomAccess.FlushToDisk(stage);
            var stagedMetadata = CaptureFile(stage, out var stagedBytes, cancellationToken);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(stagedBytes, backup.Bytes) ||
                    !stagedMetadata.SemanticallyEquals(backup.Metadata))
                {
                    throw new IOException("The missing-file recovery stage failed verification.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(stagedBytes);
            }

            RenameRelative(stage, parent, leaf);
            Flush(parent);
            var finalMetadata = CaptureFile(stage, out var finalBytes, cancellationToken);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(finalBytes, backup.Bytes) ||
                    !finalMetadata.SemanticallyEquals(backup.Metadata))
                {
                    throw new IOException("The restored missing target failed final verification.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(finalBytes);
            }

            var result = new VerifiedObject(
                opaqueObjectId,
                normalized,
                ReadFinalPath(stage),
                stage,
                finalMetadata);
            stage = null!;
            return result;
        }
        finally
        {
            stage?.Dispose();
        }
    }

    private static uint RootDirectoryAccess =>
        GenericRead |
        GenericWrite |
        Delete |
        FileListDirectory |
        FileAddFile |
        FileAddSubdirectory |
        FileTraverse |
        FileReadAttributes |
        FileWriteAttributes |
        ReadControl |
        Synchronize;

    internal static uint TargetRootAccessContract => RootDirectoryAccess;

    private static uint DirectoryAccess => RootDirectoryAccess;

    private static uint ReadOnlyDirectoryAccess =>
        GenericRead |
        FileListDirectory |
        FileTraverse |
        FileReadAttributes |
        ReadControl |
        Synchronize;

    private static uint TraversalDirectoryAccess => RootDirectoryAccess & ~Delete;

    private static uint CreatedDirectoryAccess => RootDirectoryAccess;

    private static uint MutableFileAccess =>
        GenericRead |
        GenericWrite |
        Delete |
        FileReadAttributes |
        FileWriteAttributes |
        ReadControl |
        WriteDac |
        WriteOwner |
        Synchronize;

    private static uint RetainedFileAccess =>
        GenericRead |
        FileReadAttributes |
        ReadControl |
        Synchronize;

    private static uint MetadataFileAccess =>
        GenericRead |
        FileReadAttributes |
        FileWriteAttributes |
        ReadControl |
        WriteDac |
        WriteOwner |
        Synchronize;

    private static uint IdentityFileAccess => 0;

    private static void ValidateLease(TransactionRootLease target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.EnsureActive();
        ValidateObject(target.RootHandle, directory: true);
        if (ReadDirectoryIdentity(target.RootHandle) != target.Identity ||
            !SameFinalPath(ReadFinalPath(target.RootHandle), target.FinalPath))
        {
            throw new IOException("The retained target-root lease changed identity.");
        }
    }

    private static bool AuthorityIsActive(
        MigrationTransactionCoordinator.ExecutionAuthority authority)
    {
        var plan = authority.Plan;
        return plan.Session.IsActive &&
               plan.ContentLease.IsActive &&
               plan.ContentLease.IsBoundTo(
                   plan.Session,
                   plan.SourceInstanceId,
                   plan.TargetInstanceId) &&
               plan.ContentContext.IsOwnedBy(plan.ContentLease);
    }

    private static NormalizedRelativePath RequireNormalized(NormalizedRelativePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!WritePathGuard.TryNormalize(path.Value, out var normalized) ||
            normalized is null ||
            normalized.Value.Length == 0 ||
            !string.Equals(path.Value, normalized.Value, StringComparison.Ordinal))
        {
            throw new ArgumentException("A single normalized transaction-relative path is required.", nameof(path));
        }

        return normalized;
    }

    private static NormalizedRelativePath RequireNormalized(ContentRelativePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!WritePathGuard.TryNormalize(path, out var normalized) || normalized is null)
        {
            throw new ArgumentException("The content path was not safe for transaction writes.", nameof(path));
        }

        return normalized;
    }

    private static NormalizedRelativePath NormalizeRequired(string value)
    {
        if (!WritePathGuard.TryNormalize(value, out var normalized) || normalized is null)
        {
            throw new IOException("A planned transaction path could not be normalized.");
        }

        return normalized;
    }

    private static string TemporaryName(
        TransactionId transactionId,
        string opaqueObjectId,
        string suffix)
    {
        TransactionValueValidation.RequireId(transactionId);
        TransactionValueValidation.RequireOpaqueId(opaqueObjectId, nameof(opaqueObjectId));
        TransactionValueValidation.RequireOpaqueId(suffix, nameof(suffix));
        var name = $".bf-{transactionId.Value:N}-{opaqueObjectId}.{suffix}";
        if (name.Length > 240)
        {
            throw new IOException("A deterministic transaction temporary name exceeded its bound.");
        }

        return name;
    }

    private static string TemporaryPath(
        string finalPath,
        TransactionId transactionId,
        string opaqueObjectId,
        string suffix) =>
        Path.Combine(
            Path.GetDirectoryName(finalPath) ??
            throw new IOException("A transaction final path had no parent directory."),
            TemporaryName(transactionId, opaqueObjectId, suffix));

    private SafeFileHandle ReplaceAndAuthenticateDisplaced(
        SafeFileHandle parent,
        string finalPath,
        string replacementPath,
        SafeFileHandle retainedReplacementIdentity,
        FileMetadataSnapshot replacementMetadata,
        FileMetadataSnapshot expectedDisplaced,
        TransactionId transactionId,
        string opaqueObjectId,
        string captureSuffix,
        out FileMetadataSnapshot authenticatedReplacementMetadata)
    {
        var capturePath = TemporaryPath(
            finalPath,
            transactionId,
            opaqueObjectId,
            captureSuffix);
        if (ReadFileIdentity(retainedReplacementIdentity) != replacementMetadata.Identity)
        {
            throw new IOException("The retained replacement identity changed before namespace mutation.");
        }

        if (!ReplaceFile(
                finalPath,
                replacementPath,
                capturePath,
                0,
                IntPtr.Zero,
                IntPtr.Zero))
        {
            throw NativeFailure("An authenticated transaction replacement could not capture the displaced object.");
        }

        Flush(parent);
        var captured = OpenAbsoluteFile(
            capturePath,
            MutableFileAccess,
            FileShareRead | FileShareWrite | FileShareDelete);
        try
        {
            var actual = CaptureFile(captured, out var actualBytes, CancellationToken.None);
            CryptographicOperations.ZeroMemory(actualBytes);
            using var actualReplacement = OpenAbsoluteFile(
                finalPath,
                MutableFileAccess,
                FileShareRead | FileShareWrite | FileShareDelete);
            var actualReplacementMetadata = CaptureFile(
                actualReplacement,
                out var actualReplacementBytes,
                CancellationToken.None);
            CryptographicOperations.ZeroMemory(actualReplacementBytes);
            authenticatedReplacementMetadata = actualReplacementMetadata;
            var replacementMatches =
                actualReplacementMetadata.Identity == ReadFileIdentity(retainedReplacementIdentity) &&
                actualReplacementMetadata.Length == replacementMetadata.Length &&
                string.Equals(
                    actualReplacementMetadata.Sha256,
                    replacementMetadata.Sha256,
                    StringComparison.Ordinal);
            if (actual.Identity == expectedDisplaced.Identity &&
                actual.SemanticallyEquals(expectedDisplaced) &&
                replacementMatches)
            {
                return captured;
            }

            RestoreUnexpectedDisplaced(
                parent,
                finalPath,
                actualReplacementMetadata,
                replacementMatches,
                captured,
                actual,
                transactionId,
                opaqueObjectId);
            throw new IOException(
                "The actual displaced object did not match the authenticated transaction state; " +
                "the raced object was restored.");
        }
        catch
        {
            captured.Dispose();
            throw;
        }
    }

    private void RestoreUnexpectedDisplaced(
        SafeFileHandle parent,
        string finalPath,
        FileMetadataSnapshot initialExpectedFinalMetadata,
        bool initialExpectedFinalIsTransactionOwned,
        SafeFileHandle firstUnexpected,
        FileMetadataSnapshot firstUnexpectedMetadata,
        TransactionId transactionId,
        string opaqueObjectId)
    {
        var retained = new List<SafeFileHandle> { firstUnexpected };
        var restore = firstUnexpected;
        var restoreMetadata = firstUnexpectedMetadata;
        var expectedFinalMetadata = initialExpectedFinalMetadata;
        var expectedFinalIsTransactionOwned = initialExpectedFinalIsTransactionOwned;
        try
        {
            for (var attempt = 0; ; attempt = checked(attempt + 1))
            {
                var restorePath = ReadFinalPath(restore);
                var restoreIdentity = OpenAbsoluteFile(
                    restorePath,
                    IdentityFileAccess,
                    FileShareRead | FileShareWrite | FileShareDelete);
                retained.Add(restoreIdentity);
                if (ReadFileIdentity(restoreIdentity) != restoreMetadata.Identity)
                {
                    throw new IOException("A raced target capture changed before restoration.");
                }

                restore.Dispose();
                var repairCapturePath = TemporaryPath(
                    finalPath,
                    transactionId,
                    opaqueObjectId,
                    $"repair-{attempt:D8}");
                if (!ReplaceFile(
                        finalPath,
                        restorePath,
                        repairCapturePath,
                        0,
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw NativeFailure("A raced target object could not be safely restored.");
                }

                Flush(parent);
                var repairDisplaced = OpenAbsoluteFile(
                    repairCapturePath,
                    MutableFileAccess,
                    FileShareRead | FileShareWrite | FileShareDelete);
                retained.Add(repairDisplaced);
                var actualFinal = CaptureFile(
                    repairDisplaced,
                    out var actualFinalBytes,
                    CancellationToken.None);
                CryptographicOperations.ZeroMemory(actualFinalBytes);
                using var restoredFinal = OpenAbsoluteFile(
                    finalPath,
                    MutableFileAccess,
                    FileShareRead | FileShareWrite | FileShareDelete);
                var restoredFinalMetadata = CaptureFile(
                    restoredFinal,
                    out var restoredFinalBytes,
                    CancellationToken.None);
                CryptographicOperations.ZeroMemory(restoredFinalBytes);
                if (ReadFileIdentity(restoreIdentity) != restoreMetadata.Identity ||
                    restoredFinalMetadata.Identity != restoreMetadata.Identity ||
                    !restoredFinalMetadata.SemanticallyEquals(restoreMetadata) ||
                    !SameFinalPath(ReadFinalPath(restoreIdentity), finalPath))
                {
                    throw new IOException("A raced target object failed restoration verification.");
                }

                if (actualFinal.Identity == expectedFinalMetadata.Identity &&
                    actualFinal.SemanticallyEquals(expectedFinalMetadata))
                {
                    if (expectedFinalIsTransactionOwned)
                    {
                        var repairDisplacedPath = ReadFinalPath(repairDisplaced);
                        var repairDisplacedIdentity = OpenAbsoluteFile(
                            repairDisplacedPath,
                            IdentityFileAccess,
                            FileShareRead | FileShareWrite | FileShareDelete);
                        retained.Add(repairDisplacedIdentity);
                        if (ReadFileIdentity(repairDisplacedIdentity) != actualFinal.Identity)
                        {
                            throw new IOException(
                                "The compensating displaced identity changed before cleanup.");
                        }

                        raceBoundaryHook.Hit(
                            TransactionRaceBoundary.CompensationCaptureBeforeDelete,
                            repairDisplacedPath);
                        repairDisplaced.Dispose();
                        DeleteAuthenticatedFile(
                            repairDisplacedIdentity,
                            repairDisplacedPath,
                            actualFinal,
                            CancellationToken.None);
                    }

                    return;
                }

                expectedFinalMetadata = restoreMetadata;
                expectedFinalIsTransactionOwned = false;
                restore = repairDisplaced;
                restoreMetadata = actualFinal;
            }
        }
        finally
        {
            foreach (var handle in retained)
            {
                handle.Dispose();
            }
        }
    }

    private static void EnsureWriteAllowed(TransactionRootLease target, NormalizedRelativePath path)
    {
        if (!target.WriteAllowlist.Contains(path))
        {
            throw new InvalidOperationException("The transaction path was not in the sealed write allowlist.");
        }
    }

    private static void EnsureDirectoryAllowed(TransactionRootLease target, NormalizedRelativePath directory)
    {
        var prefix = directory.Value + '\\';
        if (!target.WriteAllowlist.Any(path =>
                path.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The directory was not a parent of an allowed transaction path.");
        }
    }

    private static void EnsureTargetSpace(TransactionRootLease target, int bytes)
    {
        if (!GetDiskFreeSpaceEx(target.FinalPath, out var available, out _, out _) ||
            available < (ulong)(bytes + SafetyMarginBytes))
        {
            throw new IOException("The target volume does not have enough verified staging space.");
        }
    }

    private static void RequireMatchesPlannedSnapshot(
        ContentFileSnapshot planned,
        FileMetadataSnapshot actual)
    {
        ArgumentNullException.ThrowIfNull(planned);
        if (!planned.Exists ||
            planned.Identity is not { } identity ||
            actual.Identity != new PhysicalFileIdentity(
                identity.VolumeSerialNumber,
                identity.FileIdLow,
                identity.FileIdHigh) ||
            planned.Length != actual.Length ||
            !string.Equals(planned.Sha256, actual.Sha256, StringComparison.Ordinal) ||
            planned.LastWriteTimeUtc != actual.LastWriteTimeUtc ||
            planned.WindowsFileAttributes != (uint)actual.Attributes)
        {
            throw new IOException("The target file no longer matched the planned snapshot.");
        }
    }

    private static bool MetadataWasPreserved(
        FileMetadataSnapshot before,
        FileMetadataSnapshot after) =>
        before.CreationTimeUtc == after.CreationTimeUtc &&
        before.LastAccessTimeUtc == after.LastAccessTimeUtc &&
        before.LastWriteTimeUtc == after.LastWriteTimeUtc &&
        before.Attributes == after.Attributes &&
        before.LinkCount == after.LinkCount &&
        FileMetadataSnapshot.SecurityDescriptorsSemanticallyEqual(
            before.SecurityDescriptor,
            after.SecurityDescriptor) &&
        before.StreamNames.SequenceEqual(after.StreamNames, StringComparer.Ordinal);

    private static bool MetadataExceptSecuritySemanticallyEquals(
        FileMetadataSnapshot expected,
        FileMetadataSnapshot observed) =>
        expected.Length == observed.Length &&
        string.Equals(expected.Sha256, observed.Sha256, StringComparison.Ordinal) &&
        expected.CreationTimeUtc == observed.CreationTimeUtc &&
        expected.LastAccessTimeUtc == observed.LastAccessTimeUtc &&
        expected.LastWriteTimeUtc == observed.LastWriteTimeUtc &&
        expected.Attributes == observed.Attributes &&
        expected.LinkCount == observed.LinkCount &&
        expected.StreamNames.SequenceEqual(observed.StreamNames, StringComparer.Ordinal);

    private static FileMetadataSnapshot CreateExpectedPostReplaceMetadata(
        FileMetadataSnapshot staged,
        FileMetadataSnapshot displaced) =>
        new(
            staged.Identity,
            staged.Length,
            staged.Sha256,
            displaced.CreationTimeUtc,
            staged.LastAccessTimeUtc,
            staged.LastWriteTimeUtc,
            staged.Attributes,
            staged.LinkCount,
            staged.SecurityDescriptor.ToArray(),
            staged.StreamNames.ToArray());

    private static FileMetadataSnapshot CaptureFile(
        SafeFileHandle handle,
        out byte[] bytes,
        CancellationToken cancellationToken,
        bool allowDeletePending = false)
    {
        ValidateObject(handle, directory: false);
        var basic = ReadBasicInformation(handle);
        var attributes = (FileAttributes)basic.FileAttributes;
        var unsupported = FileAttributes.ReadOnly |
                          FileAttributes.System |
                          FileAttributes.ReparsePoint |
                          FileAttributes.Encrypted |
                          FileAttributes.SparseFile |
                          FileAttributes.Compressed |
                          FileAttributes.Directory |
                          FileAttributes.Device;
        if ((attributes & unsupported) != 0 ||
            basic.NumberOfLinks != 1 && (!allowDeletePending || basic.NumberOfLinks != 0))
        {
            throw new NotSupportedException("The target file metadata is outside BlockFerry v1's safe write subset.");
        }

        bytes = ReadAllBytes(handle, cancellationToken);
        var streams = ReadStreamNames(handle);
        if (streams.Count != 1 || !string.Equals(streams[0], "::$DATA", StringComparison.OrdinalIgnoreCase))
        {
            CryptographicOperations.ZeroMemory(bytes);
            bytes = [];
            throw new NotSupportedException("Alternate data streams are not supported for migration targets.");
        }

        var security = ReadSecurityDescriptor(handle);
        var identity = ReadFileIdentity(handle);
        return new FileMetadataSnapshot(
            identity,
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)),
            FromFileTime(basic.CreationTime),
            FromFileTime(basic.LastAccessTime),
            FromFileTime(basic.LastWriteTime),
            attributes,
            basic.NumberOfLinks,
            security,
            streams);
    }

    private static byte[] ReadAllBytes(SafeFileHandle handle, CancellationToken cancellationToken)
    {
        var length = RandomAccess.GetLength(handle);
        if (length < 0 || length > MaximumFileBytes || length > int.MaxValue)
        {
            throw new IOException("The target file exceeded its fixed transaction bound.");
        }

        var result = new byte[checked((int)length)];
        var offset = 0;
        while (offset < result.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = RandomAccess.Read(handle, result.AsSpan(offset), offset);
            if (read == 0)
            {
                CryptographicOperations.ZeroMemory(result);
                throw new IOException("The target file ended before its retained length.");
            }

            offset += read;
        }

        return result;
    }

    private static ReadOnlyCollection<string> ReadStreamNames(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(MaximumStreamInformationBytes);
        try
        {
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileInfoByHandleClass.FileStreamInfo,
                    buffer,
                    MaximumStreamInformationBytes))
            {
                throw NativeFailure("The target stream list could not be verified.");
            }

            var names = new List<string>();
            var offset = 0;
            while (true)
            {
                if (offset > MaximumStreamInformationBytes - 24)
                {
                    throw new IOException("The target stream information was malformed.");
                }

                var next = unchecked((uint)Marshal.ReadInt32(buffer, offset));
                var nameLength = unchecked((uint)Marshal.ReadInt32(buffer, offset + 4));
                if ((nameLength & 1) != 0 || nameLength > 2048 || nameLength > MaximumStreamInformationBytes - offset - 24)
                {
                    throw new IOException("A target stream name exceeded its bound.");
                }

                var name = Marshal.PtrToStringUni(
                    IntPtr.Add(buffer, offset + 24),
                    checked((int)nameLength / sizeof(char))) ?? string.Empty;
                names.Add(name);
                if (names.Count > 64)
                {
                    throw new IOException("The target stream count exceeded its bound.");
                }

                if (next == 0)
                {
                    break;
                }

                if (next < 24 || next > MaximumStreamInformationBytes - offset)
                {
                    throw new IOException("The target stream chain was malformed.");
                }

                offset = checked(offset + (int)next);
            }

            return Array.AsReadOnly(names.ToArray());
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static byte[] ReadSecurityDescriptor(SafeFileHandle handle)
    {
        var status = GetSecurityInfo(
            handle,
            SecurityObjectType.FileObject,
            OwnerSecurityInformation | GroupSecurityInformation | DaclSecurityInformation,
            out _,
            out _,
            out var dacl,
            out _,
            out var descriptor);
        if (status != 0 || descriptor == IntPtr.Zero || dacl == IntPtr.Zero)
        {
            if (descriptor != IntPtr.Zero)
            {
                _ = LocalFree(descriptor);
            }

            throw new IOException("The target owner/group/DACL could not be captured.");
        }

        try
        {
            var length = GetSecurityDescriptorLength(descriptor);
            if (length == 0 || length > 256 * 1024)
            {
                throw new IOException("The target security descriptor exceeded its bound.");
            }

            var bytes = new byte[checked((int)length)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    private static void ApplyMetadata(SafeFileHandle handle, FileMetadataSnapshot metadata)
    {
        var basic = new FileBasicInfo
        {
            CreationTime = metadata.CreationTimeUtc.ToFileTime(),
            LastAccessTime = metadata.LastAccessTimeUtc.ToFileTime(),
            LastWriteTime = metadata.LastWriteTimeUtc.ToFileTime(),
            ChangeTime = 0,
            FileAttributes = (uint)metadata.Attributes,
        };
        if (!SetFileInformationByHandle(
                handle,
                FileInfoByHandleClass.FileBasicInfo,
                ref basic,
                checked((uint)Marshal.SizeOf<FileBasicInfo>())))
        {
            throw NativeFailure("The target basic metadata could not be restored.");
        }

        var pinned = GCHandle.Alloc(metadata.SecurityDescriptor, GCHandleType.Pinned);
        try
        {
            if (!GetSecurityDescriptorControl(
                    pinned.AddrOfPinnedObject(),
                    out var control,
                    out _))
            {
                throw NativeFailure("The retained target DACL control could not be read.");
            }

            var protection = (control & SecurityDescriptorDaclProtected) != 0
                ? ProtectedDaclSecurityInformation
                : UnprotectedDaclSecurityInformation;
            if (!SetKernelObjectSecurity(
                    handle,
                    OwnerSecurityInformation |
                    GroupSecurityInformation |
                    DaclSecurityInformation |
                    protection,
                    metadata.SecurityDescriptor))
            {
                throw NativeFailure("The target owner/group/DACL could not be restored.");
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    private static SafeFileHandle OpenFileForInspection(
        SafeFileHandle root,
        NormalizedRelativePath path,
        bool allowMissing,
        out bool missing,
        uint shareMode = FileShareRead | FileShareWrite | FileShareDelete,
        uint desiredAccess = GenericRead | FileReadAttributes | ReadControl | Synchronize)
    {
        using var parent = OpenParent(root, path, out var leaf, CancellationToken.None);
        return OpenRelative(
                   parent,
                   leaf,
                   directory: false,
                   desiredAccess,
                   shareMode,
                   FileOpen,
                   allowMissing,
                   out missing) ??
               throw new FileNotFoundException("The target file was missing.");
    }

    private static SafeFileHandle OpenFileByRelativePath(
        SafeFileHandle root,
        NormalizedRelativePath path,
        uint desiredAccess,
        uint shareMode,
        bool readOnlyTraversal = false)
    {
        using var parent = OpenParent(
            root,
            path,
            out var leaf,
            CancellationToken.None,
            readOnlyTraversal);
        return OpenRelative(
                   parent,
                   leaf,
                   directory: false,
                   desiredAccess,
                   shareMode,
                   FileOpen,
                   allowMissing: true,
                   out _) ??
               throw new FileNotFoundException("The target file was missing.");
    }

    private static SafeFileHandle OpenDirectoryByRelativePath(
        SafeFileHandle root,
        NormalizedRelativePath path,
        uint desiredAccess,
        uint shareMode)
    {
        var current = Duplicate(root);
        try
        {
            for (var index = 0; index < path.Segments.Count; index++)
            {
                var next = OpenRelative(
                    current,
                    path.Segments[index],
                    directory: true,
                    index == path.Segments.Count - 1
                        ? desiredAccess
                        : TraversalDirectoryAccess,
                    shareMode,
                    FileOpen,
                    allowMissing: false,
                    out _) ?? throw new DirectoryNotFoundException("A target directory segment was missing.");
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenParent(
        SafeFileHandle root,
        NormalizedRelativePath path,
        out string leaf,
        CancellationToken cancellationToken,
        bool readOnlyTraversal = false)
    {
        if (path.Segments.Count == 0)
        {
            throw new ArgumentException("A target leaf path is required.", nameof(path));
        }

        leaf = path.Segments[^1];
        var current = Duplicate(root);
        try
        {
            for (var index = 0; index < path.Segments.Count - 1; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var next = OpenRelative(
                    current,
                    path.Segments[index],
                    directory: true,
                    readOnlyTraversal ? ReadOnlyDirectoryAccess : TraversalDirectoryAccess,
                    FileShareRead | FileShareWrite | FileShareDelete,
                    FileOpen,
                    allowMissing: false,
                    out _) ?? throw new DirectoryNotFoundException("A target parent directory was missing.");
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenAbsoluteFile(
        string path,
        uint desiredAccess,
        uint shareMode)
    {
        var handle = CreateFile(
            path,
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw NativeFailure("A retained transaction file could not be reopened.");
        }

        ValidateObject(handle, directory: false);
        return handle;
    }

    private static SafeFileHandle OpenAuthenticatedReplacementForMetadata(
        SafeFileHandle retainedIdentity,
        string finalPath,
        FileMetadataSnapshot expected,
        CancellationToken cancellationToken,
        uint? desiredAccess = null,
        FileMetadataSnapshot? replacedDaclSource = null)
    {
        var replacement = ReopenFile(
            retainedIdentity,
            desiredAccess ?? MutableFileAccess,
            FileShareRead,
            0);
        if (replacement.IsInvalid)
        {
            replacement.Dispose();
            throw NativeFailure(
                "The authenticated replacement could not be reopened exclusively for metadata restoration.");
        }

        try
        {
            ValidateObject(replacement, directory: false);
            FileMetadataSnapshot observed;
            byte[] bytes;
            try
            {
                observed = CaptureFile(replacement, out bytes, cancellationToken);
            }
            catch (NotSupportedException exception)
            {
                throw new IOException(
                    "The authenticated replacement left the supported state before metadata restoration.",
                    exception);
            }

            CryptographicOperations.ZeroMemory(bytes);
            using var pathIdentity = OpenAbsoluteFile(
                finalPath,
                IdentityFileAccess,
                FileShareRead | FileShareWrite | FileShareDelete);
            var semanticMatches = replacedDaclSource is null
                ? observed.SemanticallyEquals(expected)
                : MetadataExceptSecuritySemanticallyEquals(expected, observed) &&
                  FileMetadataSnapshot.SecurityDescriptorMatchesReplaceFileMerge(
                      expected.SecurityDescriptor,
                      replacedDaclSource.SecurityDescriptor,
                      observed.SecurityDescriptor);
            if (observed.Identity != expected.Identity ||
                observed.Identity != ReadFileIdentity(retainedIdentity) ||
                observed.Identity != ReadFileIdentity(pathIdentity) ||
                !semanticMatches ||
                !SameFinalPath(ReadFinalPath(pathIdentity), finalPath))
            {
                throw new IOException(
                    "The authenticated replacement changed before metadata restoration " +
                    $"(identity={observed.Identity == expected.Identity}, " +
                    $"retained={observed.Identity == ReadFileIdentity(retainedIdentity)}, " +
                    $"path={observed.Identity == ReadFileIdentity(pathIdentity)}, " +
                    $"content={observed.Length == expected.Length && string.Equals(observed.Sha256, expected.Sha256, StringComparison.Ordinal)}, " +
                    $"creation={observed.CreationTimeUtc == expected.CreationTimeUtc}, " +
                    $"access={observed.LastAccessTimeUtc == expected.LastAccessTimeUtc}, " +
                    $"write={observed.LastWriteTimeUtc == expected.LastWriteTimeUtc}, " +
                    $"attributes={observed.Attributes == expected.Attributes}, " +
                    $"links={observed.LinkCount == expected.LinkCount}, " +
                    $"security={(replacedDaclSource is null ? FileMetadataSnapshot.SecurityDescriptorsSemanticallyEqual(observed.SecurityDescriptor, expected.SecurityDescriptor) : FileMetadataSnapshot.SecurityDescriptorMatchesReplaceFileMerge(expected.SecurityDescriptor, replacedDaclSource.SecurityDescriptor, observed.SecurityDescriptor))}, " +
                    $"streams={observed.StreamNames.SequenceEqual(expected.StreamNames, StringComparer.Ordinal)}, " +
                    $"final-path={SameFinalPath(ReadFinalPath(pathIdentity), finalPath)}).");
            }

            return replacement;
        }
        catch
        {
            replacement.Dispose();
            throw;
        }
    }

    private void DeleteAuthenticatedFile(
        SafeFileHandle retainedIdentity,
        string retainedPath,
        FileMetadataSnapshot expected,
        CancellationToken cancellationToken)
    {
        using var deleteFile = ReopenFile(
            retainedIdentity,
            MutableFileAccess,
            FileShareRead,
            0);
        if (deleteFile.IsInvalid)
        {
            throw NativeFailure(
                "The authenticated transaction object could not be reopened exclusively for delete.");
        }

        ValidateObject(deleteFile, directory: false);
        FileMetadataSnapshot observed;
        byte[] bytes;
        try
        {
            observed = CaptureFile(deleteFile, out bytes, cancellationToken);
        }
        catch (NotSupportedException exception)
        {
            throw new IOException(
                "The authenticated transaction object left the supported state before delete.",
                exception);
        }

        CryptographicOperations.ZeroMemory(bytes);
        using var pathIdentity = OpenAbsoluteFile(
            retainedPath,
            IdentityFileAccess,
            FileShareRead | FileShareWrite | FileShareDelete);
        if (observed.Identity != expected.Identity ||
            observed.Identity != ReadFileIdentity(retainedIdentity) ||
            observed.Identity != ReadFileIdentity(pathIdentity) ||
            !observed.SemanticallyEquals(expected) ||
            !SameFinalPath(ReadFinalPath(pathIdentity), retainedPath))
        {
            throw new IOException(
                "The authenticated transaction object changed at the delete boundary.");
        }

        raceBoundaryHook.Hit(
            TransactionRaceBoundary.AuthenticatedDeleteAfterComparison,
            retainedPath);
        MarkDelete(deleteFile);
        pathIdentity.Dispose();
        retainedIdentity.Dispose();
        deleteFile.Dispose();
        RequireAbsoluteMissing(retainedPath);
    }

    private static void RequireAbsoluteMissing(string path)
    {
        using var unexpected = CreateFile(
            path,
            IdentityFileAccess,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (unexpected.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return;
            }

            throw new IOException(
                $"A deleted transaction object could not be proven absent (Windows error {error}).");
        }

        ValidateObject(unexpected, directory: false);
        throw new IOException("A deleted transaction object still resolved by absolute name.");
    }

    private static SafeFileHandle? OpenRelative(
        SafeFileHandle parent,
        string name,
        bool directory,
        uint desiredAccess,
        uint shareAccess,
        uint disposition,
        bool allowMissing,
        out bool missing)
    {
        missing = false;
        if (!NormalizedRelativePath.TryCreate(name, out var normalized, out _) ||
            normalized is null ||
            normalized.Segments.Count != 1 ||
            !string.Equals(name, normalized.Value, StringComparison.Ordinal))
        {
            throw new ArgumentException("A single normalized relative segment is required.", nameof(name));
        }

        var nameBytes = Encoding.Unicode.GetBytes(name);
        var nameBuffer = Marshal.AllocHGlobal(nameBytes.Length);
        var unicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeUnicodeString>());
        var parentReferenceAdded = false;
        try
        {
            Marshal.Copy(nameBytes, 0, nameBuffer, nameBytes.Length);
            var unicodeString = new NativeUnicodeString
            {
                Length = checked((ushort)nameBytes.Length),
                MaximumLength = checked((ushort)nameBytes.Length),
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
            };
            var options = FileSynchronousIoNonAlert | FileOpenReparsePoint |
                          (directory ? FileDirectoryFile : FileNonDirectoryFile);
            var status = NtCreateFile(
                out var rawHandle,
                desiredAccess,
                ref objectAttributes,
                out _,
                IntPtr.Zero,
                FileAttributeNormal,
                shareAccess,
                disposition,
                options | (disposition == FileCreate ? FileWriteThrough : 0),
                IntPtr.Zero,
                0);
            if (status == StatusSuccess)
            {
                var handle = new SafeFileHandle(rawHandle, ownsHandle: true);
                try
                {
                    ValidateObject(handle, directory);
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

            if (status is StatusNoSuchFile or StatusObjectNameNotFound or StatusObjectPathNotFound)
            {
                if (allowMissing)
                {
                    missing = true;
                    return null;
                }

                throw directory
                    ? new DirectoryNotFoundException(
                        "A target directory segment was missing.")
                    : new FileNotFoundException(
                        "A target file was missing.");
            }

            throw NativeStatusFailure(status, "A handle-relative target operation failed.");
        }
        finally
        {
            if (parentReferenceAdded)
            {
                parent.DangerousRelease();
            }

            CryptographicOperations.ZeroMemory(nameBytes);
            Marshal.FreeHGlobal(unicodeStringPointer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static void RenameRelative(
        SafeFileHandle source,
        SafeFileHandle destinationParent,
        string destinationName)
    {
        var nameBytes = checked(destinationName.Length * sizeof(char));
        var layout = AppStorageRenameLayout.Current;
        var bufferSize = layout.BufferSize(nameBytes);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        var parentReferenceAdded = false;
        try
        {
            Marshal.Copy(new byte[bufferSize], 0, buffer, bufferSize);
            destinationParent.DangerousAddRef(ref parentReferenceAdded);
            Marshal.WriteInt32(buffer, layout.FlagsOffset, 0);
            Marshal.WriteIntPtr(
                buffer,
                layout.RootDirectoryOffset,
                destinationParent.DangerousGetHandle());
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
                throw NativeStatusFailure(status, "The staged create-new rename failed.");
            }
        }
        finally
        {
            if (parentReferenceAdded)
            {
                destinationParent.DangerousRelease();
            }

            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void MarkDelete(SafeFileHandle handle)
    {
        var information = new FileDispositionInfoEx { Flags = FileDispositionFlagDelete };
        if (!SetFileInformationByHandle(
                handle,
                FileInfoByHandleClass.FileDispositionInfoEx,
                ref information,
                checked((uint)Marshal.SizeOf<FileDispositionInfoEx>())))
        {
            throw NativeFailure("A verified transaction object could not be deleted.");
        }
    }

    private static void ClearDelete(SafeFileHandle handle)
    {
        var information = new FileDispositionInfoEx { Flags = 0 };
        if (!SetFileInformationByHandle(
                handle,
                FileInfoByHandleClass.FileDispositionInfoEx,
                ref information,
                checked((uint)Marshal.SizeOf<FileDispositionInfoEx>())))
        {
            throw NativeFailure("A provisional transaction directory could not be persisted.");
        }
    }

    private static void RequireMissing(SafeFileHandle root, NormalizedRelativePath path)
    {
        using var parent = OpenParent(root, path, out var leaf, CancellationToken.None);
        using var unexpected = OpenRelative(
            parent,
            leaf,
            directory: false,
            GenericRead | FileReadAttributes | Synchronize,
            FileShareRead | FileShareWrite | FileShareDelete,
            FileOpen,
            allowMissing: true,
            out var missing);
        if (!missing || unexpected is not null)
        {
            throw new IOException("A deleted transaction object still resolved by name.");
        }
    }

    private static void RequireDirectoryMissing(
        SafeFileHandle root,
        NormalizedRelativePath path)
    {
        using var parent = OpenParent(root, path, out var leaf, CancellationToken.None);
        using var unexpected = OpenRelative(
            parent,
            leaf,
            directory: true,
            DirectoryAccess,
            FileShareRead | FileShareWrite | FileShareDelete,
            FileOpen,
            allowMissing: true,
            out var missing);
        if (!missing || unexpected is not null)
        {
            throw new IOException("A deleted transaction directory still resolved by name.");
        }
    }

    private static void ValidateObject(SafeFileHandle handle, bool directory)
    {
        if (handle.IsInvalid || handle.IsClosed)
        {
            throw new IOException("A retained Windows handle was invalid.");
        }

        var basic = ReadBasicInformation(handle);
        if ((basic.FileAttributes & FileAttributeReparsePoint) != 0 ||
            ((basic.FileAttributes & FileAttributeDirectory) != 0) != directory)
        {
            throw new IOException("A reparse point or unexpected object kind was rejected.");
        }

        _ = ReadFileId(handle);
    }

    private static ByHandleFileInformation ReadBasicInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw NativeFailure("A retained target object's basic information could not be read.");
        }

        return information;
    }

    private static FileIdInfo ReadFileId(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileIdInfo,
                out var information,
                checked((uint)Marshal.SizeOf<FileIdInfo>())))
        {
            throw NativeFailure("A retained target object's full file ID could not be read.");
        }

        return information;
    }

    private static PhysicalDirectoryIdentity ReadDirectoryIdentity(SafeFileHandle handle)
    {
        var id = ReadFileId(handle);
        return new PhysicalDirectoryIdentity(id.VolumeSerialNumber, id.FileId.LowPart, id.FileId.HighPart);
    }

    private static PhysicalFileIdentity ReadFileIdentity(SafeFileHandle handle)
    {
        var id = ReadFileId(handle);
        return new PhysicalFileIdentity(id.VolumeSerialNumber, id.FileId.LowPart, id.FileId.HighPart);
    }

    private static string ReadFinalPath(SafeFileHandle handle)
    {
        var required = GetFinalPathNameByHandle(handle, null, 0, 0);
        if (required == 0 || required > 32_767)
        {
            throw NativeFailure("A retained target object's final path could not be bounded.");
        }

        var buffer = new char[checked((int)required + 1)];
        var written = GetFinalPathNameByHandle(handle, buffer, checked((uint)buffer.Length), 0);
        if (written == 0 || written >= buffer.Length)
        {
            throw NativeFailure("A retained target object's final path could not be read.");
        }

        return new string(buffer, 0, checked((int)written));
    }

    private static string currentMetadataPath(SafeFileHandle handle) => ReadFinalPath(handle);

    private static bool SameFinalPath(string left, string right) =>
        string.Equals(NormalizeFinalPath(left), NormalizeFinalPath(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeFinalPath(string value) =>
        Path.TrimEndingDirectorySeparator(value.StartsWith("\\\\?\\", StringComparison.Ordinal)
            ? value[4..]
            : value);

    private static DateTimeOffset FromFileTime(NativeFileTime value)
    {
        var combined = unchecked((long)(((ulong)value.HighDateTime << 32) | value.LowDateTime));
        return DateTimeOffset.FromFileTime(combined);
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
            throw NativeFailure("A retained target handle could not be duplicated.");
        }

        return duplicate;
    }

    private static void Flush(SafeFileHandle handle)
    {
        if (!FlushFileBuffers(handle))
        {
            throw NativeFailure("A target directory durability flush failed.");
        }
    }

    private static IOException NativeFailure(string message) =>
        new(message + $" (Windows error {Marshal.GetLastWin32Error()}).", new Win32Exception(Marshal.GetLastWin32Error()));

    private static IOException NativeStatusFailure(int status, string message)
    {
        var error = RtlNtStatusToDosError(status);
        return new IOException(message + $" (Windows error {error}).", new Win32Exception(checked((int)error)));
    }

    private enum FileInfoByHandleClass
    {
        FileBasicInfo = 0,
        FileStreamInfo = 7,
        FileIdInfo = 0x12,
        FileDispositionInfoEx = 21,
    }

    private enum NativeFileInformationClass
    {
        FileRenameInformationEx = 65,
    }

    private enum SecurityObjectType
    {
        FileObject = 1,
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
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfoEx
    {
        public uint Flags;
    }

#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "ReplaceFileW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReplaceFile(
        string replacedFileName,
        string replacementFileName,
        string? backupFileName,
        uint replaceFlags,
        IntPtr exclude,
        IntPtr reserved);

    [DllImport("kernel32.dll", EntryPoint = "ReOpenFile", SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle ReopenFile(
        SafeFileHandle originalFile,
        uint desiredAccess,
        uint shareMode,
        uint flagsAndAttributes);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass informationClass,
        out FileIdInfo information,
        uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass informationClass,
        IntPtr information,
        int size);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass informationClass,
        ref FileBasicInfo information,
        uint size);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass informationClass,
        ref FileDispositionInfoEx information,
        uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[]? path,
        uint pathLength,
        uint flags);

    [DllImport("kernel32.dll", EntryPoint = "FlushFileBuffers", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalBytes,
        out ulong totalFreeBytes);

    [DllImport("kernel32.dll", EntryPoint = "DuplicateHandle", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcess,
        SafeFileHandle sourceHandle,
        IntPtr targetProcess,
        out SafeFileHandle targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("ntdll.dll", ExactSpelling = true)]
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

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtSetInformationFile(
        SafeFileHandle fileHandle,
        out NativeIoStatusBlock ioStatusBlock,
        IntPtr fileInformation,
        uint length,
        NativeFileInformationClass fileInformationClass);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("advapi32.dll", EntryPoint = "GetSecurityInfo", SetLastError = true, ExactSpelling = true)]
    private static extern uint GetSecurityInfo(
        SafeFileHandle handle,
        SecurityObjectType objectType,
        uint securityInformation,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorLength", SetLastError = true, ExactSpelling = true)]
    private static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);

    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorControl", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorControl(
        IntPtr securityDescriptor,
        out ushort control,
        out uint revision);

    [DllImport("advapi32.dll", EntryPoint = "SetKernelObjectSecurity", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(
        SafeFileHandle handle,
        uint securityInformation,
        byte[] securityDescriptor);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
#pragma warning restore SYSLIB1054
}
