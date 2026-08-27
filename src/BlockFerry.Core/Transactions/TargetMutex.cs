using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Transactions;

internal sealed class TargetMutexBusyException : InvalidOperationException
{
    internal TargetMutexBusyException()
        : base("The selected target is already being synchronized.")
    {
    }
}

internal sealed class TargetMutexFactory
{
    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The transaction composition contract requires an instance mutex factory.")]
    internal TargetMutexSession Acquire(
        MigrationTransactionCoordinator.ExecutionAuthority authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        cancellationToken.ThrowIfCancellationRequested();
        return AcquireIdentity(
            authority.CurrentPairEvidence.Target.GameRoot.Identity,
            cancellationToken);
    }

    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Recovery and execution share one injected target mutex factory contract.")]
    internal TargetMutexSession Acquire(
        RecoveryExecutionAuthority authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!authority.IsActive)
        {
            throw new InvalidOperationException("The recovery authority is no longer active.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return AcquireIdentity(authority.Locator.TargetRootIdentity, cancellationToken);
    }

    private static TargetMutexSession AcquireIdentity(
        PhysicalDirectoryIdentity identity,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Target mutexes require Windows.");
        }

        if (identity == default)
        {
            throw new ArgumentException("A complete physical target identity is required.", nameof(identity));
        }

        var name = ComputeName(identity);
        using var currentIdentity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var currentUser = currentIdentity.User ??
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var security = new MutexSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new MutexAccessRule(
            currentUser,
            MutexRights.FullControl,
            AccessControlType.Allow));
        var mutex = MutexAcl.Create(
            initiallyOwned: false,
            name,
            out _,
            security);
        var acquired = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                throw new TargetMutexBusyException();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new TargetMutexSession(mutex);
        }
        catch
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }

            mutex.Dispose();
            throw;
        }
    }

    internal static string ComputeName(PhysicalDirectoryIdentity identity)
    {
        Span<byte> material = stackalloc byte[3 * sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(material, identity.VolumeSerialNumber);
        BinaryPrimitives.WriteUInt64BigEndian(material[sizeof(ulong)..], identity.FileIdLow);
        BinaryPrimitives.WriteUInt64BigEndian(material[(2 * sizeof(ulong))..], identity.FileIdHigh);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(material, digest);
        return "Local\\BlockFerry.Target." + Convert.ToHexString(digest);
    }
}

internal sealed class TargetMutexSession : IDisposable
{
    private Mutex? _mutex;

    internal TargetMutexSession(Mutex mutex)
    {
        _mutex = mutex ?? throw new ArgumentNullException(nameof(mutex));
    }

    internal bool IsHeld => Volatile.Read(ref _mutex) is not null;

    public void Dispose()
    {
        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        finally
        {
            mutex.Dispose();
        }
    }
}
