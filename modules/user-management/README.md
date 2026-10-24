# Jellyfin User Manager

[![CI](https://github.com/someRandomDude-a/jellyfin-user-manager/actions/workflows/ci.yml/badge.svg)](https://github.com/someRandomDude-a/jellyfin-user-manager/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/someRandomDude-a/jellyfin-user-manager)](https://github.com/someRandomDude-a/jellyfin-user-manager/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A Jellyfin 12 plugin that brings user provisioning, permission presets, invite links, Discord self-service and an audit trail to Jellyfin and [Seerr](https://github.com/seerr-team/seerr).

Jellyfin stays the identity provider. New users choose their own username and password through an invite link, and are sent to the normal Jellyfin login page afterwards. The plugin never creates placeholder passwords and never signs users in automatically.

## Features

- **Unified user management** for Jellyfin and Seerr.
- **Permission presets** combining a Jellyfin user policy with a Seerr permission bitmask.
- **Import a preset from an existing user** and reuse their Jellyfin and Seerr permissions.
- **Invite links** with configurable uses, expiry, permission preset and an internal note.
- **Invite monitoring** showing status, use count, creation time and who used each link.
- **Public invite requests** (opt-in) that create an audit entry and notify admins.
- **Discord account linking** using short-lived one-time codes.
- **Discord password resets** with one-time, 30-minute links delivered by direct message.
- **Discord slash commands** for linking, resets, invites, presets, user lookup, audit entries and Seerr requests.
- **Admin notifications** through a Discord webhook and direct messages to linked Jellyfin administrators.
- **Audit log** with filtering, CSV export and a per-user activity view.
- **Native Jellyfin administration page** plus an account page for every user.
- **Optional web-client integration** through [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation): adds an Account menu to Jellyfin and reset/invite links to the login page.

## Compatibility

| Component | Tested / target version |
| --- | --- |
| Jellyfin | 12.2 (target ABI `12.2.0.0`) |
| .NET | 10 |
| Seerr | 3.5.1 (3.5.x API) |
| File Transformation | 3.0.2 (optional) |
| Discord.Net | 3.20.2 (bundled) |

This plugin targets Jellyfin 12. It will not load on Jellyfin 10.x.

## Installation

### From the plugin repository

1. In Jellyfin, open **Dashboard → Plugins → Repositories**.
2. Add a repository named `User Manager` with this URL:

   ```text
   https://raw.githubusercontent.com/someRandomDude-a/jellyfin-user-manager/main/manifest.json
   ```

3. Open the **Catalog**, install **User Manager**, and restart Jellyfin.
4. If you want the injected web-client account menu and login links, install **File Transformation** from the Jellyfin catalog as well.

> The repository manifest is updated by the release workflow. If the catalog shows an older version, use the manual installation below.

### Manual installation

Download `Jellyfin.Plugin.UserManager_<version>.zip` from the [latest release](https://github.com/someRandomDude-a/jellyfin-user-manager/releases), then extract its contents into a subdirectory of your Jellyfin plugin directory:

```text
<Jellyfin config>/plugins/User Manager_<version>/
  meta.json
  Jellyfin.Plugin.UserManager.dll
  Dapper.dll
  Discord.Net.*.dll
  Newtonsoft.Json.dll
```

Do not create an extra nested folder inside that directory. Restart Jellyfin after copying the files.

Common plugin locations:

- Linux: `/var/lib/jellyfin/plugins/` or `/config/plugins/` in Docker.
- Windows: `%LOCALAPPDATA%\jellyfin\plugins\` or the `plugins` folder next to the server.
- Docker: `/config/plugins/`.

### File Transformation integration

File Transformation is optional. It injects a small script into Jellyfin's `index.html`, which adds:

- an **Account** button on the Jellyfin web client;
- **Forgot password? Reset with Discord** on the login page;
- a **Request an invite** link when public invite requests are enabled.

Without File Transformation, everything still works through these direct URLs:

- Account page: `/UserManager/account`
- Password reset: `/UserManager/reset`
- Invite request page: `/UserManager/invite-request`
- Administration page: `/web/ConfigurationPage?name=usermanager`

Check **User Manager → Settings → Status** after startup. If File Transformation is missing or incompatible, the status card shows the reason.

## First-time setup

1. Sign in to Jellyfin as an administrator.
2. Open **Dashboard → Plugins → User Manager**, or navigate to `/web/ConfigurationPage?name=usermanager`.
3. Set **Public base URL** to the address users open in their browser, for example `https://jellyfin.example.com` or `https://example.com/jellyfin` when using a reverse-proxy subpath. This value is used for invite and reset links.
4. Configure Seerr, Discord, and the notification options described below.
5. Review **Permissions** and create at least one default preset.
6. Use **Invites** to create a test link, or create a user directly from **Users**.

### Public base URL and reverse proxies

Use the externally reachable origin and any path prefix:

```text
https://media.example.com
https://example.com/jellyfin
```

If the URL is blank, the plugin uses the URL of the current request. That is convenient for local testing but can produce wrong links behind a proxy or when the admin page is opened from a different address. Set the public URL in production.

### Seerr

1. Complete Seerr's own setup and connect it to Jellyfin first.
2. In Seerr, open **Settings → General** and copy the API key.
3. In User Manager, set:
   - **Seerr URL** — the URL reachable *from the Jellyfin server*. With the included Compose stack that is `http://seerr:5055`. For a separate server use the LAN or public URL.
   - **API key** — the key from Seerr.
4. Click **Test connection**.
5. Enable **Sync new users to Seerr** and **Sync Seerr when a preset is applied** as desired.

The plugin imports a Jellyfin user into Seerr if needed, then applies the preset's Seerr permission bitmask. If Seerr is down, Jellyfin account creation still succeeds and the UI reports a Seerr warning.

### Discord bot

1. Create an application at <https://discord.com/developers/applications>.
2. Open **Bot**, reset/copy the bot token, and enable the **Message Content** intent only if you plan to add message-based commands later. The bundled slash commands do not require privileged intents.
3. Invite the bot to your server with the `bot` and `applications.commands` scopes. The minimum useful permission is **Send Messages** (including direct messages).
4. In Discord, enable **User Settings → Privacy & Safety → Direct Messages** for server members if you want password reset DMs to arrive.
5. In User Manager, set:
   - **Bot token** — the bot token. It is encrypted in the plugin database.
   - **Guild ID** — recommended. Slash commands are registered instantly in this guild. If blank, commands are registered globally and may take up to an hour to appear.
   - **Admin webhook URL** — optional. Create a webhook in a private admin channel and paste its URL.
   - **Admin role IDs** — optional comma-separated Discord role IDs whose members may use administrator slash commands. Linked Jellyfin administrators can always use them.
6. Save settings. The embedded bot reconnects automatically. Use **Reconnect bot** if needed.
7. Use **Send test notification** to verify the webhook.

The bot token is passed to Discord.Net and is not written to logs. Never commit it or share it.

### Permission presets

A preset contains:

- Jellyfin policy values such as libraries, playback, downloads, remote access and Live TV;
- a Seerr permission bitmask;
- an optional default flag.

Presets can be created from scratch or imported from an existing user. Importing reads that user's current Jellyfin policy and Seerr permissions. Invites and new accounts inherit the selected preset or the default preset.

Applying a preset preserves identity and safety fields such as the user's provider IDs, administrator flag, disabled state and invalid-login counters. Use the admin UI to change administrator or disabled state separately.

## Invites and public requests

### Create an invite

1. Open **User Manager → Invites**.
2. Choose a permission preset, number of uses and expiry.
3. Optionally add an internal note.
4. Click **Create invite** and copy the generated URL.

Defaults are one use and seven days, both configurable in **Settings**. Maximum values are 10,000 uses and 365 days.

### What the invited user sees

The invite page shows the preset, remaining uses and expiry. The user chooses their own username and password. On success the plugin:

1. creates a disabled/hidden Jellyfin account;
2. sets the user's own password;
3. applies the preset;
4. records the invite use;
5. optionally syncs the account to Seerr;
6. enables the account and shows a link to the normal Jellyfin login page.

The user is **not** signed in automatically. If account creation fails, the partially created Jellyfin account is deleted and the invite use is released.

### Public invite requests

Enable **Allow public invite requests** to expose `/UserManager/invite-request`. Submissions are rate-limited, written to the audit log and sent to admins through the configured webhook/DMs. The page does not create accounts by itself; an administrator still issues the invite.

### Invite security

- Only a hash of the invite token is stored.
- Invite URLs cannot be reconstructed from the admin list; copy the URL when it is created.
- Invite use reservation is transactional, so concurrent signups cannot exceed the use limit.
- A failed signup releases the reservation.
- Invites can be revoked or deleted at any time.

## Discord commands

| Command | Purpose |
| --- | --- |
| `/link <code>` | Link the Discord account to the Jellyfin account that generated the code. |
| `/unlink` | Remove the caller's Discord link. |
| `/whoami` | Show the linked Jellyfin account and Seerr permissions. |
| `/resetpassword [user]` | Send a 30-minute reset link to yourself, or to another user if you are an admin. |
| `/invite [preset] [uses] [days] [note]` | Create an invite link. Admin only. |
| `/preset [name] [username]` | List/inspect presets, or apply one to a user. Admin only. |
| `/user <username>` | Show Jellyfin and Seerr details. Admin only. |
| `/audit [count]` | Show recent audit entries. Admin only. |
| `/request <tmdb_id> [type]` | Submit a Seerr movie/TV request for the linked Jellyfin user. |

Account linking codes expire after 10 minutes. Reset links expire after 30 minutes and are single-use.

## Account page

`/UserManager/account` requires a Jellyfin session and lets users:

- see their Jellyfin and Seerr connection status;
- generate a Discord link code;
- unlink Discord;
- send themselves a password reset DM;
- change their password with their current password;
- view recent activity affecting their account.

Password resets always go through Discord. Users without a linked Discord account should contact an administrator or use the normal password change form with their current password.

## Audit log and notifications

The audit log records administrative actions, invite lifecycle events, Discord links, password changes/resets, settings changes, Seerr syncs and Seerr requests. Entries include timestamp, action, actor, target, details and IP address where available.

- Filter by action or user, and export the visible history as CSV.
- Users can see their own related entries on the account page.
- Admins can use `/audit` in Discord.

Admin notifications are sent through the configured Discord webhook and by DM to every linked Jellyfin administrator. Notification categories can be toggled independently for new users, invite use and password changes.

## Security and data

Plugin data is stored in Jellyfin's plugin data directory, normally:

```text
<Jellyfin config>/data/Jellyfin.Plugin.UserManager/
  usermanager.db
  usermanager.db-wal
  usermanager.db-shm
  secrets.key
```

- Jellyfin remains the only password store. The plugin never persists Jellyfin passwords.
- Seerr API keys, Discord tokens and webhook URLs are encrypted at rest with AES-GCM.
- The encryption key lives in `secrets.key`, separate from the database. Protect and back up both files together; losing the key makes stored secrets unreadable.
- Invite tokens, link codes and reset tokens are stored as hashes only.
- Reset and link tokens are single-use and time-limited.
- Rate limits cover public registration, reset requests, reset completions and invite requests. Limits are in memory and reset when Jellyfin restarts.
- Serve Jellyfin over HTTPS in production. The plugin does not replace Jellyfin's authentication or authorization.

Back up the database and key before upgrading or moving the server. To rotate a leaked secret, clear it in the admin UI and enter a new one.

## Docker integration sandbox

The repository includes a Compose stack for testing against the target versions.

```powershell
# Build and package the plugin first.
./scripts/package.ps1

docker compose up -d
```

Services:

| Service | URL | Notes |
| --- | --- | --- |
| Jellyfin | <http://localhost:8096> | Plugin mounted from `dist/plugin`. |
| Seerr | <http://localhost:5055> | Connect it to `http://jellyfin:8096` during setup. |

The Jellyfin plugin directory inside the container is `/config/plugins/Jellyfin.Plugin.UserManager`. Plugin data and Jellyfin/Seerr configuration use named Docker volumes, which avoids SQLite corruption from Windows bind mounts.

After changing plugin code, rebuild and restart Jellyfin:

```powershell
./scripts/package.ps1
docker compose restart jellyfin
```

Smoke checks:

```powershell
Invoke-RestMethod http://localhost:8096/UserManager/api/public/health
Invoke-RestMethod http://localhost:5055/api/v1/settings/public

docker compose ps
docker compose logs -f jellyfin
docker compose logs -f seerr
```

Then:

1. Complete the Jellyfin setup wizard and install File Transformation from the catalog.
2. Open **Dashboard → Plugins → User Manager**.
3. Complete Seerr's setup and connect it to `http://jellyfin:8096`.
4. Configure the public base URL as `http://localhost:8096`.
5. Create a preset, an invite, and a test user.

Stop the sandbox with `docker compose down`. Add `-v` only when you intend to delete both Jellyfin and Seerr data.

## Local development

Prerequisites:

- .NET 10 SDK
- PowerShell 7 (`pwsh`) for the packaging script
- Docker Desktop (optional, for the integration sandbox)

```powershell
dotnet restore Jellyfin.Plugin.UserManager.slnx
dotnet build Jellyfin.Plugin.UserManager.slnx -c Release --nologo
dotnet test Jellyfin.Plugin.UserManager.slnx -c Release --nologo
```

### Packaging

```powershell
./scripts/package.ps1 -Version 1.0.0.0
./scripts/package.ps1 -Version 1.0.0 -ReleaseTag v1.0.0
```

The script produces:

- `dist/plugin/` — extracted plugin directory with `meta.json` and runtime assemblies;
- `dist/Jellyfin.Plugin.UserManager_<version>.zip` — installable plugin archive;
- `dist/Jellyfin.Plugin.UserManager_<version>.zip.md5` — checksum;
- `dist/manifest.json` — manifest with the release URL and checksum;
- `manifest.json` — repository manifest, with the release version added/updated.

Only third-party runtime assemblies are packaged. Jellyfin, ASP.NET Core, Microsoft.* and System.* assemblies are resolved from the Jellyfin host.

### Tests

The xUnit suite covers token generation and hashing, secret encryption, password policy, rate limiting, Jellyfin policy round-trips, Seerr permission mapping, SQLite storage for presets/invites/reset tokens/link codes/audit entries/settings, and statistics.

```powershell
dotnet test Jellyfin.Plugin.UserManager.slnx
```

### Releases

1. Update the version in `Directory.Build.props` if needed.
2. Run the package script with the intended release tag so `manifest.json` points at that tag.
3. Commit the version and manifest changes.
4. Create and push a tag such as `v1.0.0` or `v1.0.0.0`.
5. The `Release` workflow builds, tests, packages, verifies the manifest URL, and creates a GitHub release with the zip, checksum and `manifest.json` attached.

The tag and packaged version may differ in component count (`v1.0.0` → `1.0.0.0`); the manifest uses the exact tag for the download URL.

## Configuration reference

| Setting | Default | Purpose |
| --- | --- | --- |
| Public base URL | request URL | External origin used in invite and reset links. |
| Seerr URL / API key | unset | Enables Seerr import, permission sync, requests and deletion. |
| Discord bot token | unset | Enables the embedded bot, linking, resets and DMs. |
| Discord guild ID | unset | Registers slash commands in one guild instead of globally. |
| Admin webhook URL | unset | Channel notification target. |
| Admin role IDs | unset | Extra Discord roles allowed to run admin slash commands. |
| Default invite expiry | 7 days | 1–365 days. |
| Default invite uses | 1 | 1–10,000. |
| Allow public invite requests | off | Exposes the request form and notifications. |
| Notify on join / reset / invite use | on | Admin notification categories. |
| Sync Seerr on create / preset apply | on | Keeps Seerr permissions aligned. |

## Troubleshooting

**The plugin page is missing or fails to load.**
Confirm Jellyfin is version 12.2+, the `meta.json` and DLLs are directly inside one plugin subdirectory, and restart the server. Check Jellyfin logs for assembly-loading errors.

**File Transformation is not registered.**
Install or update File Transformation from the catalog, restart Jellyfin, and check **Settings → Status**. Direct `/UserManager/...` URLs still work without it.

**Discord slash commands do not appear.**
Check the bot token and guild ID, ensure the bot was invited with `applications.commands`, and click **Reconnect bot**. Guild commands appear immediately; global commands can take up to an hour.

**Discord DMs do not arrive.**
The user must share a server with the bot and allow direct messages from server members. The bot cannot DM users who have DMs disabled.

**Seerr test fails.**
Use a URL reachable from the Jellyfin server, not from the browser. In the Compose stack, use `http://seerr:5055`, not `http://localhost:5055`. Confirm the API key and that Seerr completed its Jellyfin setup.

**Invite or reset links use the wrong host.**
Set **Public base URL** to the exact external URL, including any reverse-proxy path prefix.

**Lost the encryption key.**
Seerr/Discord secrets cannot be decrypted without `secrets.key`. Clear and re-enter them in settings. The Jellyfin user data itself is unaffected.

## License

MIT. Relicensed by the code owner on 2026-10-23. See [LICENSE](LICENSE).
