# Disposable Discord test credentials

Fill in the repository-root `.env` locally. It is ignored by Git; `.env.example` contains only placeholders. Do not send credentials in chat, commit them, or use a production bot/guild.

| Variable | Purpose |
|---|---|
| `DISCORD_TEST_BOT_TOKEN` | Secret bot token from Developer Portal → Application → Bot. Not an OAuth client secret, public key, webhook URL, or personal Discord token. |
| `DISCORD_TEST_GUILD_ID` | Dedicated test Discord server ID. |
| `DISCORD_TEST_CHANNEL_ID` | Dedicated text channel where test messages may be posted. |
| `DISCORD_TEST_RECIPIENT_ID` | Optional consenting test user's ID for DM checks. The user must share the guild and allow bot DMs. Never supply their password or user token. |
| `DISCORD_LIVE_TESTS` | Explicit permission to run live checks when set to `true`. Defaults to `false`. |

Enable Discord Developer Mode to copy IDs. Invite the bot to the test guild with `bot` and `applications.commands` scopes. Grant only View Channel, Send Messages, Embed Links (for rich cards), and Read Message History in the test channel (history is needed to verify delivery). Do not grant Administrator or privileged member/message-content intents. A client secret or separate application ID is not needed for the current bot runtime.

## GitHub Actions policy

Before adding a live-test job, create a GitHub Environment named `discord-integration` with required reviewers and a trusted-branch deployment restriction. Store `DISCORD_TEST_BOT_TOKEN` as an **environment secret**. Store the three destination IDs as environment variables with the same names (they are identifiers, not authentication secrets).

The live job must be manually opted into, reference that environment, and run only trusted code after approval. It must not run on pull requests, fork code, or `pull_request_target`, nor pass credentials to reusable/untrusted scripts. Inject the token through step-level `env`, never command-line arguments or workflow expressions embedded in shell code. GitHub's automatic masking is defense in depth, not permission to print HTTP bodies, configuration, environment dumps, or transformed tokens.

Keep live tests separate from the ordinary secret-free suite. Never upload `.env`, disposable Jellyfin configuration/databases/key rings, raw Discord HTTP/gateway traces, or unsanitized server logs. Test credentials can exist inside the disposable server's encrypted credential store; remove its container and credential-bearing temporary directories after live tests, including failure paths. Uploaded results must be an explicit allowlist of sanitized assertions. Never echo tokens into generated files or job outputs. Rotate the bot token immediately if exposed.

## Current status

Use `--load-test-env` to load the ignored test `.env`. The historical bare `--env-file` fixture alias collides with Node's own startup flag if followed by more arguments; use the new spelling for all interactive/tunnel commands. Historical commands below document earlier runs, not the recommended current invocation.

No pre-existing HTTPS endpoint is required when using the new explicit `--discord-tunnel` option alongside `--interactive-discord`. It creates a temporary, accountless Cloudflare Quick Tunnel for the newly owned disposable fixture, configures the BaseUrl in memory, verifies public fixture identity over trusted HTTPS and tears it down. Leave `DISCORD_TEST_PUBLIC_BASE_URL` empty in that mode. See [discord-activity-development.md](discord-activity-development.md) for the command, exposure/cleanup limits and the distinction between existing slash commands and a real embedded Activity.

Interactive acceptance now requires **both** an invitation DM to `DISCORD_TEST_RECIPIENT_ID` and an invitation to the configured approved channel (`confirm-share:true`), not either path alone. Select only these disposable destinations. Cleanup is best effort and restricted to messages from this bot, created during the fixture, matching an invitation/recovery prefix and this fixture's public URL boundary. If history reads or deletion fail, inspect those two destinations manually; no message bodies are logged. Aggregate counters do not establish recipient correctness, visual content/privacy, redemption, `/reset` or `/unlink` acceptance: those still require separate human verification. The HTTPS endpoint must forward to the temporary endpoint printed by the fixture; never point it at production.

`--interactive-discord` adds an opt-in, ten-minute human-driven acceptance window to the existing local live fixture. Include `--plugins=hub,discord,syncplay --library --live-discord --env-file`. The dedicated guild user must invoke `/help`, `/status`, `/link` and complete native verified linking, then `/watchparty devices` and an explicitly targeted `/watchparty invite`. A reachable disposable HTTPS/public base URL, active native Jellyfin session and allowed destination are required. The fixture does not forge Discord interactions, automate a personal Discord account, or accept personal user tokens. Real commands must be executed using Discord's supported client. Command-handler health counters are process-local, administrator-only, fixed allowlisted aggregates; no requester IDs, recipients, bearer links or response text are retained. Gateway/registration alone does not pass this acceptance step. This window has not yet been claimed as passed.

Ordinary automated tests remain secret-free. For explicit local live checks, run `node scripts/integration.mjs --packages=<linux-x64-packages> --plugins=hub,discord --library --live-discord --env-file` with `DISCORD_LIVE_TESTS=true`. Only this flag loads `.env`; credentials are never command-line arguments. Live mode forbids `--keep`, withholds server logs/error bodies and removes its credential-bearing fixture directory. Generated channel messages are deleted after successful verification; interrupted runs can leave messages for manual inspection. Command registration updates guild commands, so use a dedicated bot/guild.

Live checks are local pre-push checks for now; no live Discord GitHub Actions job is configured or required. For future CI use, inject `DISCORD_TEST_BOT_TOKEN` from an environment secret and destination IDs from environment variables as described above—never upload `.env` as a secret file. The current live fixture verifies gateway connection, command registration and installed-plugin channel announcements, not interactive slash-command execution or personal DM delivery. The optional recipient ID is reserved for that additional coverage. No GitHub secrets or environments have been created or published by this change.
