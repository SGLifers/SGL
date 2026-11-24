# Base gamemode templates

SGL lists its base modes alphabetically. It embeds 33 creation templates: the original KOTH, Team Deathmatch and SWAT, plus 30 presets read from locally saved game modes on November 23, 2026. The new payload resources are byte-for-byte copies of `payload.json` from those saves. No save-index records, player identities or archive paths are included.

`src/SGL.Core/Data/base-modes.json` records each template resource, exact variant and SHA-256. New-mode creation changes only `Options.CustomName`; all objective components, context inheritance, faction restrictions, randomized loadouts, team counts, scores and timers remain as saved until explicitly edited. Different team-size presets have separate entries.

Chaos Domination comes from the save named **Chaos Domination (Rounds)**. **Onslight Graffiti** retains the supplied save spelling. Chaos Hotzone and Chaos Team Deathmatch were also present locally but are outside the requested set.

These are source-verified templates, not a claim that every SGL override works in every mode. Game-specific behavior still needs in-game testing. Settings absent from a template remain unavailable. Legacy recipe bases (`koth-v1`, `tdm-v1`, `swat-v1`) remain supported; new modes use full payload recipes.

| Base mode | Template ID | Game variant |
| --- | --- | --- |
| Arena Royale | `arena-royale` | `GV_Hangman_Team_1BiomeVote` |
| Big Head Snipers | `big-head-snipers` | `GV_Deathmatch_Team_BigHeadSnipers` |
| Chaos Domination | `chaos-domination` | `GV_DOM_Team_Chaos_Rounds` |
| Chaos Firecracker | `chaos-firecracker` | `GV_Firecracker_Team_Chaos_Asymmetric` |
| Chaos Splitball | `chaos-splitball` | `GV_Splitball_Team_Chaos` |
| Domination (1 Round) | `domination-1-round` | `GV_DOM_Team_1round_Default` |
| FFA Deathmatch | `ffa-deathmatch` | `GV_Deathmatch_FFA_Default` |
| FFA Fiesta | `ffa-fiesta` | `GV_Deathmatch_FFA_Fiesta` |
| FFA One in the Chamber | `ffa-one-in-the-chamber` | `GV_Deathmatch_FFA_OITC_Default` |
| FFA One in the Chamber (Goliath) | `ffa-one-in-the-chamber-goliath` | `GV_Deathmatch_FFA_OITC_Goliath` |
| Firecracker | `firecracker` | `GV_Firecracker_Team_Asymmetric` |
| Flying Splitball | `flying-splitball` | `GV_Splitball_Team_FlyingSplitball` |
| Graffiti | `graffiti` | `GV_Graffiti_Team_Default` |
| Gun Game | `gun-game` | `GV_GunGame_FFA_Default` |
| Hotzone (1 Round) | `hotzone-1-round` | `GV_Hotzone_Team_1round` |
| King of the Hill | `koth` | `GV_KOTH_Team_Default` |
| Multi-team Deathmatch | `multi-team-deathmatch` | `GV_Deathmatch_3Team_Default` |
| Multi-team deathmatch (2v2v2v2) | `multi-team-deathmatch-2v2v2v2` | `GV_Deathmatch_2v2v2v2_Default` |
| Multi-team deathmatch (3v3v3) | `multi-team-deathmatch-3v3v3` | `GV_Deathmatch_3v3v3_Default` |
| Multi-team deathmatch (4v4v4v4v4v4) | `multi-team-deathmatch-4v4v4v4v4v4` | `GV_Deathmatch_4v4v4v4v4v4_Default` |
| Multi-team deathmatch (6v6v6v6) | `multi-team-deathmatch-6v6v6v6` | `GV_Deathmatch_6v6v6v6_Default` |
| Onslight Graffiti | `onslight-graffiti` | `GV_Graffiti_3Team_Default` |
| Race | `race` | `GV_Race_FFA_Default` |
| Showdown Firecracker | `showdown-firecracker` | `GV_Firecracker_Team_1life_Asymmetric` |
| Speed bats | `speed-bats` | `GV_Deathmatch_Team_SpeedBats` |
| Splitball (1 Round) | `splitball-1-round` | `GV_Splitball_Team_1round_Default` |
| SWAT | `swat` | `GV_Deathmatch_Team_SWAT_Aeros` |
| Takedown OG | `takedown-og` | `GV_Takedown_Team_CarbineAR` |
| Teabag Confirmed | `teabag-confirmed` | `GV_TeabagConfirmed_Team_Default` |
| Team Deathmatch | `tdm` | `GV_Deathmatch_Team_1round_Default` |
| Team deathmatch (1v1) | `tdm-1v1` | `GV_Deathmatch_Team_1v1` |
| Team shotty snipers (Aeros) | `team-shotty-snipers-aeros` | `GV_Deathmatch_Team_ShottySnipers_Aeros` |
| Team shotty snipers (Powers) | `team-shotty-snipers-powers` | `GV_Deathmatch_Team_ShottySnipers_Powers` |
