using System.Text.Json;
using AccountHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace AccountHub.Core;

public sealed record RetentionIntent(int AuditRetentionDays, int NotificationRetentionDays);

// Same-database retention update; fresh proof is request-local and outside the writer transaction.
public sealed class HubSettingsService(CommandGate commands)
{
    public Task<JsonElement> UpdateRetentionAsync(Guid actor, Guid commandId, long revision, RetentionIntent intent,
        Func<CancellationToken, Task> authorize, CancellationToken ct = default)
    {
        if (intent.AuditRetentionDays is < 15 or > 365 || intent.NotificationRetentionDays is < 7 or > 365)
            throw new HubException("invalid_request", "Retention is outside the supported range");
        return commands.ExecuteAsync(actor, "settings.retention.changed", commandId, revision, intent, authorize,
            async (db, token) =>
            {
                var state = await db.State.SingleAsync(token);
                state.AuditRetentionDays = intent.AuditRetentionDays;
                state.NotificationRetentionDays = intent.NotificationRetentionDays;
                return JsonSerializer.SerializeToElement(new { revision = state.Revision + 1 });
            }, ct);
    }
}
