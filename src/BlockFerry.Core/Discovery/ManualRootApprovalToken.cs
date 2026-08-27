using BlockFerry.Core.System;

namespace BlockFerry.Core.Discovery;

public sealed class ManualRootApprovalToken
{
    internal ManualRootApprovalToken(
        Guid storeId,
        long generation,
        string canonicalPath,
        PhysicalDirectoryIdentity identity,
        byte[] nonce,
        byte[] authenticator,
        IFileSystemCapability proofFileSystem)
    {
        StoreId = storeId;
        Generation = generation;
        CanonicalPath = canonicalPath;
        Identity = identity;
        Nonce = (byte[])nonce.Clone();
        Authenticator = (byte[])authenticator.Clone();
        ProofFileSystem = proofFileSystem;
    }

    internal Guid StoreId { get; }
    internal long Generation { get; }
    internal string CanonicalPath { get; }
    internal PhysicalDirectoryIdentity Identity { get; }
    internal byte[] Nonce { get; }
    internal byte[] Authenticator { get; }
    internal IFileSystemCapability ProofFileSystem { get; }
}
