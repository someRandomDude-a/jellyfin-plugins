# Account Hub

Native Jellyfin **12.2 / .NET 10** account-control plugin, implemented from `01-account-hub.md` and its cross-cutting handoffs. **This is a foundation preview, not the complete Account Hub or a production release.**

Plugin GUID: `bc9c3d3e-6da3-4ee5-91cd-c429d58ad901` (stable; do not regenerate). Version: `0.1.0.0`. License: MIT.

## Implemented

- Jellyfin plugin entry, service registrator and embedded administrator health/status page.
- Separate `AccountHub.Api` producer library and candidate v1 fixtures, with real separate-ALC discovery/dispatch tests.
- SQLite/EF Core forward-only migration, WAL, durable audit writer, hash-chain verification and verifiable retention checkpoint.
- Source/event deduplication and conflict rejection across restart and audit pruning.
- Transactional revision/command gate with completed-result replay before requesting proof.
- Encrypted integration-secret fields, no secret readback, and missing-key/corrupt-database fail-closed behavior.
- Elevated read APIs that also reject API keys and callers without a current user session.

**Not implemented/enabled:** account management/policy, invites, Seerr, Discord bot/link/reset, notifications/bell, HTTP producers, Security proof/status/webhook adapters, domain proxies, audit search/export UI, scheduled retention. Local core settings/secret primitives are deliberately not exposed over HTTP before P2. No fake-success integration endpoints or replacement 2FA/OIDC stack.

The existing domain plugins and Jellyfin Security are not modified. No existing users are changed on installation.

## Build and test

```powershell
dotnet restore AccountHub.slnx
dotnet build AccountHub.slnx -c Release --no-restore
dotnet test AccountHub.slnx -c Release --no-build
dotnet tool restore
dotnet ef migrations has-pending-model-changes --project src/AccountHub.Core
```

Pinned SDK: 10.0.402. Jellyfin SDK: 12.2.0, target ABI 12.2.0.0. EF/SQLite: 10.0.11, matching the reviewed Jellyfin tag. Tests use disposable random directories and no production server/media/credentials.

```powershell
./scripts/package.ps1
```

Creates an allowlisted **preview** ZIP and SHA-256 under `artifacts/`. Does not install, deploy, publish a catalog or certify a live load. Server-owned Jellyfin/EF/SQLite DLLs are not bundled. Actual clean-server dependency/ALC loading remains a release gate.

## HTTP surface

Only `GET /AccountHub/api/v1/health` and `GET /AccountHub/api/v1/status` are exposed. Both require Jellyfin `RequiresElevation`, plus an authenticated non-API-key user. Security's existing request gate is not bypassed. Responses are v1 JSON envelopes, no-store. Other planned routes do not exist yet.

Admin page: `web/index.html#/configurationpage?name=AccountHub`, respecting the server BaseUrl. Browser rendering, SPA lifecycle and Security-pending behavior still require live validation. Native clients and separately hosted web clients do not gain a bell from this build.

## Storage and recovery

Database: `<Jellyfin plugin configurations>/AccountHub/account-hub.db`. Data Protection key ring: sibling `keys/`. Only integration fields are protected; **SQLite, audit rows, WAL and backups are not whole-file encrypted**. Protect the entire directory and backups using OS permissions. Data Protection key files must be treated as sensitive; this preview does not configure an external key-encryption/KMS provider.

On failure, Account Hub reports unavailable and disables its writes; the hosted-service failure is caught so it does not intentionally stop Jellyfin. Never delete/recreate the database or regenerate keys to clear an error. Restore a stopped, consistent database + key backup. See [recovery](docs/recovery.md).

See [implementation/handoff status](docs/implementation-status.md) for phase gates, test evidence and remaining work, and [candidate contracts](contracts/v1/README.md) before integrating a producer.
