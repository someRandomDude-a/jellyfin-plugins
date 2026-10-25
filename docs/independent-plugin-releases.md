# Independent plugin gates

## Current operator flow (supersedes older publication details below)

### Release everything together

Run **Actions → Release all plugins → Run workflow**, select the branch and enter a new `vX.Y.Z`. This creates an immutable suite tag at the selected branch's dispatch commit, runs the complete reusable checks (including installed-browser checks), builds all five plugins for Linux x64, Linux ARM64 and Windows x64, validates the 15 ZIPs and three manifests, and publishes one GitHub release. Publication uses the existing `plugin-release` environment and its configured approval rules.

One final **Update plugin manifests** job merges the release into the three existing combined architecture catalogs. Existing catalog history and independent releases are preserved; no new installation URL is needed. Individual plugin release actions still work as before. A previously published suite version skips checks/build/publication and only repairs manifests; immutable tags/assets are never overwritten. Partial draft uploads fail closed and require operator review rather than automatic replacement. Older four-plugin suite releases cannot pass the current five-plugin validator.

### Release a single plugin

- Run the dedicated **Release <plugin>** action on the desired branch and enter `vX.Y.Z`.
- A new version resolves the latest relevant commit, creates its immutable tag, runs selected/common checks, builds three packages and publishes after approval.
- An existing tag is reused only when its commit belongs to that branch's history; it is never moved. If the release is already published, checks/build/publication are skipped and only manifest repair runs.
- **Update plugin manifests** is the same final reusable job and can also be run manually with either `vX.Y.Z` or `<plugin>/vX.Y.Z`. It requires an actual published release and validates downloaded assets before updating the three combined architecture feeds atomically.
- Only the combined `catalog/manifest-<runtime>.json` feeds are now written. Old per-plugin branches are preserved for bootstrap/history, but no longer updated; migrate Jellyfin repositories to the combined links in README. No duplicate compatibility-feed publication or second release path is required.

The five installable plugins have independent release gates, including **Release Notifications**. Notifications runs its own storage/migration/routing tests, the shared notification backend/frontend suites and common release contracts. Account Invitations extraction remains unfinished. The notification backend under `modules/notifications` is shared implementation, not another installable plugin.

The latest local Notifications and Deletion Queue selected gates pass after adding shared admin/frontend coverage and installing the common jsdom harness for all selections. Deletion Queue also tests the notification backend contract it depends on. New intake requires standalone Notifications' registered/scoped interface; preserve existing-request recovery when it is unavailable. Initial interface extraction needs coordinated consumer upgrades: legacy ABI signatures are retained, but arbitrary mixed-version package sets have not been certified. See [review status](review-status.md) for exact package/evidence scope and remaining release blockers. No remote workflow execution or publication is implied by local tests.

## Gate ownership

`plugin-checks.yml` runs `scripts/test-plugin.ps1 -Plugin <id>` on Windows/Linux and packages/installs only that plugin on a disposable Linux amd64 Jellyfin, with and without the pinned native Security plugin. Private SyncPlay additionally runs its native-socket, guest media and SDK consumer integration. Common target checks, release validation, distribution inventory and catalog publisher fixtures run for every consumer. Security and notification module suites run for Hub/Discord, which compile those shared components; lifecycle suites run with Hub.

No selected-plugin job invokes the aggregate solution/test script or full combination matrix. Actual compile-time dependencies and shared-contract failures still block affected consumers; optional providers' independent tests do not. `tests.yml` runs the independent checks concurrently without fail-fast. Its all-plugin browser job and `integration.yml` combination matrix remain visible interoperability evidence, not dependencies of `release.yml`.

Private SyncPlay's developer SDK keeps its independent 1.1.0.0 assembly identity even when plugin/package version properties are passed globally. SDK archive checks reject an accidentally rewritten identity, and a separately built consumer is exercised against a catalog-installed plugin.

## Tags and publication

Manifest publication is centralized in **Update plugin manifests** (`update-manifest.yml`). Every plugin release calls it as the final job, after publication succeeds. It downloads and validates the published immutable assets before merging architecture catalogs and legacy compatibility feeds. Run it manually with a plugin and existing published tag to retry catalog updates without rebuilding, creating tags or republishing packages. Replaying an older release preserves newer catalog history.

**Combined installation feeds:** users add only `catalog/manifest-linux-x64.json`, `catalog/manifest-linux-arm64.json` or `catalog/manifest-win-x64.json` from this repository's raw GitHub URL. Independent releases also merge their entry into these shared feeds. All three files update in one non-forced commit; concurrent publications re-read/re-merge the latest parent (up to four attempts). Other plugins and existing versions are preserved; conflicting immutable version metadata is rejected. Existing per-plugin feeds remain updated for compatibility, but are no longer required for installation. These combined URLs become available after approved publication; standalone Notifications is not listed until implemented and published.

Use the dedicated Actions task **Release Private SyncPlay**, **Release Account Hub**, **Release Discord Bridge** or **Release Deletion Queue**. Select its branch in **Run workflow** and enter only the version tag. These tasks call the shared release engine; the former plugin dropdown is no longer needed. The automatic relevant-commit/tag behavior described below is unchanged.

Manual dispatch accepts a plugin dropdown and a version such as `v0.2.0`. On the selected workflow branch, it finds the latest commit touching that plugin, its compile-time dependency paths, or common scripts/workflows/build targets, SDK settings and license. It creates and pushes `<plugin>/v0.2.0` at that commit automatically, then tests/packages that exact commit. Existing tags are reused only at the same resolved commit and are never moved. The selected commit must contain independent release tooling. Tag creation occurs before tests; failed verification may leave the immutable tag but does not publish packages. Dispatch therefore requires repository tag-write permission, while publication still requires the protected environment approval.

Release tags are `<plugin>/vX.Y.Z`, where `<plugin>` is `account-hub`, `discord-bridge`, `private-syncplay` or `deletion-queue`. Each release checks out that immutable tag, runs selected/common checks, packages only that plugin for Linux x64/ARM64 and Windows x64, then validates all three selected packages before any release write. Version numbers may advance independently. Unprefixed historical `vX.Y.Z` tags no longer trigger new aggregate publication.

The `plugin-release` protected environment remains required. An approved workflow creates a draft, uploads validated immutable assets, publishes without replacing GitHub's global latest release, then updates only `catalog-<plugin>/manifest-<runtime>.json`. Each feed contains that plugin's history and links to immutable plugin-prefixed release URLs. A failure never overwrites another plugin's assets/feed. Catalog updates retain their parent tree and are non-forced; concurrent changes fail rather than discarding history. The catalog publication job is serialized per plugin.

History is selected from the highest published version of that plugin (all release pages are read), with the former aggregate release as first-migration fallback. A failed history download blocks publication. Old aggregate feeds and legacy standalone repositories are not automatically retired. New independent feed URLs will return 404 until their first approved publication. No tags, pushes, releases or feed updates were performed while implementing this change.

`scripts/test.ps1` and `scripts/package.ps1` without `-Plugin` remain available for full local verification/fixtures; they are no longer an unrelated-plugin release dependency. Cross-plugin compatibility or human/browser acceptance limitations must still be disclosed in release notes even when that independent release gate is green.

Local three-platform selected-package validation (no publication): `pwsh -File scripts/test-plugin-package.ps1 -Plugin private-syncplay`. This checks that no unrelated plugin package is emitted and validates the merged selected-plugin release set, including URL-encoded tag paths, checksums and distribution inventories.
