# Notification producer and host integration

## Dependencies and ownership

Use `shared/AccountHub.Api` and the current standalone Notifications plugin. The
SDK is framework-only and has no plugin identity; it does not require installing
Account Hub. The implementation in `modules/notifications` is a shared library,
not a second inbox/worker/bot. Do not embed a new Hub-owned `nh_*` database using
retired module-only setup instructions.

## Producer contract

Use `NotificationsClient`, lazy provider discovery and `Notifications.Api.1` with
the framework signature `Func<string,CancellationToken,Task<string>>`. Check
capabilities/readiness, not installation metadata alone. Portable registrations
declare source/types, recipient rules, layouts and allowed domain commands.
See the [plugin API examples](../plugins/notifications/README.md#internal-plugin-interface).

Publish only after the domain change commits. Preserve event IDs and identical
payloads on retries; never substitute a request ID for an event ID. Conflicting
payloads for the same source/event are rejected. Do not assume arbitrary JSON
reordering produces the same receipt hash. Upsert uses a stable source-scoped
logical dedupe key and a new event ID for each change; replay does not change history.
Batches retain order and return per-event results that must each be inspected.

The v1 envelope contains `apiVersion`, `operation`, `requestId` and `payload`.
Supported operations include hello/health, registration, publish, publishBatch,
upsert and audit publication. Limits include 64 KiB/event, 256 KiB/request,
1–50 batch events and a default host audience bound of 1,000; larger audiences
are rejected rather than silently truncated. Registered sources/actions and
producer scopes constrain what can be published.

External producers use `Authorization: Bearer <producer-token>` on the producer
routes, not a Jellyfin administrator token. Administrator-only token issuance
returns credentials once; there is no credential readback. A producer cannot
impersonate native Security or acquire management/recipient access through a token.

## Host and command requirements

The host supplies current native user/admin existence and authorization, current
source/media access, verified DM identity, protected settings and durable audit
adapters. API keys, request-body user IDs and browser flags cannot substitute for
native-session identity. Missing access or command projectors fail closed.
No permissive production host implementation should replace missing dependencies.

Domain commands are allowlisted and projected live for each recipient. Mutation
requires current inbox ownership/access, command revision and snapshot; the domain
must recheck them atomically at its own boundary. Published events do not contain
precomputed private commands. A successful notification action is not a cross-store
transaction: preserve uncertain effects and native ownership rules.

Notifications management audit is independent; forwarding to a configured Hub audit
owner requires explicit durable accepted/duplicate acknowledgment. Framework-only
JSON/delegates must cross plugin load contexts, not DTO-typed implementation delegates.
Audit retention/checkpoint integrity remains the audit owner's responsibility.

## Delivery, privacy and retention

In-app notices and personal preferences remain recipient-scoped. Generic shared
administrator routes must not leak private user event content. Library announcements
use their separately consented media-only Bridge path; interest/requester/viewer
audiences must be removed before that shared boundary. Current role/access/link,
destination and preferences are rechecked before queued delivery.

Webhook connections pin validated DNS addresses, refuse redirects/proxies, retain
TLS checks and require explicit approval for HTTP/private destinations. Secret
readback is redacted; rotating a route uses the existing protected settings gate.
External delivery is not exactly-once; Bridge receipts distinguish duplicate,
retryable, terminal and uncertain outcomes. Do not blindly resend ambiguous work.

Presentation history defaults to 90 days (7–365); terminal delivery/dead-letter
retention is 30 days. Source/event and logical dedupe survive for a supported
400-day producer retry horizon. Security receipts/quarantine retain their independent
bounds and are never reinterpreted automatically after a mismatch. These bounds are
not infinite deduplication or event backfill guarantees.

Protected destinations, Push keys and secret capsules are not whole-database
encryption. Back up the owned database and original key ring consistently. A sealed
secret capsule is a recipient-bound expiring primitive, not authority to mint an
invite/reset or replay delivery: the lifecycle owner must persist the claim, enforce
expiry/revocation and erase the capsule on success/expiry/revocation. Never include
secret links or plaintext credentials in generic notices, audit or logs.

## Web integration

The standalone hosted-Web bootstrap owns one bell/settings view. Preserve BaseUrl,
native authenticated `ApiClient` handling, generation/navigation cleanup and text-only
rendering. Do not mount on login/challenge/invite/reset/link-token pages or hide another
plugin's controls without an explicit handoff. Native non-Web clients do not acquire
browser controls by installing a library. Domain fallback UI must not become a second
shared inbox/worker.

## References

- [Notifications installation/migration and API examples](../plugins/notifications/README.md)
- [Categories and gestures](notification-channels.md) · [Push](web-push.md)
- [Security ingress](security-recovery-integration.md) · [Discord delivery](discord-suite-integration.md)
- [Storage](plugin-storage-recovery.md) · [Safety](safety-and-permissions.md)
