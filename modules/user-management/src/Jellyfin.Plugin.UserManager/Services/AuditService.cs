using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.UserManager.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UserManager.Services;

public interface IAuditSink { Task AppendAsync(AuditRecord record, CancellationToken ct); }
public interface IAdminNoticeSink { Task PublishAsync(CancellationToken ct); }

public sealed class AuditService
{
    public const string ActionLogin = "auth.login";
    public const string ActionInviteCreated = "invite.created";
    public const string ActionInviteRevoked = "invite.revoked";
    public const string ActionInviteUsed = "invite.used";
    public const string ActionUserCreated = "user.created";
    public const string ActionUserUpdated = "user.updated";
    public const string ActionUserDeleted = "user.deleted";
    public const string ActionPresetApplied = "preset.applied";
    public const string ActionPresetSaved = "preset.saved";
    public const string ActionPresetDeleted = "preset.deleted";
    public const string ActionPasswordReset = "password.reset";
    public const string ActionPasswordChanged = "password.changed";
    public const string ActionDiscordLinked = "discord.linked";
    public const string ActionDiscordUnlinked = "discord.unlinked";
    public const string ActionSettingsUpdated = "settings.updated";
    public const string ActionSeerrSync = "seerr.sync";
    public const string ActionSeerrRequest = "seerr.request";

    private readonly PluginStore _store;
    private readonly ILogger<AuditService> _logger;
    private readonly IAuditSink? _sink;

    public AuditService(PluginStore store, ILogger<AuditService> logger, IAuditSink? sink = null)
    {
        _store = store;
        _logger = logger;
        _sink = sink;
    }

    public Task LogAsync(
        string action,
        string actorUserId,
        string actorName,
        string? targetUserId = null,
        string? targetName = null,
        string? details = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var record = new AuditRecord
        {
            Action = action,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ActorUserId = actorUserId,
            ActorName = Truncate(actorName, 255) ?? string.Empty,
            TargetUserId = targetUserId,
            TargetName = Truncate(targetName, 255),
            Details = Truncate(details, 4000),
            IpAddress = Truncate(ipAddress, 64)
        };

        return WriteAsync(record, cancellationToken);
    }

    public async Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        if (_sink is not null) { await _sink.AppendAsync(record, cancellationToken); return; }
        try
        {
            await _store.AddAuditAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Auditing must never break the user-facing operation.
            _logger.LogError(ex, "Failed to write audit entry {Action}", record.Action);
        }
    }

    public Task<IReadOnlyList<AuditRecord>> RecentAsync(
        int limit,
        int offset = 0,
        string? action = null,
        string? userId = null,
        CancellationToken cancellationToken = default) =>
        _store.GetAuditAsync(limit, offset, action, userId, cancellationToken);

    public static (string Id, string Name) Actor(ClaimsPrincipal principal) =>
        (JellyfinUserService.GetUserId(principal)?.ToString("N") ?? "system",
         principal.Identity?.Name ?? "system");

    public static (string Id, string Name) SystemActor(string name = "system") =>
        ("system", name);

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}

public interface IDiscordMessageSender
{
    bool IsConnected { get; }

    Task<bool> SendDirectMessageAsync(string discordUserId, string message, CancellationToken cancellationToken = default);
}

public sealed class DiscordWebhookClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DiscordWebhookClient> _logger;

    public DiscordWebhookClient(IHttpClientFactory httpClientFactory, ILogger<DiscordWebhookClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<bool> SendAsync(string webhookUrl, string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)
            || !Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("UserManager");
            using var response = await client.PostAsJsonAsync(
                uri,
                new { content, allowed_mentions = new { parse = Array.Empty<string>() } },
                JsonOptions,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Discord webhook returned {Status}", (int)response.StatusCode);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send Discord webhook notification");
            return false;
        }
    }
}

public sealed class NotificationService
{
    private readonly PluginSettingsService _settings;
    private readonly PluginStore _store;
    private readonly JellyfinUserService _users;
    private readonly DiscordWebhookClient _webhook;
    private readonly Lazy<IDiscordMessageSender> _sender;
    private readonly ILogger<NotificationService> _logger;
    private readonly IAdminNoticeSink? _sink;

    public NotificationService(
        PluginSettingsService settings,
        PluginStore store,
        JellyfinUserService users,
        DiscordWebhookClient webhook,
        Lazy<IDiscordMessageSender> sender,
        ILogger<NotificationService> logger, IAdminNoticeSink? sink = null)
    {
        _settings = settings;
        _store = store;
        _users = users;
        _webhook = webhook;
        _sender = sender;
        _logger = logger;
        _sink = sink;
    }

    public async Task<bool> SendToUserAsync(Guid userId, string message, CancellationToken cancellationToken = default)
    {
        var link = await _store.GetLinkByJellyfinUserIdAsync(userId.ToString("N"), cancellationToken).ConfigureAwait(false);
        if (link is null)
        {
            return false;
        }

        return await _sender.Value.SendDirectMessageAsync(link.DiscordUserId, message, cancellationToken).ConfigureAwait(false);
    }

    public async Task NotifyAdminsAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        if (_sink is not null) { await _sink.PublishAsync(cancellationToken); return; }
        try
        {
            var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            var body = $"**{title}**\n{message}";
            if (!string.IsNullOrWhiteSpace(settings.DiscordAdminWebhookUrl))
            {
                await _webhook.SendAsync(settings.DiscordAdminWebhookUrl, body, cancellationToken).ConfigureAwait(false);
            }

            var links = await _store.GetLinksAsync(cancellationToken).ConfigureAwait(false);
            foreach (var link in links)
            {
                if (Guid.TryParse(link.JellyfinUserId, out var userId) && _users.IsAdministrator(userId))
                {
                    await _sender.Value.SendDirectMessageAsync(link.DiscordUserId, body, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify admins");
        }
    }
}
