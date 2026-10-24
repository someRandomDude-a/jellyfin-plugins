# Notifications implementation handoff

## Baseline and scope

- Repository: `jellyfin-notifications`, initial commit `b636d70e6d8d2aa07986d43d8dde0dfd1545f31d` (license/attributes only).
- This work adds uncommitted module source/tests/docs; no commit, production server modification, plugin packaging or publishing was performed.
- Inputs: `00-program-overview.md`, notification-relevant `01-account-hub.md`, producer sections of `02`/`03`, `04-notifications.md`, Security mapping in `05`, and the supplied Jellyfin 12 plugin-development guide.
- SDK 10.0.402; module `net10.0`; contract key/API major 1; SQLite schema 1. No Jellyfin plugin ABI load test has been performed. The module is intended for Hub's latest-12.x host, not a second plugin.

## Delivered source groups

| Files | Responsibility |
|---|---|
| `src/AccountHub.Api/Contract.cs` | Framework-only v1 DTO/envelopes and absent/incompatible-host client |
| `src/AccountHub.Notifications/Host.cs` | Required host/bot boundaries and settings/models |
| `Validation.cs`, `NotificationService.cs` | Limits, safe links, source/scope checks, dispatch/batch results, preferences/routing |
| `Store.cs` | Durable event/fanout commit, separate replay/presentation retention, encrypted destinations, leases, tokens, Security receipts |
| `DeliveryWorker.cs` | Independent receipt acknowledgments, bounded delivery/retry/dead-letter processing and audited presentation pruning |
| `DiscordBotTransport.cs`, `WebhookSender.cs` | Shared-bot REST sends and signed SSRF-constrained outbound transport |
| `SecurityIngress.cs` | Exact-byte HMAC, bounded ingress/replay, allowlisted redacted normalization, modes and quarantine |
| `SecretDelivery.cs` | Expiring encrypted DM capsule primitive for lifecycle-owned issuance |
| `Endpoints.cs` | Explicit user/admin/producer/HMAC route separation; static assets and optional core export bridge |
| `Web/bell.js`, `Web/settings.js` | Own-user bell and embedded administrator routing/health panel |
| `AuditCsv.cs` | Formula-safe CSV cell helper for the core writer |
| `tests/` | .NET/ALC/HTTP/SQLite/transport and minimal DOM behavior tests |
| `docs/`, repository-root `.github/workflows/tests.yml`, solution/build files | Integration, remaining acceptance gates and repeatable validation |

## Local results

```text
dotnet test AccountHub.Notifications.slnx -c Release
  65 passed, 0 failed (Windows/.NET 10.0.13 runtime)
node --test tests/web/bell.test.cjs
  11 passed, 0 failed (Node 22.22.3)
dotnet list src/AccountHub.Notifications/AccountHub.Notifications.csproj package --vulnerable --include-transitive
  No known vulnerable packages from configured NuGet source
```

Coverage includes separate producer ALC + framework delegate discovery/dispatch; absent/incompatible host; conflict/logical dedupe/restart/concurrent retries; independent audit acknowledgments; own-user/admin scope/step-up denial; raw pending fixture; body/batch/scope limits; XSS text rendering/unsafe links; replay/skew/pairing redaction/native/hub/both/quarantine; expired leases/stale commits/stable send IDs; unlink/admin demotion/opt-out; 429/403/5xx; actual local webhook signature/retry/private-policy/redirect tests; encrypted destination readback; presentation retention with retained dedupe; CSV formula escaping; secret-capsule expiry.

These tests do not constitute kill-process/power-loss injection, a real browser accessibility audit, public DNS rebinding experiments, TLS certificate tests, installed-plugin ABI verification or live Security/bot integration.

## Remaining P4/release gates

1. Embed into the real Account Hub foundation. Implement the required host adapter and shared key-ring/bot boundaries. Incorporate schema 1 in core's authoritative migration process; no separate audit DB/writer.
2. Bind actual Jellyfin user/elevation and Security fresh-proof checks; test no-factor/fallback rules. Audit view/export/search, 90-day default/15–365 retention and chain/checkpoint verification are **core-owned and not implemented by this repository**. The export bridge and CSV helper are not a substitute.
3. Complete the [real Security transport matrix](security-transport.md). No tested production URL/port/proxy recipe can honestly be supplied yet.
4. Mount the assets through the Hub/verified File Transformation integration and test native themes, mobile sizes, focus/navigation/logout/server-switching and BaseUrl on the actual Jellyfin web version.
5. Coordinate DeletionQueue fallback/legacy scripts and SyncPlay producers in their repositories. This module never disables their local UI/domain actions, but cannot certify one-bell behavior without them.
6. Attach lifecycle claims/erasure/revocation and recipient reachability checks around the capsule primitive. There is no invite/reset lifecycle implementation or dedicated durable secret worker here.
7. Complete crash-process injection/backup-restore/key-loss/native dependency load/package checks on the disposable supported Jellyfin server. A later integration owner must package the exact Hub artifact, inspect native SQLite/dependency/SDK ownership and generate its checksum/catalog metadata.

Known intentional reductions: bounded audience expansion (1,000 default) instead of large paged directory fanout; no quiet-hours/coalescing UI; dead-letter retention measured from terminal state, no inspection extension; failure alerts are local-only rather than alternate healthy external routes; settings panel is a functional JSON editor rather than a complete core audit/rule designer; no link-generation token beyond current Discord account comparison. These remain visible gaps, not silently completed acceptance boxes.

No breaking v1 fields/routes were renamed. The administrative credential issue/revoke routes and static asset paths are additive implementation surfaces. Preserve producer retry IDs and the 400-day dedupe horizon when integrating. Production enablement remains blocked on the above gates.
