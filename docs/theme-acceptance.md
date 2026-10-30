# Installed browser and theme acceptance

## Dependencies and setup

Use the declared .NET SDK, current stable Node.js, PowerShell 7+ and Docker. Create
fresh immutable packages with the root wrappers before running browser acceptance.
The suite fixture needs all six plugins; it owns disposable accounts/media and
never mounts production state. See [testing setup](testing.md).

## Run and coverage

Use the normal disposable integration runner after creating fresh packages:

```powershell
node scripts/integration.mjs --packages=artifacts/current-candidate --wiring-check --permissions-check --theme-check --account-responsive
```

The runner starts current stable Jellyfin, runs Chromium in the owned server's
network namespace and removes the container on completion. It never loads `.env`
or production data. A loopback relay provides a normal secure context without
disabling browser security; this is not trusted-TLS/proxy acceptance.

Coverage targets 30 workspace/native-theme cases, 18 account/Discord/reset
responsive cases at 320/390/1280px, standalone settings, keyboard/focus behavior,
notification scrolling/menu actions, four dark/light × mobile/desktop native
palette and skin-override cases. Current-source passes are recorded separately.
Swipe/drag consent previews and action trays also check custom-skin foreground,
accent, typography and CSS shape overrides in dark/light narrow/wide browser cases.
These are bounded skin-contract tests, not certification of every third-party
skin or physical touch device.

Personal notification categories additionally exercise Watchlisted/Favorited
toggles, Important/Silent choices, native radio-keyboard focus and narrow/wide
layouts in both themes. These use only the disposable user's preferences.
Discord's administrator permission editor adds four dark/light × 320/1280px cases
for nine native-user actions, inheritance/reset and unsaved drafts. Shared help
bubbles must fit visually **and** avoid expanding document scroll width; the isolated
`scripts/help-layout-acceptance.cjs` regression also covers LTR/RTL and Escape at
320/390/1280px using the declared Playwright tool.
Enhanced
calendar rendering uses this run's latest stable upstream sources with synthetic
data, not production Arr/Seerr calls. Actions wait for actual post-invalidation
DOM replacement, not retries of the action.

Results and screenshots remain in the printed disposable evidence directory:
`theme-results.json`, `account-responsive-results.json`, `admin-screenshots/`
and `account-screenshots/`. Fixture-only credentials must not be uploaded as
evidence. `--keep` is diagnostic opt-in; remove only its printed owned container.

This does not certify all skins, physical mobile/Push devices, human multiplayer
hosting, Modern/Legacy combinations, trusted TLS or every permission and
fault path. See [testing](testing.md) and [current evidence](review-status.md).

## Native-page and fault checks

Test administrator page registration/discovery separately from ordinary-user entry
points. Static embedded resources must contain no secrets; live admin APIs must
reject anonymous, ordinary-user and API-key callers, including a retained session
after demotion/disable. Never relax the native dashboard administrator route to
make ordinary controls reachable. Hosted-Web injection must not persistently rewrite
Web files or weaken CSP, and unsupported markup should fail without replacing commands.

Component smoke/proxy/fault scripts are developer fixtures, not standalone deployment
instructions. Resolve this run's latest image explicitly for Compose.
Fault checks must verify the exact owned project, network,
mounts and server identity before corrupting only its disposable plugin seal.
Native Jellyfin health must survive while the plugin fails closed without repairing
that seal. Never point these tools at retained/operator instances or production data.

## References

- [Testing](testing.md) · [Latest-only dependency gates](dependency-maintenance.md)
- [Notification controls and gestures](notification-channels.md)
- [Current evidence and screenshots](review-status.md)
