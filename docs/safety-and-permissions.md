# Safety, permissions and operational limits

## Prerequisites

Install compatible packages using the [installation guide](../README.md#installation-and-upgrades).
Keep stopped, consistent backups with original keys and installation identities.
Configure each provider explicitly. Loaded metadata is not evidence of readiness,
delivery or permission. A failed safety check must not be repaired by clearing history.

## Authority boundaries

| Owner | Authoritative decisions |
|---|---|
| Account Hub | Native-session account administration, managed access, invitations, verified identities and central audit. Templates never copy administrator rights, credentials, factors or removal consent. |
| Notifications | Recipient-scoped inbox/preferences/delivery; current administrator/media access and domain command projections are rechecked. Missing access providers fail closed. |
| Discord Bridge | One bot and allowlisted transport. Discord roles, display names and delivery receipts never establish native authority or consent. |
| Deletion Queue | Proposal authority, verified target/ownership, viewer consent, separate administrator approval, activity, accumulated delay, durable claims and destructive Seerr dispatch. |
| Automatic Deletion Requests | Administrator-authorized bounded rule evaluation/submission only. A verified requester retains their own identity/authority; no substitution for a disabled requester. Arr reads are GET-only. |
| Private SyncPlay | Native party/session/media authorization plus restrictive guest policies. Invitations do not grant general library access or party-control rights. |

Existing-session password recovery requires the same browser/account's exact native
Security-verified, unblocked session, including native-approved bypasses. No fresh
reset code, API-key, token-only or signed-out fallback replaces that verdict.
Operational administrator permissions and security-sensitive proof gates remain
distinct; native Security's global enforcement is never disabled by Hub settings.

## Native account-write boundary

Hub uses a bounded single-writer queue and immediate SQLite transactions for its
managed writes, publishes events after commit and reserves new identities disabled,
hidden and non-admin before native password/policy completion. Normal native HTTP
creation is blocked unless an administrator grants the temporary serialized override.

This does not intercept direct internal user-manager calls from other plugins or
arbitrary native writes. Earlier reviews identified native internal-ID allocation
and full-policy lost-update races. Last-second rechecks and finite concurrency
fixtures are not global atomicity proofs. No native schema constraints, migrations,
private locks or automatic duplicate-ID reassignment may be introduced as a workaround.
Duplicate-ID detection requires reviewed offline remediation; cache/legacy ownership
cannot be reconstructed merely by changing a number. One host must own its data.

The historical 12.2→12.3 source comparison checked identical path sets/hashes for
270 native database blobs plus user-manager/policy/event contracts. That review
explained a then-stale version guard; it does not authorize future versions.
Current builds require reviewed source-contract fingerprints and matching SDK/ABI.

## Removal and restore limits

Seerr controls Arr targeting and file removal; its reported DELETED result is
trusted. The plugin does not have an atomic remote file-set precondition and does
not independently prove physical file absence. It never directly deletes Arr
records/media files or Seerr request history. Unmanaged pending-request decline
is a separate supported path with the same applicable authority checks.

A durable claim precedes destructive dispatch. Timeout/crash/uncertain outcomes
must not be resent automatically or retried by restoring an older backup. Explicit
read-only Seerr outcome reconciliation may record FilesDeleted; completion of that
checkpoint is separate from sending another deletion. A date, rule match, gesture
or administrator approval never supplies another viewer's consent or bypasses delay.

Read-only maintenance also checks for changes made directly in Seerr. A verified
external deletion or pending-request decline closes unclaimed work as cancelled,
with an explicit external-change explanation, not queue-performed completion.
A confirmed missing upstream record is interpreted only after checking the saved
Jellyfin–Seerr server association; it is never proof that files were deleted.
Authentication/network failures, changed connections and mismatched identity or
quality evidence fail closed. Stale notification actions cannot reopen closed work.
Claimed or uncertain work is not automatically cancelled, reopened or dispatched
again; explicit read-only reconciliation remains separate.

Authenticated local journals/checkpoints detect tampering and partial/mismatched
restores, **not rollback of an entire valid database/key/seal set**. No independent
off-host rollback witness is implemented. Preserve claims/history and reconcile
external outcomes before recovery. Bounded journals, replay ledgers, receipts and
outboxes can reach capacity; representative long-running/disk-full testing remains
necessary. Failed outbox writes cannot manufacture event backfill.

Collections/artwork are opt-in native presentation writes, not removal authority.
Original artwork and pre-existing memberships are preserved; administrator conflicts
must not be overwritten. Native image-pointer changes do not provide a universal
atomic compare-and-swap against simultaneous administrator edits. Restore owned
presentation before uninstalling and retain its backups until pointers are safe.

## Host, UI and transport limits

Plugins run in-process, not a security sandbox. Failure containment cannot promise
zero host crashes, corruption or resource exhaustion. Privileged filesystem races,
all crash/effect boundaries, sustained load and every platform/proxy remain outside
bounded fixture guarantees. Use least-privilege hosting and resource limits.

Native configuration-page hosting is administrator-oriented. Public static resources
must contain no private state; every API separately enforces live sessions, role,
scope, same-origin mutation policy and API-key rejection where required. Ordinary
controls use authenticated hosted-Web adapters without persistent Web-file changes;
DOM-replacing skins/native non-Web clients are not automatically supported.

External sends are not exactly-once. Preserve stable IDs and durable uncertainty;
do not replay consumed Discord interactions, ambiguous secret delivery or claimed
removals. TLS, DNS/private-target policy, HMAC signing and narrow ingress checks
must never be weakened to turn a failing test green.

## References

- [Accounts and permissions](account-management.md) · [Creation ownership](user-creation-workflow.md)
- [Security/recovery and webhook validation](security-recovery-integration.md)
- [Storage recovery](plugin-storage-recovery.md) · [Discord delivery](discord-suite-integration.md)
- [Testing](testing.md) · [Current blockers](review-status.md) · [Evidence register](verification.md)
