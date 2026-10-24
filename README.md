# Jellyfin plugins

## Install plugins

**Requires Jellyfin 12.2.** Packages are integration previews, not a completed v1 release. Repository URLs below become available after a release is published; if they return 404, no corresponding release catalog is available yet.

1. In Jellyfin, open **Dashboard → Plugins → Repositories** and add a repository named **Jellyfin plugins**.
2. Choose the URL matching your **Jellyfin server's** operating system and architecture:

   | Server platform | Repository URL |
   |---|---|
   | Linux x64 | `https://github.com/someRandomDude-a/jellyfin-plugins/releases/latest/download/manifest.json` |
   | Linux ARM64 | `https://github.com/someRandomDude-a/jellyfin-plugins/releases/latest/download/manifest-linux-arm64.json` |
   | Windows x64 | `https://github.com/someRandomDude-a/jellyfin-plugins/releases/latest/download/manifest-win-x64.json` |

3. Open the plugin **Catalog**, select the plugins you want, and install them.
4. **Restart Jellyfin**, then open each installed plugin's settings in the Dashboard.

Choose the server platform, not the device running your browser. Jellyfin does not automatically select architecture-specific packages from a single catalog. Back up your Jellyfin configuration and plugin data before installing or upgrading previews. Never upload `.env`, bot tokens or other credentials to GitHub.

## Available plugins

| Plugin | What it does | Optional integrations |
|---|---|---|
| **Account Hub** | Unified account workspace: onboarding invitations, group/policy management, verified account links, recovery, notifications and Seerr lifecycle integration through its HTTP API. Security and Notifications are embedded modules, not separate plugins. | Discord Bridge for Discord delivery/self-service; Deletion Queue and Private SyncPlay for their domain controls; compatible native Security for supported factor workflows. |
| **Discord Bridge** | Owns the bot and encrypted Discord credentials. Delivers scoped media announcements and provides self-service commands and explicitly targeted watch-party invitations. Includes an SDK companion Activity that opens Jellyfin externally—**not video playback inside Discord**. | Account Hub for verified identity/link/recovery commands; Account Hub + Private SyncPlay for watch-party creation commands. Basic transport works independently. |
| **Deletion Queue** | Consent-driven media deletion workflow with reviewable queue state and authenticated evidence. | Account Hub for integrated administration; Discord Bridge for supported Discord delivery. |
| **Private SyncPlay** | Private watch parties, native account invitations and isolated anonymous guest playback with owner-controlled permissions. | Account Hub for integrated administration; Discord Bridge for Discord watch-party commands/delivery. |

Install any of the four independently or combine them. Missing optional integrations disable only dependent features and show repair instructions. Do **not** install the legacy User Manager alongside Account Hub. See [dependencies](docs/dependencies.md) and [remaining release gates](docs/v1-release-gates.md) before deployment.

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

Installation URLs and steps are at the top of this README. Every release catalog preserves previous published versions and links directly to immutable ZIPs in this repository. The checked-in `manifest.json` starts empty: it does not advertise packages that have not been published. The generated release catalog is authoritative; the workflow does not push generated files into `main`.

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
