# Statistics source review

Review date: 2026-11-26. This records the evidence behind [the proposed statistics design](stats-design.md), including the limits of access. No reference implementation or private game data has been copied into SGL.

## Repository status

| Repository | Version and access | Review status |
| --- | --- | --- |
| [SGL](https://github.com/SGLifers/SGL) | Local HEAD `2ed34bd1524c29b2c314b8508d2d7e9622d51951` | Read README, hosting workflow, sharing deployment/authentication, desktop session storage and hosting client integration |
| [SplitgateHost](https://github.com/djcowchaser/SplitgateHost) | Main latest commit `29a2ae2c2ffd1cecb291dad8824cd536b45ae024`, 2026-11-23 | Direct review through the connected GitHub app; principal files listed below |
| [SplitStatsWeb](https://github.com/SGLifers/SplitStatsWeb) | Main latest commit `79a643f04acb35518a485a7c959c3b8c0a53d2f2`, 2026-10-14 | Direct review of README, static page, generated data, CSV, builder and Pages workflow |
| [SplitStats](https://github.com/SGLifers/SplitStats) | Connected app returned 404; Git HTTPS access denied | Direct review blocked. SplitgateHost documents a review at `3ead7628fd070d10dc2b64f14e451956434f6e65` |
| [SplitStatsScoreboardReader](https://github.com/SGLifers/SplitStatsScoreboardReader) | Connected app returned 404; Git HTTPS access denied | Direct review blocked. SplitgateHost documents a review at `fd66b9c50560960d8c20b067a84e54764678d433` |

The user confirmed that access to the two unavailable repositories cannot be supplied and that SplitgateHost contains the required reference material. Proceed from SplitgateHost; direct access to those projects is not a dependency. The table preserves the limits of what was actually reviewed.

## SplitgateHost findings

References below are pinned to the reviewed main revision for reproducibility.

| Source | Relevant finding |
| --- | --- |
| [Statistics documentation](https://github.com/djcowchaser/SplitgateHost/blob/29a2ae2c2ffd1cecb291dad8824cd536b45ae024/docs/STATISTICS.md) | Log-first facts, local scoreboard enrichment, once-sealed submission, reviewed related-project versions, supported layouts and known OCR limitations. The newest dated entries supersede earlier pending-qualification notes |
| [Log parser](https://github.com/djcowchaser/SplitgateHost/blob/29a2ae2c2ffd1cecb291dad8824cd536b45ae024/tools/stats_log.py) | Timestamp/category-aware parsing; SERVER/CLIENT experiences; grouped and ungrouped scores; medals; join/close correlation; match-local UUIDs; authority loss and incomplete-state handling |
| [Local store](https://github.com/djcowchaser/SplitgateHost/blob/29a2ae2c2ffd1cecb291dad8824cd536b45ae024/tools/stats_store.py) | SQLite WAL with full synchronization; parser checkpoint/facts/outbox transactions; immutable sealed bodies; fragmented UTF-8 and bounded source continuity |
| [Bound log transport](https://github.com/djcowchaser/SplitgateHost/blob/29a2ae2c2ffd1cecb291dad8824cd536b45ae024/controller/stats_stream.py) | Current game/process/file binding, pre-PLAY context and prefix hashes, bounded 256 KiB reads, refusal on truncation/replacement/rewrites |
| [Scoreboard capture](https://github.com/djcowchaser/SplitgateHost/blob/29a2ae2c2ffd1cecb291dad8824cd536b45ae024/controller/stats_capture.py) | A guarded end-bound observation; finite native frame burst and optional bounded Tab input. SGL's first collector need not automate this |
| [OCR conversion](https://github.com/djcowchaser/SplitgateHost/blob/29a2ae2c2ffd1cecb291dad8824cd536b45ae024/tools/stats_ocr.py) | Accepted keys: `xp`, `kills`, `damage`, `deaths`, `captures`, `hill_time_seconds`, `score`, `placement`; explicit per-layout completeness and match association checks |
| [Native reader adapter](https://github.com/djcowchaser/SplitgateHost/blob/29a2ae2c2ffd1cecb291dad8824cd536b45ae024/tools/stats_reader_adapter.py) | Explicit observed title/column registry, numeric ambiguity handling, preservation of duplicate names and cautious local name recovery |
| [Collection tests](https://github.com/djcowchaser/SplitgateHost/blob/29a2ae2c2ffd1cecb291dad8824cd536b45ae024/tests/test_stats_collection.py) | Fixture cases for round grouping, FFA scores, authority loss, same-name ambiguity, censored sessions, source replay, exact retry bytes and false events quoted in chat |
| [Reader adapter tests](https://github.com/djcowchaser/SplitgateHost/blob/29a2ae2c2ffd1cecb291dad8824cd536b45ae024/tests/test_stats_reader_adapter.py) | Conservative full-field digit/name evidence and disagreements; repeated preprocessing masks do not become independent readers |
| [Local dashboard documentation](https://github.com/djcowchaser/SplitgateHost/blob/29a2ae2c2ffd1cecb291dad8824cd536b45ae024/dashboard/README.md) | An offline static export, not the requested public cloud service; preserves nulls and match-local identity. Explicitly notes that the KOTH field's unit is not independently verified |
| [Game log path](https://github.com/djcowchaser/SplitgateHost/blob/29a2ae2c2ffd1cecb291dad8824cd536b45ae024/controller/game_end.py) | Proton path ends in `AppData/Local/PortalWars2/Saved/Logs/PortalWars2.log`; Windows default needs pilot verification |

The directly reviewed parser recognizes these event families:

```text
LogLoad: LoadMap: ...
LogGF1047Experience: Starting experience load (SERVER|CLIENT): Default__EXP_..._C
LogGF1047Experience: SetExperienceOutcome called:
  Tag: ...
  Reason: ...
  Winners:
  Scores:
LogGF1047Medal: Awarding <medal-code> to <display-name>
LogNet: NotifyAcceptedConnection: ...
LogNet: Login request: ...
LogNet: Join succeeded: ...
LogNet: UNetConnection::Close: ...
LogGF1047Attribute: Listen Server: GF1047HealthComponent: Sending death event for owner ...
LogExit: Preparing to exit / Exiting.
```

These are pattern descriptions, not real player logs. Multiline outcome parsing and exact log-category checks are essential. The death-event line increments a diagnostic count in the reviewed implementation; it does not identify named deaths. Session correlation uses network evidence transiently and excludes its identifiers from result payloads. Linux process/file guards and private-file modes require Windows equivalents in SGL.

The newest statistics documentation reports a November 23 Domination board with 12 names, 12 team labels and 60 numeric cells checked against the captured image, followed by HTTP 200 delivery. Earlier complete-only delivery changes quarantine uncertain boards. This supports the observed layout and conservative missing-data policy; it does not prove every future OCR read or that the downstream cloud stored or aggregated the result correctly. These source tests and live trials were read, not rerun here.

## Related-project compatibility from indirect evidence

SplitgateHost's documentation says SplitStats emits schema 4 match results while SplitStatsScoreboardReader accepts schema 2 image envelopes and produces a compact scoreboard. Their endpoints serve different purposes, and a historical end-to-end helper imports an older collector package absent from the reviewed companion checkout. Do not direct match results at an image-intake endpoint or assume schemas with similar fields are interchangeable.

The same source reports two data-loss issues that SplitgateHost addresses: preserving departed players when merging a final scoreboard and retaining FFA player scores that occur before any team marker. Both behaviors belong in the new contract.

Their current private code and contracts are not independently verified. SGL should use its own documented schema and the directly reviewed SplitgateHost behavior. If compatibility with one of their receivers becomes necessary later, validate that receiver's contract at that time. No direct review of those repositories is claimed or required for the present design.

## SplitStatsWeb findings

The repository is a plain HTML/CSS/JavaScript tournament site; it has no current cloud API/backend. Its [README](https://github.com/SGLifers/SplitStatsWeb/blob/79a643f04acb35518a485a7c959c3b8c0a53d2f2/README.md) and [Pages workflow](https://github.com/SGLifers/SplitStatsWeb/blob/79a643f04acb35518a485a7c959c3b8c0a53d2f2/.github/workflows/pages.yml) establish automatic GitHub Pages deployment from main, uploading the repository root. Keep it frontend-only; the backend and AWS infrastructure will live separately in SplitStatsInfra. A future frontend build should publish its web output rather than the entire source checkout.

The [page](https://github.com/SGLifers/SplitStatsWeb/blob/79a643f04acb35518a485a7c959c3b8c0a53d2f2/index.html) presents the October 4, 2026 Super Sweats KOTH tournament with expandable match/scoreboard details, medal breakdowns by player/team/match and six timestamped video links. Its [generated dataset](https://github.com/SGLifers/SplitStatsWeb/blob/79a643f04acb35518a485a7c959c3b8c0a53d2f2/data.js) was parsed as JSON after removing its assignment wrapper, without executing it: six matches, twelve team rows, forty-eight player-medal rows, 1,348 medal events and thirty-two scoreboard rows. Scoreboard rows cover matches 1, 2, 3 and 5 only.

The [builder](https://github.com/SGLifers/SplitStatsWeb/blob/79a643f04acb35518a485a7c959c3b8c0a53d2f2/build_data.py) reads an external workbook and [scoreboard CSV](https://github.com/SGLifers/SplitStatsWeb/blob/79a643f04acb35518a485a7c959c3b8c0a53d2f2/scoreboards.csv), applies named-winner overrides and special-case name normalization, then generates `data.js`. It is not a self-contained rebuild from repository files because the workbook lives outside the checkout. Preserve the committed approved artifact and its source metadata. An import needs explicit event scope, idempotency, field provenance and reviewed alias mappings; do not add medal aggregates to the same medal events twice or assume the static tournament has verified Discord identities. The UI's zero-default numeric formatter should not be copied as the generic missing-data policy.

No SplitStatsWeb files or hosting settings were changed. The agreed ownership is Windows collection in SGL, the frontend in SplitStatsWeb, and the backend/service infrastructure, event import tooling and versioned API schemas in the new SplitStatsInfra repository.

## SGL integration findings

- [Hosting workflow](hosting.md): Open/Close Lobby manage Discord announcements. Spinner selections and custom map/mode text are not game-state proof; using them for ranked mode/rules classification would overstate current behavior.
- [Sharing architecture](../sharing/README.md): AWS Lambda, HTTP API, private S3 and on-demand DynamoDB already exist, with Discord sign-in and account-wide budget controls. A PostgreSQL statistics design adds a new operational/cost baseline.
- [Hosting client](../src/SGL.Core/HostingAnnouncements.cs): HTTPS endpoint validation, bounded responses, explicit request identities and safe user-facing failures provide patterns for a statistics client.
- [Desktop session persistence](../src/SGL.Desktop/SharingSession.cs): Current sessions use Windows DPAPI and service-specific entropy; statistics credentials should use equivalent user-bound protection.

No application behavior, AWS resources or Discord configuration changed during this design review.
