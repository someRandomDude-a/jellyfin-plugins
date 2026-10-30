# Automatic dependency maintenance

## Prerequisites and setup

These files are local configuration until installed on the default branch through
normal review. Configure the dedicated bot token/login, strict required checks,
squash merging and existing release-environment policy using the setup section
below. Do not bypass approvals or publish local work to enable automation.
The [current review status](review-status.md) records direct-dependency freshness
and acceptance results separately. Bot discovery is not proof that an upgrade passed acceptance.

`dependency-maintenance.yml` checks for stable updates **hourly**, on manual
dispatch, or on a `repository_dispatch` event of type `dependency-update`.
An upstream webhook can send that event for earlier discovery; GitHub does not
provide a universal “any dependency released” event. No workflow was triggered
while implementing this configuration.

## Update → test → merge → release

### Automatic tool bootstrap before tests and releases

`scripts/bootstrap-tools.ps1` discovers stable **Node.js, PowerShell and .NET SDK**
from their official release metadata on Windows/Linux x64/ARM64. Node SHA-256,
PowerShell asset SHA-256 and SDK SHA-512 are verified before extraction or
execution, followed by executable-version checks. Downloads go into a fresh
owned `artifacts/tool-bootstrap/` directory; no global install, PATH persistence,
`.env` loading or older-tool fallback is performed. A failed/truncated download
is retained and rejected, never executed. `-PlanOnly` prints discovered versions
without installing. A newer SDK than `global.json` requires a dependency bump,
not an override of the source's build policy.

The local test/package entry points bootstrap tools and check dependency freshness
before execution. CI uses `.github/actions/setup-latest-tools` in the parent
target gate and child target-binding action. Node/PowerShell/SDK versions bind
children to the parent evidence; a new release appearing mid-graph causes a
mismatch failure rather than mixing toolchains. The shared release engine preflights
tools/freshness **before creating a tag** as well as before tests/publication.
Docker daemon, Git and OS utilities remain runner prerequisites; managed browser,
lint, tunnel and fixture images are handled by the image freshness/bump policies.

One hourly maintenance workflow contains both jobs. Renovate
opens the protected dependency PR; the preview job applies updates
only in its disposable checkout and uploads the patch/report, then tests it.
Immutable release source is never automatically rewritten: stale releases stop
until the tested dependency update is merged.

### Manual reviewable bump (same engine locally and in Actions)

Run **Actions → Dependency maintenance** to get a dependency-only preview patch, report
and itemized version/lock changes plus Linux root tests, without commits, pushes or publication. For the
same steps locally, with current stable Node/npm:

```powershell
node scripts/update-dependencies.mjs --apply
node scripts/check-dependency-freshness.mjs
```

Without `--apply`, the updater only records a plan. It preserves existing local
edits, refuses concurrent edits, resolves all registries before writing manifests,
keeps direct dependency names/source logic fixed and updates npm locks with lifecycle
scripts disabled. npm may change upstream transitive dependencies as required by
new direct versions; that is not a project feature/dependency-set expansion. Reports
live under `artifacts/dependency-bump/`. Failed resolution or acceptance is not a
successful bump. The read-only release gate never edits immutable tagged source.

The scheduled/manual preview workflow is an artifact-only helper; the existing Renovate discovery/
PR/validation/promotion process below remains the protected recurring automation.
After bumping, run Linux root/installed acceptance locally. GitHub retains its
Windows/ARM/platform jobs; no live human fixture is launched by dependency discovery.

### Protected recurring automation

1. Renovate discovers direct NuGet/npm dependencies (refreshing their locks), .NET SDK,
   GitHub Actions (including composite actions), Docker Compose, embedded
   tool-container and Playwright tool updates. Updates
   are grouped into one dependency branch/PR. Stable major updates are eligible,
   but an incompatible update must fail tests rather than bypass them.
2. `dependency-validation.yml` accepts only a same-repository `renovate/` branch
   owned by the configured dependency bot. Trusted **base-branch** tooling checks
   that only dependency declarations/locks changed and generates the commit body.
3. All selected-plugin Windows/Linux gates, standalone installed tests, full root
   tests on Windows/Linux, installed browser checks, the 128-case matrix, native
   Security/session checks, SyncPlay integration and real Seerr/Sonarr/Radarr API
   fixtures must pass. Human/live-service acceptance is never silently launched
   or represented as tested. Failures/cancellations/skipped required jobs block
   promotion.
4. The bot squash-merges only the tested head onto the unchanged tested base.
   The final commit lists **each changed direct and lockfile/transitive dependency**,
   old/new versions, integrity/digest changes and affected files. Non-dependency
   source/config changes require separate review; npm lifecycle scripts and
   Renovate post-upgrade commands are disabled.
5. The promotion script picks the next suite **patch** above every existing
   suite/plugin version and dispatches `release-all.yml` at the exact merged SHA.
   That engine rechecks/builds/validates all 18 packages, publishes an immutable
   integration-preview release and updates existing catalogs. The itemized commit
   body becomes the release notes. Existing `plugin-release` environment approvals
   remain in force; automation does not disable reviewers or bypass branch rules.

Software under test—Jellyfin, Security, Enhanced, Seerr and Arr—continues to be
resolved automatically per run, not converted into permanent fixture pins.
`.github/upstream-dependencies.json` lets Renovate detect their new releases and
open a tested maintenance PR even when no library declaration changed. It is an
automatically maintained notification marker, **never** a build/test input or an
older-version fallback. Its changes are itemized in the final commit too.
Historical IDE versions and intentional old-version test examples are excluded
from dependency rewriting. Reviewed native database/Security source fingerprints
are authorization/safety baselines, not dependency versions: automation never
rewrites them to approve changed upstream contracts.

## Latest-only gates on every test/release graph

The bump job discovers updates; it does **not** by itself guarantee the branch is
current. `latest-targets.yml` is the mandatory shared entry gate for normal CI,
dependency validation, integration and the suite/independent release selector.
`check-dependency-freshness.mjs` blocks outdated runtime/test packages,
SDK declarations, Actions, embedded browser/tool images and mismatched image
digests. npm lock roots and resolved direct versions must match the current direct
declarations; `npm ci` verifies reproducible installation/integrities during tests.
Independent transitive releases/advisories are informational, not project freshness
requirements. Standalone transitive lock maintenance and forced major overrides are
not enabled. Source manifests and locks are never silently rewritten while
building an immutable tag.
The bot must update them and pass tests; incompatible major updates remain blocked.

One current stable target snapshot binds Jellyfin/Security/Enhanced/Seerr/Arr,
source contracts, checksums and pulled container digests across tests and packages.
Node is resolved from the current stable distribution, recorded once, installed
at that exact version in children, and checked against the snapshot. Build/test
jobs also verify the actual installed SDK. Missing/expired evidence, registry
errors, unrecognized image policies, changed native contracts and runtime/ABI
mismatches fail with errors, never an older-version fallback. GitHub Actions
builds without generated latest properties fail before restore/build instead of
using the historical IDE baseline. Official Jellyfin database dependencies remain
aligned to that server's reviewed ABI, not blindly upgraded to a different ABI.
SkiaSharp and its test native assets also follow the current server's renderer
version. They are not independently upgraded to an incompatible major and are never
shipped as competing server-native libraries.

Every suite release now requires full root Windows/Linux tests, browser checks,
the subset matrix, native Security/SyncPlay and external API fixtures, including
manual releases (not only dependency-bot releases). Independent releases retain
selected-plugin gates; Hub/Queue/automatic-rules releases additionally require the
latest external-service fixtures without running the full subset matrix. Hub and
Bridge combinations and the standalone Notifications owner exercise library
capture; Hub-only checks do not request a Notifications-owned library observer. Disposable Docker tools
pull before running, so mutable tags cannot quietly reuse an older local cache.

Nested tests/packages validate the parent's successful dependency audit rather
than repeatedly crawling GitHub. The audit is bound to the exact commit,
declarations, lock contents, Node version, run ID and attempt, with a 24-hour
expiry. Local entry points use the target snapshot's unique run ID; hosted jobs
also bind the GitHub run attempt. Line-ending differences between Windows and Linux do not invalidate it;
changed/missing/expired evidence fails without rediscovery or cross-run fallback.
GitHub calls use `GH_TOKEN` or `GITHUB_TOKEN`; CI refuses anonymous discovery.
403/429 errors include only safe rate-limit headers, never credentials or response
bodies. A shared audit reduces API pressure; it cannot override a real outage,
revoked token or exhausted quota when a fresh discovery is required.

Immediately before publication, the release engine repeats dependency freshness
with `--refresh`, bypassing the parent audit,
and recheck upstream releases, source commits, container digests and the recorded
Node target. A new upstream release or retag during testing/approval blocks
publication and requires a new complete run. Existing release approvals remain.
Catalog-only repair intentionally preserves published history; it does not rebuild
or masquerade as a new latest-target release.

The initial audit in `artifacts/latest-only-final-freshness-audit.log` **failed** on
then-stale Actions, browser/testing libraries and tool images. Current local bump,
read-only freshness, Linux acceptance and upstream vulnerability findings are
recorded separately in [review status](review-status.md). A version bump/freshness
pass does not prove safe dependencies, full acceptance or deployed remote workflows.

## One-time repository setup

- Add secret **`DEPENDENCY_BOT_TOKEN`**, a dedicated bot fine-grained token for
  this repository with Contents, Pull requests, Actions and Workflows write
  access, plus Administration read access to inspect required checks. Use normal
  secret rotation; never put a token in source or workflow inputs.
- Set repository variable **`DEPENDENCY_BOT_LOGIN`** to that token owner's GitHub
  login. Bot identity is checked before any privileged promotion.
- Protect the default branch with **Require branches to be up to date** and the
  required check **`Dependency update acceptance`**. The bot must be allowed to
  merge under the repository's ordinary rules. Required reviews remain required;
  the workflow does not use administrator bypass or auto-approve its own PR.
- Allow squash merging. Retain the existing `plugin-release` environment policy.
  Fully unattended publication is possible only if that policy does not require
  a human approval; this change does not alter repository/environment settings.
- These workflows/scripts must first be installed on the default branch through
  the normal approved source-change process. No local commit/push/publication is
  performed merely by adding this configuration.

A normal `GITHUB_TOKEN` is intentionally not used as the dependency bot: its
commits generally do not trigger subsequent Actions workflows. The dedicated
token allows PR test runs and explicit release dispatch without relying on tag
push side effects.

## Failure and retry behavior

No update is merged or released after a failed gate. A moving base/head requires
rebase and retesting, not a forced merge. A failed release leaves its immutable
tag/draft for investigation; never overwrite assets or move tags. Retrying
promotion reuses an existing suite tag for the same merge commit. Catalog repair
continues through the existing manifest workflow. A passing automated preview is
not completion of the product/live-service gates in [review status](review-status.md).

## References

- [Current release procedures](independent-plugin-releases.md) · [Release checklist](github-release-checklist.md)
- [Testing](testing.md) · [Current blockers](review-status.md)
- [Renovate documentation](https://docs.renovatebot.com/)
