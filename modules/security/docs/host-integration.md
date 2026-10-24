# Host integration contract

## Registration

Reference `src/AccountHub.Security/AccountHub.Security.csproj` from the single Account Hub plugin, then:

```csharp
services.AddAccountHubSecurity();
services.AddScoped<ISecurityHubHost, YourHubSecurityHost>();
services.AddSingleton<ISecurityReceiptStore, YourNotificationsReceiptStore>();
services.AddSingleton<ISecurityIngressBudget, YourBoundedIngressBudget>();
// Add SecurityController's assembly to the existing MVC application parts if not discovered:
services.AddControllers().AddApplicationPart(typeof(SecurityController).Assembly);
```

Jellyfin supplies `IPluginManager`. Use the host's common model-validation/error envelope configuration; MVC's default automatic 400 otherwise uses ProblemDetails. Keep Hub-owned pending errors at 403; Security middleware can intercept earlier with its raw marked 403. Do not install a broad middleware bypass.

### Mandatory `ISecurityHubHost`

- Resolve current caller through Jellyfin `IAuthorizationContext`; verify active administrator and elevation **every request**, including cached reads. Reject API/service keys and cookie-only mutations. Enforce same-origin management policy.
- Construct request-scoped `CallerSession` using that user's token and a **trusted configured same-origin Jellyfin base URI ending in `/`**, including BaseUrl. Never use untrusted Host/Forwarded headers to choose an outbound target. Never persist or log the session. The typed client disables redirects/cookies/loggers; retain TLS validation and do not add retry middleware.
- Provide current persisted settings/revision. Defaults: `Destructive`, `manual`, `native`, `auto`. Encrypt webhook/PII hash keys through Hub's existing secret store; no secret readback.
- Implement `ExecuteAsync` with the **shared durable command gate and audit writer**. Scope command ID by actor + operation; canonical payload hash includes target IDs, new settings, confirmation decision, and revision but **never proof or session**. Completed identical retry returns stored result without invoking callback or asking for proof. Conflicting retry returns `command_conflict`.
- Hold a revision/settings/command lease through authorization and execution. Pass the current **old** settings to callback. Check target existence, self/last-admin guards and idempotency before proof. Persist sanitized audit intent before mutation. Audit authorization method, fallback reason, verified time, actor/action/target/outcome; record failure/denial without serializing request objects.
- Callback result is not cross-plugin atomicity. Persist uncertain/partial results as remediation pending; cancellation/crash after outbound send must not release an ambiguous command for blind retry. Settings changes occur in the host transaction after authorization; defaults and mirror changes are audited. No proof cache or reusable authorization ticket.
- Apply the command authorizer immediately before core commands too. `Everything` audit reads/exports require POST-body proof; GET must reject before data access. Discord self-service reset is token-authorized and must **not** use this admin gate. This module does not implement reset/invite/policy/CSV logic.

`CommandDescriptor`, `AuthorizationEvidence` and receipt interfaces are same-Hub module integration seams, **not changes to `AccountHub.Api.1`** and not cross-ALC Security contracts. Core owner must adopt/reconcile these before freezing P0; no claim that the absent shared contracts already exist.

## HTTP mapping

All routes below are beneath `/AccountHub/api/v1/security` and use the common `{apiVersion:"1",ok,data|error}` envelope. Controller outputs force camel-case independently of Jellyfin media-type profile. Admin elevation + host session validation protect all routes.

| Method | Route | Upstream / behavior |
|---|---|---|
| GET | `status` | Manager metadata, persisted integration settings; no Security call |
| GET | `users?skip=0&limit=50&refresh=false` | `GET TwoFactorAuth/Users`; safe nullable status fields |
| GET | `users/{id}/summary` | `GET TwoFactorAuth/Users/{id}/Summary`; only user ID and trusted labels/dates |
| GET | `devices?skip=0&limit=50&refresh=false` | `GET TwoFactorAuth/AllTrustedDevices`; safe DTO |
| POST | `devices/{userId}/{recordId}/revoke` | Confirm, fresh target listing, proof as required, native DELETE **without proof body** |
| POST | `users/{id}/force-logout` | Confirm + proof as required, native ForceLogout |
| POST | `webhook-test` | Confirm + config-risk gate; invokes native test of **all channels** |
| PUT | `settings` | Only Hub settings; authorize mirror lowering as destructive under **old level** |

The POST revoke wrapper is the additive alternative allowed by handoff §7.2, **not** a silently repurposed upstream endpoint. The specified Hub DELETE route is intentionally not exposed until DELETE-body proxy support has been verified. Bind UI only to the documented POST shape. No proof in query strings or separate proof status endpoint.

Command example (values illustrative, not real credentials):

```json
{
  "commandId": "39114c63-be25-46c7-820e-e82828ffb5ac",
  "revision": 1,
  "confirmation": "REVOKE TRUST a1020eab-afd6-447a-9296-05080b322734",
  "stepUp": { "code": "user-entered-fresh-code" }
}
```

ForceLogout confirmation: `FORCE LOGOUT {target-guid}`. Test confirmation: `TEST ALL SECURITY CHANNELS`. Settings fallback confirmation: `CHANGE SECURITY SETTINGS`. Revoke record ID is selected against a **fresh upstream list**, never a browser label/deviceId. ForceLogout response describes upstream-reported effects, not independently verified total revocation. No automatic mutation retries.

Display cache: 10 seconds, 8 MiB budget, actor/origin/route scoped; compatibility checked before cache reads. Lists are fetched upstream as complete bounded responses (4 MiB), then paginated. Every admin read still requires host authorization; cache is not an authorization grant. Proof always uses uncached MyStatus. Mutations invalidate display models, including uncertain outcomes. Use `refresh=true` for manual refresh. Do not fetch these lists from bell polling.

## Shared webhook receiver

Notifications owns **one** anonymous HMAC route: `POST /AccountHub/api/v1/security/jellyfin-security`. It must:

1. Enforce application/json, single timestamp/signature headers, no Jellyfin token/cookie authentication, request/connection limits, trusted source IP extraction and bounded per-IP/global rate limits. Don't bypass Security's blocker; sender must omit Jellyfin tokens.
2. Load encrypted configured nonempty shared signing secret and a separate >=32-byte privacy hash key. Do not put secrets in URLs/logs. Call `SecurityWebhook.ReceiveAsync` with exact request body stream, header values, source IP and persisted alert mode.
3. Translate accepted/duplicate receipt to 202, `SecurityFailure` to its status/envelope (401 auth; 400 invalid signed JSON; 408 read timeout; 413 size; 429 limit; 503 durable store unavailable; 409 unavailable quarantine). No raw body in logging/middleware capture. Rate-limit redacted rejection auditing.
4. Implement `ISecurityReceiptStore.AcceptAsync` transactionally in shared Notifications storage. Unique source/body-hash dedupe persists **>=10 minutes across restart** (prefer existing longer ledger retention). Receipt, audit/fanout intent must be durable before acceptance. Capacity must be bounded; do not evict active dedupe records to accept new work. Valid duplicate is no-op. Quarantine must not upgrade into live fanout on duplicate or replay after restoration.
5. Normal receipt contains only sanitized event fields. Mismatch receipt contains only source/body hash/time/compatibility reason, no parsed event. Native mode/test events never alert; test updates receipt-health state. Noise summary events require worker rate/coalescing rules. Admin recipients are resolved and rechecked server-side; username snapshots never establish identity or user delivery. IP HMAC is private metadata, not an external-channel field.

HMAC is timestamp header bytes + `.` + exact raw body; ±300 seconds, lowercase `sha256=` hex, constant-time comparison. Bounded 64 KiB stream, ten-second read deadline and eight concurrent reads. Replay/dedupe storage belongs to the host, not an unsafe second in-memory receiver cache. Managed strings cannot be reliably zeroed; proof object references, UI fields and owned webhook byte buffers are cleared, but do not claim guaranteed heap erasure.

## UI

Serve `web/security-client.mjs` and `web/security-panel.mjs` as Hub-owned same-origin assets. Import and call `mountSecurityPanel(existingTabRoot, window.ApiClient, options)`; call its cleanup on `viewdestroy`. Default `safeActions:false`; explicitly enable only after command/audit/live checks pass. Handle `pending2fa` by pausing the **existing** bell poller, linking to Login/Setup and resuming on verification/focus with backoff. No new global bell or poll loop is created.

The panel uses text nodes, labeled confirmation/password inputs, pagination and no stored browser proof. It does not provide passkey/email proof-acquisition buttons: upstream user binding is source-reviewed, but acquisition has not been live-verified. TOTP/recovery input is the initial UI. The transport accepts a caller-supplied upstream `stepUpToken` for future host acquisition wiring; a deep-link return is never proof.

Bind recent redacted events/receiver health to the **existing Notifications history**, and settings to the existing Hub Settings page. No competing event-history/settings UI or factor configuration is introduced. Browser Modern/Legacy/mobile styling/accessibility and live navigation are still release checks.
