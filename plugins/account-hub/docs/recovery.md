# Foundation storage/recovery

## Backup

1. Stop Jellyfin cleanly. Do not copy a live SQLite file without a SQLite-aware backup procedure.
2. Copy the entire `plugins/configurations/AccountHub` directory, including `keys/` and any surviving DB/WAL/SHM files, to a protected backup.
3. Record the plugin build/version and database migration history with that backup.
4. Keep backup ACLs at least as restrictive as the live directory. Audit rows may contain personal information; keys can decrypt integration credentials.

## Restore

1. Stop Jellyfin. Preserve the failed directory for investigation; do not overwrite the only copy.
2. Restore the matching stopped database/key directory as a set. Do not mix a newer head with older audit entries or replace only key files.
3. Start the matching/newer supported plugin binary. Startup runs SQLite quick-check, known migration checks, secret unprotection and audit-chain verification.
4. Confirm administrator health is ready. If not, stop and investigate; no automatic schema downgrade, database recreation, secret clearing or audit repair exists.

Migrations run transactionally via EF's schema history; `Down` deliberately throws. Future-schema history is rejected. A failed migration leaves Hub unavailable; Jellyfin startup is not intentionally blocked by the plugin worker. Repeated initialization in a running process is not an operator recovery mechanism: restore while stopped and restart.

The audit chain detects ordinary entry mutation/deletion and anchors retained segments after pruning. It is **not** an independent off-host rollback witness: an attacker able to rewrite the DB and recompute its head/checkpoint can defeat a local chain. Backups/external attestations are separate concerns.

Audit pruning is currently an internal, explicitly invoked primitive; there is no retention worker or HTTP pruning action yet. It uses host receipt time, not untrusted producer time, removes a contiguous prefix and appends `audit.pruned` atomically with its checkpoint. Receipts/results currently remain for deduplication and need a bounded release policy.

Break-glass remains a local Jellyfin administrator with Jellyfin Security's own recovery codes. Account Hub provides no factor reset, no direct Security-file access, and no reason to disable 2FA.
