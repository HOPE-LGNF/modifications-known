# 已知改装 / Modifications Known — 交接文档

> 更新时间：2026-11-10  
> 当前版本：`0.6.0`  
> 仓库：<https://github.com/HOPE-LGNF/modifications-known>  
> 最新 Release：<https://github.com/HOPE-LGNF/modifications-known/releases/tag/v0.6.0>  
> 最新功能提交：`97c0819 fix: persistent instance list, delete countdown and settings polish`

---

## 1. 当前状态

### 已验证

- 52 个自动化测试通过。
- `dotnet build` 0 warning / 0 error。
- GitHub Actions CI 成功。
- Windows 单文件 exe 已发布并复制到桌面。
- 之前已验证：
  - NXM 协议注册、单实例转发；
  - NXM key/expires 下载；
  - 包清单、接管、按清单卸载；
  - 本地压缩包安装；
  - 手动关联 Nexus；
  - 发现页选择保持；
  - C/A 布局基础行为。

### 尚未由用户实测完成

- v0.6.0 的功能（作者已在 Linux 离屏渲染/编译检查，Windows 真机待确认）：
  - 实例列表 v2 持久化：启动不再自动扫描 Steam，只有显式“重新探测”才更新；
  - 删除版本倒计时、Steam 目录提示；
  - 添加实例菜单、移除“设为当前”、日志行对齐；
  - 可选数据目录（存档 profile 固定在默认目录）；
  - Hover Card 提示与页顶 Toast；
  - BepInEx 仅 GitHub 最新稳定版 + 本地导入。
- v0.5.0 的功能（作者已在 Linux 离屏渲染/编译检查，Windows 真机待确认）：
  - 版本删除两步向导、存档复制/移动/保持、可选删除实例文件夹；
  - 隐藏实例记录，刷新后不再复活；
  - 重命名按钮脏检查、Enter/Esc、保存中禁用；
  - Nexus 账号类型展示；
  - 图片灯箱（方图完整显示、滚轮缩放、拖拽、Esc）；
  - 设置页“游戏与版本”合并。
- v0.4.2 的弹窗/遮罩/外观卡（作者已在 Linux 离屏渲染检查，Windows 真机待确认）：
  - 弹窗背景改为可控 6px 模糊 + 180ms 淡入缩放；
  - 游戏皮肤遮罩只覆盖页面行，不再盖住顶部启动栏；
  - 启动器外观头部卡重排。
- v0.4.1 的界面修复：
  - 概览三张统计卡不再重叠；
  - hero 版本卡不再被拉高，字号已放大；
  - “当前”badge 已移到 BepInEx 状态之后；
  - 已安装页按钮分组与 Mod 组工具栏；
  - 弹窗改为居中圆角 + 背景压暗/模糊。
- v0.4.1 的 Mod 组重构：
  - 默认组初始化；
  - 新建/重命名/保存当前/应用/删除；
  - 未保存拦截与取消后恢复选中；
  - 同名、空名、默认组保护。
- v0.4.0 的存档隔离：
  - **没有在真实 Windows 存档上执行过完整回归**；
  - junction 创建、切换、回滚只在代码层审查过。
- v0.4.0 的 BepInEx 来源入口和本地 zip 安装（未变）：
  - 单元测试覆盖安装器；
  - Windows UI 流程未完整回归。

### 最新产物

```text
Release: ModificationsKnown-v0.6.0-win-x64.exe
SHA-256: 288a4bf5ed28e31eed762ef72720a299528a9cfe455737ed4bc855f88188312b

桌面:
C:\Users\liuchunyan\Desktop\Modifications Known\ModificationsKnown-v0.6.0-win-x64.exe
```

桌面目录应只保留最新版；旧版本 exe 删除。当前 `Downloads` 里的 v0.1.0 不属于桌面发布目录，未动。

---

## 2. 重要风险：存档隔离

正式版与 Demo 共用：

```text
%USERPROFILE%\AppData\LocalLow\Orsoniks\CasualtiesUnknown
```

存档为 `save.sv`，没有版本号字段。v0.4.0 在每次从 MK 启动前：

1. 按游戏目录 hash 生成独立 profile：
   `%APPDATA%\ModificationsKnown\save-profiles\<hash>\`
2. 首次遇到真实存档目录时：
   - 复制到 profile；
   - 原目录移动到 `save-backups\<profile>-<timestamp>`；
   - 创建 NTFS junction 指向 profile。
3. 切换版本启动时重定向 junction。
4. 真实目录和 profile 都已有文件时拒绝修改并阻止启动。
5. junction 指向 MK 之外时拒绝修改。
6. 创建/切换失败会尝试恢复，失败则阻止启动。

### 必须注意

- **不要在没有备份的情况下让测试者直接启动正式版/Demo。**
- 第一次迁移会移动真实存档目录；虽然保留 `save-backups`，仍应先让用户复制一份 `CasualtiesUnknown`。
- 从 MK 之外启动游戏时，游戏使用最后一次 MK 选中的 profile。
- 游戏目录移动后会生成新的 profile；旧 profile 不会自动迁移。
- 存档隔离只防止串档，不解决“旧存档没有版本字段、读入正式版走默认值”的游戏内部兼容问题。
- `DirectoryInfo.LinkTarget` 对 junction 的读取、`Directory.Delete` 对 junction 的删除行为，需要在真实 Windows 上验证；不要只依赖单元测试。
- 非 Windows 环境直接跳过隔离，不能作为 Windows 行为已验证的依据。

### 建议验证顺序

1. 复制一份可丢弃的 `CasualtiesUnknown` 存档目录；
2. 用 Demo 启动，创建测试存档；
3. 切正式版启动，确认不会看到 Demo 存档；
4. 切回 Demo，确认原存档仍在；
5. 检查 `save-profiles` 两个目录、`save-backups` 备份；
6. 再测试 junction 创建失败/外部 junction 的拒绝路径。

---

> 统一搁置清单见 `docs/DEFERRED.md`；新手引导分析见 `docs/ONBOARDING_ANALYSIS.md`。

## 3. 当前功能和关键文件

### 版本实例（Feat-3 方案 A）

- `GameInstallation.Alias`、`DisplayLabel`
- `AppSettings.PreferredGamePath`
- `GameInstanceStore`
  - `%APPDATA%/ModificationsKnown/game-instances.json`
- `GameContext`
  - 合并检测结果和别名；
  - `SelectByPath` / `Rename`；
  - `PreferredGamePath` 优先，旧 `PreferredInstallationId` 回退。
- `GameVersionRow`
- `SettingsView` 的“版本管理”区。

### BepInEx 安装

- `BepInExInstaller`
  - 支持 zip/7z/rar 等 SharpCompress 格式；
  - 校验 `BepInEx/core`、`winhttp.dll`；
  - 复制前预检所有目标文件；
  - 已存在 BepInEx 时拒绝覆盖。
- `SettingsViewModel.InstallLoaderAsync`
  - 本地压缩包选择；
  - 安装后重新检测。
- 来源入口：
  - GitHub Releases；
  - Thunderstore BepInExPack 搜索；
  - Nexus BepInEx 搜索。
- `docs/BEPINEX_SOURCES.md`

### Mod 组

- `ModGroup`
  - `Id`、`Name`、`EnabledPackageIds`、`CreatedAt`、`IsDefault`。
- `ModGroupStore`
  - `%APPDATA%/ModificationsKnown/mod-groups.json`
  - 按游戏目录隔离。
- `InstalledViewModel`
  - 默认组自动创建；
  - 新建组、重命名、保存当前、应用、删除；
  - 默认组不可重命名/删除；
  - 空名/重名拒绝。
- 应用组复用 `SetEnabledAsync`，逐包执行，慢但可控。

### 发现页 / Nexus

- `DiscoverViewModel`
  - 选择保持：ListBox `SelectedItem` OneWay，只有显式选择另一个模组才替换；
  - 分类/搜索/翻页不清空右侧详情；
  - 版本选择在“下载并安装”Flyout 中；
  - `nmm=1` 链接；
  - 旧版 v1 `file_id` 解析。
- `NexusModsClient`
  - v3 REST、v2 GraphQL、v1 NXM/validate/files；
  - `Application-Name` 仍写死旧值 `ModificationsKnown`，版本头写死 `0.1.2`，**待修**。
- `NxmProtocolRegistrar` / `SingleInstanceService`
  - NXM 注册、单实例转发。

### 文档

- `docs/ARCHITECTURE.md`
- `docs/NEXUS_API.md`
- `docs/AUTO_UPDATE.md`
- `docs/VERSION_MODEL.md`
- `docs/BEPINEX_SOURCES.md`
- `docs/SOURCE_AUDIT.md`
- `docs/mockups/installed-items.png`

---

## 4. 待处理事项

### P0 / 必须先做

1. **Windows 真机验证存档隔离**
   - 这是唯一可能破坏玩家存档的功能；
   - 先备份，再按第 2 节顺序测试；
   - 若 junction 行为与预期不符，优先修 `SaveIsolationService`，不要发布新版本。

2. **验证 v0.4.0 别名链路**
   - 设置改别名 → 顶部下拉栏、首页标题、当前版本标签是否同步；
   - 重启后是否持久化；
   - 不要只改 `Profile.DisplayName`，统一使用 `DisplayLabel`。

3. **验证 R4 布局**
   - “当前”badge 是否仍被拉伸；
   - 概览右侧是否与 hero 同高；
   - Nexus 卡片是否还有空白；
   - 如果仍异常，需要实际截图 + 控件树，不要继续猜样式。

### P1

4. **Mod 组流程回归**
   - 默认组是否首次自动出现；
   - 新建/重命名/保存当前/应用/删除；
   - 同名/空名/默认组保护；
   - 切换版本后组列表隔离；
   - 组应用后未在组内的新模组会被禁用，是否符合预期需和用户确认。

5. **BepInEx 来源与安装**
   - 三个来源入口是否都能打开；
   - 本地 zip 是否能安装；
   - 已存在 loader 的目录是否拒绝；
   - 安装后 `GameVersions` 状态是否刷新。

6. **更新检测仍未实现**
   - 手动 Nexus 关联已持久化；
   - 但没有任何“检查更新/可更新”逻辑；
   - 不要声称更新检测已完成。

### P2

7. **版本列表不是 Steam 全历史**
   - 目前仅列检测到的安装目录；
   - 要列未安装的历史版本，需要 Steam depot/manifest 数据源。

8. **Nexus 应用头信息**
   - `NexusModsClient` 里 `Application-Version` 还是 `0.1.2`；
   - 应改为程序集版本或统一常量，避免 Nexus 侧版本归因错误。

9. **存档 profile 孤儿清理**
   - 游戏目录移动/删除后，旧 profile 仍保留；
   - 没有清理/迁移 UI。

10. **组编辑的“取消”语义**
    - 当前切换组会重置未保存的名称草稿；
    - 没有独立“取消编辑”按钮；
    - 如果用户需要更明确的编辑模式，可后续加。

### 架构前置（不要贸然做）

- `docs/SOURCE_AUDIT.md` 已给出 Nexus 强耦合点：
  - 搜索、详情、文件、下载、认证强依赖；
  - 安装/扫描/启停是纯逻辑；
  - 先做来源中立 DTO + `NexusSourceAdapter`，不要直接大改 UI。

---

## 5. 构建 / 发布

### 构建环境

WSL 使用：

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
~/.dsh/tmp/dotnet8/dotnet build ModificationsKnown.sln -nologo
```

测试：

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
~/.dsh/tmp/dotnet8/dotnet test ModificationsKnown.sln -nologo
```

Windows 单文件发布：

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
~/.dsh/tmp/dotnet8/dotnet publish \
  src/ModificationsKnown.App/ModificationsKnown.App.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableWindowsTargeting=true \
  -p:DebugType=None -p:DebugSymbols=false \
  -o artifacts/win-x64
```

### 发布流程

1. 更新 `Directory.Build.props` 版本；
2. 更新 `CHANGELOG.md` / `README.md`；
3. 构建、测试、发布；
4. `cp artifacts/win-x64/ModificationsKnown.exe artifacts/ModificationsKnown-vX.Y.Z-win-x64.exe`；
5. 计算 SHA-256；
6. commit + tag + push；
7. `gh release create` + upload；
8. 停止桌面正在运行的旧 exe；
9. 复制新 exe 到：
   `C:\Users\liuchunyan\Desktop\Modifications Known\`
10. 删除同目录旧 `ModificationsKnown-v*-win-x64.exe`；
11. 确认桌面目录只剩当前版本。

### 注意

- 不要用 `pkill -f 'ModificationsKnown.dll'` 直接杀，容易误杀当前 bash 包装进程；用保存的 PID。
- 不要读取/输出用户 API Key、NXM key；日志里出现 `key=...` 必须脱敏。
- 桌面复制前先 `test -f` 校验源路径，避免相对路径错误。
- 发布前先确认 v0.4.0 的存档隔离已经过用户验证；否则不要继续发布。

---

## 6. 重要设计约束

- **Apache-2.0**，.NET 8 / Avalonia 11。
- UI 文本中文。
- 颜色/背景通过 `ThemePalette` 变量；不要硬编码 Windows 系统色。
- 系统强调色必须继续覆盖 `SystemAccentColor` 等 Fluent 资源。
- NXM 只通过注册协议、`--nxm` 或单实例转发进入，不做粘贴框。
- 免费账号下载仍受 Nexus NXM 授权约束，不能绕过。
- 不内置个人 API Key；每用户自己的 Key / 未来 SSO。
- 版本管理使用方案 A：版本即实例、组快照按实例隔离。
- 修改组/启用状态时优先复用 `SetEnabledAsync`，不要直接改文件名绕过清单。
- 存档隔离是 P0 风险功能，默认不要在测试前删除备份。

---

## 7. 新会话 Prompt（可直接复制）

```text
你接手《已知改装 / Modifications Known》项目。请先阅读仓库根目录 HANDOFF.md、AGENTS.md 和 docs/，再开始任何修改。

项目：/home/unauth-hope/projects/modifications-known
远程：https://github.com/HOPE-LGNF/modifications-known
当前版本：0.4.1
最新 Release：https://github.com/HOPE-LGNF/modifications-known/releases/tag/v0.6.0
最新功能提交：7a4b213 fix: repair overview layout, dialog overlays and mod-group toolbar
桌面交付目录：C:\Users\liuchunyan\Desktop\Modifications Known

先不要改代码，先完成 HANDOFF.md 第 4 节的验证顺序，尤其是：
1. Windows 真实存档隔离（P0，可能破坏存档，必须先备份并让用户确认）；
2. 别名修改后顶部启动下拉栏/首页/当前版本是否同步；
3. R4 的 badge、概览右侧高度、Nexus 卡片空白；
4. Mod 组默认组与新建/重命名/保存当前/应用/删除流程；
5. BepInEx 三个来源入口与本地 zip 安装。

验证时向用户索取截图/日志；不要只凭代码猜测 UI。若仍需改 UI，先说明实际控件树或布局根因。

继续开发时请遵守：
- .NET 8 + Avalonia，Apache-2.0；
- UI 中文；
- 颜色走 ThemePalette，不硬编码 Windows 系统色；
- 不读取或输出 API Key/NXM key，日志中出现 key 必须脱敏；
- 免费 Nexus 下载走 NXM，不绕过；
- 版本管理维持方案 A：版本即实例、Mod 组按实例快照；
- 修改启用状态复用 SetEnabledAsync，不直接破坏清单；
- 存档隔离测试前必须备份，junction 行为必须在真实 Windows 上确认；
- 不要贸然重构 Nexus 搜索/下载层，先按 docs/SOURCE_AUDIT.md 的边界做小步解耦；
- 更新版本后：测试、构建、发布 Release、复制 exe 到桌面并删除旧版。

WSL 构建使用：
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 ~/.dsh/tmp/dotnet8/dotnet build ModificationsKnown.sln -nologo
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 ~/.dsh/tmp/dotnet8/dotnet test ModificationsKnown.sln -nologo

当前已知未完成项：
- 存档隔离尚未真机回归；
- R4 布局/别名/Mod 组流程待用户确认；
- 更新检测未实现；
- 版本列表不是 Steam 全历史；
- NexusModsClient 的 Application-Version 仍为 0.1.2；
- 存档 profile 孤儿清理未做。

请用专业、直接的中文汇报：每项给问题定位、修改、风险、验证步骤；涉及数据/存档的操作先确认再执行。
```

---

## 8. 交接结论

当前代码已经具备“发现 → 下载/安装 → 清单管理 → 启用/禁用 → 版本实例 → Mod 组 → 存档隔离”的完整主链路，v0.4.1 已修复概览重叠、badge 顺序、已安装页按钮和弹窗样式，并重构 Mod 组工具栏；**存档隔离仍只完成了 junction 创建/读取的间接确认，跨版本互不可见仍需真机回归**。

下一会话最重要的不是继续加功能，而是：

1. 先验证存档不会串档；
2. 再确认别名和布局；
3. 再回归 Mod 组；
4. 最后决定是否开始更新检测和来源抽象。
