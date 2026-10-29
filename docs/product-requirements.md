# Product requirements

This guide consolidates the original architecture requirements and the subsequent
Automatic Deletion Requests split. It defines the requested target, **not a claim
of completed implementation**. Six plugins currently ship as local preview packages.
Invitations remain in Account Hub; plugins retain independent connection ownership.
See [current ownership/dependencies](dependencies.md) and [review status](review-status.md).

## Plugin ownership

1. **Account Hub**, including integrated invitations: administrator-managed invitation templates, expiration/use limits, revocation, optional approval, safe account defaults/policy, self-chosen passwords, interruption recovery, verified ownership/linking, and invitation-use events. Invitations are not extracted into another plugin. Discord Bridge is required for Discord-based invitation delivery, not native account administration. Missing optional integrations disable only dependent features, never native authorization safeguards.
2. **Private SyncPlay**: private native SyncPlay groups, their own scoped invitation links, account and anonymous guest admission, owner-controlled permissions, revocation, expiration and isolated playback. Discord watch-party commands use this plugin's authority; Discord does not own playback or grant media access.
3. **Deletion Queue**: deletion proposals, access checks, consent/review, status/history, safe execution and notification events. Its only upstream deletion integration is Seerr's supported HTTP API. Ask Seerr to delete the relevant Arr-managed media files; **do not delete the Seerr request or request history**. No direct Sonarr/Radarr client, database access or generic external-command forwarding.
4. **Discord Bridge**: the bot, encrypted bot credential, Discord notification transport, account self-service integration and native Private SyncPlay links. `/invite` exposes signup uses/expiry under independently editable Bridge defaults/limits and Hub owner ceilings. `/watchparty [user]` posts in the current approved channel by default or DMs @user instead. No Activity, embedded Discord video, device-list command or Discord role-to-administrator mapping.
5. **Notifications**: separately installable user-relevant inbox, read/dismiss state, preferences and producer contracts. Invitation use, deletion proposals/outcomes and relevant party/account events come from their owning plugins. Filter audience and access at ingestion/read/delivery; shared notifications must not disclose private identities or recipient lists. Discord delivery is delegated to Bridge, not a second bot.
6. **Automatic Deletion Requests**: administrator-managed bounded rule evaluation and proposal submission through Deletion Queue. It owns no destructive executor, viewer consent or administrator approval. Preserve the configured requester's own authority; metadata reads from Arr remain GET-only and use the existing protected connection.

Notifications alone observes library additions and owns event selection, library
scope, simulated examples and private recipient checks. Each producer registers
notification defaults/examples; administrators can override enabled destinations,
priority and safe text/image/font layouts in a Canva-like studio. Personal choices
override ordinary server defaults; critical alerts remain mandatory/high priority.
Previews never send. Layouts cannot inject scripts/HTML/CSS/external image URLs or
change actions, authorization or audiences. Hub must not own library configuration
or observation; Bridge retains only explicitly consented channel transport/outbox.

Shared implementation libraries may be bundled by each consumer. Shared services such as Notifications must have their own plugin identity/lifecycle rather than requiring Account Hub. Dependency declarations, catalog packaging and supported Jellyfin installer behavior must be verified; declaring a dependency in documentation does not prove automatic dependency installation works. Never install remote code silently from an arbitrary URL.

## Independent connection configuration

- Each plugin owns its supported connection settings and credential lifecycle; a mandatory shared connection registry is explicitly out of scope. Optional integrations must not make unrelated standalone features depend on another suite plugin.
- Updates remain transactional and revision-checked within the owner. A consumer may use an explicit optional provider contract, but cannot overwrite another plugin's settings or partially rotate its credentials.
- Connection identity includes service kind, selected instance, full BaseUrl/proxy prefix, encrypted credential and explicit endpoint/network policy. Public responses expose only redacted metadata/key-configured state.
- Seerr deletion authority and its connection remain owned by Deletion Queue; automatic rules consume that supported boundary rather than adding an executor or duplicate credential. Other independent plugins retain their own connections. API keys never round-trip through browser fields, logs, audit payloads, URLs or diagnostics; use protected server-side storage.
- One-click Enhanced import uses supported public runtime configuration, copies only matching service connection options/API keys, preserves proxy prefixes, and reports imported/skipped/ambiguous entries without returning secrets. No private Enhanced file/database reads. Multiple instances require explicit selection. Do not silently overwrite a configured destination or grant HTTP/private-network exceptions.
- Native-themed connection sections offer bounded, administrator-authorized tests with busy state, inline sanitized results and navigation cancellation. Clearly distinguish saved-connection tests from draft edits and connectivity from actual message delivery. No hidden destructive test writes or blind retries.

## Authentication/onboarding

- Keep onboarding focused: redeem an invitation, safely create/configure the account, then redirect to native Jellyfin login with the server BaseUrl preserved. Jellyfin Security takes over authentication/2FA there.
- Do not build a parallel factor-enrollment/challenge system in Invitations or migrate/read native factor records. A redirect is not proof of successful login and must not grant a session itself.
- Keep native administrator/access controls, account creation safety, identity ownership, invite revocation/use limits and password secrecy. Existing native Security policy is not disabled to make onboarding pass.

## Migration and verification

Preserve existing installation identities, users, groups, invitations, verified links, notification history, keys and pending jobs. Define reviewed migration from old embedded notification storage to the standalone provider; do not run duplicate notification workers/bells or recreate missing stores silently. Retain backward-compatible contracts while consumers migrate, then explicitly retire obsolete surfaces. No destructive migration on production data.

Refresh solution/project boundaries, UI, catalogs, package inventories, dependency
checks, release workflows and tests for the current six-plugin set. Six plugins
imply 64 subsets (128 with/without Security), including expected missing-dependency
behavior; not every subset is a fully functional installation. Earlier four-/five-plugin
matrices are historical evidence, not current architecture certification.

Use the [owner inventory](../test-profiles/owner-server.json) for an additional compatibility profile, preserving enabled/disabled state and using only disposable data. Verify exact sources/checksums/ABI before installing external packages. BaseUrl, native login/Security handoff, guest isolation, Seerr-authoritative removal, notification ownership and connection-update concurrency need installed acceptance. Local validation targets Linux; GitHub owns Windows/ARM jobs. No commit, push, tag or publication without owner authorization.

## References

- [Setup and dependencies](dependencies.md) · [Account workflows](account-management.md)
- [Safety and permissions](safety-and-permissions.md) · [Existing-session recovery](security-recovery-integration.md)
- [Current evidence](review-status.md) · [Remaining release gates](v1-release-gates.md)
