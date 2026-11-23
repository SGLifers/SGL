# SGL — SplitGate Lifers

A Windows community app for lobby hosts. Configure gamemodes, install community maps, associate maps with modes, share versioned packages, compare local Git revisions, and restore earlier configurations.

[Download SGL](https://github.com/SGLifers/SGL/releases/latest) · [Illustrated setup guide](https://sglifers.github.io/SGL/)

## Install

1. Download **SGL.zip** from this repository's release page and extract it to a writable folder.
2. Open **SGL.exe**. No separate .NET, Git, PowerShell, or browser runtime installation is needed.
3. If Windows blocks or removes the unsigned app, follow the [Windows Security guide](docs/index.html#windows-security). Verify the source and affected file before deciding whether to allow it.
4. Let SGL locate `%LOCALAPPDATA%\PortalWars2\Saved\Cloud\CloudSave`, or browse to another folder.
5. Save a custom mode in-game once if this PC has no custom saves. Close Splitgate before using **Save to game**.

The release targets Windows x64. Windows 10/11 compatibility needs testing on each host's supported OS configuration; the initial automated and desktop checks were performed on the development machine, not a clean Windows VM.

## Features

- Native WPF editor with all 115 documented advanced settings and five SGL extras (120 controls). Includes Bots, Spawning, enum selectors and expanded numeric inputs. See the [settings audit](docs/settings-audit.md).
- Create KOTH, TDM and SWAT modes; six community presets include Sticky Fists and kill-refill 1v1 modes.
- Open existing modes, preserve unexposed options, edit starting loadouts, duplicate and rename.
- Randomize loadouts with per-slot locks. Editor markers distinguish opened values from documented in-game ranges.
- Import existing SpinGate recipes, compiled modes and packs. Batch installs skip existing names.
- Export modes or the installed library as JSON. Drafts are saved separately from game saves.
- Local Git snapshots at startup/refresh and after saves. No account, external Git executable, or remote upload.
- Compare arbitrary retained revisions. Load an older version of an installed mode into the editor and save it as a new revision.
- Community map browsing with name/author search, category filters, previews, one-click download and installation, and local map .bin imports.
- Per-profile map-to-mode associations: keep maps independent, link several modes to a map, and open linked modes from the Maps tab. Select the map and mode separately in the game lobby.
- Delete installed maps and gamemodes with confirmation and automatic backups. Older saves and Map Lab edits move to the backup; associated content stays installed. Deleted modes remain available in Git history.
- Drag dividers to resize the installed-mode sidebar, settings categories and map details. Widths persist across restarts; Settings includes Reset pane widths.
- Automatic backups, complete-manifest restoration, stale-write checks and game-running guards.
- Signed update metadata and verified downloads from this repository; the Windows executable itself is unsigned.
- Portable `.sglpack` files for maps, gamemodes and paired bundles. Recipients install through SGL with a complete backup.
- Shared library with Discord sign-in, public/unlisted/private publications, attributed immutable revisions, forks and pinned map-to-mode bundles. The sharing endpoint is included in the app; publishing requires Discord sign-in. Offline packages work without an account. See [sharing](docs/sharing.md) and [deployment](sharing/README.md).

Unknown base variants are captured in history and exportable, but read-only. KOTH has no kill-scoring component in its saved template: its five kill/death/assist scoring controls are shown as unavailable. Existing custom rules may be ignored or clamped by the game. The installed-mode history uses stable FileIds: deleting/recreating a mode outside SGL produces a distinct identity. Startup scans cannot recover edits the game never saved or intermediate changes between scans. The history selector currently displays the latest 1,000 commits; older commits remain in the local repository.

## Maps

Open **Maps** to load the Splitgate-Architects community catalog. Pick a map and select **Install selected map** with Splitgate closed. The catalog is cached for offline browsing; downloads need a connection. Installation accepts map archives containing `World.cf1047`; prefab and gamemode archives are rejected. Existing map identities and unregistered files are never overwritten, protecting Map Lab edits. Maps are not executable code and no MapLoader script is run.

Catalog and map data come from the community repository over HTTPS. This catalog does not publish independent checksums or signatures: archive validation and recorded SHA-256 hashes detect local changes, but do not authenticate authorship. Current limits are 8 MB for the catalog, 128 MB compressed and 1 GB expanded for each map. Prefabs, automatic catalog-map updates and map binary Git history are outside this version. Shared publications have separate immutable revisions. Mode Git history remains independent; full backups include all registered map and mode archives.

Map associations are stored per CloudSave profile in `map-mode-links.json`; stable FileIds preserve links across SGL renames. They do not alter map data or gamemode payloads.

References: [MapLoader](https://github.com/Splitgate-Architects/Splitgate-MapLoader), [community map catalog](https://github.com/Splitgate-Architects/Splitgate-CommunityMaps), [community map website](https://splitgate-architects.github.io/Splitgate-CommunityMaps/).

## Delete and restore

Select an installed item and use **Delete selected mode** or **Delete selected map**. Close Splitgate first. SGL confirms the exact item, backs up the complete save index and registered archives, then removes the item from the index and moves its archives into `Backup/SGL-*/RemovedFiles`. Native `.bin` saves in its own identity folder, including unregistered older saves, are included. Unrelated files are retained. This prevents normal MapLoader folder scans from re-registering deleted saves.

Use **Backups → Restore selected backup** for full restoration, or recover a deleted gamemode through **History**. A full backup restore also restores other registrations to that earlier point. Associations are retained as local references and hidden while their map or mode is absent. If an archive is locked or changes during cleanup, SGL reports the retained path and writes a `cleanup-pending` receipt; do not run MapLoader until that file is handled. The committed index remains readable, and a full backup is available.

## Your data

SGL preferences, drafts, logs and per-CloudSave-profile Git repositories live under `%LOCALAPPDATA%\SGL`. Application updates leave this directory and game saves intact. History contains full configuration payloads but excludes installation ownership and archive timestamp noise. It is local and has no remote configured. Back up this directory separately for protection against disk loss.

Game-save backups live beneath `<CloudSave>\Backup\SGL-*`. Revision restoration changes one selected mode. Manifest restoration changes the full registration list and can hide later registrations; SGL explains this before writing and creates a safety backup. Historical archives are copied to fresh save paths, preserving current archives and recovering files the game changed in place. Registration FileIds stay stable; generated save IDs and paths change. Legacy `SG-*` / `SpinGate-*` backups remain untouched; index-only backups cannot be automatically restored without complete archive snapshots.

## Build and test

Install the .NET 10 SDK on Windows, then run:

```powershell
dotnet build src/SGL.Desktop/SGL.Desktop.csproj
dotnet run --project tests/SGL.Tests
./scripts/package.ps1 -Version 0.3.3
```

The test executable uses temporary game-save folders and a mock game-running probe. Never point a test host at real player saves. Development UI arguments `--cloud-save <folder> --data <folder> --no-update` isolate desktop checks. Production builds retain the real process guard.

## Releases and updates

See [RELEASING.md](RELEASING.md). The updater checks the latest public stable release at launch. It verifies an ECDSA-signed manifest and SHA-256, stages the new EXE, waits for application exit, retains the previous EXE, and requests healthy-startup confirmation. A failed startup attempts rollback. Network failure keeps the current app available. Unsaved or invalid edits defer updates. An automatic-update preference and manual check are available in Settings.

Windows may quarantine a new EXE or update helper. SGL does not change Windows Security settings; manual recovery may be needed. Normal use does not require administrator rights, but reviewing detections in Windows Security can require them.

## Architecture

`SGL.Desktop` owns the WPF interface and restart coordinator. `SGL.Core` owns payload mappings, consistent game-save reads, write transactions, history and update verification. `tools/SGL.Release` signs release metadata. Existing SpinGate schemas, templates and compiler were migrated with preservation tests. Map catalog/download handling is independent from mode configuration. Map installation follows MapLoader’s flat `MapCreator/<id>.bin` registration with a `custom-map` SaveId and recipient ownership; it preserves all unrelated manifest content.

This is an unofficial community project, not affiliated with 1047 Games. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
