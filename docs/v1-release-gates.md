# v1 release completion gates

Status: **NOT RELEASE READY**. This is the full planned suite, not a reduced-scope rename of the integration preview. Existing architecture decisions in `docs/dependencies.md` supersede older single-bot-in-Hub plans.

Current database/failure/theme audit: [release-safety-review.md](release-safety-review.md). Native creation allocation and full-policy concurrent-update risks remain open; do not equate happy-path installed tests or SQLite integrity checks with a guarantee against all races.

## Implementation tracks

- [ ] Durable invite provisioning: approval, hashed single-use/multi-use reservations, atomic redemption, interrupted-operation recovery, native account creation, self-chosen password only, complete administrator/public UI.
- [ ] Group intent: one primary group, per-user overrides, managed-field precedence, administrator exclusion, revisioned assignment and reset, bulk preview/apply, desired-vs-actual drift and explicit reconciliation.
- [ ] Seerr: verified capability discovery, explicit native-account links (no fuzzy identity guessing), provisioning, permission/quotas, bounded durable reconciliation and safe deletion follow-up jobs.
- [ ] Domain editors: complete paginated/bulk policy editing for Deletion Queue and Private SyncPlay, using their authoritative versioned boundaries; standalone behavior must remain intact.

## Privacy and security

- [ ] Full identity/ownership and API-key denial regression matrix for new endpoints.
- [ ] Native Security recovery-code proof, pending-factor sessions, trusted-device actions and deletion cleanup; never inspect or replace factor records.
- [ ] Invite/recovery/Seerr job restart and failure injection: no duplicate accounts, lost reservation slots, guessed upstream matches, plaintext passwords, or blind replay of uncertain effects.
- [ ] Audit/outbox retention and deletion consistency across embedded modules and optional plugins.
- [ ] Shared Discord/inbox surfaces never expose requester/voter/participant identity or recipient lists; titles require explicit disclosure scope and current native access where applicable.
- [ ] Local live Discord interactive-command, DM and watch-party delivery checks; investigate earlier intermittent history-verification failures without retrying sends.
- [ ] No secrets in source, ZIPs, CLI arguments, logs or uploaded artifacts. `.env` remains ignored. Live Discord checks remain local, not required Actions jobs.

## Installed acceptance

- [x] Full root tests on current r10 Activity companion source: 778 .NET / 142 JavaScript, syntax/inventory/offline distribution and upgrade fixtures (`artifacts/activity-r10-final-tests.log`). Reopen on source changes; live Activity, installed matrix and platform gates remain separate.
- [x] Current r10 Linux x64 standard installed independent/plugin-combination matrix, including native Security: 32/32 (`artifacts/runtime-review/r10-combinations/results.json`). Reopen on packaged source changes; broader feature/fault/platform gates remain separate.
- [ ] Clean install and upgrade with backup/key recovery and schema forward-compatibility failure behavior.
- [ ] Real installed Linux x64, Linux ARM64 and Windows x64 loading. Cross-publishing is not execution evidence; unavailable runners remain blockers.
- [ ] Reverse proxy, trusted TLS, BaseUrl, mobile, native Modern/Legacy and custom-theme coverage, including keyboard/focus behavior and secret erasure on navigation.
- [ ] Native outbound Security webhook transport and fault recovery.

## Distribution

- [x] License review complete per owner confirmation. Inventory/SBOM tooling remains required for each final staged package; reopen review for dependency/distribution changes.
- [ ] Supplemental review of the newly bundled Embedded App SDK JavaScript and runtime dependency notices/inventory. The previous owner-confirmed review remains recorded; this new distribution was introduced afterward.
- [x] Current r10 package identity/version/ABI/catalog/runtime checks passed for all four plugins across Linux x64/ARM64 and Windows x64. All 12 ZIPs, three catalogs, checksums and inventories passed merged-release validation (`artifacts/runtime-review/activity-r10/merged-release-check`). Cross-packaging is not installed Windows/physical ARM execution evidence.
- [ ] Immutable local v1 packages, checksums, preserved catalog history, install/upgrade/backup/recovery documentation and honest release notes.
- [ ] User approval before commits, tags, pushes or publication. Asking to prepare v1 does not discard the existing no-publication constraint.

Use `docs/review-status.md` for dated evidence and unresolved failures. Do not tick a gate based only on implementation claims, mocked behavior, or an earlier package revision.
