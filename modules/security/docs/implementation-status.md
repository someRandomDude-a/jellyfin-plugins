# Implementation handoff — 2026-10-22

> Historical module-only baseline. The monorepo now embeds this module and has run real installed-package/HMAC/TOTP checks. See [current runtime evidence and remaining gates](../../../docs/review-status.md), including the observed jf12 assembly revision and compact-GUID contract fixes.

## Baseline and evidence

- Repository began at `80e911339246d6ef6a430279705368c4e1c773ad` (license/attributes only); no Account Hub foundation, frozen P0 fixtures or disposable-server configuration existed here. Implementation is an adapter module with required host seams, not a new runtime plugin.
- Rechecked GitHub latest release: **Security v2.6.4**. Inspected detached tagged source at **`2c02686fcec8b15ea56235de8d196460711befaa`**, not moving main.
- Supported asset: **`Jellyfin.Plugin.TwoFactorAuthv2.6.4.0-jf12.zip`**, GitHub asset SHA-256 metadata **`6c1ae5e41cc0bc7f168025d9b941578f13115cd73cac7a72496888c46f64a998`**. This is published API metadata, not proof that an asset was downloaded, loaded or certified here.
- Build: Jellyfin.Common **12.2.0**, .NET SDK **10.0.402**, Windows x64, Node **22.22.3**. No Security assembly/package dependency. Server-owned NuGet assets excluded from library runtime output.

### Tagged-source observations

Reviewed controller, StepUpService, RequestBlockerMiddleware, NotificationService, UserExportService, UserTwoFactorStore and searched source for deletion wiring.

- Blocker uses raw **403 + `twoFactorRequired:true`**, skipping token-free requests. No broad Hub exemption added.
- StepUp accepts TOTP/recovery, email code and single-use user-bound passkey token. Only explicit `{verified:true}` permits fresh-proof execution. No proof cache is implemented.
- `Users` catches store failures and can synthesize all-zero rows. Thus badges are informational and **not trusted for no-factor fallback**. Fresh MyStatus is used; unknown/incomplete status denies proof-dependent authorization.
- Device DELETE removes by `TrustedDevice.Id`; native response can be empty 200. Record IDs are distinct from device IDs and restricted to safe route characters to prevent URI path traversal.
- ForceLogout clears trust and registered IDs then terminates sessions; only remaining administrator is guarded upstream, but upstream enumeration catch can proceed. **Hub must independently enforce fail-closed last-admin/target guards**, as its host interface requires. Do not treat upstream success as independent complete-session verification.
- Generic notification format and underscore event keys confirmed. Pairing can put a code in both message and payload; both are discarded. Included source-shaped pairing fixture has additional adversarial mixed-case/nested fields; it is **synthetic**, not captured live traffic.
- `DeleteUserDataAsync` call found in Security's own user-delete controller, **not a generic Jellyfin user-deleted subscription**. Whole-source search did not establish generic event wiring. Core deletion residue is **possible/unverified after restart**; no private-file access, internal cleanup call or unauthorized upstream patch was made.
- No durable sender retry/outage backfill established. Hub cannot import events it never received; no audit scraping repair path.

## Delivered files

- `src/AccountHub.Security/`: compatibility, typed allowlisted HTTP client, sanitized DTOs, bounded display cache, authorizer, admin controller, host contracts, HMAC/parser/receipt seams and DI registration.
- `web/`: BaseUrl-aware request/errors/deep links and embeddable Security tab (no standalone app or bell).
- `tests/AccountHub.Security.Tests/`: version/manager/client/gate/controller/HMAC/redaction/routing/quarantine tests and synthetic source-shaped fixtures.
- `scripts/verify-security-baseline.ps1`, CI workflow, runbook and host integration contract.

## Local verification results

| Command/check | Result |
|---|---|
| `dotnet test AccountHub.Security.slnx -c Release` | **91 passed**, 0 failed, 0 skipped; Release build has 0 warnings/errors |
| `node --test web/security-client.test.mjs` | **4 passed**, 0 failed |
| `node --check web/security-panel.mjs` | Passed syntax validation; not a browser test |
| `./scripts/verify-security-baseline.ps1` | Latest release, tagged commit and jf12 asset digest metadata match |
| `dotnet list src/AccountHub.Security package --include-transitive` | Inspected SDK graph; server runtime assemblies excluded |
| Library output inventory | Only `AccountHub.Security.dll`, `.pdb`, `.deps.json`; no bundled Jellyfin/server/EF implementation DLLs |

Tests cover running-vs-downloaded version, metadata failure, no upstream calls on non-ok states, structured denials, explicit verifier success, failed/429/transport/unknown-status denial, uncached proof, recovery-code-capable status, core-only absence fallback, manual mirror levels, old-level lowering, caller denial, safe DTO projection, display refresh, record-vs-device ID, path traversal rejection, command retry callback behavior, ambiguous ForceLogout, HMAC/skew/size/secret/budget, underscore mappings, pairing redaction, alert modes, duplicate/quarantine and durable-store failure signaling. Controller/receiver/command-store tests use test doubles; they do **not** establish real server authorization, durable ledger restart behavior or upstream proof validation.

## Remaining release blockers (not claimed complete)

1. **P0/P1 core binding**: adopt/reconcile the module host seams, implement durable revision/idempotency/audit/old-settings lease and real caller/last-admin validation. No production implementation exists in this repo; tests use explicitly test-only doubles.
2. **P4 Notifications binding**: one durable receiver/ledger/fanout pipeline, bounded rate limit/rejection audit and health display. Unit tests do not prove restart durability of that absent host implementation.
3. Real latest-12.x server: plugin manager startup/update/disable/restart behavior, authenticated API authorization, Optional/Admins/All, real TOTP/recovery replay/rate limits and public/HMAC pending-session reachability.
4. Required transport experiment: actual listener/BaseUrl/proxy/private-target/TLS/public fallback, upstream WebhookTest channel result/receipt correlation, outage/restart and bounded quarantine. [Runbook](runbook.md) is a **validation procedure**, not a tested recipe.
5. Disposable core-deletion cleanup after restart, preserved audit/Seerr deletion saga, self-service reset and residual trust messaging end-to-end; those lifecycle modules are not present here.
6. Real Hub browser navigation, theme/mobile/keyboard/screen-reader checks, settings and recent-event/receiver-health binding. JavaScript unit checks are not browser certification.
7. Combined P9 artifact staging/install/ABI/load/rollback. **No plugin ZIP/catalog was packaged or published.** Library build output is not an installable plugin.

## Storage / rollback

No database/schema migration or Security configuration changes made. Host must use shared Hub migrations and encrypted secret/key backup procedures, audit retention 15–365 days/default 90, and durable dedupe/outbox retention. Do not create a second SQLite store for this module.

Rollback: disable Hub safe actions and alert delivery; disable native Security webhook destination manually while retaining native alerts if desired; remove this module's UI/DI/controller registration from the Hub build. Preserve Hub DB/audit/Data Protection keys. Leave Jellyfin Security installed and enforcing; never disable 2FA to recover Hub. Local break-glass admin + Security recovery codes remain the recovery path.
