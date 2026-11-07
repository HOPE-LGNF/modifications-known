# Changelog

## [0.2.1] - 2026-11-08

### Added

- “接管”：为未托管目录生成基线清单，记录文件大小与 SHA-256；接管后可正常按清单卸载。接管前会二次确认，并明确只记录当前文件，不追溯历史。
- NXM protocol registration on Windows (`HKCU\Software\Classes\nxm`), with strict command shape `"<exe>" --nxm "%1"`. Refuses to overwrite another handler, and unregister only removes our own.
- Single-instance handling: a second launch with `--nxm` forwards the URL over a named pipe to the running window and exits.
- Settings page section for NXM integration with status and explicit register/unregister buttons.
- Nexus account check (`/v1/users/validate.json`) showing tier (Premium/free) and
  remaining rate-limit quota. This makes the free-account question directly
  observable instead of inferred from a 403.

### Fixed

- Single-instance detection now uses an exclusive lock file instead of a mutex, which behaves consistently across Windows and the Linux test environment.
- Forwarded NXM is dispatched to the UI thread and routed to the Discover page.


## [0.2.0] - 2026-11-07

### Added

- In-package manifest at `.modifications-known/manifest.json` with schema
  version, local id, package name, files, sizes, SHA-256 hashes and optional
  Nexus source.
- Strict install plan: ambiguous archive layouts are rejected; packages are
  preflighted before writes; per-package results report success, failure and
  partial completion.
- Staging now lives under `BepInEx/.modifications-known/staging`, outside the
  plugin loading directory but on the same volume, and the manifest is written
  before the final `Directory.Move`.
- Recursive DLL discovery inside a package, so nested plugin DLLs are scanned
  and toggled together.
- Managed vs unmanaged packages. Manually placed packages are visible but
  unmanaged; only manifest-managed packages can use “卸载”.
- Managed uninstall deletes only unchanged files listed in the manifest and
  preserves user-added/modified files. “删除整个目录” remains a separate,
  explicitly confirmed action.
- `InstalledMod` exposes mixed enabled state, missing/modified/unexpected files,
  management label and source.
- App-level `ModOperationService` serializes writes per game installation;
  manager operations now require an explicit `GameInstallation`.
- Installed list refreshes use generation tokens so switching game editions
  cannot paint an old scan over the new selection.
- Normal Nexus download inputs are pinned when the command starts.

### Changed

- `IModInstaller.InstallArchiveAsync` returns per-package results.
- `IModManager` has explicit `UninstallManagedAsync` and
  `DeleteWholePackageAsync`.
- The old sidecar source record remains as a compatibility fallback; manifest
  source is authoritative.


## [0.1.4] - 2026-11-07

### Fixed

- Installed packages downloaded through Nexus/NXM are now recorded with their
  source and shown as “Nexus 模组” instead of falling back to “本地模组”.
- Source matching is no longer only a name search: the installer result carries
  the actual package names, and those are recorded after install.
- Normal Nexus download inputs (mod/file/version) are pinned at command start so
  changing selection during a long download cannot attach the wrong source.

### Notes

- Existing installs made before this version have no source record; reinstall
  once to populate it.
- This is a sidecar record, not the final in-package manifest. The full manifest
  and managed/unmanaged split are the next architecture step.


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
