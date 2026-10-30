# Third-party references

Jellyfin Security: https://github.com/ZL154/JellyfinSecurity, MIT, source tag v2.6.4 / commit `2c02686fcec8b15ea56235de8d196460711befaa`.

This adapter independently implements HTTP consumption and event projections. Synthetic source-shaped endpoint/event fixtures are based on the tagged controller, blocker and NotificationService. No Security implementation assembly, private store code, factor implementation or secrets are bundled.

Jellyfin.Common is a compile-time server SDK dependency (runtime assets excluded); Jellyfin is GPL-2.0-or-later. No Jellyfin server implementation source is copied here. Host packaging must retain applicable dependency notices and review its full artifact dependency/license graph. Exact versions are recorded in the package inventory.

Tests use Microsoft.NET.Test.Sdk (MIT), xUnit.net (Apache-2.0), xUnit Visual Studio runner (Apache-2.0), and Moq (BSD-3-Clause), with their respective transitive licenses. Test dependencies must not be shipped as plugin runtime dependencies. The project source license remains the repository's existing MIT LICENSE.
