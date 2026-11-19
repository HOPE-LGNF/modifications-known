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
6. 调用安装器安装新包；
7. 成功：删除暂存目录，保留备份（如勾选）；
8. 失败：删除半成品，把暂存目录移回原位置。

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

### 3.1 更新通道

- **稳定版（默认）**：只接收正式版，走 `GET /releases/latest`。
- **预览版**：在全部公开发行（alpha / beta / rc 都算）里按语义版本取最高；正式版同样参与，不把“预览”理解成“只收 beta”。
- 选择保存在 `settings.json` 的 `UpdateChannel`（`stable` / `preview`）；缺字段、未知值、`null` 一律按稳定版读取，不会因为这一个字段损坏而清空 API Key 等其它设置。
- 切换遵守“改动 → 保存 / 撤销”：保存成功后清空旧候选并提示重新检查，**不会自动发请求，也不会自动降级当前程序**。

### 3.2 版本比较

启动器使用独立的 SemVer 2.0 比较器（模组更新仍用 `VersionText`，两者互不影响）：

- 支持单个 `v`/`V` 前缀、三段数字、预发布标识、build metadata；
- `0.8.0-beta.2 < 0.8.0-beta.10 < 0.8.0-rc.1 < 0.8.0 < 0.8.1-beta.1`；
- `+commit` 只用于展示，不参与排序；
- 坏格式、前导零、超长、溢出一律拒绝，不做字符串兜底猜测。

当前版本从程序集的 `AssemblyInformationalVersion` 读取。**发布构建必须显式传 `-p:Version=<tag>`**（例如 `0.8.0-beta.2`），否则预发布标识会丢失，二进制自报成正式版号。

### 3.3 发行与资产契约

- 无预发布后缀的 tag 必须对应 `prerelease=false`，带后缀的必须对应 `prerelease=true`；矛盾的条目跳过并记录原因，避免误投稳定通道。
- 历史包袱：v0.4.0、v0.3.1、v0.3.0、v0.2.x、v0.1.4 都是“普通 tag + `prerelease=true`”。按上面的规则它们会被预览通道忽略；这些版本都低于当前线，不影响实际升级。
- 预览通道最多 10 页 / 30 秒；分页中途失败、链接越界或重复都算检查失败，不会把半份列表当作最新，也不会给安装入口。
- 自动安装要求：唯一的 `win-x64` exe、合法的 `sha256:<64 hex>` digest、且下载地址是本仓库的 GitHub Release 链接。任一不满足只取消一键安装，仍可打开 Release 页手动下载。

### 3.4 现有流程

1. `GET`：稳定版 `/releases/latest`，预览版 `/releases?per_page=100` 并跟随 `Link rel=next`；
2. 过滤 draft、非法 tag、标记冲突，交给纯策略选出候选；
3. 候选严格高于当前版本才提示升级；
4. 下载到 `%TEMP%\ModificationsKnown-update`，边下边算 SHA-256 并与 digest 比对；
5. 匹配后启动 PowerShell 辅助脚本：等待当前进程退出、覆盖 exe、启动新 exe、删除脚本；
6. 当前进程退出。

校验策略：

- **有 digest**：必须匹配，否则删除下载、拒绝替换；
- **无 digest**：不自动安装，只打开 Release 页；
- 下载/校验失败：不覆盖当前版本，候选保留以便重试。

尚未实现：

- Authenticode / 代码签名验证；
- 差分更新；
- 替换失败自动回滚旧 exe；
- 旧二进制（0.7.0-beta 自报 0.7.0）无法自识别为预发布，升级到 0.7.0 正式版时需手动下载。

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
3. 更新前检测用户修改文件，提供“保留/取消”而不是整体替换；
4. 备份保留策略（最近 N 份/按时间清理）。
