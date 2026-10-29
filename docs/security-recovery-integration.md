# Existing native Security sessions for Hub recovery

## Dependencies and setup

Install Account Hub and a supported native Jellyfin Security release using the
[installation guide](../README.md#installation-and-upgrades). Discord-issued
recovery links additionally require Discord Bridge and a verified account link.
Configure the trusted HTTPS public URL in Hub and follow normal native Security
enrollment/sign-in. Keep a verified session in the same browser for the same
account: this is **not signed-out account recovery**. No Security patch or new
reset-time factor code is required or authorized.

## Current contract — owner decision, 2026-10-27

Hub requests **no fresh 2FA code**. The browser must already hold a current native
session for the reset-link account, and Jellyfin Security must mark that exact
access token verified and not blocked. The owner explicitly selected **Follow
Security's verdict**: configured LAN, trusted-device, Quick Connect, app-password
or other native bypass paths count if Security marks their session verified.
Hub does not infer verification from HTTP success, enrolled-factor counts,
`MyStatus`, browser flags or a per-user step-up window.

1. Hub resolves the reset credential exclusively from its server-side record.
   The browser cannot choose the target user or substitute an administrator/API key.
2. The authenticated readiness endpoint validates that same account, native
   session verdict, known unlocked native status and current local-password policy
   before the page shows its two password fields.
3. Completion repeats those checks under the existing reset-effect fence, before
   consuming the still-valid link or running password/session effects. It reads
   the token verdict before and after native `MyStatus`; missing, incompatible,
   unknown, blocked or unreadable verification denies access. The administrator
   mirror does not bypass this guard.
4. Successful reset changes the password once and revokes native sessions, while
   preserving native factors/trusted-device records. Denied authorization leaves
   the link unused. Claimed or uncertain password writes are never replayed.

A user without a remaining verified browser session cannot complete this recovery
flow. Discord delivers the link, not an additional factor or a login session.

## Read-only, source-reviewed integration

`shared/Jellyfin.Security.Api/SessionVerification.cs` resolves the running native
plugin's registered public `ChallengeStore` service across its assembly load
context and calls only public `IsTokenVerified(string)` and
`IsTokenBlocked(string)`. It rechecks blocking after reading the marker. A missing
service, changed method contract or exception yields unknown and fails closed.
Normal builds resolve current stable Security automatically. Approval of its
exact binary version requires unchanged reviewed upstream C# source contracts,
the discovered official asset's SHA-256 and metadata-only inspection of that
asset. ZIP filenames/source versions can differ from the binary revision; neither
is guessed or hardcoded as the current test target. Current native installed
acceptance remains mandatory. Historical v2.6.4 results do not certify v2.6.5.

No private reflection, factor models/files, encryption keys, native writes,
verification markers minted by Hub, or Security project patches are involved.
This depends on a reviewed public service contract, not a documented recovery HTTP
extension. Changed upstream source or missing/changed public methods fail closed;
version discovery alone never grants verification. See [testing](testing.md).

## Superseded designs and evidence

The earlier fresh-code implementation and `artifacts/reset-twofactor-verified-*`
evidence describe a superseded requirement. The subsequently proposed signed-out,
Security-owned recovery-factor hook was **rejected by the owner** and is not
implemented. Review of upstream v2.6.5 at
`548c19d5e2e091f78d45784a416ad83e825a6152` found no recovery-specific public hook;
its own email password-reset service is a separate flow and is not reused by Hub.
No upstream or sibling repository was edited.

Current checks use `scripts/integration.mjs --reset-session` on a new disposable
native Security fixture. Final evidence and remaining coverage limits are tracked
in [review status](review-status.md); previous human reset acceptance predates
this guard and must not be presented as current-session certification.

## Security alerts and transport validation

Native Security remains the authentication/factor owner. Its supported administrative
actions use current native-session authorization, live target/last-admin safeguards,
explicit confirmation and the applicable fresh-proof gate; this is distinct from
the existing-session reset policy above. Unknown status or a failed proof cannot
downgrade authorization. Preserve native raw marked 403/401 behavior.

Keep native alerts enabled while validating the shared HMAC receiver at the trusted
existing listener/BaseUrl: `POST /AccountHub/api/v1/security/jellyfin-security`.
Configure matching protected signing secrets through their owners. Hub derives its
separate privacy hash key from its protected command-fingerprint key; do not rotate
or replace that root as a transport troubleshooting shortcut. Never put secrets or
a standing Jellyfin token in the URL/proxy.
Require `X-2FA-Timestamp` and `X-2FA-Signature: sha256=<hex>` over timestamp bytes,
`.` and the exact raw body. Validate before parsing; retain TLS checks, explicit
private-target policy, a 64 KiB body bound and ±300-second skew validation.

Durable accepted/duplicate receipts precede 202; invalid signatures return 401,
oversized bodies 413, rate limits 429 and unavailable storage 503. Incompatible
authenticated events are bounded hash-only quarantine, never replayed after an
upgrade. Pairing codes, raw prose, nested secrets and raw IPs are not external
notification content. `test` is setup/audit feedback, not an incident alert.

Native `WebhookTest` may exercise all configured channels and have a long deadline;
correlate per-channel results with a durable receiver receipt rather than treating
a timeout as proof of non-delivery. `native`, `hub` and `both` retain explicit routing
semantics; Hub never silently disables native channels or deduplicates independent
senders. Sender outage/retry/backfill and exactly-once delivery are not guaranteed.
Rotate both sides' secrets deliberately, preserve dedupe state, and test rejection
of the old secret; no dual-secret grace override is implied.

Disposable acceptance must cover trusted/untrusted TLS, private-target false/true,
DNS/proxy/BaseUrl, token-free ingress while another session is pending 2FA,
raw-byte tamper/replay/rate limits, quarantine capacity, store/sender outage/restart,
redaction, marked versus unmarked errors, native factor/recovery/trust actions and
user-deletion residue observable through supported interfaces. Do not read private
Security records to manufacture cleanup evidence or exempt the entire Hub route tree.
Sanitize all evidence. Installed HMAC/TOTP checks do not close every real native
sender/proxy/TLS/deletion-cleanup or human recovery boundary.

## References

- [Account setup and recovery limits](account-management.md)
- [Discord command setup](discord-suite-integration.md)
- [Native Security upstream](https://github.com/ZL154/JellyfinSecurity)
- [Testing](testing.md) · [Current verification](review-status.md)
