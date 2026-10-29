# Account lifecycle implementation library

## Ownership and installation

The retained lifecycle source is compiled as `AccountHub.Lifecycle` inside
[Account Hub](../../plugins/account-hub/README.md). **Do not install the legacy
User Manager plugin alongside Hub.** Its old catalog, account/reset routes, bot,
controllers and File Transformation entry points are not this monorepo's active
installation or recovery model.

## Current boundaries

Jellyfin owns identity and native authentication. Hub owns onboarding, verified
links and recovery orchestration; Bridge owns Discord transport; Notifications
owns the inbox and delivery. Lifecycle effects retain durable claims, revision/
idempotency checks, uncertainty and separate session-revocation checkpoints.
Passwords, factors, administrator status and consent are never copied by templates.
Missing/corrupt state must not regenerate keys or replay an ambiguous password write.

Use the root Account Hub test/package wrappers and current dependency gates. Preserve
host-supplied database/key paths and consistent backups; do not revive excluded legacy
editors, controllers or public routes to work around authorization failures.

## References and license

- [Account workflows](../../docs/account-management.md)
- [Existing-session recovery](../../docs/security-recovery-integration.md)
- [Storage recovery](../../docs/plugin-storage-recovery.md) · [Testing](../../docs/testing.md)
- [Current evidence](../../docs/review-status.md)
- MIT; relicensed by the code owner on 2026-10-23. See [LICENSE](LICENSE).
