# Independent plugins and optional features

## Dependencies and installation

Install the required plugins explicitly through the [combined platform catalog](../README.md#installation-and-upgrades),
restart Jellyfin and configure each provider in its own Dashboard settings. Jellyfin
does not automatically install all optional providers. Use matching server ABI and
architecture packages; update Notifications and its consumers together for extraction
upgrades. Back up databases and original keys first, and never install legacy User
Manager alongside Hub.

## Six-plugin overview and ownership

| Owner | Native and suite boundary |
|---|---|
| Accounts (Account Hub) | Native accounts, invitation approval/provisioning, managed access, audit and verified Discord links/recovery. Core administration does not require the bot. Discord delivery and delegated `/invite` require Bridge. Password-reset completion requires this browser's target-account session marked verified/unblocked by native Security, including its configured bypasses; no fresh code or token-only fallback. |
| Discord Bridge | Single bot and allowlisted transport. Notifications routing works without Hub; account commands require Hub, and party commands additionally require Private SyncPlay. No Discord role grants native administrator access or deletion consent. |
| Notifications | Standalone shared inbox, preferences, delivery and Push. No Hub requirement. Domain producers supply current access checks. Bridge is optional for Discord routes. Only one plugin owns the active inbox/bell/worker. |
| Deletion Queue | Consent, administrator review, verified activity/history, delay, durable execution claims and Seerr-managed removal. Notifications is required for new intake, not for access to existing consent/Undo/history. Hub, Bridge and Enhanced are optional. |
| Automatic Deletion Requests | Administrator rule configuration and bounded evaluation/submission. Deletion Queue and Notifications must be ready for submissions. Arr metadata is GET-only; rules cannot execute deletion or manufacture consent/approval. |
| Private SyncPlay | Native party/session authorization and scoped account/guest invitations. Core parties do not require Hub or Bridge. Optional central integration does not authorize membership. |

Dependency discovery is separate from runtime health: a loaded bot/provider is not necessarily connected, configured or ready. Missing/corrupt storage fails closed without replacing databases or keys. Current acceptance and exclusions are tracked in [review status](review-status.md); [account management](account-management.md) explains templates, bulk edits and Discord invitation delegation.

## Readiness and migration

Automatic Deletion Requests installs independently for rule configuration. Submissions require a ready Deletion Queue boundary, Notifications and an authorized native administrator. Seerr evidence uses Queue's protected connection. Collections and reversible artwork belong to Deletion Queue.

**Current removal-action capability:** new Deletion Queue intake also requires Notifications health feature `commands`, in addition to the standalone owner marker, `registered-types` and `scoped-access`. Update Notifications and Deletion Queue together for the new inline controls. Existing-request consent, Undo and history remain available through the queue during provider failure. Enhanced is optional; only the full Requests/Calendar integration and full-page shortcut depend on its enabled pages.

Deletion Queue now requires the standalone Notifications plugin to accept new removal requests and previews. Missing or unready Notifications blocks new intake with a repair message, not a local second inbox. Existing consent, Undo and history remain accessible for recovery. Install Notifications explicitly; no automatic Jellyfin dependency installation is claimed.

Required readiness includes the standalone owner marker and health capabilities `registered-types` and `scoped-access`; older providers require an update. Other producers register notification types/layouts/actions/recipient rules through the portable `AccountHub.Api` SDK without installing Hub. Administrator alerts resolve current native administrators; domain access callbacks remain mandatory where registered. Initial extraction requires coordinated consumer updates; arbitrary old/new package combinations are not certified.

Standalone Notifications owns the shared inbox, preferences, delivery settings
and worker without Hub. Hub's old routes are upgrade adapters; the standalone
owner suppresses its old bell/worker. Migration moves notification data and
re-protects secrets into its own database/key ring before retiring source tables.
See [upgrade instructions](../plugins/notifications/README.md). Security/Lifecycle
remain implementation modules; native Security and Seerr are external integrations.
Invitations remain integrated in Account Hub. There is no mandatory shared
connection registry or separate Invitations plugin.

The following feature requirements are not blanket certification; see [current evidence](review-status.md).

| Plugin / feature | Required beyond Jellyfin | When absent |
|---|---|---|
| Hub directory, managed access and audit | None | Operate independently; standalone Notifications owns shared inbox/settings |
| Domain administration | Corresponding domain plugin | Configure Queue, Bridge and SyncPlay in their own settings; missing-provider links/features remain unavailable |
| Hub Discord delivery | Discord Bridge + configured bot | Unavailable Discord settings are hidden; inbox/webhook features remain available |
| Hub native 2FA status/safe actions | Supported Jellyfin Security | Unavailable controls are hidden; core authorization remains enforced |
| Hub Seerr lifecycle integration | Configured Seerr service URL/API key | Dependent lifecycle operations are unavailable; domain administration stays in its owning plugin |
| Discord transport/configuration | Bot credentials/guild permissions to connect | Settings work standalone; no other suite plugin needed |
| New episode/movie inbox notices | Notifications; opt-in | Work without Hub/Bridge; personal preferences and current native access apply |
| New episode/movie Discord announcements | Notifications + Discord Bridge + configured bot; opt-in libraries/channels/disclosure | Work without Hub. Notifications alone observes; Bridge owns approved-channel delivery |
| Discord account commands | Hub-owned verified identity provider | Dependent settings are hidden; never infer native identity from Discord roles |
| Discord watch-party commands | Hub verified link + native Private SyncPlay provider | Dependent settings are hidden; native web party invitations need no Discord plugin |
| SyncPlay parties, player, web invitations | None; HTTPS for guest viewing | Native core independent of Hub/Discord |
| SyncPlay central audit / notifications | Hub for central audit; Notifications for shared notices | Core parties and local/native feedback remain independent |
| Deletion Queue new intake | Standalone Notifications with required capabilities | New intake blocked; existing consent/Undo/history remain accessible |
| Deletion Queue policy/evidence/history | None | Independent of Hub/Discord/Enhanced; no bypass of intake/execution gates |
| Seerr-managed deletion | Configured Seerr service plus all existing safety/consent gates | New automatic-deletion opt-in greyed, with manual configuration/import instructions; existing opt-in can still be turned off |
| Enhanced connection import | Enabled unambiguous Jellyfin Enhanced connection | Unavailable Queue import controls are hidden; direct configuration remains available |

Installed/enabled metadata is not a claim that an external service or bot is connected. Their runtime health remains separate. Disabling or removing a provider never grants permissions, weakens native 2FA, guesses identity links, removes safety gates or silently replaces stored credentials.

## Reuse without plugin coupling

`shared/Jellyfin.Security.Api` contains only the portable optional Security client, compatibility, proof/session-verdict policy and safe display models. It has no Hub controllers, workers, state or plugin identity. `shared/DiscordBridge.Api`, `shared/AccountHub.Api` and the native SyncPlay SDK use optional framework-only boundaries across plugin load contexts. Shared native authorization/dependency/CSS helpers are linked source, not implementation-plugin references. Hub bundles its implementation modules; standalone Notifications also bundles the notification backend and owns its active store/worker/bell. Hub's retained compatibility adapter must not create a competing owner.

`shared/Jellyfin.LibraryNotifications` provides the bounded native observer, intake and plain notice models. Notifications alone starts observation and owns private intake; Bridge uses an independent audience-free transport outbox. Hub starts no library observer. The shared assembly has no plugin entry or implementation-plugin dependency. See [configuration and privacy](library-notifications.md).

## Combination tests

`scripts/test-combinations.mjs` selects all 64 subsets of six plugins on new
isolated current-stable Jellyfin servers. `--security=latest` doubles this to 128
cases and automatically downloads the current compatible-major Security asset.
`--wiring-check` adds standalone-owner discovery and HTTP audit routing/replay/scope
checks. Startup, settings, task discovery, invalid inputs and dependency reporting
are also checked. Current validation limits are tracked in [status](review-status.md).
No subset matrix implies every feature/fault flow, live delivery or arbitrary
package-version skew. See [testing](testing.md) for the authoritative commands.

```powershell
node scripts/test-combinations.mjs --packages=artifacts/current-candidate --security=latest --wiring-check --jobs=2
```

## References

- [Plugin overview and installation](../README.md) · [Documentation index](README.md)
- [Notifications migration](../plugins/notifications/README.md)
- [Accounts](account-management.md) · [Discord](discord-suite-integration.md) · [Library notices](library-notifications.md)
- [Latest-only testing](testing.md) · [Dependency maintenance](dependency-maintenance.md)
- [Current validation](review-status.md)
