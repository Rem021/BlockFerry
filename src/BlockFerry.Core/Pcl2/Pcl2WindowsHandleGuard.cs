using BlockFerry.Core.Discovery;
using BlockFerry.Core.System;

namespace BlockFerry.Core.Pcl2;

internal static class Pcl2WindowsHandleGuard
{
    public static bool TryOpenInstanceAccess(
        IFileSystemCapability fileSystem,
        Pcl2Instance instance,
        CancellationToken cancellationToken,
        out Pcl2ReadPathGuard? access,
        out string? rejectedPath,
        out string? reason)
    {
        access = null;
        rejectedPath = null;
        reason = null;
        if (instance.CapabilityAccess is not Pcl2InstanceCapabilityAccess binding)
        {
            rejectedPath = instance.InstanceRoot;
            reason = "The instance has no capability-root ownership binding from discovery.";
            return false;
        }

        try
        {
            var opened = new Pcl2ReadPathGuard(
                fileSystem,
                binding.RootAccess,
                cancellationToken);
            try
            {
                using var instanceRoot = opened.OpenMinecraftDirectory(
                    binding.InstanceRootRelativePath,
                    cancellationToken);
                if (instanceRoot.Identity != binding.InstanceRootIdentity)
                {
                    rejectedPath = instance.InstanceRoot;
                    reason = "The discovered instance directory identity changed.";
                    opened.Dispose();
                    return false;
                }

                if (binding.GameRootRelativePath is NormalizedRelativePath gameRelative &&
                    binding.GameRootIdentity is PhysicalDirectoryIdentity expectedGameIdentity)
                {
                    using var gameRoot = opened.OpenMinecraftDirectory(
                        gameRelative,
                        cancellationToken);
                    if (gameRoot.Identity != expectedGameIdentity)
                    {
                        rejectedPath = instance.GameRoot;
                        reason = "The discovered game-root directory identity changed.";
                        opened.Dispose();
                        return false;
                    }
                }

                access = opened;
                return true;
            }
            catch
            {
                opened.Dispose();
                throw;
            }
        }
        catch (CapabilityBoundaryException exception)
        {
            rejectedPath ??= instance.GameRoot ?? instance.InstanceRoot;
            reason = DiagnosticText.EscapeTechnicalValue(exception.Message);
            return false;
        }
    }
}
