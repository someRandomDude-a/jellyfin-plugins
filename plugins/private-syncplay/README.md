# Private SyncPlay

## Current dependencies and installation

Install Private SyncPlay from the [combined platform catalog](../../README.md#installation-and-upgrades),
restart Jellyfin and configure **Dashboard → Plugins → Private SyncPlay**. Core
parties require no other suite plugin. Optional notices use Notifications; Hub
supplies identity/audit and Bridge supplies Discord commands/delivery. Guest
viewing requires HTTPS, Jellyfin's FFmpeg and current owner media/conversion
permissions. Guest sharing is off by default. Do not combine with another plugin
replacing `ISyncPlayManager`.

Current monorepo commands, run from the repository root:

```powershell
npm ci --ignore-scripts --prefix plugins/private-syncplay
pwsh -File scripts/test-plugin.ps1 -Plugin private-syncplay
```

Use the SDK in `global.json`, current stable Node.js and PowerShell 7+. Test/package
wrappers discover current stable Jellyfin automatically; `Jellyfin.Target.props`
is a historical IDE baseline, not the CI target. Use the root **Release Private
SyncPlay** action and [current release policy](../../docs/independent-plugin-releases.md).
Local builds do not publish catalogs. Check [current evidence](../../docs/review-status.md)
before deployment; hardware, proxy, skin and human playback acceptance remain separate.

## Overview

Native .NET 10 plugin with private SyncPlay groups, limited invite links, administrator-controlled guest viewing, and bundled Jellyfin Web controls.

## Features

- Public groups remain browsable through `/SyncPlay/List`. Private groups never appear there, even for their members.
- Private groups have a cryptographically random, case-insensitive eight-character join code. A known group ID alone does not authorize a private join or lookup.
- Server-wide and per-user defaults and creation permissions. Existing clients that omit a mode receive their effective default.
- Hosts create revocable invite links with guest permission, redemption count, and optional expiration. Links carry independent 256-bit secrets, not short join codes.
- Party-wide concurrent viewer limits count all signed-in sessions (including the host) and guest sessions, regardless of join method. Invite-use limits count successful new admissions; refreshing an existing lease does not consume another use.
- Administrator master switch and per-user Inherit/Allow/Deny for guest invitations. Server disable always wins. Revocation affects active guest leases as well as new admissions.
- **Guests never keep a party alive.** The last signed-in participant leaving or disconnecting destroys all party invitations and guest leases. Another signed-in member may keep the party alive after its creator leaves.
- New guests are labelled **Guest 1**, **Guest 2**, and so on within their party. Labels stay stable for each lease and are not reused after departure. Signed-in party members receive Jellyfin's native SyncPlay user-joined notification; reconnecting an existing guest does not announce another join. Guests remain outside core SyncPlay membership/waiting.
- State is in memory: restarting Jellyfin expires groups, links and guests. Disabling private creation does not publish existing private groups.

## Installation and updates

Use the root architecture-specific combined catalog and current setup above.
Published compatible releases retain numeric version history and immutable ZIP
checksums. A missing catalog/version is not yet published. Never install the SDK
archive as a plugin or mix dependency DLLs from different package sets.

### Version history and updates

- **Dashboard → Plugins → Catalog → Private SyncPlay** uses Jellyfin's normal **version selector**. It lists published compatible versions, newest first, with their changelogs/timestamps. An ABI newer than your server is filtered out. Before the first publication there are no installable versions; after the first publication there is only one until another is released. Test fixture versions are never published.
- The installed version appears in Jellyfin's **Plugins / My Plugins** list. The plugin settings page also displays the **running plugin version and server version**, so a downloaded update cannot be mistaken for code already running.
- With the repository enabled and Jellyfin's plugin automatic updates allowed, its **Update Plugins** scheduled task downloads newer compatible versions. You can also select a specific version and install it from the catalog. **Restart Jellyfin** to activate the new DLLs, then refresh Jellyfin Web. Updates are not hot-loaded into an existing browser page.
- Server/per-user plugin configuration persists across upgrades. Parties, invitations and guest cookies are deliberately invalidated on restart; hosts must create new parties/links. No temporary guest is turned into an account during an upgrade.
- Back up Jellyfin's data/configuration before upgrading. For manual updates, stop Jellyfin and replace the DLLs together in the existing manual-install directory; do not mix plugin and API DLLs from different packages or create duplicate unversioned copies. Prefer catalog installation for native version selection and update tracking.

## Development and configuration

Use the root selected-plugin test/package wrappers described above and the
[testing guide](../../docs/testing.md). The package embeds its Web scripts and
hls.js with its license; do not bundle Jellyfin server assemblies.

Configure **Dashboard → Plugins → Private SyncPlay**. Use the **Watch parties** toolbar button in Jellyfin Web to create a party, select its mode and concurrent-viewer cap, join by code, and manage invitations. The usual SyncPlay controls continue to handle signed-in playback.

New invitation links are copied automatically when clipboard permission is available; otherwise the link is selected for manual copying. Creating another link replaces the displayed secret without revoking the previous invitation. Revocation clears the displayed link and disables copying. Group creation and joining wait for this device's native realtime connection and close plugin dialogs before sending the action, leaving native player navigation unobstructed. SyncPlay synchronizes playback, not members' browsing pages. Uncertain join responses are not automatically replayed.

While a party/invitation dialog is open, typing and scrolling stay in that dialog instead of triggering player shortcuts or volume commands. An early input guard loads before Jellyfin's scripts, intercepting window capture handlers rather than relying on dialog bubbling. Normal text entry, tab navigation, Escape and scrolling are preserved; player shortcuts resume after all party dialogs close. After updating the plugin, restart Jellyfin and reload existing web tabs to load this early guard.

Guest links must be created and opened over **HTTPS**. The web controls disable guest-link creation on an HTTP connection; account-only invitations remain available.

### Optional Account Hub integration

The [Account Hub adapter/outbox documentation](docs/account-hub-integration.md) covers the shared v1 administrator API, revisions, replay-safe commands, administrator exclusions, event redaction, native fallback and rollback. Account Hub remains optional; it never authorizes guest playback or owns party credentials. Installed Hub/Security acceptance applies only to its recorded package set.

### Guest player and account invitations

The isolated guest player uses Jellyfin Web's player skin hooks (`videoOsdBottom`, `osdControls`, `volumeButtons`, `btnSubtitles`, `btnFullscreen`, and native icon buttons/sliders). Volume/mute, captions and fullscreen are local controls; leaving is the player header's back action. The position display is read-only: no pause, seek, skip, speed or queue buttons are exposed. Local keyboard/media-element changes are corrected to the party position, but—as with any delivered video—an external tool can record or pause already-delivered bytes without controlling the party.

After admission, the guest player fills the **entire browser viewport** with no surrounding page margins or panels; the video's aspect ratio is preserved. **Fullscreen** uses the browser's actual Fullscreen API, covering tabs/taskbar where the browser/OS permits, and exits normally with Escape or the button. It requires a user gesture and is not automatically forced when opening an invitation.

Embedded and adjacent local text subtitles are converted through Jellyfin's subtitle encoder to bounded, party/generation-scoped WebVTT. Guests can choose a text track locally or follow the party; movie timestamps are adjusted to the HLS seek offset. Selected ASS/SSA and bitmap subtitles follow the signed-in player's selection and are rendered into the **shared** video using Jellyfin's native filters, preserving styling/positioning where supported by its encoder. They require the owner's video-transcoding permission; guests cannot launch separate caption encoders or change the party's subtitle selection. Remote subtitle URLs are not fetched, and captions are reauthorized after conversion and on every delivery.

**Join with Jellyfin account** uses `/PrivateSyncPlay/Account#<secret>` as a short handoff, not an unknown Jellyfin route hash. It stores the invitation only in server-scoped tab/session storage for up to 30 minutes, then opens the normal Jellyfin entry page without a secret in its URL. After login and websocket readiness it joins once through the authorized invite API. Jellyfin's ordinary `GroupJoined`/queue messages then open the group's current playback using its normal player behavior; an empty group's normal idle behavior is retained. The plugin does not reopen its management dialog or reload the socket after joining. A client connected to another server is never sent the invitation secret.

Component SDK/catalog builders remain developer fixtures; current publication uses
the root release engine and combined platform feeds. Packages retain their stable
GUID/API identity, numeric version/ABI, direct immutable ZIP URL, root DLL layout
and exact MD5 required by Jellyfin. MD5 is not a signature; trust only approved
repositories. The SDK keeps its API version independently of plugin releases.

### Releases

Use **Release Private SyncPlay** and the [current release guide](../../docs/independent-plugin-releases.md).
Tests, packages and publication share current-run targets; existing versions/assets
are never overwritten. Catalog-only repair uses the existing published bytes, not
a rebuild. Local builds do not publish or update installed servers.

### Native UI and skin compatibility

Controls use Jellyfin 12's native DOM/CSS contracts: `emby-button`, `raised`, `button-submit`, `button-delete`, `inputContainer`/`emby-input`/`inputLabel`, `checkboxContainer`/`emby-checkbox`/`checkboxOutline`, `selectContainer`/`emby-select`, `dialog`/`formDialog`, `paperList`/`listItem`, and `mdl-slider`. Labels are associated with inputs, focus states follow native classes, and errors use themed in-page feedback instead of browser alerts.

The launcher is placed in Jellyfin's toolbar and reuses its actual button classes, including the Modern layout's MUI skin hooks. There is no fixed-position floating button. Plugin CSS provides responsive layout and inherited defaults only, not a custom palette, font or button design. Existing themes, CSS variables and skin overrides own the appearance.

The isolated guest player loads **stylesheets only** from the installed Jellyfin Web build, its configured default theme and public server branding CSS. It never loads the normal web application or its authentication code. Guests cannot inherit another user's private client preferences; server-wide skins apply through **Dashboard → General → Custom CSS**. HTTPS stylesheet/font/image imports are permitted for administrator-installed skins; scripts, media access and API requests remain restricted. Only install trusted CSS, and never put account tokens or private media URLs in public branding CSS.

Native skin selectors are supported, but arbitrary CSS that hides controls or completely replaces Jellyfin's DOM can still break them. Skins targeting only unrelated MUI components may need the same legacy control selectors used by ordinary Jellyfin plugin configuration pages. Test your chosen skin in Modern/Legacy layouts and at mobile sizes.

### Live guest player designer

Open **Dashboard → Plugins → Private SyncPlay → Guest player designer**. Its canvas runs the **same guest HTML, renderer, and local controls** as the real guest page, with an animated video generated locally—not a screenshot, signed-in player, or private-library preview. Installed native styles and server branding CSS are used in both places. The preview has an opaque sandbox origin and cannot call guest/account APIs or read the administrator's storage. Caption selection, mute/volume, and fullscreen can be explored directly.

- Check the explicitly allowed features; unchecking a feature removes its controls. Unknown or future features are not automatically granted.
- Click a canvas control or list entry to select/remove it. Drag toolbar controls on the canvas/list to reorder, or use **Move up/down** for keyboard editing. Title and read-only position retain their normal native player slots rather than arbitrary pixel positioning that would break responsive themes.
- **Fit canvas** uses the available settings-page width automatically, without Jellyfin's normal narrow form limit. The feature sidebar moves below the canvas until the page has enough room for both. Choose **Wide player** or **Narrow player** for fixed responsive test viewports; they scroll inside the canvas without widening the page. Jellyfin's native mobile CSS may hide controls such as volume at narrow widths.
- Add approved native or contributed plugin controls using the chooser. Removing the last control for a feature disables that feature. Leaving, playback admission, and the browser's autoplay-unlock action cannot be removed.
- **Undo/Redo**, **Reset default layout**, and **Discard unsaved layout** affect only the draft. **Save guest player** merges this page's settings with the latest server configuration without overwriting user permissions. Active players receive the new policy on their next state poll.

Guest routes use a closed server-side operation allowlist. In particular, disabling text subtitle selection blocks the VTT endpoint even when called directly and rechecks permission after conversion; it is not merely a hidden button. Local volume, mute and fullscreen are browser presentation options: no server can prevent a viewer from changing their device volume, recording or pausing already-delivered bytes. Synchronization timestamps still have to reach the client when their display is hidden. Host-selected subtitles burned into the shared video remain part of that video.

This is an isolated **guest adapter**, not the authenticated Jellyfin Web app with buttons hidden. Arbitrary scripts that patch the authenticated playback manager do not automatically run for guests. Other plugins can contribute opt-in, declarative native controls through the [guest UI SDK](docs/plugin-api.md#guest-safe-player-controls); controls bind only to existing local guest actions, never arbitrary JavaScript, HTML, media URLs, or server commands. Their actual themed controls render in the live canvas. Universal compatibility with arbitrary DOM-replacing themes or authenticated player plugins is not promised.

## Administrator settings

| Setting | Default | Behavior |
| --- | --- | --- |
| Enable private groups | On | Permits creation; existing private groups remain protected if disabled |
| Allow public groups | On | Permits public creation |
| Default creation mode | Public | Per-user override; must select an allowed mode |
| Enable guest invites | **Off** | Master guest-sharing permission |
| Default guest permission | **Off** | Inherited permission when master switch is enabled |
| Maximum simultaneous viewers | 10 | Hard upper bound on each party's chosen cap |
| Maximum invite uses | 10 | Hard upper bound on each link's chosen redemption count |
| Guest reconnect grace | 30 seconds | Seats expire without a state heartbeat |
| Guest video bitrate | 2000 kbps | Shared browser-stream video bitrate cap; audio target up to 128 kbps |
| Maximum concurrent guest streams | 2 | Server-wide encoder/remux process limit (1–32); additional parties receive 503, not another encoder |

Each user can inherit or restrict public/private creation, override the default creation mode, and inherit/allow/deny guest invitations. A per-user Allow cannot bypass a server master restriction. Hosts cannot edit these policies.

The **User overrides** table supports name search, per-row editing, and checkbox selection (the header checkbox selects all currently visible users). Open **Bulk edit selected** and check only the fields you want to change; unchecked fields stay untouched. Selected users hidden by search are still included, with their count shown above the table. **Reset** on a row, **Reset selected to defaults**, and **Reset all user overrides** remove explicit overrides so users inherit the current server defaults; they do not reset server settings. Reset-all requires confirmation and also clears saved entries for users no longer listed. Permission actions support one-step undo, and **Discard unsaved changes** reloads the saved configuration. All changes and resets remain drafts until **Save**.

Only the party owner can issue or revoke links. Revoking a link disconnects guests admitted by that link. Existing signed-in participants keep their normal membership. An admin can end a party through the API. Limits describe **sessions**, not verified unique humans; anonymous identity and physical viewers cannot be proven.

### Owner moderation

The owner's **Watch parties** dialog includes a native-styled participant table and party-only controls:

- Lock/unlock new viewers, disable guest access, adjust the viewer cap within the administrator's limit, and end the party for everyone.
- Restrict signed-in participants' playback and queue permissions, with both party-wide ceilings and per-account switches. Queue editing requires playback permission to prevent queue changes becoming an indirect seek/skip bypass. Account permissions apply to every device of that account in this party and survive leaving/rejoining it.
- Guest player options are **administrator-only** and apply equally to every guest. Owners cannot override them per party or per guest. Guests never gain pause/seek/queue/account privileges. Optional subtitle access is checked server-side before and after conversion; local browser presentation options remain local.
- **Kick and block** removes all of a signed-in account's sessions and blocks rejoining this party by public list, code, or invitation, including new devices. **Allow rejoining** removes the block but does not automatically rejoin the account or reset its permissions. Other parties and Jellyfin account permissions are unaffected.
- **Kick and revoke link** removes a guest by revoking its admission invitation. The confirmation shows how many guest sessions share that link: **all of those sessions** are disconnected and the old link can no longer admit anyone. Anonymous people cannot be reliably identified across cleared cookies, new browsers, or newly issued invitations; this is not an identity-based permanent ban. Use separate links when individual guest removal is important.

Locking joins preserves existing viewers. Disabling guest access permanently revokes current guest-enabled links and their leases; turning it back on does not resurrect them. The owner retains control and cannot kick/demote themselves; they may leave or end the party. Destructive actions require UI confirmation. Permissions and participant targets are revalidated on every server request; non-owners cannot fetch the management roster or call moderation operations. Restricted signed-in viewers are excluded from group buffering waits, and their buffering/readiness/ignore-wait reports cannot pause or reposition the group. Their ordinary session-progress reports cannot become an alternate authority for guest media either: if no playback-authorized signed-in member is playing, guests wait instead of following a read-only viewer. A native removal/wait-policy cleanup failure ends the affected party rather than leaving a partially enforced group. These are party controls, not restrictions on a signed-in account's pre-existing access to Jellyfin outside the party.

## Guest security model

An invite opens an invitation page at `/PrivateSyncPlay/Watch#<secret>`. A saved native Jellyfin account is checked against this server's ID/address and authenticated through `Users/Me`; a valid account automatically continues into native signed-in joining. Otherwise, sign-in is offered and **Watch as guest** appears only after a same-origin, non-consuming server check confirms guest admission is available. Guest redemption still requires an explicit click, HTTPS, a same-origin JSON POST, and a valid guest-enabled invitation. The admitted guest moves to the separate `/PrivateSyncPlay/Player` runtime, which never reads account tokens or loads account authentication code. The fragment is immediately removed from the address bar. It is never sent as a request URL or Referer. Put the origin behind a correctly configured HTTPS reverse proxy if necessary; do not weaken the HTTPS checks.

Guests receive an opaque HttpOnly, Secure, SameSite=Strict, path-scoped session cookie, **not a Jellyfin user, account token or API key**. Secrets are stored as hashes server-side. These credentials are not registered with Jellyfin's normal authentication system. They cannot authorize ordinary Jellyfin APIs.

Guest endpoints expose only current playback state and a generated, sliding-window HLS stream. They never accept a source URL, library item ID, media path, or FFmpeg options from the guest. Every manifest and segment request checks the party, guest lease, owner sharing permission, current item, and owner library access. The player exposes local volume but no party controls; guests cannot send buffering/pause/seek/queue commands to SyncPlay.

Guest playback prefers a compatible **existing native HLS stream from a signed-in party member**, read-only through scoped guest delivery. Otherwise it uses **one shared guest HLS process per party**, through Jellyfin's configured FFmpeg executable. It follows an authenticated participant's reported playback position; it does not add guest sessions to Jellyfin's core SyncPlay engine or pause members while guests buffer. Expect browser/network synchronization tolerance, not frame-accurate playback. Administrator guest permission is an explicit delegation to stream the party's current video to unauthenticated people. The owner must retain media playback and SyncPlay permissions, plus remuxing/audio/video conversion permissions **only for the operations actually required**.

### Failure containment

- Unsupported/conflicting SyncPlay registrations **disable this plugin**, log an administrator-facing error, and leave the existing server engine untouched. Plugin endpoints return **503** rather than creating unprotected parties. A later safety-check or maintenance failure revokes all plugin invitations/guest leases and latches the plugin unavailable until restart; it does not deliberately stop Jellyfin's host.
- Revocation happens **before** native group/encoder cleanup. Failed cleanup cannot preserve a usable guest credential. If encoder termination cannot be confirmed, further plugin access is disabled rather than repeatedly spawning replacement processes. An administrator may need to stop an orphaned encoder at the OS/container level.
- Encoder launch failures return generic errors without server paths and have a retry backoff. No failure switches guests to normal Jellyfin media URLs, issues account tokens, removes permission checks, or starts a hidden software fallback.
- Guest polling/media requests are rate-limited; request bodies and media allocations are bounded. Active guest-party streams have a separate server-wide cap (default **2**), including borrowed native streams and remux jobs; reuse does not bypass this administrator limit. This also bounds plugin-owned encoder jobs, but does not replace OS resource limits or Jellyfin's signed-in transcoding controls.
- Every guest delivery rechecks an eligible, connected signed-in member and the owner's permission. Cross-origin requests, including sibling-subdomain requests carrying a SameSite cookie, are rejected when browser origin/fetch metadata is present. Non-browser callers must still present the unguessable guest credential.

Party/guest state is in memory: there are no party tables, durable party resurrection, or Jellyfin account/library/user-policy writes. Optional Account Hub event summaries use a **separate plugin-owned SQLite outbox**, never Jellyfin's database. Session admission is bound to both session and user identity, including after account switches. Disabling guest permission permanently revokes existing guest links; restoring it requires new links. Disposable integration tests also check SQLite integrity/foreign keys after a clean shutdown.

The plugin-owned guest encoder stops when the final guest lease is removed, including leave, revocation, expiry and party teardown; signed-in viewers alone do not keep it running. A borrowed member stream is only detached: the plugin never kills, pings to prolong, or deletes the signed-in viewer's native stream/cache. Other guests/parties and normal Jellyfin transcodes are unaffected.

See [security review and regression coverage](docs/security-review.md). These safeguards are not a guarantee against all crashes or corruption: plugins and FFmpeg run with the server's privileges. Keep backups and Jellyfin/FFmpeg patched, use trusted plugins and media, run Jellyfin as an unprivileged user/container with read-only media and appropriate CPU/memory limits, and verify proxy behavior before Internet exposure. Jellyfin's existing public login, branding and server-info endpoints remain public even without this plugin.

### Encoding policy: follow Jellyfin

The implementation follows Jellyfin's [transcoding guidance](https://jellyfin.org/docs/general/post-install/transcoding/) and [hardware acceleration configuration](https://jellyfin.org/docs/general/post-install/transcoding/hardware-acceleration/):

- Compatible H.264/AAC media within the bitrate cap is **stream-copied/remuxed**; neither video nor audio is re-encoded. The guest transport intentionally remains gated HLS instead of exposing the original file.
- Prefer reusing a compatible member-owned MPEG-TS HLS job for the same authorized item/audio selection. Native session/user/device/job identity and media permissions are rechecked on delivery. Only validated local segments are exposed through a rebuilt guest manifest, never native URLs, tokens or paths. Ambiguous offsets, fMP4/encrypted playlists, burned subtitles, unknown/high-bit-depth/HDR source profiles, or output above the budget fall back to the isolated guest stream. A member's direct-play file is not itself a reusable HLS stream; it can still be remuxed without video re-encoding.
- If only the audio is incompatible, copy the video and convert only the audio (Direct Stream).
- If video conversion is required by codec, bit depth, SDR browser profile or bitrate, use Jellyfin's native `EncodingHelper` and current `EncodingOptions`. These select the configured HWA backend (QSV, NVENC, VA-API, AMF, VideoToolbox, RKMPP), decoder/device arguments, filter chain, tone-mapping options, encoder preset/quality, audio downmix and thread count. The plugin does not maintain its own GPU preference or hard-coded GPU arguments.
- Software video encoding is used only when conversion is necessary and Jellyfin selects it—for example, HWA is disabled, hardware encoding is unchecked, or the configured encoder is unavailable. A failed hardware process does **not** trigger a hidden plugin-side CPU retry.
- Encoding setting changes replace the shared guest stream on the next state update. The former fixed 1280px scaler and two-thread/software-encoder override have been removed; sizing follows Jellyfin's bitrate-driven rules.

#### Shared adaptive quality

The administrator's **Guest video bitrate cap** is a maximum video-encoding budget, not a forced fixed quality. Jellyfin chooses resolution from source resolution, frame rate, codecs and the selected bitrate, as described in its [bitrate/resolution documentation](https://jellyfin.org/docs/general/post-install/transcoding/#bitrate-and-resolution); the plugin does not set an arbitrary resolution ladder. Audio and mux overhead consume additional bandwidth, and encoder/VBR bursts are not a strict wire-rate limit.

Guest HLS fragment transfer measurements and buffer/stall observations accompany ordinary state polling (no new playback-control endpoint). One budget is chosen for the **slowest fresh guest measurement**, with 30% throughput headroom and an audio/mux allowance. Reductions are quantized and rate-limited; recovery requires at least a minute of healthy fresh measurements from **every** active guest. Old generations, expired/disconnected guests, autoplay/startup stalls and invalid/non-finite measurements do not drive new encoders. Low-end video budget is 256 kbps; connections below that may still struggle. HLS.js provides throughput measurements; native-HLS browsers without visible fragment statistics can report stalls, but missing measurements are not treated as proof that it is safe to raise quality.

Feedback is untrusted, lease-scoped and bounded: it cannot change media, seek, pause, raise the administrator cap, or start one encoder per guest. A deliberately dishonest admitted guest can lower the shared quality within these bounds; anonymous throughput cannot be independently certified. Owners can remove its admission link, but cannot grant different player features to individual guests. Quality changes may briefly reconnect guest HLS; native member streams are never reconfigured to accommodate guests.

The shared guest profile remains conservative H.264 8-bit SDR plus AAC stereo, rather than negotiating different codecs per guest. Supported source streams are copied when compatible with that profile. Configure hardware acceleration under Jellyfin's **Dashboard → Playback → Transcoding**, including device access for Docker, as explained in the linked documentation.

Ending a party or revoking access prevents further delivery and stops unused encoders. Bytes already delivered or buffered cannot be recalled or made unrecordable. Guests necessarily know the public streaming address, and Jellyfin's existing public login/server-info endpoints remain public; the plugin grants no additional access to them. It is not a network firewall.

### Initial limitations

- Server-hosted Jellyfin Web only. Native clients remain subject to privacy/limits but do not gain private-code or guest controls.
- Guest playback supports **local video files only**, using the video's default track and the authoritative player's selected audio track. No Live TV, remote/STRM sources or disc images. Text subtitles use native WebVTT conversion; selected styled/bitmap subtitles require native burn-in and are party-wide rather than independently selected per guest.
- Requires Jellyfin's FFmpeg build with the selected encoders, HLS and `readrate_initial_burst`. Remuxing has low overhead; required guest conversion is additional load, separate from signed-in transcoding.
- Hardware encoder selection is covered by tests using Jellyfin's actual helpers and mocked hardware capabilities. Actual GPU execution and HDR tone mapping must still be verified on the administrator's hardware; Docker integration tests exercise real stream-copy and required software conversion on a CPU-only server.
- Do not combine with plugins replacing `ISyncPlayManager`. Registration/startup checks disable this plugin rather than silently downgrade privacy or intentionally abort server startup.
- One guest lease per browser cookie jar; reopening it resumes the same session during the grace period. Losing the cookie requires a new redemption.
- Signed-in media restrictions remain enforced by Jellyfin. Enabling guest invitations is a deliberate admin decision to allow unauthenticated viewing of owner-authorized content.

## API

### Internal API for other Jellyfin plugins

Reference the matching current SDK **1.1.0** and register `services.AddPrivateSyncPlayClient()` inside your `IPluginServiceRegistrator`. Constructor-inject the single `IWatchPartyService` into a C# service, event handler or automation workflow for parties, invitations, joins, owner rosters, permissions, kick/block/unblock and teardown. No HTTP or JavaScript is required, and no legacy interfaces or older-provider fallback paths are retained. All operations enforce user/session checks, native SyncPlay permissions, administrator ceilings, capacity limits and lifetime rules. Guest UI controls have a separate opt-in declarative registration API.

See **[plugin API documentation and complete examples](docs/plugin-api.md)**, including typed method signatures, XML/IntelliSense documentation, optional dependency handling and Jellyfin's isolated assembly load contexts. [`examples/WatchPartyConsumer`](examples/WatchPartyConsumer) is a separately compiled example plugin used in Docker integration tests; it is not shipped in the production plugin ZIP.

### HTTP API

Authenticated routes retain Jellyfin's SyncPlay authorization policies and resolve the caller's existing device session:

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/PrivateSyncPlay/Me` | Effective permissions and own current party; code only for its owner |
| POST | `/PrivateSyncPlay/Groups` | `{ Name, Mode: 0(public)/1(private), MaxViewers }` |
| POST | `/PrivateSyncPlay/JoinCode` | `{ Code }` |
| POST | `/PrivateSyncPlay/JoinInvite` | `{ Token }`; still requires the user's own media access |
| POST | `/PrivateSyncPlay/Invites` | `{ AllowGuests, MaxUses, ExpiresAt? }`; owner only |
| DELETE | `/PrivateSyncPlay/Invites/{id}` | Owner revocation |
| DELETE | `/PrivateSyncPlay/Groups/{id}` | Admin-only party termination |

Anonymous guest routes are deliberately separate: `POST Guest/Redeem`, `GET Guest/State`, `GET Guest/media/{generation}/{name}`, `GET Guest/media/{generation}/subtitles/{index}.vtt`, and `POST Guest/Leave`, under `/PrivateSyncPlay/`. Caption indices refer only to the authorized currently playing video, never an arbitrary library item or file. There is no guest playback-control endpoint. Reverse proxies must not cache these responses or log request bodies/cookies.

## Integration checklist before deployment

To run the automated disposable-server test yourself, with Docker running:

```powershell
# From this component directory, after root latest-target packaging:
node scripts/integration.mjs
```

The installed CI gate also builds `examples/WatchPartyConsumer`
and exercises `node scripts/integration.mjs --with-consumer --install-from-catalog`.
This starts a temporary nginx repository on a dedicated Docker network, installs
through Jellyfin's `Repositories`/`Packages` APIs, verifies the DLLs and generated
`meta.json` in its versioned plugin folder, restarts Jellyfin, and runs playback/security
tests. The test repository changes only the package URL to its Docker-local fixture;
ZIP bytes and MD5 remain those of the generated package. No public release is required.

For native update/version-history testing, use the root latest-target build context,
build the example consumer, then run `./scripts/test-upgrade.ps1 -Directory <new-temporary-directory> -Image <this-run-resolved-image-digest>`.
Always supply the current-run image rather than this legacy helper's IDE default.
Fixture versions 0.0.1/0.0.2 are built only there; the test installs the older plugin
on the latest server, publishes the newer fixture, runs Jellyfin's Update Plugins
task and verifies restart activation, retained settings, version listing and invalidated
guest credentials, followed by playback/security checks. There is no older-server
matrix. Fixture builds change local `bin` output; rerun normal packaging afterward.

The fixture resolves this run's current stable official image/digest, uses only new
disposable data and loopback ports, and generates synthetic media and a short-lived
test certificate. No existing server data is mounted. The certificate exception is
scoped to that test client, not production TLS. Diagnostics remain in the printed
temporary folder.

Use a disposable current stable Jellyfin instance, HTTPS, two test accounts and a short local video:

1. Install/restart, load both Modern and Legacy web layouts, and verify the launcher and dashboard settings.
2. Create private/public groups. Confirm `/SyncPlay/List` contains only public groups; outsider ID-based join and detail lookup fail. Check native/public joins also obey capacity.
3. Set a per-user private default and denied creation mode. Test both the custom and stock create endpoints. Verify no policy bypass with manually constructed requests.
4. Enable guest sharing for only one owner. Open a guest-enabled link in a private browser. Inspect requests: no Jellyfin token, original media URL, library response, server path or unrelated metadata should appear.
5. Test simultaneous redemptions at the capacity/use limits, lease expiration/reconnect, account-only links, invite expiration/revocation, and admin Allow→Deny→Allow. Old revoked guest credentials must remain invalid.
6. Test host play/pause/seek, track changes and network recovery. Attempt arbitrary segment names, other media generations, and normal Jellyfin APIs using only the guest cookie.
7. Remove the host while another signed-in member remains; guests may continue. Remove the final signed-in member; all guest requests must fail, invitations must expire and the encoder must stop.
8. Disable sharing, remove owner media or required conversion permissions, disable/delete the owner, and restart the server. Verify further guest delivery is denied. Verify that a remux-only stream does not require video transcoding permission. Review FFmpeg load and cache cleanup.
9. Use compatible and incompatible sources at different bitrate caps. Confirm remux/direct stream before video conversion; then configure your HWA backend and verify the generated FFmpeg process uses it. Check your preset, thread count, device permissions and tone-mapping settings.

Automated tests exercise admission limits, private ID/list guards, invitation and lease lifetimes, permission inheritance/revocation, concurrent redemption, origin/HTTPS checks, route separation and asset allowlisting. They do not substitute for the real-server checklist.

## References

- [Current installation](../../README.md#installation-and-upgrades) · [Dependencies](../../docs/dependencies.md)
- [Testing](../../docs/testing.md) · [Current release procedures](../../docs/independent-plugin-releases.md)
- [Plugin/guest UI SDK](docs/plugin-api.md) · [Hub adapter](docs/account-hub-integration.md)
- [Security review](docs/security-review.md) · [Current evidence](../../docs/review-status.md)
- [Jellyfin transcoding](https://jellyfin.org/docs/general/post-install/transcoding/)
