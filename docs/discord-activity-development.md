# Discord Activity and temporary HTTPS development

References: [Activities overview](https://docs.discord.com/developers/activities/overview), [first Activity](https://docs.discord.com/developers/activities/building-an-activity), [local development](https://docs.discord.com/developers/activities/development-guides/local-development), [networking/security](https://docs.discord.com/developers/activities/development-guides/networking).

## No domain or Cloudflare login required

Cloudflare Quick Tunnels provide temporary public HTTPS for a local HTTP development server. `--discord-tunnel` now creates one for the **new disposable** integration fixture:

```powershell
$env:DISCORD_LIVE_TESTS = 'true'
node scripts/integration.mjs --packages=artifacts/runtime-review/discord-theme-r9/linux-x64 --plugins=hub,discord,syncplay --library --live-discord --load-test-env --interactive-discord --discord-tunnel
```

Leave `DISCORD_TEST_PUBLIC_BASE_URL` empty when using this option. Use `--load-test-env`, not the older `--env-file` fixture alias: Node itself pre-parses `--env-file` and can mistake the following fixture flag for a filename. It still requires the configured disposable bot/guild/channel/consenting recipient, Docker, and a user performing commands in Discord. No Cloudflare account, DNS ownership, API token or router port-forwarding is needed. The helper uses a version/digest-pinned official cloudflared container, shares only the owned disposable fixture's network namespace, has no host mounts and cannot select another destination/port. The fixture's HTTPS BaseUrl is generated in memory, checked against the fixture's public server ID, and configured into Hub/Bridge. Tunnel startup/readiness can take several minutes; only readiness reads are retried, never Discord sends.

The public probe runs in an isolated pinned Node-capable Docker runtime because this host's resolver has returned NXDOMAIN for new tunnel names. It performs IPv4 DNS lookup and ordinary hostname-verified HTTPS; it does not change host DNS, use an IP URL, disable certificate checks, send credentials, or follow redirects. Human clients may still need to wait for their resolver's cached NXDOMAIN to expire. The probe is limited to public fixture-info/sentinel paths.

**Exposure:** this is a public development tunnel, not an access-control layer. It exposes the disposable Jellyfin HTTP interface, including its known fixture accounts and synthetic media, to Cloudflare and anyone with the hostname. Never use production users, media, factors, credentials or backups. No tunnel is started automatically for ordinary tests, CI, installed plugins or an existing server. The tunnel is stopped before fixture teardown; normal interrupt handling runs cleanup. Hard process/machine termination can leave resources: inspect and remove only the owned `jellyfin-suite-tunnel-*` / fixture containers.

## Embedded Activities are a separate feature

Bridge now serves an official Embedded App SDK **watch-party companion Activity frontend** at `<BaseUrl>/DiscordBridge/activity/`. It performs the real SDK handshake, offers Open Jellyfin and a strictly validated same-server invitation handoff, uses installed native CSS and fails closed when unavailable. It requests no OAuth/identity scopes and reads no Jellyfin credentials, cookies, user/participant lists or browser token storage. Invitations are cleared on handoff, hiding, navigation, invalid input and failure. No native invite is redeemed or replayed by this frontend. The explicit handoff discloses the chosen bearer URL to Discord's external-link command; the UI warns about this before submission.

**It is not in-Discord video playback.** Playback and native admission open outside Discord in Jellyfin. An embedded video player and its secure proxy/cookie/authentication transport remain unimplemented; the companion is not evidence those gaps are closed. A successful SDK mock, HTTPS probe or command registration is not live Activity acceptance.

For a development app, add these **directory** mappings in this order (substitute your current tunnel hostname and actual BaseUrl):

| Prefix | Target (no protocol) |
|---|---|
| `/web` | `<tunnel-host>/suite/web` |
| `/` | `<tunnel-host>/suite/DiscordBridge/activity` |

The native stylesheet mapping is needed because the Activity is served under a subdirectory and Discord's proxy must allow those native CSS/font resources explicitly. The shortest `/` mapping is last. Enable Activities/supported platforms and launch through the development app's entry point. The app owner/team member must do this in Developer Portal and Discord; this is not configured by bot guild command registration. No client secret is needed for the companion's readiness/external-link operations. A future identity-aware implementation must use proper server-side OAuth, not a bot token in the frontend.

For a real embedded watch-party Activity:

- Enable Activities and the supported platforms in the dedicated development application's Developer Portal. Only app owners/team members can launch a non-distributed development Activity.
- Serve an Activity-specific SDK frontend, not the native Jellyfin administration/login app. Map its `/` prefix to the generated tunnel hostname/directory in **Activities → URL Mappings** (target has no `https://` prefix). Developer Portal configuration remains a human step; bot registration is not a substitute.
- Use the official Embedded App SDK handshake; do not forge parent messages or treat client-provided Discord user/channel IDs as trusted authentication. If OAuth identity is required, exchange codes server-side and verify identity through Discord's API. Bot tokens and client secrets never enter browser code. Discord identity alone must not grant Jellyfin media access or reveal party members.
- Route HTTP/media/WebSocket requests through explicit same-application URL mappings and preserve Jellyfin BaseUrl. Discord's Activity proxy does not support WebRTC. Native guest cookies currently need a deliberate review for the iframe/proxy's `Secure; SameSite=None; Partitioned` cookie contract, origin/CSRF protections and isolation; do not globally weaken native cookies or frame restrictions.
- Reuse Private SyncPlay's authoritative anonymous lease, permission, revocation and playback boundaries. Do not duplicate host authority in Discord or disclose real account/participant/recipient identities in shared UI/Rich Presence.
- Perform human desktop/mobile Activity launch, linked/anonymous access, playback, denial/revocation, CSP/media/cookie and cleanup acceptance. Neither an SDK mock nor a tunnel smoke check closes these gates.

**Remove temporary URL mappings when testing ends.** Discord warns that domains not owned by you may be reused; do not leave a temporary hostname configured or reuse it for production.

## GitHub tests

- Normal Ubuntu/Windows `All tests` jobs install the Activity's lockfile-pinned dependencies, build the SDK bundle through the Bridge project, run frontend lifecycle/privacy/native-control unit tests and the existing .NET suites. Workflow syntax/expressions/shell snippets are checked by pinned actionlint.
- The installed integration fixture verifies the anonymous Activity shell/config/resource allowlist, disabled-state privacy and CSP. This is not an SDK handshake inside Discord.
- `Disposable Jellyfin integration` → **Run workflow** → `cloudflare_tunnel=true` explicitly enables the separate real-network smoke job. It creates only a harmless isolated HTTP sentinel and an accountless temporary tunnel, verifies trusted public HTTPS, and removes labeled owned containers even on ordinary failure/cancellation. Default push/PR/scheduled jobs do not open public tunnels or use Discord credentials. A provider/DNS outage fails this opt-in job rather than being counted as a pass.
- No workflow loads `.env`, accepts personal Discord tokens, forges interactions, or claims human Discord/Activity acceptance. No credential-bearing fixture databases/logs or browser state are uploaded. The new frontend adds a JavaScript dependency distribution; its generated license/inventory needs supplemental owner review.
