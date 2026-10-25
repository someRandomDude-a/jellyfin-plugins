# Seerr deletion requests for Jellyfin 12.x

A Jellyfin Server plugin with integrated media/request removal actions, inline Notifications controls and native administrator settings. New removal requests require standalone Notifications with registered types, scoped access and command support.

> **Real deletion requires a new explicit administrator opt-in.** Stock Seerr is trusted to choose the Arr scope and remove its record/files, retaining requests. The plugin enforces current consent, complete activity coverage and delay, persists a claim before dispatch and never automatically resends uncertain attempts. Local authenticated seals do **not** prevent restoring the database, keys and seals together to an older valid state. See [implementation status and test boundaries](docs/implementation-status.md).

## Included

- Global/per-account Denied / Own requests / Any accessible item permissions.
- Canonical movie/whole-series proposals; duplicate submissions approve the existing proposal without changing its owner.
- Snapshot-bound Yes/No, owner-only Undo, 14-day activity protection and a fresh minimum 48-hour eligibility delay.
- Media/detail/right-click removal actions and Jellyfin-Enhanced Requests actions, including verified owned unfulfilled requests.
- Compact queue icon/popout, inline Allow/Keep/Undo controls, Active/History views in Enhanced Requests, and an agenda of removal stages/earliest dates on Enhanced Calendar.
- Personal delay floors and automatic owner/required-viewer notices with opt-out, a top-bar unseen badge and explicit Mark read.
- User dropdown/reset controls and server-side, one-click import of an unambiguous Enhanced Seerr connection without exposing its key.
- Optional per-proposal administrator approval, off by default; it never replaces viewer consent or shortens delays.
- Bounded off-event-thread activity collection and fail-closed evidence/coverage diagnostics; plugin failures are contained rather than stopping the server.
- Encrypted, authenticated SQLite journal, retained history, tamper/replay detection and no silent state reset.
- Live session/admin authorization, anonymous ordinary-user DTOs and a separate native admin page.
- One-shot stock-Seerr execution with durable claims, uncertainty and a separate FilesDeleted checkpoint based on Seerr's deletion report.

## Build and test

Requires the **.NET 10 SDK**. The shared build target is **Jellyfin Server/Web 12.3**. Installed local checks use Linux amd64; this is not certification of every skin, Enhanced version or server platform.

```powershell
dotnet build DeletionQueue.slnx -c Release
dotnet test DeletionQueue.slnx -c Release
./scripts/package.ps1 -Runtime linux-x64
```

The package script creates `artifacts/DeletionQueue-0.1.0.0-linux-x64.zip` with MD5/SHA-256 sidecars, a package descriptor and installed `meta.json`. Existing packages are not overwritten; choose a fresh `-OutputDirectory` when rebuilding. Extract into a new Jellyfin plugin directory and restart. Back up existing plugin state first; do not overwrite another plugin's directory.

## Releases and repository installation

Tagged releases are built and verified by GitHub Actions. The exact triggering tag supplies the release version: `v0.1.0.1` builds version `0.1.0.1` throughout the DLLs, installed manifest and repository catalogs, without editing a version file. `build/Plugin.props` keeps the untagged-build default, ABI, channel and immutable plugin identity. Tags must use four numeric components; see [release setup](docs/releases.md).

After publication, register **one** platform/channel JSON URL in Jellyfin's plugin repositories. For stable releases on standard Linux x64 Docker:

```text
https://raw.githubusercontent.com/someRandomDude-a/seerr-deletion-requests/manifests/stable/manifest-linux-x64.json
```

Linux ARM64 and Windows x64 have separate catalogs because Jellyfin cannot choose an architecture from one manifest. Stable subscribers never receive preview versions. See [release setup, all catalog URLs and publishing/recovery instructions](docs/releases.md). These URLs are created by the first successful release workflow; adding the workflow does not publish or enable deletion.

## Use

1. Open **Dashboard → Plugins → Deletion queue**. Save the Seerr URL/key, or select **Use Jellyfin-Enhanced connection**. Only one enabled Enhanced Seerr URL is imported; ambiguous/multiple endpoints require manual configuration. Later Enhanced changes require a fresh import. The configured Jellyfin server ID must match this server.
2. Set global/per-user proposal permission using the user dropdown. **Reset selected user** restores inherited permission and default personal preferences, preserving proposals, consent, history and existing deadline floors. Optionally enable **Require administrator approval for each proposal** and approve/revoke in the backlog. Approvals are bound to the target, activity and policy; the approver's current admin role and visibility are rechecked before dispatch.
3. Use **Request removal** on a movie/whole-series detail page, media card, right-click menu or supported Enhanced request. Review the title/quality, then confirm. Duplicate submissions register consent on the existing active request, retaining its original owner and expiry; repeated current Yes consent is idempotent.
4. Open the **notification bell in Jellyfin's top bar** to review proposals, explicitly allow/keep media, Undo your own unclaimed proposal, mark notices read or change preferences. Viewing/Help/notification polling never records activity or consent.
5. If desired, explicitly enable **automatic Seerr-managed deletion** and confirm the real-deletion warning. Old preview-only opt-ins cannot activate this transport. Every restart requires a fresh continuous activity window (default 14 days), then a full minimum two-day eligible delay. Incomplete inventory/activity evidence blocks removal.

An ASP.NET startup filter injects same-origin resources into Web without modifying Web files. File Transformation is not required. The full queue is embedded in Enhanced's Requests page, not a second standalone application. The popout's **Open full queue** action appears only when the enabled Enhanced plugin reports its Requests page enabled; users are not shown missing-dependency instructions. Administrator settings explain this integration. Native TV/mobile clients that do not load Jellyfin Web are not modified. Unsupported Web/Enhanced markup may omit controls rather than replacing native commands.

### Removal lifecycle and playback

An unaccepted request expires after 30 days. A distinct administrator **Deny request** action expires it immediately; revoking approval through the existing API remains a separate, non-terminal operation. Accepted requests waiting through their safety delay and already claimed/uncertain execution are not expired by the intake deadline. Expired requests remain in History, and a new request can be created.

An administrator requesting removal also records administrator approval when that policy is enabled, without bypassing other viewers, current activity checks or the delay. Fresh activity/policy changes invalidate that approval as before.

When the owner starts playback, the earliest removal time is pushed back by at least the effective delay floor, fresh consent is required, and the intake clock restarts. A dedicated owner-only notification is emitted once per playback session, not on each progress event. It offers **View request** and owner-only **Cancel request**. Administrators can change its plain-text message (`{media}`, `{days}`) and toast duration (0–10 seconds) under **Notifications → Registered notification types → Playback resets removal**. The default toast lasts six seconds; the notice remains in the inbox.

### Unfulfilled requests and incomplete downloads

Request IDs/media identities come from supported Enhanced card metadata and Seerr HTTP reads, never titles or usernames. Seerr's native Jellyfin user mapping and server association must verify ownership. When no native media item exists, visibility is limited to the verified request owner. Unmanaged pending requests use Seerr's **decline** endpoint after normal queue safeguards, preserving request history; the plugin never deletes `/request/{id}`. Managed incomplete media uses Seerr's existing media-file removal endpoint only after verifying the managed edition and ownership of every live request in its scope. Unverified ownership, a changed target or unsupported state fails closed.

Calendar integration is a scoped removal agenda alongside Enhanced's calendar: consent-stage requests have no scheduled deletion date, and queued dates are explicitly earliest times, not a promise that deletion will occur. The owner's Enhanced 12.13/custom-skin installation and real upstream download/removal behavior still require acceptance; local gateway tests use controlled HTTP responses, and installed browser tests use controlled Enhanced DOM/API contracts.

Latest-stable Server/Web compatibility, hostile-input and browser/fault-isolation tests run in a separate scheduled/PR workflow and gate releases. An in-process plugin cannot promise immunity from all host crashes; see the [safety review and limits](docs/safety-review.md).

## Isolated Docker integration tests

Never mount a real media library or use production credentials. These tests configure a fresh synthetic server and refuse an already-configured instance.

```powershell
dotnet publish src/Jellyfin.Plugin.DeletionQueue/Jellyfin.Plugin.DeletionQueue.csproj -c Release -r linux-x64 --self-contained false -o artifacts/plugin/linux-x64
docker compose -f test-infrastructure/compose.yaml up -d jellyfin
docker compose -f test-infrastructure/compose.yaml run --rm smoke
```

The fixture uses `network_mode: none` and no media mounts. If Docker cannot start a shared network namespace, run `node test-infrastructure/browser-proxy.mjs` in one terminal and `node test-infrastructure/smoke.mjs` in another. The proxy exposes only the synthetic fixture at `127.0.0.1:18496`, supports HTTP but not WebSockets, and must not be used for production.

See [repeatable browser and fault-isolation checks](test-infrastructure/README.md) for the themed bell, native admin page and corrupt-plugin-storage survival tests.

After stopping the test proxy, remove **only this test project's disposable containers/volumes** with `docker compose -f test-infrastructure/compose.yaml down --volumes`. Do not reuse its volumes for another test run: the smoke driver intentionally refuses to reconfigure an existing server.

## Design and operations

- [Implementation plan](docs/implementation-plan.md)
- [Optional Account Hub v1 integration, migration and handoff](docs/account-hub-v1.md)
- [Implemented behavior and remaining gates](docs/implementation-status.md)
- [Native-page verification](docs/jellyfin-12-native-pages.md)
- [Recovery and stopped backups](docs/recovery.md)
- [Host isolation, hostile input and themed notifications](docs/safety-review.md)
