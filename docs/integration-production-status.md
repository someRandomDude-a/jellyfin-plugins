# Integration production review — current r6 candidate

**Decision: do not release yet.** Evidence is scoped to `artifacts/runtime-review/blocker-followup-r6/linux-x64`. No commit, tag, upload or publication is authorized by this review.

Later r8 installed Hub-to-Seerr normal-flow acceptance passed against real Seerr 3.5.1, including discovery, durable provision/quotas, drift/requests and native-deletion follow-up. Seerr access was API-only. An earlier transport failure remains unexplained; API fault and connection/identity-replacement cases are not implied passed.

The exhaustive standard installed matrix passed **32/32 Linux x64 cases**: all plugin subsets with/without pinned native Security. Results: `artifacts/runtime-review/r6-combinations/results.json`. This closes the current candidate's standard combination check, not all feature/fault/platform acceptance or the native concurrency blockers below.

| Boundary | Verified | Still required |
| --- | --- | --- |
| Hub ↔ native Jellyfin users/policy | Installed onboarding/groups, administrator exclusion on tested paths, native password login, durable retries, drift/reset; guarded disabled-first schema adapter | Atomic native creation/allocation and full-policy concurrent-update guarantees remain unresolved. Per-effect crash, permission/disk failure and backup-restore coverage is incomplete. |
| Hub embedded Lifecycle/Notifications/Security | Root tests, failure isolation, reset completion/fairness, installation identity and missing-database fail-closed behavior; actual browser loads Hub controls/native CSS | Installed reset fault-boundary injection, cross-module retention/deletion consistency and sustained-load acceptance |
| All four plugins ↔ native Security | r6 combined real TOTP, recovery-code consumption/replay denial, pending challenge completion, trust revocation, explicit session termination, raw-body signed ingress, restart and storage recovery passed | Native outbound webhook sender/proxy/TLS and user-deletion factor cleanup; final exhaustive combination matrix tracked separately |
| Hub ↔ Discord Bridge | Framework-only boundaries, standalone/combined earlier fixtures, identity verification, privacy projection tests; earlier real library cards | Interactive commands, personal DMs and explicit watch-party invitation delivery on the final candidate; no production Discord users/credentials |
| Hub ↔ domain plugins | Authoritative adapters, paginated/bulk UI tests and installed settings/authorization checks | Full combined mutation/failure/retention acceptance; real browser domain/theme/mobile coverage |
| Deletion Queue authenticated storage | 150 backend tests including checkpoints, heartbeat deltas, tamper, replay and rotation interruption cases; installed five-database checks | Representative installed long-running soak, replay-evidence/checkpoint capacity and slow/disk-full storage behavior |
| Hub ↔ Seerr | API-only contract tests and real Seerr 3.5.1 public-API fixture; immutable audit outbox tests | Installed Hub discovery/provision/reconcile/deletion test tracked separately; API fault/identity replacement and connection-change acceptance |
| Shared media notifications | Unit/UI/privacy/access tests; earlier opt-in/current-access/live-card evidence | Final candidate live delivery, revocation and retention/failure acceptance; uncertainty must never trigger blind external-send replay |
| Native web client/themes | r6 real Jellyfin dark-theme browser rendered Groups with native checkbox classes/outline/labels and disabled administrator selection; Invites disabled unavailable Discord delivery and retained native labeled controls | Modern/Legacy, mobile/keyboard/focus, light/custom-theme and player integration acceptance. DOM/computed-style inspection is not visual certification. |
| Distribution/platforms | Full root 770 .NET / 124 JS tests, inventory and offline package/catalog/upgrade fixtures; fresh immutable local r6 packages | Fresh architecture-specific installed ARM64 and Windows evidence, transitive legal/license text review, complete install/upgrade/recovery acceptance |

## Native concurrency limitation is a release blocker

The pinned native API has neither atomic disabled-first user creation with a reserved ID nor a managed-field conditional policy-update operation. The plugin must not silently add native database constraints, alter native migrations, use private server locks, or claim that last-second rechecks prevent every interleaving. An upstream supported atomic/conditional API, or another explicitly reviewed solution preserving the required security invariants, is needed. Passing a bounded concurrency test does not remove this limitation.

### Revised scope requested by owner

The owner explicitly requested leaving native Jellyfin races as known limitations and fixing Hub's own concurrency without native schema changes. New source implements a bounded 128-operation single-writer queue and explicit SQLite `BEGIN IMMEDIATE` before native user/policy reads. Tracked policy mutations touch only requested managed fields; administrator/disabled/password checks occur inside that transaction. Onboarding enable no longer submits a full stale policy. Events are published only after commit and queue release. Native password API calls share Hub's queue but do not run inside a separate SQLite transaction. No native service replacement, private locks, new native indexes or migrations are used.

Maintenance scans report duplicate internal-ID counts through administrator-only `AccountHub/api/v1/native-users/integrity` and worker health; creation refuses already duplicated state. Automatic live reassignment is **not implemented**: the pinned native user-data cache uses `InternalId` and exposes no supported global invalidation operation. Changing an ID alone does not safely repair already ambiguous cached/legacy ownership. A reviewed offline repair procedure remains required, and historical misattribution cannot be inferred from a duplicate number alone.

These edits passed 121 targeted Hub tests (including queue serialization, capacity/failure/cancellation); fresh installed acceptance is separate. Earlier r6 evidence does not certify the new queue/transaction adapter.

The subsequent r8 candidate also blocks native HTTP admin user creation by default, with an explicitly confirmed five-minute override in Hub Settings that pauses onboarding and serializes/drains ownership changes. The timer is monotonic and resets to Hub on restart. 124 targeted Hub tests, 17 workspace UI tests and installed all-four-plugin onboarding/restart/integrity/storage-recovery checks passed. See [user creation workflow](user-creation-workflow.md) and `suite-integration-99a333d4-898b-44ce-ac0c-e76ebad8349c` evidence. Direct internal calls by other plugins still bypass this HTTP guard; no global native guarantee or automatic duplicate repair is claimed.

Detailed findings/history: [release safety review](release-safety-review.md). Test evidence: [review status](review-status.md). Complete gates: [v1 release gates](v1-release-gates.md). Backup requirements: [plugin storage recovery](plugin-storage-recovery.md).
