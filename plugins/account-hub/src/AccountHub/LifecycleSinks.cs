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

public sealed class LifecycleNoticeSink(AccountHub.Notifications.NotificationService notifications) : IAdminNoticeSink
{
    public async Task PublishAsync(CancellationToken ct)
    {
        var entry = new AccountHub.Api.NotificationEvent(1, Guid.NewGuid(), "account-hub", "account.updated", "info", "Account activity", "Review Account Hub audit for account activity.", [], true, DateTimeOffset.UtcNow);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AccountHub.Api.Contract.Serialize(entry))));
        await notifications.PublishAsync(entry, hash, ct);
    }
}
