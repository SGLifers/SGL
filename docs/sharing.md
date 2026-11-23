# Sharing maps and gamemodes

SGL 0.3 adds portable packages and a Shared library. Portable `.sglpack` files work without a service or account. Online sharing requires the administrator to deploy the service and configure its Discord application first. The deployed service endpoint is embedded in the app.

## Send a portable package

1. Open **Shared library → Publish**.
2. Choose **Map**, **Gamemode**, or **Map + gamemode bundle** and select your local content. The current gamemode editor can be exported before saving it to the game.
3. Select **Export local package** and send the `.sglpack` file to another player.
4. The recipient chooses **Shared library → Browse → Import package**, reviews the contents and installs with Splitgate closed.

SGL validates the package, makes a complete game-save backup and registers both bundle entries in one transaction. A bundle also creates a local map-to-mode association. Players select the map and mode separately in the custom lobby. Recipients must already have saved a custom gamemode once so SGL can determine their save ownership.

Offline files preserve creator credits, but their claimed account identities are not authenticated. Imported files never acquire the service's verified attribution status. Packages contain map/configuration data, not executable installers.

## Map tags and discovery

Shareable tags and the tag/publisher filters require the **0.3.3 preview or later**. Version 0.3.2 keeps tags local.

Select a map in **Maps → Edit local tags…** and enter comma-separated labels such as **TDM, 1v1, Friday event**. A map supports up to 20 tags of 40 characters each. The Publish tab previews the tags that will be included.

Tags travel with online map publications, map/gamemode bundles and portable packages. Each published revision retains its own tags; tag-only edits create a new revision and reuse the existing map file when its contents are unchanged. Existing published items have no tags until their owner publishes a tagged revision. Older clients that omit tags preserve the current labels during updates.

Installing a map imports its pinned revision's tags. Later installations update tags that the host has left unchanged. If the host has added or removed tags locally, SGL preserves that complete personal tag set. A separate map copy receives the incoming tags. Tag edits are shared only when the host explicitly publishes or exports the map again.

In **Shared library → Browse**, enter an exact **Tag** and/or a **Publisher name or Discord ID**, then choose **Apply filters** or press Enter. Matching ignores case; creator filtering uses partial publisher names or IDs, not free-form original-author credits. Filters apply to public items, or your own items when **My publications** is checked, and continue through **Load more**. Empty result pages do not mean the remaining pages have no matches. **Search loaded results** narrows the downloaded results by title, description, publisher or tags. **Clear filters** starts a fresh listing.

## Publish online

Once the service is configured, sign in using **Sign in with Discord**. SGL requests only the Discord `identify` scope. It does not join servers, read messages, or post to Discord.

Choose the content, enter a title, credits and change note, then select visibility:

| Visibility | Access |
| --- | --- |
| Public | Listed in the library; anyone can read/download it. |
| Unlisted | Omitted from browsing; anyone with its link can read/download it. |
| Private | Only the publishing account can read/download it. |

Publishing is an explicit action. Local saves, local Git history, Windows usernames, CloudSave ownership and local paths are not automatically uploaded. Map archives retain their own embedded metadata; review those files before publishing. Public profiles expose Discord ID, display name and avatar. Credits identify original map authors separately from the account uploading a file.

The copied link pins the exact revision. **Settings & help → Enable Open in SGL links** registers the `sgl:` protocol for the current Windows user. A link opens a review screen; installation always requires confirmation. Keep SGL.exe in its permanent folder before enabling links. Moving the EXE requires enabling them again.

Cancel stops further requests; revisions that already completed remain published. Bundle assets are published before the final bundle, so an interrupted publication can leave individual map or mode items in My publications.

## Edits, revisions and installation

The publisher can add immutable revisions to an item. Other members use **My own version of selected shared item** to publish a fork with an upstream item/revision reference and original credits. A Discord identity is attached by the service, not accepted from the uploaded file. Display names can change; the Discord ID remains the identity reference. This attribution identifies the uploader/editor and does not prove ownership of the underlying map.

Use **Compare revisions** for named gamemode-setting differences. Map revisions retain separate binary versions and hashes; geometry comparisons are not provided. Use **Load older revisions** to browse beyond the first page. Installing an older revision is an explicit restore operation; it preserves later service revisions and makes a local backup.

A bundle pins one map revision and one mode revision. Publishing a newer map or mode does not silently change existing bundles. Public bundles require public dependencies; unlisted bundles cannot reference private dependencies. Making an item public exposes its retained revision history. The app creates separate dependency publications where needed to avoid exposing previously private history through a public bundle. Changing dependencies to private later can make a previously shared bundle unavailable.

Installation uses recipient save ownership and retains stable local identities for clean updates. When an installed shared item has local edits, SGL installs a separate copy and keeps those edits. Reinstalling an unchanged revision does nothing. Offline files with a different revision also install as copies. Hash/size checks detect corruption; online attribution is only as trustworthy as the configured service.

Local tracking lives under `%LOCALAPPDATA%\SGL\Profiles\<profile>\shared-library.json`. Full game backups live under `<CloudSave>\Backup\SGL-*`; shared-install receipts include recovery tracking. Per-user publication mappings are separate from installation mappings. Sessions are encrypted with Windows DPAPI for the current user; Discord access tokens and client secrets are not stored on the PC. Signing out clears the local session and attempts server-side revocation. Unreachable-server sessions expire after 30 days.

## Service budget pauses

The sharing service has account-wide monthly cost alerts at 50%, 80% and 100% of its configured budget (default $10). When AWS reports a breach of the full budget, the guard pauses the online API and S3 uploads/downloads. Local editing, local history, backups and portable package sharing continue to work. Only an authorized operator can restore the online service; it does not reopen automatically next month. See the [operator budget guide](../sharing/README.md#budget-alerts-and-automatic-cutoff).

Billing data can be delayed, and storage/monitoring costs continue while paused. These controls reduce further sharing activity; they do not guarantee an exact spending ceiling. Operators must confirm monitoring email delivery and complete the live cutoff checks before community rollout.

## Preview limitations

A live Discord round-trip and host/clean-machine pilot are still required before a stable release. Shared editing is owner-only with forks; collaborative permissions, cloud deletion/moderation controls, notifications and map geometry diffs are not included. Operators can hide abusive items through the private administrative datastore. Published revisions and content are retained; there is no automatic cloud garbage collection. A previously issued download URL can remain usable for up to 15 minutes after visibility changes. Deleting a local item does not remove its cloud publication.
