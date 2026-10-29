# v1 release completion gates

> **Architecture gate:** validate the current [six-plugin ownership requirements](product-requirements.md), including integrated Hub invitations, independent connection ownership, standalone Notifications and rule/Queue separation. No separate Invitations plugin or mandatory common connection registry is required. Historical passes do not close latest-source gates. Test the [owner plugin profile](../test-profiles/owner-server.json) only with exact verified external packages and disposable data.

Status: **NOT RELEASE READY**. These are final acceptance gates, not a claim that
every unchecked feature is unimplemented. Use [current dependencies](dependencies.md),
not older single-bot-in-Hub plans or historical package counts.

Current evidence: [review status](review-status.md), [testing](testing.md) and the [scoped evidence register](verification.md). [Safety and permission limits](safety-and-permissions.md) retain unresolved risks consolidated from earlier reviews. Native creation/concurrent-policy safety still needs scoped fault evidence; SQLite integrity alone is not a guarantee against all races.

## Implementation tracks

- [ ] Final-package invite provisioning/fault acceptance: approval, hashed single-use/multi-use reservations, atomic redemption, interrupted-operation recovery, native account creation, self-chosen password only, complete administrator/public UI.
- [ ] Final-package group intent acceptance: primary group, per-user overrides, managed-field precedence, administrator exclusion, revisioned assignment/reset, bulk preview/apply, drift and explicit reconciliation.
- [ ] Final-package Seerr acceptance: verified capability discovery, explicit native-account links (no fuzzy guessing), provisioning, permission/quotas, bounded durable reconciliation and safe deletion follow-up.
- [ ] Final-package domain editor acceptance for Deletion Queue and Private SyncPlay through authoritative versioned boundaries; standalone behavior remains intact.
- [ ] Final-source native `/watchparty [user]` and bounded `/invite` options, independent Bridge/Hub defaults/limits UI and current native-user permissions.
- [ ] Notifications-only library ownership, legacy intake/preferences migration, administrator defaults/examples and safe notification studio, including standalone and installed browser acceptance.

## Privacy and security

- [ ] Full identity/ownership and API-key denial regression matrix for new endpoints.
- [ ] Native same-browser/same-account verified-session recovery (including native-approved bypasses), pending-factor rejection, trusted-device actions and deletion cleanup. Fresh proof remains separate for applicable administrative Security actions; never add a new reset-code step or inspect/replace factor records.
- [ ] Invite/recovery/Seerr job restart and failure injection: no duplicate accounts, lost reservation slots, guessed upstream matches, plaintext passwords, or blind replay of uncertain effects.
- [ ] Audit/outbox retention and deletion consistency across embedded modules and optional plugins.
- [ ] Shared Discord/inbox surfaces never expose requester/voter/participant identity or recipient lists; titles require explicit disclosure scope and current native access where applicable.
- [ ] Explicitly authorized final-package live Discord human-command/DM/watch-party acceptance; earlier scoped passes are recorded separately. Investigate history-verification uncertainty without replaying sends.
- [ ] No secrets in source, ZIPs, CLI arguments, logs or uploaded artifacts. `.env` remains ignored. Live Discord checks remain local, not required Actions jobs.

## Installed acceptance

- [ ] Fresh Linux root/package acceptance on the final source. Earlier preview
  passes, including 0.0.37.0, do not certify later changes. GitHub platform jobs
  remain separate.
- [ ] Final-source installed plugin matrix including native Security; its with-Security half stays late-stage. Historical matrix passes do not close it.
- [ ] Clean install and upgrade with backup/key recovery and schema forward-compatibility failure behavior.
- [ ] Fresh installed Linux x64 authorization, invitation/group/library, standalone Notifications studio/defaults, restart/storage, browser and native SyncPlay checks. Windows/ARM jobs run on GitHub; native Security and human Discord checks remain separate.
- [ ] Reverse proxy, trusted TLS, BaseUrl, mobile, native Modern/Legacy and custom-theme coverage, including keyboard/focus behavior and secret erasure on navigation.
- [ ] Native outbound Security webhook transport and fault recovery.

## Distribution

- [x] License review complete per owner confirmation. Inventory/SBOM tooling remains required for each final staged package; reopen review for dependency/distribution changes.
- [ ] Final-source six-plugin/18-package identity/version/ABI/catalog/runtime, checksum and inventory validation across Linux x64/ARM64 and Windows x64. Earlier four-plugin r10/12-package results are historical only; cross-packaging is not installed Windows/physical ARM execution evidence.
- [ ] Immutable local v1 packages, checksums, preserved catalog history, install/upgrade/backup/recovery documentation and honest release notes.
- [ ] User approval before commits, tags, pushes or publication. Asking to prepare v1 does not discard the existing no-publication constraint.

Do not tick a gate based only on implementation claims, mocked behavior or an earlier
package revision. Keep our direct dependencies current; upstream transitive
advisories are informational, not project blockers. See current status for the
latest bump and Linux acceptance results.

## References

- [Current evidence and failures](review-status.md) · [Historical evidence register](verification.md)
- [Latest-only maintenance](dependency-maintenance.md) · [Testing](testing.md)
- [Safety and permissions](safety-and-permissions.md) · [Release checklist](github-release-checklist.md)
