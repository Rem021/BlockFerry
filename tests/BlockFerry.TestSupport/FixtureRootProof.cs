using BlockFerry.Core.System;

namespace BlockFerry.TestSupport;

public sealed class FixtureRootProof
{
    internal FixtureRootProof(
        object ownerToken,
        string rootPath,
        Guid rootId,
        string finalPath,
        PhysicalDirectoryIdentity physicalIdentity)
    {
        OwnerToken = ownerToken;
        RootPath = rootPath;
        RootId = rootId;
        FinalPath = finalPath;
        PhysicalIdentity = physicalIdentity;
    }

    public string RootPath { get; }
    public Guid RootId { get; }
    public string FinalPath { get; }
    public PhysicalDirectoryIdentity PhysicalIdentity { get; }
    internal object OwnerToken { get; }
}
