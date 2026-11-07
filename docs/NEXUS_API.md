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
