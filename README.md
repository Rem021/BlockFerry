<p align="center">
  <img src="src/BlockFerry.App.WinUI/Assets/AppIcon-1024.png" width="112" alt="BlockFerry 图标">
</p>

<h1 align="center">BlockFerry · 方块渡口</h1>

<p align="center">把 Minecraft 个人习惯安全带到新整合包，而不是复制整个实例。</p>

> [!IMPORTANT]
> 当前是 `0.1.0-beta.4` 测试版，面向 Windows 10/11、PCL2 与 Minecraft Java 1.21.1。请先看最终清单并保留应用生成的还原点。

## 它解决什么

升级整合包后，语言、按键、界面尺寸、音量、JEI 收藏和部分模组偏好经常要重新设置。BlockFerry 会先理解来源与目标的差异，再生成只读预览；只有用户确认最终清单后，才会进行带备份、复读验证和撤销能力的同步。

它不会复制旧版模组 JAR、脚本、世界、账号令牌或启动器私有数据库，也不会把整个 `.minecraft` 目录覆盖到新版。

## 当前能力

| 内容 | 当前行为 |
| --- | --- |
| Vanilla 设置 | 按字段合并语言、按键、声音、界面尺寸、FOV 与辅助功能，保护目标版本结构与资源包字段 |
| JEI 19.x | 同步单人和可安全识别的多人收藏；scope 不明确时阻止而不是猜测 |
| Dark Mode Everywhere 1.x | 同步当前深色模式与受支持的界面偏好 |
| Extreme Sound Muffler 3.x | 同步已验证格式的静音规则 |
| PCL2 实例 | 有限自动探测，也支持手动选择 PCL 根、`.minecraft`、`versions` 或具体实例目录 |
| 安全事务 | 创建还原点、原子替换、SHA-256 复核、中断恢复和安全撤销 |

暂不迁移世界、服务器列表、登录凭据、模组文件、KubeJS/脚本、资源包文件或启动器账号数据。

## 下载和使用

1. 在 [Releases](https://github.com/Rem021/BlockFerry/releases) 下载最新的 `BlockFerry-*-win-x64-portable.zip`。
2. 完整解压 ZIP；不要直接在压缩包内运行。
3. 双击 `BlockFerry.App.WinUI.exe`。当前测试版会显示一次 Windows 管理员授权提示，用于保留受保护文件的安全元数据。
4. 选择来源实例和目标实例，勾选要同步的内容。
5. 先点“检查同步计划”，确认最终清单后再执行。

应用启动后会向 GitHub 的公开 Releases API 发起一次只读检查。有新版本时，标题栏会出现入口；BlockFerry 不会自动下载或执行更新。离线、被限流或响应异常时，这一步会安静跳过。

## 安全与隐私

- 发现和预览阶段不写 Minecraft/PCL 实例。
- 真实写入只能发生在用户确认过的、generation 绑定的 immutable plan 中。
- 所有目标修改先备份，再暂存、替换、复读验证；失败时回滚。
- 不收集遥测，不上传实例路径、配置、收藏、服务器地址或设备标识。
- 为识别 JEI 局域网多人 scope，应用可能对 `latest.log` 中最后一次连接的 IP 地址发起一次 Minecraft 状态查询；不会发送账号或登录凭据。
- 本地状态位于 `%LOCALAPPDATA%\BlockFerry`，包括主题、已保护的最近位置以及恢复/撤销记录。

完整说明见 [PRIVACY.md](PRIVACY.md) 与 [SECURITY.md](SECURITY.md)。

## 开发与测试

需要 Windows 10/11、PowerShell 7、.NET SDK `10.0.302` 或兼容的 10.0.x SDK，以及安装了 `requirements-dev.txt` 的 Python 3。

```powershell
dotnet run --project tests/BlockFerry.Core.SmokeTests/BlockFerry.Core.SmokeTests.csproj -c Release
dotnet run --project tests/BlockFerry.Pcl2FixtureTests/BlockFerry.Pcl2FixtureTests.csproj -c Release
dotnet run --project tests/BlockFerry.ContentFixtureTests/BlockFerry.ContentFixtureTests.csproj -c Release
dotnet run --project tests/BlockFerry.TransactionFixtureTests/BlockFerry.TransactionFixtureTests.csproj -c Release -r win-x64
dotnet run --project tests/BlockFerry.AppLogicTests/BlockFerry.AppLogicTests.csproj -c Release -r win-x64
dotnet build src/BlockFerry.App.WinUI/BlockFerry.App.WinUI.csproj -c Release -p:Platform=x64 -r win-x64
```

全部自动测试只使用随机临时夹具，不应读取或写入真实 Minecraft 实例。提交和拉取请求还会在 Windows GitHub Actions 上重复执行核心、发现、内容、事务、UI 合约和 Release 构建门禁。真实整合包的测试方法见 [TESTING.md](TESTING.md)。

## 参与项目

欢迎提交可复现的 bug、兼容性样本和适配器提案。报告问题前请移除用户名、绝对路径、服务器地址、日志中的令牌和世界数据；安全漏洞请遵循 [SECURITY.md](SECURITY.md)，不要公开披露。

代码结构与适配原则见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) 和 [docs/ADAPTERS.md](docs/ADAPTERS.md)。

## 许可证与品牌

源代码以 [Mozilla Public License 2.0](LICENSE) 发布。你可以使用、修改和分发；修改过的 MPL 文件需要继续公开源码。`BlockFerry` 名称与图标不随源码许可授权，衍生版本请使用自己的名称和图标。

BlockFerry 是独立社区项目，不是 Minecraft 官方产品，也未获 Mojang Studios 或 Microsoft 批准、赞助或关联。Minecraft 是其各自权利人的商标。
