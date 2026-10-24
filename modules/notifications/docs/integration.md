# Embedding in Account Hub

## Host registration

Reference `AccountHub.Notifications.csproj` from the real Hub project. Do not introduce a new plugin identity or separate service. Use the **same Hub SQLite path**, never another plugin's database. `nh_*` tables are owned only by this module.

```csharp
services.AddSingleton<INotificationHost, HubNotificationHost>();
services.AddAccountHubNotifications(new NotificationOptions
{
    DatabasePath = hubDatabasePath
});

// Either adapt the existing shared bot's transport:
services.AddSingleton<IDiscordTransport, SharedBotNotificationTransport>();

// OR use the provided REST transport with the EXISTING bot's encrypted token accessor:
// services.AddAccountHubDiscordTransport(sp =>
//     () => sp.GetRequiredService<HubBotSecrets>().CurrentToken);

// Configure Hub's shared IDataProtectionProvider with persistent protected keys.
// Do not leave a production key ring ephemeral or world-readable.
```

The names `HubNotificationHost`, `SharedBotNotificationTransport`, and `HubBotSecrets` above are host-owned adapters, **not implemented classes in this repository**. Exactly one transport registration/bot owner is required. `DiscordBotTransport` itself has no gateway connection, slash-command handler, credentials store, or second bot worker.

At the Hub endpoint-mapping hook, after normal Jellyfin authentication and Security middleware:

```csharp
endpoints.MapAccountHubNotifications();
// Only if core has NOT already mapped /audit/export:
// endpoints.MapNotificationAuditExport();
```

This module exposes ASP.NET endpoint-mapping extensions. Wiring them into the actual Jellyfin plugin/host lifecycle is the Hub integrator's responsibility; no unsupported Jellyfin startup hook is claimed here.

### `INotificationHost` requirements

- `CurrentUserAsync`: validated Jellyfin **user** identity from the current request, not request JSON, a producer credential, or a standalone service API key. Preserve Security's gate. Mutation authorization uses an Authorization header, not cookies alone.
- `UserExistsAsync`: return false for deleted/disabled/ineligible recipients. `AdministratorsAsync` returns actual current administrator IDs; capped at `MaxAudience` (default 1,000). This implementation expands within that bounded cap; it rejects larger audiences rather than claiming unbounded batched fanout.
- `AuthorizeManagementAsync`: verify Jellyfin elevation, inherited Security authorization and the manual step-up mirror. Configuration writes include test/retry/credential management. Consume fresh proof from the authenticated request **body**, never a query string or cached ticket. If inspecting a body, enable buffering with an explicit size bound and rewind it so module deserialization can read it. Failures throw an appropriate `NotificationException`; do not downgrade a denied proof.
- `AuthorizeAuditAsync`: additionally apply `Everything` and its fresh-proof rules. Existing Security middleware runs first; its raw marked 403/401 must remain untouched.
- `AppendAuditAsync`: the **one shared core writer**, durable/idempotent by `(source,eventId)` plus payload hash; return `{status,auditId}` only after durable success, otherwise throw. It owns the hash chain, search, 90-day default/15–365 day retention, pruning checkpoints, integrity health and exports. Notification/audit acknowledgment streams are independent. Intent audit entries say `changing`/`issuing`/`revoking`; they are not proof a subsequent mutation committed.
- `ExportAuditAsync`: core must audit export, apply filters/limits and formula-safe CSV (`AuditCsv` is provided), or JSON. The optional endpoint returns the core result in a v1 envelope; production streaming/download behavior can stay in core's existing endpoint.
- `DiscordLinkAsync`: current verified Discord owner, rechecked before delivery. A link changed away and back must be represented by the lifecycle owner's generation-aware adapter if that distinction matters; the current interface compares account IDs, not a link-generation token.
- Security properties: current installed **supported release** check (plan baseline 2.6.4), encrypted webhook secret accessor, and validated alert mode (`native` default). No private Security files or audit scraping.

Missing required adapters fail DI; there is deliberately no permissive default. Schema/storage initialization failures leave the module unavailable (`health.ready=false`) rather than fabricating acceptance. Key-ring failure never resets configuration to empty defaults.

## Contracts and routing

Key: `AccountHub.Api.1`; exact type: `Func<string,CancellationToken,Task<string>>`. `AccountHub.Api` has no plugin/framework-package dependency. Producers resolve lazily and retain their **own post-domain-commit durable outbox/local fallback**. Never use request IDs as event IDs.

```json
{"apiVersion":"1","operation":"notifications.publish","requestId":"13ca4b37-c7ba-42ab-8ffe-68a6aa0e69c5","payload":{"schemaVersion":1,"eventId":"2c0e02fd-1ed8-45cb-8437-9d17ab9e933e","source":"deletion-queue","type":"proposal.updated","severity":"info","title":"Proposal updated","body":"Review your proposal status.","audience":["2b1a7839-fc16-44b3-9534-70f8d2a4f53b"],"adminOnly":false,"occurredAt":"2026-10-22T12:00:00Z","dedupeKey":"notice:123"}}
```

Successful response data is `{status:"accepted"|"duplicate",notificationIds:[...]}`. `event_conflict` is terminal `rejected` with that message. Payload hash uses exact event JSON bytes for keyed/HTTP envelopes: keep the event representation stable across retries. Semantically equivalent JSON with reordered properties/whitespace is not promised to have the same hash. Batches preserve input order and each result includes `eventId` and its own v1 result envelope; inspect each result.

Default registered sources: `account-hub`, `deletion-queue`, `private-syncplay`, `jellyfin-security`. Register external sources in `NotificationOptions.Sources` before issuing credentials. HTTP credentials cannot impersonate Security, access management, or route admin notices without `notifications:admin`. Limits: 64 KiB/event, 256 KiB/request, 50 events/batch. Rates are process-local bounded fixed windows: Security 120/minute; producers 600/minute global, 120/credential and 240/source. Add deployment-level connection/body/concurrency limits at the host/proxy.

Producer token endpoints are admin-only `POST /settings/notifications/producer-tokens` (`{source,scopes}`), one-time `{token,credentialId}` with `no-store`, and idempotent `DELETE /settings/notifications/producer-tokens/{credentialId}`. There is no credential GET/readback. External producer routes use `Authorization: Bearer <producer-token>`; Security uses HMAC without a Jellyfin token.

Rules are exact source/type matches or `*`. Shared Discord channels/webhooks **require `adminOnly` events and admin-only routes**. Shared payloads use a fixed sanitized summary, never event body or metadata. Private user events only support opt-in linked Discord DMs and their own inbox; they never go to shared webhooks. DM rules with `adminOnly:false` explicitly permit user delivery. Administrators and critical notices cannot mute the inbox; ordinary noncritical in-app preferences default on, DM preferences default off. Preferences do not suppress audit. Quiet hours/coalescing are not implemented.

Settings GET redacts every destination as `[configured]` and omits secret values (`null`); PUT preserves those fields when these placeholders are retained. Send a new secret explicitly to rotate it. Pending delivery bodies/IDs are stable, but the latest route secret and private/HTTP approval are checked at send time. Route deletion/destination change, unlink, opt-out or admin demotion prevents queued delivery.

Webhook URLs cannot have URL credentials, query strings, fragments or metadata destinations. Only HTTPS unless the administrator explicitly enables HTTP; private/loopback requires a separate explicit approval. DNS is resolved and pinned by the actual connection callback, all returned IPs are checked, proxies are disabled, and **all redirects are refused**. TLS validation is never overridden. Invalid/blocked destinations are terminal; 429/5xx/network ambiguity retry. Worker concurrency is one; each loop handles up to 20 receipts and 20 deliveries, with a 120-second lease, 20-second transport timeouts, and six attempts. Retry hints are capped at 24 hours.

Discord sends store message IDs and stable enforced nonces, but nonce dedupe is provider-bounded. A send/commit crash can still produce an external duplicate; no exactly-once promise is made. Failure summaries are durably queued with the dead-letter commit and route to the inbox only, avoiding recursive failure-alert delivery. Alternate healthy external failure routes are not implemented.

### Web assets

Anonymous **static only** assets:

- `/AccountHub/assets/notifications/bell.js`
- `/AccountHub/assets/notifications/settings.js`

Load the bell once from the authenticated hosted-web bootstrap using `ApiClient.getUrl(...)` or a verified BaseUrl-aware File Transformation hook. There is no disk modification or FT registration in this module. It requires the existing web `ApiClient`, polls with ETag/backoff, pauses hidden/blocked sessions, clears data on navigation/session changes, and renders text only. It does not run on login/challenge/Security/invite/reset/link-token URLs. This is a web adapter, not a native/mobile-client integration.

The core admin shell mounts the routing panel with:

```javascript
const dispose = await window.AccountHubNotificationSettings.mount(root, request);
// request(relativeApiPath, method, payload) returns unwrapped v1 data and handles
// current-user Authorization plus any required fresh step-up body proof.
// Call dispose() on view destruction.
```

Before enabling the global bell, update DeletionQueue to cooperate. It can expose `window.AccountHubLocalInbox={href:'#/its-local-inbox'}` so the Hub surface includes a reachable fallback link, then suppress only its duplicate mount. **The module never hides/deletes another plugin's bell.** Old scripts without a handoff still need the producers-first upgrade; one-bell coexistence is not certified by these unit tests.

## Persistence, backup and rollback

Initial module schema is `nh_schema.version=1`; initialization is transactional and refuses unknown versions. It is not a migration of an existing Account Hub database schema: core must approve/incorporate these names in its real migration manifest before deployment. No downgrade is implemented.

- WAL + `synchronous=FULL`, per-intake transaction for event ledger/content, logical dedupe, recipient inbox rows and pending deliveries.
- Notifications/source presentation content: default 90 days, configurable 7–365. Terminal deliveries/dead letters: 30 days from terminal state (no inspection extension yet).
- Source/event and logical dedupe ledgers: **400-day supported producer retry horizon**, independent of presentation pruning. Do not retry beyond this horizon. Security receipts/replay hashes/quarantine: 90 days; quarantine bounded to 1,000 receipts and never replayed automatically.
- Audit: core-owned, default 90 days/15–365 range; module pruning does not truncate a hash chain. Core must verify and checkpoint its own pruning. Module pruning is preceded by a durable audit intent; if audit is unavailable pruning waits.
- Data Protection encrypts destinations/secrets/capsules, **not the whole database**. User IDs, redacted events and routing status remain plaintext. Protect database/key files with host ACLs and encrypted backups. Production key protection/persistence must be configured by core. Losing keys fails delivery/configuration closed, not open.

Stop all writers for an offline consistent database backup (including WAL if not checkpointed), or use SQLite's supported online backup mechanism. Preserve the key ring, its wrapping keys, Hub configuration and exact binaries together. Never copy only an active `.db` while ignoring WAL. Restore a verified matched backup; do not edit schema version or restore an older binary over newer data in place. Disabling Hub must leave DeletionQueue/SyncPlay native behavior intact.

`SecretDelivery.Seal` returns an encrypted, recipient-bound, expiring capsule (at most six hours). Lifecycle must store it atomically with issuance metadata, retry only until expiry, and **erase it on success/expiry/revocation**. `SendAsync` targets only that Discord DM, including an explicitly selected unlinked invitee. This primitive does not implement lifecycle claims, revocation, reachability preflight, cleanup scheduling, or a second durable secret outbox. Never put capsule/plaintext links in generic notifications/audit/logs.
