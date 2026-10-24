# Account Hub — Jellyfin Security integration

Optional **Account Hub module**, not an installable Jellyfin plugin or a replacement 2FA provider. Implements handoff `05-jellyfin-security.md`. MIT.

## Implemented

- Running-plugin GUID/enabled/version gate; exactly `2.6.4` / `2.6.4.0`. Missing, disabled, incompatible or unknown blocks every Security API call.
- Safe status/device projections, bounded short-lived display cache, pagination, BaseUrl links and an embeddable admin Security panel.
- Current-caller HTTP authentication only; no cookies, redirects, standing token, factor reset, audit import, private files or cross-plugin assembly reference.
- Manual step-up mirror, fresh `StepUp/Verify`, explicit confirmation, old-level authorization when weakening settings, no failed-proof downgrade.
- Exact-byte HMAC validation and redacted event mapping for the **shared Notifications receiver**; quarantine and durable acceptance interfaces.

## Build and test

Requires .NET SDK `10.0.402` and Node 22.

```powershell
dotnet test AccountHub.Security.slnx -c Release
node --test web/security-client.test.mjs
node --check web/security-panel.mjs
./scripts/verify-security-baseline.ps1
```

Compiled against Jellyfin.Common `12.2.0` / `net10.0`. Security source: tag `v2.6.4`, commit `2c02686fcec8b15ea56235de8d196460711befaa`. See [handoff](docs/implementation-status.md) for actual evidence and remaining release gates.

## Integration required

This repository started with only a license. Account Hub core P0/P1 and the Notifications durable receiver are **not present**. This module deliberately does not create a competing plugin identity, database, audit writer, receiver controller or bot.

The Hub owner must implement the mandatory host interfaces and bind this module to its existing foundation before deployment. No production fallback implementations are supplied. Unit-test doubles are not durable storage.

See [host integration](docs/host-integration.md) and the [operator validation runbook](docs/runbook.md). Live Jellyfin loading, real TOTP/recovery, webhook transport, browser rendering and deletion cleanup are **unverified**. Do not install the library DLL by itself or mark the program release-ready.
