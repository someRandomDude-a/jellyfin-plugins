# Route authorization tracing and test coverage

## Scope and setup

Run `node scripts/audit-routes.mjs` from the repository root. The current inventory
records MVC actions, catch-all adapters and minimal-handler templates across all six plugins and
the shared Security/Notifications modules. Eight legacy lifecycle controller files
are excluded by the active lifecycle project and are explicitly listed as inactive;
the example consumer is not shipped. Inventories under `artifacts/discord-embedded-*`
and `artifacts/route-audit/` are historical and predate removal/ownership changes.
The fresh source inventory is `artifacts/commands-studio-menu-final-routes.json`:
216 declarations/templates and eight excluded inactive legacy files.

This is a source trace, **not a count of executed runtime authorization tests**. It records
route declarations, class/action overrides, source lines and guard-call leads. Minimal
handlers are parameterized templates; the installed adapter/prefix determines the
actual route. Follow each named helper and validate its effects on a disposable server.

## Authority trace by route family

| Family | Authorization path and tests to retain |
|---|---|
| Hub administration, groups, invitations, Seerr and domain proxy | Native live administrator/elevation checks through `HubHost` and current native user state; appropriate command authorization, revisions and durable receipts. Domain proxy allowlists do not replace the domain's own checks. Test anonymous/ordinary/API-key, retained demoted/disabled sessions, stale revisions and replay/conflict. |
| Hub integrated invitation redemption | Public lookup/redeem/resume are intentionally anonymous and token/claim/rate/origin bounded. Native creation ownership, reservation limits and safe provisioning remain authoritative. Static public pages contain no account state. Test invalid/expired tokens, interruption, duplicates and takeover boundaries. |
| Hub Discord link and self-service | Native signed-in account/session; reviewed account ID and explicit link confirmation. Bridge's optional policy checks the actual native user before confirmation. Recovery lookup does not authorize completion: same-browser/same-account Security verdict remains required. Scoped native reset/proof runtime evidence is recorded in current review; human Discord remains separate. |
| Notifications private inbox/preferences/actions | `EndpointAdapter` hosts shared handlers at the owned prefix. Native `User`/management checks, current administrator/media/domain access, recipient ownership and live command revision/snapshot apply. Mutations enforce native same-origin/session policy; history/polling does not grant domain consent. |
| Notifications producer, live and asset routes | Public assets/service worker contain no private state. Producer routes authenticate scoped producer credentials, not request-body actor IDs. Live streaming uses a short-lived one-use bound ticket and current user/access checks; anonymous endpoint metadata is not an authentication bypass. Test scope misuse, expired/reused tickets and cross-account reads/actions. |
| Native Security management and signed ingress | Administrator/current-caller client, old-policy proof/confirmation where applicable, live target guards and no uncertain mutation retry. The webhook is token-free exact-byte HMAC with bounded replay/quarantine/receipt state. No broad native Security middleware exception. Full native transport/recovery tests stay late-stage. |
| Bridge administration and connection tests | `RequiresElevation` plus fresh native administrator checks, API-key denial and same-origin body filter. Transport configuration is operational with or without Hub: enrolling a native factor does not add a fresh-proof or typed-confirmation requirement. This does not bypass native authentication/elevation or protect other actions less strictly. Revisioned writes preserve explicit denials on legacy omission; denied/stale writes must not alter settings. Connection test is read-only and sends no Discord message. |
| Notifications library/defaults/studio administration | Native administrator/origin and management authorization, revisioned writes, bounded library selection and declarative text/image/font geometry. Examples never send; layouts cannot modify recipients/actions or inject executable markup/external URLs. |
| Bridge gateway commands and portable sends | `InteractionGate` checks configured guild, bot availability, durable interaction admission and duplicates before dispatch. Verified Hub identity precedes per-user action policy; Hub and SyncPlay independently retain native authority. Channel allowlist/disclosure and one-shot delivery receipts are separate checks. Trusted in-process delegates are not a sandbox for malicious plugins. |
| Queue ordinary users and admin adapters | Native live user, account authority, target visibility/verified Seerr ownership; own preferences/consent/Undo and separate live administrator routes. Current proposal revision/snapshot, activity, required viewers, approval and delay are rechecked. Notifications provider failure blocks new intake, not retained-request safety UI. |
| Queue presentation and removal execution | Admin/native origin gates, bounded renderer and explicit presentation writes. Only Queue claims and sends destructive Seerr work; uncertain outcomes are not resent. Seerr's deletion report is trusted—no independent physical-file-removal test is required. |
| Automatic Deletion Requests | `NativeAccess.Require` checks live native administrator status/origin, not stale claims alone. Connection tests/import and dry-run/evidence paths are bounded; Arr reads stay GET-only. Rule submission uses current requester/authorizer plus Queue's own authority, never consent or an executor. |
| Private SyncPlay authenticated and owner API | Native authentication and exact user/device/session binding, current media/SyncPlay permissions, party admission and owner/admin checks. Private IDs/list visibility are not join credentials. Adapter writes retain revision/receipt and administrator-exclusion rules. |
| Private SyncPlay guest and public player resources | Public pages/assets do not carry native credentials. HTTPS/same-origin redemption requires an explicit invitation; opaque scoped guest leases authorize only current party state/generated HLS/captions. Every delivery rechecks party/lease/media/permissions, including after asynchronous work. Guests cannot acquire native playback commands. |

## Runtime acceptance

Run final Linux root tests and new Linux packages, then use
`scripts/integration.mjs --permissions-check --wiring-check --v1-flows --library`
with those packages on a new disposable fixture. The permission matrix covers all
installed plugin admin entry points, anonymous/ordinary/API-key denial, demotion,
disable and personal-route restrictions. The Discord additions verify policy
persistence, unknown/legacy clients, stale/unauthorized updates and the native link
confirmation ceiling. Component/unit suites cover the detailed domain routes.

Passing these selected checks is not exhaustive fuzzing or a universal race guarantee.
Extend runtime cases for new routes; do not treat the inventory's guard candidates as
proof that a helper is called on every branch. Actual run results and failures belong
in current status, with final native Security checks deliberately later.

## References

- [Testing](testing.md) · [Discord integration](discord-suite-integration.md)
- [Safety and permissions](safety-and-permissions.md)
- [Current evidence](review-status.md) · [Remaining v1 gates](v1-release-gates.md)
