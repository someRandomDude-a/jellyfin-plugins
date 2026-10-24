# Native database boundary: Jellyfin 12.3

Reviewed 2026-10-25 after reproducing Hub's `remediation_pending` policy-apply result on the 12.3 installed fixture. `NativeUserWrites` and integrity checks share the onboarding database guard; the guard still allowed only 12.2, so the SDK target update inadvertently blocked all of them.

Compared the complete, non-truncated official Git trees for [v12.2](https://api.github.com/repos/jellyfin/jellyfin/git/trees/v12.2?recursive=1) and [v12.3](https://api.github.com/repos/jellyfin/jellyfin/git/trees/v12.3?recursive=1). All 270 blobs under `src/Jellyfin.Database/` plus matching `UserManager.cs`, `UserPolicy.cs`, `UserUpdatedEventArgs.cs` and `UserCreatedEventArgs.cs` paths have identical path sets and Git blob hashes across the two tags. This includes model mappings, SQLite migrations/providers, entity permission/preference mutators and user event contracts, not just the SDK's assembly version.

Key unchanged blobs:

| Path beneath `src/Jellyfin.Database/Jellyfin.Database.Implementations/` | Git blob |
| --- | --- |
| `Entities/User.cs` | `bf6568d10a9b6fb8981a1d62be593e8e19659077` |
| `Entities/Permission.cs` | `eae02dda1c46e9114ce5823439f0416a39593677` |
| `Entities/Preference.cs` | `9bd159f2cf09d5e072ad4ebd51c57bb3ed863032` |
| `JellyfinDbContext.cs` | `b0c12bf592c50c37d4602645142e7f6a509e4faf` |
| `ModelConfiguration/UserConfiguration.cs` | `ed4138680d963b4fdfcc384a6843b1d1edec3fad` |

The boundary now explicitly permits SQLite assembly versions `12.2.0.0` and `12.3.0.0`; other providers and unknown/patch/future versions still fail closed. Pending migrations still block writes, and tracked-only writes, immediate transactions, administrator protection and event publication are unchanged. This does not change native schemas, indexes or services, nor establish production/native-creation-race certification. Installed acceptance evidence is recorded separately in `docs/review-status.md`.
