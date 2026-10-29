# Discord Bridge dependencies

Plugin and API source: MIT. Jellyfin SDK: GPL-3.0-only, referenced but not redistributed.
Bundled Discord.Net libraries: MIT. Newtonsoft.Json: MIT.
SQLite, Microsoft.Data.Sqlite and ASP.NET Core are supplied by the supported Jellyfin server.
Discord.Net voice dependencies are not used: this plugin requests only the Guilds gateway intent and does not connect to voice or read message content.

Discord Activities and their JavaScript SDK distribution have been removed. No embedded Discord SDK assets or generated Activity inventory are shipped.
