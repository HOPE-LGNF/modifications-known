# 搜索 → 安装动线的 Nexus 依赖审计

## 当前调用链

```text
DiscoverView
  → DiscoverViewModel.SearchAsync
    → INexusModsClient.SearchModsAsync        (v3 POST /mods/search)
  → SelectModAsync
    → GetModAsync                              (v3 GET /mods/{uid})
    → GetFilesAsync                            (v3 GET /mods/{uid}/files)
    → GetFileVersionsAsync                     (v3 GET /mod-file-versions/...)
    → GetLegacyFileIdAsync                     (v1 GET /games/{game}/mods/{id}/files.json)
  → DownloadAsync
    → GetDownloadLinksAsync                    (v3 download-link)
      or GetNxmDownloadLinksAsync              (v1 download_link with key/expires)
  → DownloadFile
  → ArchiveModInstaller.InstallArchiveAsync    (local archive logic)
  → RecordSourceAsync                          (Nexus source + sidecar)
```

## 依赖等级

| 环节 | 依赖 | 等级 | 说明 |
|---|---|---|---|
| 搜索 | v3 search schema | 强 | `NexusSearchQuery`、`NexusModSummary` 直接映射 Nexus 字段 |
| 详情 | v3 mod schema | 强 | 描述、画廊、分类、作者 |
| 文件/版本 | v3 mod-files schema | 强 | 文件组、版本、分类 |
| 浏览器 file_id | v1 files schema | 强（Nexus 特有） | 只用于 Nexus 网页链接 |
| 下载链接 | v3 download-link / v1 NXM | 强 | `key/expires` 与 Nexus 账号绑定 |
| 压缩包安装 | 本地布局/清单 | 纯逻辑 | 与来源无关 |
| 扫描/启停/卸载 | BepInEx 文件 | 纯逻辑 | 与来源无关 |
| 手动关联 | NexusMatch | 强 | `GameScopedId/ModUid` 是 Nexus 标识 |
| 更新检测 | 尚未实现 | — | 未来需要来源适配 |

## 必须抽象的边界

1. `SearchProvider`：查询、分页、排序、过滤能力。
2. `ModMetadataProvider`：统一 mod 摘要/详情/分类。
3. `FileCatalog`：文件组、版本、平台/架构。
4. `DownloadResolver`：返回普通 URL 或需要用户授权的中断结果。
5. `SourceIdentity`：来源 ID、网页 URL、包来源字段。
6. `RateLimitPolicy`：把 Nexus 的 `x-rl-*` / GraphQL 配额封装在适配器内。

## 最小重构建议

- 先引入来源中立的 DTO（`SourceModSummary`、`SourceFileVersion`、`SourceDownload`），不直接改 UI；
- 用 `NexusSourceAdapter` 包装现有 `NexusModsClient`；
- 把 NXM 授权单独抽成 `NxmDownloadFlow`，不要混进通用下载器；
- `ArchiveModInstaller`、扫描、启停保持不动；
- 等真正接入第二个来源时再拆 `SearchProvider`/`MetadataProvider`，避免过早抽象。
