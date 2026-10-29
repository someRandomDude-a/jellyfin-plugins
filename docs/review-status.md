# Current review status

**Not release ready. Nothing committed or published.**

## Current work

### Latest verified outcome — 0.0.37.0, Linux x64

**Newer source work is active:** factor continuation now carries only a five-minute
per-tab intent expiry across Security's marker-dropping redirect; no challenge,
identity or credential is stored there. The source is newer than 0.0.37.0 and
requires a fresh package/native acceptance. The owner additionally requested
automatic latest tools/version bumps before tests and releases. Official
checksum-verified Node/PowerShell/SDK bootstrap is wired into local test/package
and shared CI target entry points; both release engines preflight before tagging.
The dependency bump preview is hourly alongside the existing protected Renovate
PR/validation/promotion workflow. No workflow was triggered or release published.
Discovery-only bootstrap, workflow lint and real Windows tool installation pass.
`artifacts/grouping-diagnostics-tool-bootstrap-install-r1.log` verifies official
checksums and executable versions for Node 26.13.0, PowerShell 7.6.9 and SDK
10.0.403. **223 script regressions pass** in
`artifacts/grouping-diagnostics-scripts-r17.log`. Current direct-image updates
(Python digest/cloudflared 2026.10.6) pass the 99-declaration freshness audit in
`artifacts/grouping-diagnostics-freshness-r11.log` and offline version launches.
Hosted CI and Linux tool installation are not yet end-to-end certified; current
plugin-source changes still require a fresh coherent package/native gate.

The fresh matrix **passes 128/128 unique combinations**, including **64 with
native Security**, in `artifacts/grouping-diagnostics-combinations-r11/results.json`.
The exact previously failed Security combination also passes separately in
`artifacts/grouping-diagnostics-security-route-r1.log`. Restored PowerShell is
checksum-verified; preflight now stops promptly on runner loss. The original
93/128 matrix failure and its missing native Security directory remain retained
evidence, with no claim to have explained the external disappearance.

Completed for this package: 1,201 .NET tests (one optional skip), full-suite and
standalone Hub/Notifications browser acceptance, native grouping/defaults/user
overrides, same-tab synthetic linking and exact-account consent, signup,
combination-independent diagnostics/cached-owner navigation, themes/studio,
eight-database restart/recovery and extended native Security. Current script
regressions pass 218 cases; later changes are fixture-only.

Remaining: factor-enrolled same-tab link continuation, then renewed human
Discord acceptance only after the owner is ready. Fresh target discovery passes in
`artifacts/grouping-diagnostics-targets-r2.log`, with unchanged Jellyfin 12.4,
Security 2.6.5, Enhanced 12.14.0.0 and Seerr 3.5.1, reviewed contracts and a new
snapshot at `artifacts/grouping-diagnostics-current-r2/targets.json`. The old
snapshot is not an expired fallback. Three exact-package gates are running:
`artifacts/grouping-diagnostics-factor-link-r1.log` (native public-API enrollment
and actual browser factor challenge, no pre-factor handoff consumption),
`artifacts/grouping-diagnostics-seerr-r10.log`, and
`artifacts/grouping-diagnostics-syncplay-r10.log` after the isolated consumer build
in `artifacts/grouping-diagnostics-consumer-r10.log`. The SyncPlay catalog copies
the immutable archive and original checksum without rebuilding it. No pending
gate is counted as passed. Windows/ARM and broader production boundaries remain
separate. No live human fixture or release is active.

The exact-package **real Seerr gate passes** in
`artifacts/grouping-diagnostics-seerr-r10.log`: Seerr 3.5.1 public-API onboarding,
native import/lookup, quotas/inheritance/permissions/request paging/deletion, plus
installed Hub provisioning/drift and native-account deletion follow-up. This is
not real media-removal certification. **Native SyncPlay/consumer/media also passes**
in `artifacts/grouping-diagnostics-syncplay-r10.log`, with the isolated consumer
build in `artifacts/grouping-diagnostics-consumer-r10.log`: immutable catalog
install, native guest/HLS/captions/encoder/moderation/revocation, isolated public
consumer API and database integrity. Native screenshot/JSON evidence from the
full run is preserved in `artifacts/grouping-diagnostics-native-r10`.

The first factor-enrolled link attempt fails in
`artifacts/grouping-diagnostics-factor-link-r1.log`: the browser does not reach
the expected native challenge and loses the marker after native navigation. The
fixture now asserts native enrollment state and captures only auth response flags
and endpoint status in a new diagnostic repeat; no enrolled-factor pass or native
factor bypass is claimed. A read-only direct-dependency audit is also running in
`artifacts/grouping-diagnostics-freshness-r10.log`.

### Producer grouping, native onboarding and shared diagnostics — active source work

The owner superseded the current human run with new requirements. Inbox grouping
must use each registering plugin's declared metadata key (for example show ID),
with administrator per-plugin defaults and independent personal inherit/on/off
choices. Source grouping/projection/preferences and bell/admin controls are being
implemented; unregistered keys never infer a group, and rows retain individual
authorization, actions and read/dismiss state. Library notices now carry show/movie
grouping identity; party notices declare party grouping and action-specific text.

The broader requested work remains active: automatic session-aware link review
without anonymous Jellyfin identity disclosure; native-login-styled signup with
signed-in detection, safe actionable failures and normal native-login redirect;
one combination-independent diagnostics database, user-facing opaque error IDs,
administrator search/owner navigation/clear/level controls and secret-safe details;
and useful audience-safe notifications across all plugins. Current source is not
certified by the preceding 0.0.27.0 package evidence. No new release claim is made.

The error boundary also applies to ordinary UI callbacks, native/AJAX failures,
guest playback, legacy account pages and Security integration controls. User
messages use fixed safe categories, not provider prose, internal codes, exception
text, model-state values or technical help. Native verification signals and
machine-readable compatibility/no-retry codes remain intact. An opaque ID is
shown when supplied by the server; client/network failures do not invent a
searchable server record. Technical investigation is administrator-only, and even
diagnostics omit message formatters, state/scope arguments, credentials, payloads,
private links and exception messages.

Latest completed coherent backend/package gate:
`artifacts/grouping-diagnostics-backend-package-r12.log` records **1,178 .NET
passes**, one optional external-service skip, and six validated local **0.0.34.0**
Linux archives in `artifacts/grouping-diagnostics-packages-r7`. It predates the
same-tab continuation and explicit invitation direction changes below.
`artifacts/grouping-diagnostics-scripts-r10.log` records **213 script passes**;
`artifacts/grouping-diagnostics-syncplay-ui-r6.log` records **71 SyncPlay UI passes**;
`artifacts/grouping-diagnostics-bell-r3.log` records **60 bell passes**. Security
client tests (5) and legacy account-client tests (3) also pass. Backend r4's stale
startup-filter count included MVC's own filter; the corrected assertion still
requires exactly one diagnostics startup owner. Raw native content/status results
and framework-provider failures now have explicit redaction regressions. The
removal UI's asynchronous status test waits for the projected state instead of
assuming two fixed timer ticks. These tests do not certify native installations,
cross-plugin controller ownership, Windows/ARM or a new human Discord run.

A fresh immutable local **0.0.28.0** Linux package candidate built successfully in
`artifacts/grouping-diagnostics-packages-r1` (six validated archives, each containing
`Jellyfin.Diagnostics.dll`; build/inventory log:
`artifacts/grouping-diagnostics-package-r1.log`). Standalone Notifications completed
its API/library gate, but the all-six run failed on a domain denial incorrectly
rewritten from 403 to 500. Native logs also caught redaction of already-started
adapter responses. The correction runs redaction after domain envelope filters,
preserves verified provider diagnostic IDs and skips already-written safe adapters;
explicit regression tests cover both cases.

`artifacts/grouping-diagnostics-backend-package-r6.log` builds the corrected local
**0.0.29.0** candidate in `artifacts/grouping-diagnostics-packages-r2`. Its full
installed run (`artifacts/grouping-diagnostics-full-r2.log`) passes shared
diagnostics/permissions, domain 403 preservation, signup/groups/library and abrupt
restart/integrity across **eight databases**, then fails before browser execution
because the new browser flag was omitted from the fixture restart condition.
The standalone browser run (`artifacts/grouping-diagnostics-standalone-r2.log`)
passes APIs/library but fails at lookup. A concurrent initial-list/search race was
identified and is now covered by queued-read and stale-list/owner-navigation tests.
Those follow-up UI/health-compatibility changes are not certified by the 0.0.29.0
package. No whole installed/browser pass is claimed for either predecessor.
The **0.0.30.0** standalone/full installed/browser repeats and a fresh **128-case**
matrix (64 with native Security, all with shared diagnostics wiring checks) are
recorded in `artifacts/grouping-diagnostics-{standalone,full}-r3.log`
and `artifacts/grouping-diagnostics-combinations-r3/results.json`. The matrix
completed **128/128 unique passes**, including **64 with native Security**, for
**0.0.30.0 only**. The full fixture passes native grouping/defaults/user overrides,
eight-database integrity and missing-state recovery, then fails the browser's strict
page-error assertion. The standalone browser reaches layout/levels/clear checks,
then its checkbox action targets a row collapsed by the preceding reload; the
harness now reopens the current row and uses its visible native label. Neither
browser run is counted as passing. A dedicated owned debug fixture is retaining
sanitized page-error evidence rather than hiding those errors. A native-control
regression also reproduced missing stable IDs/associated labels; the follow-up UI
now supplies native labelElement/container/focus contracts and themed selects.
Those latest UI changes are newer than the 0.0.30.0 archives and need a fresh
candidate. The installed library fixture now also checks producer-supplied
show/movie grouping keys, revisioned admin defaults and per-user inherit/on/off
isolation without external replay. The native browser harness checks opaque lookup,
owner navigation, native dark/light × 320/1280px layouts, level controls, explicit
selected clearing, ordinary-user denial and page errors; it does not suppress errors.

The **0.0.31.0** full installed/browser repeat now **passes** in
`artifacts/grouping-diagnostics-full-r4.log`, including all-six authorization and
source discovery, native grouping/defaults/user overrides, signup/groups, abrupt
restart/eight-database integrity, original-state recovery, cross-owner diagnostics
navigation, four native theme/width cases, levels, explicit clearing and ordinary
user denial with **zero page errors**. Credential-free JSON/screenshots are retained
in `artifacts/grouping-diagnostics-native-r4`; the narrow dark view was visually
reviewed. Earlier page errors were native `attachedCallback` failures setting a
missing label's `htmlFor`; IDs/associated labels/focus containers fixed them. The
owned debug container `jellyfin-suite-test-dcf68182` was verified by its exact config
mount and removed after preserving sanitized evidence.

The 0.0.31.0 standalone browser repeat instead timed out trying to sign in to
`suite-user`, which only the Hub fixture creates. The harness now creates a
dedicated hidden ordinary test account for any plugin selection; no product
authorization requirement was weakened. Standalone native/browser acceptance must
be repeated. Invitation markup was also aligned with the installed native login's
`standalonePage`/`backdropPage`, centering and form placement, rather than interior
dashboard classes. A fresh **0.0.32.0** candidate is building for that source, and
new disposable native-browser signup checks cover session detection, layout,
pre-reservation validation, normal-login redirect and used-link denial. Those
newest invitation changes are not certified by the 0.0.31.0 pass or running
0.0.30.0 matrix.

**0.0.32.0** builds after **1,177 .NET passes** (one optional skip) in
`artifacts/grouping-diagnostics-backend-package-r9.log`. Standalone Notifications
now passes its own installed/native-browser gate in
`artifacts/grouping-diagnostics-standalone-r5.log`, with four theme/width cases,
normal-user denial and zero page errors; sanitized evidence is retained in
`artifacts/grouping-diagnostics-native-standalone-r5`. The full run passes API,
grouping and storage gates but its signup browser check targets the initial submit
button text after validation changes it; the harness now targets the form's submit
action. The separate Hub signup repeat in `artifacts/grouping-diagnostics-signup-r5.log`
passes session detection, invalid username before reservation, native-login redirect,
new-account sign-in and used-link denial with zero page errors. **Do not treat its
automated layout checks as a visual pass**: screenshot review found the native fixed
background painted over the static fields/headings. The latest source positions
the standalone page above that background and the browser harness now requires
visible, hit-testable headings/labels/inputs/buttons before recording layout passes.

A real `ILoggerFactory` regression also proved that host-level logging defaults
discarded debug events before diagnostics could apply its own selected threshold.
The correction is provider-specific and leaves the native/global threshold intact;
it still never formats state, scopes or exception messages. The first 0.0.33.0
attempt failed compilation on a logging overload before creating any packages
(`artifacts/grouping-diagnostics-backend-package-r10.log`). The corrected serial
backend/new-package run is `artifacts/grouping-diagnostics-backend-package-r11.log`
and targets `artifacts/grouping-diagnostics-packages-r6`. Its logger/paint fixes are
newer than every preceding installed pass; no latest-source acceptance is implied.

**0.0.33.0** standalone Hub signup passes the stronger paint/pointer checks in
`artifacts/grouping-diagnostics-signup-r6.log`; screenshots confirm fields are
visible above the native background. The current source also sets explicit English
`dir="ltr"` so native safe-area padding activates; this is newer than that pass.
Cached diagnostics-owner restoration has a passing unit regression, but its repeated
native browser case has not yet passed on the newest package.

Same-tab `/link` is now wired in source: signed-out and rejected-session paths
prepare a server-memory handoff with an anonymous same-origin POST, then navigate
the same tab to native login with only a credential-free return marker. The native
helper uses an HttpOnly, Strict, path-scoped session cookie to resume review, not
confirmation. Challenges are not persisted in browser storage; transport is bounded,
volatile, expires within five minutes/original challenge expiry, and is one-use.
Unknown handoff outcomes are not retried. Original durable challenge confirmation,
exact-account consent and native/Bridge authorization remain unchanged. Reset login
behavior stays separate. New store/controller/bootstrap/JS regressions cover these
boundaries. The first coherent attempt fails test compilation and the targeted
retry finds a wrong expectation about native cookie deletion; both test issues
are corrected without changing the authorization boundary. The coherent
**0.0.35.0** backend/package retry in
`artifacts/grouping-diagnostics-backend-package-r14.log` passes **1,201 .NET tests**
with one optional skip. The server restart interrupted captured output during
packaging, but the surviving owned container was separately observed exiting **0**
and six archives exist in `artifacts/grouping-diagnostics-packages-r8`. Installed
full/standalone/browser runs are active; no installed pass is claimed before they
complete. A new `--public-link-ui` disposable fixture seeds only synthetic
challenge rows and checks native same-tab login/review/confirmation; this is not
human Discord issuance. Its first standalone Hub attempt
(`artifacts/grouping-diagnostics-hub-r8.log`) reaches native Web but times out on
an exact login URL assertion. The browser harness now waits for actual native
login fields and records sanitized routing state. Its repeat reaches native sign-in
but remains at `#/home?accountHubLinkReturn=1` rather than reopening review; this
is an unresolved native continuation failure, not a passing flow. A dedicated
disposable debug repeat confirms the helper was injected and the native client
was authenticated, but no continuation request was made. Native SPA history
navigation can skip `hashchange`; source now checks the credential-free marker on
a 500ms interval, without replaying any request. The regression and all **214**
scripts pass in `artifacts/grouping-diagnostics-scripts-r11.log`. The serial
**0.0.36.0** backend/package gate passes **1,201 tests**, one optional skip, and
six local Linux archives in `artifacts/grouping-diagnostics-packages-r9`; evidence
is `artifacts/grouping-diagnostics-backend-package-r15.log`. The standalone Hub
and full installed/browser repeats were started. The standalone Hub repeat
`artifacts/grouping-diagnostics-hub-r10.log` still fails at native continuation:
the helper and authenticated client exist but no request is sent. The interval
alone is not yet reliably verified. A second disposable debug run successfully
performs same-tab sign-in, review, exact-account denial, consent/link and
replay/expiry/origin checks, then fails its existing-session case because navigating
to the same path with a new fragment did not reload the public script. The harness
now opens a new document before testing that case; another repeat is running. The
earlier continuation timeout is now traced in the full-suite sanitized evidence:
Jellyfin bubbles a `pagehide` CustomEvent from an SPA view, incorrectly triggering
the helper's browser-lifecycle cleanup. The source now ignores those custom events
and stops only on actual `PageTransitionEvent`; both cases have regressions, and
all **216 scripts** pass in `artifacts/grouping-diagnostics-scripts-r12.log`.
The 0.0.36.0 repeat also fails on this issue. A fresh serial **0.0.37.0** build
and package gate passes **1,201 .NET tests**, one optional skip and six Linux
archives in `artifacts/grouping-diagnostics-backend-package-r16.log`; standalone
Hub and full installed/browser repeats were started. The standalone Hub run
`artifacts/grouping-diagnostics-hub-r12.log` now passes synthetic same-tab native
login, exact-account review/consent, no automatic link, one-use cookie, replay,
expiry/origin/credential denial and existing-session review with zero page errors;
it also passes signup's session detection, native layouts and padding, validation,
normal login redirect/sign-in and used-link denial. This is **not human Discord
issuance**. The full six-plugin repeat `artifacts/grouping-diagnostics-full-r10.log`
also passes API/auth/grouping, eight-database restart integrity and missing-state
recovery, synthetic same-tab review/consent and signup, and shared diagnostics
including repeated cached-owner navigation, four theme/width cases and zero page
errors. Narrow signup and exact-account review screenshots were visually reviewed.
Standalone Notifications diagnostics/studio also passes on 0.0.37.0 in
`artifacts/grouping-diagnostics-standalone-r10.log`: native library grouping,
ordinary-user denial, four diagnostics theme/width cases with zero page errors and
six studio theme/width cases with editing/persistence/live bell/no preview sends.
The first full theme/studio attempt (`artifacts/grouping-diagnostics-theme-r10.log`)
fails in an outdated responsive fixture that clicked the now-hidden manual Review
link button despite automatic authenticated review. The fixture now waits for
the actual reviewed accounts; the repeat `artifacts/grouping-diagnostics-theme-r11.log`
passes **18 responsive**, **30 native workspace/theme** and **six studio** cases
on 0.0.37.0. Native Security/proof and the 128-case matrix are running, with no
pass claimed before completion. The Security-enabled link browser case uses
an ordinary unenrolled disposable account; it does not itself certify an enrolled
factor challenge continuation.

The first 0.0.37.0 Security attempt `artifacts/grouping-diagnostics-security-r10.log`
passes native Security 2.6.5 HMAC/replay/tamper, exact-session reset, real factor
enrollment/proof/recovery/replay, native session termination and eight-database
restart/recovery. It then fails when the new link fixture tries to create its user
after administrator factor enrollment: native user-creation override correctly
requires fresh proof. The harness now creates that disposable user before factor
enrollment, with no Security bypass. The exact-package repeat
`artifacts/grouping-diagnostics-security-r11.log` **passes** all those checks and
synthetic same-tab linking with Security installed and zero page errors. The link
test's ordinary user is not enrolled in a factor; factor-enrolled same-tab link
continuation remains a separate unverified case.

The current **0.0.37.0 matrix failed: 93/128 pass, 35 fail**, recorded in
`artifacts/grouping-diagnostics-combinations-r10/results.json`. Of the failures,
34 stop on `ENOENT` launching the configured temporary PowerShell executable;
that exact executable is now absent. One additional combination
(`hub+deletion+syncplay+notifications+security`) reaches native APIs but receives
404 from `AccountHub/api/v1/security/devices`. The tool availability problem and
the devices-route failure require investigation and a fresh matrix; this is not
128-combination certification, regardless of the passing standalone/full Security
run. No background matrix remains running.

On the owner's rerun request, runner restoration is in progress from the latest
official PowerShell release with its published SHA-256 checked before extraction.
New fixture preflight rejects missing/broken/old PowerShell before creating
servers, and the matrix checks again between cases and stops on tool loss without
manufacturing plugin failures. All **218 scripts** pass in
`artifacts/grouping-diagnostics-scripts-r13.log`; focused runner/isolation checks
pass 19 cases in `artifacts/grouping-diagnostics-matrix-preflight-r1.log`.
Read-only retained diagnostics confirm the 404 was produced inside Hub's existing
Security devices action (`not_found`, diagnostic `5526d7dc-1dfa-4320-950b-089da94a4666`),
not a missing Hub route. A failed-read evidence hook now compares native endpoint
status without dumping device data or credentials. Exact-combination reproduction
and a fresh matrix remain pending tool restoration; no corrected pass claimed.

The first official PowerShell restoration download was truncated (56,819,548
bytes versus the release's 109,620,855) and failed its SHA-256 check. It was not
extracted or executed; the rejected archive is preserved. A second verified
download is active. Further read-only investigation of the failed fixture finds
the native Security `TwoFactorAuth/users` directory absent; its endpoint log
reports a missing path. This is not a missing Hub MVC controller and no original
Security state is recreated or bypassed. Fresh owned-fixture reproduction remains
required; the cause of the directory/tool disappearance is not yet established.

Official PowerShell **7.6.9** restoration succeeds on the second download, with
release size/SHA-256 and executable launch verified; evidence is
`artifacts/grouping-diagnostics-powershell-restoration-r2.json`. The unchanged
0.0.37.0 packages are now under exact-combination reproduction in
`artifacts/grouping-diagnostics-security-route-r1.log` and a fresh complete matrix
in `artifacts/grouping-diagnostics-combinations-r11`. No rerun pass is claimed
before completion; earlier failed logs and the rejected download are preserved.
The exact failed Security combination now **passes** in
`artifacts/grouping-diagnostics-security-route-r1.log`, including status/users/
devices and HMAC/replay/tamper checks in a new owned fixture. No product workaround
or state recreation was needed. This does not establish why the prior fixture's
Security directory disappeared, and the full matrix remains running.
The first 0.0.35.0 standalone Notifications
attempt (`artifacts/grouping-diagnostics-standalone-r8.log`) passes APIs/library
grouping but its browser harness reads the initial list before an exact-ID lookup
finishes; the harness now waits for the one-record rendered status and the repeat
passes in `artifacts/grouping-diagnostics-standalone-r9.log` on 0.0.35.0, including
native library grouping, diagnostics controls, ordinary-user denial, four native
theme/width cases and zero page errors. Full/latest Security, matrix and other native
gates must be rerun against the exact new candidate.

The **0.0.35.0** full attempt (`artifacts/grouping-diagnostics-full-r8.log`)
passes installed authorization, shared diagnostics APIs, grouping, onboarding
APIs, eight-database restart integrity and missing-state recovery, then fails at
the same link browser continuation before signup/diagnostics browser acceptance.
Those partial results are not a full installed/browser pass.

The authorized Discord fixture was deliberately stopped before editing, interrupting
its human phase. The owner reported successful linking and status; signup limits,
party redemption and privacy were not completed or confirmed. Its sanitized log
remains `artifacts/notification-catalog-discord-human.log`; its owned container,
tunnel, credential-bearing temporary state and private readiness file were cleaned.
Root `.env` values were loaded only for that authorized run, never printed or edited.
No current human acceptance pass is claimed, and no replacement live run is active.

### Earlier human Discord window — interrupted by the new implementation request

The owner explicitly announced readiness after local non-human gates passed.
The exact 0.0.27.0 invitation/watch-party human fixture became ready with
`--human-invitations --discord-tunnel`, the configured consenting recipient and a
60-minute human window. Only this explicitly authorized live process loads the
ignored root `.env`; values are not printed or changed. Its credential-bearing
fixture/readiness state is in the approved temporary directory, not published
artifacts; the sanitized log is `artifacts/notification-catalog-discord-human.log`.
The later interruption/cleanup is recorded above; this window is no longer active.
No human acceptance pass is claimed. HTTPS reachability and native trusted-proxy
authorization passed, and the private readiness file was opened for the owner's
disposable sign-in details without printing passwords in chat. Actual
handler results and native signup records are checked separately from explicit
human confirmation of party redemption, recipient privacy and third-use denial.

### Grouped registered-notification navigation — installed gates pass on 0.0.27.0

The latest owner request groups the admin catalog by **registering plugin →
plugin-reported notification type**, using existing source/name/type registration
metadata rather than inventing categories from event-name prefixes. Plugin/type
sections start collapsed, sort by display name, show source/type identifiers and
plugin type counts, and use native keyboard-accessible disclosures. Unknown plugin
sources remain visible. Collapsing a section preserves the active studio draft;
navigation performs no writes and source/type/revision save boundaries stay intact.
No registry schema, authorization, delivery or existing custom layout changes.

All **56 focused catalog/studio/frontend tests pass** in
`artifacts/notification-catalog-focused-r2.log`, including duplicate names across
plugins, exact reported IDs, unknown/empty sources, plain-text labels, retained
drafts and per-plugin revision/save isolation. The serial root/new 0.0.27.0 Linux
package gate PASSES in `artifacts/notification-catalog-root-package.log`:
**1,129 .NET / 331 JavaScript tests**, one optional external-service skip and six
validated Linux packages in `artifacts/notification-catalog-packages`.
Installed acceptance now navigates the grouped catalog and verifies every reported
plugin/type is represented, checks narrow/wide navigation geometry and retained
drafts without writes, and captures collapsed/expanded plugin menus. Both installed
runs PASS in `artifacts/notification-catalog-{standalone,installed}.log`. The full
suite verifies **six plugin groups / thirteen reported types**, retained studio
drafts without writes, all six dark/light × 320/390/1280px studio cases, loaded
left-poster geometry and strict page-error checks. It also passes installed
authorization/wiring/invitation/groups/library, restart/seven-database integrity,
missing-state recovery, 18 responsive cases and 30 native-theme/workspace cases.
Collapsed narrow/wide and expanded narrow plugin menus were visually reviewed.
The 0.0.26.0
passes below certify the prior frontend only. Human Discord testing was unstarted
at completion of these non-human gates; its later authorized window is tracked above.

Exact 0.0.27.0 extended Security/reset/proof/recovery PASSES in
`artifacts/notification-catalog-security.log`: exact-session reset/negative cases/
factor preservation, real TOTP/recovery consumption/replay protection, trusted-device
revocation, native session termination, HMAC ingress/replay/tamper and restart/
seven-database integrity/missing-state recovery. The chained matrix PASSES **128
unique cases**, zero failures, **64 with Security**, in
`artifacts/notification-catalog-combinations/results.json`.
Exact filtered-catalog SyncPlay/consumer/media and real Seerr PASS in
`artifacts/notification-catalog-{syncplay,seerr}.log`: native guest/HLS/captions/
encoder/moderation/revocation/consumer and database checks, plus real Seerr public
API and installed Hub account provisioning/quotas/drift/native-deletion follow-up.
These are not human Discord/party redemption or real media deletion certification.
The owned retained 0.0.26.0 debug container `jellyfin-suite-test-ab78cbf4` and its
browser tab were cleaned after preserving its native log in
`artifacts/notification-studio-poster-retained-native.log`; fixture files and all
failed evidence remain intact. Unrelated containers were not stopped or removed.
Current grouped-catalog static checks PASS: **190 script tests** in
`artifacts/notification-catalog-all-scripts-final.log`, **96 direct dependency
declarations** in `artifacts/notification-catalog-freshness-final.log`, Actionlint
in `artifacts/notification-catalog-actionlint-final.log` and whitespace in
`artifacts/notification-catalog-evidence-whitespace-final.log`. No dependency
manifests or locks were modified by the freshness check.

**Historical local candidate: 0.0.27.0, Linux x64 only.** Applicable local root,
packaging, standalone/full browser, native SyncPlay/consumer, real Seerr, extended
Security and 128-combination gates above pass. This is not release readiness:
Discord human commands/DM/redemption/privacy remain unverified (the later authorized
window is tracked above); Windows/ARM, full owner-plugin profile, native-origin
outbound webhook transport and broader fault/proxy certification remain separate
unclosed boundaries. Nothing committed, tagged, pushed or published. Root `.env`,
original identities, histories, keys and credentials remain untouched.

### Notification Studio frontend review — installed gates pass on 0.0.26.0

The owner requested a proper frontend review, not just test completion. The studio
now has a dedicated responsive canvas/layers/inspector workspace, compact native
controls, bounded direct resizing and keyboard editing, layer duplication/order
tools and accurate dirty/save acknowledgments. Selecting a layer is not an edit.
At the owner's request, **Ctrl+Z / Ctrl+Y** replace dedicated Undo/Redo buttons;
shortcuts stay scoped to the studio and leave native text-field undo alone.
The subsequent PowerPoint-style request adds screen-pixel edge/center snapping,
live guide lines, Alt bypass and explicit Left/Center/Right/Top/Middle/Bottom
alignment relative to the card or another layer. Resize handles snap too. Snapping
preferences/reference selection are not saved-design edits; geometric alignment
is undoable and bounded. New pure-geometry and pointer-handler regressions pass.
The latest default-poster request also fixes two live-card defects: compact inbox
image CSS could override canvas poster sizing, and image intake accepted only the
legacy Hub source, not Notifications' own library source. Canvas posters now use
the left column with Contain, text starts after a clear gap, and timestamps occupy
a separate grid row. New text layers use the text-column defaults. Saving an
untouched opened designer persists its actual displayed canvas. Native image
authorization, matching item/image IDs and current-session checks remain intact.
All **53 focused frontend tests / 58 bell tests pass**. Installed acceptance now
creates a local synthetic native poster and checks actual image loading, left
placement, gap, sizing and timestamp separation. Current-package standalone
acceptance now PASSES, both standalone and with all six plugins.
Focused tests cover history, selection, resizing/cancellation, bounds and stale-save
acknowledgments. An initial 0.0.23.0 standalone
installed run passes six theme/width/shortcut/drag/persistence cases. Its native
screenshots exposed shared admin margins bloating the inspector; scoped overrides
and a bounded desktop inspector now fix that. Native input nodes persist through
edits for stable focus/Tab order; empty/out-of-range fields block a save. The final
acceptance also tests real resizing, native Tab order and invalid-save refusal.
The serial 0.0.24.0 run completed in
`artifacts/notification-frontend-final-root-package.log`, but the snapping request
arrived during it. The 0.0.23.0/0.0.24.0 results do not certify the latest refinements.
The 0.0.25.0 run completed as the poster request superseded it; no cancellation
actually occurred (the owned build container had already exited). The coherent
current-source serial run PASSES in `artifacts/notification-studio-poster-root-package.log`:
**1,129 .NET / 324 JavaScript tests**, one optional external-service skip, six
validated **0.0.26.0 Linux packages** and the external consumer build. Exact-package
native SyncPlay/consumer/media and real Seerr account-lifecycle acceptance PASS in
`artifacts/notification-studio-poster-{syncplay,seerr}.log`.
Earlier frontend candidates are not promoted as final proof of the poster changes.

The first 0.0.26.0 standalone studio run passed poster geometry but failed the
strict reload page-error gate: native capability registration returned 404 after
the HTTP-only harness relay dropped WebSocket upgrades. The fixed-upstream relay
now preserves HTTP identity/auth and bidirectional WebSocket upgrade/head data;
four relay regressions pass. No fake sessions, error suppression or write retries
were added. Its first two retained-fixture repeats reached live-poster verification
but failed on detached-row screenshot/scroll stabilization, not geometry. Those
failed logs remain in `artifacts/notification-studio-poster-relay-{review,capture}.log`.
Refreshing inbox screenshots now capture the viewport without retaining a row.
Fresh standalone acceptance PASSES in
`artifacts/notification-studio-poster-relay-standalone.log` and its
`studio-results.json`: six dark/light × 320/390/1280px cases, Ctrl+Z/Y, alignment,
guides, Alt bypass, resize, stable native Tab order, invalid-save refusal,
persistence, loaded native poster geometry and strict page-error assertions.
The full-suite attempt in `artifacts/notification-studio-poster-relay-installed.log`
passes installed API/permission/invitation/library/restart/storage and 18 responsive
checks, then fails the theme harness's hidden Layout control action: that control
is now inside the collapsed Message & delivery behavior disclosure. The harness
now opens the disclosure normally; it does not force interaction with hidden
controls. The fresh full-suite run PASSES in
`artifacts/notification-studio-poster-relay-installed-r2.log`: all-six installed
authorization/wiring/invitation/groups/library, restart/seven-database integrity,
missing-state recovery, 18 responsive cases, 30 native-theme/workspace cases and
all six studio cases with loaded-poster geometry and strict reload page-error
assertions. Its live-poster and desktop inspector captures were visually reviewed.
The first attempt did not reach its studio gate. Both passing runs use the exact
0.0.26.0 packages; only harness corrections followed packaging.

Direct authenticated native-browser inspection of 0.0.26.0 confirms the loaded
Contain poster at the left edge, an **11.8px gap** before both text layers and a
timestamp below the canvas. Desktop light, mobile dark, live-poster and center-guide
captures were visually reviewed; the default poster/text gap is visible. The
desktop-app screenshot operation itself was unavailable because its tab was not
visible, so reviewed screenshots are installed Playwright captures, not a claimed
desktop capture. Current direct browser console failures are caught missing-episode
image 404s, not native capability/session failures. The first current-package
Security attempt (`artifacts/notification-studio-poster-security.log`) timed out
downloading the release asset before fixture startup; it did not execute Security
tests or its chained matrix. A fresh extended Security attempt and chained 128-case
matrix use the existing exact asset after checking its SHA-256 against the same
unexpired current-run target, not an older-version fallback. Evidence paths are
`artifacts/notification-studio-poster-security-r2.log` and
`artifacts/notification-studio-poster-combinations-r2/results.json`; both PASS on
0.0.26.0. The matrix contains **128 unique passing cases**, zero failures, **64 with
Security**. Native acceptance covers exact-session reset/negative cases/factor
preservation, real TOTP/recovery consumption/replay protection, trusted-device
revocation, session termination, HMAC ingress/replay/tamper and restart/seven-database
integrity/missing-state recovery. These earlier-package passes do not certify the
subsequent grouped frontend. No human Discord
fixture, tunnel or live bot test has started.

Final script checks PASS **187 cases** in
`artifacts/notification-studio-poster-all-scripts-final.log`; direct dependency
freshness PASS **96 declarations** in
`artifacts/notification-studio-poster-freshness-final.log`, and Actionlint PASS in
`artifacts/notification-studio-poster-actionlint-final.log`. The separate relay,
studio, docs and route group also passes **24 cases** in
`artifacts/notification-studio-poster-relay-final-tests.log`. These do not substitute
for the separately recorded native Security/matrix gates.

The first 0.0.22.0 full run passes its theme/menu checks but later fails the studio
gate on an uncaught native `Response` error; the failed log is retained in
`artifacts/commands-studio-menu-final-installed.log`. Studio acceptance now records
error phase/stack and failed HTTP paths rather than only an unhelpful message.
The chained Security rerun did not execute after that failure. No new full-package
frontend or current-source completion claim is made yet.

**Previous local candidate: 0.0.22.0, Linux x64 only.** No commit, tag, push or
publication. Earlier candidate passes below retain their original scope.

| Current gate | Status / evidence |
|---|---|
| Serial root, six packages and consumer build | PASS: 1,129 .NET/310 JS cases; one optional external-service skip. `artifacts/commands-studio-menu-final-root-package.log` |
| Detached-menu regression | PASS: reproduced before fix; all 57 bell cases pass after. `artifacts/commands-studio-menu-detached-toggle-{before,after}-fix.log` |
| Standalone/full Notifications browser and storage | Standalone and full theme/menu/storage gates pass on 0.0.22.0; its later full studio gate fails on an uncaught `Response` and does not execute the chained Security stage. Current frontend changes require fresh acceptance. |
| Native SyncPlay/consumer and real Seerr | PASS against exact 0.0.22.0 packages: `artifacts/commands-studio-menu-final-{syncplay,seerr}.log`. Native media/consumer/isolation and real Seerr account lifecycle, not human redemption or production media deletion. |
| Extended native Security and 128-subset wiring | Both 0.0.21.0/0.0.22.0 matrices pass 128 unique cases (64 with Security each). Extended reset/proof/recovery passes separately on 0.0.21.0; the 0.0.22.0 chained rerun did not execute after its studio failure. |
| Discord human commands/DM/redemption/privacy | DEFERRED: owner must explicitly announce readiness. No human server/tunnel/live bot acceptance started. |
| Windows/ARM, full owner profile, native-origin outbound webhook transport | Separate unclosed acceptance boundaries; no local Linux pass certifies them. |

### Native invitations, Notifications studio and external Seerr changes — Linux acceptance

Discord Activities, embedded-video APIs/player/SDK, associated tests and dedicated
documentation are removed at the owner's request. The canceled human fixture exited
unsuccessfully; no real Discord OAuth/video pass was established. Historical
`discord-embedded-*` artifacts remain untouched but do not certify current source.

`/invite` now exposes bounded uses/expiry; Bridge has its own defaults/limits UI,
while Hub retains independent owner ceilings/delegation/template/quota checks.
`/watchparty [user]` posts a native link in the current approved channel by default
or DMs @user instead. Notifications alone observes library additions and owns scope,
type defaults, simulated examples and the safe text/image/font layout studio.

Fresh latest-target discovery is recorded in
`artifacts/notification-studio-current/targets.json`. Node 26.13.0 and hls.js 1.7.4
are current; the read-only freshness check passes 96 declarations in
`artifacts/notification-studio-dependency-freshness-r2.log`. Actionlint also passes.
All 164 top-level JavaScript and 55 bell tests pass in
`artifacts/notification-studio-all-js.log` and `artifacts/notification-studio-bell-final.log`.

Six synthetic 0.0.18.0 Linux packages build in
`artifacts/notification-studio-packages-r4`; all 324 affected backend/owner tests
pass in `artifacts/notification-studio-native-label-fix.log`. This set includes
library-selector ID normalization and removes Hub's unused library DLL/reference.

Installed standalone Notifications acceptance PASSES on that set in
`artifacts/notification-studio-standalone-r5.log`, without Hub or Bridge: library
capture/scope/access/preferences, administration and native Web ownership, plus
six dark/light × 320/390/1280px studio cases with drag/keyboard/fonts, saved
defaults/layout, reload persistence, live bell rendering and no preview publication.
The narrow dark screenshot was reviewed. No native-control JavaScript errors
remain; earlier failing logs are retained. Actual external Discord delivery and
native Security are not part of this fixture.

Earlier all-six installed authorization, groups/invitations, library,
restart/seven-database checks and storage recovery passed before the old studio
gate. The 0.0.18.0 full-suite browser rerun then finds a separate inbox menu
refresh race in the light desktop case. Source now retains open menu/focus only
for rows that remain in the freshly authorized projection; all 56 bell tests,
including a revoked-row regression, pass. The first fresh 0.0.21.0 installed run
also passes, but a repeat run exposes a second queued-toggle race described below.
The standalone 0.0.18.0 studio pass remains historical and scoped as above.

The first consolidated run passed its .NET suites but failed frontend loading
while dependency installation replaced node_modules. The next run in
`artifacts/notification-studio-root-final.log` passes all 1,108 .NET cases (one
optional external-service skip), but later packaging fails with MSB3030 on a
missing DeletionQueue.Core build output while another package run overlaps.
Neither attempt was a completed consolidated gate. The subsequent serial root and
six-package 0.0.19.0 run completed in
`artifacts/notification-studio-serial-final.log`. Both the 0.0.17.0 and 0.0.18.0
no-Security matrices passed all 64 subsets; these predate the newest changes.

External Seerr changes now have explicit read-only handling: verified direct
deletion, pending-request decline or missing upstream records close unclaimed work
as cancelled without a destructive send or invented FilesDeleted checkpoint.
Missing records do not prove physical file deletion. Changed connections, server
associations and media/quality mismatches fail closed. Claimed/uncertain work is
never automatically cancelled, reopened or resent. Final dispatch rechecks Seerr
state and live native permissions; current review also adds a personal-preference
revision guard during the final network read and stale-notification-action tests.

`artifacts/commands-studio-seerr-tests-package.log` passes **260 Deletion Queue,
66 Discord Bridge and 138 shared Notifications tests**, plus six validated
0.0.20.0 Linux packages. The subsequent serial run in
`artifacts/commands-studio-seerr-final-root-package.log` passes **1,129 .NET tests**
(one optional external-service skip), all aggregate root gates, six validated
**0.0.21.0 Linux packages** and the native external consumer build. This includes
264 Queue cases with preference-during-read and stale-action regressions.
Fresh **0.0.21.0** installed/browser checks PASS in
`artifacts/commands-studio-seerr-standalone.log` and
`artifacts/commands-studio-seerr-installed.log`: standalone library/access/preferences
and six studio cases without Hub/Bridge; all-six authorization/wiring, invitation/
group flows, restart/seven-database integrity, missing-state recovery, **30 workspace,
18 responsive and six studio cases** with persistence/live bell/no preview sends.
Narrow dark and wide light studio screenshots were reviewed. Tall element captures
include the native fixed header; freeform test drags deliberately move layers.
The nine-action permission editor passed, but its report incorrectly labelled the
count as twelve; the harness now records its measured count. The corrected-report
browser repeat (`artifacts/commands-studio-seerr-browser-r2.log`) then exposes a
second real menu race: a native details toggle queued on a detached old row can
close its authorized replacement after refresh. A deterministic regression fails
before the fix in `artifacts/commands-studio-menu-detached-toggle-before-fix.log`.
Only connected menus may now close peers; **57 bell tests pass** after that guard.
Browser fixture timestamps are also frozen and controls target an exact row ID,
not a moving first row. Fresh serial root acceptance PASSES in
`artifacts/commands-studio-menu-final-root-package.log`: **1,129 .NET/310 JavaScript
cases**, one optional external-service skip, six validated **0.0.22.0 Linux packages**
and the external consumer build. Current-package standalone/full browser, native
SyncPlay/Seerr, extended Security and 128-case wiring acceptance are now running.
The 0.0.21.0 passes do not certify this latest fix. Earlier report files remain unchanged.

Native SyncPlay's
first run found an obsolete consumer test still calling the removed Activity
endpoint; the failed log is retained in
`artifacts/commands-studio-seerr-syncplay-installed.log`. Only that obsolete block
was removed; native media and invitation/management consumer tests remain. The
fresh rerun PASSES in `artifacts/commands-studio-seerr-syncplay-installed-r2.log`,
with the exact final ZIP/checksum in its filtered catalog: real HLS/captions/burn-in,
native stream reuse, isolation, encoder limits/failure containment, moderation/
revocation and the external .NET consumer. **64/64 unique no-Security subsets PASS**
in `artifacts/commands-studio-seerr-combinations/results.json`.

Late-stage native Security extended reset/proof/recovery/storage acceptance PASSES
in `artifacts/commands-studio-seerr-security.log`: exact same-account native-session
reset verdict and negative cases, preserved factors, fresh TOTP/recovery consumption
and replay denial, trusted-device revocation, native session termination, signed
ingress/replay/tamper checks, restart/seven-database integrity and missing-state
recovery. This does not certify native-origin outbound webhook transport or every
fault/proxy boundary. Its 128-case with/without-Security wiring matrix PASSES in
`artifacts/commands-studio-seerr-security-combinations/results.json`; the later
0.0.22.0 matrix also passes all 128 cases in
`artifacts/commands-studio-menu-final-combinations/results.json`. Both are earlier
package evidence, not certification of the new frontend/poster source.
A separate real-Seerr
fixture failed before setup on startup socket/readiness races; failed evidence is
retained in `artifacts/commands-studio-seerr-real-seerr-failure.md` and the `-r2.log`.
The fixture now polls read-only setup readiness and avoids stale socket reuse,
without retrying mutations. Its fresh run PASSES in
`artifacts/commands-studio-seerr-real-seerr-r3.log`: real Seerr 3.5.1 public API,
native import/immutable lookup/quotas/permissions/request paging, installed Hub
provisioning/drift and native-account deletion follow-up. This is not real media
removal or independent physical-file verification; those queue edge cases remain
scoped gateway/core regressions, not production deletion certification.

The real `/invite` and `/watchparty` handlers have actionable privacy-safe guidance
for missing native devices/providers, invalid recipients and native policy/limit
failures. The unstarted `--human-invitations` fixture requires actual command
outcomes, exact native linking, bounded two-use/60-minute issuance and two native
signup completions; third-use denial, party redemption and recipient privacy still
require explicit human confirmation. The handoff now rejects pre-existing readiness
or confirmation files before live setup and preserves them; pure local focused
acceptance/studio/docs/route/isolation tests pass 19 cases. All **170 top-level
JavaScript tests** and **56 bell regressions** pass in
`artifacts/commands-studio-seerr-all-js-final.log` and
`artifacts/commands-studio-seerr-bell-final-r2.log`. The first standalone bell
command used the wrong working directory and failed before loading tests; its log
is retained, and the module-scoped rerun passes. Final read-only freshness passes
96 declarations; Actionlint and whitespace checks pass. **The owner requires Discord human tests last and will explicitly announce
readiness. No human server, tunnel or live bot tests may start before that.**
No commit, tag or publication occurred; `.env` remains unchanged and undisclosed.

### Confirmed v1 scope and current dependency/Discord work

- Invitations remain integrated in Account Hub. A standalone Invitations plugin
  and mandatory shared connection registry are removed requirements; plugins retain
  independent settings and optional feature dependencies.
- Discord uses native invitation links only; embedded video/Activities are removed.
  Signup options and party defaults retain independent Bridge settings.
- The permission UI is Discord-only: native-user defaults/Allow/Deny/Inherit, not
  guild-role grants. Native media access, Hub invitation delegation/recovery,
  SyncPlay guest enablement and channel disclosure checks remain mandatory.
- Local acceptance targets Linux. GitHub owns Windows/ARM/platform jobs. Native
  Security's final recovery/transport/cleanup suite is intentionally late-stage.
  Seerr's deletion report is trusted; independent physical-file verification is
  not required. Queue consent, approvals, delay and uncertain-effect guards remain.
- The direct dependency set is fixed; versions, locks, Actions and image refs must
  remain current. **Preview dependency bump** and `scripts/update-dependencies.mjs`
  share the manual local/Actions update engine, with protected Renovate automation
  retained. No new direct dependency or replacement was introduced.

The first local bump inspected 130 declarations and refreshed manifests/locks/refs.
The final fixed-direct-scope repeat inspected **128 declarations**, made **no
changes**, and recorded `verified:true` in
`artifacts/v1-direct-bump-verification/report.json`. The separate read-only audit now
passes **98 unique declarations**, including current Node, in
`artifacts/v1-dependency-freshness.log`. Host-provided SkiaSharp is derived from the
current Jellyfin source manifest (3.119.4 for this run), not independently bumped
to incompatible 4.x. Native EF/SQLite retain the same reviewed-source policy.

Before Activities removal, its then-current embedded Discord SDK brought an upstream
Zod memory-exhaustion advisory: `artifacts/dependency-bump-npm-audit.json` reported two
high findings through one transitive issue, with no compatible advertised fix. That
SDK/dependency tree is now removed. Per the owner's direction,
upstream transitive advisories are informational, **not project blockers**; direct
dependencies stay current. Existing pre-SDK message budgets remain; no forced major
override/removal was made. Final Linux root acceptance passes **1,098 .NET / 314 JavaScript
tests**, with the optional real-Arr test skipped, in
`artifacts/v1-linux-root-and-package-final.log`. Six coordinated Linux x64 packages
validate in `artifacts/v1-linux-packages-final` (synthetic fixture version 0.0.11.0,
not a created/pushed tag). Final installed/browser and native SyncPlay checks pass
on fresh disposable fixtures, as does the final **64/64 no-Security matrix** in
`artifacts/v1-combinations-final/results.json` (64 unique subsets, zero failures).
Earlier failed attempts remain under `artifacts/v1-linux-root-tests*.log`. Do not treat freshness,
static route tracing or UI unit tests as final installed/human acceptance.

The earlier v1 route inventory traced **213 MVC actions/catch-all adapters/minimal
templates**. The current [route review](route-authorization-review.md) traces **216**,
with eight inactive legacy lifecycle files excluded, in
`artifacts/commands-studio-menu-final-routes.json`. These are source traces and
runtime-test maps, not counts of individual passing authorization tests. Focused docs/policy/permission
checks passed 36 cases in `artifacts/v1-focused-tests.log`; actionlint and whitespace
checks pass. All work remains uncommitted/unpublished.

An initial installed/matrix attempt exposed a real guard-ordering regression:
link-confirmation permission denial masked the existing wrong-reviewed-account 409.
The first matrix passed 48/64; all 16 failures were Hub+Bridge subsets with that
same error (`artifacts/v1-combinations/results.json`). The native identity guard is
now evaluated first. Persistent user policy is also independent of bot connectivity,
while feature/user denials and send-time policy rechecks remain enforced. A failed
initial live attempt did not establish Discord acceptance; its details remain
sanitized. The corrected 0.0.10.0 matrix passed **64/64 without native Security**
(`artifacts/v1-combinations-r2/results.json`). Its installed API/storage and 18
responsive cases passed, but full browser acceptance found a shared help-tooltip
overflow: transformed bubbles fit visually while their original boxes expanded
document scrolling (`artifacts/v1-installed-r3.log`). Physical box positioning now
passes six real Chromium LTR/RTL cases at 320/390/1280px
(`artifacts/v1-help-layout-final.log`) and a unit regression. Final-package installed
acceptance and the 64-case matrix now pass, separately from the earlier packages.

Final 0.0.11.0 installed acceptance passes in `artifacts/v1-installed-final.log`:
all-six authorization/API-key/stale-session checks, Discord policy persistence and
link ceiling, invitation/group flows, library notices, abrupt restart/seven-database
integrity and missing-state recovery. Browser evidence in
`artifacts/v1-installed-final/theme-results.json` and
`artifacts/v1-installed-final/account-responsive-results.json` records **36 workspace,
18 responsive and four Discord permission-editor cases**, plus all six administrator
pages at 320/1280px without help overflow. The Account Hub 320px screenshot and four
dark/light × 320/1280px Discord editor screenshots were reviewed for wrapping,
native controls and theme contrast. Element screenshots include the native fixed
header intersecting the tall captured region; they are not full-page screenshots.
This run deliberately omits native Security and external Discord credentials.

Authorized real Discord gateway connection, guild/channel slash-command registration,
synthetic episode/movie cards and Notifications-to-Bridge approved-channel delivery
passed with 0.0.10.0 packages in `artifacts/v1-discord-live-r2.log`. Fixture-owned
messages and credential-bearing state were cleaned. This does **not** certify human
slash-command use, private DMs, watch-party redemption or embedded Activity video.
The final shared-resource change is tooltip positioning, not Discord transport.

Final 0.0.11.0 Private SyncPlay catalog/consumer/media acceptance passes in
`artifacts/v1-syncplay-installed-final.log`: native repository installation/restart,
HTTPS isolated guests, real HLS/segments/captions and styled burn-in, native member
stream reuse, bounded bitrate/encoders, failure containment, owner moderation,
revocation/no resurrection, external .NET consumer permissions and SQLite integrity.
The filtered catalog in `artifacts/v1-syncplay-catalog-final` preserves the exact
final ZIP bytes/checksum; the consumer was built separately against the current
host in `artifacts/v1-syncplay-consumer-build-final.log`. This is native SyncPlay
acceptance, **not** embedded Discord Activity playback or human watch-party acceptance.

Final read-only direct freshness passes **98 unique declarations** in
`artifacts/v1-dependency-freshness-final.log`; all **162** root script tests pass in
`artifacts/v1-all-script-tests-final.log`. Actionlint 1.7.12 passes in
`artifacts/v1-actionlint-final.log`. No remote workflow was launched.

### Documentation consolidation — local checks passed

Merged still-relevant safety/permissions, storage recovery, Security ingress,
producer/adapter contracts and scoped verification milestones into the current
guides, then removed **21 superseded audits, plans, handoffs and recovery/release
documents**. Useful SDK/domain contracts remain current developer references, not
old standalone installation instructions. Root, component and module guides link
to the canonical documentation index; obsolete catalog/version-edit instructions
and stale authorization claims were removed or corrected.

Documentation link checks now include module READMEs, retained contracts and
component fixture guides. Both documentation tests and all 26 selected dependency/
target-policy regressions pass (28 total); diff whitespace checks pass. These are
local documentation/policy checks, not upgraded-dependency, hosted-release or live
service acceptance. Original runtime logs/screenshots/state remain untouched, and
all changes remain uncommitted/unpublished. See the [guide index](README.md),
[safety boundaries](safety-and-permissions.md) and [evidence register](verification.md).

### Initial latest-only workflow audit — historical stale-dependency baseline

The bump workflow previously discovered many updates but did not guarantee every
manual release was current: test/browser tooling and Actions remained old, suite
manual releases omitted full external/matrix gates, and jobs resolved independent
targets. All CI/release graphs now share `latest-targets.yml` evidence, including
current Node/SDK and pulled upstream image digests; build/test jobs bind and verify
the actual toolchain. Suite releases require the full root Windows/Linux and
installed/browser/matrix/external gates. Independent providers add external-only
acceptance without running the unrelated full matrix. Both engines recheck
dependency/release/source/image freshness immediately before publication.

Before the current bump, the new read-only freshness audit **failed loudly**:
`artifacts/latest-only-final-freshness-audit.log` lists outdated Actions, Playwright,
test SDK/runners, SkiaSharp, jsdom/hls.js and tool images in that earlier source.
The current bump/freshness results are recorded above, separately from full
acceptance; no remote workflow/merge/release was launched. Historical IDE versions, migration
examples, existing catalogs and upstream-aligned native database ABI packages are
not indiscriminately rewritten. Manual Compose fixtures now require an explicit
current-run image rather than retaining Jellyfin 12.2/12.3 defaults.

All **38 focused policy/automation regressions** pass in
`artifacts/latest-only-final-policy-tests.log`; latest Actionlint **1.7.12** passes
in `artifacts/latest-only-final-actionlint.log`. The missing-target CI build guard
was also exercised and correctly failed (`artifacts/latest-only-missing-target-guard.log`).
The complete new GitHub workflow graph and upgraded dependency acceptance have
not been executed remotely; older local tests do not certify the current bump.
Local Renovate configuration validation passed with package/toolchain
deprecation warnings; it does not certify a remote run or successful upgrades.
See [dependency maintenance](dependency-maintenance.md) for the policy and unchanged
bot/branch-protection/release-approval prerequisites.

### Personal notification categories, consent swipes and skin defaults — local acceptance passed

All signed-in users can now configure their own per-app/category importance in an
Android-style expandable list; server-wide routes/layouts remain administrator-only.
Watchlisted media uses native `Likes` (Enhanced's watchlist convention), separately
from native Favorites and verified Requested media. New library notices retain
recipient-scoped interest groups and frozen retry IDs; all private membership
arrays are stripped/rejected at the shared Discord boundary. Critical severity,
current media access, device opt-in and blocked-history/no-replay safeguards remain.

The final pre-swipe root regression passes (including muted existing-row behavior):
`artifacts/notification-categories-verified-tests.log`. Inbox swipes/desktop drags
reveal Mark read on the right and Hide notification on the left, with a confirming
selection and Cancel. For eligible Deletion Queue consent requests only, a deliberate
right gesture submits the live projected Allow consent, then hides the notification
after success; current native revision/snapshot, permissions, approvals and delays
still apply. A preview explains the pending gesture. Failed consent does not hide;
failed hiding can be retried without reconsenting. Physical touch-device behavior
remains unverified. Focused bell regressions are recorded in
`artifacts/notification-consent-swipes-tests.log` (**55 passing cases**, including
duplicate-command/session-change guards). An additional 36 documentation/web
contract tests pass in `artifacts/notification-consent-contract-tests.log`.

Six fresh packages pass inventory/validation in
`artifacts/notification-categories/skin-packages`; packaging evidence is
`artifacts/notification-skin-package.log`. Final installed acceptance passes in
`artifacts/notification-skin-installed.log`: six-plugin authorization/audit wiring,
ordinary-user preference isolation, native watchlist/Favorites filtering and library
access checks, 18 responsive cases and 36 workspace/theme cases. Final browser results
in `artifacts/notification-categories/skin-installed/theme-results.json` additionally
record four category/keyboard/touch-target cases, four native notification palette
cases, and four dark/light × 320/1280px swipe/consent/skin cases. Touch/mouse pointer
events submit projected Allow before dismissal; mocked command endpoints prevent
real removal. Custom-skin foreground, accent, font inheritance and shape overrides
pass. Screenshots in that run's `admin-screenshots` were reviewed.

Earlier failures remain retained: autosave radio-selection flicker, a test-only
nested-details toggle, the ordinary-to-consent fixture refresh, and a real late-CSS
skin-shape cascade issue. Swipe defaults now use `:where()` scoping so ordinary skin
CSS wins without `!important`; accents also support Jellyfin's native palette.
These bounded checks are not certification of every third-party skin, physical
touch hardware, live removal or native Security recovery with this final package
set (the final installed run had no native Security).
The older passing package sets below do **not** certify these new changes. See
[notification categories](notification-channels.md) for user controls and defaults.

Test orchestration is consolidated in `scripts/run-tests.ps1`; the root and
selected-plugin commands are thin wrappers. Selected release gates retain their
plugin/dependency scope. Notifications now includes its Push-settings regressions.

Normal test/package commands automatically discover current stable Jellyfin and
Security, generate matching SDK/EF/SQLite properties, and propagate a dated run
snapshot. Installed Jellyfin, Seerr, Sonarr/Radarr and Enhanced fixtures resolve
current releases, refresh images and record concrete versions/digests. Neither a
manual version edit nor an opt-in update command is required. See [testing](testing.md).

Native database and Security compatibility remain fail-closed: automatic build
approval requires source-contract equivalence to the reviewed baselines, not just
a matching release number. Actual contract changes require investigation, not an
older-server fallback. Reset still requires the same browser's same-account
Security-verified/unblocked session, including native-approved bypasses.

## Latest-target verification

Current-run discovery selected Jellyfin **12.4**, Security **2.6.5** (verified
asset assembly **2.6.5.1**), Enhanced **12.14.0.0**, Seerr **3.5.1**, Sonarr
**4.0.20.3014** and Radarr **6.4.4.10685**. These are observations, not new pins;
the next independent run discovers current stable releases again.

- Consolidated root run passes **1,077 .NET / 274 JavaScript tests**, with one
  optional real-Arr case skipped: `artifacts/latest-auto-final-root-tests.log`.
  Twenty-one focused discovery/automation/isolation/documentation checks also pass,
  including the final action-digest and upstream-trigger regressions. These overlap the
  root checks and are not added to its count.
- Six fresh Linux amd64 packages pass validation/inventory:
  `artifacts/latest-auto-final-packages`, `artifacts/latest-auto-final-package.log`.
- Final-package native Security, same-session reset, factor/trust/recovery-code,
  permissions, audit routing, library, onboarding, restart and seven-database
  safety acceptance passes: `artifacts/latest-auto-final-installed-security.log`.
- Real latest Seerr API acceptance passes (`artifacts/latest-auto-seerr-final.log`).
  Owned latest Sonarr/Radarr provider acceptance passes, including the real-Arr
  test skipped by the offline root run (`artifacts/latest-auto-arr-verified.log`).
- Final-package latest Jellyfin/Enhanced browser acceptance passes: 36 workspace,
  18 responsive and four notification-palette cases, with installed permissions
  and audit routing (`artifacts/latest-auto-final-installed-browser.log`). Earlier
  pre-correction package evidence remains separately scoped.
- Actionlint, local Renovate configuration validation, link checks and diff
  whitespace checks pass. The Renovate validator reported local Node/optional-RE2
  warnings; no dependency bot, GitHub merge or publication was executed.

The current 128-case matrix, full native SyncPlay consumer/upgrade acceptance,
Windows checks and three-platform suite release gates have **not** all been rerun
locally. They remain mandatory in dependency automation before promotion/release.
Do not reuse historical passes to close them. The initial Security check correctly
rejected a filename/source-vs-binary revision mismatch; bootstrap now derives the
exact version from checksum-verified PE metadata, without executing the assembly.

Dependency maintenance workflows now group stable updates, require all automated
gates, generate itemized direct/transitive dependency commit messages, then merge
the tested head and dispatch an immutable patch release. This is local
configuration only; bot credentials, strict branch protection and existing release
approvals must be configured as documented in [dependency maintenance](dependency-maintenance.md).

## Preserved historical evidence

The six-plugin Jellyfin **12.3 / Linux amd64** wiring audit passed 1,075 .NET and
259 JavaScript tests, all 128 subsets with/without then-targeted Security, native
session recovery, restart/storage checks, 36 workspace + 18 responsive cases and
four notification-palette cases. One optional real-Arr test was skipped.

- [Consolidated evidence register, historical milestones and failed-attempt limits](verification.md)
- [Current safety and permission boundaries](safety-and-permissions.md)

These results certify only their recorded software/package set, not latest
upstream releases, arbitrary package skew or the owner's live server.

## Remaining product and live-service limits

- Embedded Discord video/OAuth/admission/media transport remains planned; invitations
  stay in Hub and a common connection registry is not a requirement.
- Remaining latest-source/package matrix, native consumer and platform acceptance
  must pass before full current-release compatibility is claimed.
- Human delegated `/invite`, multiplayer hosting, physical-device Push and
  the complete owner plugin/skin set remain uncertified. Seerr-authoritative deletion
  is the accepted boundary; no independent physical-file-removal gate is imposed.
  Cross-publishing is not native Windows/physical ARM testing; those platform jobs
  belong on GitHub rather than blocking this local Linux testing phase.
- Deletion Queue retains consent, approval, delay, history and destructive Seerr
  authority; automation submits only. Arr metadata reads remain GET-only.
- No `.env`, native identities, credentials, keys, history, consent or generated
  historical evidence is removed by this cleanup. Live human/Discord checks need
  explicit authorized destinations and readiness.

See [release gates](v1-release-gates.md), [dependencies](dependencies.md) and
[product requirements](product-requirements.md). No commit, push, tag or release
is authorized merely by passing local tests. The requested dependency workflow
is separately scoped recurring automation; it does not publish unrelated local
working-tree changes outside its protected process.
