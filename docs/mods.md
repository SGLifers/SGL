# Mods

The Mods workspace is available in SGL 0.5.0 and later.

1. Open **Mods** in the sidebar. SGL checks the latest stable [Splitgate-Mods release](https://github.com/Splitgate-Architects/Splitgate-Mods/releases/latest) at startup. **Refresh mods** checks again; the last verified release remains available offline.
2. Choose the Splitgate installation. SGL detects a single Steam installation automatically; use **Find in Steam** or **Browse** for another copy. Select the game root or `PortalWars2/Binaries/Win64`, separately from CloudSave. The upstream mods target the P2P build.
3. Close Splitgate, select **Bot Chatter** or **Map Editor Budget**, and click **Install & enable**. On the first installation SGL downloads UE4SS 4.0.0 and Splitgate-Mods 1.1.0's compatibility files. It verifies their pinned SHA-256 hashes, runs the pinned patcher in a temporary folder, verifies the patched DLL, then installs the prepared files. No Python installation is required. The helper is hidden and does not change system execution policy. Runtime debug consoles start disabled.
4. Launch Splitgate. BotChatter is configured for host-only operation; its chat and bot names replicate to unmodified clients according to its authors. MapEditorBudgetMod changes the local Map Lab budget. Host/client requirements for future mods are not inferred from folder names.

**Disable** updates `mods.txt` and removes that mod's `enabled.txt` marker. Both can enable a mod in UE4SS, so a single disabled flag is insufficient. Changes take effect at the next game launch. A configured mod may also have its own enabled option.

## Configuration and updates

Select a configuration file, edit it, then **Save configuration** with Splitgate closed. BotChatter provides JSON settings, phrase pools and bot names. The budget mod returns a Lua table with a positive integer `BudgetLimit`; SGL validates this limited format without executing Lua. The game/mod may impose additional practical limits. Every configuration save creates a backup. Invalid JSON and stale edits are rejected.

An update button appears when a newer release is available. Updates preserve JSON/config.lua files and enabled state. Locally edited script code blocks an overwrite. A changed game executable or managed runtime blocks enabling/updating until compatibility has been reviewed; disabling remains available. Updates are explicit and no new mod is automatically activated.

## Discovery contract

SGL reads the latest stable release metadata, requires a SHA-256 digest for its ZIP, verifies the download and discovers folders under `ue4ss/Mods/<id>/Scripts/main.lua` (case insensitive). New Lua mod folders following that structure appear without rebuilding SGL. Support directories without an entry point are excluded. A new mod's scope is shown as undeclared; check its documentation before hosting.

Runtime compatibility is pinned independently of catalog discovery. Native DLL mods, custom installers, undeclared dependencies or a new UE4SS requirement need an updated integration. This version does not execute downloaded installers from newly discovered releases. Only the pinned bootstrap patcher is run, against staging files. The release digest protects download integrity; it is not an independent signature or a code review. Mod code comes from the selected community repository.

## Backups and existing installations

Mod receipts and backups are separate from CloudSave backups, under `%LOCALAPPDATA%/SGL/Mods/<installation-key>/Backups`. Use **Open mod backups** to locate them. Each backup records the before/after hashes and original bytes for the files changed by that operation, including the installation receipt.

Choose an operation in **Mod backups** and click **Restore selected mod backup**. Restore newer operations first. SGL checks that files still match that operation's result, creates a safety backup, then restores the previous files. Restore refuses to overwrite later or external edits. An initial-install restore removes only the managed files that installation added. A failed transaction is rolled back; if an external change prevents rollback, its journal records `recovery-needed` and SGL reports the backup location.

SGL refuses to overwrite an unmanaged `ue4ss` directory or `dwmapi.dll`. Back up and move that installation out before migrating to SGL. Existing unrelated files, including wall-running `version.dll`, are preserved; coexistence of mods still needs gameplay testing. Keep SGL.exe in its own extracted folder, outside the game's DLL directory. Do not remove SGL's Mods data before disabling/removing its game-side installation.

## Verification

Core checks use temporary game-folder fixtures and fake network responses. Desktop checks exercise discovery, offline fallback, configuration editing, updates, toggles and narrow layout. The runtime smoke check downloads the pinned archives, patches in staging, installs both real mod packages into a non-executable fixture and verifies configuration/toggling. It never loads a game DLL. Real P2P gameplay compatibility still needs host testing.

Sources: [Splitgate-Mods](https://github.com/Splitgate-Architects/Splitgate-Mods), [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS), [Architects UE4SS fork](https://github.com/Splitgate-Architects/RE-UE4SS).

Game installation controls are under **Settings → Splitgate game folder**. **Find in Steam** reads Steam’s library records and selects the game’s Win64 folder when exactly one installation is found; otherwise use Browse. It does not launch, install or download Splitgate. Mods, Maps and Gamemodes display a reminder to close Splitgate before applying changes.
