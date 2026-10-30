# Discord Bridge

One bot runtime for suite Discord communication, packaged for the current approved Jellyfin target. No Discord role mapping or administrative slash commands. Account Hub owns verified Discord↔Jellyfin links and password recovery; Private SyncPlay owns invitations and native party/session authorization.

## Dependencies and installation

Use the [combined platform catalog](../../README.md#installation-and-upgrades).
Basic transport needs no other suite plugin. Account commands need Hub; party
commands additionally need Private SyncPlay. Notifications owns shared delivery
routes. A Discord application/bot and the selected guild/channel permissions are
required for Discord features; secrets must never be pasted into reports.

## Setup and features

1. Install Discord Bridge by itself or with any other suite plugins; restart Jellyfin. Account Hub and Private SyncPlay are optional feature providers, not startup/configuration dependencies.
2. In Discord, create a bot and invite it to one guild with `bot` and `applications.commands` scopes. Give only the channel permissions needed to view/send messages; no Administrator permission or privileged message-content/member intents are required.
3. Open **Dashboard → Plugins → Discord Bridge**. Configure guild ID, token, and optional channel allowlist. A public **HTTPS** Jellyfin URL including BaseUrl is needed for watch-party links, not plain library announcements. Settings require a live elevated native administrator; operational transport settings do not add a fresh-factor prompt, while native Security's global enforcement still applies. Changes retain the protected revision and local non-secret configuration audit. Blank token preserves the encrypted credential; clearing is explicit. Never paste a token into a screenshot or issue.
4. Enable the bot. Registration/reconnect health appears in settings. Set the matching public URL in Account Hub for link/recovery pages.

Commands: `/help`, `/link`, `/unlink confirm:true`, `/reset`, `/status`, `/invite [uses] [expires-in-minutes]`, `/watchparty [user]`.

**Discord permissions** provides per-action defaults and searchable native-user
Allow/Deny/Inherit overrides. It controls account commands, delegated invitation
creation, party/guest invitations and DM/channel sharing. Account invitations and
guest invitations default to Deny; channel sharing defaults to Allow, subject to
approved channels and preserved administrator overrides. All edits remain drafts until
Save; resetting a user's policy restores inheritance, not native permissions or
consent. Saved overrides for unavailable users remain visible and are not silently
deleted. At most 100 explicit user policies are supported; the selector shows the
first 500 native users plus saved overrides. No Discord-role grants are supported.

Allow never bypasses Hub invitation delegation/quotas or verified-session recovery,
native library access, SyncPlay guest policy or channel approval.
Initial link challenges do not prove identity; final linking enforces the signed-in
native user's policy. Discord Activities and embedded playback are removed.
Account signup defaults/maxima and party use/expiry/guest defaults have separate
sections in Bridge's own settings. Hub retains additional signup-owner ceilings,
creator delegation, templates and quotas.

Settings show integration status in a table and hide unavailable account/party controls. Generic bot transport remains configurable without Hub or SyncPlay. Optional Hub audit forwarding does not gate configuration.

The public URL is an optional override: leave it blank to reuse Account Hub's configured HTTPS URL, including BaseUrl, for party links. Notifications routes Deletion Queue, automation and other registered notices through the same bot. Stable notification delivery IDs have durable duplicate/uncertain-send protection; permanent Bridge errors are not retried as temporary outages. See [suite ownership, setup, delivery guarantees and verified limits](../../docs/discord-suite-integration.md).

`/watchparty` requires a verified enabled Jellyfin account and automatically selects its most recently active real native playback device. Default delivery posts the link in the current approved channel. Optional `user` sends a DM instead. Anyone receiving a bearer invitation can redeem it subject to native policy. Guest access defaults off, one use, 30-minute expiry; administrators can configure these defaults without bypassing native limits. No device/channel/confirm-share options or subcommands remain.

## New episodes and movies

Library announcements require Notifications, not Hub, and are disabled by default. Select native Jellyfin libraries and approved channels in Bridge, acknowledge metadata disclosure, then enable episodes/movies. Notifications is the only native library observer; Bridge retains its approved-channel transport/outbox, with no fallback observer. Private notices are configured in Notifications and respect current native access. See [privacy and uncertainty limits](../../docs/library-notifications.md).

## Recovery and limitations

Back up `plugins/configurations/DiscordBridge/discord-bridge.db` and its original `keys` directory together while Jellyfin is stopped. Wrong/missing credential keys fail closed. Interaction claims survive restart and uncertain actions are not automatically replayed.

Discord may reject DMs due to recipient privacy settings. Delivery failure does not prove an invitation was never created; inspect the native party before requesting another. See [validation limits](../../docs/review-status.md) before live use.

## References

- [Full configuration and delivery contract](../../docs/discord-suite-integration.md)
- [Account commands and recovery](../../docs/account-management.md)
- [Library privacy](../../docs/library-notifications.md)
- [Route authorization trace](../../docs/route-authorization-review.md)
- [Current verification](../../docs/review-status.md)
