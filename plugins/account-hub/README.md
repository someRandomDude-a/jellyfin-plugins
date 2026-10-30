# Account Hub

## Dependencies and installation

Install Account Hub through the [combined platform catalog](../../README.md#installation-and-upgrades),
restart Jellyfin and open **Dashboard → Plugins → Account Hub** with a native
administrator session. Use a package matching the server ABI and architecture.
**Do not install legacy User Manager alongside Hub.** Back up existing plugin data
and original keys before upgrading.

- Core account administration does not require a Discord bot.
- Standalone **Notifications** owns the shared inbox, preferences and delivery worker.
- **Discord Bridge** supplies optional Discord delivery and account commands.
- Compatible native **Jellyfin Security** supplies supported factor/session verdicts.
- **Seerr** lifecycle features require a configured external Seerr connection.

This is an integration preview, not completed v1 software. Installation does not
authorize bulk account changes or certify every optional integration.

## What it offers

- People management: search, managed access edits, enable/disable, session revocation
  and verified Discord link management.
- Group access templates, primary assignments, overrides, previews, drift inspection
  and rollback of managed changes without overwriting unrelated native settings.
- Onboarding invitations with approval, explicit creator delegation and safe native
  provisioning; invitation credentials are not disclosed again from history.
- Verified identity links, session-bound recovery and Seerr account lifecycle integration.
- Durable central audit with deduplication, retention checkpoints and protected integration secrets.
- Optional library notices supplied by Notifications; Hub does not observe additions.

Hub is an account workspace, **not a universal settings screen** for the suite.
Configure Discord transport, removal policy and parties in their owning plugins.
Invitations are an integrated Account Hub feature, not a separate plugin. Other
plugins retain independent connection settings and optional feature integrations.

## Security and recovery limits

Access templates do not copy administrator status, credentials, factors or removal
consent. Discord roles never grant native administrator rights. Sensitive operations
retain their current-session, confirmation and audited authorization requirements.

Recovery requires the same browser's current native session for the link's account,
verified and unblocked by native Security, including its approved bypasses. There
is no fresh-code prompt, token-only or signed-out fallback. Password recovery
revokes native sessions without deleting factors or trusted-device records.

## Storage and upgrades

Hub stores `AccountHub/account-hub.db` and its sibling `keys/` directory under
Jellyfin's plugin configuration directory. Protect both and take consistent stopped
backups. Protected integration secrets do not make the whole SQLite database,
audit journal or backups encrypted. Missing/corrupt storage fails closed: never
delete databases or regenerate keys as a repair shortcut.

When upgrading embedded notifications, update Notifications and its consumers
together and follow its migration guide. Hub's retained compatibility routes must
not create a second inbox, bell or worker.

## Development and verification

From the repository root, use the declared .NET SDK, current stable Node.js and
PowerShell 7+. Install the shared frontend harness, then run:

```powershell
npm ci --ignore-scripts --prefix plugins/private-syncplay
pwsh -File scripts/test.ps1 -Plugin account-hub
```

Normal entry points resolve current stable upstream targets automatically. CI/release
freshness failures cannot be bypassed by using historical IDE settings. Passing
unit tests does not certify human recovery, physical devices or production services.

Plugin GUID: `bc9c3d3e-6da3-4ee5-91cd-c429d58ad901` (stable). Published package
versions and ABI come from the selected immutable release, not this README.

## References

- [Account configuration and permissions](../../docs/account-management.md)
- [Security/recovery contract](../../docs/security-recovery-integration.md)
- [Library notifications](../../docs/library-notifications.md) · [Discord integration](../../docs/discord-suite-integration.md)
- [Notifications upgrade guide](../notifications/README.md)
- [Storage recovery](../../docs/plugin-storage-recovery.md) · [Producer contracts](contracts/v1/README.md)
- [Testing](../../docs/testing.md) · [Release policy](../../docs/independent-plugin-releases.md)
- [Current evidence and blockers](../../docs/review-status.md)
