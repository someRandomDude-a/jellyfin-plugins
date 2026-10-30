# Validation status

Published versions and Jellyfin ABI are shown in the [live release table](../README.md#published-releases).
Publication does not certify later working-tree changes or every plugin, platform
and theme combination.

## Current source checks

Latest recorded local checks, 2026-10-30:

| Check | Result | Local evidence |
|---|---|---|
| Nine .NET test projects | 1,216 passed; one opt-in live Arr test skipped | `artifacts/ui-polish-backend-suite-r2.log` |
| Root scripts, tooling, docs and release metadata | 261 passed | `artifacts/docs-cleanup-scripts-r1.log` |
| Notification bell | 61 passed | `artifacts/ui-polish-bell-r3.log` |

Backend and bell results cover the media-identity/UI changes. The script run also
covers documentation links, per-plugin published metadata and Latest-marker
workflow checks. Logs are local artifacts, not installed-browser evidence.

## Remaining acceptance

- Fresh coherent packages and installed browser/theme checks for the current UI,
  including the enrolled-factor account-link return flow.
- Owner-approved human Discord signup-limit, DM privacy and party-redemption tests.
- Full owner-plugin/skin profile, installed Windows/physical ARM behavior,
  physical-device Push, native outbound Security webhooks and broader proxy/fault
  boundaries.

Run the [testing procedures](testing.md) and [release checklist](github-release-checklist.md)
against the exact candidate. Unit tests, cross-packaging and older fixture results
do not replace these gates. Raw logs and failed attempts remain in `artifacts/`;
obsolete development diaries are not maintained as current documentation.

No documentation authorizes production deletion, live fixtures, credential
disclosure, history/key replacement or publication.
