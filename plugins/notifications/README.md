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

Rich notification actions, media embeds and audio/toast interaction remain a separate follow-up. This extraction does not certify real Discord delivery or the owner's Enhanced right-click markup.
