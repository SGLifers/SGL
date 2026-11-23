# Advanced settings audit

SGL 0.2.0 maps all **115 documented Standard Rules settings**, with five additional controls. The previous editor mapped 37 documented settings and four extras. This audit adds 78 documented settings and Bot Difficulty, for 120 controls in total.

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

The five extras are match round count, match score limit, change teams every round, tiebreaker and bot difficulty. Custom starting loadout selection is also marked as an SGL feature.

## Corrections and preservation

- The previous `scoreLimit` recipe identifier remains mapped to `GameMode.GameScoreLimit` for compatibility. Its UI label is now **Match Score Limit** and it is marked as an extra. **Score Limit** edits `ScoreLimit.ScoreLimit`, as documented in-game.
- Max Health accepts decimals. Round Length accepts `-1`, and Portal Gun Recharge Rate accepts `0`, matching the documented special values.
- Numeric inputs retain SGL's expanded limits where already supported. Values outside known in-game endpoints receive a marker. Demonstrated slider endpoints are not engine limits or proven defaults.
- Enemy Portal Wall Spawn Sort Weight has no demonstrated bounds. The UI explicitly describes the SGL range instead of claiming a known in-game range.
- KOTH's verified template omits the kill-scoring component. Its five kill/death/assist scoring controls are marked unavailable. SGL does not inject a different mode's scoring component.
- Editing an exposed value patches existing matching contexts and retains per-team overrides, unexposed data and unrelated components. Imports with unknown enum values retain them until the user explicitly selects a supported replacement.
- Custom loadout selection patches starting items. The Loadouts category also exposes the documented toggle for changing loadouts during gameplay.

Coverage tests verify all reference IDs and category counts, exact payload properties, enum rejection, numeric bounds, bot/spawn export round trips, legacy recipe compatibility, and the known KOTH omissions. These checks establish save-format correctness; runtime effects still depend on the game.
