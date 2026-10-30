# New episodes and movies

## Dependencies and setup

Use the [installation guide](../README.md#installation-and-upgrades) first. For
in-app library notices, install **Notifications** and enable capture
in **Dashboard → Plugins → Notifications → Library events**. For shared Discord
announcements, add/configure **Discord Bridge**; Hub is optional. Personal Discord DMs
also need verified Hub identity links and an administrator-configured DM route.
No existing library is announced merely by installing these plugins.

## Overview

Library notifications are opt-in and apply only to future native Jellyfin additions. Installing a plugin or enabling this feature does not scan the existing library for announcements. Series/season containers and virtual missing episodes are not announced.

Notifications is the sole library observer. When both destinations are opted out it retains no new library notices or recipient lists. Discord-only capture does not resolve or store a private audience. Hub owns neither library observation nor library settings; Bridge has no fallback observer.

## Notification center

Open **Dashboard → Plugins → Notifications → Library events**. Enable future additions, choose episodes/movies and native libraries (no selection means all). Settings use native administrator authorization, audit and revision checks. Below, each registered type has a simulated example, administrator defaults for in-app/Discord delivery and priority, and **Notification Studio** for text/image placement, fonts, alignment and safe placeholders. Drag layers or use arrow keys/geometry fields, undo/redo and save the layout. Simulated previews never publish events or send messages. Critical alerts remain mandatory/high priority and personal choices win over ordinary administrator defaults.

Only enabled Jellyfin users with native access to the item are included. Access is checked again when publishing, reading the inbox/count, and delivering personal Discord DMs. Removing library access or deleting media hides its existing notification from subsequent inbox reads. Data already viewed or sent cannot be recalled.

In **bell Settings → New episodes and movies**, set each kind's in-app or personal
Discord preference. **Library categories** separates Watchlisted, Favorited,
Requested and All additions. Priority choices are directly selectable. Personal
DM controls require an available provider and verified link; delivery also needs
an administrator-configured DM route. Personal preferences never control shared
channel announcements. See [category behavior](notification-channels.md).

Watchlisted reads native `Likes` (the flag Enhanced uses for its watchlist); Favorited reads native `IsFavorite`. For new episodes, an accessible parent series can supply either flag. The flags remain separate, are not modified by notifications and never grant library access. Classification is frozen for each newly captured notice; old publications and external deliveries are not replayed or reclassified.

## Discord channels

Notifications + Discord Bridge work without Hub for library announcements. In **Dashboard → Plugins → Discord Bridge**:

1. Configure the bot, guild, and approved channels.
2. Explicitly select native Jellyfin libraries and announcement channels. Announcement channels must be a subset of approved channels.
3. Acknowledge disclosure of titles/episode metadata to the channel's members, regardless of their individual Jellyfin permissions.
4. Enable future additions, choosing episodes, movies, or both.

An HTTPS public Jellyfin URL is not required for plain library announcements; it is needed for watch-party links. The bot must be connected and have permission to send in the chosen guild channels. Messages disable mentions, including `@everyone` embedded in media titles.

Announcements use media-only Discord cards: a fixed episode/movie heading, bounded media description, content kind, availability status and addition timestamp. Inspired by Seerr's rich notification layout, but intentionally **not** its requester/commenter/reporter identity fields. Cards contain no author/avatar, requester names, Jellyfin/Discord user IDs, audience lists, native item/library IDs, poster URLs, server URLs or invitation links. Media markdown is escaped and `@` characters neutralized; transport also disables mentions. Discord card accent colors do not change Jellyfin's theme styling.

This is identity minimization, not a promise of absolute anonymity: Discord still sees bot/guild/channel traffic, and library metadata or unusually identifying titles can themselves be sensitive. Only approve libraries whose titles are appropriate for every member of the chosen channel. Deleting a message later cannot undo disclosure to recipients or Discord.

Notifications captures additions once and forwards audience-free notices over the framework-only Bridge API. Bridge owns Discord transport/outbox only. Current native library membership is rechecked before channel delivery, so deleted/moved items cannot use stale scope.

## Durability and limits

- Each native item is deduplicated durably; repeated metadata updates and rescans do not create another notice within the retained intake window.
- A 30-second delay lets initial metadata settle. Notices contain bounded text, native item/library IDs, and (inside Notifications only) private recipient IDs, never paths or credentials.
- Intake is capped at 10,000 retained items per owning plugin. Completed entries can be pruned after 90 days on later capture; unfinished/exhausted entries remain visible in health. Retention is not an infinite deduplication guarantee.
- Local forwarding failures have a bounded retry budget. Native library use does not depend on notification storage or optional integrations.
- Bridge persists a claim before any direct channel send. Interrupted or failed sends become **uncertain**, are counted in health, and are never automatically replayed. A multi-channel send can therefore be partial; this deliberately favors avoiding duplicates over pretending Discord provides exactly-once delivery.
- Inspect capture health in Notifications or delivery health in Bridge settings. Legacy Hub/Bridge intake is read without deleting its tables, retaining IDs and terminal/uncertain states. Oversized imports fail closed without pruning history. Existing personal library choices remain effective after ownership moves. Inspect Discord before manual action on failed/uncertain work; no automatic uncertain-send replay exists.

## Verification

`scripts/integration.mjs --library --packages=<linux-x64-package-directory>` uses disposable accounts/tiny generated videos to check capture, opt-in/no backfill, kind preferences, native series watchlist/Favorites, interest-only filtering/importance, access exclusion/revocation and optional Bridge intake. Use `--plugins=notifications --library` for standalone ownership. Add `--security=latest --proof` for separate native Security checks. Current implementation/test progress is recorded in [review status](review-status.md); pre-migration passes do not certify this source.

External Discord delivery still requires disposable bot/guild credentials; successful intake tests do not certify gateway or message delivery.

Add `--notification-studio` to the standalone Notifications fixture to exercise
installed native dark/light layouts at 320/390/1280px, drag/keyboard/font edits,
revisioned defaults/layout persistence, live bell rendering and preview no-send
behavior. Only sanitized result JSON and synthetic screenshots are evidence;
fixture databases, keys and native credentials are never upload artifacts.

## References

- [Personal categories and importance](notification-channels.md) · [Web Push](web-push.md)
- [Discord setup and delivery semantics](discord-suite-integration.md)
- [Dependency matrix](dependencies.md) · [Testing](testing.md) · [Current evidence](review-status.md)
