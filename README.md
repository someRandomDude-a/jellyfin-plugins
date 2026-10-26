# Jellyfin plugins

> **Architecture update:** Notifications now has its own installable plugin, storage, inbox API, settings and delivery worker. Existing notification data has a checkpointed one-time migration out of Account Hub. Account Hub → Account Invitations extraction and the shared connection registry remain unfinished. See [authoritative requirements](docs/product-requirements.md), [Notifications upgrade instructions](plugins/notifications/README.md) and the [owner compatibility profile](test-profiles/owner-server.json). Historical four-plugin evidence does not certify the expanded set.

## Install plugins

**Current build/test target: Jellyfin 12.3 (SDK 12.3.0).** Upstream stable-release discovery must agree with `Jellyfin.Target.props`; a newer stable version blocks CI until the SDK/server compatibility is updated. Packages are integration previews, not a completed v1 release. Repository URLs below become available after a release is published; if they return 404, no corresponding release catalog is available yet.

1. In Jellyfin, open **Dashboard → Plugins → Repositories** and add one repository named **Jellyfin plugins**.
2. Choose the combined catalog for your server architecture. Each lists all plugins published for that platform:

   | Server platform | Repository URL |
   |---|---|
   | Linux x64 | <https://raw.githubusercontent.com/someRandomDude-a/jellyfin-plugins/catalog/manifest-linux-x64.json> |
   | Linux ARM64 | <https://raw.githubusercontent.com/someRandomDude-a/jellyfin-plugins/catalog/manifest-linux-arm64.json> |
   | Windows x64 | <https://raw.githubusercontent.com/someRandomDude-a/jellyfin-plugins/catalog/manifest-win-x64.json> |

3. Open the plugin **Catalog**, select the plugins you want, and install them.
4. **Restart Jellyfin**, then open each installed plugin's settings in the Dashboard.

Choose the server platform, not the device running your browser. Jellyfin does not automatically select architecture-specific packages from a single catalog. Back up your Jellyfin configuration and plugin data before installing or upgrading previews. Never upload `.env`, bot tokens or other credentials to GitHub.

## Available plugins

**Account Hub scope:** onboarding, user/group administration, creation ownership, verified identities, Security integration and notifications. It is no longer the unified administration screen for the suite: manage Discord transport, Deletion Queue and Private SyncPlay in their own plugin settings. Existing backend integration contracts and stored configuration are preserved; the Seerr administration tab is also removed from Hub's navigation.

| Plugin | What it does | Optional integrations |
|---|---|---|
| **Account Hub** | Account workspace: onboarding invitations, group/policy management, verified account links, recovery and Seerr lifecycle integration through its HTTP API. Security remains an integration module. | Notifications for the shared inbox/delivery; Discord Bridge for Discord delivery/self-service; compatible native Security for supported factor workflows. |
| **Notifications** | Owns the shared inbox, read/dismiss state, preferences, server delivery settings, Web bell and worker. Migrates existing notification data into its own database and key ring. | Discord Bridge for delivery; Account Hub for verified DM identity links and its audit provider. Neither is needed for the standalone inbox. |
| **Discord Bridge** | Owns the bot and encrypted Discord credentials. Delivers scoped media announcements and provides self-service commands and explicitly targeted watch-party invitations. Includes an SDK companion Activity that opens Jellyfin externally—**not video playback inside Discord**. | Account Hub for verified identity/link/recovery commands; Account Hub + Private SyncPlay for watch-party creation commands. Basic transport works independently. |
| **Deletion Queue** | Consent-driven media deletion workflow with reviewable queue state and authenticated evidence. Configure it in its own plugin settings. | Account Hub backend lifecycle/notification integration; Discord Bridge for supported Discord delivery. |
| **Automatic Deletion Requests** | Disabled-by-default administrator rules submit ordinary removal requests. No consent, approval or deletion execution. | Deletion Queue and Notifications are required for new requests; no separate Seerr credentials. |
| **Private SyncPlay** | Private watch parties, native account invitations and isolated anonymous guest playback with owner-controlled permissions. Configure it in its own plugin settings. | Account Hub backend identity/lifecycle integration; Discord Bridge for Discord watch-party commands/delivery. |

Install the six plugins independently or combine them. For the initial Notifications extraction, update existing consumers together as described in its upgrade instructions. Missing optional integrations disable only dependent features. Do **not** install the legacy User Manager alongside Account Hub. See [dependencies](docs/dependencies.md) and [remaining release gates](docs/v1-release-gates.md).

Main source repository: **someRandomDude-a/jellyfin-plugins**. Jellyfin 12.3 / .NET 10. Owner compatibility target: Linux x86_64/amd64.

## Layout and status

- `shared/AccountHub.Api`: single v1 producer SDK used by all projects.
- `plugins/account-hub`: integration preview with the native workspace, embedded Security/Notifications and lifecycle/recovery services; not yet the complete planned control plane.
- `plugins/deletion-queue`, `plugins/private-syncplay`: independent domain plugins.
- `plugins/discord-bridge`: independently packaged single bot, Discord delivery, self-service/watch-party commands and an Embedded App SDK companion frontend. Hub owns verified links and recovery. The companion opens playback in Jellyfin outside Discord; in-Discord video playback is not implemented. See [Activity development and temporary HTTPS testing](docs/discord-activity-development.md).
- `modules/security`: shared Security integration; `modules/notifications`: shared notification backend library, not a second runtime plugin. `plugins/notifications` owns the standalone lifecycle.
- `modules/user-management`: owner-authorized MIT lifecycle source compiled as `AccountHub.Lifecycle`; legacy controllers, plugin entry, bot and File Transformation hooks are excluded. Do not install the old plugin alongside Hub.

Consolidation does not certify the complete plan. See [review status](docs/review-status.md) for remaining blockers.

All six installable plugins support independent installation and combinations. Missing optional providers disable only their dependent controls. See the [feature dependency matrix](docs/dependencies.md).

## Development

Requires .NET SDK 10.0.402, Node 22+, PowerShell **7+**; Docker for live fixtures.

```powershell
npm ci --ignore-scripts --prefix plugins/private-syncplay
pwsh -File scripts/test.ps1
```

The root solution covers the .NET suites, including Automatic Deletion Requests. The test script also runs JavaScript/UI suites and existing offline release-tool tests. Generated artifacts, databases and test credentials are ignored.

To test only one plugin and its common/dependency contracts, use `pwsh -File scripts/test-plugin.ps1 -Plugin private-syncplay` (also accepts `account-hub`, `discord-bridge`, `deletion-queue`, `notifications`, `automatic-deletion-requests`). Install `plugins/discord-bridge/activity` npm dependencies for Discord tests; Hub UI tests use the jsdom dependency in `plugins/private-syncplay`. See [independent release gates](docs/independent-plugin-releases.md).

## Releases and Jellyfin repository

Root workflows:

- `tests.yml` / `plugin-checks.yml`: independent Windows/Linux plugin tests plus their common/dependency checks and standalone installed tests, with fail-fast disabled. Installed all-plugin browser checks are separate interoperability evidence. No Discord credentials or public tunnels are used by default.
- `integration.yml`: scheduled/manual interoperability and 64-combination testing, not an unrelated-plugin release gate or full product certification.
- `release.yml`: existing **plugin-prefixed** tags such as `private-syncplay/v0.2.0`, automatically or by manual dispatch. Only that plugin's checks gate its three-platform packages and independent catalog feeds. Existing releases are never overwritten. Configure required reviewers on the **plugin-release** environment before enabling production publishing.

Each release validates only its selected plugin's merged three-platform artifact set before release writes; another optional plugin's failed tests or combination checks do not block it. Shared contracts and actual compile-time dependencies still apply. See [independent release gates](docs/independent-plugin-releases.md). Ordinary source uploads/pushes run tests; they do not create a release.

Installation URLs and steps are at the top of this README. Every release catalog preserves previous published versions and links directly to immutable ZIPs in this repository. The checked-in `manifest.json` starts empty: it does not advertise packages that have not been published. The generated release catalog is authoritative; the workflow does not push generated files into `main`.

Local packaging (does not publish):

```powershell
pwsh -File scripts/package.ps1 -Tag v0.2.0 -Plugin private-syncplay -ReleaseTag private-syncplay/v0.2.0 -Runtime linux-x64
```

Packages are **integration previews**. See the explicit remaining gates in [review status](docs/review-status.md); a green build is not full product certification. Source changes here have not been committed or pushed.

To test locally generated Linux x64 packages on a new disposable Jellyfin server:

```powershell
node scripts/integration.mjs --packages=artifacts/release/v0.2.0/linux-x64
```

Optional `--security=<path-to-pinned-jf12-zip>` verifies the Security package SHA-256 before installing it. Add `--proof` to self-enroll the disposable administrator and test real TOTP success/denial/replay without reading Security private records. No existing server data is mounted. Only `--keep` retains the random test container; otherwise the container is removed and its isolated evidence directory remains.

## Library additions

New episode/movie notifications can be enabled separately in **Account Hub → Library** and **Discord Bridge** settings. Inbox delivery respects current Jellyfin access; shared Discord channels require explicit library/channel selection and disclosure consent. Bridge works independently and remains the only Discord transport when Hub is installed. See [configuration, privacy and delivery limits](docs/library-notifications.md).

## Automatic deletion requests (in development)

[Automatic Deletion Requests](plugins/automatic-deletion-requests/README.md) adds a disabled-by-default Jellyfin/Seerr rule-evaluation preview. It submits ordinary requests; Deletion Queue owns all media operations, opt-in collections and reversible artwork overlays/editor. Sixth-plugin packaging and installed checks are now wired; see [current verification and limits](docs/review-status.md). Local packages do not imply publication, and older five-plugin evidence does not certify this preview.

Native **Scheduled Tasks → Deletion Queue** jobs run daily by default: rules/collections at 02:00, fresh safety checks and due deletions at 03:00, and artwork generation/restoration at 04:00. Edit their triggers or run them manually in Jellyfin. The redesigned editors provide native scope pickers, rule cards, match previews and portrait/landscape artwork controls. Current playback, consent, policy, target and originating-rule conditions are checked again before advancement and deletion; an elapsed date never grants authorization.

## Credits

The automatic-rule and planned collection/overlay features are inspired by [Maintainerr](https://github.com/Maintainerr/Maintainerr), copyright © 2026 Maintainerr, MIT-licensed. The focused implementation targets Jellyfin and Seerr rather than complete Maintainerr parity.
