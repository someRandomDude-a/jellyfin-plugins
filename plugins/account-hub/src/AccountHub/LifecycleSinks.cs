using System.Text.Json;
using AccountHub.Core;
using AccountHub.Notifications;
using Jellyfin.Plugin.UserManager.Data;
using Jellyfin.Plugin.UserManager.Services;

namespace AccountHub;

public sealed class LifecycleAuditSink(HubStore store) : IAuditSink
{
    public async Task AppendAsync(AuditRecord record, CancellationToken ct) => await store.AppendAuditAsync(new AccountHub.Api.Contracts.AuditEvent
    {
        EventId = Guid.TryParse(record.Id, out var id) ? id : Guid.NewGuid(), Source = "account-hub", EventType = record.Action,
        ActorUserId = Guid.TryParse(record.ActorUserId, out var actor) ? actor : null,
        SubjectType = "user", SubjectId = Guid.TryParse(record.TargetUserId, out var target) ? target.ToString("D") : null,
        OccurredAt = DateTimeOffset.FromUnixTimeMilliseconds(record.Timestamp),
        Metadata = JsonSerializer.SerializeToElement(new { reason = "lifecycle_transition" })
    }, ct);
}

public sealed class LifecycleNoticeSink(AccountHub.Api.NotificationsClient notifications) : IAdminNoticeSink
{
    public Task PublishAsync(CancellationToken ct)=>PublishAsync("Account activity","",ct);
    public async Task PublishAsync(string title,string message,CancellationToken ct)
    {
        // Project the action, never arbitrary invite contact text, usernames,
        // passwords, reset links or provider prose supplied by a legacy caller.
        var notice=title switch {
            "Password changed" => ("account.password.changed","Account password changed","An account password was changed from its account page. Review the account audit if this was unexpected."),
            "User created" => ("account.created","Jellyfin account created","A new account was created with an administrator-selected access preset. Review its current access in user management."),
            "Invite requested" => ("account.invite.requested","Invitation request received","A new signup invitation request needs administrator review. Open invitations to review its details and decide whether to approve it."),
            "Password reset" => ("account.password.reset","Account password reset","An account completed a password reset through its private Discord recovery link. Review the account audit if this was unexpected."),
            "Password reset requested" => ("account.password.reset.requested","Password recovery requested","A private Discord password-recovery link was requested. Requesting a link alone does not change the password."),
            "Invite used" => ("account.invite.used","Signup invitation redeemed","A signup invitation was used to create an account with its configured access preset. Review invitations for its remaining uses."),
            _ => ("account.updated","Account management activity","An account management operation completed. Open the account audit to review its action and outcome.")
        };
        var entry = new AccountHub.Api.NotificationEvent(1, Guid.NewGuid(), "account-hub", notice.Item1, "info", notice.Item2, notice.Item3, [], true, DateTimeOffset.UtcNow,Link:"web/index.html#/configurationpage?name=AccountHub");
        await notifications.PublishRequiredAsync(entry, ct);
    }
}
