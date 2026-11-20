# 游戏版本、实例与 Mod 组

> 最后更新：2026-11-18（0.7.0-beta）

## 决策

采用 **版本即实例（Instance-per-version）+ Mod 组快照**。每个游戏目录拥有自己的
`BepInEx/plugins`、存档 profile、Nexus 来源和 Mod 组；Mod 组不跨实例共享。

## 数据模型（当前实现）

```text
SavedGameInstance (game-instances.json, schema v2)
  path              游戏根目录，实例身份
  alias             用户显示名
  profileId         full / demo / 自定义
  edition           正式版 / 试玩版
  steamAppId
  executablePath    手动添加或探测到的 exe

ModGroup (mod-groups.json，按游戏目录隔离)
  id
  name
  isDefault
  enabledPackageIds[]
  createdAt

AppSettings
  PreferredGamePath
  PreferredInstallationId（旧字段，兼容回退）
```

说明：

- `path` 是实例身份，不能只用 `profileId` 区分“同一版本的多个副本”；
- 启动时读取 `game-instances.json`，不再自动重新扫描 Steam；
- 只有显式“重新探测”或添加实例才更新列表；
- 删除实例会写 `hidden-instances.json`，防止被重新探测复活。

## 交互

### 版本管理（设置页）

- 列出实例：别名、edition、BepInEx 状态、路径；
- 重命名只改 `alias`，不改目录；
- 当前实例由 `Game.Selected` / `PreferredGamePath` 决定；
- 每个实例的 `⋯` 菜单包含：安装/卸载 BepInEx、下载 BepInEx、删除版本；
- 设置页支持首页深链：安装 BepInEx 时滚动并高亮当前实例的 `⋯`。

### BepInEx

- 安装：本地 zip 或打开 GitHub 最新稳定版；
- 安装前校验 `BepInEx/core`、`winhttp.dll`、`BepInEx/plugins`；
- 已存在 loader 时拒绝覆盖；
- 卸载：删除 `BepInEx/`、`winhttp.dll`、`doorstop_config.ini`、`.doorstop_version`，**同时删除该实例所有模组/配置**；两步确认 + 5 秒倒计时；
- 不跨实例复用 DLL。

### Mod 组

- 组属于实例；
- “保存当前状态”快照当前启用 package id；
- 在下拉栏选中组即立即应用该组的启用快照（逐包 `SetEnabledAsync`，显示逐包失败）；只有当前组存在“未保存到快照”的修改时才先弹确认；
- 默认组自动创建，不可重命名/删除；
- 未托管包默认不纳入组；
- 组切换不改 manifest、不移动目录，只改 `.dll` / `.dll.disabled`。

### 存档隔离

- 每个实例按游戏目录 hash 生成独立 profile；
- 启动前将 `%LocalLow%\Orsoniks\CasualtiesUnknown` junction 到当前 profile；
- profile 固定在默认 AppData 目录，不跟随自定义数据目录移动；
- 用户已确认 2026-11-15 真机回归成功；删除实例的存档复制/移动/保留流程也已确认。

### 更新

- 已安装页按 Nexus 来源检查最新文件版本；
- 单项/一键更新走 `PackageUpdateService`：同盘暂存、可选跨卷备份、失败恢复；
- 更新完成后通知已安装页清除徽标；
- 组不记录“更新前版本”，当前没有按组回滚更新。

## 迁移

1. 旧 `GamePath` / `PreferredInstallationId` 生成默认实例；
2. 旧 schema v1 的 path→alias map 迁移到 v2 完整记录；
3. 清单、manifest、enabled 状态不变；
4. 旧来源 sidecar 保留为兼容回退，manifest source 优先。

## 分阶段

1. ✅ 实例注册表、别名、当前实例；
2. ✅ 每实例 BepInEx 检测/安装/卸载；
3. ✅ Mod 组快照/应用/持久化；
4. ✅ 更新检测按实例 + Nexus 来源；
5. ⏳ 启动器自更新签名校验/回滚、按组回滚更新。

## 为什么不选“单安装 + 部署层”

BepInEx 通过物理 DLL 加载，不提供虚拟文件系统；部署层需要硬链接/符号链接/复制，
会引入 Windows 权限、跨盘、杀软、备份和卸载所有权问题。当前阶段收益不足。
