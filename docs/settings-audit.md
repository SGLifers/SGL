# Advanced settings audit

SGL exposes **180 settings**: the 115 documented general controls, 60 additional controls captured directly in-game, and five existing extras. All 88 View All presets were saved untouched and inspected through every tab and full scroll range in Standard Rules and Tiebreaker on November 23–24, 2026, game build `3.1-643074`.

The embedded `gamemode-ui-audit.json` records visible fields by variant, phase and category. `gamemode-setting-evidence.json` records the 60 new controls' exact hover descriptions and probed slider endpoints. The desktop renders applicable controls, marks a saved field as **SGL extra** when it is absent from that specific mode/phase menu, and flags values beyond known in-game endpoints. Unrelated objective controls are omitted.

| Additional controls | Count |
| --- | ---: |
| Capture-point objective rules and capture respawns (KOTH, Domination, Hotzone) | 18 |
| Firecracker objective, planting/defusing and respawn rules | 15 |
| Splitball spawning, returning, capture zones and scoring | 15 |
| Gun Game progression scoring and promotion/loadout behavior | 9 |
| Race restarting | 1 |
| Teabag confirming/denying points | 2 |

The **Rules** selector edits Standard Rules and Tiebreaker independently. Save-file context inheritance was verified using distinct round lengths (601/602) and health values (88/77) in isolated in-game probe saves. Edits propagate to that phase's inherited state contexts while retaining the other phase, global options and per-team overrides. Global-only settings are labeled **Shared across rule phases**.

Observed quirks are preserved:

- SWAT precision is stored as 10000 although visiting Players displays/clamps it to 2.5. SGL rendering never clamps source values.
- Splitball Additional Objective Time can be stored as 0 (disabled), below the displayed slider minimum of 10. Initial Objectives also documents a zero disable value below its slider minimum of 1.
- Both Teabag scoring sliders clamp to 0 at both ends. The untouched confirm value is 1, deny is 0. These observed UI bounds are not treated as engine limits.
- Bomb Plant Time has an empty hover description, checked in regular and Chaos Firecracker. SGL does not invent an in-game description.
- The game's Firecracker tiebreaker page can display inherited standard values despite distinct raw tiebreaker values. SGL shows the actual selected phase's saved values (e.g. explosion time 50 versus 15).

## General settings reference

Reference: [SplitgateDocs advanced settings](https://github.com/SGLifers/SplitgateDocs/blob/main/docs/settings/advanced-settings.md), commit `2a7eba0f8a8550f1d456fe7d15963a29a3c57509`, game build `3.1-643074`. The embedded `settings-reference.json` records the reference independently from the mappings and is checked by the integration suite.

| Category | Documented controls | Included settings |
|---|---:|---|
| Base | 6 | Round/overtime/sudden-death timers, sudden-death toggles, score mode |
| Mode | 28 | Spectating, kill scoring, airdrops, team wipes, round score limit and score display |
| Map | 11 | Weapon pickups, cleanup, boundaries, item pads and selection behavior |
| Teams | 6 | Friendly fire, enemy healing, collision and enemy HUD information |
| Players | 18 | Health, ammo, damage, movement, thruster recharge and player lives |
| Loadouts | 1 | Loadout changes during gameplay, alongside the custom starting-loadout editor |
| Abilities | 16 | Radar, portals, equipment, thruster and sprint |
| Bots | 4 | Enabled, auto rebalance, custom count toggle and bot count |
| Spawning | 25 | Respawn timing and penalties, team/objective weighting and portal proximity |

The five globally undocumented extras are match round count, match score limit, change teams every round, tiebreaker and bot difficulty. Custom starting loadout selection is also marked as an SGL feature.

## Corrections and preservation

- The previous `scoreLimit` recipe identifier remains mapped to `GameMode.GameScoreLimit` for compatibility. Its UI label is now **Match Score Limit** and it is marked as an extra. **Score Limit** edits `ScoreLimit.ScoreLimit`, as documented in-game.
- Max Health accepts decimals. Round Length accepts `-1`, and Portal Gun Recharge Rate accepts `0`, matching the documented special values.
- Numeric inputs retain SGL's expanded limits where already supported. Values outside known in-game endpoints receive a marker. Demonstrated slider endpoints are not engine limits or proven defaults.
- Enemy Portal Wall Spawn Sort Weight has no demonstrated bounds. The UI explicitly describes the SGL range instead of claiming a known in-game range.
- KOTH's verified template omits the kill-scoring component. Its five kill/death/assist scoring controls are omitted. SGL does not inject a different mode's scoring component.
- Editing an exposed value patches matching contexts in the selected phase and retains per-team overrides, unexposed data and unrelated components. Imports with unknown enum values retain them until the user explicitly selects a supported replacement.
- Custom loadout selection patches starting items. The Loadouts category also exposes the documented toggle for changing loadouts during gameplay.

Coverage tests verify all reference IDs and category counts, exact payload properties, enum rejection, numeric bounds, bot/spawn export round trips, legacy recipe compatibility, and the known KOTH omissions. These checks establish save-format correctness; runtime effects still depend on the game.

The core checks verify every observed control for all 88 bases, profile/category mappings, source hashes, full archive round trips, phase isolation, inherited-field reset, untouched objective/loadout preservation and reserves/drop rules. Desktop fixture checks render every category of both phases for every base and verify objective search, extra/range markers, and independent phase edits.
