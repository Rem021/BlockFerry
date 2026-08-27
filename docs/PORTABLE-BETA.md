# BlockFerry Portable Beta 使用说明

本文描述版本 `0.1.0-beta.4` 的便携版交付约定。版本文件夹固定为 `BlockFerry-0.1.0-beta.4-win-x64-portable`，zip 固定为 `BlockFerry-0.1.0-beta.4-win-x64-portable.zip`。发布脚本只有在本地校验全部通过后才会生成这两项；已有同名路径绝不覆盖。

## 系统要求

- Windows 10 1809 或更高版本，x64 架构。
- 不需要预装 .NET 10、Windows App SDK runtime installer 或 SDK。
- 不需要 MSIX、Developer Mode 或安装程序；当前便携版启动时会显示一次 Windows administrator 管理员权限确认，用于保持事务备份、元数据还原和撤销边界。

## 启动方法

1. 将完整 zip 解压到普通本地文件夹。
2. 双击 `BlockFerry.App.WinUI.exe`。程序、运行库与界面资源已经合并在这一个 EXE 中；首次启动会由 Windows App SDK 解包到当前用户的临时目录。

不要直接从压缩包内部运行（never run from inside the zip）。

## beta.4 能同步什么

BlockFerry 先进行有上限的 discovery，也可由你通过文件夹选择器明确指定 PCL、`.minecraft`、`versions` 或具体实例目录。发现、内容读取和 dry-run 最终清单阶段对 Minecraft/PCL 保持 zero writes；只有你在卡片清单中确认并点击“备份并同步”后，才进入 apply / migration。

首个真实同步版本固定支持：

- Minecraft `1.21.1` 的 `options.txt` 玩家设置；目标资源包字段和 schema 始终受保护。
- FancyMenu `3.x` 存在于两侧时，所选 `guiScale` 会与其首次启动标记在同一事务中迁移，避免目标首次进入游戏后把界面尺寸重置为整合包默认值；不会修改目标 FancyMenu 默认配置。
- Dark Mode Everywhere `1.x` 的当前深色模式；目标已有配置时只映射两侧都能唯一识别的同一个 shader，并保留目标端 shader 定义与 JSON 格式。目标尚未生成配置时会提供“创建配置”选项（默认跳过），明确选中后写入经过同一严格校验的来源 `version: 2` 配置。
- JEI `19.x` 的版本 2 收藏范围（包含该格式支持的物品与配方收藏；文件仍须通过严格 schema 验证）。
- Extreme Sound Muffler `3.x` 的静音规则（文件仍须通过严格资源 ID、数值范围与 JSON 验证）。

Minecraft 版本、模组主版本系列、modId、格式或物理目录身份证据不完整时，相应卡片会显示不支持并产生零变更。EMI 只检测并提示，当前版本不读取或同步 `emi.json`。

JEI 会根据当前服务器显示名、地址与 LAN 标记生成收藏 scope。目标未启动且尚无服务器 scope 时，BlockFerry 会从来源的 `latest.log` 只读取得最后连接的数字 IP 与端口，并使用 Minecraft `1.21.1` 状态协议读取服务器当前显示名，按 JEI 的命名规则直接写入目标将使用的收藏目录；不解析域名、不发送账号或 Token、不修改 `servers.dat`，请求限时且响应有大小上限。无法唯一确认时才退回安全预置与待复核流程。

首次进入目标服务器并关闭 Minecraft 后，BlockFerry 会识别 JEI 真正生成的唯一目标 scope，并用第二笔带独立还原点、进程门禁、复读验证和撤销能力的事务自动归位。目标只有 JEI 默认空收藏时可安全替换；已经与来源相同时直接确认完成；已有任何不同收藏时保留目标并暂停，不会静默覆盖。待复核记录由当前 Windows 用户保护，关闭 BlockFerry 后再次打开也会自动恢复；多个候选或任一来源/目标证据变化仍会安全阻止。

## 写入与恢复边界

- UI 先显示按类别分组的最终清单；新增、更新、相同、未选择、受保护、不支持、冲突和跳过分别成组。
- 提交前会重新验证来源、目标、内容摘要、目标 Minecraft 进程和独占锁。
- 提交前还会等待目标实例至少连续 20 秒没有文件活动，避免 PCL 尚在安装整合包时把刚同步的设置重新覆盖；90 秒内仍不稳定则本次零写入并提示稍后重试。
- migration 只允许已审核适配器声明的相对文件；不进行整目录 copy，也不复制 JAR、脚本、账号、Token、世界或启动器数据库。
- 每个目标文件在修改前创建经身份验证的 backup；使用暂存、原子替换、SHA-256 复读和事务日志。
- 失败会 rollback。若进程在中间退出，下次启动必须先恢复，不能开始新同步。
- 成功后仅在目标 after-state 未被后续修改时提供“撤销这次同步”；撤销本身也可中断恢复。
- UNC、映射网络盘和网络重定向在 beta.4 仍是只读，不执行迁移写入。

开发与验收只使用 demo mode 或 copied fixtures。正式版第一次对真实实例执行操作，应由用户自己选择来源/目标、检查卡片并明确确认；不要拿唯一实例做开发故障注入。

## 本地持久化

BlockFerry 的应用状态位于 `%LOCALAPPDATA%\BlockFerry`。其中可包含 `%LOCALAPPDATA%\BlockFerry\theme.txt`、受当前 Windows 用户保护的最近发现位置、JEI 待复核记录，以及事务、backup 和 recovery 记录。它们不属于 Minecraft 实例。旧版普通主题文件会在确认是当前用户拥有的普通本地对象后收紧访问权限，主题字节保持不变。

## 完整性文件

`SHA256SUMS.txt` 覆盖便携文件夹内除清单自身之外的每个交付文件。`THIRD-PARTY-NOTICES.txt` 包含运行时与第三方许可说明。

发布流程会拒绝 PDB，并按 Windows App SDK 官方的 unpackaged self-contained single-file 方式把 .NET、WinUI、PRI、XBF、背景与应用图标收进一个 x64 EXE；同时核对 PE 架构、manifest/file-set equality、zip 安全路径以及解压 round-trip。最终文件夹固定只有 EXE、使用说明、第三方许可和 SHA-256 清单四个文件。

## 移除

关闭程序后删除 `BlockFerry-0.1.0-beta.4-win-x64-portable` 文件夹即可移除程序。若同时删除 `%LOCALAPPDATA%\BlockFerry`，主题、最近位置、未完成恢复记录和撤销依据都会一并消失；有“上次同步未完成”提示时不要先删除该目录。

## 故障排查

- 重新完整解压 zip，不要在 inside the zip 状态运行。
- EXE 本身可以单独运行；若要核验完整交付物，请同时保留使用说明、许可和 `SHA256SUMS.txt`。
- 使用 `SHA256SUMS.txt` 检查完整性，失败时重新取得完整交付物，不要手工补文件。
- 若提示目标正在运行，请正常关闭 Minecraft 后重试。
- 若提示恢复优先，请先按恢复卡片操作；身份验证失败时只导出脱敏诊断，不要猜测目标目录。
