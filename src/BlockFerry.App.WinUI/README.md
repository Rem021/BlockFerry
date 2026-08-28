# BlockFerry.App.WinUI

BlockFerry 的原生 Windows 外壳，目标为 .NET 10、WinUI 3 与 Windows App SDK 2.3.1。项目采用 unpackaged、framework-dependent x64 配置，不要求开启 Developer Mode。

## 当前可体验内容

- 应用生产启动现在进入 `等待发现实例`，不会再自动落入演示预览。迁移工作区提供 `自动探测`、`选择文件夹` 和次要的 `试用演示`；文件夹选择器接受 PCL 文件夹、`.minecraft`、`versions` 或具体实例文件夹。
- 自动探测是有上限的候选检查：受保护的最近位置、当前用户的两个常见 `.minecraft` 位置，以及用户/公共桌面和开始菜单中经过验证的 PCL 快捷方式。它不会扫描整块磁盘、网络位置、注册表或浏览器数据。
- 发现结果由 generation 绑定的单一活动会话持有。成功重新发现会先替换再释放旧会话；取消文件夹选择、无效目录或无法形成安全来源/目标对时，当前有效会话与选择保持不变。
- 进入 `试用演示` 后，发现卡仍保留在同一工作区内，用户可直接返回 `自动探测` 或 `选择文件夹`，无需重启应用。
- 主场景只保留一个“选择同步设置”入口；进入后切换为占满标题栏下方的迁移工作区，不再留下不可操作的左侧场景。桌面布局把来源、发现与安全边界放在辅助栏，把设置选择放在主栏；窄屏自动纵向排列。
- 工作区按 `选择内容 → 审核清单 → 执行与验证` 分成三个独立阶段。审核、执行进度和完成结果各自占据内容区，固定 footer 只保留当前阶段摘要与唯一主操作；`SelectedCountFooterText` 仍是唯一 `Polite` live region。
- 工作区入场使用短距离 shared-axis 淡入；阶段切换、执行步骤与确定进度分别动画。高对比立即完成转场，减少动态使用无位移降级。
- 来源/目标 route、生产发现入口、四个分类、整合包保护、错误与结果分别使用 16-DIP 圆角卡片和 WinUI 内置符号。分类为 `语言与界面`、`按键与控制`、`声音与显示`、`其他玩家设置`。
- 分类选择使用原生三态 CheckBox，展开/折叠是独立按钮；每个设置整行都是原生 CheckBox 命中区。分类摘要精确区分 `已选 · n/n`、`已选 · x/n` 和 `未选择 · 共 n 项`，完整转义 technical key 保留在帮助文本与工具提示中。
- 顶部 `全选` 在原对象上选择全部无冲突项目，不重建分类、丢失展开状态或替用户决定冲突；已无可安全补选项目时按钮保持禁用。结果卡的 `修改选择` 会回到先前有效选择焦点。
- footer 集中投影发现、选择、最终清单、执行、恢复和完成状态；执行时禁用可变选择，专属执行页显示重新核对、还原点、写入与复读验证四步进度。
- 完成后的 footer 会把原来的只读状态按钮切换为 `再次同步`；它保留当前来源和目标，但重新只读打开实例并生成全新内容清单，不复用旧计划。
- 完成提示音只在当前 generation 与 transactionId 对应的真实事务已经提交并复读验证后播放一次；preview、blocked、failed、stale、canceled、rollback、recovery-required 和重复通知均静音。
- 主界面支持深色与浅色主题；工作区 opening 240 ms、closing 190 ms，阶段转场为 240–280 ms，进度插值为 260 ms。高对比会立即完成过渡，减少动态使用无位移的降级路径。

## 同步安全边界

- 演示模式使用确定性的内存数据，不读取 Minecraft 路径。
- 真实模式只接受文件夹选择器明确给出的根，或上述有上限且经过本地卷/路径验证的自动候选，不进行全盘扫描。
- 页面本身不读取 `File`、`Directory`、`Environment`，也不直接创建 picker；这些操作都位于窗口拥有者绑定的组合层和能力边界之后。手动根只有在发现出可安全配对的隔离实例后，才会保存为当前 Windows 用户受保护的最近位置。
- PCL2 发现、内容目录和最终清单只读。Vanilla、JEI 与 ESM 适配器通过 generation 绑定的 retained-handle 能力读取；选择变化本身不访问磁盘，计划接受与事务提交前都会重新校验来源、目标和内容摘要。
- 界面不显示来源、目标或诊断中的绝对路径。结果卡只说明两个 `options.txt` 是否已验证，并保留不含路径的诊断代码；原始路径继续留在能力边界内部。
- `resourcePacks`、`incompatibleResourcePacks`、`version` 始终受保护，不能从 UI 或 Core 选择。
- `MigrationTransactionCoordinator` 是唯一事务写入入口：它只写 accepted plan 的封闭 allowlist，先生成 DPAPI/认证日志绑定的 before backup，再暂存、提交、复读验证；故障自动回滚，非终态在下次启动进入恢复优先页。
- 本地持久化包含主题、受保护的最近位置和认证事务/恢复记录，位于 `%LOCALAPPDATA%\BlockFerry`。旧主题文件仅在普通本地对象、当前用户所有权和无 reparse 证明成立时收紧 DACL，字节不变。

## 使用便携式 SDK 验证、构建与运行

系统当前没有全局 .NET 10 SDK，因此使用工作区外的便携式 10.0.302 SDK：

```powershell
$repo = 'Z:\Minecraft\BlockFerry\BlockFerry-Windows-Handoff-20260809-053133'
$sdk = 'C:\Users\owo\Documents\Codex\2026-08-09\z-minecraft-blockferry-2\work\dotnet'

$env:DOTNET_ROOT = $sdk
$env:DOTNET_ROOT_X64 = $sdk
$env:PATH = "$sdk;$env:PATH"
$env:DOTNET_CLI_HOME = 'C:\Users\owo\Documents\Codex\2026-08-09\z-minecraft-blockferry-2\work\dotnet-cli-home'
$env:NUGET_PACKAGES = 'C:\Users\owo\Documents\Codex\2026-08-09\z-minecraft-blockferry-2\work\nuget-packages'
$env:DOTNET_MULTILEVEL_LOOKUP = '0'

dotnet run --project "$repo\tests\BlockFerry.Core.SmokeTests\BlockFerry.Core.SmokeTests.csproj" -c Release
dotnet run --project "$repo\tests\BlockFerry.Pcl2FixtureTests\BlockFerry.Pcl2FixtureTests.csproj" -c Release
dotnet run --project "$repo\tests\BlockFerry.AppLogicTests\BlockFerry.AppLogicTests.csproj" -c Release -r win-x64
dotnet build "$repo\src\BlockFerry.App.WinUI\BlockFerry.App.WinUI.csproj" -c Debug -p:Platform=x64 -r win-x64 --no-restore
dotnet build "$repo\src\BlockFerry.App.WinUI\BlockFerry.App.WinUI.csproj" -c Release -p:Platform=x64 -r win-x64 --no-restore

dotnet format "$repo\src\BlockFerry.Core\BlockFerry.Core.csproj" --verify-no-changes --no-restore
dotnet format "$repo\src\BlockFerry.App.WinUI\BlockFerry.App.WinUI.csproj" --verify-no-changes --no-restore
dotnet format "$repo\tests\BlockFerry.Core.SmokeTests\BlockFerry.Core.SmokeTests.csproj" --verify-no-changes --no-restore
dotnet format "$repo\tests\BlockFerry.Pcl2FixtureTests\BlockFerry.Pcl2FixtureTests.csproj" --verify-no-changes --no-restore
dotnet format "$repo\tests\BlockFerry.AppLogicTests\BlockFerry.AppLogicTests.csproj" --verify-no-changes --no-restore

& "$repo\src\BlockFerry.App.WinUI\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\BlockFerry.App.WinUI.exe"
```

以上命令必须串行运行，因为多个项目共享 Core intermediates。请只用演示数据或专门复制的测试实例做真实路径验收，不要拿唯一的正式 Minecraft 实例做开发测试。
