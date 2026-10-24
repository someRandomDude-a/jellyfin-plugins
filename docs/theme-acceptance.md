# Local installed-theme acceptance

`scripts/theme-acceptance.cjs` runs real Chromium against a disposable installed Jellyfin fixture, never a production server. It does not load `.env` or retain browser credentials/screenshots.

1. Package a new candidate using `scripts/package.ps1` into a new directory (never replace an existing ZIP).
2. Run `scripts/integration.mjs --packages=<candidate> --plugins=hub,discord,deletion,syncplay --v1-flows --keep`. Keep the printed container name and temporary URL.
3. Run `mcr.microsoft.com/playwright:v1.56.1-noble` with the repository's `scripts` directory mounted read-only at `/checks`, `DISPOSABLE_THEME_TEST=true`, and `THEME_TEST_BASE_URL=http://host.docker.internal:<printed-port>/suite`. Add `--add-host=host.docker.internal:host-gateway`. Invoke `/bin/sh /checks/run-theme-acceptance.sh`.
4. Preserve the aggregate result JSON, then remove only the owned fixture container with `docker rm -f <printed-container>`.

The fixture uses known disposable setup credentials. Its allowlisted local URL is relayed through container loopback so Web Crypto has a normal browser secure context; no browser security flags are disabled. This relay is not TLS/proxy acceptance.

Coverage: ten workspace tabs × native dark/light CSS × 320/390/1280px (60 cases), panel overflow, associated control labels, native checkbox hooks, keyboard Home/End/ArrowRight navigation and a scoped custom-theme button foreground override. Native transitions must settle before computed-color assertions. Plugin CSS remains scoped layout, not an alternate palette.

Not covered: Security/Audit acceptance, expanded editors/dialogs, standalone plugin settings, actual media/player rendering, full custom theme distributions, contrast, physical mobile devices, native Modern/Legacy switching or trusted TLS. Audit was attempted separately and reported an authorization failure during iteration; its cause is not established and it is not a pass.
