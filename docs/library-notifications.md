# New episodes and movies

Library notifications are opt-in and apply only to future native Jellyfin additions. Installing a plugin or enabling this feature does not scan the existing library for announcements. Series/season containers and virtual missing episodes are not announced.

When both destinations are opted out, Hub does not retain library notices or recipient lists. Discord-only capture does not resolve or store a Jellyfin audience.

## Notification center

Install Account Hub, then open **Account Hub → Library**. Enable notifications and choose episodes, movies, or both. Saving uses the same elevated, audited configuration command gate as other Hub settings.

Only enabled Jellyfin users with native access to the item are included. Access is checked again when publishing, reading the inbox/count, and delivering personal Discord DMs. Removing library access or deleting media hides its existing notification from subsequent inbox reads. Data already viewed or sent cannot be recalled.

In the notification bell, expand **New episodes and movies: personal preferences** to enable/mute each kind separately for in-app or personal Discord delivery. Personal DMs additionally require a verified Discord link and an administrator-configured direct-message notification route. Personal settings do not control shared channel announcements.

## Discord channels

Discord Bridge works without Hub for library announcements. In **Dashboard → Plugins → Discord Bridge**:

1. Configure the bot, guild, and approved channels.
2. Explicitly select native Jellyfin libraries and announcement channels. Announcement channels must be a subset of approved channels.
3. Acknowledge disclosure of titles/episode metadata to the channel's members, regardless of their individual Jellyfin permissions.
4. Enable future additions, choosing episodes, movies, or both.

An HTTPS public Jellyfin URL is not required for plain library announcements; it is needed for watch-party links. The bot must be connected and have permission to send in the chosen guild channels. Messages disable mentions, including `@everyone` embedded in media titles.

Announcements use media-only Discord cards: a fixed episode/movie heading, bounded media description, content kind, availability status and addition timestamp. Inspired by Seerr's rich notification layout, but intentionally **not** its requester/commenter/reporter identity fields. Cards contain no author/avatar, requester names, Jellyfin/Discord user IDs, audience lists, native item/library IDs, poster URLs, server URLs or invitation links. Media markdown is escaped and `@` characters neutralized; transport also disables mentions. Discord card accent colors do not change Jellyfin's theme styling.

This is identity minimization, not a promise of absolute anonymity: Discord still sees bot/guild/channel traffic, and library metadata or unusually identifying titles can themselves be sensitive. Only approve libraries whose titles are appropriate for every member of the chosen channel. Deleting a message later cannot undo disclosure to recipients or Discord.

When both plugins are installed, Hub captures additions once and forwards audience-free notices over the framework-only bridge API. Bridge always owns Discord transport. Without Hub, Bridge observes native additions itself. Current native library membership is rechecked before channel delivery, so deleted/moved items cannot use stale scope.

## Durability and limits

- Each native item is deduplicated durably; repeated metadata updates and rescans do not create another notice within the retained intake window.
- A 30-second delay lets initial metadata settle. A notice contains bounded display text, native item/library IDs, and (inside Hub only) recipient IDs, never media file paths or credentials.
- Intake is capped at 10,000 retained items per owning plugin. Completed entries can be pruned after 90 days on later capture; unfinished/exhausted entries remain visible in health. Retention is not an infinite deduplication guarantee.
- Hub/local forwarding failures have a bounded retry budget. The native library remains usable if notification storage or dependencies fail.
- Bridge persists a claim before any direct channel send. Interrupted or failed sends become **uncertain**, are counted in health, and are never automatically replayed. A multi-channel send can therefore be partial; this deliberately favors avoiding duplicates over pretending Discord provides exactly-once delivery.
- Inspect intake health in Hub's Library tab or Bridge settings. For failed/uncertain entries, check bot connectivity, scope, approved channels and storage, then inspect Discord before taking manual action. There is no automatic uncertain-send replay button.

## Verification

`scripts/integration.mjs --library --packages=<linux-x64-package-directory>` creates only disposable accounts and tiny generated video fixtures. It checks episode/movie capture, opt-in/no backfill, native library discovery, rescan deduplication, and durable Bridge intake. With Hub it also checks personal kind preferences, inaccessible-user exclusion, and access revocation. Use `--plugins=discord` to check the independent observer. Add the pinned `--security=<zip> --proof` options for real native TOTP checks.

External Discord delivery still requires disposable bot/guild credentials; successful intake tests do not certify gateway or message delivery.
