# GitHub workflow map

## Entry points

| Workflow | Trigger and purpose |
|---|---|
| `tests.yml` | Push, pull request, manual or reusable call: one Windows/Linux unit matrix, standalone installation matrix, lint and suite browser acceptance. Select `all` or one plugin. |
| `integration.yml` | Weekly, manual or reusable call: disposable combinations, native Security and external-service acceptance. The public sentinel tunnel is manual opt-in only. |
| `dependency-maintenance.yml` | One hourly/manual workflow: Renovate opens protected stable dependency PRs; a separate disposable preview job exports and tests an update patch without committing or publishing. Bot discovery requires the configured bot. |
| `dependency-validation.yml` | Eligible same-repository bot PRs: scope validation, shared acceptance gates, then protected merge/release promotion. |
| `release-all.yml` | **Release plugins**: one manual selector for `all` or an independent plugin, plus plugin-prefixed tag triggers. Exact source, immutable tags and protected approval remain required. |
| `update-manifest.yml` | Manual/reusable catalog update from validated, already-published assets; not a build or release shortcut. |

## Shared implementation

- `latest-targets.yml` is the seventh workflow: a reusable-only entry gate that
  discovers stable software and audits dependency freshness once per graph.
  Child jobs validate the same source/declaration/lock/Node hash and run attempt;
  audits expire after 24 hours. Publication always rediscovers, even with an audit.
- `tests.yml` owns both suite and selected-plugin checks. Full root tests already
  cover every plugin, so there are no extra six Windows/Linux unit matrices.
- The suite and independent releases share `release-all.yml`; six dispatch
  wrappers and the duplicate release engine are removed.
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
