# Changelog

Published notes are maintained in [GitHub releases](https://github.com/someRandomDude-a/jellyfin-plugins/releases).

## [Unreleased]

- Verify concrete media identifiers instead of treating saved connection revisions
  as persistent identity; preserve matching consent/approval fingerprints.
- Explain administrator blockers and media-identifier mismatches.
- Show immediate loading states, concise context actions and compact timelines.
- Default administrators to Active when no approval decision is pending.
- Replace Requests polling with explicit, reopen and change-event refreshes.
- Present backlog, journal, coverage and diagnostics in themed tables.

Deletion remains opt-in, consent-bound and freshly verified. Uncertain dispatches
are never resent automatically. See [safety limits](../../docs/safety-and-permissions.md).
