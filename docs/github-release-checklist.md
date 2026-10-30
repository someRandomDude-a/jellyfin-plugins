# GitHub build and release path

## Prerequisites and setup

Use the [current release guide](independent-plugin-releases.md), a reviewed source
commit and an unused immutable version. Configure repository bot/branch rules for
automatic maintenance and retain the **plugin-release** environment's required
approvals. Latest-only freshness and acceptance must pass before release; a local
bump/freshness pass does not authorize publication. Catalog-only repair
requires an already published release and never rebuilds its immutable assets.

## What uploading/pushing does

An ordinary source push or pull request runs independent plugin/common checks on Ubuntu/Windows, workflow lint and separate installed interoperability/browser checks. It does **not** create a release. Generated `bin`, `obj`, local artifacts and `.env` must not be uploaded. Each final staged package requires its own source/license inventory.

Use **Release plugins**, select one plugin or `all`, and enter `vX.Y.Z`. One engine uses the exact selected source and creates/reuses immutable tags under its checks. Plugin-prefixed tag pushes use the same engine. See [gate ownership and feeds](independent-plugin-releases.md).

1. Verify the immutable tag resolves to the checked-out commit.
   Any existing release tag must match the selected source exactly, not merely be
   its ancestor. Pushing fixes and rerunning an older version still selects that
   version's old source. Choose an unused version for the fixes; never move tags.
2. Pass the shared latest-only dependency gate and bind tests/packages to its target/toolchain snapshot. Selected releases require their unit/common/installed checks and relevant external-provider fixtures; SyncPlay also checks native media/SDK consumers. Suite releases require full Windows/Linux root tests, browser acceptance, all 64 subsets (128 with/without native Security), native recovery and external-service gates. Failures block publication, not trigger older-version fallback.
3. Build the selected plugin for `linux-x64`, `linux-arm64` and `win-x64`, or all six plugins/18 packages for a suite release. Preserve published history and generate inventories/checksums.
4. Download/merge and validate the full selected three-package set or suite 18-package set plus three architecture catalogs: stable identities, version/ABI, exact encoded URLs, checksums, inventories and archive safety. Missing or unrelated files fail closed before publication.
5. After protected approval, recheck dependency/upstream/source/image freshness. An intervening release requires a new complete run. Create a draft, upload validated assets, publish only after success, then merge the combined architecture catalogs without force. Preserve previous versions and other plugins. Failed uploads leave a draft for inspection; catalog-only repair does not rebuild or replace assets.

Configure required reviewers on the GitHub **plugin-release** environment **before** enabling publication. The workflow declaration alone does not enforce a human reviewer unless repository environment settings do. Workflow permissions are read-only except the isolated publication job's release write permission. Ensure Actions and Docker-backed Ubuntu jobs are enabled in the destination repository.

Cloudflare testing is separate: manually run `Disposable Jellyfin integration` with `cloudflare_tunnel=true`. This exposes only a harmless disposable sentinel, needs no account/token, retains normal TLS validation and cleans labeled containers. Public tunnels and live Discord credentials are not required CI/release jobs. Human commands and multiplayer redemption cannot be certified by those automated jobs.

## Retained historical local evidence—not current certification

- Earlier Jellyfin 12.3 installed matrix: **32/32** (`artifacts/independent-final-combinations/results.json`).
- Earlier root tests: **781 .NET / 156 JavaScript**, plus offline release fixtures (`artifacts/independent-release-full-tests.log`).
- Independent Private SyncPlay **three-runtime** package set validated (`artifacts/independent-syncplay-release-check-final`). Historical all-plugin/three-platform packaging does not certify current source or gate unrelated releases; see the scoped [evidence register](verification.md).
- Historical local workflow lint and installed browser checks are not hosted-CI
  certification. Consult current status for the exact verified source/package.

Windows package generation is not installed Windows acceptance. ARM cross-packaging is not physical ARM certification. Human live invitation/multiplayer acceptance remains separate from automated checks and other [v1 release gates](v1-release-gates.md). Do not describe the full suite as v1-ready based on implementation/build/matrix results alone. No commit, tag, push or publication is authorized by preparing this pipeline.

## References

- [Latest-only maintenance policy](dependency-maintenance.md)
- [Workflow entry points and shared gate ownership](../.github/README.md)
- [Independent/suite release selector](independent-plugin-releases.md)
- [Testing](testing.md) · [Current evidence and blockers](review-status.md)
- [Evidence register and historical scope](verification.md)
