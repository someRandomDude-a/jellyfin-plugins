# Automatic Deletion Requests — implementation preview

## Dependencies and installation

Install Automatic Deletion Requests, Deletion Queue and Notifications through the
[combined platform catalog](../../README.md#installation-and-upgrades), then restart
Jellyfin. Configure **Dashboard → Plugins → Automatic Deletion Requests** with a
native administrator session. New submissions require a ready Queue boundary,
Notifications and an authorized automation account. Seerr evidence uses Queue's
existing connection; optional Arr sources are GET-only and use their protected settings.

## Overview

Independent plugin, disabled by default. Rules evaluate a bounded Jellyfin library scope and submit ordinary requests to Deletion Queue. They do not manage media, collections, artwork, consent, administrator approval or deletion. Those responsibilities belong exclusively to Deletion Queue for both manual and automated requests.

## Implemented subset

- Native Dashboard configuration with library pickers and a node canvas. The server binds the automation authorizer. Create a **Rule** or **Library filter** card with a title/description; select it to edit. Type stays fixed.
- Library-filter cards own condition circuits and output connections to rule cards. Multiple filters may feed one rule; overlapping items run once per rule. Rule mode allows connecting/disconnecting library inputs. Click a library card to edit it, or **Test path** to find eligible media. Default cards are seeded per current native library without a global library selector.
- Typed conditions, expandable AND/OR, NOT, draggable nodes/wires, zoom/pan/fit, reusable circuits and JSON import/export. True/false/unknown signals are distinct; unknown evidence blocks matching, including under NOT. The terminal requests removal through Deletion Queue, not direct deletion. **Save automation** persists valid circuits and layouts; incomplete circuits are not executable.
- Jellyfin title, release year, date added, community rating and movie/series kind conditions.
- Version-2 circuits use expandable, source-specific metadata cards and typed outputs into conditions; no execution Start is needed in rules or filters. Version-1 circuits remain readable and migrate when edited; IDs and submission receipts are retained. AND/OR gates resize to their input count (minimum two, bounded to 128 for safety). Empty required sockets are invalid drafts, not implicit true values.
- Seerr requester IDs/counts/dates/edition/availability are read through Deletion Queue's saved connection. The internal administrator authorizes automation; it is not requester metadata. Missing or mixed singular requesters stay unknown. The legacy `seerr.ownRequest` field keeps its old authorizer-relative meaning.
- Sonarr/Radarr can be configured as **read-only metadata** sources or imported and verified from exactly one enabled Jellyfin Enhanced instance per source. Keys are protected in plugin-owned storage, never returned in settings responses, and never reused at a changed endpoint without a new key. HTTP reads are bounded and do not follow redirects; provider IDs must map to one exact item. No Arr deletion, unmonitoring or filesystem operations are implemented. File paths are metadata strings only.
- Missing/unavailable evidence never matches, including OR groups and negative comparisons. Unsupported fields/operators are rejected. No executable expressions.
- Live native administrator sessions only for settings/preview; explicit confirmation for enabling automation. The configured automation authorizer must remain an enabled native administrator with Queue authority. A singular verified requester remains the request owner and must satisfy their own current access/removal authority; unknown ownership is not invented or relabeled as the administrator.
- Immediate submission; repeat matches do not renew existing requests or imply votes. Administrator automation never grants viewer consent or approval. Cancelled/denied requests are not automatically recreated.
- Revisioned atomic configuration writes, persisted submission receipts, bounded runs (200 items/20 submissions), protected against concurrent runs and mid-run configuration changes. Oversized scopes fail before submission; they are not silently truncated. Storage corruption fails closed rather than resetting/enabling rules.
- Live originating-rule verification before queue progression, claiming and deletion. The same rule must still be enabled, the configured administrator/library scope must still agree, and current Jellyfin/Seerr evidence must match. Missing/uninstalled automation blocks its requests; it does not block independent manual requests. Failing a rule check clears eligibility and requires a new safety delay once checks pass again.

## Daily scheduled tasks

In **Dashboard → Scheduled Tasks → Deletion Queue**, manage the daily triggers or use **Run now**:

| Job | Default local time | Responsibility |
|---|---|---|
| Process automatic removal rules and collections | 02:00 | Evaluate enabled rules, submit ordinary requests, ask Deletion Queue to refresh collections |
| Verify removal conditions and process due deletions | 03:00 | Deletion Queue freshly verifies every gate, advances eligible stages, processes due requests only with destructive opt-in, refreshes collections |
| Generate and restore removal artwork overlays | 04:00 | Deletion Queue generates/restores native artwork, preserving originals and administrator edits |

Jobs are independent: changing their order never skips safety checks. A past deletion date is not authorization; current activity, target, permissions, originating rules, consent, optional administrator approval and policy are rechecked. Missing evidence stops progress. Playback collection stays continuous so new viewing can block a request between daily jobs. Startup recovers uncertain claims but does not execute deletions. The old `IntervalHours` configuration field remains readable for compatibility; native Scheduled Tasks triggers now own scheduling.

## Verification

```powershell
pwsh -File scripts/test.ps1 -Plugin automatic-deletion-requests
```

Use `--plugins=automatic` for disposable installed checks. Current source evidence
and remaining installed/browser acceptance are tracked in [validation status](../../docs/review-status.md).
Published versions are shown in the [release table](../../README.md#published-releases).

## Import and limits

**JSON import/export** detects supported Maintainerr rule groups and community
exports. Imports stay disabled until reviewed; unsupported metadata, actions and
scopes reject the whole import without dropping conditions. See
[supported formats and boundaries](../../docs/metadata-source-parity.md#maintainerr-json-import-2026-10-30).

Deletion Queue owns opt-in collection selection/creation, reversible native-image overlays/backups/restoration and portrait/landscape editing. These are presentation features, not permission to remove media. The batch preview uses saved rules; per-media circuit testing uses draft conditions and filters. Test searches inspect at most 50 candidates and can be refined by title (or item ID for filtered input tests); unavailable external facts never enter an eligible test path. Circuit limits are 128 nodes, 256 expanded nodes and four nested custom circuits; rules/filters are bounded to 50 each. Full pagination, real Seerr path testing and richer fields remain follow-ups. Node-editor installed/browser acceptance is tracked separately in `docs/review-status.md`; earlier acceptance does not certify these later changes. The subset does not promise Maintainerr parity. Do not downgrade automation while enabled: older plugin versions do not understand circuit/filter semantics.

## Credits

Inspired by [Maintainerr](https://github.com/Maintainerr/Maintainerr), copyright © 2026 Maintainerr, MIT-licensed. Upstream feature research was pinned to commit `3dbd5463a82beb4cd970a41c3afd042af4fccfd8`. This implementation is independent C# code; upstream application code/assets have not been copied or bundled.

## References

- [Deletion Queue and destructive opt-in](../deletion-queue/README.md)
- [Metadata fields and provider contracts](../../docs/metadata-source-parity.md)
- [Dependency matrix](../../docs/dependencies.md) · [Testing](../../docs/testing.md)
- [Current verification and limits](../../docs/review-status.md)
