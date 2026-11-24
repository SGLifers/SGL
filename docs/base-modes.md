# Base gamemode templates

SGL lists all **88 presets from Splitgate View All** alphabetically. Each was selected in game, saved as a custom mode before inspection, and audited across every Advanced Settings tab and scroll page in both Standard Rules and Tiebreaker. Audit: November 23–24, 2026, game build `3.1-643074`.

`src/SGL.Core/Data/base-modes.json` records each template resource, exact variant and SHA-256. Payloads preserve the saved options; only the audit save's custom name and JSON formatting are normalized. No save-index records, player identities or local archive paths are included. Creation changes only `Options.CustomName` until the user edits rules or loadouts.

`gamemode-ui-audit.json` records the controls visible for each base and rule phase. Fields present in the save but absent from that mode's menu receive an SGL extra marker. Different team sizes, competitive loadouts, and faction variants retain separate templates. Existing template IDs remain compatible, including the historical `onslight-graffiti` ID for **Onslaught Graffiti**.

Untouched source values are retained even when the game UI displays a clamped value. For example, SWAT saves contain precision damage 10000, while the slider displays 2.5. This is save-format and menu coverage; it does not establish the runtime effect of every possible override. See [settings audit](settings-audit.md).

| Base mode | Template ID | Game variant | Visible controls per phase |
| --- | --- | --- | ---: |
| Arena Royale (1 Biome Voting) | `arena-royale` | `GV_Hangman_Team_1BiomeVote` | 87 / 87 |
| Big Head Snipers | `big-head-snipers` | `GV_Deathmatch_Team_BigHeadSnipers` | 115 / 115 |
| Chaos Domination (Rounds) | `chaos-domination` | `GV_DOM_Team_Chaos_Rounds` | 128 / 128 |
| Chaos Firecracker | `chaos-firecracker` | `GV_Firecracker_Team_Chaos_Asymmetric` | 126 / 126 |
| Chaos Hotzone | `chaos-hotzone` | `GV_Hotzone_Team_Chaos` | 128 / 128 |
| Chaos Splitball | `chaos-splitball` | `GV_Splitball_Team_Chaos` | 126 / 126 |
| Chaos Team Deathmatch (Rounds) | `chaos-team-deathmatch-rounds` | `GV_Deathmatch_Team_Chaos_Rounds` | 115 / 115 |
| Classic Domination | `classic-domination` | `GV_DOM_Team_1round_CarbineAR` | 128 / 128 |
| Classic King of the Hill | `classic-king-of-the-hill` | `GV_KOTH_Team_CarbineAR` | 128 / 128 |
| Classic Team Deathmatch | `classic-team-deathmatch` | `GV_Deathmatch_Team_1round_CarbineAR` | 115 / 115 |
| Domination (1 Round) | `domination-1-round` | `GV_DOM_Team_1round_Default` | 128 / 128 |
| Domination (Carbine Pistol) | `domination-carbine-pistol` | `GV_DOM_Team_1round_CarbinePistol` | 128 / 128 |
| Domination (Carbine Pistol, Competitive) | `domination-carbine-pistol-competitive` | `GV_DOM_Team_1round_CarbinePistol_Competitive` | 128 / 128 |
| Domination (Competitive) | `domination-competitive` | `GV_DOM_Team_1round_Competitive` | 128 / 128 |
| Domination (Rounds) | `domination-rounds` | `GV_DOM_Team_Rounds` | 128 / 128 |
| FFA Deathmatch | `ffa-deathmatch` | `GV_Deathmatch_FFA_Default` | 115 / 115 |
| FFA Deathmatch (Fists) | `ffa-deathmatch-fists` | `GV_Deathmatch_FFA_Fists` | 115 / 115 |
| FFA Fiesta | `ffa-fiesta` | `GV_Deathmatch_FFA_Fiesta` | 115 / 115 |
| FFA One in the Chamber | `ffa-one-in-the-chamber` | `GV_Deathmatch_FFA_OITC_Default` | 115 / 115 |
| FFA One in the Chamber (Goliath) | `ffa-one-in-the-chamber-goliath` | `GV_Deathmatch_FFA_OITC_Goliath` | 115 / 115 |
| Firecracker | `firecracker` | `GV_Firecracker_Team_Asymmetric` | 126 / 126 |
| Firecracker (Competitive) | `firecracker-competitive` | `GV_Firecracker_Team_Asymmetric_Competitive` | 126 / 126 |
| Firecracker (Neutral) | `firecracker-neutral` | `GV_Firecracker_Team_Default` | 126 / 126 |
| Flying Splitball | `flying-splitball` | `GV_Splitball_Team_FlyingSplitball` | 126 / 126 |
| Graffiti | `graffiti` | `GV_Graffiti_Team_Default` | 110 / 110 |
| Gun Game | `gun-game` | `GV_GunGame_FFA_Default` | 119 / 119 |
| Hardcore Domination (Rounds) | `hardcore-domination-rounds` | `GV_DOM_Team_Hardcore_Rounds` | 128 / 128 |
| Hardcore Firecracker | `hardcore-firecracker` | `GV_Firecracker_Team_Hardcore_Asymmetric` | 126 / 126 |
| Hardcore Hotzone | `hardcore-hotzone` | `GV_Hotzone_Team_Hardcore` | 128 / 128 |
| Hardcore Splitball | `hardcore-splitball` | `GV_Splitball_Team_Hardcore` | 126 / 126 |
| Hardcore Team Deathmatch (Rounds) | `hardcore-team-deathmatch-rounds` | `GV_Deathmatch_Team_Hardcore_Rounds` | 115 / 115 |
| Hotzone | `hotzone` | `GV_Hotzone_Team_Default` | 128 / 128 |
| Hotzone (1 Round) | `hotzone-1-round` | `GV_Hotzone_Team_1round` | 128 / 128 |
| Hotzone (Competitive) | `hotzone-competitive` | `GV_Hotzone_Team_Default_Competitive` | 128 / 128 |
| Instagib | `instagib` | `GV_Deathmatch_Team_Instagib_Default` | 115 / 115 |
| King of the Hill | `koth` | `GV_KOTH_Team_Default` | 128 / 128 |
| King of the Hill (Carbine Pistol) | `king-of-the-hill-carbine-pistol` | `GV_KOTH_Team_CarbinePistol` | 128 / 128 |
| King of the Hill (Carbine Pistol, Competitive) | `king-of-the-hill-carbine-pistol-competitive` | `GV_KOTH_Team_CarbinePistol_Competitive` | 128 / 128 |
| King of the Hill (Competitive) | `king-of-the-hill-competitive` | `GV_KOTH_Team_Competitive` | 128 / 128 |
| Laser Tag | `laser-tag` | `GV_Deathmatch_Team_1round_LaserTag` | 115 / 115 |
| Multi-team Big Head Snipers | `multi-team-big-head-snipers` | `GV_Deathmatch_3Team_BigHeadSnipers` | 115 / 115 |
| Multi-team Chaos Deathmatch | `multi-team-chaos-deathmatch` | `GV_Deathmatch_3Team_Chaos` | 115 / 115 |
| Multi-team Chaos Domination | `multi-team-chaos-domination` | `GV_DOM_3Team_Chaos` | 128 / 128 |
| Multi-team Chaos Hotzone | `multi-team-chaos-hotzone` | `GV_Hotzone_3Team_Chaos` | 128 / 128 |
| Multi-team Chaos Splitball | `multi-team-chaos-splitball` | `GV_Splitball_3Team_Chaos` | 126 / 126 |
| Multi-team Deathmatch | `multi-team-deathmatch` | `GV_Deathmatch_3Team_Default` | 115 / 115 |
| Multi-team Deathmatch (2v2v2v2) | `multi-team-deathmatch-2v2v2v2` | `GV_Deathmatch_2v2v2v2_Default` | 115 / 115 |
| Multi-team Deathmatch (3v3v3) | `multi-team-deathmatch-3v3v3` | `GV_Deathmatch_3v3v3_Default` | 115 / 115 |
| Multi-team Deathmatch (4v4v4v4v4v4) | `multi-team-deathmatch-4v4v4v4v4v4` | `GV_Deathmatch_4v4v4v4v4v4_Default` | 115 / 115 |
| Multi-team Deathmatch (6v6v6v6) | `multi-team-deathmatch-6v6v6v6` | `GV_Deathmatch_6v6v6v6_Default` | 115 / 115 |
| Multi-team Domination | `multi-team-domination` | `GV_DOM_3Team_Default` | 128 / 128 |
| Multi-team Hotzone | `multi-team-hotzone` | `GV_Hotzone_3Team_Default` | 128 / 128 |
| Multi-team Speed Bats | `multi-team-speed-bats` | `GV_Deathmatch_3Team_SpeedBats` | 115 / 115 |
| Multi-team Splitball | `multi-team-splitball` | `GV_Splitball_3Team_Default` | 126 / 126 |
| One in the Chamber | `one-in-the-chamber` | `GV_Deathmatch_Team_OITC_Default` | 115 / 115 |
| One in the Chamber (Goliath) | `one-in-the-chamber-goliath` | `GV_Deathmatch_Team_OITC_Goliath` | 115 / 115 |
| Onslaught Graffiti | `onslight-graffiti` | `GV_Graffiti_3Team_Default` | 110 / 110 |
| Race | `race` | `GV_Race_FFA_Default` | 111 / 111 |
| Showdown Firecracker | `showdown-firecracker` | `GV_Firecracker_Team_1life_Asymmetric` | 126 / 126 |
| Speed Bats | `speed-bats` | `GV_Deathmatch_Team_SpeedBats` | 115 / 115 |
| Splitball | `splitball` | `GV_Splitball_Team_Default` | 126 / 126 |
| Splitball (1 Round) | `splitball-1-round` | `GV_Splitball_Team_1round_Default` | 126 / 126 |
| Splitball (Competitive) | `splitball-competitive` | `GV_Splitball_Team_Default_Competitive` | 126 / 126 |
| Takedown | `takedown` | `GV_Takedown_Team_Default` | 115 / 115 |
| Takedown (Carbine Pistol) | `takedown-carbine-pistol` | `GV_Takedown_Team_CarbinePistol` | 115 / 115 |
| Takedown (Carbine Pistol, Competitive) | `takedown-carbine-pistol-competitive` | `GV_Takedown_Team_CarbinePistol_Competitive` | 115 / 115 |
| Takedown (OG) | `takedown-og` | `GV_Takedown_Team_CarbineAR` | 115 / 115 |
| Takedown Classic | `takedown-classic` | `GV_Takedown_Team_Classic` | 115 / 115 |
| Takedown Classic (OG) | `takedown-classic-og` | `GV_Takedown_Team_Classic_CarbineAR` | 115 / 115 |
| Takedown Classic (OG, Bots) | `takedown-classic-og-bots` | `GV_Takedown_Team_Classic_CarbineAR_Bots` | 115 / 115 |
| Takedown Classic (OG, Carbine Pistol) | `takedown-classic-og-carbine-pistol` | `GV_Takedown_Team_Classic_CarbinePistol` | 115 / 115 |
| Takedown Classic (OG, Carbine Pistol, Competitive) | `takedown-classic-og-carbine-pistol-competitive` | `GV_Takedown_Team_Classic_CarbinePistol_Competitive` | 115 / 115 |
| Teabag Confirmed | `teabag-confirmed` | `GV_TeabagConfirmed_Team_Default` | 112 / 112 |
| Team Deathmatch (1 Round) | `tdm` | `GV_Deathmatch_Team_1round_Default` | 115 / 115 |
| Team Deathmatch (1v1) | `tdm-1v1` | `GV_Deathmatch_Team_1v1` | 115 / 115 |
| Team Deathmatch (Carbine Pistol) | `team-deathmatch-carbine-pistol` | `GV_Deathmatch_Team_1round_CarbinePistol` | 115 / 115 |
| Team Deathmatch (Carbine Pistol, Competitive) | `team-deathmatch-carbine-pistol-competitive` | `GV_Deathmatch_Team_1round_CarbinePistol_Competitive` | 115 / 115 |
| Team Deathmatch (Competitive) | `team-deathmatch-competitive` | `GV_Deathmatch_Team_1round_Competitive` | 115 / 115 |
| Team Deathmatch (Fists) | `team-deathmatch-fists` | `GV_Deathmatch_Team_1round_Fists` | 115 / 115 |
| Team Deathmatch (Rounds) | `team-deathmatch-rounds` | `GV_Deathmatch_Team_Rounds` | 115 / 115 |
| Team Fiesta | `team-fiesta` | `GV_Deathmatch_Team_Fiesta` | 115 / 115 |
| Team Shotty Snipers (Aeros) | `team-shotty-snipers-aeros` | `GV_Deathmatch_Team_ShottySnipers_Aeros` | 115 / 115 |
| Team Shotty Snipers (Meridian) | `team-shotty-snipers-meridian` | `GV_Deathmatch_Team_ShottySnipers_Meridian` | 115 / 115 |
| Team Shotty Snipers (Powers) | `team-shotty-snipers-powers` | `GV_Deathmatch_Team_ShottySnipers_Powers` | 115 / 115 |
| Team Shotty Snipers (Sabrask) | `team-shotty-snipers-sabrask` | `GV_Deathmatch_Team_ShottySnipers_Sabrask` | 115 / 115 |
| Team SWAT (Aeros) | `swat` | `GV_Deathmatch_Team_SWAT_Aeros` | 115 / 115 |
| Team SWAT (Meridian) | `team-swat-meridian` | `GV_Deathmatch_Team_SWAT_Meridian` | 115 / 115 |
| Team SWAT (Sabrask) | `team-swat-sabrask` | `GV_Deathmatch_Team_SWAT_Sabrask` | 115 / 115 |
