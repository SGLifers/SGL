# SGL — SplitGate Lifers

A Windows community app for lobby hosts. Create and edit custom gamemodes, install them directly, compare local Git revisions, and restore earlier configurations.

[Download SGL](https://github.com/SGLifers/SGL/releases/latest) · [Illustrated setup guide](https://sglifers.github.io/SGL/)

## Install

1. Download **SGL.zip** from this repository's release page and extract it to a writable folder.
2. Open **SGL.exe**. No separate .NET, Git, PowerShell, or browser runtime installation is needed.
3. If Windows blocks or removes the unsigned app, follow the [Windows Security guide](docs/index.html#windows-security). Verify the source and affected file before deciding whether to allow it.
4. Let SGL locate `%LOCALAPPDATA%\PortalWars2\Saved\Cloud\CloudSave`, or browse to another folder.
5. Save a custom mode in-game once if this PC has no custom saves. Close Splitgate before using **Save to game**.

The release targets Windows x64. Windows 10/11 compatibility needs testing on each host's supported OS configuration; the initial automated and desktop checks were performed on the development machine, not a clean Windows VM.

## First release

- Native WPF editor with 41 schema-driven settings and expanded numeric inputs.
- Create KOTH, TDM and SWAT modes; six community presets include Sticky Fists and kill-refill 1v1 modes.
- Open existing modes, preserve unexposed options, edit starting loadouts, duplicate and rename.
- Randomize loadouts with per-slot locks. Editor markers distinguish opened values from documented in-game ranges.
- Import existing SpinGate recipes, compiled modes and packs. Batch installs skip existing names.
- Export modes or the installed library as JSON. Drafts are saved separately from game saves.
- Local Git snapshots at startup/refresh and after saves. No account, external Git executable, or remote upload.
- Compare arbitrary retained revisions. Load an older version of an installed mode into the editor and save it as a new revision.
- Automatic backups, complete-manifest restoration, stale-write checks and game-running guards.
- Signed update metadata and verified downloads from this repository; the Windows executable itself is unsigned.

Unknown base variants are captured in history and exportable, but read-only. Bots and Spawning controls are not exposed yet. Existing custom rules may be ignored or clamped by the game. The installed-mode history uses stable FileIds: deleting/recreating a mode outside SGL produces a distinct identity. Startup scans cannot recover edits the game never saved or intermediate changes between scans. The history selector currently displays the latest 1,000 commits; older commits remain in the local repository.

## Your data

SGL preferences, drafts, logs and per-CloudSave-profile Git repositories live under `%LOCALAPPDATA%\SGL`. Application updates leave this directory and game saves intact. History contains full configuration payloads but excludes installation ownership and archive timestamp noise. It is local and has no remote configured. Back up this directory separately for protection against disk loss.

Game-save backups live beneath `<CloudSave>\Backup\SGL-*`. Revision restoration changes one selected mode. Manifest restoration changes the full registration list and can hide later registrations; SGL explains this before writing and creates a safety backup. Historical archives are copied to fresh save paths, preserving current archives and recovering files the game changed in place. Registration FileIds stay stable; generated save IDs and paths change. Legacy `SG-*` / `SpinGate-*` backups remain untouched; index-only backups cannot be automatically restored without complete archive snapshots.

## Build and test

Install the .NET 10 SDK on Windows, then run:

```powershell
dotnet build src/SGL.Desktop/SGL.Desktop.csproj
dotnet run --project tests/SGL.Tests
./scripts/package.ps1 -Version 0.1.0
```

The test executable uses temporary game-save folders and a mock game-running probe. Never point a test host at real player saves. Development UI arguments `--cloud-save <folder> --data <folder> --no-update` isolate desktop checks. Production builds retain the real process guard.

## Releases and updates

See [RELEASING.md](RELEASING.md). The updater checks the latest public stable release at launch. It verifies an ECDSA-signed manifest and SHA-256, stages the new EXE, waits for application exit, retains the previous EXE, and requests healthy-startup confirmation. A failed startup attempts rollback. Network failure keeps the current app available. Unsaved or invalid edits defer updates. An automatic-update preference and manual check are available in Settings.

Windows may quarantine a new EXE or update helper. SGL does not change Windows Security settings; manual recovery may be needed. Normal use does not require administrator rights, but reviewing detections in Windows Security can require them.

## Architecture

`SGL.Desktop` owns the WPF interface and restart coordinator. `SGL.Core` owns payload mappings, consistent game-save reads, write transactions, history and update verification. `tools/SGL.Release` signs release metadata. Existing SpinGate schemas, templates and compiler were migrated with preservation tests. Map loading and other community modules can be added without coupling them to the editor.

This is an unofficial community project, not affiliated with 1047 Games. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
