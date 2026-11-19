# Roadmap

> 最后更新：2026-11-18（0.7.0-beta）

## 0.1 — Foundation

- [x] Steam appmanifest detection for full game and demo
- [x] BepInEx scanner with multi-DLL package grouping
- [x] Enable/disable/delete a package as a unit
- [x] Safe archive extraction and staged install
- [x] Nexus v3 search/details/files/versions
- [x] Download and install from Nexus (with free-account fallback)
- [x] DynamicResource theme presets and JSON theme import
- [x] Settings, logging and path pickers
- [x] Adopt unmanaged package with a baseline manifest
- [x] NXM protocol registration and single-instance forwarding
- [x] Detailed `--console` logging

## 0.2 — Trust and rollback

- [x] Install manifest per package (source, version, files, hashes)
- [x] Managed/unmanaged boundary and preserving uninstall
- [x] Read-only update check against Nexus
- [x] Single-package update with same-volume staging
- [x] Optional cross-volume backup and restore-on-failure
- [x] One-click update for Nexus-linked packages
- [ ] Operation history / restore the last N operations
- [ ] Upgrade/downgrade a package without losing config
- [ ] Package comparison and duplicate detection
- [ ] Better text search without violating API terms
- [ ] Settings migration/versioning

## 0.3 — Dependency and conflict visibility

- [ ] Parse and display `BepInDependency` (hard/soft, min version)
- [ ] Detect missing dependencies and version mismatches
- [ ] Show same-GUID duplicates across packages
- [ ] Conflict hints based on declared metadata
- [ ] Install plan preview before applying

## 0.4 — Diagnostics

- [ ] BepInEx/Unity log viewer (implemented; pending user acceptance)
- [ ] Map stack frames/assembly names back to packages
- [ ] Export a diagnostic bundle
- [ ] Crash attribution with confidence

## 0.5 — Launcher lifecycle

- [x] GitHub release update check
- [x] Windows self-update with SHA-256 digest verification
- [ ] Authenticode / signed release artifacts
- [ ] Stable / preview update channels
- [ ] Differential updates
- [ ] Automatic rollback when replacement fails
- [ ] Portable mode
- [ ] UI automation tests (Avalonia.Headless)

## Later

- [ ] Game skin packages with Body/Head/Wings preservation
- [ ] Optional community index (explicit content/licensing rules)
- [ ] Full Steam version history
- [ ] Onboarding interactive tour
