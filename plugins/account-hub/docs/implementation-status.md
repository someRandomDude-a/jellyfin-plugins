# Implementation handoff — 2026-10-22

## Baseline and scope

- Writable repository: `jellyfin account hub`; baseline commit `e58a9cead62e00893a55daeb3bbf9a729e7f0935` (initial commit). Repository was clean and contained only `.gitattributes`.
- Inputs read: overview, all five numbered handoffs and `jellyfin-12-plugin-development-skill.md` under the user's `.opencode/plan` directory.
- Source/API baseline: Jellyfin `v12.2`, SDK NuGet 12.2.0, commit `ca0f16eb7c195f720a1493ed469a27c4657db0c6`; upstream authorization-context signatures and package metadata checked during implementation. EF/SQLite 10.0.11 matches that tag's dependency manifest.
- .NET SDK 10.0.402; Windows x64; runtime 10.0.13.
- Source edits remain uncommitted for user review. No other repositories, installed server, users, external services or production media were modified.

## Phase gates (overview §9, not numbered module order)

| Gate | Evidence/status |
|---|---|
| P0 identity/SDK/client | Implemented; real separate host/producer ALC test uses exact framework delegate, lazy resolution, absent-host behavior |
| P0 shared freeze | Candidate envelopes/events/common metadata fixtures provided; domain-owner consumption and full domain-native schemas still pending |
| P1 storage/audit/commands/secrets/shell | Implemented and locally tested; local-only mutations audited in the same DB transaction, no unaudited HTTP writes |
| P1 live plugin load/fault acceptance | Not verified on a running Jellyfin instance; do not mark the milestone complete |
| P2 Security authorization spike | Not implemented; prerequisite for exposing management writes |
| P3–P9 | Not complete; no lifecycle, notification, domain or release-readiness claims |

## Implemented boundaries

- `src/AccountHub.Api/`: MIT public client/DTOs; no Hub or Jellyfin assembly reference. Framework reference supplies keyed DI. See `contracts/v1`.
- `src/AccountHub.Core/`: migration/model snapshot, single-instance serialized transactional store, audit/dedupe/checkpoint, command/results, local settings/secret primitives. No direct Jellyfin DB writes or external API calls.
- `src/AccountHub/`: native plugin, empty generic XML configuration, parameterless registrator, startup failure containment, no-store elevated health/status HTTP API and embedded admin preview.
- `tests/AccountHub.Tests/`: disposable on-disk SQLite and ALC fixtures; no live network dependencies.
- `scripts/`, `.github/workflows/`: allowlisted preview packaging and build/test checks.

Initial migration deliberately creates only foundation-owned tables: `hub_state`, `audit_events`, `event_receipts`, `command_results`, `integration_secrets`, plus EF history/lock. The full program's directory, lifecycle claims, Seerr links/jobs, rollback snapshots, inbox/delivery/token tables **are not yet designed/migrated**; add them in versioned migrations when their phases begin. Do not use a generic JSON job table as a substitute for the specified cross-store sagas.

The secret command intent stores a keyed HMAC fingerprint (key itself Data-Protected), not plaintext or a cheap standalone secret hash. Stored results reveal `configured` only. Startup requires the original key ring once any encrypted row exists, even before an administrator adds an integration secret. Key loss is not silently repaired.

Local settings/secret command authorization accepts a request-local callback for the future proof gate. It is **not** a production Security implementation or an HTTP authorization grant. No HTTP route invokes those callbacks yet. Completed replay is checked before revision/proof; a changed actor/operation/command/payload is independently scoped or rejected. Only same-database callbacks are supported. External provisioning/reset/deletion must use durable claims/sagas, not pretend these transactions cover Jellyfin/Seerr.

## Executed evidence

`dotnet build AccountHub.slnx -c Release`: succeeded, zero warnings/errors.

`dotnet test AccountHub.slnx -c Release`: **26 passed, 0 failed**. Includes:

- actual separate ALC host/client dispatch, host absent, incompatible API version, malformed/duplicate-property/oversize envelopes;
- additive-field receipt conflicts, canonical field ordering, wire names under PascalCase host options;
- initial migration/defaults/reopen, concurrent audit appends, restart idempotency, payload conflict;
- audit mutation/deletion/receipt-time tamper detection, retained checkpoint/reopen/dedupe;
- scoped completed command replay without a second proof, stale revision, denied proof without mutation;
- injected audit-insert failure rolls back settings/command result and disables writes;
- encrypted secret storage/restart/replay, no raw secret/result/hash readback, wrong-input conflict;
- missing-key refusal without regeneration, corrupt-file preservation, future-schema refusal;
- fixed plugin identity/embedded page, elevation attribute and API-key/anonymous rejection.

`dotnet ef migrations has-pending-model-changes --project src/AccountHub.Core`: no pending changes.

Dependency/output inspection: plugin output contains the three owned DLLs, symbols and `.deps.json`, no Jellyfin/EF/SQLite binaries. EF/SQLite/native resolution from a clean server still requires actual runtime evidence. Test references intentionally include server runtime assets and do not prove artifact loading.

`./scripts/package.ps1`: succeeded after rerunning the Release build and all 26 tests. Local preview artifact (ignored by git):

- `artifacts/AccountHub-0.1.0.0-preview-20261022123846477.zip`
- SHA-256: `f6cd2f5bb4e3846e960d6097d726ecdba6ecc15f224b11ae556b2bb491076824`
- Not installed, published or live-load certified. A future packaging invocation creates a new artifact/checksum; this hash refers only to these exact bytes.

## Remaining blockers / next work

1. Load the allowlisted preview in an isolated Jellyfin 12.2 server; verify native discovery/startup ordering, dependency resolution, `RequiresElevation` behavior, API-key rejection, disabled plugin behavior and root/nonempty BaseUrl page loading. Confirm migrations/restore after forced failures. Do not test on production.
2. Finish P0 domain-specific schema review/freeze with DeletionQueue and Private SyncPlay owners before producer integration. Existing public SDK keys and domain stores must stay unchanged.
3. P2: strict running Security GUID/enabled/version detection; manual mirror; old-level gate when lowering; fresh body proof; distinguish marked 403/401 from ordinary denials; verified no-factor fallback only; no transient-error downgrade. Actual Security 2.6.4 jf12 package/proof tests are mandatory before management writes. No standing token or broad route exemption.
4. P3: read-only initial directory discovery, managed-field policy preview/apply/rollback and live admin exclusions; Seerr capability/identity/default/unknown-bit checks and durable jobs/deletion snapshots.
5. P4: one durable notification service/bot runtime, own-user API, fanout/dedupe/outbox leases/retries, safe URLs/SSRF, one-bell fallback, audit query/export with fresh-proof POST at Everything and safe CSV. Add retention worker and bounded receipt/result lifecycle. One redacted signed Security receiver, real transport recipe.
6. P7/P8 after specialist gates: safe disabled-incomplete user provisioning/claims, bounded one-time secret delivery, Discord link/reset claims with honest cross-store partial failures, no password retention/auto-login/2FA reset; actual domain and unified UI adapters.
7. P9: clean install/upgrade/backup/restore, running-server auth/2FA/webhook/cleanup tests, themes/proxy/browser/performance matrix, immutable ZIP/checksum and catalog review. Current package is preview-only, not published or certified.

No clean-server install, actual Security calls, 2FA/proxy/browser run, real Seerr/Discord integration or full end-to-end user lifecycle has been performed. Tests are not a substitute for those gates.
