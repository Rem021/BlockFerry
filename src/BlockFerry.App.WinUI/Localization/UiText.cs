using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace BlockFerry.App.WinUI.Localization;

internal enum UiLanguage
{
    ChineseSimplified,
    English,
}

/// <summary>
/// Keeps presentation copy bilingual without leaking language choices into the
/// migration core. Chinese remains the source copy; English is projected only
/// onto the live visual tree and can therefore be switched without restarting.
/// </summary>
internal static partial class UiText
{
    private sealed class SourceSnapshot
    {
        internal Dictionary<string, LocalizedSlot> Values { get; } = new(StringComparer.Ordinal);
    }

    private sealed record LocalizedSlot(string Source, string Projected);

    private static readonly ConditionalWeakTable<DependencyObject, SourceSnapshot> Snapshots = new();

    private static readonly Dictionary<string, string> Exact =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["方块渡口"] = "BlockFerry",
            ["BlockFerry · 方块渡口"] = "BlockFerry",
            ["从 "] = "From ",
            ["到 "] = "To ",
            ["发现新版本"] = "Update available",
            ["查看 BlockFerry 新版本"] = "View the latest BlockFerry release",
            ["在 GitHub 查看新版本"] = "View the new release on GitHub",
            ["切换颜色主题"] = "Switch color theme",
            ["迁移设置"] = "Migrate settings",
            ["迁移工作区"] = "Migration workspace",
            ["BLOCKFERRY · 安全迁移"] = "BLOCKFERRY · SAFE MIGRATION",
            ["选择内容"] = "Choose content",
            ["审核清单"] = "Review plan",
            ["执行与验证"] = "Execute & verify",
            ["当前步骤"] = "Current step",
            ["已完成步骤"] = "Completed step",
            ["待进行"] = "Pending",
            ["来源与目标"] = "Source & target",
            ["安全边界"] = "Safety boundary",
            ["等待发现实例 · PCL 2"] = "Waiting for instances · PCL 2",
            ["等待发现实例"] = "Waiting for instances",
            ["未选择"] = "Not selected",
            ["个人设置去往新版本"] = "Move your preferences to the new version",
            ["准备同步"] = "Ready to sync",
            ["演示实例已检查；不会读取或写入磁盘"] = "Demo instances checked; no disk access",
            ["同步准备与执行进度"] = "Sync preparation and execution progress",
            ["选择同步设置"] = "Choose sync settings",
            ["打开同步设置选择"] = "Open sync settings",
            ["请先发现并选择两个不同实例，再选择内容并检查最终清单"] = "Find and choose two different instances, select content, then review the final plan",
            ["正在处理"] = "Working",
            ["同步完成"] = "Sync complete",
            ["查看演示结果"] = "View demo result",
            ["查看同步结果"] = "View sync result",
            ["已重新读取并验证全部输入"] = "All inputs reread and verified",
            ["已复读并验证全部同步结果"] = "All sync results reread and verified",
            ["正在封存事务记录并清理临时文件"] = "Finalizing the transaction record and cleaning temporary files",
            ["关闭设置选择"] = "Close sync settings",
            ["安全事务进行中时此面板必须保持打开；完成提交或回滚后可关闭"] = "This panel stays open during a protected transaction and can be closed after commit or rollback",
            ["返回主页"] = "Back to home",
            ["安全事务进行中时工作区必须保持打开；完成提交或回滚后可返回主页"] = "The workspace stays open during a protected transaction; return home after commit or rollback",
            ["同步会在主页继续显示进度；事务完成前请保持程序打开"] = "Sync progress continues on the home page; keep BlockFerry open until the transaction finishes",
            ["可以关闭"] = "Ready to close",
            ["来源"] = "Source",
            ["来源实例"] = "Source instance",
            ["目标"] = "Target",
            ["目标实例"] = "Target instance",
            ["只读发现 PCL2 实例"] = "Find PCL2 instances (read-only)",
            ["自动探测"] = "Auto-detect",
            ["自动探测 Minecraft 实例"] = "Auto-detect Minecraft instances",
            ["只检查受保护的最近位置、当前用户常见 Minecraft 目录和已验证的 PCL 快捷方式"] = "Checks only protected recent locations, common Minecraft folders, and verified PCL shortcuts",
            ["选择文件夹"] = "Choose folder",
            ["选择 Minecraft 或 PCL 文件夹"] = "Choose a Minecraft or PCL folder",
            ["选择 PCL 文件夹、.minecraft、versions 或具体实例文件夹"] = "Choose a PCL folder, .minecraft, versions, or a specific instance folder",
            ["试用演示"] = "Try demo",
            ["试用演示数据"] = "Try demo data",
            ["使用内存中的固定示例，不访问 Minecraft 或 PCL 文件"] = "Uses fixed in-memory samples without accessing Minecraft or PCL files",
            ["发现阶段只读，不会修改 Minecraft/PCL 文件。"] = "Discovery is read-only and never changes Minecraft or PCL files.",
            ["正在搜索可用实例…"] = "Searching for available instances…",
            ["上次同步未完成"] = "Previous sync was interrupted",
            ["选择实例位置"] = "Choose instance location",
            ["为恢复选择原目标实例文件夹"] = "Choose the original target instance folder for recovery",
            ["安全恢复"] = "Recover safely",
            ["安全恢复上次同步"] = "Safely recover the previous sync",
            ["导出诊断"] = "Export diagnostics",
            ["导出脱敏恢复诊断"] = "Export redacted recovery diagnostics",
            ["发现与选择阶段只读；确认最终清单后才会先备份再同步。"] = "Discovery and selection are read-only. Sync starts only after the final review and backup.",
            ["模组设置"] = "Mod settings",
            ["界面外观、JEI 收藏与静音规则独立选择；展开卡片可处理单项冲突。"] = "Choose appearance, JEI bookmarks, and mute rules separately. Expand a card to resolve individual conflicts.",
            ["暂时无法生成预览"] = "Preview unavailable",
            ["确认同步清单"] = "Review sync plan",
            ["撤销这次同步"] = "Undo this sync",
            ["当前安全阶段与进度"] = "Current safety stage and progress",
            ["正在准备可选设置…"] = "Preparing available settings…",
            ["检查同步计划"] = "Review sync plan",
            ["检查同步计划；此步骤不写入文件"] = "Review the sync plan; this step writes no files",
            ["重新检查同步计划"] = "Review sync plan again",
            ["重新检查同步计划；此步骤不写入文件"] = "Review the sync plan again; this step writes no files",
            ["再次同步"] = "Sync again",
            ["重新验证实例和内容后开始新的同步"] = "Revalidate the instances and content, then start a new sync",
            ["检查当前选择并生成最终同步清单；此步骤不写入文件"] = "Review the current selection and build the final sync plan; this step writes no files",
            ["返回修改"] = "Back to settings",
            ["返回修改同步内容"] = "Return to edit sync content",
            ["自定义选择"] = "Custom selection",
            ["全选"] = "Select all",
            ["恢复全选"] = "Select all",
            ["整合包保护"] = "Modpack protection",
            ["资源包结构和版本标记将保留目标值。"] = "Resource-pack structure and version markers stay unchanged on the target.",
            ["保留目标"] = "Keep target",
            ["采用来源"] = "Use source",
            ["跳过此项"] = "Skip item",
            ["冲突处理方式"] = "Conflict resolution",
            ["同步内容"] = "Sync content",
            ["正在读取内容"] = "Reading content",
            ["等待读取实例内容"] = "Waiting for instance content",
            ["展开同步内容详情"] = "Expand sync content details",
            ["检测到 EMI 收藏：beta.4 暂不支持"] = "EMI bookmarks detected: not supported in beta.4",
            ["原版设置"] = "Vanilla settings",
            ["语言、按键、声音与显示选项"] = "Language, controls, sound, and display",
            ["界面外观"] = "Appearance",
            ["Dark Mode Everywhere 深色模式"] = "Dark Mode Everywhere theme",
            ["JEI 合成收藏"] = "JEI bookmarks",
            ["单人世界与服务器收藏"] = "Single-player and server bookmarks",
            ["声音静音设置"] = "Sound mute settings",
            ["Extreme Sound Muffler 音量规则"] = "Extreme Sound Muffler rules",
            ["来源中没有找到对应数据"] = "No matching source data was found",
            ["目标中没有找到对应数据"] = "No matching target data was found",
            ["Minecraft 版本暂不兼容"] = "The Minecraft versions are not compatible",
            ["模组版本暂不兼容"] = "The mod versions are not compatible",
            ["数据格式版本暂不支持"] = "This data format is not supported",
            ["实例内容已变化，请重新扫描"] = "Instance content changed; scan again",
            ["安全边界阻止了这项内容"] = "The safety boundary blocked this item",
            ["没有可迁移内容"] = "No transferable content",
            ["目标中没有可唯一对应的收藏作用域，请检查实例后重新探测"] = "No unique bookmark scope exists on the target. Check the instance and scan again.",
            ["技术信息已隐藏"] = "Technical details hidden",
            ["演示数据 · 只读预览"] = "Demo data · read-only preview",
            ["尚未选择（未访问 Minecraft/PCL 文件）"] = "Not selected (Minecraft/PCL files were not accessed)",
            ["未选择来源"] = "No source selected",
            ["未选择目标"] = "No target selected",
            ["演示数据 · 只读预览 · 0 写入"] = "Demo data · read-only preview · 0 writes",
            ["等待发现实例 · 0 写入"] = "Waiting for instances · 0 writes",
            ["真实实例 · 选择内容后会显示最终清单"] = "Real instances · choose content to see the final plan",
            ["演示完成"] = "Demo complete",
            ["正在生成演示预览"] = "Building demo preview",
            ["正在安全处理同步"] = "Processing sync safely",
            ["正在执行受保护操作"] = "Running a protected operation",
            ["正在执行安全迁移"] = "Running protected migration",
            ["正在重新核对来源、目标和同步清单"] = "Rechecking the source, target, and sync plan",
            ["重新核对"] = "Recheck",
            ["创建还原点"] = "Create restore point",
            ["写入设置"] = "Write settings",
            ["复读验证"] = "Verify result",
            ["事务进行期间请保持 Minecraft 关闭。发生问题时会自动回滚，不会留下半完成状态。"] = "Keep Minecraft closed during the transaction. BlockFerry rolls back automatically if anything fails, so no partial state is left behind.",
            ["演示预览完成"] = "Demo preview complete",
            ["同步已验证"] = "Sync verified",
            ["目标文件已经复读验证"] = "Target files were written and verified",
            ["操作被阻止"] = "Operation blocked",
            ["请在同步设置中查看安全提示"] = "Review the safety notice in sync settings",
            ["尚未准备可选设置"] = "Settings are not ready",
            ["生成预览"] = "Build preview",
            ["重试预览"] = "Retry preview",
            ["预览已完成"] = "Preview complete",
            ["正在生成预览…"] = "Building preview…",
            ["正在生成只读预览 · 0 写入"] = "Building read-only preview · 0 writes",
            ["备份并同步"] = "Back up and sync",
            ["正在安全处理…"] = "Processing safely…",
            ["等待 JEI 复核"] = "Waiting for JEI verification",
            ["恢复优先"] = "Recovery required",
            ["请先完成上次同步的恢复"] = "Recover the previous sync first",
            ["等待 JEI 自动复核"] = "Waiting for automatic JEI verification",
            ["同步保持不变"] = "Sync left unchanged",
            ["已安全撤销"] = "Safely undone",
            ["需要处理"] = "Action required",
            ["正在恢复"] = "Recovering",
            ["正在安全同步"] = "Syncing safely",
            ["重新验证实例"] = "Revalidating instances",
            ["检查 Minecraft 进程"] = "Checking Minecraft processes",
            ["准备安全备份"] = "Preparing safe backup",
            ["正在创建备份"] = "Creating backup",
            ["准备待写入文件"] = "Staging files",
            ["提交文件"] = "Committing files",
            ["复读验证目标文件"] = "Verifying target files",
            ["安全回滚"] = "Rolling back safely",
            ["完成清理"] = "Finishing cleanup",
            ["正在安全处理"] = "Processing safely",
            ["语言与界面"] = "Language & interface",
            ["按键与控制"] = "Controls",
            ["声音与显示"] = "Sound & display",
            ["其他玩家设置"] = "Other player settings",
            ["新增"] = "Add",
            ["更新"] = "Update",
            ["相同"] = "Unchanged",
            ["受保护"] = "Protected",
            ["不支持"] = "Unsupported",
            ["冲突处理"] = "Conflicts",
            ["已跳过"] = "Skipped",
            ["计划变更"] = "Planned changes",
            ["All the Mods 10 r19（演示）"] = "All the Mods 10 r19 (demo)",
            ["All the Mods 10 r20（演示）"] = "All the Mods 10 r20 (demo)",
            ["JEI 收藏"] = "JEI bookmarks",
            ["JSON 中存在重复字段"] = "JSON contains duplicate properties",
            ["JSON 数据无法安全读取"] = "JSON data could not be read safely",
            ["MC 未知"] = "MC unknown",
            ["为安全起见，已跳过一个无效或不受支持的位置。"] = "An invalid or unsupported location was skipped for safety.",
            ["关闭设置选择面板。"] = "Close the sync settings panel.",
            ["其他可迁移设置 · 展开查看详情"] = "Other transferable settings · expand for details",
            ["其他设置"] = "Other settings",
            ["内容位置不在允许范围内"] = "The content location is outside the allowed scope",
            ["内容超过 beta.4 的安全读取上限"] = "Content exceeds the beta.4 safe read limit",
            ["原版与模组设置已复读验证；JEI 收藏会在目标首次生成真实服务器目录后自动复核。"] = "Vanilla and mod settings were verified; JEI bookmarks will be checked automatically after the target creates a real server folder.",
            ["原版设置 · 展开查看具体键值"] = "Vanilla settings · expand for individual values",
            ["发现与选择阶段只读；最终清单确认后才会先备份、再同步并复读验证。"] = "Discovery and selection are read-only; after final confirmation BlockFerry backs up, syncs, and verifies the result.",
            ["发现和内容选择阶段只读；真正同步前还会显示最终清单并要求确认。"] = "Discovery and content selection are read-only; the final plan is shown for confirmation before syncing.",
            ["只读预览失败；你的设置选择已保留。"] = "The read-only preview failed; your selection was preserved.",
            ["合成列表收藏 · 展开查看详情"] = "Recipe-list bookmarks · expand for details",
            ["同步正在进行，关闭暂时不可用"] = "Sync is running; closing is temporarily unavailable",
            ["备份并同步已确认的设置"] = "Back up and sync the confirmed settings",
            ["多个内容项目指向同一目标位置"] = "Multiple content items point to the same target location",
            ["安全事务正在提交或回滚；此面板会保持打开并显示当前进度。"] = "The protected transaction is committing or rolling back; this panel stays open and shows progress.",
            ["安全事务正在提交或回滚；此工作区会保持打开并显示当前进度。"] = "The protected transaction is committing or rolling back; this workspace stays open and shows progress.",
            ["同步会在主页继续显示进度；事务完成前请保持程序打开。"] = "Sync progress continues on the home page; keep BlockFerry open until the transaction finishes.",
            ["返回主页。"] = "Back to home.",
            ["安全检查阻止了当前预览；你的设置选择已保留。"] = "A safety check blocked this preview; your selection was preserved.",
            ["安全检查阻止了设置目录准备；没有执行任何写入。"] = "A safety check blocked preparation; no files were written.",
            ["尚未写入。点击“备份并同步”后会先创建可验证还原点，再提交所列文件。"] = "Nothing has been written. Back up and sync creates a verified restore point before committing the listed files.",
            ["尚未连接（未访问磁盘）"] = "Not connected (disk was not accessed)",
            ["展开"] = "Expand",
            ["已达到安全探测上限，剩余位置没有继续检查。"] = "The safe discovery limit was reached; remaining locations were not checked.",
            ["当前是 UI 状态演示。未读取真实实例、未创建还原点、未迁移设置，也不会显示完成 Toast。"] = "This is a UI-state demo. No real instance was read, no restore point was created, and no settings were migrated.",
            ["当前是内存演示，不会访问或修改 Minecraft 实例。"] = "This in-memory demo does not access or change Minecraft instances.",
            ["恢复优先：在上次事务安全结束前，不允许开始新的同步。"] = "Recovery comes first; a new sync cannot start until the previous transaction ends safely.",
            ["执行前会再次核对来源、目标、运行中的 Minecraft 与文件摘要"] = "Before execution, BlockFerry rechecks the source, target, running Minecraft processes, and file digests",
            ["折叠"] = "Collapse",
            ["按类别汇总 · 展开查看具体键值"] = "Grouped by category · expand for individual values",
            ["撤销尚未执行；同步后的文件保持原样，可以关闭 Minecraft 后重试"] = "Undo has not run; synced files remain unchanged. Close Minecraft and retry.",
            ["收藏已安全预置；进入目标服务器并关闭 Minecraft 后自动归位"] = "Bookmarks were staged safely and will move into place after you enter the target server and close Minecraft.",
            ["数据中存在含义重复的项目"] = "The data contains semantically duplicate items",
            ["文件不是有效的 UTF-8 文本"] = "The file is not valid UTF-8 text",
            ["无可显示的详细值"] = "No displayable detail",
            ["无法准备当前设置目录；选择尚未生成。"] = "The current settings catalog could not be prepared; no selection was created.",
            ["暂时无法安全读取这项内容"] = "This content cannot be read safely right now",
            ["暂时无法生成预览 · 0 写入"] = "Preview unavailable · 0 writes",
            ["有一处启动器信息暂时无法读取。"] = "Some launcher information could not be read.",
            ["未识别"] = "Unknown",
            ["来源和目标不能是同一个实例。"] = "Source and target cannot be the same instance.",
            ["来源实例内的 options.txt 已验证"] = "options.txt in the source instance was verified",
            ["来源实例没有可迁移的 options.txt。"] = "The source instance has no transferable options.txt.",
            ["来源或目标 options.txt 已变化；旧选择已失效，已重新准备最新目录。"] = "The source or target options.txt changed; the old selection expired and the latest catalog was prepared.",
            ["来源设置文件未找到"] = "Source settings file not found",
            ["某个实例没有通过完整的安全检查。"] = "An instance did not pass the complete safety check.",
            ["某个实例的文件夹隔离状态无法安全确认。"] = "An instance folder's isolation state could not be confirmed safely.",
            ["检测到重复或无法唯一确认的模组版本"] = "Duplicate or ambiguous mod versions were detected",
            ["正在执行受保护事务；请保持 Minecraft 关闭并暂时不要关闭此窗口。"] = "A protected transaction is running; keep Minecraft and this window open until it finishes.",
            ["正在检查受保护的最近位置、常见 Minecraft 目录和 PCL 快捷方式…"] = "Checking protected recent locations, common Minecraft folders, and PCL shortcuts…",
            ["正在生成当前选择的只读预览 · 0 写入"] = "Building a read-only preview of the current selection · 0 writes",
            ["没有发现可安全配对的来源与目标；当前选择未改变。"] = "No safely pairable source and target were found; the current selection is unchanged.",
            ["没有找到可用的 PCL2 隔离实例。"] = "No usable isolated PCL2 instance was found.",
            ["深色模式"] = "Dark mode",
            ["演示数据：纯内存目录与预览，不包含文件路径。"] = "Demo data: in-memory catalog and preview with no file paths.",
            ["界面主题与外观 · 展开查看详情"] = "Interface theme and appearance · expand for details",
            ["目标实例内的 options.txt 已验证"] = "options.txt in the target instance was verified",
            ["目标实例尚未生成 options.txt；预览会按安全缺失处理。"] = "The target has not created options.txt; preview treats it as safely missing.",
            ["目标文件已复读验证为同步前状态"] = "Target files were verified in their pre-sync state",
            ["目标文件已复读验证；可在未发生后续变化时撤销这次同步。"] = "Target files were verified; this sync can be undone while they remain unchanged.",
            ["目标设置文件尚不存在"] = "Target settings file does not exist yet",
            ["设置文件无法稳定读取，请关闭游戏后重试。"] = "The settings file could not be read consistently; close the game and retry.",
            ["设置类别"] = "Settings category",
            ["语言"] = "Language",
            ["请选择 PCL 文件夹、.minecraft、versions 或具体实例文件夹…"] = "Choose a PCL folder, .minecraft, versions, or a specific instance folder…",
            ["请选择两个不同且可用的来源与目标实例。"] = "Choose two different, usable source and target instances.",
            ["请选择来源与目标后再准备可选设置。"] = "Choose a source and target before preparing settings.",
            ["跳跃"] = "Jump",
            ["跳过冲突"] = "Skip conflict",
            ["还原点已通过身份与摘要验证"] = "The restore point passed identity and digest verification",
            ["静音与音量规则 · 展开查看详情"] = "Mute and volume rules · expand for details",
            ["音乐音量"] = "Music volume",
            ["JEI 实例证据已变化，需要重新探测。"] = "JEI instance evidence changed; scan again.",
            ["JEI 收藏已复核"] = "JEI bookmarks verified",
            ["JEI 来源或实例证据已经变化；没有自动覆盖目标，请重新探测。"] = "JEI source or instance evidence changed. The target was not overwritten; scan again.",
            ["JEI 真实目录已出现；检测到 Minecraft 仍在运行，关闭后会自动完成复核。"] = "The real JEI folder now exists. Minecraft is still running; verification will finish after it closes.",
            ["JEI 自动复核未能安全结束；正在检查恢复记录。"] = "Automatic JEI verification did not finish safely; checking recovery records.",
            ["JEI 自动复核未通过最新实例检查。"] = "Automatic JEI verification did not pass the latest instance check.",
            ["JEI 自动复核需要先安全恢复。"] = "Automatic JEI verification requires recovery first.",
            ["仍有未完成的同步；请先安全恢复。"] = "An unfinished sync remains; recover it first.",
            ["内容识别完成；请选择要同步的项目。"] = "Content discovery is complete; choose what to sync.",
            ["内容适配器不可用，请重新探测。"] = "A content adapter is unavailable; scan again.",
            ["发现上次未完成的同步；请先安全恢复。"] = "An unfinished previous sync was found; recover it first.",
            ["取消后仍发现未完成的同步；请先安全恢复。"] = "An unfinished sync remains after cancellation; recover it first.",
            ["同步内容存在路径冲突，计划未被接受。"] = "The sync content contains a path conflict; the plan was not accepted.",
            ["同步失败后仍有未完成事务；请先安全恢复。"] = "An unfinished transaction remains after the failed sync; recover it first.",
            ["同步完成并复读验证：JEI 收藏已位于目标真实服务器目录。"] = "Sync completed and verified: JEI bookmarks are in the target's real server folder.",
            ["同步已取消；已重新检查未完成事务。"] = "Sync cancelled; unfinished transactions were checked again.",
            ["同步未完成；已重新检查未完成事务。"] = "Sync did not finish; unfinished transactions were checked again.",
            ["同步未能安全结束；请先执行恢复，BlockFerry 不会猜测后续写入。"] = "Sync did not end safely. Recover first; BlockFerry will not guess about further writes.",
            ["同步没有完成，所有已开始的变化均已回滚。"] = "Sync did not complete; every started change was rolled back.",
            ["同步计划未能通过复核；请重新选择内容。"] = "The sync plan failed review; choose the content again.",
            ["基础设置已验证，等待 JEI 真实作用域"] = "Base settings verified; waiting for a real JEI scope",
            ["存在未处理的冲突或失效选择。"] = "There are unresolved conflicts or expired selections.",
            ["安全检查阻止了同步；目标实例未被提交。"] = "A safety check blocked the sync; nothing was committed to the target.",
            ["实例会话已经失效，请重新探测。"] = "The instance session expired; scan again.",
            ["实例在读取前发生变化，请重新探测。"] = "The instance changed before reading; scan again.",
            ["实例或内容已经变化，请重新探测并检查。"] = "The instance or content changed; scan and review again.",
            ["实例或内容已经变化，请重新检查。"] = "The instance or content changed; review again.",
            ["尚未选择需要写入的变化。"] = "No changes have been selected for writing.",
            ["已取消保存诊断。"] = "Diagnostic export cancelled.",
            ["已取消；目标实例未发生变化。"] = "Cancelled; the target instance was not changed.",
            ["已定位目标服务器收藏目录，正在创建还原点并复核 JEI 收藏…"] = "Target server bookmark folder found; creating a restore point and verifying JEI bookmarks…",
            ["已恢复 JEI 收藏待复核任务；正在自动定位服务器收藏目录。"] = "Pending JEI bookmark verification was restored; locating the server bookmark folder automatically.",
            ["已确认是同一个物理实例，可以开始安全恢复。"] = "The same physical instance was confirmed; safe recovery can begin.",
            ["恢复取消后事务仍未完成；请继续安全恢复。"] = "The transaction remains unfinished after recovery was cancelled; continue safe recovery.",
            ["恢复失败后事务仍未完成；请继续安全恢复。"] = "The transaction remains unfinished after recovery failed; continue safe recovery.",
            ["恢复已取消；已重新检查所有未完成事务。"] = "Recovery cancelled; all unfinished transactions were checked again.",
            ["恢复未完成；已重新检查所有未完成事务。"] = "Recovery did not finish; all unfinished transactions were checked again.",
            ["恢复检查完成，可以自动探测或选择文件夹。"] = "Recovery check complete; you can auto-detect or choose a folder.",
            ["恢复记录已经处于终态，可以重新探测实例。"] = "The recovery record is already final; instances can be scanned again.",
            ["所选文件夹不是上次记录的同一个物理实例，请重新选择。"] = "The selected folder is not the same physical instance recorded previously; choose again.",
            ["撤销取消后仍有未完成事务；请先安全恢复。"] = "An unfinished transaction remains after undo was cancelled; recover it first.",
            ["撤销失败后仍有未完成事务；请先安全恢复。"] = "An unfinished transaction remains after undo failed; recover it first.",
            ["撤销尚未安全结束；请先执行恢复。"] = "Undo did not end safely; run recovery first.",
            ["撤销已取消；已重新检查所有未完成事务。"] = "Undo cancelled; all unfinished transactions were checked again.",
            ["撤销未完成；已重新检查所有未完成事务。"] = "Undo did not finish; all unfinished transactions were checked again.",
            ["撤销需要恢复，但暂时无法验证恢复位置；请重新打开 BlockFerry 后继续。"] = "Undo requires recovery, but the recovery location cannot be verified yet. Reopen BlockFerry to continue.",
            ["文件夹验证未完成；原有选择保持不变。"] = "Folder verification did not finish; the existing selection is unchanged.",
            ["本次恢复已完成，但仍有另一项未完成的同步；请继续恢复。"] = "This recovery completed, but another unfinished sync remains; continue recovery.",
            ["正在创建还原点并安全同步，请保持窗口打开。"] = "Creating a restore point and syncing safely; keep this window open.",
            ["正在复核 JEI 收藏作用域"] = "Verifying the JEI bookmark scope",
            ["正在定位 JEI 服务器作用域"] = "Locating the JEI server scope",
            ["正在恢复上次 JEI 收藏的自动复核…"] = "Restoring automatic verification of the previous JEI bookmarks…",
            ["正在自动探测 PCL 与 Minecraft 实例…"] = "Auto-detecting PCL and Minecraft instances…",
            ["正在识别原版设置、界面外观、JEI 收藏与静音规则…"] = "Reading vanilla settings, appearance, JEI bookmarks, and mute rules…",
            ["正在重新读取实例与可同步内容"] = "Rereading the instances and available sync content",
            ["正在重新确认实例与同步清单"] = "Revalidating instances and the sync plan",
            ["正在重新读取并检查所选内容…"] = "Reading and checking the selected content again…",
            ["正在验证当前文件并撤销这次同步…"] = "Verifying current files and undoing this sync…",
            ["正在验证所选文件夹并发现实例…"] = "Verifying the selected folder and finding instances…",
            ["正在验证还原点并恢复上次未完成的同步…"] = "Verifying the restore point and recovering the unfinished sync…",
            ["演示使用内存固定数据，不会访问或修改 Minecraft 实例。"] = "The demo uses fixed in-memory data and does not access or change Minecraft instances.",
            ["目标服务器已有不同的 JEI 收藏；已保留目标，等待重新检查。"] = "The target server already has different JEI bookmarks; the target was preserved for another review.",
            ["真实数据 · 已验证实例"] = "Real data · verified instances",
            ["脱敏诊断已保存到你选择的文件。"] = "Redacted diagnostics were saved to the selected file.",
            ["自动探测未完成；没有修改任何实例。"] = "Auto-detection did not finish; no instance was changed.",
            ["设置已复读验证；JEI 收藏已安全预置，请保持 BlockFerry 打开，关闭 Minecraft 后会自动复核。"] = "Settings were verified and JEI bookmarks staged safely. Keep BlockFerry open; verification will run after Minecraft closes.",
            ["设置已复读验证；JEI 收藏已安全预置，首次进入目标服务器并关闭 Minecraft 后会自动复核。"] = "Settings were verified and JEI bookmarks staged safely. Verification will run after you first enter the target server and close Minecraft.",
            ["诊断保存器尚未就绪。"] = "The diagnostic exporter is not ready.",
            ["请选择两个不同且可安全访问的实例。"] = "Choose two different instances that can be accessed safely.",
            ["选择已变化，请重新检查同步计划。"] = "The selection changed; review the sync plan again.",
            ["同步完成并复读验证：JEI 收藏已自动归位，共写入 {result.CommittedFileCount} 个收藏文件。"] = "Sync completed and verified: JEI bookmarks were placed automatically; {result.CommittedFileCount} bookmark files written.",
            ["同步完成并复读验证：已写入 {result.CommittedFileCount} 个文件。"] = "Sync completed and verified: {result.CommittedFileCount} files written.",
            ["已安全撤销：恢复 {result.RestoredFileCount} 个文件。"] = "Safely undone: {result.RestoredFileCount} files restored.",
            ["已检查 {reviewItems.Count(IsActionable)} 项内容；确认后将先备份再同步。"] = "Reviewed {reviewItems.Count(IsActionable)} items; confirmation will back up before syncing.",
            ["恢复完成：已还原 {result.RestoredFileCount} 个文件，可以重新探测实例。"] = "Recovery complete: {result.RestoredFileCount} files restored; instances can be scanned again.",
            ["提示"] = "Info",
            ["注意"] = "Warning",
            ["错误"] = "Error",
            ["独立"] = "Isolated",
            ["需诊断"] = "Needs review",
            ["未知"] = "Unknown",
            ["浅色"] = "light",
            ["深色"] = "dark",
        };

    internal static UiLanguage Current { get; private set; } = UiLanguage.ChineseSimplified;

    internal static long Revision { get; private set; }

    internal static string LanguageTag => Current == UiLanguage.English ? "en-US" : "zh-CN";

    internal static void SetLanguage(UiLanguage language)
    {
        if (Current == language)
        {
            return;
        }

        Current = language;
        Revision++;
    }

    internal static string Translate(string source) => Translate(Current, source);

    private static string Translate(UiLanguage language, string source)
    {
        if (language != UiLanguage.English || string.IsNullOrEmpty(source))
        {
            return source;
        }

        if (Exact.TryGetValue(source, out var exact))
        {
            return exact;
        }

        var match = SelectedCountRegex().Match(source);
        if (match.Success)
        {
            return $"Selected {match.Groups[1].Value} / {match.Groups[2].Value} items";
        }

        match = SelectedItemsRegex().Match(source);
        if (match.Success)
        {
            return $"Selected {match.Groups[1].Value} items";
        }

        match = PlannedItemsRegex().Match(source);
        if (match.Success)
        {
            return $"Planned {match.Groups[1].Value} items · 0 writes";
        }

        match = WriteFilesRegex().Match(source);
        if (match.Success)
        {
            return $"Will write {match.Groups[1].Value} files · backup first";
        }

        match = BackupProgressRegex().Match(source);
        if (match.Success)
        {
            return $"Checked {match.Groups[1].Value} / {match.Groups[2].Value} restore points; backed up {match.Groups[3].Value} files";
        }

        match = StagingProgressRegex().Match(source);
        if (match.Success)
        {
            return $"Staged {match.Groups[1].Value} / {match.Groups[2].Value} files";
        }

        match = SealedProgressRegex().Match(source);
        if (match.Success)
        {
            return $"Sealed {match.Groups[1].Value} / {match.Groups[2].Value} verification copies";
        }

        match = CommitProgressRegex().Match(source);
        if (match.Success)
        {
            return $"Safely wrote {match.Groups[1].Value} / {match.Groups[2].Value} files";
        }

        match = VerifiedProgressRegex().Match(source);
        if (match.Success)
        {
            return $"Verified {match.Groups[1].Value} files";
        }

        match = ProtectedItemsRegex().Match(source);
        if (match.Success)
        {
            return $"Protected {match.Groups[1].Value} items";
        }

        match = NewVersionRegex().Match(source);
        if (match.Success)
        {
            return $"New {match.Groups[1].Value}";
        }

        match = CompletedFilesRegex().Match(source);
        if (match.Success)
        {
            return $"Sync completed and verified: {match.Groups[1].Value} files written.";
        }

        match = CompletedJeiFilesRegex().Match(source);
        if (match.Success)
        {
            return $"Sync completed and verified: JEI bookmarks were placed automatically; {match.Groups[1].Value} bookmark files written.";
        }

        match = RestoredFilesRegex().Match(source);
        if (match.Success)
        {
            return $"Safely restored {match.Groups[1].Value} files.";
        }

        match = ReviewedItemsRegex().Match(source);
        if (match.Success)
        {
            return $"Reviewed {match.Groups[1].Value} items; confirmation will back up before syncing.";
        }

        match = GeneratedPlanChangesRegex().Match(source);
        if (match.Success)
        {
            return $"Created {match.Groups[1].Value} planned changes · 0 writes";
        }

        match = PreviewSummaryRegex().Match(source);
        if (match.Success)
        {
            return $"Planned sync: {match.Groups[1].Value} settings. This is a read-only preview with 0 writes.";
        }

        match = PreviewSecondaryCountsRegex().Match(source);
        if (match.Success)
        {
            return $"Unselected {match.Groups[1].Value} · protected {match.Groups[2].Value} · target-only {match.Groups[3].Value}";
        }

        match = PreviewCompletedAnnouncementRegex().Match(source);
        if (match.Success)
        {
            return $"Read-only preview complete. {match.Groups[1].Value} settings are planned.";
        }

        match = ReviewAutomationNameRegex().Match(source);
        if (match.Success)
        {
            var title = Translate(language, match.Groups[1].Value);
            var summary = match.Groups[3].Success
                ? $", {Translate(language, match.Groups[3].Value)}"
                : string.Empty;
            return $"{title}, {match.Groups[2].Value} items{summary}";
        }

        var result = source;
        foreach (var pair in PhraseReplacements)
        {
            result = result.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        }

        return result;
    }

    internal static void ApplyToVisualTree(DependencyObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        ApplyObject(root);
        if (root is not UIElement)
        {
            return;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            ApplyToVisualTree(VisualTreeHelper.GetChild(root, index));
        }
    }

    private static void ApplyObject(DependencyObject value)
    {
        if (value is FrameworkElement element)
        {
            element.Language = LanguageTag;
            ApplySlot(value, "AutomationName", () => AutomationProperties.GetName(value),
                text => AutomationProperties.SetName(value, text));
            ApplySlot(value, "AutomationHelp", () => AutomationProperties.GetHelpText(value),
                text => AutomationProperties.SetHelpText(value, text));
            ApplySlot(value, "AutomationItemStatus", () => AutomationProperties.GetItemStatus(value),
                text => AutomationProperties.SetItemStatus(value, text));
            if (ToolTipService.GetToolTip(element) is string)
            {
                ApplySlot(value, "ToolTip", () => ToolTipService.GetToolTip(element) as string,
                    text => ToolTipService.SetToolTip(element, text));
            }
        }

        switch (value)
        {
            case TextBlock textBlock:
                ApplySlot(value, "Text", () => textBlock.Text, text => textBlock.Text = text);
                break;
            case Button button when button.Content is string:
                ApplySlot(value, "Content", () => button.Content as string, text => button.Content = text);
                break;
            case ContentControl contentControl when contentControl.Content is string:
                ApplySlot(value, "Content", () => contentControl.Content as string, text => contentControl.Content = text);
                break;
            case ComboBox comboBox when comboBox.Header is string:
                ApplySlot(value, "Header", () => comboBox.Header as string, text => comboBox.Header = text);
                break;
            case TextBox textBox:
                ApplySlot(value, "Placeholder", () => textBox.PlaceholderText, text => textBox.PlaceholderText = text);
                break;
            case ToggleSwitch toggleSwitch:
                if (toggleSwitch.Header is string)
                {
                    ApplySlot(value, "Header", () => toggleSwitch.Header as string, text => toggleSwitch.Header = text);
                }
                if (toggleSwitch.OnContent is string)
                {
                    ApplySlot(value, "OnContent", () => toggleSwitch.OnContent as string, text => toggleSwitch.OnContent = text);
                }
                if (toggleSwitch.OffContent is string)
                {
                    ApplySlot(value, "OffContent", () => toggleSwitch.OffContent as string, text => toggleSwitch.OffContent = text);
                }
                break;
        }
    }

    private static void ApplySlot(
        DependencyObject owner,
        string slot,
        Func<string?> read,
        Action<string> write)
    {
        var current = read();
        if (string.IsNullOrEmpty(current))
        {
            return;
        }

        var snapshot = Snapshots.GetOrCreateValue(owner);
        snapshot.Values.TryGetValue(slot, out var tracked);
        var source = tracked is null ||
                     !string.Equals(current, tracked.Projected, StringComparison.Ordinal)
            ? current
            : tracked.Source;
        var projected = Translate(Current, source);
        snapshot.Values[slot] = new LocalizedSlot(source, projected);
        if (!string.Equals(current, projected, StringComparison.Ordinal))
        {
            write(projected);
        }
    }

    private static readonly KeyValuePair<string, string>[] PhraseReplacements =
    [
        new("只读预览已完成，计划同步", "Read-only preview complete; planned settings: "),
        new("计划同步", "Planned sync: "),
        new("已选", "Selected"),
        new("项设置", " settings"),
        new("项内容", " items"),
        new("真实实例", "Real instances"),
        new("选择后可安全同步", "choose to sync safely"),
        new("计划 ", "Planned "),
        new("这是只读预览", "this is a read-only preview"),
        new("计划变更", "planned changes"),
        new("未完整识别模组版本", "Mod versions were not fully identified"),
        new("版本系列不兼容", "Incompatible version lines"),
        new("Minecraft 版本不匹配", "Minecraft version mismatch"),
        new("当前支持", "supported"),
        new("并逐文件验证格式", "with per-file format validation"),
        new("冲突处理方式", "conflict resolution"),
        new("其他可迁移设置", "Other transferable settings"),
        new("展开查看具体键值", "expand for individual values"),
        new("展开查看详情", "expand for details"),
        new("提交前仍会完整校验", "all items are still fully validated before commit"),
        new("同步设置已保留", "sync settings were preserved"),
        new("你的设置选择已保留", "your selection was preserved"),
        new("在 GitHub 查看 BlockFerry", "View BlockFerry on GitHub"),
        new("切换到中文", "Switch to Chinese"),
        new("切换到", "Switch to "),
        new("主题", " theme"),
        new("将处理", "Will process"),
        new("涉及", "across"),
        new("个文件", "files"),
        new("将写入", "Will write"),
        new("先备份", "backup first"),
        new("原版设置 · 变更", "Vanilla settings · change"),
        new("原版设置", "Vanilla settings"),
        new("语言与界面", "Language & interface"),
        new("按键与控制", "Controls"),
        new("声音与显示", "Sound & display"),
        new("其他玩家设置", "Other player settings"),
        new("界面外观项", "Appearance item"),
        new("界面外观", "Appearance"),
        new("JEI 合成收藏", "JEI bookmarks"),
        new("声音静音设置", "Sound mute settings"),
        new("没有可迁移内容", "No transferable content"),
        new("展开", "Expand "),
        new("折叠", "Collapse "),
        new("详情", " details"),
        new("收藏项", "Bookmark item"),
        new("静音规则", "Mute rule"),
        new("设置项", "Setting item"),
        new("未选择", "Unselected"),
        new("仅目标", "target-only"),
        new("受保护", "protected"),
        new("已保护", "Protected"),
        new("正在生成当前选择的只读预览", "Building a read-only preview of the current selection"),
        new("目标首次生成真实服务器目录后自动复核", "automatically verify after the target creates a real server folder"),
        new("来源实例", "Source instance"),
        new("目标实例", "Target instance"),
        new("来源设置", "Source settings"),
        new("目标设置", "Target settings"),
        new("目标文件", "Target files"),
        new("来源或目标", "Source or target"),
        new("来源", "Source"),
        new("目标", "Target"),
        new("未识别", "Unknown"),
        new("没有执行任何写入", "no files were written"),
        new("已生成", "Created"),
        new("另有", "Additional"),
        new("选择", "Select "),
        new("正在检查上次同步是否完整", "Checking the previous sync"),
        new("安全事务仍在进行", "The protected transaction is still running"),
        new("完成提交或回滚后即可关闭窗口", "You can close the window after commit or rollback completes"),
        new("实例状态已经变化", "Instance state changed"),
        new("请重新探测后再继续", "scan again before continuing"),
        new("请选择两个不同", "Choose two different"),
        new("来源与目标实例", "source and target instances"),
        new("真实实例", "Real instances"),
        new("选择后可安全同步", "choose content to sync safely"),
        new("只读预览", "read-only preview"),
        new("0 写入", "0 writes"),
        new("正在准备", "Preparing"),
        new("正在检查", "Checking"),
        new("正在生成", "Building"),
        new("正在备份", "Backing up"),
        new("正在验证", "Verifying"),
        new("正在回滚", "Rolling back"),
        new("同步已完成", "Sync completed"),
        new("同步完成", "Sync complete"),
        new("未找到", "Not found"),
        new("不可用", "Unavailable"),
        new("请选择", "Choose"),
        new("重新探测", "Scan again"),
        new("来源：", "Source: "),
        new("目标：", "Target: "),
        new("完整路径已隐藏", "Full paths are hidden"),
        new("写入", "writes"),
        new("文件", "files"),
        new("项目", "items"),
        new("项", "items"),
    ];

    [GeneratedRegex("^已选 (\\d+) / (\\d+) 项(?:设置)?$")]
    private static partial Regex SelectedCountRegex();

    [GeneratedRegex("^已选 (\\d+) 项内容$")]
    private static partial Regex SelectedItemsRegex();

    [GeneratedRegex("^计划 (\\d+) 项 · 0 写入$")]
    private static partial Regex PlannedItemsRegex();

    [GeneratedRegex("^将写入 (\\d+) 个文件 · 先备份$")]
    private static partial Regex WriteFilesRegex();

    [GeneratedRegex("^已检查 (\\d+) / (\\d+) 个还原点；备份 (\\d+) 个文件$")]
    private static partial Regex BackupProgressRegex();

    [GeneratedRegex("^已准备 (\\d+) / (\\d+) 个文件$")]
    private static partial Regex StagingProgressRegex();

    [GeneratedRegex("^已封存 (\\d+) / (\\d+) 个验证副本$")]
    private static partial Regex SealedProgressRegex();

    [GeneratedRegex("^已安全写入 (\\d+) / (\\d+) 个文件$")]
    private static partial Regex CommitProgressRegex();

    [GeneratedRegex("^已验证完成 (\\d+) 个文件$")]
    private static partial Regex VerifiedProgressRegex();

    [GeneratedRegex("^已保护 (\\d+) 项$")]
    private static partial Regex ProtectedItemsRegex();

    [GeneratedRegex("^新版本 (.+)$")]
    private static partial Regex NewVersionRegex();

    [GeneratedRegex("^同步完成并复读验证：已写入 (\\d+) 个文件。$")]
    private static partial Regex CompletedFilesRegex();

    [GeneratedRegex("^同步完成并复读验证：JEI 收藏已自动归位，共写入 (\\d+) 个收藏文件。$")]
    private static partial Regex CompletedJeiFilesRegex();

    [GeneratedRegex("^(?:已安全撤销：恢复|恢复完成：已还原) (\\d+) 个文件(?:，可以重新探测实例)?。$")]
    private static partial Regex RestoredFilesRegex();

    [GeneratedRegex("^已检查 (\\d+) 项内容；确认后将先备份再同步。$")]
    private static partial Regex ReviewedItemsRegex();

    [GeneratedRegex("^已生成 (\\d+) 项计划变更 · 0 写入$")]
    private static partial Regex GeneratedPlanChangesRegex();

    [GeneratedRegex("^计划同步 (\\d+) 项设置；这是只读预览，0 写入。$")]
    private static partial Regex PreviewSummaryRegex();

    [GeneratedRegex("^未选择 (\\d+) · 受保护 (\\d+) · 仅目标 (\\d+)$")]
    private static partial Regex PreviewSecondaryCountsRegex();

    [GeneratedRegex("^只读预览已完成，计划同步 (\\d+) 项设置。$")]
    private static partial Regex PreviewCompletedAnnouncementRegex();

    [GeneratedRegex("^(.+)，(\\d+) 项(?:，(.+))?$")]
    private static partial Regex ReviewAutomationNameRegex();
}
