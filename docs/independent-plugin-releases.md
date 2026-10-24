# Independent plugin gates

The current four installable plugins have independent release gates. The planned standalone Invitations/Notifications extraction remains unfinished; embedded modules are tested with their actual consumers, not advertised as independently releasable plugins.

## Gate ownership

`plugin-checks.yml` runs `scripts/test-plugin.ps1 -Plugin <id>` on Windows/Linux and packages/installs only that plugin on a disposable Linux amd64 Jellyfin, with and without the pinned native Security plugin. Private SyncPlay additionally runs its native-socket, guest media and SDK consumer integration. Common target checks, release validation, distribution inventory and catalog publisher fixtures run for every consumer. Security and notification module suites run for Hub/Discord, which compile those shared components; lifecycle suites run with Hub.

No selected-plugin job invokes the aggregate solution/test script or full combination matrix. Actual compile-time dependencies and shared-contract failures still block affected consumers; optional providers' independent tests do not. `tests.yml` runs the independent checks concurrently without fail-fast. Its all-plugin browser job and `integration.yml` combination matrix remain visible interoperability evidence, not dependencies of `release.yml`.

Private SyncPlay's developer SDK keeps its independent 1.1.0.0 assembly identity even when plugin/package version properties are passed globally. SDK archive checks reject an accidentally rewritten identity, and a separately built consumer is exercised against a catalog-installed plugin.

## Tags and publication

Release tags are `<plugin>/vX.Y.Z`, where `<plugin>` is `account-hub`, `discord-bridge`, `private-syncplay` or `deletion-queue`. Each release checks out that immutable tag, runs selected/common checks, packages only that plugin for Linux x64/ARM64 and Windows x64, then validates all three selected packages before any release write. Version numbers may advance independently. Unprefixed historical `vX.Y.Z` tags no longer trigger new aggregate publication.

The `plugin-release` protected environment remains required. An approved workflow creates a draft, uploads validated immutable assets, publishes without replacing GitHub's global latest release, then updates only `catalog-<plugin>/manifest-<runtime>.json`. Each feed contains that plugin's history and links to immutable plugin-prefixed release URLs. A failure never overwrites another plugin's assets/feed. Catalog updates retain their parent tree and are non-forced; concurrent changes fail rather than discarding history. The catalog publication job is serialized per plugin.

History is selected from the highest published version of that plugin (all release pages are read), with the former aggregate release as first-migration fallback. A failed history download blocks publication. Old aggregate feeds and legacy standalone repositories are not automatically retired. New independent feed URLs will return 404 until their first approved publication. No tags, pushes, releases or feed updates were performed while implementing this change.

`scripts/test.ps1` and `scripts/package.ps1` without `-Plugin` remain available for full local verification/fixtures; they are no longer an unrelated-plugin release dependency. Cross-plugin compatibility or human/browser acceptance limitations must still be disclosed in release notes even when that independent release gate is green.

Local three-platform selected-package validation (no publication): `pwsh -File scripts/test-plugin-package.ps1 -Plugin private-syncplay`. This checks that no unrelated plugin package is emitted and validates the merged selected-plugin release set, including URL-encoded tag paths, checksums and distribution inventories.
