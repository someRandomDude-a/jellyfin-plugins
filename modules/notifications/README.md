# Account Hub Notifications

Implementation of the notifications module described by `04-notifications.md` in the supplied Account Hub plan. **This is an embeddable .NET 10 module, not a standalone Jellyfin plugin.** This repository initially contained only its license; the Hub/core and Security adapters are not present here.

## Included

- Framework-only `AccountHub.Api.1` JSON delegate and optional producer client.
- SQLite durable intake, immutable event-ID/payload checks, logical dedupe, frozen recipients, per-destination delivery leases/retries, and dead letters.
- Own-user inbox, unread count, read/dismiss/read-all, preferences, ETags; revisioned administrator routing and redacted delivery health.
- Hashed/revocable source-scoped HTTP producer tokens with one-time issuance.
- Discord DM/guild-channel REST transport using **the Hub's existing bot credentials**, suppressing mentions, respecting 429, and treating 403 as terminal.
- HMAC-signed generic outbound webhooks, connection-time DNS/IP validation, explicit private/HTTP exceptions, no redirects, and bounded requests.
- Signed Jellyfin Security ingress, durable independently acknowledged notification/audit work, `native|hub|both`, unknown/test handling, and mismatch quarantine. Raw Security prose/payload is discarded.
- Hosted-web bell/inbox and mountable administrator panel, plus a **separate encrypted, expiring DM secret-delivery capsule** for the lifecycle owner.

Only Discord and generic webhooks are external delivery channels. Audit remains owned by Hub/core; this module does not create another audit writer or bot runtime.

## Build and test

Requires .NET SDK **10.0.402** and Node 22+.

```powershell
dotnet build AccountHub.Notifications.slnx -c Release
dotnet test AccountHub.Notifications.slnx -c Release
node --test tests/web/bell.test.cjs
dotnet list src/AccountHub.Notifications/AccountHub.Notifications.csproj package --vulnerable --include-transitive
```

Latest local verification: **65 .NET tests and 11 JavaScript tests passed** on Windows; NuGet reported no known vulnerable packages. Tests include an actual local Kestrel webhook receiver, but **not a running Jellyfin or Jellyfin Security installation**. CI is configured for Windows/Linux; remote CI results are not asserted.

## Integration and release status

See [integration](docs/integration.md), [Security transport acceptance](docs/security-transport.md), and [implementation handoff](docs/handoff.md).

**Not release-certified:** the Hub must supply real Jellyfin authorization, mirrored fresh step-up, its shared audit writer/export/checkpoints, persistent protected encryption keys, bot secret access, supported-Security version detection, and endpoint/web mounting. Real Jellyfin/Security/proxy/TLS, Discord credentials, File Transformation, and DeletionQueue one-bell/fallback coexistence remain integration gates. No plugin ZIP/catalog has been packaged or published.
