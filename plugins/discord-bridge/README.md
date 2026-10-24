# Discord Bridge

One Jellyfin 12.2 bot runtime for all suite Discord communication. No Discord role mapping or administrative slash commands. Account Hub owns verified Discord↔Jellyfin links and password recovery; Private SyncPlay owns invitations and native party/session authorization.

## Setup

1. Install Discord Bridge by itself or with any other suite plugins; restart Jellyfin. Account Hub and Private SyncPlay are optional feature providers, not startup/configuration dependencies.
2. In Discord, create a bot and invite it to one guild with `bot` and `applications.commands` scopes. Give only the channel permissions needed to view/send messages; no Administrator permission or privileged message-content/member intents are required.
3. Open **Dashboard → Plugins → Discord Bridge**. Configure guild ID, token, and optional channel allowlist. A public **HTTPS** Jellyfin URL including BaseUrl is needed for watch-party links, not plain library announcements. With Hub installed, its fresh authorization policy applies. Without Hub, native Jellyfin elevation plus conservative fresh Security proof applies; known absence of Security or verified no-factor fallback requires explicit confirmation and is recorded in Bridge's own durable non-secret configuration audit. Blank token preserves the encrypted credential; clearing is explicit. Never paste a token into a screenshot or issue.
4. Enable the bot. Registration/reconnect health appears in settings. Set the matching public URL in Account Hub for link/recovery pages.

Commands: `/help`, `/link`, `/unlink confirm:true`, `/reset`, `/status`, `/watchparty devices`, `/watchparty invite`.

Settings show per-feature dependency health. Missing account/party providers grey out only their command switches, explain why, and provide installation/restart instructions. Generic bot transport stays configurable and operational without Hub or SyncPlay. Optional audit forwarding to Hub never gates standalone configuration.

Watch-party commands require a verified enabled Jellyfin account and one of its real active sessions. Default delivery is an ephemeral reply. Optional `user` sends a DM; optional `channel` requires the configured guild, channel allowlisting and `confirm-share:true`. Anyone receiving a bearer invitation can redeem it subject to the native party policy. Guest access defaults off, one use, 30-minute expiry. User and channel options are mutually exclusive. A bot cannot read arbitrary private channels or infer Jellyfin permissions from roles.

## New episodes and movies

Library announcements work independently of Hub and are disabled by default. Select native Jellyfin libraries and approved announcement channels, acknowledge metadata disclosure to channel members, then enable episodes/movies. There is no existing-library backfill. Hub owns the single capture stream when present; Bridge always owns Discord delivery. In-app notices are separately enabled in Hub and respect each user's current Jellyfin access. See [privacy, personal preferences, health and uncertainty limits](../../docs/library-notifications.md).

## Recovery and limitations

Back up `plugins/configurations/DiscordBridge/discord-bridge.db` and its original `keys` directory together while Jellyfin is stopped. Wrong/missing credential keys fail closed. Interaction claims survive restart and uncertain actions are not automatically replayed.

Actual gateway registration, DM permissions, channel permissions and message delivery require a disposable bot/guild and remain unverified without those credentials. Discord may reject DMs due to recipient privacy settings. Delivery failure does not imply an invitation was never created; inspect the Jellyfin party before requesting another.
