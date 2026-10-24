# GitHub build and release path

## What uploading/pushing does

An ordinary source push or pull request runs `All tests`: Ubuntu/Windows tests, locked Activity build/tests, workflow lint and installed native-theme/browser checks. It does **not** create a release. Generated `dist`, `bin`, `obj`, local artifacts and `.env` must not be uploaded; the Activity is rebuilt from its committed source and `package-lock.json`.

An approved `vX.Y.Z` tag push, or manual `Publish plugin packages` dispatch selecting an **existing** tag, starts the release path:

1. Verify the immutable tag resolves to the checked-out commit.
2. Run the complete tests/browser/workflow-lint workflow and reusable installed integration at that exact tag. Integration includes all 32 Linux x64 plugin combinations, with/without pinned native Security, and standalone SyncPlay fixtures. The matrix packages use the requested release version.
3. Build all four packages for `linux-x64`, `linux-arm64` and `win-x64`, preserving previous platform catalog history. Generate inventories and checksums.
4. Download/merge those artifacts and validate the complete 12-ZIP/three-catalog set before any release write: stable identities, version/ABI, exact repository/tag/runtime URLs, MD5/SHA256, inventories and archive safety; unexpected upload files fail closed.
5. After the `plugin-release` environment approval, create a draft, upload assets and platform catalogs, then publish only if those commands succeed. Existing releases are not overwritten; failed uploads leave a draft for inspection rather than silently replacing assets.

Configure required reviewers on the GitHub **plugin-release** environment **before** enabling publication. The workflow declaration alone does not enforce a human reviewer unless repository environment settings do. Workflow permissions are read-only except the isolated publication job's release write permission. Ensure Actions and Docker-backed Ubuntu jobs are enabled in the destination repository.

Cloudflare testing is separate: manually run `Disposable Jellyfin integration` with `cloudflare_tunnel=true`. This exposes only a harmless disposable sentinel, needs no account/token, retains normal TLS validation and cleans labeled containers. Public tunnels and live Discord credentials are not required CI/release jobs. Human commands/real Discord Activity launch cannot be certified by those automated jobs.

## Current local evidence, not a GitHub-hosted guarantee

- r10 standard installed matrix: **32/32** (`artifacts/runtime-review/r10-combinations/results.json`).
- Final root tests: **778 .NET / 142 JavaScript**, plus offline release fixtures (`artifacts/r10-release-gate-tests.log`).
- All **12 runtime/plugin ZIPs** built and merged-release validation passed (`artifacts/runtime-review/activity-r10/merged-release-check`).
- Local workflow lint and installed browser checks passed. GitHub-hosted workflows have not yet run on these uncommitted changes.

Windows package generation is not installed Windows acceptance. ARM cross-packaging is not physical ARM certification. The Activity currently opens Jellyfin externally; embedded video playback, human live acceptance and other [v1 release gates](v1-release-gates.md) remain incomplete. Do not describe the full suite as v1-ready based on build/matrix results alone. No commit, tag, push or publication is authorized by preparing this pipeline.
