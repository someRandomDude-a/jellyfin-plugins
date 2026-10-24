# AccountHub.Api.1 — candidate v1 handoff

Exact key: `AccountHub.Api.1`. Exact boundary type:
`Func<string, CancellationToken, Task<string>>`.
Only framework types cross plugin ALCs. Bundle `AccountHub.Api.dll`, never the Hub implementation.
`AccountHubApi.CallAsync` resolves the delegate lazily and returns a retryable unavailable result for a missing, broken or incompatible host. Producer cancellation is propagated.
Installed plugins are trusted server code; this delegate is not a sandbox or a source-authentication boundary.

## Operations

| Operation | Foundation build | Result data |
|---|---|---|
| `hello` | Implemented | `hostVersion`, `apiVersion`, `operations` |
| `health` | Implemented, side-effect free | `ready`, `hostVersion`, `schemaVersion`, `features` |
| `audit.publish` | Durable | `status:accepted\|duplicate`, `auditId` |
| `notifications.publish` | Retryable unavailable | Reserved until P4 |
| `notifications.publishBatch` | Retryable unavailable | Reserved until P4 |

`hello.operations` advertises only implemented operations. `health.ready` means **foundation storage** readiness, not notification delivery readiness. Consumers must check operations/features as well. Do not suppress a local bell or local notices on this build.

Request/response/event field names are the supplied plan's v1 names, not PascalCase variants. Explicit JSON properties on public DTOs and explicit serialized HTTP content protect names against Jellyfin media-type negotiation.
Limits are 64 KiB per event, 256 KiB per transport request and (when P4 ships) 50 events per batch. JSON nesting is bounded at 32. Batch fanout/delivery semantics are not implemented yet.

The examples are fixtures, not live acceptance evidence. `notification.request.json` intentionally receives `unavailable` today. Additive envelope/event fields are tolerated; unrecognized audit event fields are ignored for storage but included in the canonical intake hash. Metadata is stricter: an explicit allowlist is required to avoid turning the audit into a secret archive. Add approved fields in `EventValidation` with privacy tests; never silently accept private domain evidence.

## Idempotency

- Stable producer `eventId`, scoped by `source`; `requestId` is per invocation.
- Object property ordering is canonicalized recursively; arrays and JSON number spelling retain their meaning/order.
- Same ID and input returns `duplicate` with the original audit ID, including after restart/pruning.
- Changed input with the same ID is terminal `rejected`; stored evidence is not replaced.
- Receipts and completed command results are currently not pruned. Minimum supported producer retry horizon: 365 days. Disk growth/pruning policy is a release blocker, not permission to drop receipts early.
- Publish only after the producer commits. Keep separate notification/audit acknowledgments. Do not roll back a primary domain action when Hub delivery fails.

## Domain adapter candidate schema

`domain-capabilities.schema.json` and `DomainCapabilities` define the common metadata projection. Domain defaults/enums/ranges still belong to the domain plugin. Roots:

- `/DeletionQueue/admin/account-hub/v1`
- `/PrivateSyncPlay/admin/account-hub/v1`

Suffixes: `capabilities`, `status`, `settings` GET/PUT, `policy` GET/PUT, `policy/preview` POST, `policy/reset` POST, `users/{id}/effective-policy` GET, `users/{id}/policy` PUT/DELETE.
Writes use the domain revision, scoped command ID, current Jellyfin administrator and native safety engine. Bulk/reset skip live administrators server-side; direct per-user admin writes reject. Hub is not a policy or execution authority for either plugin.

This common schema has **not** been accepted/tested by the two domain repositories yet. Do not claim the P0 cross-project freeze gate is complete until their owners consume these fixtures and verify their native projections.
