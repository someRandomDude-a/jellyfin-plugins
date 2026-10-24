# Account Hub integration handoff — Private SyncPlay

## Source and ownership

- Baseline: `3db22d9c63e4a57d54031f187c8ac832ede2f751`.
- Implementation changes are uncommitted; no commit, push, release or catalog publication was requested/performed.
- Contract source: the approved plan-defined v1 contract from `00`, `01` sections 5/6/14, `03`, and `04` under the user's plan directory. No canonical Hub checkout/SDK was available; the user explicitly authorized local implementation against those plans.
- Existing plugin GUID and party SDK 1.1.0/key/signature remain unchanged. New producer client: AccountHub.Api 1.0.0, no plugin identity. Own outbox component: PrivateSyncPlay.IntegrationStore 1.0.0, schema v2.

## Changed areas

- `src/AccountHub.Api`: framework-only keyed-DI JSON client, timeouts and bounded in-flight dispatch.
- `src/PrivateSyncPlay.IntegrationStore`: separate bounded SQLite outbox, WAL, claim leases, migration, freshness, retention and independent acknowledgments.
- `HubAdminController`, `HubPolicyService`, `HubWire`: versioned administrator routes, explicit envelopes, native policy projection, pagination, preview/reset, current administrator exclusions, revision/command replay gates.
- `HubIntegration`, `PartyStore`, `PrivateSyncPlayManager`: allowlisted event summaries, native fallback, asynchronous dispatch, playback-start and lifecycle/moderation hooks.
- `Configuration`, `Plugin`, `ServiceRegistrator`: compatible settings defaults, shared native-save revision gate, protected admin soft defaults and command receipts in the native XML save.
- Web settings/UI: integration settings, refreshed server revision after saving, marked pending-2FA handling without playback changes. The existing early player input guard is retained.
- Packaging explicitly stages the two additional owned DLLs, never SQLite/Jellyfin implementation DLLs. Existing standalone party SDK packaging is unchanged.
- Tests cover adapter, publisher, outbox, separate ALC, redaction, migration and auth-state distinctions; real-server integration includes adapter checks and both database integrity checks.

## Verification commands

Use a .NET 10 SDK (`dotnet` below; the local temporary SDK was used during this run):

```text
npm ci --ignore-scripts
npm run check
node --test tests/ui/*.test.mjs
dotnet test tests/Jellyfin.Plugin.PrivateSyncPlay.Tests -c Release
dotnet build examples/WatchPartyConsumer -c Release
pwsh ./scripts/package.ps1 -Dotnet dotnet
node scripts/validate-package.mjs
pwsh ./scripts/test-sdk.ps1 -Dotnet dotnet
node scripts/integration.mjs <new-disposable-directory> --with-consumer --install-from-catalog
git diff --check
```

Local tests passed with 225 .NET tests and 61 UI tests. The standalone extracted party SDK consumer built successfully. The first expanded disposable Jellyfin 12.2 catalog-install run (`psp-account-hub-v1-2`) passed adapter anonymous/nonadmin denial, preview/admin exclusion, command replay, revision conflicts, native guest/media/moderation tests, consumer-plugin checks and SQLite integrity for Jellyfin plus the own outbox. The final expanded run/result is recorded below after completion. Retained fixture containers/networks are removed after checking stopped databases; no existing Jellyfin installation or production media is used.

## Next owner / remaining gates

1. Read `account-hub-integration.md`, the actual capabilities response, and `tests/fixtures/account-hub-v1/contract.json` (fixtures are provisional plan-defined v1).
2. Reconcile/replace the producer SDK with the canonical frozen Hub SDK without changing the existing party SDK.
3. Test actual Hub durable acceptance, outage/restart, notification/audit dedupe, rejection, routing/preferences and one-bell integration. Local fixtures are not proof of live host compatibility.
4. Test installed supported Jellyfin Security marked 403/401, actual 2FA pending/verified states and denied step-up through Account Hub. No Security bypass is implemented; upstream auth middleware owns the gate.
5. Complete combined P0–P9 acceptance, proxy/BaseUrl testing and deployment-specific operational rollback. The plan's complete program is not certified by this plugin-only handoff.
6. Keep the native XML and own outbox/SQLite sidecars in stopped-server backups. Set Local/disable integration before rollback; retain the outbox dormant and use a tested pre-upgrade config backup for an older binary. Restart intentionally expires parties/guest links.

Known limits: failed local outbox writes cannot be backfilled; the 30-day/512-command/8 MiB receipt budget is finite and refuses additional commands rather than silently evicting active replay protection. Legacy native saves without revision metadata remain compatible but cannot provide optimistic conflict protection. Native settings writes have no reliable caller identity in the plugin configuration callback, so their federated audit actor is unknown rather than fabricated. Mandatory in-party guest-joined feedback remains native even with Hub inbox publishing; only optional native notices are suppressed on Hub acceptance.

## Final local result (2026-10-22)

- Final source checks: **225 .NET tests**, **61 UI tests**, JavaScript syntax, extracted SDK consumer build and `git diff --check` passed.
- Both `psp-account-hub-v1-final-3` and the final packaged-source run `psp-account-hub-v1-final-4` passed the complete expanded real Jellyfin 12.2 integration suite, including catalog install/restart, adapter checks, native media/guest/consumer behavior and SQLite integrity/foreign keys for both `jellyfin.db` and `integration-v1.db`.
- Test containers from these final non-retained runs were removed by the harness; the earlier retained fixture was stopped, both databases checked read-only and its containers/network removed. Unrelated user/test containers were left untouched.
- Artifact: `artifacts/PrivateSyncPlay-0.1.3.zip`, SHA-256 `49da6843e8712a920e09dc1a705f5fb5d651d2c074d4bcdff1c320cbf227870d`.
- Packaging/catalog checksum validation passed. **Not published.** No claim is made that the public catalog changed or that the real Hub/Security combined acceptance gates passed.
