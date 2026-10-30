# Testing

## Dependencies and setup

Use the .NET SDK in `global.json`, current stable Node.js and PowerShell 7+; install
Docker for installed/browser/external fixtures. From the repository root, install
frontend dependencies without lifecycle scripts:

```powershell
npm ci --ignore-scripts --prefix plugins/private-syncplay
```

Dependency-update candidates also run the full automated acceptance before merge
and patch release; see [dependency maintenance](dependency-maintenance.md).

## Normal commands

```powershell
pwsh -File scripts/test.ps1
pwsh -File scripts/test.ps1 -Plugin notifications
pwsh -File scripts/test.ps1 -Plugin notifications -List
```

One entry point uses `scripts/test-plan.mjs` through `scripts/run-tests.ps1`.
`-List` prints an offline JSON plan without bootstrap, builds or fixtures.
Aggregate runs execute the solution once and batch shared/frontend tests once.
Selected-plugin gates compile only that plugin and its actual dependencies;
interoperability remains separate.

### What belongs in each gate

| Layer | Current coverage |
|---|---|
| Common unit contracts | Safe diagnostics/errors, source/target freshness, immutable packages/catalogs, dependency maintenance, fixture safety and current CI wiring. |
| Owner/dependency units | All nine current .NET test projects plus owner-scoped frontend suites. Mixed UI cases use explicit owner prefixes, never incidental words in titles. |
| Installed/browser | Standalone plugins with/without native Security; suite themes, navigation, studio geometry and native accounts/permissions. |
| Integration | All 128 plugin/Security combinations, native recovery/restart/storage loss, read-only database checker, SyncPlay/media/SDK and external-service APIs. |
| Migration/history | Supported schemas, retained keys/identities, prior catalogs/archives and old-format rejection. Old fixture versions here are deliberate inputs, not stale software defaults. |

Workflow assertions live in `scripts/workflows.test.mjs`. The test-plan tests reject
unassigned/missing root `.test.mjs` files and ensure every solution test project is
registered, so consolidation cannot silently drop an owner or a new test file.

Every normal test/package run automatically discovers the latest **stable**
Jellyfin and Security releases. The bootstrap generates build properties with
the corresponding Jellyfin SDK, ABI and upstream EF/SQLite versions. No checked-in
version update, update flag or manual download is needed. `Jellyfin.Target.props`
is only a historical IDE baseline; raw IDE/`dotnet` commands without bootstrap
are not latest-target acceptance commands.

Each run retains `targets.json` and `Jellyfin.Latest.props` under
`artifacts/test-runs/<run-id>/`. Nested processes inherit `SUITE_TEST_TARGETS`,
`SUITE_TEST_RUN_ID` and `SUITE_JELLYFIN_PROPS`; matrix children and browser
containers consume the same snapshot. Snapshots expire after 24 hours, are not
permanent pins and are never an offline/older-version fallback. Start a new run
without inherited `SUITE_TEST_*` variables to discover releases again.

Discovery, authenticated asset checksum, image pull, SDK mismatch and actual
runtime-version failures stop the run. Native database/user and Security source
contracts must match reviewed fingerprints before a new ABI/version can receive
compatibility approval. Fingerprints are safety baselines, not software targets;
contract changes are not automatically trusted or worked around.

## Installed and external acceptance

Build new immutable packages first; never replace an earlier ZIP:

```powershell
# Replace vX.Y.Z with an unused candidate version.
pwsh -File scripts/package.ps1 -Tag vX.Y.Z -OutputDirectory artifacts/current-candidate
node scripts/integration.mjs --packages=artifacts/current-candidate --security=latest --proof --reset-session --wiring-check --permissions-check --library --v1-flows --database-safety --storage-recovery
node scripts/test-combinations.mjs --packages=artifacts/current-candidate --security=latest --wiring-check --output=artifacts/current-combinations --jobs=2
```

Package and installed commands reject mismatched SDK/ABI targets. CI carries one
snapshot between build and installed steps. Independent commands discover fresh
targets if no run snapshot is inherited; a release changing between those runs
can therefore correctly reject older packages.

- Add `--theme-check --account-responsive --notification-studio` for [browser/theme acceptance](theme-acceptance.md).
- Standalone studio acceptance uses `--plugins=notifications --library --notification-studio` without Hub or Bridge.
- Add `--diagnostics-ui --public-onboarding-ui --public-link-ui` to a disposable
  full-suite run without native factor enrollment for shared diagnostics, signup
  and same-tab native linking. Standalone repeats use `--plugins=notifications
  --diagnostics-ui` and `--plugins=hub --public-onboarding-ui --public-link-ui`.
  Diagnostics/signup browser flags currently reject native Security; the link
  flag also runs with Security installed. It seeds only synthetic challenge rows
  in that newly owned fixture and labels evidence accordingly—never as human
  Discord issuance. The temporary challenge input is removed after browser checks.
- Add `--public-link-factor` alongside `--public-link-ui --security=latest` to
  enroll the disposable link account through native Security's public APIs and
  complete its actual browser factor challenge before review/consent. It checks
  that the continuation cookie survives and is not consumed before verification.
  Factor material is confined to the owned temporary fixture input, deleted after
  the browser run, and omitted from evidence/logs. No production factors or private
  Security state are modified.
- `node scripts/seerr-api-integration.mjs` tests current stable Seerr with current
  Jellyfin through public APIs on newly owned disposable servers.
- `node scripts/metadata-arr-acceptance.mjs` creates uniquely named disposable
  latest Sonarr/Radarr instances, verifies versions and runs real provider reads.
  Only owned synthetic fixture databases are seeded. Retained/operator containers
  are never stopped, seeded or reused.
- Enhanced browser contracts load sources from the current stable release's
  resolved commit, not a permanently pinned fixture revision.

Resolved releases, image digests, Security asset SHA-256 and source contracts are
evidence for that run. Intentional older-version migration, rejection and upgrade
examples remain tests of history, not current software fixture defaults. Build
tools/browser automation and library lockfiles remain reproducible source inputs,
but CI and releases now reject stale versions: they cannot quietly stay on an
older toolchain just because the server target is current. The shared
`latest-targets.yml` gate records the current Node/SDK, reviewed upstream releases
and immutable pulled image digests once for the workflow graph. Children use the
same artifact; build/test jobs verify their installed Node/SDK. The release engine
recheck registry/source/image freshness before publication. See
[latest-only gates](dependency-maintenance.md#latest-only-gates-on-every-testrelease-graph).

A passing local unit run against the currently declared libraries is not approval
of dependency freshness. The read-only `node scripts/check-dependency-freshness.mjs`
audit must pass too; outdated packages/locks, registry failures or missing policies
block CI/release rather than silently editing source or falling back. Raw GitHub
Actions `dotnet` builds without generated latest properties now fail explicitly.

## Safety and evidence

No default command loads `.env`, contacts production services or starts a human
fixture. Disposable tests own their data and containers. Live Discord requires
the separately authorized destinations and explicit readiness workflow; never
print secrets or retry an uncertain external effect blindly.
For this work, Discord human tests must be last: do not launch the human server,
tunnel or live bot acceptance until the owner explicitly says they are ready.

Keep current results in [validation status](review-status.md). Preserve raw logs,
including failed attempts, in the run's artifact directory.
A build, mock or older fixture pass does not certify installed latest software,
live delivery, physical-device Push, destructive production behavior or v1 release.

## References

- [Plugin installation](../README.md#installation-and-upgrades) · [Documentation index](README.md)
- [Latest-only dependency policy](dependency-maintenance.md)
- [Browser/theme checks](theme-acceptance.md) · [Independent releases](independent-plugin-releases.md)
- [Current validation](review-status.md)
