# Hosting

The Hosting tab is available in the local 0.6.0 build. The latest published release remains 0.5.0 until a separate release is requested.

## SpinGate

The spinner is a native port of [SpinGate](https://github.com/djcowchaser/SpinGate)'s spinner. It works offline and requires no Discord account. **Original** and **Quick Play** retain the existing built-in gamemode pools, map lists and compatible assignments, including simulation maps for Takedown.

Select a preset and choose **Spin**. Only gamemodes with assigned maps participate. A mode avoids its most recent half-pool of draws; maps share a global cooldown across overlapping lists. If unequal overlapping pools temporarily exhaust their cooldown, the oldest eligible map is used. One-item pools remain usable. **Copy gamemode** retains SpinGate's in-game menu-name overrides for Team Deathmatch and Takedown. Spin chooses a result; it does not change or install content in the running game.

Expand **Configure rotation** to copy/delete presets, restore a built-in preset, edit gamemodes and map lists, and link each mode to one or more lists. Unlinked modes do not participate. You can add installed gamemode names or use installed map names as a list; assign compatible links explicitly. Saved changes reset rotation history. Settings persist in `%LOCALAPPDATA%/SGL/Hosting/rotations.json`, with the previous file kept as `rotations.json.previous`. Browser-saved SpinGate presets are not imported automatically.

## Lobby announcements

The lobby-code field is implemented, but the included endpoint is deliberately empty. It cannot announce anything until the separate [SGL Discord Bot](https://github.com/SGLifers/SGL-Discord-Bot) is deployed and SGL is connected to it. No bot token or AWS credential is stored in the desktop application.

After connection, sign in with Discord, optionally enter a lobby name, then enter or paste your lobby code. The app waits for two seconds without edits, normalizes the code to uppercase, and submits it automatically. The compact code field accepts exactly six letters or digits. Lobby names are optional, limited to 80 characters on one line, and included in new announcements. The bot verifies the existing SGL session, server membership and configured host role/allowlist, then posts the host identity and code into one operator-selected server channel. Name edits made before sending reset the typing pause. Editing a name after the code was announced applies to the next lobby and does not create another post. The app cannot choose a different host or channel.

Codes are not saved for startup replay. Signing in again, switching accounts or signing out clears the field and cancels pending work. Clearing the field cancels unsent client work; a message already received by the bot may still complete and is not deleted. Repeated codes are deduplicated locally and by the service. Safe retries reuse the same request identity, at most three automatic attempts. Rate limits show their wait; uncertain delivery stops and asks you to review the channel. Selecting a spinner result never sends a Discord message.

## Deployment handoff

Deploy/configure the separate bot using its README. It runs on demand through AWS Lambda and Discord REST, without an always-on Gateway connection. It defaults to announcements disabled until the operator configures the bot secret, destination and permitted hosts. Configure the bot token through its local secret-setup script; do not paste it into SGL or chat.

After deployment, use its public `ApiUrl` output:

```powershell
./scripts/configure-hosting.ps1 -ApiUrl https://YOUR_API.execute-api.us-west-2.amazonaws.com
```

The script validates HTTPS and the health response, then writes `src/SGL.Core/Data/hosting-service.json`. Rebuild/publish SGL to connect all hosts. This task prepares the integration but does not deploy AWS resources, configure a real bot token or send Discord messages.

## Verification

Core tests verify pools, compatibility, cooldowns, persistence, lobby validation and the HTTP contract. Desktop checks verify debounce, no typing-time messages, duplicates, account/session switches, stable retry identity, canceled/stale responses, disabled deployment state, configuration edits and narrow layout. All use isolated files and mocked HTTP. Existing map/mod/gamemode checks remain required. A real-channel pilot is an operator step after deployment.
