# Independent plugins and optional features

Only **Account Hub**, **Discord Bridge**, **Deletion Queue**, and **Private SyncPlay** are installable suite plugins. Security/Notifications/Lifecycle remain embedded Hub modules under the previously selected architecture. Jellyfin Security and Seerr are independent external integrations, not required suite plugins.

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

`scripts/test-combinations.mjs` exercises all 16 subsets of the four plugins, including the no-plugin baseline, on new isolated Jellyfin servers. Supplying `--security=<pinned-jf12-zip>` runs all 16 again with native Security (32 cases). Cases verify actual installed startup, standalone settings writes, rejected invalid confirmation/origin/JSON, and per-feature dependency reports. Live Discord messaging still needs disposable bot/guild credentials. These checks do not certify unfinished onboarding, group intent or Seerr job features.

```powershell
node scripts/test-combinations.mjs --packages=artifacts/release/v0.2.0/linux-x64 --jobs=2
```
