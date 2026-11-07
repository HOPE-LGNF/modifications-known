# Roadmap

## 0.1 — Foundation (current)

- [x] Steam appmanifest detection for full game and demo
- [x] BepInEx scanner with multi-DLL package grouping
- [x] Enable/disable/delete a package as a unit
- [x] Safe archive extraction and staged install
- [x] Nexus v3 search/details/files/versions
- [x] Download and install from Nexus (with free-account fallback)
- [x] Launcher theme presets backed by DynamicResource variables
- [x] Settings, logging and path pickers
- [x] Unit tests for path safety, archive install, scanner and game detection
- [x] Game skin entry (placeholder, explicitly marked 开发中)

## 0.2 — Trust and rollback

- [ ] Install manifest per package (source, version, files, hashes)
- [ ] Backup before overwrite; restore the last N operations
- [ ] Upgrade/downgrade a package without losing config
- [ ] Package comparison and duplicate detection
- [ ] Better text search: evaluate Nexus GraphQL or a local index without
      violating API terms
- [ ] Settings migration/versioning

## 0.3 — Dependency and conflict visibility

- [ ] Parse and display `BepInDependency` (hard/soft, min version)
- [ ] Detect missing dependencies and version mismatches
- [ ] Show same-GUID duplicates across packages
- [ ] Conflict hints based on declared metadata (not guesses)
- [ ] Install plan preview before applying

## 0.4 — Diagnostics

- [ ] BepInEx/Unity log viewer
- [ ] Map stack frames/assembly names back to installed packages
- [ ] Export a diagnostic bundle with paths and hashes
- [ ] Crash attribution as “likely package” with confidence, not certainty

## Later

- [ ] Game skin packages with Body/Head/Wings preservation and SkinSync integration
- [ ] Launcher self-update and signed release artifacts
- [ ] Portable mode
- [ ] Optional community index (only with explicit content/licensing rules)
- [ ] UI automation tests (Avalonia.Headless)
