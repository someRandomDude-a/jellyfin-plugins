# Seerr deletion requests for Jellyfin 12.x

## Dependencies and installation

Install Deletion Queue and standalone Notifications from the
[combined platform catalog](../../README.md#installation-and-upgrades), restart
Jellyfin and open **Dashboard → Plugins → Deletion Queue**. New intake requires
Notifications with registered types, scoped access and commands. Existing consent,
Undo and history remain available during provider failure. Hub/Bridge/Enhanced are
optional; Seerr and explicit destructive opt-in are required for supported real
media operations. Back up plugin state and original keys before upgrades.

Use root test/package wrappers with the SDK in `global.json`, current stable
Node.js and PowerShell 7+. Configure publication using **Release Deletion Queue**
and the [current release policy](../../docs/independent-plugin-releases.md).

## Overview

A Jellyfin Server plugin with integrated media/request removal actions, inline Notifications controls and native administrator settings. New removal requests require standalone Notifications with registered types, scoped access and command support.

> **Real deletion requires a new explicit administrator opt-in.** Stock Seerr is trusted to choose the Arr scope and remove its record/files, retaining requests. The plugin enforces current consent, complete activity coverage and delay, persists a claim before dispatch and never automatically resends uncertain attempts. Local authenticated seals do **not** prevent restoring the database, keys and seals together to an older valid state. See [current status and test boundaries](../../docs/review-status.md).

## Included

- Global/per-account Denied / Own requests / Any accessible item permissions.
- Canonical movie/whole-series proposals; duplicate submissions approve the existing proposal without changing its owner.
- Snapshot-bound Yes/No, owner-only Undo, 14-day activity protection and a fresh minimum 48-hour eligibility delay.
- Media/detail/right-click removal actions and Jellyfin-Enhanced Requests actions, including verified owned unfulfilled requests.
- Compact queue icon/popout, inline Allow/Keep/Undo controls, Active/History views in Enhanced Requests, and dated removal events/filter within Enhanced Calendar.
- Personal delay floors and automatic owner/required-viewer notices with opt-out, a top-bar unseen badge and explicit Mark read.
- User dropdown/reset controls and server-side, one-click import of an unambiguous Enhanced Seerr connection without exposing its key.
- Optional per-proposal administrator approval, off by default; it never replaces viewer consent or shortens delays.
- Bounded off-event-thread activity collection and fail-closed evidence/coverage diagnostics; plugin failures are contained rather than stopping the server.
- Encrypted, authenticated SQLite journal, retained history, tamper/replay detection and no silent state reset.
- Live session/admin authorization, anonymous ordinary-user DTOs and a separate native admin page.
- One-shot stock-Seerr execution with durable claims, uncertainty and a separate FilesDeleted checkpoint based on Seerr's deletion report.
- Read-only detection of direct Seerr deletion, pending-request decline and verified missing records, closing unclaimed work without another destructive request or false queue completion.

## Presentation and scheduled maintenance

### Collections and reversible artwork (preview)

Dashboard settings include an opt-in pending-removal collection (existing or plugin-created) and **Collections & Overlay Studio** with separate portrait/landscape artboards. Select text/rectangle layers to drag, resize, rotate, align, reorder, duplicate or delete them; use snapping, keyboard nudging, undo/redo and zoom. Layer properties edit colors, opacity and text styling. Native server-rendered PNG previews confirm font fitting/output. Up to 12 bounded layers and four text lines per layer are supported; existing single-band settings remain compatible. This covers pending manual and automated requests. Unknown schedule placeholders say **pending**, never an invented date. Native image pointers serve generated artwork to Jellyfin clients, without modifying original files.

Top badges show plugin versions and distinguish loaded/available providers from actual readiness or explicit connection-test results. **Test connection** verifies the saved Seerr key and Jellyfin server association using reads only; save draft changes first. **Copy Jellyfin Enhanced connection** explains unavailable/ambiguous configuration only when clicked. Native task badges link to the real task screens and reflect edited triggers. Diagnostic JSON/journals can be browsed as collapsible blocks or syntax-highlighted code.

Original bytes are separately preserved under plugin-owned storage. Disabling or closing requests restores verified original pointers and removes only plugin-added collection members; collections and pre-existing memberships are retained. Administrator artwork changes are reported as conflicts and are never overwritten. Missing originals can be served from preserved backups. Back up the plugin data directory along with its authenticated state.

Before uninstalling, disable presentation and run collection maintenance plus the artwork restoration task (or Sync / restore now) until owned overlays/memberships are restored. Resolve reported conflicts first. Do not delete the plugin's artwork data while native image pointers still reference it; uninstalling is not an automatic restoration hook.

Preview limits: 1000 artwork receipts, 1000 descendants per series, 20 image changes per interactive sync (up to 1000 in the cancellable daily artwork task), 8 MiB/16 million pixels per image and 512 MiB plugin artwork storage. JPEG/PNG/WebP/GIF/BMP are supported; rotated EXIF images are skipped, and animated inputs produce a static first-frame PNG. Backups/generated files are retained conservatively (no automatic purge yet); reaching capacity stops new artwork generation, not deletion authorization or restoration. The renderer uses Jellyfin's reviewed SkiaSharp runtime; no competing Skia binaries are shipped. Native pointer persistence across arbitrary clients/restarts remains subject to installed acceptance evidence in the root review status.

### Daily maintenance jobs

**Dashboard → Scheduled Tasks → Deletion Queue** exposes separate daily jobs: automatic rules/collection processing (02:00, supplied by Automatic Deletion Requests), verification/due removals/collection maintenance (03:00), and overlay generation/restoration (04:00). Defaults use Jellyfin's local scheduled-task clock; edit triggers or use Run now in the native Dashboard. Coalesced safety rechecks also run between jobs, including read-only external Seerr observation, but never dispatch deletion. Continuous playback capture and immediate safety invalidation remain active. Artwork can lag the live request state until its next job or explicit Sync; the date is always conditional.

Every advancement requires fresh target, native playback/session, permission, current consent, optional administrator approval and policy evidence. Automated requests additionally require their originating rule to remain enabled, in scope and matching current evidence. The execution coordinator obtains fresh evidence again before claiming and immediately before dispatch; rule conditions are also checked at dispatch. A past date never bypasses these checks, disabled deletion opt-in or durable uncertain-claim protections. Startup recovery never resends an attempt.

## Development and releases

From the repository root:

```powershell
npm ci --ignore-scripts --prefix plugins/private-syncplay
pwsh -File scripts/test.ps1 -Plugin deletion-queue
```

Use root packaging and the combined architecture catalogs. **Release Deletion Queue**
accepts `vX.Y.Z`; the shared engine validates immutable packages and current targets
before protected publication. See [release procedures](../../docs/independent-plugin-releases.md).
Do not use the retired standalone four-component tag or channel feeds.

## Use

1. Open **Dashboard → Plugins → Deletion queue**. Save the Seerr URL/key, or select **Use Jellyfin-Enhanced connection**. Only one enabled Enhanced Seerr URL is imported; ambiguous/multiple endpoints require manual configuration. Later Enhanced changes require a fresh import. The configured Jellyfin server ID must match this server.
2. Set global/per-user proposal permission using the user dropdown. **Reset selected user** restores inherited permission and default personal preferences, preserving proposals, consent, history and existing deadline floors. Optionally enable **Require administrator approval for each proposal** and approve/revoke in the backlog. Approvals are bound to the target, activity and policy; the approver's current admin role and visibility are rechecked before dispatch.
3. Use **Request removal** on a movie/whole-series detail page, media card, right-click menu or supported Enhanced request. Review the title/quality, then confirm. Duplicate submissions register consent on the existing active request, retaining its original owner and expiry; repeated current Yes consent is idempotent.
4. Open the **notification bell in Jellyfin's top bar** to review proposals, explicitly allow/keep media, Undo your own unclaimed proposal, mark notices read or change preferences. Mobile swipes and desktop drags support eligible consent requests: a deliberate right gesture submits your live Allow response and dismisses only after success; left reveals Hide without consenting. Current revision/access checks, approvals and delays still apply. Viewing/Help/notification polling never records activity or consent.
5. If desired, explicitly enable **automatic Seerr-managed deletion** and confirm the real-deletion warning. Old preview-only opt-ins cannot activate this transport. A complete bounded native Jellyfin last-played scan (including unfinished playback and related series items across all accounts) can verify the activity window immediately after collector startup; a new multi-day observation wait is not required when that scan succeeds. Current consent and the minimum two-day eligible delay still apply. Missing/incomplete inventory, an unhealthy collector or clock, and active playback fail closed. Fresh verification runs in the daily maintenance task, on explicit status inspection, and again before execution. Failed checks remain blocked until another successful verification; use Scheduled Tasks → Run now to retry earlier.

An ASP.NET startup filter injects same-origin resources into Web without modifying Web files. File Transformation is not required. The full queue is embedded in Enhanced's Requests page, not a second standalone application. The popout's **Open full queue** action appears only when the enabled Enhanced plugin reports its Requests page enabled; users are not shown missing-dependency instructions. Administrator settings explain this integration. Native TV/mobile clients that do not load Jellyfin Web are not modified. Unsupported Web/Enhanced markup may omit controls rather than replacing native commands.

### Removal lifecycle and playback

Administrator settings share the request graph: Request → (all users inactive **OR** every relevant active viewer consents) → verify safeguards → optional administrator review → safety delay → verify/remove → Completed. Administrator denial/timeout takes a separate branch directly to Cancelled. The live unsaved preview updates the lookback, delay and approval decision limit (default 30 days); it distinguishes an activity lookback from an additional wait. Saving and destructive opt-in remain explicit. Personal longer windows/delays and existing deadline floors are never shortened by lowering a setting.

Native history verification reads only Jellyfin's latest-played value for each relevant account/item; it does not enumerate historical playback events. Values older than the saved lookback are discarded during the scan. All relevant accounts and aliases still require inspection so a recent viewer cannot be skipped. A policy revision change during the scan invalidates the evidence and requires a fresh check.

Library thumbnails are not overlaid with removal buttons. Right-click an item (or open its native More menu) for a separate **Deletion Queue** section with native menu icons. Unqueued items offer **Request removal** without a timeline. Active requests show their shared checkpoint timeline, a color-coded **Removal · [stage]** action, **Allow removal**, **Keep item (reject removal)** and owner-only **Undo removal request** when permitted. Detail-page actions use the same stage icon/color and open the existing request rather than creating a duplicate. Keep withholds your consent; it is not an administrator denial. Execution-stage requests do not offer consent changes. Menu commands use the displayed request revision and current access checks; unavailable status fails closed.

Notification routing is fixed to Auto; there is no mode selector. Legacy saved modes are normalized once without clearing stored queue data. **Reset all user permissions to global** clears every per-user removal-permission override using the saved global policy and requires confirmation. It preserves personal preferences/subscriptions, consent, history and existing delay floors; unsaved policy edits are not applied.

Queue rows include a **?** status explanation (hover, focus or tap) and distinguish pending checks/consent, conditional scheduled dates, ready state and closed outcomes. A date is shown as **Scheduled no earlier than** only when automatic removal is enabled; ready requests show **Eligible since**. Safety checks still apply, and no exact execution time is promised. Stale deadline floors on blocked requests are not advertised as a schedule.

Request views show connected forked paths, shared with settings and the administrator backlog. All users inactive **OR** consent from every relevant recent viewer can cross eligibility; neither path bypasses history verification, active-playback blocking or other safety gates. Uninvolved users do not vote. Administrator approval is another fork: approved requests continue to the delay; denial or expiry connects directly to the separate Cancelled endpoint, not through removal. The default decision limit is 30 days, with the actual configured/personal extension and recorded deadline displayed. The approval clock starts on entering review, never on opening a timeline. Terminal backend statuses (including Expired/Denied) remain unchanged; Cancelled is the graph's non-removal endpoint.

The graph keeps Completed and Cancelled separate, highlights travelled branches and places its dot at the current/terminal node. Lookback, optional waiting limit, approval limit and delay show day/hour values **on the connecting lines**; exact recorded dates appear on hover/focus. Missing dates are not invented. Native `paperList` surfaces, inherited text and theme/semantic variables support skins; narrow layouts scroll within the graph. Older projections without branch metadata retain their linear fallback. History preserves known stopping stages and public closure explanations without disclosing other users' identities, individual votes/activity timestamps, paths or upstream evidence.

The queue is a right-aligned header popout on desktop (half-height maximum), and a centered modal on mobile. When Enhanced Requests is enabled, its small top-right popout icon closes it and opens the full Requests deletion view, retaining the selected request; otherwise a small close icon is used. Both notification and deletion popouts have thin, theme-aware scrollbars.

Review links open the queue without changing the current Web page. The relevant request is highlighted, focused and scrolled into view; older requests outside the first page are pinned without duplication, and closed requests open History. Existing bookmarked fragment links remain supported. Queue and request-preview modals dismiss on a genuine outside click or native Escape; dragging from inside to outside does not submit or accidentally dismiss an action. Explicit removal confirmation is still required.

An optional administrator-configured activity/consent waiting limit replaces the previous hardcoded 30-day intake cutoff. It is off by default; enabling it uses 45 days unless changed (1–365). This covers the whole eligibility stage, not only live playback, and is measured from request creation without renewal by playback or repeated submissions. Disabling it allows this stage to wait without expiry. Administrator review has its separate decision clock (30 days by default), retained through transient verification failures once review begins. Accepted requests waiting through their safety delay and claimed/uncertain execution are not expired by the waiting limit. A distinct administrator **Deny request** action still closes immediately; revoking approval remains a separate non-terminal operation. Expired requests remain in History; existing terminal records are not reopened.

An administrator requesting removal also records administrator approval when that policy is enabled, without bypassing other viewers, current activity checks or the delay. Fresh activity/policy changes invalidate that approval as before.

When the owner starts playback, the earliest removal time is pushed back by at least the effective delay floor, fresh consent is required, and the intake clock restarts. A dedicated owner-only notification is emitted once per playback session, not on each progress event. It offers **View request** and owner-only **Cancel request**. Administrators can change its plain-text message (`{media}`, `{days}`) and toast duration (0–10 seconds) under **Notifications → Registered notification types → Playback resets removal**. The default toast lasts six seconds; the notice remains in the inbox.

### Changes made directly in Seerr

If someone deletes the selected media edition or declines an unmanaged pending
request directly in Seerr, maintenance closes unclaimed queue work with **Removed
outside the queue** or **Closed in Seerr**. A verified missing record instead says
**Upstream record missing**; it does not establish that physical files were deleted.
Current connection/server association and exact media/quality identity must match.
Network/authentication failures remain blocked verification, not successful removal.
Closed requests lose their live actions; old Allow/Keep/Undo controls cannot change
them. Automatic rules do not silently repropose cancelled work.

Claimed/uncertain attempts retain their durable claim and are never automatically
cancelled, reopened or resent. Administrators must use the separate read-only
outcome reconciliation. Native library visibility still follows Jellyfin's scan
and current item access, not an independent filesystem check.

### Unfulfilled requests and incomplete downloads

Request IDs/media identities come from supported Enhanced card metadata and Seerr HTTP reads, never titles or usernames. Seerr's native Jellyfin user mapping and server association must verify ownership. When no native media item exists, visibility is limited to the verified request owner. Unmanaged pending requests use Seerr's **decline** endpoint after normal queue safeguards, preserving request history; the plugin never deletes `/request/{id}`. Managed incomplete media uses Seerr's existing media-file removal endpoint only after verifying the managed edition and ownership of every live request in its scope. Unverified ownership, a changed target or unsupported state fails closed.

Enhanced 12.13 Calendar integration adds dated removal events to its own renderer and a **Deletion Date** filter, preserving native OR/AND/NOT filtering and month/week/day/agenda and list/card/backdrop modes. There is no separate queue/sidebar list. Unscheduled requests have no calendar date; dated removals are explicitly earliest possible times. Click an event to focus its request in Enhanced Requests, or open the queue when Requests is unavailable. The adapter checks the reviewed Enhanced calendar-state/rendering contract and does not guess dates from DOM day numbers. Browser acceptance additionally exercises immutable upstream Enhanced 12.13 rendering/filter modules; the owner's complete custom-skin/navigation installation and real upstream download/removal behavior still require acceptance.

Latest-stable Server/Web compatibility, hostile-input and browser/fault-isolation checks are release gates in the local workflow configuration. An in-process plugin cannot promise immunity from all host crashes; see [safety and operational limits](../../docs/safety-and-permissions.md).

## Disposable installed acceptance

Never mount real media or use production credentials. Create fresh packages with
the root wrappers, then from the repository root run:

```powershell
node scripts/integration.mjs --packages=artifacts/current-candidate --plugins=deletion,notifications --security=latest --permissions-check
```

The runner owns its fixture and cleanup. Component smoke/proxy/fault tools remain
controlled developer fixtures; Compose requires an explicit current-run image, not
an old default. See [testing](../../docs/testing.md) and [browser/fault checks](../../docs/theme-acceptance.md).

## Design and operations

- [Safety and permission boundaries](../../docs/safety-and-permissions.md)
- [Optional Account Hub v1 integration, migration and handoff](docs/account-hub-v1.md)
- [Current behavior and remaining gates](../../docs/review-status.md)
- [Native-page verification](../../docs/theme-acceptance.md)
- [Recovery and stopped backups](../../docs/plugin-storage-recovery.md)

## References and credits

- [Current installation and dependencies](../../docs/dependencies.md)
- [Consent gestures and notification controls](../../docs/notification-channels.md)
- [Automation rules](../automatic-deletion-requests/README.md)
- [Current testing](../../docs/testing.md) · [Release policy](../../docs/independent-plugin-releases.md)
- [Current evidence and blockers](../../docs/review-status.md)

Collection and overlay workflows were inspired by [Maintainerr](https://github.com/Maintainerr/Maintainerr) (MIT). This is an independent implementation; no upstream source or artwork is bundled.
