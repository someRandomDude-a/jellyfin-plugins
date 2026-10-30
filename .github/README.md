# GitHub workflow map

## Entry points

| Workflow | Trigger and purpose |
|---|---|
| `tests.yml` | Push, pull request, manual or reusable call: one Windows/Linux unit matrix, standalone installation matrix, lint and suite browser acceptance. Select `all` or one plugin. |
| `integration.yml` | Weekly, manual or reusable call: disposable combinations, native Security and external-service acceptance. The public sentinel tunnel is manual opt-in only. |
| `.github/dependabot.yml` | Native hourly grouped discovery for NuGet, npm, SDK, Actions and Compose; no custom discovery secret. |
| `dependency-maintenance.yml` | Hourly/manual residual updater for embedded tools/images and upstream markers. Supplements a pending Dependabot PR or opens a residual-only PR. |
| `dependency-validation.yml` | Eligible same-repository bot PRs: dependency-only scope and full shared acceptance, without custom secrets. |
| `dependency-promotion.yml` | Trusted default-branch `workflow_run` phase: verify the successful exact-head/base run, protected merge and immutable patch release. |
| `release-all.yml` | **Release plugins**: one manual selector for `all` or an independent plugin, plus plugin-prefixed tag triggers. Exact source, immutable tags and protected approval remain required. |
| `update-manifest.yml` | Update platform catalogs and live README metadata from validated published assets; no rebuild. |

## Shared implementation

- `latest-targets.yml` is a reusable-only entry gate that
  discovers stable software and audits dependency freshness once per graph.
  Child jobs validate the same source/declaration/lock/Node hash and run attempt;
  audits expire after 24 hours. Publication always rediscovers, even with an audit.
- `tests.yml` owns both suite and selected-plugin checks. Full root tests already
  cover every plugin, so there are no extra six Windows/Linux unit matrices.
- The suite and independent releases share `release-all.yml`; six dispatch
  wrappers and the duplicate release engine are removed.
- Successful publication sets GitHub's Latest marker. Catalog publication also
  writes `releases.json` for the README's per-plugin version/ABI badges.
- `actions/setup-latest-tools` installs official checksum-verified stable Node,
  PowerShell and SDK tools. `actions/use-latest-targets` installs those tools and
  binds each child job to the parent's immutable target evidence. Child jobs do
  not need separate Node/.NET installation steps.

Dependency validation calls the same tests and integration gates. Keep
`Dependency update acceptance` required in branch protection. Migrate any old
per-plugin `unit` or duplicate `root-tests` check names to the root OS matrix,
standalone installation matrix and acceptance aggregate as appropriate; never
drop their test coverage. Repository protection settings are not edited here.

## Safety and operation

Read [dependency maintenance](../docs/dependency-maintenance.md) for bot setup,
latest-only rules and update scope, and the [release checklist](../docs/github-release-checklist.md)
before publishing. Freshness or discovery failures stop tests/releases; immutable
release source is not silently rewritten. The `plugin-release` environment,
exact-head/base assertions and catalog validation remain in force.

GitHub discovery uses the job token explicitly (including composite actions).
403/429 failures report safe rate-limit headers and remain blocking. Shared
audits avoid repeated registry crawls from nested test/package entry points;
there is no cross-run cache, anonymous retry or stale-version fallback. Old
workflow run history may remain visible in Actions after the files are removed.

No default workflow loads the local `.env` or starts human Discord testing.
Local results do not prove hosted CI, physical Windows/ARM or production safety.
See [current review status](../docs/review-status.md) for actual acceptance gaps.
