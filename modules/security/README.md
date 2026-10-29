# Account Hub Security integration library

## Dependencies and ownership

This is an optional Hub implementation module, not an installable replacement for
native Jellyfin Security. Install/configure its native provider using the
[current guide](../../docs/security-recovery-integration.md). Supported versions
are discovered and bound to reviewed source contracts and official asset/binary
metadata; an old fixed version in a handoff is not an authorization allowlist.

## Responsibilities

- Current-caller, bounded native status/device operations with no standing token,
  private factor access, TLS bypass, automatic mutation retry or no-factor guessing.
- Live administrator/target protections and applicable fresh authorization for
  security-sensitive actions; lowering settings uses the old policy.
- Exact-token existing-session verdicts for recovery, separate from admin proof.
- Redacted exact-byte signed ingress with durable host-owned receipt/quarantine state.

Use root test/package wrappers; do not install this DLL separately or create another
database, receiver, bot, factor store or permissive host implementation. Native
Security remains installed/enforcing during module failure or rollback.

## References

- [Developer host contract](docs/host-integration.md)
- [Recovery, ingress setup and validation](../../docs/security-recovery-integration.md)
- [Storage recovery](../../docs/plugin-storage-recovery.md)
- [Testing](../../docs/testing.md) · [Current status](../../docs/review-status.md)
