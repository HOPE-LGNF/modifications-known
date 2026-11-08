# 游戏版本与 Mod 组：方案 A

决策：采用 **版本即实例（Instance-per-version）+ Mod 组快照**。每个游戏版本拥有自己的
游戏目录、BepInEx 和 plugins；Mod 组只保存该实例下的启用集合，不跨版本共享。

## 数据模型

```text
GameInstance
  id                稳定 GUID
  profileId         casual-unknown / casual-unknown-demo / 未来版本
  rootPath          游戏根目录
  alias             用户可重命名的显示名
  versionLabel      原始版本/edition 标签
  loaderState       none / installed / broken
  lastSeenAt

ModGroup
  id
  gameInstanceId
  name
  isDefault
  enabledPackageIds[]
  createdAt

ActiveGroupId        按 GameInstance 保存
```

持久化：

- `game-instances.json`
- `mod-groups.json`
- `AppSettings.ActiveInstanceId`
- `AppSettings.ActiveGroupId`

## 交互

### 版本管理（设置页）

- 列出所有实例：别名、原始 edition、路径、BepInEx 状态；
- 重命名只改 `alias`，不改目录；
- 检测到同一路径的新版本时更新 `versionLabel`，不重复添加；
- 当前实例切换写入 `ActiveInstanceId`；
- 未安装 BepInEx 的实例显示“安装 BepInEx”。

### BepInEx 安装

- 只在选定实例根目录解压；
- 校验 `BepInEx/core`、`winhttp.dll`、`BepInEx/plugins`；
- 失败不留下半成品；
- 不跨实例复用 DLL。

### Mod 组

- 组属于实例；
- “新建组”快照当前启用 package id；
- “应用组”逐包调用现有 `SetEnabledAsync`，显示逐包失败；
- 未托管包默认不纳入组，可在组编辑器里手动勾选；
- 组切换不改 manifest、不移动整个目录，只改 `.dll` / `.dll.disabled`。

## 迁移

1. 旧 `GamePath` / `PreferredInstallationId` 生成默认实例；
2. 旧清单、manifest、enabled 状态不变；
3. 组功能未启用时行为与当前版本一致；
4. 后续再迁移下载缓存和更新检测键（实例 + package + source）。

## 分阶段

1. 实例注册表、设置页版本列表、别名、当前实例；
2. 每实例 BepInEx 检测/安装；
3. Mod 组快照/应用/持久化；
4. 更新检测按实例 + 来源工作。

## 为什么不选单安装 + 部署层

BepInEx 通过物理 DLL 加载，不提供虚拟文件系统；部署层需要硬链接/符号链接/复制，
会引入 Windows 权限、跨盘、杀软、备份和卸载所有权问题。当前阶段收益不足。
