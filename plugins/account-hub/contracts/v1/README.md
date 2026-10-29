# AccountHub.Api.1 — producer compatibility contract

## Dependencies and ownership

Use the canonical framework-only SDK in `shared/AccountHub.Api`. New notification
producers use standalone `Notifications.Api.1`; Hub's older key remains a compatibility
and audit surface, not evidence that Hub owns the current inbox/worker/storage.

Exact key: `AccountHub.Api.1`. Exact boundary type:
`Func<string, CancellationToken, Task<string>>`.
Only framework types cross plugin ALCs. Bundle `AccountHub.Api.dll`, never the Hub implementation.
`AccountHubApi.CallAsync` resolves the delegate lazily and returns a retryable unavailable result for a missing, broken or incompatible host. Producer cancellation is propagated.
Installed plugins are trusted server code; this delegate is not a sandbox or a source-authentication boundary.

## Operations

| Operation | Current behavior | Result data |
|---|---|---|
| `hello` | Implemented | `hostVersion`, `apiVersion`, `operations` |
| `health` | Implemented, side-effect free | `ready`, `hostVersion`, `schemaVersion`, `features` |
| `audit.publish` | Durable | `status:accepted\|duplicate`, `auditId` |
| `notifications.publish` / `notifications.publishBatch` | Compatibility dispatch when available; unavailable in the fallback foundation dispatcher | Check advertised capabilities and each result |
| `notifications.register` / `notifications.upsert` | Use standalone Notifications' advertised interface | See the producer guide |

`hello.operations` advertises available operations. `health.ready` is not proof of
delivery or domain authority; check operations/features and standalone provider
readiness. The fallback foundation dispatcher advertises audit only. Bell suppression
uses the explicit hosted-Web handoff, never the mere presence of a Hub delegate.

Request/response/event field names are the supplied plan's v1 names, not PascalCase variants. Explicit JSON properties on public DTOs and explicit serialized HTTP content protect names against Jellyfin media-type negotiation.
Limits are 64 KiB per event, 256 KiB per transport request and up to 50 events per
notification batch. JSON nesting is bounded at 32. See the [current notification
producer/host guide](../../../../docs/notification-producer-api.md) for fanout,
retention and command projection semantics.

The examples are fixtures, not live acceptance evidence. `notification.request.json`
still exercises `unavailable` in the foundation dispatcher fixture; that is not the
standalone Notifications contract. Additive envelope/event fields are tolerated;
unrecognized audit fields are ignored for storage but included in the canonical intake
hash. Metadata requires an explicit privacy allowlist in `EventValidation`; never
silently accept private domain evidence.

## Idempotency

- Stable producer `eventId`, scoped by `source`; `requestId` is per invocation.
- Object property ordering is canonicalized recursively; arrays and JSON number spelling retain their meaning/order.
- Same ID and input returns `duplicate` with the original audit ID, including after restart/pruning.
- Changed input with the same ID is terminal `rejected`; stored evidence is not replaced.
- Receipts and completed command results are currently not pruned. Minimum supported producer retry horizon: 365 days. Disk growth/pruning policy is a release blocker, not permission to drop receipts early.
- Publish only after the producer commits. Keep separate notification/audit acknowledgments. Do not roll back a primary domain action when Hub delivery fails.

## Domain adapter schema

`domain-capabilities.schema.json` and `DomainCapabilities` define the common metadata projection. Domain defaults/enums/ranges still belong to the domain plugin. Roots:

- `/DeletionQueue/admin/account-hub/v1`
- `/PrivateSyncPlay/admin/account-hub/v1`

Suffixes: `capabilities`, `status`, `settings` GET/PUT, `policy` GET/PUT, `policy/preview` POST, `policy/reset` POST, `users/{id}/effective-policy` GET, `users/{id}/policy` PUT/DELETE.
Writes use the domain revision, scoped command ID, current Jellyfin administrator and native safety engine. Bulk/reset skip live administrators server-side; direct per-user admin writes reject. Hub is not a policy or execution authority for either plugin.

The retained domain contracts describe actual native projections; generic schema
metadata never grants domain authority. Installed-package and failure-boundary
acceptance remain scoped to their recorded evidence, not an unconditional cross-project
freeze or full release certification.

## References

- [Notification producer/host guide](../../../../docs/notification-producer-api.md)
- [Queue adapter](../../../deletion-queue/docs/account-hub-v1.md)
- [SyncPlay adapter](../../../private-syncplay/docs/account-hub-integration.md)
- [Current status](../../../../docs/review-status.md) · [Evidence register](../../../../docs/verification.md)
