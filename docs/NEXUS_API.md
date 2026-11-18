# Nexus Mods API Notes

Base: `https://api.nexusmods.com/v3`  
Auth: `apikey: <personal API key>`  
Docs: <https://api-docs.nexusmods.com/>

## Endpoints used

| Operation | Endpoint | Notes |
|---|---|---|
| Search/browse | `POST /v3/mods/search` | Body filters by `games`, sort and page pagination; no free-text parameter in v3. |
| Name search | `POST /v2/graphql` | Used for text search with a `name { op: WILDCARD }` filter; falls back to v3 when unavailable. |
| Mod details | `GET /games/{game_domain}/mods/{game_scoped_id}` | Returns `data` with description, gallery, primary file. |
| Mod files | `GET /mods/{mod_id}/files` | `mod_id` is the composite id from search results. |
| File versions | `GET /mod-files/{mod_file_id}/versions` | Returns `data.versions`. |
| Download link | `GET /games/{game_domain}/mod-file-versions/{game_scoped_file_version_id}/download-link` | Premium/direct-download eligible; free accounts may get 403. |
| Trending (optional) | `GET /games/{game_domain}/trending-mods` | Public feed; currently not used by the UI. |

## Response shapes

Most v3 responses wrap the payload:

```json
{ "data": { ... }, "meta": { "page": 1, "page_size": 24, "total_count": 123 } }
```

The client handles the envelope in one place and maps snake_case DTOs into the
provider-neutral models in `Core/Nexus`.

## Constraints discovered

- The v3 `mods/search` filters expose game, time and adult filter, but **not a
  text/name filter**. The launcher uses Nexus GraphQL v2 for text search, and
  falls back to v3 page-local filtering if GraphQL changes.
- v3 is marked **Experimental** by Nexus. Endpoints may change; keep the adapter
  and contract tests updated.
- The download-link endpoint may return `403` for free accounts even with a valid
  API key. The correct fallback is opening the mod page in a browser; the app
  does not attempt to bypass this.
- The game domain used here is `scavprototype`, based on the current CU Nexus
  community URL. It is editable in Settings because the domain can differ after
  a release-page migration.
- Rate limits are enforced by Nexus. The UI performs one search request per
  explicit action and one details/files/versions request when the user opens a
  mod; it avoids background polling.

## Security

- API keys are never logged.
- The key is stored in `%APPDATA%\ModificationsKnown\settings.json`.
- Downloads use HTTPS and stream to a `.part` file with a 4 GB cap.
- Archive extraction rejects absolute paths and `..` traversal.

## Free accounts

The `download-link` endpoint requires either a premium account, a file with
direct downloads enabled, or a website-generated `key`/`expires` pair. A normal
personal API key alone cannot bypass that restriction.

The launcher therefore exposes:

1. **浏览器下载（免费账号）** — opens the exact Nexus file tab.
2. **安装已下载文件** — lets the user pick the downloaded archive; it goes
   through the same path-validated installer as an API download.

This keeps the flow honest and avoids scraping logged-in browser sessions.

## Debugging API calls

Start the launcher with `--console` (or `--debug-console`) to see detailed logs
for search, details, file/version lookups, download-link failures and install
steps. API keys are never logged.


## NXM 与免费账号

NXM 是 Nexus 文件页“Mod manager download”生成的 `nxm://` 链接。MK 的处理方式：

- 设置页/首页可注册当前用户的 `nxm://` handler（`HKCU`，无需管理员）；
- 已运行时，第二个进程通过命名管道把 NXM 转发给当前实例；
- 未运行时，`--nxm "<url>"` 或协议启动会进入同一处理链；
- 处理链使用 v1 `download_link.json`，带 NXM 的 `key`/`expires`，不把短期密钥写入日志；
- 403 时打开 Nexus 文件页，附带 `file_id`（可解析时）与 `nmm=1`，让用户完成 Slow download；
- 免费账号拿不到直链时不会绕过限制。

旧版曾提供“粘贴 NXM 测试框”；当前入口只有协议、命令行和单实例转发。

## 更新检查

已安装页手动“检查更新”使用：

```text
GET /mods/{mod_id}/files
GET /mod-file-versions/{mod_file_id}/versions
GET /games/{game_domain}/mod-file-versions/{id}/download-link
```

为了不漏掉非 active 文件中的新版本，检查会遍历 mod 的所有文件/版本，取版本号最高者。检查与更新共用同一份内存缓存，避免“检查说有更新、更新却说已是最新”。

限制：

- 每个 mod 可能产生多次 files/versions 请求，受免费账号配额约束；
- 版本字符串不规范时只能做保守比较；
- “有更新”不等于“适合当前正式版/Demo”，也不等于免费账号能一键下载。

## 安全

- API Key 只保存在 `%APPDATA%\ModificationsKnown\settings.json`，不写入日志；
- NXM 的 `key`/`expires` 只在内存与请求 URL 中使用；
- 下载使用 HTTPS，先写 `.part`；
- 压缩包拒绝绝对路径与 `..`；
- 启动器自更新只接受带 SHA-256 digest 的 GitHub asset。

## 调试

`--console` 会输出：

- Nexus 请求路径/状态（不含 API Key）；
- 更新检查选中的文件/版本；
- 更新回退页的完整 URL；
- 下载/安装/回滚结果。

```powershell
.\ModificationsKnown-v0.7.0-beta-win-x64.exe --console
```
