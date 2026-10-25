# Notifications

Independent Jellyfin 12.3 notification inbox, user preferences, server delivery settings and shared producer API. No Account Hub installation is required. Discord delivery uses Discord Bridge; verified DM identity links remain owned by Account Hub.

The plugin owns one Web bell, `/Notifications/api/v1/notifications`, its own native administrator page, storage and delivery worker. Other suite plugins use the framework-only producer client; notification traffic prefers `Notifications.Api.1` even if Hub is unavailable. Hub's old API remains a compatibility adapter to the shared service when both plugins are installed, not another notification worker or bell.

## Upgrade and migration

Back up plugin data and stop Jellyfin before updating. Update the suite consumers together for this initial extraction: the shared API/backend assemblies now have stable identities independent of individual plugin release versions. Future plugin versions retain those shared identities. Install Notifications and restart; never copy mixed old/new dependency DLLs into an installed plugin directory.

The one-time migration moves only the old `nh_*` notification tables out of `AccountHub/account-hub.db` into `Notifications/notifications.db`. Inbox IDs/read/dismiss state, preferences, event deduplication, producer credentials, pending delivery/receipt state and server routes are preserved. Protected routes/settings are decrypted with the original keys and re-encrypted with Notifications' own key ring. No Hub keys are copied. Hub's unrelated tables and keys remain intact.

The migration uses a locked staging database, a source snapshot, integrity/schema/column checks, durable checkpoints and atomic destination cutover. After validation it checks that the source did not change, retires only the old notification tables transactionally and seals the completed destination. A tiny source tombstone prevents an old host from silently recreating an empty notification store. Completed startup uses only Notifications' database and keys; it does not read the legacy database. Temporary migration files and the old storage-binding file are removed.

Interrupted staging, cutover and source-retirement phases resume to the same stable completed state. Missing keys/databases, unknown schemas or changed source data fail closed; they are never repaired by resetting history. Restore the original consistent backup and restart if migration cannot be validated. No Jellyfin native schema/index is changed.

## Verification

`pwsh -File scripts/test-plugin.ps1 -Plugin notifications` runs the notification backend, frontend and migration/common suites. `pwsh -File scripts/test-plugin-package.ps1 -Plugin notifications` checks all three platform packages. Notifications has its own release task and shares the combined architecture feeds with the other plugins.

## Compact inbox and producer presentation

### Updating an existing notification

Use `NotificationsClient.UpsertAsync(entry)` or operation `notifications.upsert` in the v1 JSON envelope (including `/Notifications/api/v1/producer/notifications` with the usual producer credential). Supply a stable, source-scoped `DedupeKey` for the logical thread, and a new `EventId` for each change. Retries reuse their event ID and identical payload. `ReadyForUpsertAsync` checks operation support.

Upsert preserves each recipient's inbox row ID, replaces its content, moves it to the top and restores unread/undismissed state. Recipient existence, preferences, registered types/actions, access callbacks and producer scopes are rechecked on new changes. Older occurred-at values cannot replace newer content. Replayed events remain idempotent. Separate security events should continue using normal Publish; its existing deduplication behavior is unchanged. Updates do not edit or resend already-issued Discord/webhook messages. Deletion Queue uses one logical update thread per removal request; existing historical rows are not destructively migrated or deleted.

The inbox uses compact expandable rows and loads older pages as you scroll (with a keyboard-accessible load-more fallback). Expanded text is plain text, not HTML. Producers can optionally supply `details` (up to 8,000 characters) and up to five `actions`, each with `label`, a validated `link`, and a `size` of `compact` (default) or `normal`. No executable action, arbitrary CSS or client-side permission grant is supported. A link still requires native authorization at its destination. Omitted presentation fields are not serialized, preserving legacy event hashes.

Routine UI feedback does not automatically open the menu. Transient client feedback is account-bound, never persisted and cleared at authentication/account boundaries. Consent, destructive operations and security confirmation remain explicit. Administrator events require the administrator producer scope, resolve recipients through the host, and are filtered again on reads/counts/delivery after demotion. Deletion Queue request notices additionally recheck live native media access through the owning plugin; missing ownership adapters fail closed.

Media embeds and audio/toast interaction remain follow-ups. This work does not certify real Discord delivery or the owner's Enhanced right-click markup.

## Internal plugin interface

### Inline domain commands and short notices

`NotificationTypeDefinition.Commands` declares an allowlist of command IDs. `RegisterNotificationCommands(source, factory)` registers a framework-only delegate receiving `(nativeUserId, immutableEventJson, commandRequestJsonOrNull, cancellationToken)`. A null request projects current, per-user `NotificationCommand` controls (label, selected state, revision and snapshot); a request invokes a declared command after Notifications verifies inbox ownership, current access and the projected revision/snapshot. The producer must recheck live domain permissions and snapshot/revision atomically during mutation. Commands are not executable HTML, producer-supplied URLs or administrator-authored scripts. A failed projector leaves the notice readable and exposes no mutating controls.

Deletion Queue uses this for inline **Allow / Keep / Undo**, with Undo offered only to the owner. Selections use green/red tints and accessible pressed state; no request modal is opened by these controls. `KeepNavigationActions` allows a type to retain navigation alongside commands, as used by playback-reset notices with **View request / Cancel request**. Published events cannot contain precomputed per-recipient commands.

Layout overrides include an optional bounded plain-text `MessageTemplate` (`{media}` and `{days}` substitutions) and `ToastSeconds` (0–10; 0 disables). Only fresh unread opted-in notices create a short, non-modal toast; historical rows stay in the inbox. Toast timers pause during hover/focus. The desktop menu is capped at half the viewport height and aligned to the right edge; compact single-line titles/excerpts expand through Details when needed.

The original navigation/layout registration contract below remains supported. Arbitrary executable commands or rich media remain out of scope.

The portable SDK currently lives in `AccountHub.Api` for binary compatibility; it does **not** require the Account Hub plugin. `NotificationsClient` publishes through `Notifications.Api.1`. Plugins can register types during service registration:

```csharp
services.RegisterNotificationTypes(new NotificationRegistration("example-plugin", "1", [
    new("request.updated", "Request updates", "A request changes", "explicit-users",
        new NotificationLayout("compact", 140, true),
        [new NotificationActionDefinition("Review request", "compact")]),
    new("system.alert", "Administrator alerts", "A service needs attention", "administrators",
        new NotificationLayout("standard", 180, true), [])
]));
```

Registering describes the producer's triggers; it does not execute code, create arbitrary timers or subscribe to Jellyfin events automatically. The producer still owns when its domain event fires. Publish a `NotificationEvent` with the matching source/type through `NotificationsClient.PublishAsync` (or `PublishRequiredAsync` when an acknowledged result is required). Dynamic registration is also available through `RegisterAsync`. Registration uses only framework JSON/delegates across plugin load contexts. New plugin sources need no hard-coded Notifications change. Existing legacy publication remains compatible when no type catalog exists; registered sources must use declared types and actions.

Recipient rules are `explicit-users` (producer supplies exact native user IDs), `administrators` (empty audience; Notifications resolves current administrators), or `event` (a compatibility type may support either, with normal producer scopes still enforced). A definition cannot turn a user event into an administrator event or vice versa. Private user content is not automatically copied to administrators. Use a separately declared sanitized administrator alert instead. Discord Bridge now reports sanitized connection/command-registration failures this way, with throttling and daily/configuration deduplication.

For domain-specific permissions, register `RegisterNotificationAccess(source, factory)`. The returned framework delegate receives `(nativeUserId, eventJson, cancellationToken)` and must check live domain/native visibility. Do not trust a client-supplied username, title or role. Deletion Queue implements this for removal request notices. Normal authorization, current administrator checks and native library notification checks remain authoritative.

Administrators can review registered triggers/recipients/actions and override density, summary length and detail expansion under **Notifications → Registered notification types**. Overrides are persisted in Notifications' own database with revision checks, survive producer re-registration/restarts, and do not alter recipient policy or grant permissions. Removed type overrides are retired. Shared delivery settings separately select administrator Discord/webhook routing.

The menu groups recent administrator/warning/high/critical notices under **Important**, other recent notices under **Other**, and notices at least seven days old under **Older**. Exact localized timestamps appear on hover/accessibility labels alongside relative time. Primary notification links open the source; registered actions remain inline, with Hide/read/source-mute options in the per-row menu. Muting controls are not offered as a way to suppress mandatory administrator or critical alerts.
