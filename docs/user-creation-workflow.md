# User creation ownership

With Account Hub installed, create accounts through **Account Hub → Invites**. The normal native HTTP `POST /Users/New` endpoint is blocked with `409 use_account_hub` and instructions linking to Hub. Initial Jellyfin server setup is unaffected. The plugin does not redirect POST requests into an onboarding form or silently resubmit credentials.

An elevated administrator can use **Account Hub → Settings → User creation workflow → Enable native creation for five minutes**. This is an audited, revisioned command requiring the configured confirmation/fresh-proof policy. It does not bypass Jellyfin's own authorization. API keys and non-administrators cannot grant the override.

During the override:

- Hub public redeem/resume is paused before reserving slots or provisioning accounts.
- Native HTTP creation requests are serialized and retain their native authentication/validation.
- An admitted native request holds ownership until it finishes, even if the timer expires while it runs. Hub cannot start a competing creation in that interval.
- Ownership changes wait for an active Hub onboarding workflow to finish. No native database transaction is held while waiting for that workflow.

The admission window expires after five minutes using a monotonic timer, can be ended early in Settings, and is cleared on restart. A repeated completed command does not renew the window; a new authorized command is required. The Settings status can be refreshed after expiry.

This guard covers the native HTTP admin workflow, including clients that call the same endpoint. It does **not** intercept direct `IUserManager` calls from other plugins or direct database writes. Native internal races outside that boundary remain known limitations. No native schema, indexes, migrations, server binaries or private locks are changed.

Duplicate internal IDs are detected by maintenance and require reviewed offline remediation; blocking the native workflow does not reconstruct previously ambiguous cache/legacy ownership.
