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

### 2. A package is the unit of management

`InstalledMod` represents a folder in `BepInEx/plugins`. All DLLs in that folder
are scanned for `BepInPlugin`/`BepInDependency` and are enabled/disabled together.
This is safer than renaming only `dllFiles[0]` for multi-DLL mods.

### 3. Never load plugin assemblies

`BepInExAssemblyReader` uses `PEReader`/`MetadataReader` to read attributes. A
broken or malicious plugin is not executed during a scan.

### 4. Archive installation is staged

`ArchiveModInstaller` extracts to a temp directory, validates every archive entry
against path traversal, then copies package folders into a staging directory next
to the plugins directory. Only a validated staging folder is moved into place.
Existing packages are not overwritten.

### 5. Nexus v3 is an adapter

Nexus marks v3 endpoints as experimental. DTOs live only in
`NexusModsClient`; the rest of the app uses `NexusModSummary`, `NexusModDetails`,
`NexusFileVersion`, etc. If the API changes, only the adapter and its tests need
updating.

### 6. Theme values are resources

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
