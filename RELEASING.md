# Releasing SGL

1. Use the latest servicing .NET 10 SDK. Update/rebuild self-contained releases for runtime security fixes.
2. Run core and desktop checks. Verify backups, stale saves, single-file Git operation and an update between two fixture builds.
3. Tag `vMAJOR.MINOR.PATCH`, or run the Release workflow with that numeric version. Prerelease version suffixes are not supported by the current updater.
4. CI builds and tests, packages `SGL.zip`, `SGL.exe`, `SHA256SUMS.txt` and a signed `update.json`, then creates a **draft** release. Review its release notes and assets before publishing.
5. Publish only after host testing. The latest non-prerelease release becomes the startup update feed.

## Update signing

The pinned verification key is `src/SGL.Core/Data/update-public-key.txt`. The private ECDSA P-256 key must never be committed. Store it in the repository Actions secret `SGL_UPDATE_PRIVATE_KEY`.

To generate a new project key before its first release:

```powershell
dotnet run --project tools/SGL.Release -- keygen C:\secure\sgl-private.pem src/SGL.Core/Data/update-public-key.txt
```

Changing the public key after distribution requires a trusted transition release; replacing it arbitrarily breaks updates for existing users. The metadata signature is separate from Authenticode and does not remove Windows reputation warnings.

For local signing, set `SGL_SIGNING_KEY_FILE` to a private-key path or supply `SGL_UPDATE_PRIVATE_KEY` through the environment. Run `scripts/package.ps1`. The script refuses to create a signed update manifest when the key is absent. Never paste private keys into command arguments, logs, issues or release notes.

## Failure tests

- Bad signature, checksum, package size, wrong repository and non-HTTPS URL must be rejected before replacement.
- Current-mode edits survive an unavailable update server and application updates.
- A blocked update helper must not remove the installed application. If the new executable cannot launch, retain `.previous` and attempt rollback.
- A blocked/quarantined update may require manual download and Windows Security review; no automatic bypass is attempted.
- Retain the release ZIP as a manual recovery option. Do not automatically update in the middle of a game-save transaction or editor session with unsaved changes.
