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

Use `--load-test-env` to load the ignored test `.env`. The historical bare `--env-file`
fixture alias collides with Node's own startup flag if followed by more arguments;
use the new spelling for all interactive/tunnel commands. Loading credentials requires
the explicit live flag and authorization for these disposable destinations.

No pre-existing HTTPS endpoint is required with `--discord-tunnel --interactive-discord`. It creates a temporary accountless Cloudflare Quick Tunnel only for the newly owned disposable fixture, verifies its public identity over trusted HTTPS and tears it down. Leave `DISCORD_TEST_PUBLIC_BASE_URL` empty in that mode. This exposes disposable fixture data to the tunnel provider/public endpoint; never use production data or credentials. Discord Activities and their OAuth-secret requirement are removed; existing root `.env` values are preserved, not printed or reused for embedded video.

Interactive acceptance requires **both** `/watchparty user:@user` for `DISCORD_TEST_RECIPIENT_ID` and `/watchparty` in the approved test channel. Select only these destinations. Cleanup is best effort, limited to this bot/time window/invitation prefix/fixture URL boundary. History/deletion failure requires manual inspection; bodies are not logged and sends are not replayed. Counters do not prove recipient correctness, privacy, redemption, reset or unlink. The HTTPS endpoint must forward only to the printed temporary fixture, never production.

`--interactive-discord` adds an opt-in ten-minute human window. Include `--plugins=hub,discord,syncplay,notifications --library --live-discord --load-test-env`. Invoke `/help`, `/status`, `/link`, complete native linking and start native playback, then exercise both `/watchparty` delivery paths. A disposable HTTPS endpoint and approved destination are required. The fixture never forges interactions, automates personal accounts or accepts personal user tokens. Counters are process-local administrator-only allowlisted aggregates with no identities or message text. Earlier human/package passes do not certify current source.

Ordinary tests remain secret-free. Authorized local live checks use `node scripts/integration.mjs --packages=<linux-x64-packages> --plugins=notifications,discord --library --live-discord --load-test-env` with `DISCORD_LIVE_TESTS=true`. Credentials never appear in arguments. Live mode forbids `--keep`, withholds sensitive logs/bodies and removes credential-bearing fixture state. Interrupted runs can leave owned messages requiring manual inspection. Registration updates guild commands: use a dedicated bot/guild.

Live checks are local only; no live Discord GitHub Actions job is configured or
required. Future CI must use the reviewed environment policy above, never an uploaded
`.env`. The default live mode verifies gateway/registration and installed channel
announcements; human interaction/DMs require their explicit additional mode, a consenting
recipient and independent acceptance evidence. No GitHub secrets/environments were
created or published by this change.

## References

- [Discord setup and delivery](discord-suite-integration.md)
- [Current acceptance scope](review-status.md) · [Evidence register](verification.md)
