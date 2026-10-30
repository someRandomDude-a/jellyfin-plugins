# Current review status

**Not release-ready. Historical package passes do not certify the latest source.**

This summary distinguishes the last verified package from subsequent changes.
The repository's current commit/tag names do not establish publication or runtime
acceptance. See the [evidence register](verification.md) for milestones and the
[preserved review log](review-history.md) for detailed development history.

## Last verified package: 0.0.37.0, Linux x64

| Gate | Evidence |
|---|---|
| 1,201 .NET passes, one optional external-service skip; six archives | `artifacts/grouping-diagnostics-backend-package-r16.log` |
| 128 unique combinations, all passed; 64 with native Security | `artifacts/grouping-diagnostics-combinations-r11/results.json` |
| Full installed/browser: authorization, grouping, signup, synthetic same-tab linking, shared diagnostics and cached-owner navigation; zero page errors | `artifacts/grouping-diagnostics-full-r10.log` |
| Standalone Hub and Notifications, including studio | `artifacts/grouping-diagnostics-hub-r12.log`, `artifacts/grouping-diagnostics-standalone-r10.log` |
| Native themes, responsive layouts and studio | `artifacts/grouping-diagnostics-theme-r11.log` |
| Native Security: factor/proof/recovery/reset/session checks and restart integrity | `artifacts/grouping-diagnostics-security-r11.log` |
| Native SyncPlay/media and isolated consumer API | `artifacts/grouping-diagnostics-syncplay-r10.log`, `artifacts/grouping-diagnostics-consumer-r10.log` |
| Real Seerr public API and installed Hub account lifecycle | `artifacts/grouping-diagnostics-seerr-r10.log` |

The matrix initially failed 35 cases. A checksum-verified runner restoration and
fresh fixtures passed the complete rerun; the earlier tool/native-directory
disappearance remains unexplained. No original state was recreated or bypassed.
Synthetic link challenges are **not human Discord issuance**. Native-account
deletion follow-up is not production media-removal certification.

## Newer source and automation

- Factor-enrolled link acceptance exposed a lost return marker after native
  Security verification. The newer helper carries only a five-minute per-tab
  intent expiry, not a challenge, identity or credential. It needs a fresh
  coherent package and installed/browser acceptance.
- Current tool/bootstrap regression evidence: **223 script tests** in
  `artifacts/grouping-diagnostics-scripts-r17.log`; workflow lint in
  `artifacts/grouping-diagnostics-tool-actionlint-r1.log`.
- Real Windows bootstrap verified official checksums and executable versions for
  Node 26.13.0, PowerShell 7.6.9 and SDK 10.0.403 in
  `artifacts/grouping-diagnostics-tool-bootstrap-install-r1.log`. These are
  observations, not permanent targets or Windows plugin certification.
- Direct-image maintenance passed the freshness audit in
  `artifacts/grouping-diagnostics-freshness-r11.log`. Freshness is time-sensitive;
  new runs must discover current releases, never reuse expired snapshots.

## Remaining acceptance

1. Fresh coherent suite package/native gates for the newer factor-return helper,
   including a genuinely enrolled native factor challenge.
2. Linux tool installation and actual hosted CI/release workflow execution.
3. Renewed owner-approved human Discord testing: signup limits/third-use denial,
   party redemption and recipient privacy. No live fixture is active.
4. Windows/physical ARM plugin execution, full owner-plugin profile,
   native-origin outbound webhook transport and broader proxy/fault boundaries.

## Workflow and documentation cleanup

**16 → 7 workflows**, one suite/plugin release selector and one Windows/Linux
unit matrix. `scripts/test.ps1 -Plugin <owner|all>` consumes one tested registry;
mixed UI cases have explicit owners. Overlapping workflow assertions and retired
source/document-style checks are removed, not migration or safety coverage.

- Full root suite: **1,201 .NET passes**, one optional external-service skip,
  frontend/package/upgrade gates and live freshness of **82 declarations** pass in
  `artifacts/test-suite-consolidation-full-r1.log`. Nested gates reuse the same
  source/run-bound audit without rediscovery; publication always rediscovers.
- **236 root script tests** pass on Windows/Linux in
  `artifacts/test-suite-consolidation-scripts-r3.log` and
  `artifacts/test-suite-consolidation-linux-r2.log`.
- Workflow lint and four read-only database-checker regressions pass in
  `artifacts/test-suite-consolidation-actionlint-r2.log` and
  `artifacts/test-suite-database-checker-r1.log`.

Hosted execution remains unverified; API/authentication failures still block
release. See [test ownership](testing.md#what-belongs-in-each-gate) and the
[workflow map](../.github/README.md) for branch-protection migration notes.
Earlier workflow, Linux fixture and poster evidence is preserved in the history.

Latest Discord fixture correction: transport settings already use operational
native-administrator authorization, not fresh proof. The stale standalone 403
expectation is replaced with enrolled-factor administrator writes, anonymous/
ordinary/API-key denial without mutation, and stale revision rejection. **241
script tests** pass on Windows/Linux in `artifacts/discord-operational-scripts-r1.log`
and `artifacts/discord-operational-linux-scripts-r1.log`. Backend/native acceptance
is unverified; fresh discovery timed out in `artifacts/discord-operational-targets-r1.log`
and `artifacts/discord-operational-targets-r2.log`. No older snapshot was substituted.
The traced third attempt in `artifacts/discord-operational-targets-r3-trace.log`
passes current release metadata and both reviewed source contracts; the official
Security ZIP returns HTTP 200 but times out while downloading its body. This is
not evidence that the new installed authorization checks passed.
The owner test process was subsequently stopped after 90 minutes: its log was
empty and bootstrap had downloaded only part of PowerShell, before any tests ran.
The missing web-request deadlines are corrected with bounded, stage-logged HTTP;
**248 script/tool checks** pass in `artifacts/tool-http-and-scripts-r2.log`, including
seven real PowerShell HTTP cases (headers/body stalls, continuous trickle, clean
metadata, HTTP failure, size limit and success). Live metadata-only bootstrap
passes in `artifacts/tool-http-bootstrap-plan-r2.log`. Full tool installation and
Discord backend/native acceptance remain unverified; no full rerun is active.

Documentation and workflow configuration do not authorize publication, production
deletion, live Discord activity, credential disclosure or changes to original keys
and histories. Use [testing](testing.md), [dependency maintenance](dependency-maintenance.md)
and the [release checklist](github-release-checklist.md) for the applicable procedures.
