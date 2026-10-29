# Deletion Queue integration adapter v1

## Dependencies and ownership

Use the [current plugin setup](../README.md) and root latest-target test/package
wrappers. This is the retained developer contract for the administrator adapter and
durable outbox, not the former standalone repository's installation/handoff procedure.

DeletionQueue still owns authorization, proposals, consent, protected activity, deadlines, claims, Seerr execution and its authenticated evidence. Account Hub cannot execute media deletion through this adapter. Existing native routes remain supported; the adapter adds its own bulk/reset admin protections without substituting a second policy database.

The canonical `shared/AccountHub.Api` SDK has no plugin identity or Hub implementation
dependency. Lazy discovery uses **`Notifications.Api.1`** for notification publication/
upsert and **`AccountHub.Api.1`** for Hub audit compatibility, both with the framework
signature **`Func<string, CancellationToken, Task<string>>`**. Calls use a two-second
cancellation/wait bound and reject accumulating non-cooperative in-flight calls.
Absent, throwing, incompatible or unhealthy providers are unavailable, never successful
delivery. Keep the package's matching SDK DLL; do not substitute another release's DLL.

New proposal intake requires standalone Notifications' registered-type, scoped-access
and command capabilities. Existing consent, Undo, history and local safety UI remain
available during provider failure. Hub is optional and never executes media deletion.
This adapter creates no alternative host, bot, audit database or factor integration.

## Adapter HTTP contract

Base: `/DeletionQueue/admin/account-hub/v1`. Every operation requires the current Jellyfin **administrator user session**, not a service API key or body-supplied actor. Same-origin mutation, duplicate/unknown JSON rejection, bounded requests and native store validation remain enforced. Jellyfin Security middleware is not bypassed. Hub must satisfy its own mirrored step-up gate before proxying; domain authorization is checked independently.

| Method | Suffix | Body / behavior |
|---|---|---|
| GET | `/capabilities` | Version, feature/field metadata, enum names, defaults/ranges and limits |
| GET | `/status` | Health, mode, host availability, proposal counts and bounded outbox diagnostics |
| GET/PUT | `/settings` | Non-secret integration settings and Seerr association; write-only key replacement |
| POST | `/settings/import-enhanced` | `{revision,commandId}`; one unambiguous server-side import |
| GET/PUT | `/policy` | GET `{revision,defaults,users}`; PUT `{revision,commandId,defaults?,users?:[{userId,policy}]}` |
| POST | `/policy/preview` | `{userIds,changes}`; detached native safety evaluation, no writes |
| POST | `/policy/reset` | `{revision,commandId,scope:"global|users|all",userIds?}` |
| GET | `/users/{id}/effective-policy` | Effective authority, source, preferences, warnings, read-only admin flag |
| PUT | `/users/{id}/policy` | `{revision,commandId,policy:{authority,notificationPreferences?}}` |
| DELETE | `/users/{id}/policy` | Empty body; `If-Match` and `X-Command-Id` headers |

Wire fields are lower camel case, enum names are strings, output GUIDs are dashed; native compact GUID inputs are accepted. Responses are always `{apiVersion:"1",ok:true,data:{...}}` or `{apiVersion:"1",ok:false,error:{code,message,retryable,details}}`, independent of Jellyfin's media-type casing profile. Errors intercepted earlier by Security retain their original status/body.

`revision` is an opaque 64-character token covering native policy, Seerr configuration, integration settings and **all personal preference revisions**. PUT accepts the returned body revision or `If-Match`; supplying inconsistent values fails validation. A stale write returns `409 revision_changed` with the current token in `error.details.revision`. Adapter command receipts are committed with the mutation: identical successful command/actor/input returns the stored result without applying changes again; changed input returns `409 command_conflict`. This is adapter-scoped idempotency; native endpoints retain their original `CommandReplay` semantics.

Per-user `authority:null` inherits. Preferences expose only `delayHours` and `notifications`, not subscription IDs or consent. Bulk changes/reset classify administrators from live Jellyfin accounts inside the mutation. Admin targets are skipped/reported; direct per-user adapter mutations reject them. Global authority changes preserve excluded admins' effective authority with overrides **only when needed**. Global hard safety limits still affect everyone and are disclosed in preview/results. Reset invokes the native policy/preference safety rules, preserves deadline floors, proposals/consent/claims and connection credentials, and leaves factory automatic deletion/acknowledgement false. Unknown/deleted account overrides are not silently discarded.

Settings PUT uses flat optional fields: `notificationMode`, `hubIntegrationEnabled`, `hubFallbackAfterMinutes`, `publishAudit`, `deviceBell`, plus optional `seerr:{baseUrl?,apiKey?,clearApiKey?,confirmClear?}`. Omitted/null API keys preserve the existing key; clearing requires both flags and no replacement key. Seerr server identity is bound to this Jellyfin host. Reads return URL/server ID and `keyConfigured`, never the key. Factory reset preserves the entire Seerr connection to avoid sending retained credentials to a different default URL.

## Persistence and dispatch

The SQL schema remains the strict, plugin-owned journal-only format (`user_version=1`); no unrelated outbox table is created. The **authenticated snapshot payload format advances from 1 to 2** by appending one migration event. Existing encrypted events, keys, installation identity, local notices, consent and claims are retained. Old local notices are not backfilled. Mixed/downgraded payload history is rejected. A DB/head crash still freezes the store without repair. Previous binaries reject payload format 2: rollback requires a stopped, consistent pre-upgrade database + keys + seal backup, not just replacing the DLL.

`QueueRuntime.Write` creates local notices and event intents in the same authenticated
transaction as the domain change; no outbound calls occur in the writer. Notification
and audit streams have independent UUID IDs, acknowledgements and retry state.
Personal media notices contain only the recipient-authorized title/proposal context;
never paths, other viewers' votes/activity, credentials or journal payloads. Policy
audit carries non-secret before/after safety summaries. Uncertainty also creates a
critical administrator alert and remains visible locally. Optional preferences do
not suppress audit. Domain actions are projected live, not pre-authorized in event JSON.

One dispatcher probes compatibility/health, leases up to ten events for five minutes,
sends after commit, then persists acknowledgements in one batch. Restart recovers
expired delivery leases, **not destructive claims for resend**. Backoff is
1m/5m/15m/1h/6h, at most eight attempts. Notification retries are clipped to the
configured fallback deadline. Accepted/duplicate notices stay Notifications-owned;
rejected/exhausted notices become local fallback and are not automatically re-published.
Audit retries/dead letters are independent. Local domain evidence remains authoritative.

Notification routing is fixed to `Auto`; legacy `Local`/`Hub`/`Disabled` values are
normalized on load/write without clearing domain data. Defaults retain integration,
audit and device bell on, with a 15-minute fallback budget (range 1–1440). Existing
work can remain local when the provider is unavailable; that does not permit new
intake without required Notifications capabilities. Audit is separately enabled.
Old local notices are not migrated/backfilled when providers appear or data is wiped.

Limits remain fail-closed: 32 KiB browser mutations, 500-account classification, 2,000 current outbox entries, 2,000 adapter receipts, 4 MiB snapshots, 256 MiB journal and two-second replay. Terminal delivery metadata expires after 30 days or rolls out earlier under capacity pressure; original encrypted journal events remain retained, notice ownership remains durable, and `deliveryMetadataRolledOff` reports the boundary. Pending evidence is not silently discarded to make space. Saturation requires operator review. This is **not** a validated 10k-pending-event/high-volume archival solution.

An accepted response can be lost forever. Idempotent retries resolve ordinary ambiguity,
but after the fallback budget a permanently lost acknowledgement can still produce
both a local and shared notice. No cross-store cancellation/status transaction removes
that possibility. Do not claim exactly-once delivery or retry a destructive media
request because notification delivery failed.

## Bell and Security boundary

The local script recognizes marked `403/401` with `twoFactorRequired:true`, clears private content, pauses polling and shows a Login link. Focus/visibility resumes verification; unmarked 401/403 is not classified as pending 2FA. No notice data is saved in localStorage.

The hosted-Web handshake recognizes standalone `[data-notifications-bootstrap]`
or the visible legacy `[data-account-hub-bell]` marker. Queue then uses a plain
**Deletion queue** shortcut rather than a second shared inbox bell, retaining local/
fallback notices and safety controls. Disabling `deviceBell` also leaves the shortcut.
Review links use `#/home?deletionQueueProposal=<guid>`; critical administrator links
retain the existing native admin page. Removing a marker is not an authority grant.

No other plugin's files/controls are removed without the explicit hosted handoff.
Browser generations, live native access and current command revisions/snapshots
remain mandatory. A right consent swipe invokes the projected Allow command and
dismisses only after success; it never grants administrator approval or dispatches Seerr.

## Verification and limits

- Unit tests cover separate client/host load contexts, absent/incompatible/throwing host, atomic abort/reopen, migration/key preservation, stable IDs, independent acknowledgements, fallback ownership, privacy and admin-safe native policy effects.
- API fixtures check v1 casing/envelopes, stored-result replay/conflict, composite revision conflicts, administrator exclusion/effective-authority protection, header-only DELETE and factory reset/credential preservation.
- Chromium uses synthetic Hub markers and pending-2FA responses; it still exercises real Jellyfin Web login, injected UI, native admin settings, local notice access and session clearing.
- Installed evidence is scoped by package/source set in [current status](../../../docs/review-status.md). It does not close every lost-ack crash, old-script coexistence, production proxy/TLS or 10k-pending performance boundary. No live media deletion or production credentials are authorized by these fixtures.

Before release, pass the current latest-target SDK/HTTP/installed checks and remaining
combined-system gates, including the ambiguity/capacity boundaries. Older producer
fixtures do not certify later hosts/packages or the full product.

## References

- [Producer/host API](../../../docs/notification-producer-api.md)
- [Storage migration and recovery](../../../docs/plugin-storage-recovery.md)
- [Safety and permissions](../../../docs/safety-and-permissions.md)
- [Scoped historical evidence](../../../docs/verification.md) · [Testing](../../../docs/testing.md)
