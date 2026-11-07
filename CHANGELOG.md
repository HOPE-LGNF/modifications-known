# Changelog

## [0.1.3] - 2026-11-07

### Fixed

- Fixed the downloader moving the `.part` file while its `FileStream` was still
  open; this blocked downloads on Windows. Added a regression test.
- Fixed NXM parsing of links copied from page HTML: `&amp;` is now normalized to
  `&`, so `expires` is no longer reported as missing.
- Added TLS compatibility settings (TLS 1.2/1.3, HTTP/1.1) and detailed inner
  exception logging for CDN download failures.
- Added a “浏览器打开下载直链” fallback when a signed CDN URL cannot be fetched by
  the desktop HTTP stack.


## [0.1.2] - 2026-11-07

### Added

- NXM first-stage testing: paste a copied `nxm://` link into the Discover page,
  or pass it with `--nxm`.
- Strict `NxmLinkParser` validation for game domain, mod/file IDs and key/expiry.
- Legacy v1 NXM download-link lookup using `modId`/`fileId` and optional
  `key`/`expires`.
- Unit tests for NXM parsing and the v1 request shape (HTTP mocked, no live key).

## [0.1.1] - 2026-11-07

### Fixed

- Fixed the “包数量” badge alignment in the installed header.
- Launch button now shows a launching state and the status bar changes after the
  launch command is sent instead of staying on “正在启动…”.
- Nexus free accounts can now use a browser-download + local-archive-install flow
  when the API returns 403.

### Added

- `--console` / `--debug-console` startup option for a detailed debug console.
- Debug-level logs for Nexus requests, game detection, plugin scanning and install
  steps; all logs are still written to AppData.
- Nexus source matching for installed packages, with a `本地模组` label when no
  exact match exists.
- Enabled/disabled badges are now visually different.
- Compact/detailed installed-list display mode.
- Custom launcher theme import from JSON, with a template at
  `docs/themes/aurora.json`.
- Construction overlay on the game-skin placeholder page.

### Changed

- Removed the developer-only “变量化约定” section from the appearance page.
- Removed the “为什么先不做” copy from the game-skin placeholder.
- Updated GitHub release assets to 0.1.1.


## [0.1.0] - 2026-11-07

### Added

- New Avalonia/.NET 8 launcher named “已知改装 / Modifications Known”.
- Steam appmanifest detection for the full game (`4576490`) and demo (`4576510`).
- BepInEx plugin scanner that groups multiple DLLs into one package and reads
  `BepInPlugin`/`BepInDependency` metadata without loading assemblies.
- Enable, disable and delete operations with rollback on partial toggle failure.
- Safe archive extraction with path traversal checks and staged installation.
- Nexus Mods v3 client: browse, details, files, versions and signed download
  links, with a readable 403 fallback for free accounts.
- DynamicResource theme system with three initial launcher palettes.
- Settings for game path, Nexus API key/domain, page size, adult filter,
  delete confirmation and logs.
- Game skin navigation entry marked “开发中”; no game skin files are touched.
- Unit tests for path safety, archive install, package scanning and game detection.
- Apache-2.0 license and project documentation.
