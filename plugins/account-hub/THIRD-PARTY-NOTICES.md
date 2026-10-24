# Dependency/license notes

Account Hub's original implementation and public API library are MIT licensed.
No source from Jellyfin Enhanced, File Transformation, Jellyfin Security,
DeletionQueue or Private SyncPlay has been copied into this repository.

Build/test dependencies retain their upstream licenses:

- Jellyfin SDK 12.2.0: GPL-3.0-only; server-owned assemblies are referenced, not redistributed in the preview ZIP.
- Microsoft .NET / ASP.NET Core / Entity Framework Core / Microsoft.Data.Sqlite: MIT; framework/EF/SQLite assemblies are server-owned and not bundled.
- SQLitePCLRaw: Apache-2.0; SQLite: public domain; native/runtime dependencies are supplied by Jellyfin, not bundled.
- xUnit: Apache-2.0; test runner/SDK and EF tooling are development-only.

The integration preview includes Account Hub Core, Lifecycle, Security and Notifications, their framework-only API libraries, and Dapper (Apache-2.0). Lifecycle source was relicensed to MIT by its owner. EF/SQLite remain server-owned dependencies. Runtime compatibility must be tested on a clean supported server before release.
