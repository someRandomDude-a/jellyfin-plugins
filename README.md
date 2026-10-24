# Jellyfin plugins

Main source repository: **someRandomDude-a/jellyfin-plugins**. Jellyfin 12.2 / .NET 10.

## Layout and status

- `shared/AccountHub.Api`: single v1 producer SDK used by all projects.
- `plugins/account-hub`: integration preview with the native workspace, embedded Security/Notifications and lifecycle/recovery services; not yet the complete planned control plane.
- `plugins/deletion-queue`, `plugins/private-syncplay`: independent domain plugins.
- `plugins/discord-bridge`: independently packaged single bot, Discord delivery, self-service/watch-party commands and an Embedded App SDK companion frontend. Hub owns verified links and recovery. The companion opens playback in Jellyfin outside Discord; in-Discord video playback is not implemented. See [Activity development and temporary HTTPS testing](docs/discord-activity-development.md).
- `modules/security`, `modules/notifications`: embedded Hub modules, never standalone installable plugins.
- `modules/user-management`: owner-authorized MIT lifecycle source compiled as `AccountHub.Lifecycle`; legacy controllers, plugin entry, bot and File Transformation hooks are excluded. Do not install the old plugin alongside Hub.

Consolidation does not certify the complete plan. See [review status](docs/review-status.md) for remaining blockers.

All four installable plugins support independent installation and arbitrary combinations. Missing optional providers disable only their dependent controls, with visible reasons and remediation. See the [feature dependency matrix](docs/dependencies.md).

## Development

Requires .NET SDK 10.0.402, Node 22+, PowerShell **7+**; Docker for live fixtures.

```powershell
npm ci --ignore-scripts --prefix plugins/private-syncplay
pwsh -File scripts/test.ps1
```

The root solution covers all seven .NET suites. The test script also runs JavaScript/UI suites and existing offline release-tool tests. Generated artifacts, databases and test credentials are ignored.

## Releases and Jellyfin repository

Root workflows:

- `tests.yml`: Windows/Linux tests on pushes and pull requests, locked Activity frontend build/tests, workflow lint and disposable installed-browser checks. No Discord credentials or public tunnels are used by default.
- `integration.yml`: reusable release gate plus scheduled/manual four-package/BaseUrl and SyncPlay fixtures; 32 plugin combinations, not full product certification.
- `release.yml`: `vX.Y.Z` tags or manual dispatch of an existing tag; tests first, then packages for Linux x64/ARM64 and Windows x64, exact ZIP checksums and catalogs, then a draft upload followed by publication. Existing releases are never overwritten. Configure required reviewers on the **plugin-release** environment before enabling production publishing.

The release workflow also requires the reusable installed/plugin-combination matrix at the verified tag and validates the merged three-platform artifact set before release writes. See [GitHub release checklist and remaining limits](docs/github-release-checklist.md). Ordinary source uploads/pushes run tests; they do not create a release.

After the first successful release, add this **Linux x64** repository URL in Jellyfin:

```text
https://github.com/someRandomDude-a/jellyfin-plugins/releases/latest/download/manifest.json
```

For other platforms use `manifest-linux-arm64.json` or `manifest-win-x64.json` at the same URL. Jellyfin does not auto-select runtime assets from a single catalog. Every catalog preserves previous published versions and links directly to immutable ZIPs in this repository. The checked-in `manifest.json` starts empty: it does not advertise packages that have not been published. The generated release catalog is authoritative; the workflow does not push generated files into `main`.

Local packaging (does not publish):

```powershell
pwsh -File scripts/package.ps1 -Tag v0.2.0 -Runtime linux-x64
```

Packages are **integration previews**. See the explicit remaining gates in [review status](docs/review-status.md); a green build is not full product certification. Source changes here have not been committed or pushed.

To test locally generated Linux x64 packages on a new disposable Jellyfin server:

```powershell
node scripts/integration.mjs --packages=artifacts/release/v0.2.0/linux-x64
```

Optional `--security=<path-to-pinned-jf12-zip>` verifies the Security package SHA-256 before installing it. Add `--proof` to self-enroll the disposable administrator and test real TOTP success/denial/replay without reading Security private records. No existing server data is mounted. Only `--keep` retains the random test container; otherwise the container is removed and its isolated evidence directory remains.

## Library additions

New episode/movie notifications can be enabled separately in **Account Hub → Library** and **Discord Bridge** settings. Inbox delivery respects current Jellyfin access; shared Discord channels require explicit library/channel selection and disclosure consent. Bridge works independently and remains the only Discord transport when Hub is installed. See [configuration, privacy and delivery limits](docs/library-notifications.md).
