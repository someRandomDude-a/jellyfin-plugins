# Plugin storage backup and recovery

Stop Jellyfin before making a filesystem backup or restoring plugin state. Back up the complete plugin-owned directories, including database sidecars, encryption keys, `installation.id`, and Deletion Queue's authenticated head/checkpoint artifacts. Keep backups private: they contain credentials and user data. Do not upload them as test evidence.

Hub and Bridge bind their plugin database to an adjacent installation identity marker. A missing database, mismatched marker, or unreadable original keys requires recovery; it must not silently reset command history or create replacement credentials. Embedded Hub modules share its database and must not recreate it independently.

Restore the database, marker and original keys from the same consistent backup. Never delete the marker, regenerate keys, edit identity rows, modify authentication seals, or copy an unrelated database to bypass recovery checks. Preserve the failed originals separately before a reviewed restoration. A malformed/partial marker fails closed rather than being overwritten automatically.

On first successful initialization, the identity is committed inside the plugin database before the marker is durably created. An interruption before marker creation can resume using the existing database identity. Older installations enroll only after their existing validation succeeds; enrollment is not proof that a pre-upgrade incomplete restore was complete.

Deletion Queue checkpoints preserve authenticated state, recent history and command replay evidence. Once checkpoint-format state is present, do not downgrade to a build that cannot read it; use a reviewed complete backup/compatible build. Never remove a checkpoint to force journal replay.

These are plugin-owned storage operations, not instructions to edit Jellyfin's native database. Seerr integration uses HTTP API only; no Seerr database or private configuration manipulation is part of recovery. Restore native Jellyfin and Seerr themselves according to their supported backup procedures.

Installed path-loss/restore tests and long-running checkpoint growth acceptance remain release gates; see [release safety review](release-safety-review.md).
