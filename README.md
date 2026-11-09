# 已知改装 / Modifications Known

> 为《未知伤亡》（Casualties Unknown）做一个安静、可靠、本地优先的模组管理器。

**当前版本：0.5.0（版本删除、图片灯箱与设置合并版）**  
**状态：** 模组浏览、安装、启停、删除、主题与设置已可用；依赖图、版本回滚、崩溃归因和游戏内皮肤仍在路线图中。

## 它是什么

“已知改装”是一个 Windows 桌面启动器，目标是把 CU 的 BepInEx 模组管理做得像成熟游戏启动器一样顺手，但**不替用户隐藏文件系统**：

- 通过 Steam `appmanifest` 识别正式版与试玩版，两个版本可以同时存在。
- 扫描 `BepInEx/plugins`，把一个文件夹视为一个模组包，递归识别嵌套 DLL，多 DLL 模组会整体启停。
- 安装时在包内写入 `.modifications-known/manifest.json`，记录文件、哈希和可选 Nexus 来源；因此能区分“已托管”和手动放入的“本地模组”。
- 按清单卸载只删除未修改的托管文件，保留用户新增/修改的文件；“删除整个目录”是单独的危险操作。
- 未托管的旧包可以“接管”：为当前目录生成基线清单，之后即可按清单卸载。
- 设置页可注册/取消 `nxm://` 协议；浏览器点击 “Mod manager download” 会回到启动器。
- 单实例：启动器已运行时，新的 NXM 启动会转发到当前窗口，不再开第二个进程。
- 安装先做整份归档的冲突预检，再逐包提交；部分成功会列出失败包，不会只显示“安装完成”。
- 操作写入按游戏安装串行化；切换游戏版本时会丢弃过期的扫描结果。
- 支持从 Nexus Mods 浏览、查看文件版本、下载并安装。
- 普通 Nexus 账号无法通过 API 获取直链时，可使用“浏览器下载 + 安装已下载文件”流程，或使用 NXM 粘贴/命令行。
- 安装前校验压缩包路径，拒绝 `..` 和绝对路径；先解压到临时目录，再移动到插件目录。
- 删除模组前默认二次确认；根目录 DLL 不会被误删。
- 启动器颜色、背景、强调色全部通过 `DynamicResource` 变量提供，支持基础主题切换。
- 游戏内皮肤入口保留，但明确标记为“开发中”，当前版本不会修改游戏皮肤目录。

## 截图 / 界面

当前界面包含：

- **概览**：游戏版本、BepInEx 状态、模组数量、快捷启动。
- **模组发现**：Nexus 搜索、排序、分页、详情、文件与版本选择。
- **已安装**：本地包扫描、启用/禁用、删除、打开插件目录。
- **启动器外观**：苔原 / 余烬 / 夜航三套主题变量。
- **游戏皮肤**：占位页，标注开发中。
- **设置**：游戏路径、Nexus API Key、游戏域、分页、成人内容过滤、日志目录。

## 环境要求

- Windows 10/11（当前主要目标；代码保留了跨平台结构）
- .NET 8 SDK
- 已安装《未知伤亡》与 BepInEx 5（模组功能需要）
- Nexus Mods 个人 API Key（可选；浏览公开数据可匿名，但下载通常需要）

## 构建

```bash
dotnet restore
dotnet build
dotnet run --project src/ModificationsKnown.App
```

NXM 粘贴测试（v0.1.2 新增）：

1. 在 Nexus 文件页右键“Mod manager download”，复制链接地址；
2. 打开“模组发现”，把 `nxm://...` 粘贴到“NXM 链接测试”；
3. 点“处理 NXM”。

也可以从命令行传入：

```powershell
.\ModificationsKnown.exe --nxm "nxm://scavprototype/mods/67/files/123456?key=...&expires=..."
```

也可以在设置页注册 `nxm://` 协议，让浏览器直接把 NXM 交给启动器。

需要详细日志时，用测试启动参数打开控制台：

```bash
dotnet run --project src/ModificationsKnown.App -- --console
```

Windows 发布版同样支持：

```powershell
.\ModificationsKnown.exe --console
```

日志同时写入 `%APPDATA%\ModificationsKnown\logs\`。`--debug-console` 是等价别名。

测试：

```bash
dotnet test
```

发布单文件（win-x64）：

```bash
dotnet publish src/ModificationsKnown.App -c Release \
  -r win-x64 --self-contained true \
  -p:PublishSingleFile=true
```

## Nexus Mods API

本项目使用 Nexus Mods **v3 REST API**（`https://api.nexusmods.com/v3`），使用 `apikey` 请求头认证。API 文档：<https://api-docs.nexusmods.com/>。

在应用内：

1. 打开“设置”。
2. 粘贴个人 API Key。
3. 确认游戏域为 `scavprototype`（正式版发布后如域名变化，可在此修改）。
4. 点击“测试连接”。

安全约定：

- API Key 只保存在本机 `%APPDATA%\ModificationsKnown\settings.json`，不会写入日志。
- 不要把 API Key 提交到仓库或发给他人。
- 免费账号可能无法直接生成下载链接；此时使用“在 Nexus 打开”，或配置 Premium 账号。应用会把 403 转成可读提示，不会崩溃。

API v3 目前仍被 Nexus 标注为实验性；所有 DTO 和端点集中在 `Infrastructure/Nexus/NexusModsClient.cs`，后续变更不会扩散到 UI。

## 目录结构

```text
src/
  ModificationsKnown.Core/          领域模型、接口、路径安全
  ModificationsKnown.Infrastructure/ Nexus、Steam、BepInEx、安装器、存储
  ModificationsKnown.App/            Avalonia UI、ViewModel、主题
tests/
  ModificationsKnown.Tests/          路径安全、安装器、扫描器、API 模型测试
docs/
  ARCHITECTURE.md                    分层与关键决策
  NEXUS_API.md                       API 契约与限制
  ROADMAP.md                         下一阶段计划
```

## 已知限制

- 搜索使用 Nexus GraphQL 的名称过滤；如果该接口变化，会回退到 v3 当前页过滤。
- 免费 Nexus 账号的下载链接可能返回 403；应用提供“浏览器下载”和“安装已下载文件”作为回退，不绕过 Nexus 的限制。
- 依赖解析、冲突图、版本回滚、备份和崩溃归因仍未实现；安装清单是这些功能的前置。
- 游戏内皮肤功能未实现，入口仅作占位。
- 启动器主题支持导入 JSON，模板见 [docs/themes/aurora.json](docs/themes/aurora.json)。
- 未实现自动更新；可行性分析见 [docs/AUTO_UPDATE.md](docs/AUTO_UPDATE.md)。

## 设计原则

1. **本地优先**：配置、日志、缓存都在用户目录，不依赖后端服务。
2. **路径安全**：所有压缩包路径、安装目标、删除目标都做 canonical path 校验。
3. **不静默覆盖**：安装不会覆盖已有包；失败不会留下半安装目录。
4. **可解释状态**：每个失败都尽量给出可操作的提示，而不是只显示异常。
5. **变量化外观**：页面不写死颜色；主题只替换资源。
6. **先做正确，再做丰富**：先把扫描、启停、安装、删除做可靠，再扩展依赖和版本。

## 许可证

Apache License 2.0。见 [LICENSE](LICENSE) 与 [NOTICE](NOTICE)。

第三方依赖保留各自许可证：Avalonia、CommunityToolkit.Mvvm、SharpCompress。
