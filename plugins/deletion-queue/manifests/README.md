# Component catalog fixtures

## Installation and release ownership

This directory supports retained component catalog/publisher tests. It is not the
current monorepo's installation feed or release procedure. Use the root
[combined architecture catalogs](../../../README.md#installation-and-upgrades) and
[independent/suite release engines](../../../docs/independent-plugin-releases.md).
Do not register a GitHub HTML page or runtime ZIP as a Jellyfin repository URL.

## Preserved catalog invariants

Choose only the server's architecture; `targetAbi` does not select architecture or
prove compatibility with arbitrary later servers. Preserve immutable tag-specific
URLs, version history, MD5 checksums, ABI, changelog and UTC timestamps. MD5 is a
Jellyfin compatibility check, not a signature; trust only approved HTTPS repositories
and verify available SHA-256/provenance evidence. Publication never authorizes
automatic deletion, resets consent/history or changes native identities.

## References

- [Current installation](../README.md) · [Release checklist](../../../docs/github-release-checklist.md)
- [Storage recovery](../../../docs/plugin-storage-recovery.md)
