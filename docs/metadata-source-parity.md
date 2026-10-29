# Metadata sources and Maintainerr review

Research reference: [Maintainerr](https://github.com/Maintainerr/Maintainerr), MIT,
commit `3dbd5463a82beb4cd970a41c3afd042af4fccfd8` (the `main` head returned during
this review). Reviewed `rules.constants.ts`, the shared rule types, and the
Jellyfin, Seerr, Sonarr and Radarr getters. No upstream implementation or assets
were copied. Maintainerr remains credited for the workflow and condition research.

## Implemented in this iteration

### Typed condition calculations and membership (2026-10-27)

Conditions now have an explicit **Metadata conversion** control. Supported
conversions are date age → fractional days/hours, first date minus second date →
signed fractional days/hours, date → Unix seconds, list → count, strict numeric
text → number, and Boolean → number (true = 1, false = 0). Date differences fetch
and validate both metadata sources. UTC instants are used; future ages and reversed
date differences remain negative. No implicit string coercion, absolute-value
conversion, truncation or unknown-to-zero fallback is applied. Numeric conversion
results compare with a numeric threshold; this is not an arbitrary expression
language or a claim of every Maintainerr conversion/composition.

List membership can compare against another metadata field, not just a constant.
User-ID lists accept user metadata; text lists accept text metadata. For example:

1. Select **All episodes watched by (Jellyfin IDs)** (`jellyfin.allEpisodesSeenBy`).
2. Choose **contains** → **Compare with metadata**.
3. Select **Requester Jellyfin user ID** (`seerr.requesterId`).

This tests whether the uniquely mapped requester finished every episode. It does
not mean every server user finished the show. Missing/ambiguous requester evidence
and malformed/missing viewer evidence stay unknown, including under NOT/OR.
GUID membership compares identities rather than textual GUID formatting. These
facts still use native aggregates, not an invented exhaustive watch history.

Field and second-operand selectors create/reuse the proper source cards and wire
them automatically. Existing sockets still work; operand-two wires now anchor to
the correct field. Metadata outputs have search and bounded internal scrolling.
The canvas adds Arrange blocks, centred Fit canvas and pointer-anchored zoom;
first-use framing does not replace a saved pan. Selecting a block restores a
readable inspection scale. Canvas height and card-creation dialog dimensions are
viewport-bounded; long metadata lists scroll internally rather than stretching
the whole canvas. Result cards say **REQUEST REMOVAL**: consent, administrator
approval, fresh playback/history checks and delays remain Deletion Queue's job.

- A source/type/operator catalog (`MetadataCatalog.cs`) drives both server
  validation and the browser's metadata sockets/condition controls. Numeric,
  Boolean, text, native-user, date and text-list values have different operators.
  List membership, partial membership, any/all/none and count comparisons are
  supported. Date comparisons include absolute before/after/equality and relative
  day/hour windows. Relative units are part of the operator; file sizes use MiB
  and runtimes use minutes. Missing evidence never becomes a positive match via
  NOT or a negative comparator.
- Version-2 circuits connect a source card's typed output to a condition, then
  Boolean conditions/gates to the result. There is no Start/execution wire.
  Version 1 remains readable; editing upgrades it while retaining condition IDs,
  rule IDs and submission receipts. Metadata node/source identity and wiring are
  validated, including imported/custom circuits. A dangling required socket is
  an invalid draft, never implicit consent or a true value.
- Library inputs can be connected/disconnected from rule mode; a library card
  opens its filter editor, and **Test path** separately selects its draft test
  scope. Default library cards use native roots, not an administrator-selected
  global library. AND/OR gates fit their connected inputs, retain two required
  slots and have a separate add-input affordance; the existing 128-input/node
  safety bounds remain. Gate fills/source cards have skin-overridable colours.
- Jellyfin scalar metadata: title/year/kind/added/audience rating, genres, tags,
  studios, content/critic rating, release/end dates, original title, production
  locations and runtime. A separate native provider supplies active-user watched
  and favourite IDs, play count/latest played time and whole-series episode
  counts, watched-episode counts, all-episode watchers, episode watchers and latest
  episode added/aired dates. These are native user-state aggregates, **not an
  invented complete per-play history**. Scans have user/episode/work/time bounds;
  removed series parents are rejected before descendant queries.
- Seerr metadata: edition, legacy authorizer-relative ownership, verified
  requester ID/list, request count/presence/first-request date, media-added date
  and availability status. Unique requester attribution is unknown for mixed
  owners; a verified requester list can still represent several owners. Failed
   mappings stay unknown. Automatic queue attribution now uses a uniquely verified
   native requester while retaining the independent automation authorizer; it
   grants no consent and never bypasses native access checks.
- Radarr metadata: title/service ID, added/path/monitored/tags/profile, size,
  language/status/runtime, file added/path/resolution/quality/cutoff/audio,
  physical/digital/cinema dates and IMDb/Trakt/Rotten Tomatoes ratings/votes.
- Sonarr whole-series metadata: title/service ID, added/path/monitored/tags/profile,
  size/language/status/runtime/type, ended/first-air date, season/monitored-season
  and episode/file/missing/unaired counts, rating/votes.
- Sonarr/Radarr configuration is administrator-only, revision checked and stored
  with protected keys. Enhanced import reads public installed-plugin configuration,
  accepts exactly one enabled instance and verifies its application before saving.
  Credentials never appear in GET/status responses. Retained keys cannot be sent
  to a changed endpoint, redirects are refused, and HTTP/body budgets apply.
  Provider IDs must identify one exact source record. All requests are GETs;
  there is no Arr destructive operation or filesystem cleanup.

## Not complete — do not claim full parity

### Added in the Web Push / parity expansion

- Native actors/directors/writers/people, provider IDs, manual collection/playlist
  names and IDs, direct manual-collection sibling watch counts/watchers/latest
  play, all-enabled-user completion/unwatched flags, partial/resume state, and
  bounded whole-target video/audio/subtitle codec/language/range/dimension data.
  Stream lists on series aggregate stored episode streams; they are not a claim
  that every possible version was probed. Membership is explicitly **manual**;
  no dynamic/smart membership is guessed from an empty manual-link result.
- Maintainerr-style **Jellyfin summaries**: latest timestamp on currently watched
  items versus latest playback including unfinished items; completed native
  user/item record count (not total historical completions); users whose native
  watched timestamp is since added; all episodes watched since added; timestamp
  of the highest-numbered watched episode. These use `Played` and
  `LastPlayedDate`, not invented per-play events. Missing dates on watched entries
  make date-dependent summaries unknown. As the upstream Jellyfin adapter does,
  this normalizes per-user state rather than claiming an exhaustive event log.
  The user explicitly chose **native aggregates only**; no separate history
  recorder or retroactive reconstruction was added.
- Sonarr latest **regular** season number, aired/file/missing counts, last/next
  episode dates, explicitly flagged aired season/series finales, episode-file
  qualities/codecs/audio languages, all-file cutoff status and latest file-added
  date. Specials are excluded from latest-regular-season calculations. Date,
  file and series identity omissions remain unknown, and no date authorizes
  removal. The destructive target remains the entire verified series.
- `exists` / `not-exists` now distinguish explicit provider-confirmed absence
  from missing keys, legacy nulls, failed reads and ambiguous matches. Existing
  null/owner evidence is **not** reinterpreted as confirmed absence. Empty but
  successfully read lists are present; use count comparisons to test emptiness.
- Compatible typed/unit-matched cross-source value comparisons now have a second
  metadata input on condition cards. Both input sources are validated, fetched
  and required; unknown either side blocks OR/NOT and live execution as before.
- Real API acceptance: disposable Linux amd64 Sonarr **4.0.20.3014** and Radarr
  **6.4.4.10685**, with synthetic records seeded only into stopped test containers
  because native create APIs require external catalogue lookups. Plugin providers
  exercised actual GET endpoints, native provider-ID matching, profile lookup,
  latest-season/missing/finale aggregates and confirmed unmatched-record absence.
  Evidence: `artifacts/metadata-real-arr-tests.log`; reproducible runner:
  `scripts/metadata-arr-acceptance.mjs`. No production mounts/keys or Arr writes
  from the plugins were used.

The catalog intentionally does not advertise getters that are not implemented.
Maintainerr includes functionality beyond this iteration, notably:

- Exhaustive per-play/completion-event history cannot be reconstructed from native
  aggregate user state and is excluded by the user's native-aggregates-only
  decision. The implemented watched-since-added summaries above are not claimed
  to reconstruct every past watch. Parent/smart
  collection/playlist variants and per-provider/parent-show ratings still need
  their own verified semantics; the implemented direct/manual sibling metadata
  above is not substituted for complete history or smart membership.
- Seerr release-date projection for seasons/episodes and precise approval-time
  semantics; no inferred timestamp is presented as an authoritative approval date.
- Arbitrarily selected Sonarr episode/season scopes and file ranks remain
  unsupported. Latest-regular-season and whole-series file projections above
  have explicit semantics; they are not aliases for a user-selected child scope.
- Every upstream client-side setting variation. Plex/Tautulli/Ombi/Emby and other
  unrelated sources are not included.

The new modular providers/catalog are an implementation foundation, not a claim
that the entire Maintainerr catalog or combination matrix has been completed.
Disposable real Sonarr/Radarr API coverage is listed above; production servers and
real configured Seerr request/deletion paths are not certified by those fixtures.
Installed/package evidence is tracked separately in `review-status.md`.
