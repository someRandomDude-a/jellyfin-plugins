using System.Text;
using Jellyfin.Plugin.UserManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.UserManager.Api;

[Authorize(Roles = "Administrator")]
[Route("UserManager/api/admin/audit")]
public sealed class AuditController : ApiBaseController
{
    private readonly AuditService _audit;
    private readonly PluginStoreShim _store;

    public AuditController(AuditService audit, Data.PluginStore store)
    {
        _audit = audit;
        _store = new PluginStoreShim(store);
    }

    [HttpGet]
    public async Task<ActionResult<AuditPageDto>> Get(
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string? action = null,
        [FromQuery] string? user = null,
        CancellationToken cancellationToken = default)
    {
        var entries = await _audit.RecentAsync(limit, offset, action, user, cancellationToken).ConfigureAwait(false);
        var actions = await _store.GetActionsAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new AuditPageDto
        {
            Entries = entries.Select(ApiMapping.ToDto).ToList(),
            Total = await _store.CountAsync(cancellationToken).ConfigureAwait(false),
            Actions = actions.ToList()
        });
    }

    [HttpGet("export.csv")]
    public async Task<IActionResult> Export([FromQuery] int limit = 5000, CancellationToken cancellationToken = default)
    {
        var entries = await _audit.RecentAsync(Math.Clamp(limit, 1, 10000), 0, null, null, cancellationToken)
            .ConfigureAwait(false);
        var builder = new StringBuilder();
        builder.AppendLine("timestamp,action,actor,target,details,ip");
        foreach (var entry in entries)
        {
            builder.AppendLine(string.Join(
                ',',
                Csv(DateTimeOffset.FromUnixTimeMilliseconds(entry.Timestamp).ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
                Csv(entry.Action),
                Csv(entry.ActorName),
                Csv(entry.TargetName),
                Csv(entry.Details),
                Csv(entry.IpAddress)));
        }

        return File(Encoding.UTF8.GetBytes(builder.ToString()), "text/csv", $"usermanager-audit-{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    private static string Csv(string? value)
    {
        var text = value ?? string.Empty;
        return text.Contains(',') || text.Contains('"') || text.Contains('\n')
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }

    /// <summary>
    /// Keeps the store dependency explicit while avoiding Dapper types in the controller public surface.
    /// </summary>
    private sealed class PluginStoreShim
    {
        private readonly Data.PluginStore _store;

        public PluginStoreShim(Data.PluginStore store) => _store = store;

        public Task<long> CountAsync(CancellationToken cancellationToken) => _store.CountAuditAsync(cancellationToken);

        public Task<IReadOnlyList<string>> GetActionsAsync(CancellationToken cancellationToken) => _store.GetAuditActionsAsync(cancellationToken);
    }
}
