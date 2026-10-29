# Plugin storage backup and recovery

## Prerequisites

Identify the affected plugin and its storage location, retain the failed originals,
and obtain a consistent backup with its **original keys and installation identity**.
Use compatible plugin versions and stop Jellyfin before restoration. There is no
safe empty-database, new-key or identity-reset substitute for a missing backup.

## Backup and restore

Stop Jellyfin before making a filesystem backup or restoring plugin state. Back up the complete plugin-owned directories, including database sidecars, encryption keys, `installation.id`, and Deletion Queue's authenticated head/checkpoint artifacts. Keep backups private: they contain credentials and user data. Do not upload them as test evidence.

Hub and Bridge bind their plugin database to an adjacent installation identity marker. A missing database, mismatched marker, or unreadable original keys requires recovery; it must not silently reset command history or create replacement credentials. Embedded Hub modules share its database and must not recreate it independently.

Restore the database, marker and original keys from the same consistent backup. Never delete the marker, regenerate keys, edit identity rows, modify authentication seals, or copy an unrelated database to bypass recovery checks. Preserve the failed originals separately before a reviewed restoration. A malformed/partial marker fails closed rather than being overwritten automatically.

On first successful initialization, the identity is committed inside the plugin database before the marker is durably created. An interruption before marker creation can resume using the existing database identity. Older installations enroll only after their existing validation succeeds; enrollment is not proof that a pre-upgrade incomplete restore was complete.

Deletion Queue checkpoints preserve authenticated state, recent history and command replay evidence. Once checkpoint-format state is present, do not downgrade to a build that cannot read it; use a reviewed complete backup/compatible build. Never remove a checkpoint to force journal replay.

These are plugin-owned storage operations, not instructions to edit Jellyfin's native database. Seerr integration uses HTTP API only; no Seerr database or private configuration manipulation is part of recovery. Restore native Jellyfin and Seerr themselves according to their supported backup procedures.

Installed path-loss/restore tests and long-running checkpoint growth acceptance remain release gates; see [safety and operational limits](safety-and-permissions.md).

## Storage sets and component-specific recovery

| Component | Stopped backup set |
|---|---|
| Account Hub | Complete plugin configuration `AccountHub/` directory, `account-hub.db`, SQLite sidecars, original `keys/`, installation marker and migration history/build version |
| Notifications | Owned `Notifications/` database/key ring and migration checkpoints; initial extraction also requires the intact original Hub backup until migration is validated |
| Discord Bridge | Complete Bridge database, original key ring and installation marker |
| Deletion Queue | Complete `DataPath/deletion-queue/`, including `queue.sqlite3`, SQLite sidecars, `keys.json`, head/checkpoint seals, initialization artifacts and preserved artwork/receipts |
| Private SyncPlay | Native XML settings plus `DataPath/private-syncplay/integration-v1.db` and sidecars; parties/invitations/guest leases are in memory and intentionally expire on restart |

Hub migration history is forward-only; future/unknown schemas and invalid audit
chains are rejected. Startup recovery is not permission to downgrade schemas, repair
audit chains or regenerate encryption keys. Local hash chains/seals are not independent
off-host witnesses against restoring a whole consistent older installation.

Queue payload-format upgrades preserve keys, consent, history, claims and existing
ciphertext. Older binaries may reject newer formats. Keep automatic deletion disabled
through a reviewed restore and reconcile Seerr outcomes before permitting work. An
uncertain claim is never released for another dispatch; **Recheck Seerr outcome
(never resend deletion)** reads status and may record FilesDeleted, after which the
scheduler may complete the checkpoint without another deletion. Never manufacture
success from an empty path or restore old claims as a retry mechanism.

Restore original artwork pointers and only plugin-owned collection memberships
before uninstalling presentation features; do not delete backups while native
pointers reference them. Resolve administrator conflicts rather than overwriting them.
For SyncPlay rollback, disable optional integration and retain its outbox dormant
with a compatible XML/binary backup; do not resurrect parties or replay stale notices.

Break-glass access remains a local native administrator and Security's own recovery
codes. Hub/module failure never justifies disabling 2FA or editing factor records.

## References

- [Installation and upgrade prerequisites](../README.md#installation-and-upgrades)
- [Notifications migration](../plugins/notifications/README.md)
- [Safety and operational limits](safety-and-permissions.md) · [Current evidence](review-status.md)
