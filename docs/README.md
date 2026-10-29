# Documentation

## Dependencies and installation

Start with the [plugin overview and architecture-specific installation instructions](../README.md).
Then check the [feature dependency matrix](dependencies.md). Packages are previews;
published catalog availability, compatible server ABI and current provider health
are separate requirements. Back up plugin databases and their original keys before
upgrading; never reset history or credentials to repair a failed migration.

## Choose a guide

| Goal | Guide |
|---|---|
| Manage accounts, templates, invitations and verified links | [Account management](account-management.md) |
| Understand the existing-session recovery requirement | [Security and recovery](security-recovery-integration.md) |
| Configure the bot, account commands and delivery | [Discord integration](discord-suite-integration.md) |
| Enable future episode/movie announcements | [Library notifications](library-notifications.md) |
| Choose personal categories, importance or consent swipes | [Notification controls](notification-channels.md) |
| Enroll a browser/device for Push | [Web Push](web-push.md) |
| Configure removal requests, safeguards and artwork | [Deletion Queue](../plugins/deletion-queue/README.md) |
| Configure rules that submit removal requests | [Automatic Deletion Requests](../plugins/automatic-deletion-requests/README.md) |
| Configure parties, invitations and guest viewing | [Private SyncPlay](../plugins/private-syncplay/README.md) |
| Recover plugin storage without losing keys/history | [Storage recovery](plugin-storage-recovery.md) |
| Build or run disposable acceptance | [Testing](testing.md) · [Theme acceptance](theme-acceptance.md) |
| Trace HTTP, bot and provider authorization boundaries | [Route authorization review](route-authorization-review.md) |
| Maintain dependencies or publish a release | [Dependency maintenance](dependency-maintenance.md) · [Release checklist](github-release-checklist.md) |
| Choose a GitHub workflow or understand its gates | [Workflow map](../.github/README.md) |

## Status and document scope

[Current review status](review-status.md) is the source for current evidence and
blockers. Latest-only release gates reject stale dependency declarations; the current
bump and acceptance results are tracked there, not inferred from earlier packages.

Product requirements describe intended boundaries, not a promise that every planned
feature is implemented. Superseded audits/handoffs have been consolidated into the
current safety guides and evidence register; original runtime artifacts remain
unchanged. Older evidence is not blanket compatibility certification. No document
authorizes live Discord testing, production deletion, credential disclosure or
publication by itself.

## References

- [Product requirements](product-requirements.md) · [Remaining v1 gates](v1-release-gates.md)
- [Independent and suite releases](independent-plugin-releases.md)
- [Metadata source contracts](metadata-source-parity.md)
- [Test credential handling](discord-test-credentials.md)
- [User creation workflow](user-creation-workflow.md)
- [Evidence register and scoped historical milestones](verification.md)
- [Preserved development review history](review-history.md)
- [Safety, permissions and operational limits](safety-and-permissions.md)
- [Notification producer/host API](notification-producer-api.md)
