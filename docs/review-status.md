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

1. Fresh backend/package/native gates for the newer factor-return helper,
   including a genuinely enrolled native factor challenge.
2. Linux tool installation and actual hosted CI/release workflow execution.
3. Renewed owner-approved human Discord testing: signup limits/third-use denial,
   party redemption and recipient privacy. No live fixture is active.
4. Windows/physical ARM plugin execution, full owner-plugin profile,
   native-origin outbound webhook transport and broader proxy/fault boundaries.

## Workflow and documentation cleanup

The cleanup preserves test/release gates while consolidating tool installation
and the duplicate dependency-validation root matrix. Its **226 script tests**,
local documentation links and workflow lint pass in
`artifacts/workflow-docs-cleanup-tests-r1.log` and
`artifacts/workflow-docs-cleanup-actionlint-r1.log`. This is not a hosted-CI run
or new plugin-package certification. See the [workflow map](../.github/README.md)
for entry points and branch-protection migration notes.

Latest hosted-run fixture fixes initialize missing temp parents, align Queue's
strict archive allowlist with its diagnostics dependency, and permit narrowly
scoped offline state moves when Docker owns Linux fixture directories. Local
script regressions pass **229** cases; a clean Linux container passes **15**
focused cases in `artifacts/ci-fixture-fixes-linux-r1.log`. Full hosted package
and recovery jobs must be rerun; these focused checks do not certify those jobs.

Poster-layout CI regression: Chromium reproduces empty computed styles on an
inbox row detached by refresh. The acceptance sampler now queries and measures
the current connected, loaded card atomically; genuine overlap and incorrect CSS
still return immediately and fail the unchanged layout assertion. **235 script
tests** pass on Windows and Linux in `artifacts/live-poster-script-tests-r1.log`
and `artifacts/live-poster-linux-scripts-r1.log`; real Chromium refresh, overlap
and wrong-CSS coverage passes in `artifacts/live-poster-chromium-r3.log`.
Fresh standalone Notifications **0.0.38.0** on Jellyfin **12.4** passes all six
installed studio theme/width cases, live-poster geometry and page-error checks
in `artifacts/live-poster-installed-r2.log` and its `studio-results.json`.
The first installed attempt omitted `--library`, so no live card was seeded;
its failed log remains preserved. This focused Notifications gate does not
certify the full suite or newer factor-return changes; hosted CI needs rerunning.

Documentation and workflow configuration do not authorize publication, production
deletion, live Discord activity, credential disclosure or changes to original keys
and histories. Use [testing](testing.md), [dependency maintenance](dependency-maintenance.md)
and the [release checklist](github-release-checklist.md) for the applicable procedures.
