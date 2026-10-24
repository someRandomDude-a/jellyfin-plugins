# Product requirements — owner revision, 2026-10-25

This document supersedes the earlier four-plugin architecture and unified Account Hub plan. It defines the requested target, **not a claim of completed implementation**. The current implementation/packages still require migration and validation against this target.

## Five installable plugins

1. **Account Invitations** (migration from Account Hub): administrator-managed invitation templates, expiration/use limits, revocation, optional approval, safe account defaults/policy, self-chosen passwords, interruption recovery, verified ownership/linking, and invitation-use events. It is not the administration screen for other plugins. Discord Bridge is a required dependency for the Discord-based invitation workflow requested by the owner. Missing dependencies must produce explicit installation/repair instructions, not bypasses.
2. **Private SyncPlay**: private native SyncPlay groups, their own scoped invitation links, account and anonymous guest admission, owner-controlled permissions, revocation, expiration and isolated playback. Discord watch-party commands use this plugin's authority; Discord does not own playback or grant media access.
3. **Deletion Queue**: deletion proposals, access checks, consent/review, status/history, safe execution and notification events. Its only upstream deletion integration is Seerr's supported HTTP API. Ask Seerr to delete the relevant Arr-managed media files; **do not delete the Seerr request or request history**. No direct Sonarr/Radarr client, database access or generic external-command forwarding.
4. **Discord Bridge**: the bot, encrypted bot credential, Discord notification transport, account self-service integration, watch-party creation through Private SyncPlay, and explicitly targeted invitation delivery. No Discord role-to-Jellyfin administrator mapping. An embedded companion/Activity is separate from proof that video plays inside Discord.
5. **Notifications**: separately installable user-relevant inbox, read/dismiss state, preferences and producer contracts. Invitation use, deletion proposals/outcomes and relevant party/account events come from their owning plugins. Filter audience and access at ingestion/read/delivery; shared notifications must not disclose private identities or recipient lists. Discord delivery is delegated to Bridge, not a second bot.

Shared implementation libraries may be bundled by each consumer. Shared services such as Notifications must have their own plugin identity/lifecycle rather than requiring Account Hub. Dependency declarations, catalog packaging and supported Jellyfin installer behavior must be verified; declaring a dependency in documentation does not prove automatic dependency installation works. Never install remote code silently from an arbitrary URL.

## Common configuration

- One authoritative, versioned connection registry for all suite plugins, not independently copied Seerr settings in each plugin.
- Plugins can initialize/read/add their own supported connection entries through a shared contract. Updates must be transactional and revision-checked; one plugin cannot overwrite another plugin's settings or partially rotate credentials.
- Connection identity includes service kind, selected instance, full BaseUrl/proxy prefix, encrypted credential and explicit endpoint/network policy. Public responses expose only redacted metadata/key-configured state.
- Configuring Seerr once makes it usable by every authorized suite consumer. API keys never round-trip through browser fields, logs, audit payloads, URLs or exported diagnostics. Credentials belong in protected server-side storage, not a plaintext common JSON file.
- One-click Enhanced import uses supported public runtime configuration, copies only matching service connection options/API keys, preserves proxy prefixes, and reports imported/skipped/ambiguous entries without returning secrets. No private Enhanced file/database reads. Multiple instances require explicit selection. Do not silently overwrite a configured destination or grant HTTP/private-network exceptions.
- Native-themed connection sections offer bounded, administrator-authorized tests with busy state, inline sanitized results and navigation cancellation. Clearly distinguish saved-connection tests from draft edits and connectivity from actual message delivery. No hidden destructive test writes or blind retries.

## Authentication/onboarding

- Keep onboarding focused: redeem an invitation, safely create/configure the account, then redirect to native Jellyfin login with the server BaseUrl preserved. Jellyfin Security takes over authentication/2FA there.
- Do not build a parallel factor-enrollment/challenge system in Invitations or migrate/read native factor records. A redirect is not proof of successful login and must not grant a session itself.
- Keep native administrator/access controls, account creation safety, identity ownership, invite revocation/use limits and password secrecy. Existing native Security policy is not disabled to make onboarding pass.

## Migration and verification

Preserve existing installation identities, users, groups, invitations, verified links, notification history, keys and pending jobs. Define reviewed migration from old embedded notification storage to the standalone provider; do not run duplicate notification workers/bells or recreate missing stores silently. Retain backward-compatible contracts while consumers migrate, then explicitly retire obsolete surfaces. No destructive migration on production data.

Refresh solution/project boundaries, UI, catalogs, package inventories, dependency checks, release workflows and tests for five plugins. The previous 32-case four-plugin matrix is historical evidence, not target-architecture certification. Five plugins imply 32 subsets (64 with/without Security), including expected missing-dependency behavior; not every subset is a supported fully functional installation.

Use the owner inventory in `test-profiles/owner-server.json` for an additional compatibility profile, preserving enabled/disabled state and using only disposable data. Verify exact sources/checksums/ABI before installing external packages. BaseUrl, native login/Security handoff, guest isolation, deletion preserving Seerr requests, notification ownership and shared-config concurrency all need installed acceptance. No commit, push, tag or publication without owner authorization.
