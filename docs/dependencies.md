# Independent plugins and optional features

**Current removal-action capability:** new Deletion Queue intake also requires Notifications health feature `commands`, in addition to the standalone owner marker, `registered-types` and `scoped-access`. Update Notifications and Deletion Queue together for the new inline controls. Existing-request consent, Undo and history remain available through the queue during provider failure. Enhanced is optional; only the full Requests/Calendar integration and full-page shortcut depend on its enabled pages.

Deletion Queue now requires the standalone Notifications plugin to accept new removal requests and previews. Missing or unready Notifications blocks new intake with a repair message, not a local second inbox. Existing consent, Undo and history remain accessible for recovery. Install Notifications explicitly; no automatic Jellyfin dependency installation is claimed.

Required readiness includes the standalone owner marker and health capabilities `registered-types` and `scoped-access`; older providers require an update. Other producers register notification types/layouts/actions/recipient rules through the portable `AccountHub.Api` SDK without installing Hub. Administrator alerts resolve current native administrators; domain access callbacks remain mandatory where registered. Initial extraction requires coordinated consumer updates; arbitrary old/new package combinations are not certified.

> **Superseded target:** the owner selected a five-plugin architecture on 2026-10-25. [Product requirements](product-requirements.md) now govern the migration, standalone Notifications, shared settings and invitation-to-Discord dependency. The tables below describe the earlier implementation only, not the new target or proof of automatic dependency installation.

**Notifications** is now a fifth installable plugin alongside Account Hub, Discord Bridge, Deletion Queue and Private SyncPlay. It owns the shared inbox, preferences, server delivery settings and worker, without requiring Hub. Hub's old routes/backend registration remain compatibility adapters when upgrading; the standalone owner suppresses the old bell and delivery worker. Its one-time migration moves notification tables and re-protects their secrets into its own database/key ring, then retires the source tables. See [upgrade and migration](../plugins/notifications/README.md). Security/Lifecycle remain implementation modules. Jellyfin Security and Seerr are separate external integrations.

The table below is the required behavior, not blanket certification of the current candidate. The [release safety review](release-safety-review.md) found outstanding dependency-greyscale gaps in the new Seerr lifecycle panel and Discord invite delivery inputs. Existing dependency tests do not cover every new control.

| Plugin / feature | Required beyond Jellyfin | When absent |
|---|---|---|
| Hub directory, managed access, audit, inbox/settings | None | Operate independently |
| Hub domain controls | Corresponding domain plugin | Tab greyed; visible reason and install/enable/restart instructions |
| Hub Discord delivery | Discord Bridge + configured bot | Discord route options greyed; inbox/webhook features remain available |
| Hub native 2FA status/safe actions | Supported Jellyfin Security | Greyed with supported-version instructions; core features retain explicit audited authorization fallback |
| Hub Seerr connection/directory | Configured Seerr service URL/API key | Connection fields remain editable; dependent test/directory actions greyed |
| Discord transport/configuration | Bot credentials/guild permissions to connect | Settings work standalone; no other suite plugin needed |
| New episode/movie inbox notices | Account Hub only; opt-in | Work without Bridge; personal preferences and current native library access apply |
| New episode/movie Discord announcements | Discord Bridge + configured bot; opt-in libraries/channels/disclosure | Work without Hub; native libraries must be configured. Hub is the single capture owner when both are installed |
| Discord account commands | Hub-owned verified identity provider | Only account-command controls greyed; never infer account/admin identity from Discord roles |
| Discord watch-party commands | Hub verified link + native Private SyncPlay provider | Only party controls greyed with missing-provider instructions; native web party invitations need no Discord plugin |
| SyncPlay parties, player, web invitations | None; HTTPS for guest viewing | Native core independent of Hub/Discord |
| SyncPlay audit/central notifications | Account Hub | Central options greyed; local/native feedback remains independent |
| DeletionQueue policy/evidence/local notices | None | Native core independent of Hub/Discord/Enhanced |
| Seerr-managed deletion | Configured Seerr service plus all existing safety/consent gates | New automatic-deletion opt-in greyed, with manual configuration/import instructions; existing opt-in can still be turned off |
| Enhanced connection import | Enabled unambiguous Jellyfin Enhanced connection | Import greyed; direct Seerr configuration remains available |

Installed/enabled metadata is not a claim that an external service or bot is connected. Their runtime health remains separate. Disabling or removing a provider never grants permissions, weakens native 2FA, guesses identity links, removes safety gates or silently replaces stored credentials.

## Reuse without plugin coupling

`shared/Jellyfin.Security.Api` contains only the portable optional Security client, compatibility, proof policy and safe display models. It has no Hub controllers, workers, state or plugin identity. `shared/DiscordBridge.Api`, `shared/AccountHub.Api` and the native SyncPlay SDK use optional framework-only boundaries across plugin load contexts. Shared native authorization/dependency/CSS helpers are linked source, not implementation-plugin references. Only Hub packages its embedded module implementations.

`shared/Jellyfin.LibraryNotifications` provides the bounded native library observer, durable intake and plain notice models. Each host owns its own instance/database; Hub→Bridge messages contain no Jellyfin audience identities. The assembly has no plugin entry or dependency on an implementation plugin. See [library notification configuration and privacy](library-notifications.md).

## Combination tests

`scripts/test-combinations.mjs` exercises all 32 subsets of the five currently packaged plugins, including the no-plugin baseline, on new isolated Jellyfin servers. Supplying `--security=<pinned-jf12-zip>` runs all 32 again with native Security (64 cases). Cases verify actual installed startup, standalone settings writes, rejected invalid confirmation/origin/JSON, and per-feature dependency reports. The interface matrix passed 64/64 on an earlier coordinated package set; see [current evidence and its limits](review-status.md) for newer focused checks. Live Discord messaging still needs disposable bot/guild credentials. These checks do not certify unfinished onboarding, group intent, Seerr job features or arbitrary independent version skew.

```powershell
node scripts/test-combinations.mjs --packages=artifacts/release/v0.2.0/linux-x64 --jobs=2
```
