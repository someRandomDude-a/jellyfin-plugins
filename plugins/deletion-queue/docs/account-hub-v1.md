# DeletionQueue / Account Hub v1 handoff

Implementation input: `00-program-overview.md`, `01-account-hub.md` §§5/6/14, `02-seerr-deletion-requests.md`, `04-notifications.md`, the pending-2FA rules in `05-jellyfin-security.md`, and the Jellyfin 12 plugin development handoff under the operator's `.opencode/plan` directory. Repository baseline: `1d682617dff1524bd244f3f5817109c1027ef643`. Changes are not automatically committed, tagged or published.

## Ownership and dependencies

DeletionQueue still owns authorization, proposals, consent, protected activity, deadlines, claims, Seerr execution and its authenticated evidence. Account Hub cannot execute media deletion through this adapter. Existing native routes remain supported; the adapter adds its own bulk/reset admin protections without substituting a second policy database.

`src/AccountHub.Api` is a small MIT producer client, with no plugin identity or dependency on the Hub implementation assembly. It resolves **`AccountHub.Api.1`** lazily as **`Func<string, CancellationToken, Task<string>>`**. It sends the plan's v1 JSON envelopes for `hello`, `health`, `notifications.publish` and `audit.publish`. Only framework types cross load contexts. Calls have a three-second cancellation/wait bound. Absent, throwing, incompatible or unhealthy providers are unavailable, never successful delivery. The plugin package explicitly includes `AccountHub.Api.dll`, versioned with the triggering release tag.

No shared SDK/host checkout or frozen Hub package was supplied in the available repositories. This client implements the documented wire contract; Hub/core must reconcile/publish the common SDK/fixtures before combined-system release. This is not an alternative Account Hub host, Discord bot, audit database or Security integration.

## Adapter HTTP contract

Base: `/DeletionQueue/admin/account-hub/v1`. Every operation requires the current Jellyfin **administrator user session**, not a service API key or body-supplied actor. Same-origin mutation, duplicate/unknown JSON rejection, bounded requests and native store validation remain enforced. Jellyfin Security middleware is not bypassed. Hub must satisfy its own mirrored step-up gate before proxying; domain authorization is checked independently.

| Method | Suffix | Body / behavior |
|---|---|---|
| GET | `/capabilities` | Version, feature/field metadata, enum names, defaults/ranges and limits |
| GET | `/status` | Health, mode, host availability, proposal counts and bounded outbox diagnostics |
| GET/PUT | `/settings` | Non-secret integration settings and Seerr association; write-only key replacement |
| POST | `/settings/import-enhanced` | `{revision,commandId}`; one unambiguous server-side import |
| GET/PUT | `/policy` | GET `{revision,defaults,users}`; PUT `{revision,commandId,defaults?,users?:[{userId,policy}]}` |
| POST | `/policy/preview` | `{userIds,changes}`; detached native safety evaluation, no writes |
| POST | `/policy/reset` | `{revision,commandId,scope:"global|users|all",userIds?}` |
| GET | `/users/{id}/effective-policy` | Effective authority, source, preferences, warnings, read-only admin flag |
| PUT | `/users/{id}/policy` | `{revision,commandId,policy:{authority,notificationPreferences?}}` |
| DELETE | `/users/{id}/policy` | Empty body; `If-Match` and `X-Command-Id` headers |

Wire fields are lower camel case, enum names are strings, output GUIDs are dashed; native compact GUID inputs are accepted. Responses are always `{apiVersion:"1",ok:true,data:{...}}` or `{apiVersion:"1",ok:false,error:{code,message,retryable,details}}`, independent of Jellyfin's media-type casing profile. Errors intercepted earlier by Security retain their original status/body.

`revision` is an opaque 64-character token covering native policy, Seerr configuration, integration settings and **all personal preference revisions**. PUT accepts the returned body revision or `If-Match`; supplying inconsistent values fails validation. A stale write returns `409 revision_changed` with the current token in `error.details.revision`. Adapter command receipts are committed with the mutation: identical successful command/actor/input returns the stored result without applying changes again; changed input returns `409 command_conflict`. This is adapter-scoped idempotency; native endpoints retain their original `CommandReplay` semantics.

Per-user `authority:null` inherits. Preferences expose only `delayHours` and `notifications`, not subscription IDs or consent. Bulk changes/reset classify administrators from live Jellyfin accounts inside the mutation. Admin targets are skipped/reported; direct per-user adapter mutations reject them. Global authority changes preserve excluded admins' effective authority with overrides **only when needed**. Global hard safety limits still affect everyone and are disclosed in preview/results. Reset invokes the native policy/preference safety rules, preserves deadline floors, proposals/consent/claims and connection credentials, and leaves factory automatic deletion/acknowledgement false. Unknown/deleted account overrides are not silently discarded.

Settings PUT uses flat optional fields: `notificationMode`, `hubIntegrationEnabled`, `hubFallbackAfterMinutes`, `publishAudit`, `deviceBell`, plus optional `seerr:{baseUrl?,apiKey?,clearApiKey?,confirmClear?}`. Omitted/null API keys preserve the existing key; clearing requires both flags and no replacement key. Seerr server identity is bound to this Jellyfin host. Reads return URL/server ID and `keyConfigured`, never the key. Factory reset preserves the entire Seerr connection to avoid sending retained credentials to a different default URL.

## Persistence and dispatch

The SQL schema remains the strict, plugin-owned journal-only format (`user_version=1`); no unrelated outbox table is created. The **authenticated snapshot payload format advances from 1 to 2** by appending one migration event. Existing encrypted events, keys, installation identity, local notices, consent and claims are retained. Old local notices are not backfilled. Mixed/downgraded payload history is rejected. A DB/head crash still freezes the store without repair. Previous binaries reject payload format 2: rollback requires a stopped, consistent pre-upgrade database + keys + seal backup, not just replacing the DLL.

`QueueRuntime.Write` creates local notices and redacted event intents in the same authenticated transaction as the domain change. There are no outbound calls in the writer. Notification and audit streams have independent UUID event IDs, acknowledgements and retry state. Generic bodies contain no media titles/paths, other viewers' votes/activity, credentials or journal payloads. Policy audit includes non-secret before/after safety summaries; uncertainty additionally creates a critical, admin-only Hub event. Optional preferences do not suppress audit.

One dispatcher probes compatibility/health, leases up to ten events for five minutes, sends after commit, then persists acknowledgements in one batch. Restart recovers expired leases. Backoff is 1m/5m/15m/1h/6h, at most eight attempts. Notification retry schedules are clipped to the configured fallback deadline. Accepted/duplicate notifications stay Hub-owned; rejected/exhausted notifications become local fallback and are never automatically re-published. Audit retries/dead letters are independent. Local source evidence always remains authoritative; critical local uncertainty is visible in the native admin backlog/health even without Hub delivery.

Defaults: `Auto`, Hub integration on, audit on, device bell on, fallback 15 minutes (range 1–1440). `Auto` chooses local for new notices when the cached host is unavailable. `Local` never forwards new notifications. `Hub` retains pending notices until fallback. `Disabled` suppresses optional new notifications, not proposal controls/evidence. Audit is independently enabled. Old local notices do not migrate when Hub later appears. A Hub data wipe does not cause replay/backfill of accepted notices.

Limits remain fail-closed: 32 KiB browser mutations, 500-account classification, 2,000 current outbox entries, 2,000 adapter receipts, 4 MiB snapshots, 256 MiB journal and two-second replay. Terminal delivery metadata expires after 30 days or rolls out earlier under capacity pressure; original encrypted journal events remain retained, notice ownership remains durable, and `deliveryMetadataRolledOff` reports the boundary. Pending evidence is not silently discarded to make space. Saturation requires operator review. This is **not** a validated 10k-pending-event/high-volume archival solution.

An accepted response can be lost forever. Idempotent retries resolve ordinary ambiguity, but after the configured fallback budget a permanently lost acknowledgement can still result in a local notice plus a Hub notice. The v1 contract supplies no cancellation/status transaction to eliminate that possibility. Do not claim exactly-once cross-host delivery under that failure. Never retry destructive media requests because of delivery failure.

## Bell and Security boundary

The local script recognizes marked `403/401` with `twoFactorRequired:true`, clears private content, pauses polling and shows a Login link. Focus/visibility resumes verification; unmarked 401/403 is not classified as pending 2FA. No notice data is saved in localStorage.

**Proposed DOM handshake for the Hub integrator:** a visible Hub bell has `[data-account-hub-bell]`. When that marker is visible, DeletionQueue replaces its own bell with a plain **Deletion queue** shortcut, preserving access to existing local/fallback notices and safety UI. Removing the marker restores local behavior. Disabling `deviceBell` also leaves the shortcut. Hub notification links use the ordinary-user overlay route `#/home?deletionQueueProposal=<guid>`; critical admin links use the existing native admin page.

The supplied plans do not freeze a browser marker/SDK. The marker is a producer-side proposal tested with a browser fixture, **not** proof of a live Hub integration or old-script compatibility. Hub/core must adopt/coordinate this handshake (or agree an additive replacement) before enabling its bell. No other plugin's files or DOM implementation are copied or removed.

## Verification / next-owner gates

- Unit tests cover separate client/host load contexts, absent/incompatible/throwing host, atomic abort/reopen, migration/key preservation, stable IDs, independent acknowledgements, fallback ownership, privacy and admin-safe native policy effects.
- The disposable Jellyfin 12.2 API fixture checks v1 casing/envelopes, stored-result replay/conflict, composite revision conflicts, administrator exclusion/effective-authority protection, header-only DELETE and factory reset/credential preservation.
- Chromium uses synthetic Hub markers and pending-2FA responses; it still exercises real Jellyfin Web login, injected UI, native admin settings, local notice access and session clearing.
- Full Hub installed/disabled/outage acceptance, actual Security 2.6.4 middleware/step-up, crash-killed host acceptance with lost acknowledgement, old DeletionQueue JS coexistence, reverse-proxy/BaseUrl and 10k-pending performance remain **unverified**. No live media deletion or production credentials are used.

Before program release, the Hub/core owner must freeze the SDK/HTTP fixtures, coordinate the browser marker, test the real supported host and Security plugin, review the ambiguity/capacity limits, and execute the program P8/P9 combined-system gates. Do not label the Account Hub program complete from these producer tests alone.

## Worktree verification and packaged handoff

- `dotnet test tests/DeletionQueue.Tests/DeletionQueue.Tests.csproj --no-restore`: **129 passed on Windows and Linux (.NET 10)**, including all prior deletion safety tests. Linux ran in an isolated SDK container with cached packages and no network. A separate genuine-feed restore/build retained NuGet audit and warnings-as-errors.
- `scripts/test-release.ps1 -Runtime all -Tag v0.1.0.6`: all three runtime package checks, SDK assembly versions, catalogs, offline publisher recovery and two-version upgrade tests passed. This tag is a **synthetic fixture input**, not a created/pushed Git tag or published release.
- `test-infrastructure/smoke.mjs`: passed the real Jellyfin 12.2 v1 adapter checks described above. The final run installs the extracted Linux x64 ZIP, not a shared build directory, and verifies installed version `0.1.0.6`.
- `test-infrastructure/browser/integrated-check.mjs` and `native-check.mjs`: passed on Jellyfin 12.2 with synthetic Hub/2FA contracts, including the proposed single-bell/local-shortcut handshake and pending-state resume. These fixtures do not install a real Hub/Security host.
- `test-infrastructure/fault-check.mjs`: passed against the extracted package; a corrupt head seal disables only DeletionQueue, Jellyfin survives worker failure/restart, and no silent state repair occurs. The disposable fixture was then stopped and its test volumes removed.

Local artifacts (ignored, not published): `artifacts/release-tests/8f9a33bdc4f8490d83efa3a07d597778/packages/`.

| Archive | SHA-256 |
|---|---|
| `DeletionQueue-0.1.0.6-linux-x64.zip` | `7470482D25DED24E3DEBB99DEBA6D947F74D9F14546159EF8C9D20FE0E346278` |
| `DeletionQueue-0.1.0.6-linux-arm64.zip` | `EECD50A1D2C67EB01E5443AFB26F73748BCAF374622ECE86A5A8F453A167D133` |
| `DeletionQueue-0.1.0.6-win-x64.zip` | `16674489DD2E8A688606B3F9D67311C5AA03565D676A58C990A930BDCB84373D` |

The source baseline remains the commit at the top of this handoff; implementation changes are in the worktree. Use `git diff --stat` plus the new SDK, adapter, dispatcher, outbox and test files for the changed-file inventory. No changes were made to another project's implementation or to the plan files. A maintainer must review/commit this work and the remaining integration gates before selecting a release tag.
