# Private SyncPlay Account Hub adapter v1

Implementation baseline: `3db22d9c63e4a57d54031f187c8ac832ede2f751` (Jellyfin 12.2 / .NET 10).
The user authorized the **plan-defined v1** contract as the implementation baseline. `src/AccountHub.Api` is a producer-side implementation of that contract, not an imported/published canonical Hub SDK. Its version is 1.0.0; canonical SDK/host acceptance remains a handoff requirement. The existing `Jellyfin.Plugin.PrivateSyncPlay.Api.1.1` party SDK, identity, signature and API 1.1.0 are unchanged.

## Boundary and configuration

- Hub discovery is lazy keyed DI: `AccountHub.Api.1`, exactly `Func<string,CancellationToken,Task<string>>`. No Hub implementation assembly or standing HTTP token is used.
- `hello` verifies v1 and required operations; `health.ready` gates delivery. Missing, disabled, incompatible, malformed, throwing or timed-out hosts do not authorize or stop parties. Non-cooperative timed-out calls cannot accumulate new pending calls in the client.
- Defaults: `notificationMode=Auto`, `hubIntegrationEnabled=true`, `publishAudit=true`, `nativeNotifications=true`.
- Auto: unavailable notification delivery falls back locally; audit keeps its independent retry state. Local: optional native notifications and independent audit, no Hub notifications. Hub: bounded retry (five attempts or freshness deadline). Disabled: no optional notifications, independent audit remains controlled by `publishAudit`.
- `nativeNotifications` controls optional message notices, not mandatory native SyncPlay guest-joined feedback. The guest-joined toast is never sent a second time by the optional fallback. Accepted Hub notices do not also take the optional native path. There is **no second bell**; the Hub owns its bell, and local party UI remains available.
- Summaries use constant text, party/user IDs, stable `Guest N` labels and reason categories. They exclude invitation secrets, guest cookies, session/device IDs, names, email addresses, IPs, media IDs/paths/URLs and account tokens. Config audit uses an explicit policy projection; guest layout is reported as changed, not copied.
- Playback-start summaries come from a real signed-in member's playback-start event, once per party. Invite creation/revocation, guest admission/departure/revocation, party closure and moderation have separate transition IDs. Lease refresh/preflight emits no admission event.

## Persistence and rollback

The only new database is `<Jellyfin DataPath>/private-syncplay/integration-v1.db`. The isolated `PrivateSyncPlay.IntegrationStore.dll` uses the server's SQLite provider; it never opens `jellyfin.db`. Package staging does not bundle SQLite, Jellyfin, EF or MediaBrowser binaries.

Schema v2 is forward-only, with an atomic v1 -> v2 migration adding sanitized per-row error codes. WAL/FULL synchronization is enabled. Notification/audit acknowledgments are separate rows keyed by stable event ID + kind. A 30-second claim lease recovers abandoned work. Batches contain at most eight rows; delivery has a two-second call deadline and exponential backoff capped at one hour. Bounds: 10,000 rows, 32 MiB payload total, 60,000 UTF-8 bytes per event. Terminal rows are pruned 30 days after termination. Notification freshness is **120 seconds**; stale notices are not replayed. Audit retries for 30 days. Rejected audits dead-letter; fallback is reported in status and rate-limited server warnings. A failed outbox write never rolls back a party operation; such events cannot be recovered later.

Parties, bindings, invitations and guest credentials remain **in memory only**. Restart expires them as before. There is no event backfill and no party resurrection from the outbox.

Configuration remains the native plugin XML, including existing `Users` overrides. New optional revision/command-receipt metadata shares the same native configuration save as the settings. Successful command replies survive configuration serialization/restart and do not rerun mutations. Receipts are scoped by actor/operation/target, retained for 30 days, capped at 512; capacity returns unavailable instead of evicting an unexpired receipt. Back up the XML and own integration DB/WAL together while stopped. Do not promise idempotency after restoring a backup older than a completed command.

Rollback: set notificationMode Local, disable Hub integration, stop Jellyfin, keep the integration DB dormant, then restore the prior plugin/config backup if needed. Old binaries do not understand new metadata/schema; a tested configuration backup is preferable to in-place schema downgrade. Process restart expires parties/links. No release/catalog publication is performed by packaging.

## Administrator HTTP adapter

Base: `/PrivateSyncPlay/admin/account-hub/v1`. All routes require Jellyfin elevation plus a **current actual administrator** re-read from Jellyfin; API keys without a user claim are not accepted. Security middleware remains authoritative, with no exemptions or local TOTP/step-up implementation.

Routes:

| Method | Suffix |
|---|---|
| GET | capabilities, status, settings, policy?skip=0&limit=50 |
| PUT | settings, policy |
| POST | policy/preview, policy/reset |
| GET | users/{id}/effective-policy |
| PUT / DELETE | users/{id}/policy |

JSON success is `{apiVersion:"1",ok:true,data:{...}}`; errors are `{apiVersion:"1",ok:false,error:{code,message,retryable}}`. JSON serialization is explicitly camel-case, independent of Jellyfin's negotiated profile. All readbacks are no-store and contain no party secrets. Native authentication/Security middleware responses are preserved.

Examples (values abbreviated):

```json
{"revision":"current-opaque-revision","commandId":"uuid","settings":{"maxViewers":20}}
```

```json
{"revision":"current-opaque-revision","commandId":"uuid","defaults":{"defaultGuestPermission":true},"users":[{"userId":"uuid","policy":{"defaultMode":null,"createPublic":"Inherit","createPrivate":"Deny","guestInvites":"Allow"}}]}
```

```json
{"revision":"current-opaque-revision","userIds":["uuid"],"changes":{"policy":{"guestInvites":"Deny"}}}
```

```json
{"revision":"current-opaque-revision","commandId":"uuid","scope":"users","userIds":["uuid"]}
```

`settings` carries a settings object; `policy` carries defaults and/or users. `preview.changes` supports settings/defaults/policy; `resetScope` previews a reset. Direct user PUT carries `policy`. DELETE requires `If-Match` and `X-Command-Id`; all other writes require body commandId and body revision or If-Match. If both revisions are supplied they must agree. Unknown properties, bool/integer permission substitutes, unknown enums, invalid defaults/ranges and caller-supplied stepUpVerified are rejected. Same completed command + different payload returns `409 command_conflict`; stale new commands return `409 revision_changed`. Identical retries return the stored data. Legacy native saves lacking a revision remain compatible, but are serialized and rotate the revision; current revision-bearing native clients reject stale saves.

Capabilities contain supported fields, enum values, factory defaults, hard limits, player allowlists and page/receipt limits. `/policy` paginates actual Jellyfin users; effective-policy exposes current defaults, overrides, effective permissions and per-field default/override/globalSafety sources. Permission overrides retain native `Inherit|Allow|Deny`; defaultMode null means inherit. No second policy engine is introduced.

Bulk and reset operations skip administrator targets and report skippedAdmins. Direct admin user PUT/DELETE returns `403 administrator_protected`. Global defaults preserve administrator soft defaults through protected overrides, including native global-default saves; global hard switches still apply and preview discloses that limitation.

Preview returns changed fields, before/after non-admin policies, warnings and skipped admins without saving. Preview and commit share validation. Settings/party gates are held together during reset checking and commit, preventing guest admission from racing the check. A reset that would revoke existing guest links/leases is rejected: use an explicit settings change after reviewing preview. Explicit settings changes retain native guest revocation, expiry and stream-limit maintenance. Policy writes do not create/end parties or issue/rotate invitation credentials.

## 2FA and remaining acceptance

Native browser calls normalize **only** marked 403/401 (`twoFactorRequired:true`) as pending 2FA. Unmarked 401 remains an invalid session; unmarked 403 remains a permission denial. Pending invitation retries pause until an explicit retry/focus return. Existing playback and guest access are not stopped. No proof/code/token enters a URL, event or standing credential store.

Account Hub must enforce its own step-up mirror before invoking this adapter. This adapter never trusts a stepUpVerified flag and does not proxy or weaken Security.

Tests include native permission non-regression, adapter revision/replay/admin exclusion/preview/reset, separate-ALC client dispatch, absent-host fallback, independent audit acknowledgment, leases/freshness/pruning, bounded payloads, schema rejection and redaction. Real disposable Jellyfin integration exercises installed-package loading, auth/elevation, native guest isolation, the existing consumer SDK, adapter preview/replay/revisions and read-only integrity of both databases.

**Still unverified without the real dependencies:** canonical SDK replacement/compatibility, actual Account Hub durable acceptance/bell/Discord delivery, installed Jellyfin Security 2FA/step-up behavior, full combined P0–P9 acceptance and production proxy/BaseUrl behavior. Fixtures are not a claim of live Hub or Security acceptance. See `account-hub-handoff.md` for actual commands/results.
