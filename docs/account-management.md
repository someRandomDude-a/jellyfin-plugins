# Accounts

## Dependencies and setup

Install Account Hub from the [combined platform catalog](../README.md#installation-and-upgrades)
and restart Jellyfin. Configure accounts through **Dashboard → Plugins → Account Hub**
using a native administrator session. Do not install legacy User Manager alongside Hub.
Notifications supplies the shared inbox; Discord Bridge is needed only for Discord
delivery/commands. Supported native Security is required for its factor/session
workflows; Seerr lifecycle features require a configured Seerr connection.

## Overview

Hub provides account onboarding, access templates, managed policy changes, audit
and verified identities. It does not replace native authentication, grant removal
consent through templates or own other plugins' administration screens.

## Access and invitations

- **People** supports search, individual access changes, enable/disable, session revocation, Discord link status and administrator-confirmed **Unlink Discord**, plus selection of up to 100 non-administrator users for bulk access editing. Select the fields to change, preview them, then authorize the apply. Editing a draft invalidates its preview. Managed changes retain rollback snapshots; unrelated native permissions and credentials are not copied or replaced.
- **Copy access to template** creates a group template from the supported access fields of an existing native user. It never copies administrator status, passwords, authentication providers, Discord links or 2FA. Supported fields: playback, remote access, downloading, audio/video transcoding, remuxing, session limit, library access and parental rating.
- **Groups** owns templates, primary assignments, overrides, explicit apply/reset, drift inspection and rollback. Observing drift does not automatically overwrite native changes.
- **Settings** keeps public links and invitation permissions prominent. Temporary native creation, audit retention and Security integration are under **Advanced settings**; an active native-creation override expands that section automatically. Friendly Security choices retain the existing stored policy values and fresh authorization requirements.
- **Invites → Invitation defaults** selects the default template and available Discord permissions. Ordinary invitation requests require separate approval before their credential is issued. Link rotation invalidates the previous link; credentials are disclosed once, not available in history or completed retries.
- Provisioning creates the reserved native identity disabled, establishes the invitee's chosen password, applies managed access, then enables the account. Uncertain operations require inspection/resume rather than blind creation retries. New accounts are non-administrators and hidden from public login tiles.

## Selection and confirmation

Account selection includes a page-level select-all checkbox that excludes
administrators. Access editing opens a centred modal; drag its heading to move it.
Confirmation uses review buttons, not a typed phrase. Required verification codes
and native authorization remain enforced.

## Discord

Discord Bridge owns the only bot. Enable account self-service there, configure a trusted HTTPS public URL, and use `/link` followed by authenticated, explicit Jellyfin confirmation to verify a link.

- `/reset` sends a five-minute, one-time recovery link privately to the linked Discord account. Disabled/unlinked accounts and disabled local-password policy cannot recover this way. Issuance checks the effective native sign-in policy, validates the configured HTTPS URL, and is rate-limited. **Completing a reset requires this browser's existing native session for the same account, with its exact token marked verified and not blocked by Jellyfin Security. No fresh code is requested.** The link alone, an API key, another account's session or a browser verification flag is insufficient. Native configured bypasses count when Security marks the session verified; Hub does not impose a separate enrolled-factor requirement. Missing/incompatible/unreadable verification and unknown/locked account status fail closed, even when the administrator step-up mirror is Off.
- The page checks session readiness before showing two password fields; completion rechecks authorization under the reset-effect fence before consuming the link or changing the password. A user without a remaining verified session cannot complete Hub recovery while locked out. Hub does not read private factor records, create provisional sessions, verify a fresh factor or patch Security. Recovery revokes native sessions but preserves factors and trusted-device records. See [session integration contract](security-recovery-integration.md).
- Unlinking (from Discord or administrator account management) atomically revokes unused recovery links. Recovery issuance rechecks the exact Discord link in the same database transaction; delegated invitation issuance also rechecks it at commit. Already-claimed password effects and required session-revocation work are preserved, never replayed or discarded by unlinking. If private recovery delivery fails or is uncertain, its unused link is revoked; no automatic resend occurs.
- `/invite [uses] [expires-in-minutes]` is denied by default. Administrators enable delegation, choose a default template and select linked native creators. Hub's defaults/maxima permit 1–100 signup uses and 15–43200 minutes; Discord Bridge independently exposes its own command defaults and tighter ceilings in its settings. Both owners' limits are enforced, including live rechecks. Each signup reserves a use. The rolling per-creator limit is 1–20 invitations, not signups; expiry/revocation does not refund it. Local sign-in policy must permit creation. Discord roles grant neither invitation nor administrator rights. Disabling delegation blocks new issuance; existing links must be revoked separately.
- **Discord backup 2FA is deferred by owner choice.** Discord delivers the recovery link, not an additional factor. Native Security owns authentication and the verified-session verdict.

## Removal and automation permissions

Account access templates manage the native access fields listed above. They do **not** copy or grant Deletion Queue permissions, administrator status, native deletion rights, consent or approval. Settings → **Plugin permissions** shows links only for available plugins.

| Control | Owner and scope |
| --- | --- |
| Removal proposals | Deletion Queue's global default plus per-user `Denied`, `OwnRequests` or `AnyAccessibleItem` override. Native item/library visibility and verified Seerr ownership still apply. |
| Viewer consent | Each enabled viewer acts only for themselves. Denying proposal permission does not discard that viewer's playback history or consent protection. Native permission changes cannot manufacture a Yes vote. |
| Administrator approval | A current enabled native administrator approves the current request snapshot. Approval does not replace required viewer consent, playback checks or delays. |
| Automatic request rules | Only a current native administrator session can manage rules; API keys and ordinary users cannot. The stored automation authorizer is bound by the server, not chosen by the browser. A verified singular requester remains the request owner and must satisfy their own current Queue authority and native access. A known disabled requester is rejected, not relabeled as the administrator. Explicit Queue denial of the automation authorizer also blocks intake/execution; an administrator role alone does not override it. |
| Destructive execution | Deletion Queue alone executes through Seerr, with separate opt-in/acknowledgement, live authorization and rule rechecks, unsuspended policy, verified history, stopped playback, consent/approval and accumulated delay floors. Automatic rules submit ordinary requests; they do not grant approval or execute deletion. |

These are separate permission boundaries, not another administrator role supplied by Discord or an Account Hub template. Missing or ambiguous requester metadata is not invented; library automation without a singular verified requester still needs the automation account's own removal authority.

## Scope and verification

These workflows are inspired by jfa-go, not full jfa-go parity. They do not implement its entire notification/provider ecosystem, arbitrary account-setting copying or every bulk account action. Production-handler tests exercise actual link confirmation, private recovery issuance/failed-delivery cleanup, native account/policy gates, unlinking and delegated invitations over real plugin SQLite state with a synthetic transport and native-user adapter.

See [validation status](review-status.md) for current checks and remaining live-service acceptance.

## References

- [Dependency matrix](dependencies.md) · [User creation](user-creation-workflow.md)
- [Security/recovery contract](security-recovery-integration.md)
- [Discord integration](discord-suite-integration.md)
- [Current evidence and limitations](review-status.md)
