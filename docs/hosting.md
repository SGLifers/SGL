# Hosting

Hosting is the first tab in SGL 0.6.0. It combines the SpinGate spinner with explicit Open Lobby and Close Lobby controls for Discord announcements.

## SpinGate

The spinner is a native port of [SpinGate](https://github.com/djcowchaser/SpinGate)'s spinner. It works offline and requires no Discord account. **Original** and **Quick Play** retain the existing built-in gamemode pools, map lists and compatible assignments, including simulation maps for Takedown.

Select a preset and choose **Spin**. Only gamemodes with assigned maps participate. A mode avoids its most recent half-pool of draws; maps share a global cooldown across overlapping lists. If unequal overlapping pools temporarily exhaust their cooldown, the oldest eligible map is used. One-item pools remain usable. **Copy gamemode** retains SpinGate's in-game menu-name overrides for Team Deathmatch and Takedown. Spin chooses a result; it does not change or install content in the running game.

Expand **Configure rotation** to copy/delete presets, restore a built-in preset, edit gamemodes and map lists, and link each mode to one or more lists. Unlinked modes do not participate. You can add installed gamemode names or use installed map names as a list; assign compatible links explicitly. Saved changes reset rotation history. Settings persist in `%LOCALAPPDATA%/SGL/Hosting/rotations.json`, with the previous file kept as `rotations.json.previous`. Browser-saved SpinGate presets are not imported automatically.

## Lobby announcements

SGL connects to the deployed [SGL Discord Bot](https://github.com/SGLifers/SGL-Discord-Bot). Hosts sign in with Discord and must have the configured host role. Announcements currently target the configured test server and channel. No bot token or AWS credential is stored in the desktop application.

After connection, sign in with Discord, optionally enter a lobby name, then enter or paste your six-character lobby code. Choose **Open Lobby** to post the announcement to Discord. Typing, changing tabs and spinning never send a message. The code accepts letters or digits and is normalized to uppercase. Names are limited to 80 characters on one line. The bot verifies the SGL session, server membership and configured host role/allowlist before posting to the operator-selected channel.

Choose **Close Lobby** when you finish hosting. The bot edits the original announcement to say the lobby is closed, preserving its name and code. It does not create a second Discord post. These buttons manage announcements; they do not create or close the lobby in Splitgate. Closing remains available during the new-announcement cooldown and does not consume the daily opening limit. Details are locked while a request is in progress and while the app knows the lobby is open. Close it before changing those details or reopening the same code.

Codes are not saved for startup replay. Signing in again, switching accounts or signing out clears the fields and cancels pending client work; this does not close announcements already received by the service. After restarting SGL, enter the existing code and choose **Close Lobby** to close your own recent announcement. The service retains receipts for 24 hours. A deleted announcement is treated as already closed. Other hosts cannot close your announcements.

Requests are sent only by button clicks. If a request fails, review the status and retry the same button after any displayed wait. Retries retain their request identity to avoid duplicates. A close retry stays bound to the original announcement even if the same code is reopened. An uncertain open delivery requires channel review and cannot be blindly retried outside the service's two-minute nonce window.

## Deployment handoff

Deploy/configure the separate bot using its README. It runs on demand through AWS Lambda and Discord REST, without an always-on Gateway connection. It defaults to announcements disabled until the operator configures the bot secret, destination and permitted hosts. Configure the bot token through its local secret-setup script; do not paste it into SGL or chat.

After deployment, use its public `ApiUrl` output:

```powershell
./scripts/configure-hosting.ps1 -ApiUrl https://YOUR_API.execute-api.us-west-2.amazonaws.com
```

The script validates HTTPS and the health response, then writes `src/SGL.Core/Data/hosting-service.json`. Rebuild/publish SGL to connect all hosts. The connected test build uses the test-server endpoint; the default source configuration remains empty until a public rollout is requested.

## Verification

Core tests verify pools, compatibility, cooldowns, persistence, lobby validation and the HTTP contract. Desktop checks verify explicit Open/Close actions, no typing-time messages or automatic retries, locked open-lobby details, reopening with a fresh identity, account/session switches, stable manual retry identity, canceled/stale responses, disabled deployment state, configuration edits and narrow layout. All use isolated files and mocked HTTP. Existing map/mod/gamemode checks remain required. A real-channel pilot is an operator step after deployment.
