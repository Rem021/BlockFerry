using BlockFerry.Core.System;

namespace BlockFerry.Core.Discovery;

public interface IShortcutTargetResolver
{
    ShortcutResolution Parse(BoundedFileSnapshot shortcutBytes);
}
