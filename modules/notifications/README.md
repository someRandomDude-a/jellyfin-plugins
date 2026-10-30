# Notifications implementation library

## Dependencies and ownership

This directory contains the backend/frontend library used by the independently
installable [Notifications plugin](../../plugins/notifications/README.md), not a
second runtime plugin, bot or inbox. Install through the root platform catalogs.
Standalone Notifications owns its database/key ring, worker, API and hosted-Web bell;
Hub's old routes remain compatibility adapters, not an embedding recipe for new stores.

## Development

From the repository root, use `pwsh -File scripts/test.ps1 -Plugin notifications`.
Current target/toolchain and native ABI gates apply. Producers use the framework-only
shared SDK and current scoped/registered interface; preserve retry IDs and current
recipient/domain authorization. Do not reactivate retired Hub bot or broad 2FA bypasses.

The shared studio renderer uses safe declarative layers only. Keep layer selection
separate from model edits, scope Ctrl+Z/Ctrl+Y to the editor, preserve native input
nodes during property edits, and acknowledge only the actual saved snapshot.
Do not introduce HTML/CSS/script or external-image-URL inputs as designer features.

## References

- [Producer and host contract](../../docs/notification-producer-api.md)
- [Installation/migration](../../plugins/notifications/README.md)
- [Security ingress](../../docs/security-recovery-integration.md)
- [Storage recovery](../../docs/plugin-storage-recovery.md) · [Testing](../../docs/testing.md)
