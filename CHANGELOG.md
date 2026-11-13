# Changelog

## [0.6.0] - 2026-11-13

### Fixed

- Delete-version step 1: switching from “保持原位” back to “复制到指定目录” now
  re-enables the target directory row (the radio handler trusted the stale
  checked state instead of the radio that raised the event).
- Instances added manually no longer disappear: the hidden-instance list was
  removed, and detection is no longer run on every startup. `game-instances.json`
  is now schema v2 (full records), migrated from the old path→alias map.
  Startup loads the saved list; Steam is re-scanned only from 添加实例 → 重新探测.
- Settings keeps the “当前” badge in sync when the top ComboBox switches
  instances (subscribes to `Game.Changed`).
- “打开日志目录” now aligns with the log path row instead of the delete toggle.

### Added

- Version deletion step 2 disables “确认删除” for five seconds and shows the
  remaining seconds on the button.
- “添加实例” menu: choose executable, choose folder, or re-scan Steam; manual
  adds inspect only that path instead of re-detecting every version.
- Optional data root (settings/logs/caches metadata) via a `location.json`
  pointer; save profiles deliberately stay in the default AppData root so the
  live junction cannot be orphaned. Restart required.
- Hover-card “?” hints (no native tooltip) and a page-top transient toast that
  mirrors the status bar.
- Steam-library warning in the delete wizard; BepInEx management now only
  offers GitHub latest stable + local zip import.
- `docs/ONBOARDING_ANALYSIS.md` (analysis only) and `docs/DEFERRED.md` (unified
  deferred list). Removed the per-version “设为当前” button.
## [0.5.0] - 2026-11-10

### Added

- Version deletion with a two-step wizard: archive saves by copy/move, or keep
  the managed save directory in place; optionally delete the game folder
  (default off). The second step lists every affected item in red before the
  destructive action. Records are hidden in `hidden-instances.json` so detection
  does not resurrect them, and every execution step reports success/failure.
  Deletion is blocked while the instance is running; the live save junction is
  detached when its profile is removed.
- Full-window image lightbox for Nexus screenshots: aspect-fit with
  `min(availW / w, availH / h, 1)`, no crop or stretch; wheel zoom anchored at
  the cursor, clamped panning, Esc/backdrop/close, fit-on-resize.
- Overview Nexus card now shows the account tier (免费账号 / Premium) and quota,
  cached for five minutes.

### Changed

- Rename save button in version management is hidden until the trimmed text
  differs; it is accent-highlighted, disables while saving, supports Enter to
  save and Esc to restore.
- “游戏与安装” and “版本管理” are merged into one “游戏与版本” card; bindings and
  settings keys are unchanged, and the settings nav subtitle now covers game,
  versions and Nexus. The image lightbox fixes the square-image crop caused by
  the old fixed 210/480 px container plus `UniformToFill`.
## [0.4.2] - 2026-11-10

### Changed

- Modal backdrop no longer relies on the system acrylic blur: the owner content
  gets a tunable 6 px blur, the scrim is lighter, and the card fades/scales in
  over 180 ms instead of appearing abruptly.
- The game-skin “施工中” veil moved from the page into `MainWindow`, so it now
  covers the header and the status bar as well as the page body.
- Launcher appearance header card rebuilt: the import action sits under the
  description, and the current theme is a labelled swatch block instead of a
  floating chip with a detached button.

## [0.4.1] - 2026-11-10

### Fixed

- Overview: the three stat cards overlapped because only the hero panel set
  `Grid.Column`; the cards now occupy their own columns, and the “从这里开始”
  card no longer stacks on “当前安装”.
- Overview: the hero version card no longer stretches to the hero height; its
  content is centred and the edition/BepInEx text is larger.
- Version management: the “当前” badge now follows the BepInEx status instead of
  sitting between the edition and loader labels.
- Installed: the action buttons were a single cramped row of mixed styles. They
  are now grouped — scan/open-directory on the title row, view/enable/disable/
  local-install in a toolbar — with 重新扫描 as the primary action.
- All dialogs now use a borderless full-window overlay with a dimmed, blurred
  backdrop and a centred rounded card, including the file/version picker that
  used to be a Flyout anchored to “下载并安装”.

### Changed

- Mod group toolbar: `ComboBox` + state-aware `保存当前状态 / 保存修改 *` button +
  `⋮ 更多操作` menu (new/rename/delete). New and rename use a centred text
  prompt instead of an inline text box; delete asks for confirmation.
- Group switching is intercepted when there are unsaved edits; cancelling
  restores the previous ComboBox selection. Unsaved state no longer changes
  simply because another group was selected.

## [0.4.0] - 2026-11-09

### Fixed

- Version aliases now feed the top launch ComboBox through `DisplayLabel`;
  changing an alias updates the selected display and the header label.
- The “当前” badge in version management is vertically centered, removing the
  stretched blank block.
- Overview’s right-side version panel now stretches to the hero height with a
  surface card; the Nexus status card top-aligns instead of stretching to the
  left card height.
- Mod groups now create a persistent “默认组” per instance and select it by
  default.
- Mod group editing is explicit: 新建组 / 重命名 / 保存当前 / 应用 / 删除组.
  Duplicate and empty names are rejected; the default group cannot be renamed
  or deleted; current enabled state can be saved into the selected group.

### Added

- Per-installation save isolation before launch. `%LocalLow%\Orsoniks\CasualtiesUnknown`
  is junctioned to a profile keyed by the game directory, so Demo and full-game
  save.sv cannot overwrite each other.
- BepInEx source links: GitHub Releases, Thunderstore BepInExPack search, Nexus
  BepInEx search; local archive install remains the validated path.
- Source dependency audit (`docs/SOURCE_AUDIT.md`) and BepInEx channel comparison
  (`docs/BEPINEX_SOURCES.md`).


## [0.3.1] - 2026-11-08

### Fixed

- BepInEx archive installation now preflights every destination file before
  copying, so an existing file cannot leave a half-installed loader behind.


## [0.3.0] - 2026-11-08

### Added

- Scheme A version management: per-installation aliases, current-version
  switching, persisted `game-instances.json`, and a version list in Settings.
- BepInEx installation from a user-supplied archive, validated before copying
  into the selected game directory.
- Mod Group snapshots per installation: save current enabled packages, apply a
  group, delete a group; stored in `mod-groups.json` and isolated by game path.
- `PreferredGamePath` and legacy `PreferredInstallationId` migration.

### Changed

- Game labels now use the user-defined version alias where available.
- Installed page exposes the Mod Group bar above the list.

### Notes

- Mod Group application uses the existing enable/disable file operations; it is
  intentionally slow and reports per-package failures.
- Update detection for manually linked packages is stored but not yet executed.


## [0.2.9] - 2026-11-08

### Changed

- Design C’s trailer actions are collapsed into a “⋯” Flyout. The card now
  shows only the enable checkbox and the menu trigger; Nexus/link/adopt/uninstall
  and whole-directory delete live inside the menu.
- Design A remains the compact mode, where actions stay visible.


## [0.2.8] - 2026-11-08

### Changed

- Discover selection is sticky: category changes, searches and page changes no
  longer clear the selected mod or its detail pane. Only selecting another card
  replaces it. The ListBox is OneWay-bound so clearing the collection cannot
  null the view-model selection.
- Installed page defaults to design C (information panel with monogram, metadata
  pills and trailer actions). The “紧凑列表” toggle switches to design A (compact
  row with trailing enable checkbox).
- Re-selecting the already-loaded card no longer reloads its details.


## [0.2.7] - 2026-11-08

### Fixed

- Replaced the stock `ListBoxItem` template with a content-only template. The
  default template was still drawing the square selection/focus surface under
  the rounded card; styling the background was not enough.
- Download now opens a version-picker Flyout from the “下载并安装” button. The
  duplicate standalone “选择版本” flyout was removed.
- Overrode Avalonia Fluent’s `SystemAccentColor` / system accent brushes with the
  active MK palette. Windows’ brown DWM colorization no longer leaks into
  checkboxes, sliders and selection.

### Docs

- Added `docs/VERSION_MODEL.md` for the approved Instance-per-version + Mod Group
  architecture (Scheme A).
- Added installed-item design samples at `docs/mockups/installed-items.png`.


## [0.2.6] - 2026-11-08

### Fixed

- The v1 files endpoint returns `{ files: [...] }`, not a bare array. The
  legacy-file-id resolver now accepts both shapes, which restores the missing
  `file_id=905` in browser NXM links. Regression test updated.
- Category changes now clear stale detail state when the selected card is no
  longer in the filtered list, instead of leaving an image/description without
  title/tags.
- Disabled packages no longer show “缺失 N · 新增 N”: `.dll.disabled` is
  compared as the same logical file for manifest diagnostics. Added regression
  test.
- Discover hover now changes the card background itself; ListBox item
  selection/pointerover backgrounds and focus adorners are transparent.

### Added

- Manual Nexus association dialog with search, candidate list and confirmation;
  associations persist in `nexus-manual-matches.json`.
- File/version selection moved into a Flyout (`选择版本`), removing the nested
  scroll area that stole page scrolling.
- Manager download flags are shown in the file list.


## [0.2.5] - 2026-11-08

### Added

- Current-page category filter on Discover, populated from the `category.name`
  values Nexus returns for each result. This is client-side because the v3
  search request has no category filter; the tooltip states it filters the
  current page.


## [0.2.4] - 2026-11-08

### Fixed

- Restored the missing `OpenApiKeyPage` command; the “获取 API Key” button is
  enabled again and opens the Nexus API key page.
- Browser download URLs now resolve the legacy v1 `file_id` instead of using the
  v3 file id. A v3 id such as 7439696 no longer replaces the website id (905).
- Home’s Nexus card hides “在设置中配置 API Key” once a key is configured.
- Scan progress is now an overlay outside the scroll content, so the list no
  longer jumps when it appears.
- Install failures are reported in the global status area instead of echoing the
  same text under a single mod’s detail pane.
- Toggling enable/disable no longer reorders the installed list by state.
- Discover cards use a transparent, stretched ListBox item with no focus
  adorner, removing the square hover/selection edge and bottom gap.
- Enable/disable log messages include the mod name.
- “删除确认” disable confirmation no longer fires while settings are loading.

### Added

- Expand/collapse for long mod descriptions.
- Click-to-enlarge detail image (click again to restore).
- `&nmm=1` on browser NXM links.
- File manager-download flags are surfaced (“支持管理器” / “仅手动下载”).
- Settings page widens and centers on large screens.
- Installed action row places the enable checkbox at the trailing edge so
  managed and unmanaged rows align.


## [0.2.3] - 2026-11-08

### Added

- Vortex-style free-download handoff: a 403 from the direct download-link API
  now parks the selected mod/file as a pending download, opens the Nexus file
  page, and resumes when the NXM link arrives through the registered protocol.
- NXM `user_id` is parsed and compared with the validated Nexus account; links
  issued for another account are rejected.
- NXM registration now supports confirmed overwrite. A foreign handler's
  command/icon is backed up and restored on unregister; an old MK handler is
  simply replaced.
- Turning off “删除确认” now asks for confirmation and reverts to enabled if the
  user cancels.

### Fixed

- Installed-package ordering no longer moves disabled mods to the bottom after a
  toggle; order is stable by package name.
- Discover result cards now stretch inside the ListBox item, removing the empty
  selection-container gap shown in the screenshot.


## [0.2.2] - 2026-11-08

### Added

- API Key row now has a “获取 API Key” shortcut and a help tooltip explaining MK's use of the key.
- NXM section has a help tooltip and explicit “注册 NXM 链接” guidance.
- Installed page: bulk enable/disable, search, and “安装已下载文件” moved here.
- NXM browser-download button is disabled until the protocol is registered and
  links to Settings when it is not.
- Installed page now uses a checkbox labelled “启用” instead of a toggle button;
  disabled mod names are rendered in the secondary color.

### Changed

- Removed the first-stage NXM paste/test box from the Discover page; NXM now
  enters only through the registered protocol, `--nxm`, or forwarded single-instance.
- Renamed the browser download action to “通过浏览器 NXM 下载”.
- Removed the green/yellow enabled/disabled badges.

### Docs

- Added `docs/AUTO_UPDATE.md` with automatic-update feasibility and sequencing.


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
