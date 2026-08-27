namespace BlockFerry.Core.Transactions;

using global::System.Runtime.InteropServices;

public enum AppStorageMutationState
{
    NotCommitted,
    CommittedVerified,
    RecoveryRequired,
}

public sealed record AppStorageMutationResult(
    AppStorageMutationState State,
    AppStorageDiagnostic? Diagnostic = null)
{
    public static AppStorageMutationResult NotCommitted(AppStorageDiagnostic? diagnostic = null) =>
        new(AppStorageMutationState.NotCommitted, diagnostic);

    public static AppStorageMutationResult CommittedVerified() =>
        new(AppStorageMutationState.CommittedVerified);

    public static AppStorageMutationResult RecoveryRequired(AppStorageDiagnostic diagnostic) =>
        new(AppStorageMutationState.RecoveryRequired, diagnostic);
}

internal enum AppStorageReadState
{
    Missing,
    Read,
    LimitExceeded,
    Unavailable,
    RecoveryRequired,
}

internal sealed record AppStorageReadResult(
    AppStorageReadState State,
    byte[]? Bytes = null);

internal enum AppStorageRecoveryState
{
    Clean,
    RolledBack,
    Committed,
    RecoveryRequired,
}

internal enum AppStorageInterleavingPoint
{
    StageCreated,
    StageDurable,
    RecoveryManifestDurable,
    TargetRetainedDurable,
    BeforeCommitRename,
    AfterTargetTombstoneRename,
    DirectoryDurableAfterTargetTombstone,
    AfterCommitRename,
    DirectoryDurableAfterCommit,
    FinalDurable,
    OldTombstoneDeleted,
    BeforeClearTombstoneRename,
    ClearRecoveryManifestDurable,
    AfterClearTombstoneRename,
    ClearDirectoryDurableAfterTombstone,
    BeforeClearDelete,
    AfterClearDelete,
    ClearDirectoryDurableAfterDelete,
}

internal sealed class AppStorageCrashSimulationException(
    AppStorageInterleavingPoint point) : Exception(
        $"A fixture simulated process termination at {point}.")
{
    public AppStorageInterleavingPoint Point { get; } = point;
}

internal sealed record AppStorageInterleavingContext(string RelativeName);

internal interface IAppStorageInterleaving
{
    void Reach(AppStorageInterleavingPoint point, AppStorageInterleavingContext context);
}

internal sealed class NullAppStorageInterleaving : IAppStorageInterleaving
{
    public static NullAppStorageInterleaving Instance { get; } = new();

    private NullAppStorageInterleaving()
    {
    }

    public void Reach(AppStorageInterleavingPoint point, AppStorageInterleavingContext context)
    {
    }
}

internal interface IAppStoragePrecommitAuthority
{
    IDisposable Revalidate(CancellationToken cancellationToken);
}

internal sealed class NullAppStoragePrecommitAuthority : IAppStoragePrecommitAuthority
{
    public static NullAppStoragePrecommitAuthority Instance { get; } = new();

    private NullAppStoragePrecommitAuthority()
    {
    }

    public IDisposable Revalidate(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return EmptyAuthorityLease.Instance;
    }

    private sealed class EmptyAuthorityLease : IDisposable
    {
        public static EmptyAuthorityLease Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

internal sealed class AppStoragePrecommitAuthorityException(
    string message,
    Exception? innerException = null) : IOException(message, innerException);

internal enum AppStorageNativeArchitecture
{
    X86,
    X64,
    Arm64,
}

internal readonly record struct AppStorageRenameLayout(
    int FlagsOffset,
    int RootDirectoryOffset,
    int FileNameLengthOffset,
    int FileNameOffset,
    int PointerSize)
{
    public static AppStorageRenameLayout Current =>
        ForArchitecture(RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => AppStorageNativeArchitecture.X86,
            Architecture.X64 => AppStorageNativeArchitecture.X64,
            Architecture.Arm64 => AppStorageNativeArchitecture.Arm64,
            _ => throw new PlatformNotSupportedException(
                "Guarded app storage requires a supported Windows pointer ABI."),
        });

    public static AppStorageRenameLayout ForArchitecture(AppStorageNativeArchitecture architecture) =>
        architecture switch
        {
            AppStorageNativeArchitecture.X86 => FromLayout<NativeRenameLayout32>(sizeof(uint)),
            AppStorageNativeArchitecture.X64 or AppStorageNativeArchitecture.Arm64 =>
                FromLayout<NativeRenameLayout64>(sizeof(ulong)),
            _ => throw new ArgumentOutOfRangeException(nameof(architecture)),
        };

    public int BufferSize(int fileNameBytes)
    {
        var minimumStructureSize = checked(FileNameOffset + sizeof(char));
        var alignedStructureSize = checked(
            ((minimumStructureSize + PointerSize - 1) / PointerSize) * PointerSize);
        return checked(alignedStructureSize + fileNameBytes);
    }

    private static AppStorageRenameLayout FromLayout<T>(int pointerSize)
        where T : struct =>
        new(
            checked((int)Marshal.OffsetOf<T>(nameof(NativeRenameLayout32.Flags))),
            checked((int)Marshal.OffsetOf<T>(nameof(NativeRenameLayout32.RootDirectory))),
            checked((int)Marshal.OffsetOf<T>(nameof(NativeRenameLayout32.FileNameLength))),
            checked((int)Marshal.OffsetOf<T>(nameof(NativeRenameLayout32.FileName))),
            pointerSize);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct NativeRenameLayout32
    {
        public uint Flags;
        public uint RootDirectory;
        public uint FileNameLength;
        public ushort FileName;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NativeRenameLayout64
    {
        public uint Flags;
        public ulong RootDirectory;
        public uint FileNameLength;
        public ushort FileName;
    }
}
