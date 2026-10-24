# Jellyfin Security transport acceptance

**Status: live Security transport unverified.** No disposable Jellyfin/Security server, proxy configuration, TLS certificate or bot credentials were supplied. Do not replace this statement with “working loopback” based on the local Kestrel tests.

Plan baseline: latest stable Jellyfin 12.x/.NET 10 (12.2 at plan date), Security **2.6.4**, matching jf12 asset. Actual installed versions and source/package checksums must be recorded by the integrator. This module deliberately has no Security SDK/project reference, status polling importer, native-channel configuration writes or private-file access.

## Candidate setup (not a tested deployment recipe)

1. Keep Security native alerts enabled initially (`native` is the Hub default).
2. Mount exact `POST /AccountHub/api/v1/security/jellyfin-security` after normal middleware. Example URL shape: `https://<trusted-host>/<BaseUrl>/AccountHub/api/v1/security/jellyfin-security`; the listener scheme/port/hostname must come from the real server, not this example.
3. Configure a nonempty shared secret through Hub's encrypted secret accessor and Security's `WebhookSecret`. Never put it in the URL. Rotate both sides deliberately; no old-secret grace period is implemented.
4. Security sends **no Jellyfin access token**. Require `X-2FA-Timestamp: <Unix seconds>` and `X-2FA-Signature: sha256=<hex HMAC-SHA256(secret, exact timestamp + '.' + raw body)>`.
5. Keep TLS validation on. Test a trusted public hostname first. LAN/loopback targets additionally require Security's explicit `AllowPrivateNotificationTargets` approval; never enable this automatically. Proxy authentication must not inject a standing Jellyfin/admin token.
6. Run `POST /TwoFactorAuth/Admin/WebhookTest` through Security's actual admin UI/API. It can test all native channels, not only the webhook. Look for receiver `202` plus a core audit setup receipt (`security.webhook.test`); test does **not** generate an incident alert.
7. Only after testing choose `hub` or intentional `both`. Account Hub does not disable native channels. Warn about duplicates in `both`; no cross-provider dedupe exists.

Receiver semantics: raw body limit 64 KiB; ±300-second signed-header skew; constant-time HMAC check before JSON parsing; invalid signature 401 with at most one rejection audit/minute and no incident fanout; oversized 413; rate limit 429; unavailable storage 503. Valid receipts are durable before 202. Body-hash dedupe survives restart for 90 days; unchanged bodies are duplicates even if the sender refreshes its signature timestamp. A mismatch is authenticated first, then hash-only quarantined (202); full/unavailable quarantine yields `409 security_incompatible` or storage 503. Quarantined entries are never automatically reinterpreted.

Only explicit underscore keys are mapped. `lockout` remains unknown until a verified fixture establishes it. Missing user/IP is acceptable. All payload/prose is discarded, including nested/mixed-case pairing codes. No username-based private routing or raw IP export exists here. Unknown labels become sanitized warning `security.unknown`. `test` is audit/setup feedback only. Native mode imports redacted audit receipts but creates no Hub inbox/external Security notices.

## Required live evidence matrix

| Experiment | Current evidence |
|---|---|
| Actual installed Security tag/jf12 asset checksum and generic event samples | Not supplied / unverified |
| Local listener HTTP loopback, exact port and nonempty BaseUrl | Local **module** Kestrel transport only; not Security |
| Security `AllowPrivateNotificationTargets=false` then explicit `true` | Unverified |
| Public hostname, hairpin/private DNS, reverse proxy | Unverified |
| Self-signed certificate refused; trusted fallback succeeds | Unverified; code never bypasses TLS |
| Token-free webhook through tagged Security blocker | Unverified; TestServer auth-route fixture only |
| Anonymous invite/reset reachability through proxy | Lifecycle owner; unverified |
| Actual `Admin/WebhookTest`, all-channel result and receipt | Unverified |
| Outage/restart and actual sender retry behavior | Receiver durability unit tests; no sender retry/backfill promise |
| Pairing/test/unknown/mismatch redaction | Synthetic contract fixtures tested, not live source captures |
| Pending 2FA marked 403 and unmarked expired/denied session | Synthetic middleware/client tests; live gate unverified |
| Discord reachable DM/guild permissions/closed DM/429 | HTTP handler tests; credentials not supplied |

Troubleshooting: confirm exact BaseUrl/listener first, then sender private-target policy, TLS trust, DNS/proxy routing, clock skew, raw-byte signature and matching secret. A marked 403 on browser API access means pending 2FA; token-free HMAC traffic should not be given an admin token to work around it. If middleware blocks the exact route, obtain a narrowly scoped upstream fix—**never exempt `/AccountHub/api/v1/*`**. Security's reviewed sender has no established durable retry/backfill guarantee; events not received during an outage cannot be recovered by this module.
