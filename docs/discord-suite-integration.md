# Discord Bridge integration

## Dependencies and setup

Install Discord Bridge from the [platform catalog](../README.md#installation-and-upgrades)
and restart Jellyfin. Create a Discord application/bot and invite it with `bot` and
`applications.commands` scopes and only the required channel permissions. Configure
it in **Dashboard → Plugins → Discord Bridge**. Never disclose the bot token.

Generic transport works without Hub. Account commands require Account Hub's verified
identity provider; party commands additionally require Private SyncPlay. Notifications
owns shared notification routes. HTTPS public URLs are required for account/party
links, not plain library announcements. Discord Activities and embedded video are
removed; watch parties open the native Private SyncPlay invitation link.

## Overview and ownership

Discord Bridge owns the single bot connection and Discord transport. The other
plugins retain their native authorization, records and safety decisions.

| Owner | Integration |
| --- | --- |
| Notifications | Routes registered plugin events to approved channels or opted-in, currently linked users. Channel messages remain generic; DMs recheck native media access, account status, link and preferences. |
| Deletion Queue | Publishes consent/review/status notifications through Notifications. Discord does not approve, consent to or execute deletion. |
| Automatic Deletion Requests | Evaluates rules and submits through Deletion Queue. Its registered notices can use Notifications routes; Bridge does not evaluate rules or call Arr deletion APIs. |
| Account Hub | Owns verified Discord links, account recovery and invitations. Discord roles never grant Jellyfin access. |
| Private SyncPlay | Owns sessions, invitations, guest permissions and media authorization. Bridge requests an invite only for the verified user's real active device. |
| Library notifications | Notifications alone observes additions, owns private audience/configuration and forwards audience-free notices to Bridge's approved-channel outbox. No Hub or Bridge observer fallback. |

## Configuration

Bridge's **Discord permissions** editor uses per-action defaults and native-user
Allow/Deny/Inherit overrides. It does not use guild roles or delegate native admin
rights. Account invitation creation and guest invitations default to Deny.
Channel sharing defaults to Allow but saved administrator denials still win;
approved channels and native Hub/SyncPlay authority remain mandatory.
See [setup](../plugins/discord-bridge/README.md).

Configure bot token, guild and approved channels in Discord Bridge. Configure
notification sources/categories and delivery routes in Notifications. Missing Hub
disables linked-account features, not generic channel delivery; missing SyncPlay
disables party features, not the bot. Bridge settings show the optional
Notifications integration separately.

Bridge's public HTTPS URL is an optional override. With no override, party links
use Account Hub's administrator-configured public URL,
preserving BaseUrl. Resolution uses a framework-only optional delegate, not an
implementation reference, browser Host header or arbitrary forwarding header.
An invalid explicit override fails closed. Account linking/recovery continue to
use Hub's own configured URL; Bridge never rewrites it or native authentication.

Behind a reverse proxy, configure that proxy in **Jellyfin → Networking → Known
proxies** so native middleware can establish the original HTTPS scheme. Strict
same-origin account POSTs deliberately reject mismatched HTTP/HTTPS origins; do
not bypass this by trusting arbitrary forwarded headers or opening proxy ranges.
The disposable Cloudflare-tunnel harness trusts only `127.0.0.1`, its explicit
shared-network-namespace upstream. That setting is not applied to production.

Account commands include `/link`, `/unlink`, `/reset`, `/status` and the separately
administrator-delegated `/invite`. The global account-command switch is not an
invitation grant: Hub also checks the current native account/link, explicit
creator allowlist, default template, effective sign-in policy and persisted daily
limit. See [account management](account-management.md). Account failures use fixed
privacy-safe guidance rather than forwarding provider errors. Request cancellation
and the bounded request deadline propagate to Hub; uncertain work is not replayed.

`/invite [uses] [expires-in-minutes]` creates account signup invitations. Discord
Bridge exposes defaults and maxima in its own settings; Hub independently enforces
its owner-side maxima, template/delegation and daily invitation quota. The tighter
ceiling wins. `/watchparty [user]` chooses the caller's most recently active native
device, posts the native link in the current approved channel by default, or DMs
the explicitly selected @user. Party use/expiry/guest defaults live in Bridge;
native party and guest permissions are never granted by those defaults.

Open Jellyfin on a real playback device before using `/watchparty`; the command
prefers an active playing session, then the most recently active device. Known
missing-device/provider or policy failures use fixed safe guidance. An ambiguous
invitation or delivery result is not automatically resent: inspect the selected
destination and native party before trying again.

Recovery-link possession alone cannot change a password. Hub requires this
browser's current native session for the linked target account, with its exact
token marked verified and not blocked by native Security. Configured native
bypasses count; there is no fresh-code prompt or independent factor-count gate.
Completion repeats authorization before claiming the reset or changing the
password. Missing/unreadable verification and unknown/locked account status block
completion irrespective of the administrator step-up mirror. There is no
signed-out recovery fallback; see [account management](account-management.md).

Invoke commands inside the configured Discord guild. Recovery links are sent by
DM, but a bot DM or another guild is not an authorized command context. Those
contexts now receive explicit guidance rather than the ambiguous “unavailable or
already handled” response. Duplicate gateway events are ignored without sending
a competing response; the original durable claim owns dispatch and acknowledgment.
Completed/uncertain receipts remain consumed after restart. Admission uses SQLite's
statement-local `changes()` result rather than a provider aggregate row count.

## Delivery semantics

Notifications sends stable delivery IDs. Bridge now stores bounded durable
receipts containing only ID, SHA-256 payload fingerprint, state and creation time:
never message text, native credentials, recovery links or invitation tokens.
Completed delivery retries are acknowledged without another network send. Reusing
an ID for different content is rejected. Claimed/interrupted/uncertain sends are
not replayed automatically. Offline work does not consume a new receipt.

Receipts are bounded at 100,000 and are not silently pruned into replayable IDs;
capacity exhaustion blocks new claimed delivery. Back up the Bridge database
with its original keys. Legacy requests without a delivery ID retain their old
contract, including owner-controlled secret delivery; this is not a claim of
provider-level exactly-once delivery for all Discord operations. Standalone
Notifications now preserves Bridge's permanent-versus-retryable error policy.

## Verification

`artifacts/discord-suite-verified-{tests,package,installed,standalone,live}.log`:
993 .NET tests, root JavaScript/release checks, all six Linux amd64 packages,
installed native-theme/authorization/restart/database/recovery checks, and
Discord Bridge + Notifications without Hub passed. Authorized live testing used
only the configured `.env` test guild/channel: gateway connection, slash-command
registration, exactly one episode/movie card each, rescan deduplication and real
Notifications → Bridge channel delivery passed with no mentions/private audiences.
Owned messages and credential-bearing fixture state were cleaned up.

Earlier account-focused human acceptance (before the verified-session guard) passed in
`artifacts/discord-visible-status-live.log`: consenting-user `/link` confirmation,
`/status`, private `/reset` delivery, completed native password reset, old-password
denial and prior-session revocation; a subsequent native sign-in also succeeded.
It did not include native factors and does not certify the current reset guard.
The fixture, tunnel and owned messages were cleaned up. Human delegated `/invite`,
watch-party redemption remain
separate acceptance work. No native authentication bypass or destructive Discord
commands were added, and no release was committed or published.

Current human invitation acceptance is deliberately deferred until the owner says
they are ready, after non-human gates close. The implemented but unstarted
`--human-invitations` mode requires explicit interactive/live authorization and a
unique private readiness file. It checks actual handler outcomes (including an
over-limit rejection), the exact linked native identity and two completed signups
from a two-use/60-minute invite. Native channel/DM party redemption, third-use
denial and recipient privacy need explicit human confirmation; counters are not
substitutes. Never launch its server/tunnel merely to prepare for that later test.

## References

- [Bridge setup](../plugins/discord-bridge/README.md) · [Dependency matrix](dependencies.md)
- [Account management](account-management.md) · [Security/recovery](security-recovery-integration.md)
- [Library disclosure and privacy](library-notifications.md)
- [Test credentials](discord-test-credentials.md)
- [Current acceptance](review-status.md)
