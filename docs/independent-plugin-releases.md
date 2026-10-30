# Independent and suite releases

## Prerequisites and setup

Use a reviewed immutable source commit, an unused `vX.Y.Z` version, current
dependencies and the [latest-only acceptance policy](dependency-maintenance.md).
Enable Actions/Docker jobs and configure required reviewers on **plugin-release**;
a workflow declaration alone does not create human approval requirements.

Dependency freshness and final acceptance remain mandatory. See
[validation status](review-status.md) before attempting release.

## Release all six plugins

Run **Actions → Release plugins → Run workflow** on the intended branch, select
`plugin=all` and enter `vX.Y.Z`. The shared latest-only gate runs before the
engine creates/reuses an immutable suite tag. It runs Windows/Linux root tests and
installed/browser/matrix/native Security/external acceptance, then builds 18 packages across `linux-x64`,
`linux-arm64` and `win-x64`. Validation covers all six identities, inventories,
encoded immutable URLs and exact checksums before protected publication.

Dependency maintenance uses this same engine with an exact tested merge SHA and
itemized release notes. A failed test/freshness check cannot fall back to older
software. Tag creation precedes acceptance, so a failed run may leave an immutable
tag, not a published release. Existing tags are never moved.

## Release one plugin

Use the same **Release plugins** workflow and choose the plugin, branch and
`vX.Y.Z`. There are no separate per-plugin workflow files.

The shared engine uses the exact selected source commit (optional `ref` and
`expected_commit` inputs), not an older relevant ancestor. It creates
`<plugin>/vX.Y.Z`, tests that exact source and builds only its three runtime packages.
An existing tag is reused only when it matches that selected commit exactly; it is never
moved to a newly resolved commit. Direct plugin-prefixed tag pushes also use this
engine. Ordinary source pushes/PRs do not publish releases.

## Gate ownership

- Every selected plugin runs `scripts/test.ps1 -Plugin <id>` on Windows/Linux,
  common contracts, package/inventory validation and disposable latest Linux amd64
  installation with/without this run's checksum-verified native Security package.
- Notifications runs its storage/migration/routing and shared backend/frontend
  tests. Queue also runs the notification backend it depends on. Hub/Discord run
  their shared Security/notification suites; lifecycle suites run with Hub.
- Private SyncPlay additionally checks native socket/playback/guest isolation and a
  separately compiled SDK consumer installed through a disposable catalog. Its SDK
  retains the independent 1.1.0.0 identity, not the plugin release version.
- Hub/Queue/automatic-rules releases require the relevant real external-provider
  fixtures in external-only mode. Unrelated plugins do not acquire a full-suite
  matrix dependency. Suite releases require all 64 subsets, 128 with/without native
  Security, plus full root/browser/external gates.
- Tests/packages share one current-run target/toolchain/source/image snapshot;
  publication rechecks freshness and rejects intervening upstream changes. Required
  environment approvals remain in place.

Initial Notifications extraction requires coordinated consumer upgrades; stable ABI
signatures do not certify arbitrary mixed DLLs. Preserve existing-request consent,
Undo and history when providers fail. Invitations remain integrated in Account Hub;
each plugin retains its own connection settings.

## Immutable publication and catalog repair

After protected approval, the engine creates a draft, uploads validated assets and
publishes with GitHub's **Latest** marker without replacing existing versions/assets. Partial draft uploads fail
closed for operator review. History retrieval failures block publication rather
than discarding older entries. Cross-packaging is not native Windows/physical ARM
acceptance, and automated fixtures do not authorize human/live destructive tests.

**Update plugin manifests** downloads and validates an already published release,
then merges the three combined `catalog/manifest-<runtime>.json` feeds. Suite
mode requires six matching identities across platforms; selected mode requires one.
Existing versions and other plugins are preserved. The three feeds and derived
`releases.json` README metadata update in one
non-forced commit; races re-read/re-merge the latest parent up to four attempts.
Conflicting immutable metadata is rejected. README badges use the newest published
entry for each plugin rather than applying one suite version to every plugin.
An existing catalog can gain the summary through an authorized catalog-only repair;
no source-branch README commit is needed. Badge caches may delay display briefly.

If publication succeeded but the catalog update failed, run **Update plugin manifests**
with the plugin (or `all`) and existing published tag. Independent repair accepts
`vX.Y.Z` or `<plugin>/vX.Y.Z`; suite repair uses `vX.Y.Z`. Rerunning a release action
for an already published version also performs catalog-only repair. It does not
rebuild, retest or replace published bytes; it is not new software certification.
Never delete/recreate a release or move a tag to fix a catalog.

Use only the [combined architecture installation feeds](../README.md#installation-and-upgrades).
Legacy per-plugin branches remain bootstrap/history inputs, not additional updated
compatibility feeds. Missing URLs/versions are not yet published. Local packaging
never changes the public catalog or installed servers.

## Local verification

Root `scripts/test.ps1` and `scripts/package.ps1` remain full-suite wrappers; pass
`-Plugin` for selected scope. Three-platform selected-package validation without
publication: `pwsh -File scripts/test-plugin-package.ps1 -Plugin private-syncplay`.
It checks package scope, encoded URLs, checksums and inventories, not every installed
platform or live feature. Do not repeat historical passes as current acceptance.

## References

- [Release checklist](github-release-checklist.md) · [Latest-only maintenance](dependency-maintenance.md)
- [Testing](testing.md) · [Dependencies](dependencies.md)
- [Current validation](review-status.md)
