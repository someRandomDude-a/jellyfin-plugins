# Automatic dependency maintenance

## Prerequisites and setup

Configure Dependabot, the residual/promotion bot secret, required checks, squash merging and
release-environment policy using the setup section below. Changes take effect
only after the normal reviewed deployment; never bypass approvals.
The [current review status](review-status.md) records direct-dependency freshness
and acceptance results separately. Bot discovery is not proof that an upgrade passed acceptance.

Dependabot checks supported ecosystems **hourly**, grouped across ecosystems by
`.github/dependabot.yml`. It uses public registries without a custom token.
`dependency-maintenance.yml` checks the remaining embedded declarations hourly, on manual
dispatch, or on a `repository_dispatch` event of type `dependency-update`.
An upstream webhook can send that event for earlier discovery; GitHub does not
provide a universal “any dependency released” event.

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

Bootstrap logs each fetch stage and applies a hard **30-second metadata** and
**300-second archive** deadline across connection, headers and the entire body.
Slow trickle traffic cannot reset the deadline. Failures preserve partial files
without extracting/executing them, retrying indefinitely or selecting older tools.
Explicit `-MetadataTimeoutSeconds`/`-DownloadTimeoutSeconds` values remain bounded
to 1–900 seconds. `tests/tooling/tool-http.test.mjs` exercises real stalled/trickling
local HTTP responses through PowerShell; normal owner/suite test plans include it.

The local test/package entry points bootstrap tools and check dependency freshness
before execution. CI uses `.github/actions/setup-latest-tools` in the parent
target gate and child target-binding action. Node/PowerShell/SDK versions bind
children to the parent evidence; a new release appearing mid-graph causes a
mismatch failure rather than mixing toolchains. The shared release engine preflights
tools/freshness **before creating a tag** as well as before tests/publication.
Docker daemon, Git and OS utilities remain runner prerequisites; managed browser,
lint, tunnel and fixture images are handled by the image freshness/bump policies.

Dependabot owns standard manifests/locks. The residual workflow updates only
embedded image/tool versions, numeric Node declarations and upstream markers.
It supplements a pending Dependabot PR, preserving its native changes, or opens
a separate `dependency-maintenance/` PR if no Dependabot PR is pending. Candidate
files are read as Git objects, never checked out or executed by the publisher.
Immutable release source is never automatically rewritten: stale releases stop
until the tested dependency update is merged.

### Manual reviewable bump (same engine locally and in Actions)

**Actions → Dependency maintenance** publishes or supplements a residual dependency
PR; it does not merge or release it. For a local full reviewable bump without a
commit, push or publication, use current stable Node/npm:

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

Use `node scripts/update-dependencies.mjs --apply --residual` for only the custom
updater's scope, without npm lock refresh or dependency installation. Residual
verification covers that scope only; full freshness remains mandatory in PR CI.
After bumping, run Linux root/installed acceptance locally. GitHub retains its
Windows/ARM/platform jobs; no live human fixture is launched by dependency discovery.

### Protected recurring automation

1. Dependabot discovers NuGet/npm dependencies and locks, .NET SDK,
   GitHub Actions (including composite actions) and Docker Compose. The custom
   residual workflow covers embedded tool/container declarations and upstream releases.
   Native version updates share a multi-ecosystem group with no cooldown. Major updates are eligible,
   but an incompatible update must fail tests rather than bypass them.
2. `dependency-validation.yml` accepts same-repository `dependabot/` branches
   authored by `dependabot[bot]`, or `dependency-maintenance/` branches authored by
   the configured residual bot. Trusted **base-branch** tooling checks
   that only dependency declarations/locks changed and generates the commit body.
3. All selected-plugin Windows/Linux gates, standalone installed tests, full root
   tests on Windows/Linux, installed browser checks, the 128-case matrix, native
   Security/session checks, SyncPlay integration and real Seerr/Sonarr/Radarr API
   fixtures must pass. Human/live-service acceptance is never silently launched
   or represented as tested. Failures/cancellations/skipped required jobs block
   promotion.
4. A separate default-branch `dependency-promotion.yml` workflow runs after successful
   validation. It checks the API-reported workflow path, event, repository, PR
   association, exact head/base and successful acceptance job in the current run
   attempt. It recomputes the entire dependency-only diff using trusted scripts,
   without candidate artifacts/caches, checkout or dependency installation.
   The bot squash-merges only the tested head onto the unchanged tested base.
   The final commit lists **each changed direct and lockfile/transitive dependency**,
   old/new versions, integrity/digest changes and affected files. Non-dependency
   source/config changes require separate review. No general external-code
   execution is enabled in Dependabot; custom updates do not install dependencies.
5. The promotion script picks the next suite **patch** above every existing
   suite/plugin version and dispatches `release-all.yml` at the exact merged SHA.
   That engine rechecks/builds/validates all 18 packages, publishes an immutable
   integration-preview release and updates existing catalogs. The itemized commit
   body becomes the release notes. Existing `plugin-release` environment approvals
   remain in force; automation does not disable reviewers or bypass branch rules.

Software under test—Jellyfin, Security, Enhanced, Seerr and Arr—continues to be
resolved automatically per run, not converted into permanent fixture pins.
`.github/upstream-dependencies.json` lets the residual updater detect new releases and
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

A version bump or freshness pass does not prove full acceptance. See
[validation status](review-status.md) and retain evidence for the exact run.

## One-time repository setup

### Discovery ownership

| Dependency source | Native Dependabot |
| --- | --- |
| NuGet projects, npm manifests/locks | Supported; preserve server-owned package exclusions |
| GitHub workflows and composite Actions | Supported |
| `global.json` SDK | Supported through `dotnet-sdk` |
| Compose manifests | Configured through `docker-compose`; there are no tracked Dockerfiles |
| Images/tool versions embedded in scripts or workflow commands | Custom residual updater |
| `.github/upstream-dependencies.json` release markers | Custom residual updater |
| Full acceptance, protected merge and exact-SHA patch release | Keep the project workflows |

Nested NuGet projects and both npm manifests are covered. Server-owned SDK,
database and renderer packages and the historical IDE baseline are excluded.
SDK prereleases are disabled in `global.json`. Reviewed source fingerprints are
never updated automatically. Add an explicit `docker` entry if Dockerfiles are added.

Dependabot PR workflows normally receive a read-only `GITHUB_TOKEN` and
**Dependabot secrets**, not ordinary Actions secrets. Do **not** copy the promotion
credential into Dependabot secrets or use `pull_request_target` to execute candidate
code. The separate `workflow_run` promotion uses trusted default-branch tooling.
Private registry credentials, if ever needed, belong in narrowly scoped Dependabot
secrets, not a general repository-write bot token. Public discovery needs no custom
bot token. Prefer short-lived GitHub App credentials for privileged promotion in a
future migration.

### Credential handling

The current custom credential is referenced only through GitHub Actions
`secrets.DEPENDENCY_BOT_TOKEN`; `DEPENDENCY_BOT_LOGIN` is public identity, not a
credential. Never supply credentials through repository variables, dispatch inputs,
URLs, command arguments, source files, patches or artifacts. Do not enable shell,
HTTP or Git debug tracing in credential-bearing jobs.

Maintenance and promotion checkouts do not persist credentials. The custom
token is scoped to final trusted publication/promotion steps, never passed into candidate test
jobs or inherited by reusable workflows. Promotion recomputes its summary rather
than downloading candidate-produced artifacts. Subprocess failures discard output
and nested errors instead of printing potentially credential-bearing diagnostics.
Dependabot receives no custom bot token. The residual discovery step uses only
the read-only job token; only its final publication step receives the custom secret.

These controls reduce exposure; they do not establish that historical logs or
remote secret configuration have never leaked. Secret values and `.env` files were
not read during this assessment, and remote logs/settings were not audited. If
exposure is suspected, revoke/rotate the credential in GitHub and review affected
logs/artifacts; masking is not a substitute for isolation.

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

The dedicated residual/promotion token ensures custom branch updates trigger PR
tests and permits explicit release dispatch. It is not needed by native Dependabot.
Do not copy it into Dependabot secrets. Activate the configuration through normal
review on the default branch and enable Dependabot version updates in repository
settings if disabled. No remote settings or secrets are changed by local edits.

## Failure and retry behavior

No update is merged or released after a failed gate. The residual publisher never
force-pushes or overwrites an existing residual branch. An existing residual PR
requiring fresh versions must be rebased/reviewed; a pending grouped Dependabot PR
can instead receive the latest residual supplement. Ambiguous multiple native PRs
require consolidation. Newly stale native dependencies wait for Dependabot; the
residual workflow does not silently take over native manifests. Full freshness
may remain blocked until that happens. A moving base/head requires
rebase and retesting, not a forced merge. A failed release leaves its immutable
tag/draft for investigation; never overwrite assets or move tags. Retrying
promotion is refused for closed/merged PRs, preventing a replay from publishing a
second release. If release dispatch fails after merge, use the protected release
selector at the exact merge SHA and its existing immutable tag, if any. Catalog
repair continues through the existing manifest workflow. Passing automation is
not completion of the product/live-service gates in [review status](review-status.md).

## References

- [Current release procedures](independent-plugin-releases.md) · [Release checklist](github-release-checklist.md)
- [Testing](testing.md) · [Current blockers](review-status.md)
- [Dependabot ecosystems](https://docs.github.com/en/code-security/dependabot/ecosystems-supported-by-dependabot/supported-ecosystems-and-repositories)
- [Dependabot Actions security](https://docs.github.com/en/code-security/dependabot/working-with-dependabot/automating-dependabot-with-github-actions)
