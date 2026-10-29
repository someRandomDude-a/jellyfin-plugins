# GitHub workflow map

## Entry points

| Workflow | Trigger and purpose |
|---|---|
| `tests.yml` | Push, pull request, manual or reusable call: plugin checks, root Windows/Linux tests, workflow lint and installed browser acceptance. |
| `integration.yml` | Weekly, manual or reusable call: disposable combinations, native Security and external-service acceptance. The public sentinel tunnel is manual opt-in only. |
| `dependency-maintenance.yml` | Hourly/manual/repository dispatch: Renovate opens protected stable dependency-update PRs. Requires the configured dependency bot. |
| `dependency-bump.yml` | Hourly/manual: apply updates in a disposable checkout, upload a reviewable patch/report and run tests. Never commits or publishes. |
| `dependency-validation.yml` | Eligible same-repository bot PRs: scope validation, shared acceptance gates, then protected merge/release promotion. |
| `release-all.yml` | Manual suite release at an asserted source revision; immutable tags and environment approvals remain required. |
| `release-<plugin>.yml` | Six thin manual dispatchers selecting the independent release engine. |
| `update-manifest.yml` | Manual/reusable catalog update from validated, already-published assets; not a build or release shortcut. |

## Shared implementation

- `latest-targets.yml` discovers current stable software once per workflow graph
  and rejects stale declarations or unreviewed upstream contracts.
- `plugin-checks.yml` runs owner-scoped tests and standalone installation checks.
- `release.yml` is the independent release engine and plugin-tag trigger.
- `actions/setup-latest-tools` installs official checksum-verified stable Node,
  PowerShell and SDK tools. `actions/use-latest-targets` installs those tools and
  binds each child job to the parent's immutable target evidence. Child jobs do
  not need separate Node/.NET installation steps.

Dependency validation calls `tests.yml`, including its root OS matrix; it does
not run a duplicate root matrix. All required reusable jobs must succeed before
promotion. Keep the `Dependency update acceptance` aggregate check configured in
branch protection; workflow configuration alone does not enable that protection.
If branch protection explicitly requires the former duplicate `root-tests`
check, review that configuration when adopting this cleanup: require the shared
root matrix or the aggregate acceptance check rather than leaving an obsolete
check pending. Do not remove the underlying root-test requirement.

## Safety and operation

Read [dependency maintenance](../docs/dependency-maintenance.md) for bot setup,
latest-only rules and update scope, and the [release checklist](../docs/github-release-checklist.md)
before publishing. Freshness or discovery failures stop tests/releases; immutable
release source is not silently rewritten. The `plugin-release` environment,
exact-head/base assertions and catalog validation remain in force.

No default workflow loads the local `.env` or starts human Discord testing.
Local results do not prove hosted CI, physical Windows/ARM or production safety.
See [current review status](../docs/review-status.md) for actual acceptance gaps.
