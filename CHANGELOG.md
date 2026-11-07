# Changelog

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
