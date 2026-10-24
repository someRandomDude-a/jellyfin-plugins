# Security integration validation runbook

**Acceptance checklist, not full deployment certification.** The initial module-only evidence below is historical; see [current monorepo runtime evidence](../../../docs/review-status.md) for installed-package, BaseUrl, HMAC and fresh TOTP results. Complete the remaining gates on disposable supported servers. Never test account deletion or lockout against production accounts/media.

## Prerequisites

1. Verify latest stable Jellyfin 12.x ABI and compiled SDK agreement; review any release drift. Run `scripts/verify-security-baseline.ps1`. Install the jf12 Security package pinned in `implementation-status.md`, not the .NET 9/10.11 asset. Verify downloaded ZIP bytes against its published SHA-256 before installing.
2. Complete the shared Account Hub command/audit/Notifications host implementations described in `host-integration.md`; no library-only install.
3. Preserve a separate local break-glass administrator with working 2FA and offline recovery codes. Back up Hub DB/Data Protection keys using Hub's own procedure. Do not read/copy Security private records into Hub.
4. Start with Hub `safeActions:false`, `alertMode=native`, default `Destructive` mirror. Explicitly match mirror to Security's step-up setting. Do not change Security factors, enforcement or password-login configuration through Hub.

## Transport setup to verify

1. Identify the **existing Jellyfin listener** scheme, port and BaseUrl from trusted server configuration. Receiver is `<that base>/AccountHub/api/v1/security/jellyfin-security`; no new listener/port is created. Record direct and proxied URLs without credentials.
2. In Security's admin UI configure its **generic** webhook payload format, that URL and a strong nonempty WebhookSecret; set matching encrypted secret in Hub. Generate a separate >=32-byte IP privacy hash key. No secret in URL or evidence logs.
3. Test loopback/LAN with `AllowPrivateNotificationTargets=false` (expected blocked), then explicitly true on the isolated server. Record actual result. HTTP loopback is not equivalent to protected HTTPS; do not claim it secure across machines/proxies.
4. Prefer trusted HTTPS. Verify untrusted/self-signed certificate fails. **Never disable certificate validation.** Test public-hostname fallback, DNS/hairpin private resolution, allow-private policy, reverse-proxy auth, BaseUrl forwarding and redirects. No wildcard auth/2FA exemption.
5. Send token-free HMAC ingress; no X-Emby-Token, Authorization Jellyfin token, query api_key or proxy-injected Jellyfin credential. Verify sender still reaches receiver when an unrelated browser is pending 2FA. Test exact public invite/reset routes similarly using their token-only POST flow.
6. Invoke Hub's gated webhook-test command or native `POST /TwoFactorAuth/Admin/WebhookTest` as current admin. Native call tests **all configured channels**, can be slow and has upstream rate limits. Correlate per-channel results with Hub's durable test receipt time. A generic timeout does not prove webhook failure. Test event updates setup health, never incident alerts.
7. `native` records redacted history only; `hub` enables configured Hub admin delivery; `both` intentionally duplicates paths. Admin manually adjusts native destinations; Hub never edits them or dedupes messages sent independently by Security.

## Negative and fault tests

- Missing secret/signature, altered byte, wrong HMAC, uppercase/malformed signature, skew beyond ±5 minutes: 401, no receipt/fanout. >64 KiB: 413. Burst: bounded 429. Signed malformed JSON when compatible: 400.
- Duplicate signed body (including re-signed fresh timestamp): accepted no-op, no second alert. Restart receiver and repeat within supported ledger horizon (>=10 minutes) to prove durable dedupe.
- Pairing fixture/code/prose/nested tokens/cookies: none in database/WAL, audit export, UI, logs, bell, Discord or outbound webhook. Unknown wire label: generic warning, no prose/unknown fields.
- Older/newer/missing/disabled/unknown plugin: no API/proof/device calls; tab/badges disabled, core healthy. HMAC events authenticate then only redacted quarantine; full/quarantine unavailable returns 409. Restore compatible plugin and verify quarantine is **not replayed**.
- Store outage: normal ingress 503, no false 202. Sender outage/restart: inspect loss honestly. No established durable Security sender queue, outage backfill or exactly-once guarantee. Keep native alerts if needed.
- Marked 403 and marked 401 pause existing bell; unmarked 401 means session failure, unmarked 403 permission denial. Verify no retry storm or pending-2FA bypass.
- Optional/Admins/All and enrolled/unenrolled: scope inherited, no forced enrollment under Optional, no local password policy change. Browser public-config hint depends on recipient IP; server loopback probe is not authoritative. `local_disabled` rejects local lifecycle flows; `allow_local` warns and never overrides Security login enforcement.
- Real TOTP/recovery: fresh success, invalid/replayed/rate-limited failure; action never executes after failure and proof never reaches query/log/persistent data. Lower mirror with old policy. Audit at Everything uses body POST, no reusable proof ticket.
- Revoke by record ID, not deviceId; verify target identity and trust reduction. ForceLogout separately confirms stronger effect; last-admin/self safeguards fail closed. Network loss after mutation reports remediation pending, not complete revocation or automatic retry.
- Self-service reset (user and admin) uses single-use token, not admin step-up; changes password/revokes sessions, preserves factors/trust/app passwords, no auto-login. Show trust residual and separate Security action. Lost-factor recovery stays in native Security UI.
- Disposable user with factors/devices: delete through Hub/core, restart and verify cleanup through supported observable behavior/native UI. If residual/unobservable, record that result; never reach into private files/internal store. Verify Seerr deletion job and retained audit through Hub lifecycle owner.

## Secret rotation

Keep native alerts active if required. Pause Security webhook sending; rotate Hub's encrypted signing secret and then Security's configured WebhookSecret to the same new value; resume and run native WebhookTest. Old secret must fail. No dual-secret grace period or replay window override is implemented here. Maintain existing dedupe ledger across rotation. Rotate the separate IP hash key only under privacy/retention policy (hash correlation changes); never export keys.

## Evidence required before release

Record sanitized data only; never screenshot recovery codes/proof/token/signature/secret fields.

| Check | Actual value/result/evidence |
|---|---|
| Jellyfin/server/web ABI and OS | **Unverified** |
| Security loaded version + exact downloaded ZIP hash | **Unverified** |
| Listener scheme/port/BaseUrl + proxy | **Unverified** |
| Private flag false/true, HTTP vs trusted HTTPS | **Unverified** |
| Self-signed refusal and public/DNS fallback | **Unverified** |
| Native per-channel WebhookTest + durable receipt | **Unverified** |
| Pending token-free ingress/public routes | **Unverified** |
| Outage/restart/dedupe/quarantine | **Unverified** |
| Real proof/recovery/replay/429 + old-level change | **Unverified** |
| Revoke/ForceLogout safeguards/partial results | **Unverified** |
| Core user-deletion cleanup after restart | **Unverified / possible residue** |
| Browser themes/BaseUrl/mobile/accessibility | **Unverified** |

Troubleshooting: first inspect non-secret compatibility/revision/receipt health. 401 webhook → secret/header/skew/exact bytes; 409 → incompatible/quarantine; 503 → durable receiver/transport health; missing receipt with native timeout → inspect individual channel results. Do not weaken TLS, signing, elevation, 2FA or last-admin policy to make a test pass.
