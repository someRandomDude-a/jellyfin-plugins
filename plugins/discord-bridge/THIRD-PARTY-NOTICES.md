# Discord Bridge dependencies

Plugin and API source: MIT. Jellyfin SDK: GPL-3.0-only, referenced but not redistributed.
Bundled Discord.Net libraries: MIT. Newtonsoft.Json: MIT.
SQLite, Microsoft.Data.Sqlite and ASP.NET Core are supplied by the supported Jellyfin server.
Discord.Net voice dependencies are not used: this plugin requests only the Guilds gateway intent and does not connect to voice or read message content.

The embedded Activity companion bundles the official `@discord/embedded-app-sdk` JavaScript SDK and its used runtime dependencies. The build generates `Activity-THIRD-PARTY-NOTICES.txt` with the original license texts and `activity-inventory.json` with exact bundled package versions, license metadata and SHA-256 of the embedded JavaScript. esbuild and jsdom are build/test tools, not shipped runtime dependencies. This newly introduced JavaScript distribution needs owner review in addition to the previously completed license review.
