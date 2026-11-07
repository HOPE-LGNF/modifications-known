# Architecture

## Goals

The launcher is intentionally split so that game-specific behavior, network
contracts and UI do not leak into each other.

```text
ModificationsKnown.Core
  ├─ Games        game profiles, installation model, launcher interfaces
  ├─ Mods         installed package model, dependency model, manager interfaces
  ├─ Nexus        provider-neutral catalog models and API contract exceptions
  ├─ Settings     user settings model and store interface
  ├─ Network      download abstraction
  └─ Common       OperationResult, PathSafety

ModificationsKnown.Infrastructure
  ├─ Games        Steam VDF/appmanifest locator, process launcher
  ├─ BepInEx      PE metadata reader, plugin scanner, enable/disable/delete
  ├─ Nexus        Nexus v3 REST client and DTO boundary
  ├─ Installation safe archive extraction and transactional package install
  ├─ Network      streaming HTTP downloader
  └─ Storage      AppData paths, atomic JSON settings, file logger

ModificationsKnown.App
  ├─ ViewModels   shell, pages, commands, view state
  ├─ Views        Avalonia XAML only; no file-system logic
  ├─ Services     game context, dialogs, file picker, thumbnail cache
  └─ Theming      palette records and DynamicResource application
```

## Key decisions

### 1. Game edition is data, not a hardcoded AppID

`GameProfile` contains the real Steam AppIDs: `4576490` for the full game and
`4576510` for the demo. `SteamGameLocator` reads `appmanifest_*.acf` and
`libraryfolders.vdf`, so both editions can coexist. UI defaults to the full game
when both are present, but the user can switch.

### 2. Package manifests and ownership boundaries

An installed package is a directory under `BepInEx/plugins`. Every launcher-owned
package contains `.modifications-known/manifest.json`, listing the files, sizes,
SHA-256 hashes and optional Nexus source. The manifest is written in staging and
moved together with the files, so an installed package and its ownership record
commit as one unit.

A directory without a valid manifest is **unmanaged**. It is visible and can be
enabled/disabled, but normal uninstall is disabled; only the explicit “delete
whole directory” action removes it. This preserves manually added files and
avoids treating a folder name as proof of ownership.

### 3. A package is the unit of management

`InstalledMod` represents a folder in `BepInEx/plugins`. All DLLs in that folder
are scanned for `BepInPlugin`/`BepInDependency` and are enabled/disabled together.
This is safer than renaming only `dllFiles[0]` for multi-DLL mods.

### 4. Never load plugin assemblies

`BepInExAssemblyReader` uses `PEReader`/`MetadataReader` to read attributes. A
broken or malicious plugin is not executed during a scan.

### 5. Archive installation is staged

`ArchiveModInstaller` extracts to a temp directory, validates every archive entry
against path traversal, then copies package folders into a staging directory next
to the plugins directory. Only a validated staging folder is moved into place.
Existing packages are not overwritten.

### 6. Nexus v3 is an adapter

Nexus marks v3 endpoints as experimental. DTOs live only in
`NexusModsClient`; the rest of the app uses `NexusModSummary`, `NexusModDetails`,
`NexusFileVersion`, etc. If the API changes, only the adapter and its tests need
updating.

### 7. Theme values are resources

Views use `DynamicResource` keys such as `AppBackgroundBrush`, `SurfaceBrush`,
`AccentBrush` and `HeroStartBrush`. `ThemeService` applies a `ThemePalette` by
replacing brushes at runtime. A future launcher wallpaper can be added as another
resource without touching page XAML.

## What is deliberately not here yet

- dependency resolution and conflict graph
- backup/rollback and version pinning
- crash log attribution
- game skin package management
- launcher self-update
- a server-side mod index

These are tracked in [ROADMAP.md](ROADMAP.md). The interfaces for some of them
will be added only after the file/package semantics are proven by tests.

## Custom launcher themes

Built-in palettes live in `src/ModificationsKnown.App/Theming/ThemePalettes.cs`.
Additional themes are JSON files under `%APPDATA%\ModificationsKnown\themes\`.
The **导入主题 JSON** button validates a file, copies it into that directory, and
applies it immediately. A template is provided at
[`docs/themes/aurora.json`](themes/aurora.json).

The JSON shape is a flat palette:

```json
{
  "id": "aurora",
  "name": "极光",
  "description": "…",
  "background": "#0B1018",
  "sidebar": "#101827",
  "surface": "#141D2D",
  "surfaceAlt": "#1B2940",
  "border": "#2C3F5E",
  "textPrimary": "#EAF1FF",
  "textSecondary": "#93A7C4",
  "accent": "#7CE0C3",
  "accentHover": "#9BEBD5",
  "accentSoft": "#163B36",
  "accentContrast": "#081210",
  "success": "#7CE0A3",
  "warning": "#E8C46B",
  "danger": "#E88888",
  "heroStart": "#1B2B4A",
  "heroEnd": "#2B4B58"
}
```

All color fields are required. Values are validated before the theme is saved.
The `id` must be unique against built-in themes and cannot contain path
separators. Theme files only change launcher brushes; they never touch game files.
