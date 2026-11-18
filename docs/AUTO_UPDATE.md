# 模组与启动器更新

> 最后更新：2026-11-18（0.7.0-beta）

## 1. 模组更新检查

入口：已安装模组 → **检查更新**。手动触发，不做后台轮询。

流程：

1. 只处理有 Nexus 关联（自动匹配或手动关联）的包；
2. 调用 `GetFilesAsync(modUid)`，遍历该 mod 的所有文件；
3. 对每个文件调用 `GetFileVersionsAsync(fileId)`；
4. 在**全部文件/版本**中按数值版本选出最高版本，而不是只取 `IsActive` 文件的主版本；
5. 与已安装包版本比较；
6. 命中时写入内存缓存，显示“可更新 vX”徽标；
7. 检查与更新共用同一份缓存，避免页面版本/文件版本不一致。

注意：

- 版本字符串用 `VersionText.IsNewer` 做数值比较；无法解析时退化为字符串比较；
- 已安装版本未知时视为可更新，但仍需用户确认；
- 一次检查可能产生 `文件数 × 版本请求`，受 Nexus 免费账号配额限制；
- Nexus 的“最新版本”不保证适合当前游戏版本，徽标只是提示。

## 2. 单项更新 / 一键更新

入口：

- 已安装页条目 `⋯ → 更新到 vX…`；
- 顶部“一键更新”（存在可更新项时显示）。

步骤：

1. 解析最新文件/版本；
2. 获取直链；免费账号有 403 时打开对应文件页并跳过，不绕过限制；
3. 可选“备份旧版本”（默认勾选）；
4. 将旧包移动到**同盘**的暂存目录 `包目录.mk-old-<id>`；
5. 若勾选备份，把暂存目录复制到：
   `%APPDATA%\ModificationsKnown\backups\<yyyyMMdd-HHmmss>\<包名>`
6. 调用安装器安装新包；如果归档是已知目标包，允许同时携带 `plugins` 根 DLL 与 `BepInEx/patchers` 附属文件；
7. 只有**全部包成功**才删除暂存目录并保留备份（如勾选）；
8. 失败：删除半成品，把暂存目录移回原位置；暂存不存在时绝不删除仍存活的安装。

Together 这类归档的严格路径：

- 目标包目录：`BepInEx/plugins/Together/`；
- 附属文件：`BepInEx/plugins/CUCoreLib.dll`、`BepInEx/patchers/CT_AutoUpdateApplier.dll`；
- `ArchiveInstallOptions(TargetPackageName, AllowAuxiliaryFiles)` 只在已知更新目标时启用附属文件；
- 普通安装仍拒绝无法归属的散放文件；散放根 DLL 不再出现在“可更新”列表。

为什么先移动到同盘：

- `Directory.Move` 不能跨卷；
- 同盘移动是原子/快速的回滚副本；
- 备份到 C 盘时用复制，允许游戏在 E 盘。

风险：

- 更新匹配依赖 Nexus game-scoped id、目录名或显示名，复杂多包归档可能只更新主包；
- 旧包有用户修改/新增文件时，当前实现仍会整体替换；更新前应确认；
- 备份不会自动清理；
- 没有依赖变更检查。

## 3. 启动器自更新

入口：设置 → **启动器更新**。

实现（`AppUpdateService`）：

1. `GET https://api.github.com/repos/HOPE-LGNF/modifications-known/releases/latest`；
2. 解析 `tag_name`、`html_url`、`assets`；
3. 用 `VersionText.IsNewer` 比较当前程序集版本；
4. 选择 `win-x64` 的 `.exe` 资产；
5. 读取 GitHub asset 的 `digest`（`sha256:...`）；
6. 下载到 `%TEMP%\ModificationsKnown-update`；
7. 边下载边计算 SHA-256，与 digest 比较；
8. 匹配后启动 PowerShell 辅助脚本：
   - 等待当前 MK 进程退出；
   - 覆盖原 exe；
   - 启动新 exe；
   - 删除脚本自身；
9. 当前进程退出。

校验策略：

- **有 digest**：必须匹配，否则删除下载、拒绝替换；
- **无 digest**：不自动安装，只打开 Release 页；
- 下载/校验失败：不覆盖当前版本。

尚未实现：

- Authenticode / 代码签名验证；
- 差分更新；
- 替换失败自动回滚旧 exe；
- 预览/稳定双通道（当前 `/releases/latest` 不返回预发布，Beta 需手动下载）。

## 4. 主流启动器的做法（调研）

| 类型 | 代表 | 更新方式 | 校验/回退 |
|---|---|---|---|
| Electron | Vortex、CurseForge、VS Code | `electron-updater`/Squirrel 检查元数据，后台下载，退出/重启时应用 | 通常校验 sha512/签名；失败保留旧版本 |
| 独立启动器 | Playnite | 检查 Release，下载安装包/便携包，退出后安装 | 明确做文件完整性校验，失败提示重新下载 |
| Java 启动器 | Prism Launcher | 独立 updater 进程（`PrismExternalUpdater`）在退出后替换文件 | 下载后校验；失败保留旧文件 |
| 大型平台 | Steam、GOG Galaxy | 专用 bootstrap/updater + manifest/块哈希 + 差分补丁 | 以文件清单/块哈希校验，失败可重下 |

共同点：

1. 更新器与主程序分离（或退出后应用）；
2. 下载后必须校验（哈希/签名/manifest）；
3. 失败保留旧版本，不能把用户留在半更新状态；
4. 发布元数据（版本、资产、校验值）与安装包一起发布。

参考：

- [Vortex updater docs](https://github.com/nexus-mods/vortex/blob/cbe8654355d315943da54b6d3cd728ae39e13e3b/docs/updater.md)
- [electron-updater reference](https://github.com/pedronauck/skills/blob/8a05756d6bdb8e1057769f9765ad1f13b5abd4ec/skills/curated/electron-builder/references/auto-update.md)
- [Playnite integrity check error](https://errors.standardbeagle.com/JosefNemec/Playnite/update-file-integrity-check-failed/)
- [PrismExternalUpdater](https://cdn.jsdelivr.net/gh/PrismLauncher/PrismLauncher@develop/launcher/updater/PrismExternalUpdater.h)

## 5. 下一步

1. 给启动器 Release 增加代码签名 / Authenticode 校验；
2. 保留上一版 exe，替换失败自动回滚；
3. 增加稳定/预览更新通道；
4. 更新前检测用户修改文件，提供“保留/取消”而不是整体替换；
5. 备份保留策略（最近 N 份/按时间清理）。
