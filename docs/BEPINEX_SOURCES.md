# BepInEx 获取渠道与安装策略

## 渠道对比

| 来源 | 版本类型 | 获取方式 | 校验 | 适用场景 |
|---|---|---|---|---|
| GitHub Releases | 官方 Stable / Bleeding Edge | 浏览器下载 zip | 版本号、架构、文件结构；发布页人工比对 | 默认首选，版本最可控 |
| Thunderstore | 社区 BepInExPack | 网页/社区包管理器下载 | 包版本、依赖、manifest、文件结构 | 游戏社区已有适配包时 |
| Nexus Mods | 作者上传的 BepInEx 文件 | 网页/NXM 下载 | 文件页、来源、哈希/人工比对 | 游戏有明确 BepInEx 页面时 |
| 本地压缩包 | 用户已有 | 文件选择器 | MK 校验 BepInEx/core + winhttp.dll | 离线、镜像、特殊版本 |

## 版本与架构

- 先确认游戏是 Mono 还是 IL2CPP、x64 还是 x86；
- 不要默认选最新 Bleeding Edge；优先 Stable；
- BepInEx 5 与 6 的文件布局不同，不能混用；
- 解压后必须存在：
  - `winhttp.dll`
  - `BepInEx/core`
  - `BepInEx/plugins`（缺失时创建）
- MK 不自动下载 loader，避免给错误的游戏版本安装不匹配的 loader。

## 当前实现（0.6.0）

- 设置 → 游戏与版本 → 每个未安装 BepInEx 的版本有“安装 BepInEx”；
- 点击后弹出两个选项：
  - **本地导入压缩包…**：本地 zip，复制前预检所有目标；
  - **GitHub 下载最新稳定版**：打开 `https://github.com/BepInEx/BepInEx/releases/latest`
    下载 zip，再回到“本地导入压缩包…”安装。
- Thunderstore / Nexus 入口已移除；BepInEx 只从官方 GitHub 获取或由用户本地导入。
- 安装完成后重新检测该实例的 BepInEx 状态。

## 版本选择

- CU 为 Mono Unity 游戏，使用 BepInEx 5（`winhttp.dll` + `BepInEx/core`）。
- `/releases/latest` 会跳过 6.x 预发布，指向最新的 BepInEx 5 稳定版；若后续确认需要固定版本，在这里改成具体 tag 即可（只需改 `SettingsViewModel.OpenBepInExDownloadAsync` 一个 URL）。
