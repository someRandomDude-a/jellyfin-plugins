# GitHub build and release path

## What uploading/pushing does

An ordinary source push or pull request runs independent plugin/common checks on Ubuntu/Windows, workflow lint and separate installed interoperability/browser checks. It does **not** create a release. Generated `bin`, `obj`, local artifacts and `.env` must not be uploaded. Activity's pinned distribution and source/license inventory remain governed by its documented build policy.

An approved plugin-prefixed tag such as `private-syncplay/vX.Y.Z`, or manual `Publish one independent plugin` dispatch selecting that **existing** tag, starts the release path. See [independent gate ownership and feeds](independent-plugin-releases.md).

1. Verify the immutable tag resolves to the checked-out commit.
2. Run the selected plugin's tests, common/actual dependency contracts, workflow lint and standalone installed checks at that exact tag. Standalone installs run with/without pinned native Security; Private SyncPlay additionally runs native-socket/media/consumer integration. Other plugins' test failures and the separate 32-combination interoperability matrix do not gate this release.
3. Build only the selected plugin for `linux-x64`, `linux-arm64` and `win-x64`, preserving its previous platform catalog history. Generate inventories and checksums.
4. Download/merge those artifacts and validate the complete selected 3-ZIP/three-catalog set before any release write: stable identity, version/ABI, exact encoded repository/tag/runtime URLs, MD5/SHA256, inventories and archive safety; unrelated/missing upload files fail closed.
5. After the `plugin-release` environment approval, create a draft, upload assets and platform catalogs, publish only if those commands succeed, then update that plugin's independent catalog branch without force. Existing releases are not overwritten; failed uploads leave a draft for inspection rather than silently replacing assets. A catalog-update failure after publication requires explicit repair; it does not undo the immutable release or touch another plugin's feed.

Configure required reviewers on the GitHub **plugin-release** environment **before** enabling publication. The workflow declaration alone does not enforce a human reviewer unless repository environment settings do. Workflow permissions are read-only except the isolated publication job's release write permission. Ensure Actions and Docker-backed Ubuntu jobs are enabled in the destination repository.

Cloudflare testing is separate: manually run `Disposable Jellyfin integration` with `cloudflare_tunnel=true`. This exposes only a harmless disposable sentinel, needs no account/token, retains normal TLS validation and cleans labeled containers. Public tunnels and live Discord credentials are not required CI/release jobs. Human commands/real Discord Activity launch cannot be certified by those automated jobs.

## Current local evidence, not a GitHub-hosted guarantee

- Current Jellyfin 12.3 installed matrix: **32/32** (`artifacts/independent-final-combinations/results.json`).
- Current root tests: **781 .NET / 156 JavaScript**, plus offline release fixtures (`artifacts/independent-release-full-tests.log`).
- Independent Private SyncPlay **three-runtime** package set validates (`artifacts/independent-syncplay-release-check-final`). Historical r10 all-plugin/three-platform packaging evidence remains in the review history; it is not a gate on unrelated releases.
- Local workflow lint and installed browser checks passed. GitHub-hosted workflows have not yet run on these uncommitted changes.

Windows package generation is not installed Windows acceptance. ARM cross-packaging is not physical ARM certification. The Activity currently opens Jellyfin externally; embedded video playback, human live acceptance and other [v1 release gates](v1-release-gates.md) remain incomplete. Do not describe the full suite as v1-ready based on build/matrix results alone. No commit, tag, push or publication is authorized by preparing this pipeline.
