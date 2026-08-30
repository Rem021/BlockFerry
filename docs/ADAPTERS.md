# 适配器契约与首发范围

## 启动器适配器

启动器适配器只负责定位和描述，不负责理解 Minecraft 内容。

**当前状态**：beta.5 已实现 PCL2 有限自动发现与手动文件夹入口，并将经过证明的隔离 gameRoot 交给四个内容适配器。实例解析仍然只读；真正写入由独立事务层执行，启动器适配器本身从不修改 PCL 元数据。

```csharp
public interface ILauncherAdapter
{
    string Id { get; }
    ValueTask<IReadOnlyList<LauncherInstallation>> DiscoverInstallationsAsync(CancellationToken ct);
    ValueTask<IReadOnlyList<GameInstance>> DiscoverInstancesAsync(LauncherInstallation install, CancellationToken ct);
    ValueTask<InstanceReadiness> ProbeReadinessAsync(GameInstance instance, CancellationToken ct);
}
```

`GameInstance` 至少包含：

- 启动器与实例稳定 ID；
- 实例元数据根目录与实际 `gameDir`，二者不能混用；
- Minecraft、加载器、整合包项目与构建版本；
- 是否隔离、是否导入完成、最近运行时间；
- 只读的显示名称、图标和来源路径。

0.1 目标支持等级：

1. 完整支持 PCL2：读取全局当前版本和实例 `PCL/Setup.ini`，解析隔离状态与真实 gameDir；
2. 完整支持 Prism/MultiMC：读取稳定的 `instance.cfg` 与组件元数据，解析 `minecraft` 或旧 `.minecraft` gameDir；
3. HMCL Beta：只读解析已知 schema，未知 schema 立即退化到目录模式；
4. Modrinth、CurseForge：只读识别或手动选择 gameDir，不写 SQLite、`minecraftinstance.json` 等私有元数据；
5. 手动目录始终可用，作为所有平台的安全兜底。

当前 PCL2 实现对未知旧隔离值保持阻断，不调用会写回 `VersionArgumentIndieV2` 的 PCL 初始化逻辑；junction、符号链接、网络重定向和不可信实例记录也会在读取或授权前拒绝。beta.5 不迁移任何 PCL 启动器专属设置。

ATLauncher、GDLauncher 等其他 Java 启动器可在用户明确选择独立实际 gameDir 后使用目录模式；这只代表已支持内容的目录兼容，不代表能自动发现实例或迁移启动器专属设置。Mojang 默认共享 `.minecraft`、封闭客户端、Bedrock 和移动版不在 0.1 原生适配承诺内。

用户看到的是同一个“一键同步”，底层则由各启动器适配器分别吸收实例位置、隔离规则、导入完成判断、运行进程、缓存和首启行为差异。解析出可信且独立的实际 gameDir 后，内容分析、快照、迁移、验证与回滚才复用共同内核。

默认实例建议必须由整合包身份与构建版本决定，不使用目录修改时间或名称里的脆弱字符串。

beta.5 不创建 `saves/` 或其他未由内容适配器声明的目录。任何未来的 PCL 首启兼容动作都必须先进入计划、清单、事务 allowlist 和回滚测试，不能由启动器适配器暗中执行。

## 内容适配器

生产 `IContentAdapter` 依次执行 `Probe`、`BuildCatalog`、`Plan`、`Stage`、`Verify` 和 `RegenerateAllowedPaths`。适配器只拿到只读、限额、generation 绑定的 `ContentProbeContext`；`Stage` 只生成内存中的候选字节，不能直接打开目标写句柄。事务层根据 accepted plan 重新生成封闭写入 allowlist。

每个计划项必须提供：稳定身份、来源值、目标值、最终值、风险等级、解释、写入路径、验证器和回滚信息。

## 首发内容规则

### Vanilla options

- 按第一个冒号拆分键和值；
- 用户字段从来源迁移；
- 目标的 `resourcePacks`、`incompatibleResourcePacks`、options `version` 默认保护；
- 未识别字段原样保留；
- 不产生重复键；
- 写回沿用目标编码与换行风格。
- 两侧均为受支持的 FancyMenu `3.x`、用户选择 `guiScale` 且目标首次启动标记缺失时，将来源端已验证标记作为同一 Vanilla 事务的第二个 mutation；不改写目标 FancyMenu 默认配置。

### 界面外观

- 只在两侧都证明 Minecraft `1.21.1`、modId `darkmodeeverywhere`，且版本都属于受支持的 `1.x` 系列时启用；
- 严格解析 `config/darkmodeeverywhereshaders.json` 的版本 2 schema，只迁移当前选中的 shader；
- 通过 shader JSON 的语义身份在目标数组中唯一映射，允许数组顺序变化，但重复或缺失匹配会阻止迁移；
- 仅替换目标 `selectedShaderIndex` 数字 token，保留目标 shader 定义、未知字段和原始格式。

### JEI 收藏

- 只在来源和目标都证明 Minecraft `1.21.1`、modId `jei`，且版本都属于受支持的 JEI `19.x` 系列时启用；
- 文件必须是受限大小的严格 UTF-8 JSON，顶层数组第一项是整数 `version: 2`；
- 迁移整个已审核收藏 scope，保留 JEI codec 中的物品/配方收藏对象，不猜测或逐项改写未知字段；
- 单人世界 scope 只接受精确目录名；服务器 scope 仅在目标运行时目录可唯一确认时映射，目标尚未进入服务器或存在歧义时安全阻止；
- 缺失、重复属性、不受支持的主版本、错误 schema 或超过读取限额时为 Unsupported，产生零 mutation；
- 检测到 EMI 时仅显示 `beta.5 暂不支持`，不读取或生成 `emi.json`。

### Extreme Sound Muffler

- 只在两侧都证明 Minecraft `1.21.1`、modId `extremesoundmuffler`，且版本都属于受支持的 `3.x` 系列时启用；
- 解析 `ESM/soundsMuffled.dat` 的受限严格 UTF-8 JSON 对象；
- 声音 ID 必须符合小写 namespace/path 规则，音量值只能在官方 `0.0..0.9` 闭区间；
- 非法 ID、NaN/Infinity、越界值、错误格式或不受支持的主版本全部 Unsupported，不做部分猜测写入。

## 能力声明

每个适配器随版本发布机器可读 manifest：

```json
{
  "id": "jei",
  "version": "1.0.0",
  "reads": ["config/jei/world/**"],
  "writes": ["config/jei/world/**"],
  "neverReads": ["accounts/**"],
  "executableContent": false,
  "supportsDryRun": true,
  "supportsRollback": true
}
```

UI 必须展示适配器将访问的范围；实际运行时再由核心执行路径沙箱检查，不能只相信声明。
