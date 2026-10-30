# Release checklist

Use **Actions → Release plugins** with `all` or one plugin and an unused `vX.Y.Z`.
Ordinary source pushes and pull requests do not publish releases.

## Before publication

1. Review the exact source commit and [validation limits](review-status.md). Existing
   tags must resolve to that commit; never move a tag or replace published assets.
2. Pass current dependency/source-contract checks and bind tests and packages to
   the same target, toolchain and image snapshot.
3. Pass the selected plugin's Windows/Linux, installed and relevant external-provider
   gates. Suite releases also require the full subset matrix, native Security,
   browser, SyncPlay and external-service acceptance.
4. Validate all three runtime packages per plugin, or 18 packages for the suite:
   identities, ABI, inventory/licenses, archive safety, immutable URLs and checksums.
5. Verify clean installation, supported upgrade/migration and stopped backup/recovery
   behavior. Review native authorization, secret handling and uncertainty protections.
6. Retain required branch checks and **plugin-release** environment approvals.
   A workflow declaration does not configure repository reviewers by itself.

## Publication and catalogs

The protected publication job rechecks freshness, creates a draft, uploads validated
assets, then publishes with `--latest=true`. Failed uploads leave a draft, not a
successful release. Both suite and independent releases update GitHub's Latest marker.

The catalog job merges the three architecture feeds and regenerates `releases.json`
in the same non-forced catalog commit. README badges read that metadata automatically.
For a failed catalog update, use **Update plugin manifests** with the already
published tag. Catalog repair does not rebuild assets or change GitHub's Latest marker.

## Limits

- Windows/ARM cross-packaging is not installed-platform certification.
- Browser automation is not physical-device Push or human multiplayer acceptance.
- Human Discord fixtures and production operations require separate authorization.
- Never upload `.env`, credentials, local data stores or secret-bearing fixture files.

See [release procedures](independent-plugin-releases.md), [testing](testing.md),
[dependency maintenance](dependency-maintenance.md) and the [workflow map](../.github/README.md).
