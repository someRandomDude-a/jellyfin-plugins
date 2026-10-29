# Jellyfin plugins

Six independently installable plugins for account management, notifications,
Discord integration, consent-driven media removal and private watch parties.

> **Integration previews—not a completed v1 release.** Some features and live-service
> acceptance remain unfinished. Catalog entries exist only after approved publication.

## Dependencies

Use packages matching your **Jellyfin server version and server architecture**.
Build/test workflows resolve the latest stable relevant software; incompatible or
stale targets block release rather than falling back to an older server. This is
not a promise of compatibility with every installed plugin or skin.

| Plugin | Required for its main features | Optional integrations |
|---|---|---|
| **Account Hub** | Notifications for the shared notification center; compatible native Jellyfin Security for supported factor/recovery workflows | Discord Bridge for Discord delivery and self-service; Seerr for account lifecycle integration |
| **Notifications** | Jellyfin; no other suite plugin is needed for the standalone inbox | Discord Bridge for Discord delivery; Account Hub for verified personal-DM identity links and central audit |
| **Discord Bridge** | A Discord application/bot for Discord features | Account Hub for verified identity/recovery commands; Hub + Private SyncPlay for watch-party creation commands |
| **Deletion Queue** | Standalone Notifications for **new** requests; Seerr for supported destructive media operations | Account Hub audit, Discord delivery and Enhanced views; existing consent, Undo and history remain available if Notifications is unavailable |
| **Automatic Deletion Requests** | Deletion Queue and Notifications for new removal requests | Seerr/native Jellyfin metadata and GET-only Sonarr/Radarr evidence for supported rules; no separate Seerr credentials |
| **Private SyncPlay** | Jellyfin; core parties do not require another suite plugin | Notifications for shared notices; Account Hub for identity/audit; Discord Bridge for Discord commands and delivery |

Install only the plugins you need. Missing optional integrations disable their
dependent features, not unrelated functions. **Do not install the legacy User
Manager alongside Account Hub.** Invitations remain integrated in Account Hub.
Each plugin owns its connection settings; a shared registry is not required.

## Installation and upgrades

1. Back up Jellyfin's configuration and plugin data before installing or upgrading previews.
2. Open **Dashboard → Plugins → Repositories** and add a repository named **Jellyfin plugins**.
3. Select the combined catalog for your **server**, not your browser/client device:

   | Server platform | Repository URL |
   |---|---|
   | Linux x64 | <https://raw.githubusercontent.com/someRandomDude-a/jellyfin-plugins/catalog/manifest-linux-x64.json> |
   | Linux ARM64 | <https://raw.githubusercontent.com/someRandomDude-a/jellyfin-plugins/catalog/manifest-linux-arm64.json> |
   | Windows x64 | <https://raw.githubusercontent.com/someRandomDude-a/jellyfin-plugins/catalog/manifest-win-x64.json> |

4. Open the plugin **Catalog** and install your chosen plugins and required dependencies.
5. **Restart Jellyfin**, then configure each plugin through its Dashboard settings.
6. Enable optional integrations only after configuring their providers and permissions.

A catalog returning **404** has not been published yet; local source or packages
do not imply availability. Jellyfin does not automatically choose architecture-specific
packages from one universal catalog. Published catalogs preserve earlier versions
and reference immutable release ZIPs.

**Upgrading from embedded Hub notifications:** install standalone Notifications
and update its consumers together. Its checkpointed migration moves existing
notification data out of Hub; follow the Notifications upgrade guide before
changing an existing installation. Arbitrary mixed-version combinations are not
certified. Keep credentials private—never upload `.env`, bot tokens or keys.

## What the plugins offer

### Account Hub

- Account onboarding and invitations, user/group administration and policy management.
- Verified account links, supported native Security integration and session-bound recovery.
- Seerr account lifecycle integration through its HTTP API and a central audit provider.
- Opt-in future library-addition notifications through Notifications.

Hub is an **account workspace**, not the suite's universal administration screen.
Configure Discord transport, Deletion Queue and Private SyncPlay in their own settings.
Native Security remains a separate upstream integration, not a replacement authentication system.

### Notifications

- A shared notification bell/inbox with read, dismiss, History and Blocked views.
- Personal Android-style app/category switches and **Important, Alerting, Silent and Low** choices.
- Separate **Watchlisted** and **Favorited** filters for future accessible library additions.
- Browser Push enrollment and optional Discord delivery, with server routing controlled by administrators.
- Mobile swipes and desktop dragging: ordinary notices reveal read/hide actions;
  eligible deletion-consent requests accept a deliberate right gesture to consent,
  then dismiss only after success.

The UI follows Jellyfin theme colors and fonts and supports skin overrides.
Critical alerts and current authorization remain protected; physical-device and
every-third-party-skin compatibility are not universally certified. Watchlisted
uses native `Likes` (also used by Enhanced); Favorited uses independent `IsFavorite`.
Notifications do not change either list or viewing history.

### Discord Bridge

- One bot/transport with encrypted Discord credentials and scoped library announcements.
- Optional verified account linking, self-service/recovery commands and targeted watch-party invitations.
- `/invite` signup uses/expiry with administrator limits, and `/watchparty [user]` native links with approved-channel or DM delivery.

Discord Activities and embedded video are removed. Shared library announcements
require Notifications, explicit library/channel selection and disclosure consent.
Basic Bridge transport works independently of Hub.

### Deletion Queue

- Reviewable removal requests with recipient consent, administrator approval where required,
  safety delays, Undo and preserved history.
- Fresh access, playback, policy and target checks before advancement or deletion.
- Optional collections and reversible portrait/landscape artwork overlays with native editors.
- Native scheduled tasks for maintenance, due-deletion checks and artwork generation/restoration.

Deletion Queue alone owns destructive Seerr operations. A due date, notification
gesture or automated rule never bypasses its consent, approval or safety checks.

### Automatic Deletion Requests

- Disabled-by-default administrator rules using Jellyfin/Seerr metadata and supported Arr evidence.
- Native scope pickers, rule cards and match previews.
- Submission of ordinary removal requests to Deletion Queue.

It **does not grant consent, approve requests or execute deletion**. Arr metadata
reads remain GET-only. This is a focused rule-evaluation preview, not complete
Maintainerr feature parity.

### Private SyncPlay

- Private watch parties and invitations for native Jellyfin accounts.
- Isolated anonymous guest playback with owner-controlled permissions.
- Optional shared notifications, central identity/audit and Discord invitations.
- A separately versioned developer SDK and consumer example.

Core parties work without Hub or Bridge. Human multiplayer hosting and
the complete owner plugin/skin combination still have acceptance limits.

## Development and release policy

Use the SDK declared in `global.json`, current stable Node.js and PowerShell **7+**;
Docker is required for disposable installed/browser/external fixtures.

```powershell
npm ci --ignore-scripts --prefix plugins/private-syncplay
pwsh -File scripts/test.ps1
# Or test one plugin and its shared contracts:
pwsh -File scripts/test-plugin.ps1 -Plugin notifications
```

Normal test/package entry points discover current stable Jellyfin/Security and
generate matching SDK/ABI/database settings. CI shares a concrete target/toolchain
snapshot across jobs and fails if evidence is missing or incompatible.

Dependency maintenance discovers updates hourly, groups them into a tested PR and
can dispatch a patch release after protected acceptance. Every release path
requires latest-only freshness checks; suite releases also require full
root/browser/matrix/external acceptance. Publication rechecks upstream freshness.
**The current local freshness audit reports outdated dependencies: adding these
gates does not mean all upgrades or remote workflows have passed.** Existing branch
rules and `plugin-release` approvals remain mandatory. Ordinary pushes do not publish releases.

## References

### Installation, configuration and feature guides

- [Documentation index](docs/README.md)
- [Dependency and feature matrix](docs/dependencies.md)
- [Notifications installation, upgrades and migration](plugins/notifications/README.md)
- [Notification categories, importance and consent gestures](docs/notification-channels.md)
- [Library additions, privacy and delivery](docs/library-notifications.md)
- [Browser Push](docs/web-push.md)
- [Account management](docs/account-management.md) · [Security and recovery](docs/security-recovery-integration.md)
- [Discord integration](docs/discord-suite-integration.md)
- [Automatic Deletion Requests](plugins/automatic-deletion-requests/README.md)

### Verification, development and releases

- [Current review status and blockers](docs/review-status.md) · [Evidence register](docs/verification.md)
- [Safety and permissions](docs/safety-and-permissions.md) · [Notification producer API](docs/notification-producer-api.md)
- [Testing commands](docs/testing.md) · [Theme/browser acceptance](docs/theme-acceptance.md)
- [Independent and suite releases](docs/independent-plugin-releases.md) · [Dependency maintenance and latest-only gates](docs/dependency-maintenance.md)
- [Product requirements](docs/product-requirements.md) · [Remaining release gates](docs/v1-release-gates.md)
- [Owner compatibility profile](test-profiles/owner-server.json) — an observed profile, not blanket certification

### Upstream projects and credits

- [Jellyfin](https://github.com/jellyfin/jellyfin)
- [Jellyfin Security](https://github.com/ZL154/JellyfinSecurity)
- [Jellyfin Enhanced](https://github.com/n00bcodr/Jellyfin-Enhanced)
- [Seerr](https://github.com/seerr-team/seerr) · [Sonarr](https://github.com/Sonarr/Sonarr) · [Radarr](https://github.com/Radarr/Radarr)
- [Discord Embedded App SDK](https://github.com/discord/embedded-app-sdk)
- [Maintainerr](https://github.com/Maintainerr/Maintainerr) inspired the automatic-rule
  and collection/overlay work (copyright © 2026 Maintainerr, MIT). This implementation
  targets Jellyfin and Seerr rather than complete Maintainerr parity.
- [Repository license](LICENSE)
