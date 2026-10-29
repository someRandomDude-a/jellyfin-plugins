# Disposable integration checks

Never mount media or production configuration. `compose.yaml` isolates Jellyfin with `network_mode: none`; the host smoke driver refuses an already-configured server. Use a fresh set of this project's volumes per run.

1. Prefer the [root installed acceptance runner](../../../docs/testing.md). For these component fixtures, use the component directory, root-built Linux x64 package and this run's resolved `JELLYFIN_TEST_IMAGE` (immutable digest). Compose refuses a missing image; no pinned older-server fallback exists. Set `PLUGIN_TEST_DIRECTORY`/`PLUGIN_TEST_VERSION` as described below before starting `jellyfin`.
2. In a separate terminal, run `node test-infrastructure/browser-proxy.mjs`. It binds **only** `127.0.0.1:18496`, proxies the named fixture with `docker exec`, and is not a production proxy.
3. In PowerShell, create a synthetic browser-session file and run the checks:

   ```powershell
   $env:DQ_BROWSER_SESSION='.test-data/browser-session.json'
   node test-infrastructure/smoke.mjs
   Push-Location test-infrastructure/browser
   npm ci --ignore-scripts
   node node_modules/playwright/cli.js install chromium
   Pop-Location
   node test-infrastructure/browser/integrated-check.mjs
   node test-infrastructure/browser/native-check.mjs
   node test-infrastructure/fault-check.mjs
   ```

   On Linux use `export DQ_BROWSER_SESSION=.test-data/browser-session.json`, run npm/Playwright with `test-infrastructure/browser` as the working directory, and add Playwright's `--with-deps` installation option.

The integrated browser check uses real Jellyfin Web login, response injection, header navigation and native admin hosting. It mocks proposal/notice/administrator-approval transport and supplies synthetic media/Enhanced/action-sheet DOM contracts to exercise actions without a media library or Seerr. It checks mobile header visibility/overflow, custom CSS, Escape dismissal, explicit consent/read/Undo, revision-bound admin approval and text-only/session-clearing behavior. This is not a live Enhanced+Seerr+Arr deletion test. The native check exercises the user dropdown/reset, optional per-proposal approval setting and real-deletion opt-in/cancel dialog, then leaves automatic deletion and required admin approval off. Its policy changes affect only this isolated no-media fixture; Seerr's configured port is deliberately unreachable.

**Fault injection intentionally corrupts the fixture's plugin head seal.** The driver verifies the Compose project label, no-network configuration, exact mount destinations and server identity first. It checks host health/user APIs after a worker failure and restart; it must leave the corrupt seal untouched, with the plugin unavailable. It never edits Jellyfin's databases. Do not run this against production or substitute another container.

Screenshots and ephemeral fixture sessions stay under ignored `.test-data/`; do not publish the session file. Stop the loopback proxy when finished and remove **only** these disposable resources:

```powershell
docker compose -f test-infrastructure/compose.yaml down --volumes --remove-orphans
```

The root workflow configuration owns current stable Server/Web acceptance and its
immutable image record; these component scripts are retained developer fixtures,
not an independently deployed compatibility workflow. Match the installed package's
manifest version in `PLUGIN_TEST_VERSION`; a synthetic four-part fixture version is
not the root release engine's tag format. Do not hand-edit native SDK/ABI targets.

For an actual ZIP installation check, extract a package into a fresh directory and set `PLUGIN_TEST_DIRECTORY` to that directory (paths are relative to `test-infrastructure/compose.yaml`, or absolute), plus `PLUGIN_TEST_VERSION` to its manifest version. Compose mounts only that package at the versioned plugin destination; the default source remains `../artifacts/plugin/linux-x64`. Keep these values consistent when running smoke/fault checks. The API smoke driver also verifies the Account Hub v1 adapter with no Hub installed; browser checks use synthetic Hub/2FA contracts. See the [adapter contract](../docs/account-hub-v1.md) for migration and combined-system limits.
