# 已知改装 / Modifications Known — 交接文档

> 更新时间：2026-11-18  
> 当前版本：`0.7.0-beta`  
> 仓库：<https://github.com/HOPE-LGNF/modifications-known>  
> Beta Release：<https://github.com/HOPE-LGNF/modifications-known/releases/tag/v0.7.0-beta>  
> 构建：.NET 8 / Avalonia 11.3 / Windows x64 单文件  
> 测试：78 core + 5 headless UI passed / 0 failed  
> 许可证：Apache-2.0

---

## 1. 当前状态

### 已完成并验证

- `dotnet build` 0 warning / 0 error。
- `dotnet test`：78 core + 5 headless UI passed。
- GitHub `main` 已推送，Beta Release 已发布：
  - tag：`v0.7.0-beta`
  - 资产：`ModificationsKnown-v0.7.0-beta-win-x64.exe`
  - SHA-256：`aeba515098bd8f1318362efd9a664d85ba9093227351819b7c6adb90cb3d54f0`
  - GitHub asset digest 与本地一致。
- 用户已确认真机成功：
  - 存档隔离真机回归；
  - 版本/实例删除真机回归。
- 已实现并完成代码级验证：
  - 发现页、已安装页、存档页、设置页、主题页；
  - 更新事务：同盘暂存、全成功才删暂存、失败恢复、内容相同记录新版本、Together 附属文件（manifest `auxiliaryFiles`）与卸载引用计数；
  - 多实例、BepInEx 安装/卸载、托管包清单；
  - Nexus 浏览/下载、NXM 回退；
  - 检查更新、单项更新、一键更新、跨卷备份/失败恢复；
  - 启动器 GitHub Release 检查、SHA-256 校验、退出后替换重启；
  - 自绘标题栏、hazard 图标、BETA 标识、三套主题；
  - 首页动态清单（含 NXM 步骤）与设置深链。

### 尚未由用户真机完成

- 更新安装全流程（跨盘备份、失败恢复、更新后徽标清除、一键更新）。
- BepInEx 卸载流程（两步确认、倒计时、运行中拦截、卸载后状态）。
- 启动器自更新替换/重启（代码已实现，未在真实 Windows 上完成一次升级）。
- 自绘标题栏在多 DPI、多屏、最大化/Snap Layouts 下的表现。
- 启动等待 20 秒、350ms 进程扫描在真实 Steam/非 Steam 游戏上的表现。

### 最新产物

```text
Release: ModificationsKnown-v0.7.0-beta-win-x64.exe
SHA-256: aeba515098bd8f1318362efd9a664d85ba9093227351819b7c6adb90cb3d54f0
```

桌面交付目录：

```text
C:\Users\liuchunyan\Desktop\Modifications Known\
```

配置文件备份（清理前）：

```text
C:\Users\liuchunyan\Desktop\MK-config-backup-20261115-1300.zip
```

> 2026-11-15 已按要求删除当时整个 `%APPDATA%\ModificationsKnown`（设置、实例、来源、Mod 组、活动、缓存、日志、save-profiles）；游戏目录与 BepInEx 未动。备份 zip 中保留了当时的 `save-profiles`。

---

## 2. 当前架构

```text
src/
  ModificationsKnown.Core/           领域模型、接口、路径安全
    Games/                           GameProfile, GameInstallation, launcher interfaces
    Mods/                            InstalledMod, PackageManifest, IModManager/IModInstaller
    Nexus/                           provider-neutral Nexus models
    Settings/                        AppSettings
    Network/                         IFileDownloader
    Common/                          OperationResult, PathSafety, VersionText

  ModificationsKnown.Infrastructure/ 实现
    Games/                           Steam locator, GameLauncher
    BepInEx/                         PE metadata, scanner, enable/disable/delete
    Installation/                    ArchiveModInstaller, BepInExInstaller,
                                     ModOperationService, PackageUpdateService
    Nexus/                           NexusModsClient
    Network/                         streaming downloader
    Storage/                         AppPaths, JSON settings, logger
    Windows/                         NxmProtocolRegistrar

  ModificationsKnown.App/            Avalonia UI
    ViewModels/                      Shell, Home, Discover, Installed, Saves, Settings, Themes
    Views/                           XAML + 少量 code-behind（拖拽、焦点、滚动、对话框）
    Services/                        GameContext, dialogs, thumbnails, update resolver,
                                     app updates, NXM flow, activity log
    Theming/                         ThemePalette, ThemeService
```

### 关键文件

- `ShellViewModel`：导航、启动/关闭、NXM 分发、Toast、设置深链、`ModsChanged` 通知；`SetStatus` = 结果（活动+Toast），`ReportProgress` = 仅状态栏。
- `GameProcessMonitor`：按 exe 路径检测实例进程；启动后 350ms 快速轮询，最多 20 秒。
- `DiscoverViewModel`：搜索/详情/下载/NXM/更新预检；403 文件页携带 `file_id` + `nmm=1`。
- `InstalledViewModel`：扫描、启停、更新检查、单项/一键更新、BepInEx 状态。
- `PackageUpdateService`（Infrastructure）：同盘暂存、跨卷备份、全成功提交、失败恢复、附属文件与内容校验。
- `SettingsViewModel`：实例、BepInEx 安装/卸载、NXM 注册、API Key、启动器更新、深链焦点。
- `AppUpdateService`：GitHub Releases API、版本比较、asset digest、SHA-256 下载校验。
- `SaveIsolationService`：NTFS junction 存档隔离（用户已真机确认）。
- `ThemePalettes` / `ThemeService`：苔原（默认）/ 日光 / 夜航。

### 数据位置

```text
%APPDATA%\ModificationsKnown\
  settings.json
  game-instances.json          schema v2
  hidden-instances.json
  installed-sources.json
  mod-groups.json
  activity.json
  themes/
  save-profiles/
  backups/
  logs/
  cache/
```

`save-profiles` 固定在默认 AppData，不跟随自定义数据目录移动。

---

## 3. 本轮（0.7.0-beta）主要改动

### 模组更新

- 已安装页“检查更新”：遍历 Nexus 全部文件/全部版本，取最高文件版本，修复“页面版本 vs 文件版本”不一致。
- 单项更新 / 一键更新；更新前先比较版本。
- 旧包先移动到同盘暂存目录；可选复制备份到 `backups`；跨卷安全；失败恢复。
- 更新完成后通知已安装页清除徽标与摘要，无需手动复查。

### NXM / 免费账号

- 下载/更新前检测“非 Premium + 未注册 NXM”，弹提醒框，带“NXM 是什么？”说明。
- 点“去注册 NXM”复用设置页注册流程（含覆盖确认/备份）。
- 403 页始终带 `nmm=1`，尽量解析 legacy `file_id`。
- NXM 到达时不再强制跳转“模组发现”。

### 实例 / BepInEx

- 卸载 BepInEx：两步确认 + 5 秒倒计时；删除 loader 与所有模组/配置。
- 首页“安装 BepInEx”跳到设置并高亮当前实例 `⋯`。
- 首页“注册 NXM”跳到设置 NXM 小节。
- 非 Steam 实例直接启动自身 exe；找不到 exe 时报错，不回退 `steam://`。

### 界面

- 默认主题恢复苔原；日光/夜航保留。
- hazard 图标：exe / 窗口 / 标题栏；BETA 徽标。
- 长标题不遮 Demo 提示；卡片名称行内布局。
- 关闭游戏按钮深红→亮红 hover；修复启动按钮发黑。
- 更新摘要成功后隐藏。
- README 重做，加入 5 张截图（`docs/images/`）。

### 启动器更新

- 设置页“检查更新/下载并安装”。
- GitHub `/releases/latest` + asset digest。
- 下载后 SHA-256 必须匹配；无 digest 不自动安装。
- PowerShell 等待进程退出→替换 exe→重启。
- Beta 为 pre-release，稳定版通道不会自动推送。

---

## 4. 待处理

### P0

1. **更新安装真机回归**  
   在 Windows 上实际更新一个 Nexus 包：旧包备份、跨盘、失败恢复、徽标清除、一键更新多项。
2. **BepInEx 卸载真机回归**  
   两步确认、5 秒倒计时、运行中拦截、卸载后实例状态与磁盘实际一致。
3. **存档隔离回归已完成**，不要重新破坏；改动 `SaveIsolationService` 前先备份。

### P1

- 别名链路真机确认（顶部下拉/首页/设置/重启持久化）。
- Mod 组完整流程（默认组、新建/重命名/保存/应用/删除、切换拦截）。
- BepInEx 安装：GitHub 最新稳定版适配、本地 zip、已存在拒绝、状态刷新。
- 灯箱滚轮/拖拽/DPI。
- 缩略图与菜单、Toast、窄窗口、自绘标题栏多 DPI。
- 启动等待 20 秒与提权进程检测。
- 免费账号更新回退页参数与一键更新跳过行为。
- 启动器自更新替换/重启/失败表现。

### P2

- 启动器更新：Authenticode、稳定/预览双通道、差分、失败自动回滚。
- 备份自动清理（最近 N 份）。
- Steam 全历史版本列表。
- `NexusModsClient` 的 `Application-Version` 仍写死 `0.1.2`，待改为程序集版本。
- 存档 profile 孤儿清理（目录移动/在 MK 外删除）。
- Mod 组“取消本次修改”回滚（按用户要求暂不做）。
- 更新前用户修改文件检测（当前整体替换）。
- 依赖图/冲突检测/崩溃归因/游戏内皮肤。
- 交互式新手导览（动态清单已做，遮罩导览未做）。
- 数据目录迁移完整回归、设置子路由/搜索。

完整列表见 `docs/DEFERRED.md`。

---

## 5. 构建 / 发布

### 环境

- WSL + `~/.dsh/tmp/dotnet8/dotnet`
- 必须设置 `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`
- Windows 发布需 `EnableWindowsTargeting=true`

### 构建

```bash
cd /home/unauth-hope/projects/modifications-known
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 ~/.dsh/tmp/dotnet8/dotnet build ModificationsKnown.sln -nologo
```

### 测试

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 ~/.dsh/tmp/dotnet8/dotnet test ModificationsKnown.sln -nologo
```

### 发布单文件

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 ~/.dsh/tmp/dotnet8/dotnet publish \
  src/ModificationsKnown.App/ModificationsKnown.App.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableWindowsTargeting=true \
  -p:DebugType=None -p:DebugSymbols=false \
  -o artifacts/win-x64
```

### 复制到桌面

```bash
cp artifacts/win-x64/ModificationsKnown.exe \
  "artifacts/ModificationsKnown-v0.7.0-beta-win-x64.exe"
sha256sum "artifacts/ModificationsKnown-v0.7.0-beta-win-x64.exe"

cp "artifacts/ModificationsKnown-v0.7.0-beta-win-x64.exe" \
  "/mnt/c/Users/liuchunyan/Desktop/Modifications Known/"
```

### 发布 GitHub

```bash
git add -A
git commit -m "..."
git push origin main

gh release create v0.7.0-beta \
  "artifacts/ModificationsKnown-v0.7.0-beta-win-x64.exe" \
  --target main \
  --title "已知改装 v0.7.0 Beta" \
  --notes-file /path/to/notes.md \
  --prerelease
```

### 注意

- **不要主动启动 GUI**；用户明确要求静默构建，由用户自己运行预览。
- 不要抢焦点；不要自动打开浏览器/窗口。
- 发布前确认桌面 exe 未被占用；C 盘空间很紧，先清理旧 preview。
- 不要在日志/回复中输出 API Key、NXM key；日志出现 `key=` 必须脱敏。
- 破坏性操作（删除配置、存档、实例、BepInEx）先确认/备份；用户明确要求删除时才执行。

---

## 6. 每轮输出要求（必须遵守）

每轮修改/交付后，最终回复至少包含以下内容：

1. **本轮改动**  
   按“问题 → 原因 → 修改”说明，不要只列文件名。

2. **涉及文件**  
   列出主要修改文件；必要时按领域分组。

3. **构建与测试结果**  
   `0 warning / 0 error`；测试通过数量；若未跑测试要说明原因。

4. **可执行的 Console 启动命令**  
   必须给出用户可直接复制运行的完整命令，例如：

   ```powershell
   & "C:\Users\liuchunyan\Desktop\Modifications Known\ModificationsKnown-v0.7.0-beta-win-x64.exe" --console
   ```

   如果本轮产物文件名不同，替换为实际文件名；不要只写 `dotnet run`。

5. **预览/产物信息**  
   - 本地 exe 路径；
   - SHA-256；
   - git commit；
   - 是否已 tag / Release；
   - 是否复制到桌面；
   - 是否启动过 GUI（默认“未启动”）。

6. **验收步骤**  
   给用户 3–6 条可操作验证项，覆盖本轮改动和风险点。

7. **风险/限制**  
   明确哪些只在 Linux 编译/离屏验证，哪些需要 Windows 真机；不要声称未验证的功能“已通过”。

8. **搁置事项**  
   每次交付末尾列出当前 P0/P1/P2 中仍未完成/被搁置的事项；已完成项从 `docs/DEFERRED.md` 删除。

9. **文档同步**  
   修改功能后同步 `README.md`、`CHANGELOG.md`、`docs/AUTO_UPDATE.md`、`docs/VERSION_MODEL.md`、`docs/DEFERRED.md` 中受影响的部分；不要留下“未实现更新检测”这类过期描述。

10. **语言与风格**  
    中文、直接、可执行；先给结论，再给细节；不写营销式空话。

---

## 7. 新会话 Prompt（可直接复制）

```text
你接手《已知改装 / Modifications Known》项目。先阅读仓库根目录 HANDOFF.md、README.md、CHANGELOG.md 和 docs/（尤其 DEFERRED.md、AUTO_UPDATE.md、VERSION_MODEL.md），再开始修改。

项目：/home/unauth-hope/projects/modifications-known
远程：https://github.com/HOPE-LGNF/modifications-known
当前版本：0.7.0-beta
Beta Release：https://github.com/HOPE-LGNF/modifications-known/releases/tag/v0.7.0-beta
桌面交付目录：C:\Users\liuchunyan\Desktop\Modifications Known
配置备份：C:\Users\liuchunyan\Desktop\MK-config-backup-20261115-1300.zip

当前重点：
1. Windows 真机回归更新安装（同盘暂存/跨卷备份/失败恢复/徽标清除/一键更新）；
2. Windows 真机回归 BepInEx 卸载（两步确认/倒计时/运行中拦截）；
3. 启动器自更新替换/重启与失败表现；
4. 多 DPI/多屏标题栏、启动等待与进程检测；
5. 免费账号更新回退页 file_id/nmm=1 与一键更新跳过行为。

请遵守：
- 不要主动启动 GUI，不要抢焦点；只构建/测试/发布，用户自己运行。
- 每轮最终输出必须包含：改动、涉及文件、构建/测试结果、可直接执行的完整 --console 命令、预览路径/SHA/commit、验收步骤、风险、搁置事项。
- 破坏性操作先确认/备份；不要读取或输出 API Key/NXM key；日志中 key 必须脱敏。
- 中文 UI；颜色走 ThemePalette；更新安装复用 PackageUpdateService 的暂存/备份/恢复语义。
- 修改功能后同步 README/CHANGELOG/相关 docs，不要留下过期描述。

WSL 构建：
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 ~/.dsh/tmp/dotnet8/dotnet build ModificationsKnown.sln -nologo
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 ~/.dsh/tmp/dotnet8/dotnet test ModificationsKnown.sln -nologo
```

---

## 8. 结论

0.7.0-beta 已把“发现 → 安装 → 启停 → 更新 → 备份/回滚 → 卸载 BepInEx → 启动器自更新”串成完整主链路，并且已有公开 Beta Release。

下一阶段最重要的不是继续加功能，而是：

1. 在真实 Windows 上完成更新安装与 BepInEx 卸载回归；
2. 验证自更新替换与失败回滚；
3. 处理多 DPI/多屏与提权进程检测；
4. 再决定是否做依赖图、崩溃归因、交互导览和稳定/预览双通道。
