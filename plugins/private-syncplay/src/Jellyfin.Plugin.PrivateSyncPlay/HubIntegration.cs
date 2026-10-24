using System.Text.Json;
using System.Text.Json.Nodes;
using AccountHub.Api;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.PrivateSyncPlay;

public sealed record HubDomainEvent(string Type, Guid PartyId, Guid Owner, Guid[] Participants, string? GuestLabel = null, string? Reason = null);

public sealed class HubIntegration(PartyStore store, HubOutbox outbox, AccountHubClient client, Func<Configuration> configuration,
    HubPolicyService policies, ILogger<HubIntegration> logger, ISessionManager sessions) : BackgroundService
{
    private DateTimeOffset _warned;
    private bool _ready;
    private string _lastError = "none";
    private Plugin? _plugin;
    public object Status()
    {
        try { return new { available = client.IsAvailable, ready = _ready, lastError = _lastError, outbox = outbox.Counts() }; }
        catch { return new { available = false, ready = false, lastError = "outbox_unavailable", outbox = new Dictionary<string, long>() }; }
    }
    private void Warn(string code)
    {
        _lastError = code;
        if (DateTimeOffset.UtcNow - _warned < TimeSpan.FromMinutes(5)) return;
        _warned = DateTimeOffset.UtcNow;logger.LogWarning("Private SyncPlay optional Account Hub integration: {Code}. Native party behavior is unchanged.", code);
    }
    private void PolicyCommitted(Guid actor, JsonObject summary)
    {
        // Explicit projection; never forward policy response/user names or arbitrary request data.
        Publish(new("party.settings.changed", Guid.Empty, actor, [actor]), "syncplay.policy.changed",
            new { scope = summary["scope"]?.GetValue<string>(),
                fields = (summary["changedFields"] as JsonArray)?.Select(n => new { field = n!["field"]!.GetValue<string>(),
                    before = n["field"]!.GetValue<string>() == "guestPlayer" ? JsonValue.Create("layout_changed") : n["before"]?.DeepClone(),
                    after = n["field"]!.GetValue<string>() == "guestPlayer" ? JsonValue.Create("layout_changed") : n["after"]?.DeepClone() }).ToArray(),
                userPolicies = (summary["users"] as JsonArray)?.Select(n => new { userId = n!["userId"]?.DeepClone(), before = n["before"]?["policy"]?.DeepClone(), after = n["after"]?["policy"]?.DeepClone() }).ToArray(),
                skippedAdminCount = (summary["skippedAdmins"] as JsonArray)?.Count ?? 0 });
    }
    private void Event(HubDomainEvent value)
    {
        var config = configuration();
        if (config.NotificationMode != NotificationMode.Disabled && (config.NotificationMode == NotificationMode.Local || !config.HubIntegrationEnabled))
            NativeNotice(value.Type, value.Owner == Guid.Empty ? policies.Administrators() : [value.Owner], "A watch party event occurred. Open Private SyncPlay for details.");
        Publish(value);
    }
    private void NativeNotice(string type, Guid[] audience, string text)
    {
        // UserJoined is already mandatory native in-party feedback. Never duplicate it through the optional path.
        if (!configuration().NativeNotifications || type == "party.guest.joined") return;
        try
        {
            foreach (var session in sessions.Sessions.Where(s => audience.Contains(s.UserId)).Take(1000).ToArray())
                _ = SendNative(session.Id, text);
        }
        catch { Warn("native_notice_unavailable"); }
    }
    private async Task SendNative(string id, string text)
    {
        try { await sessions.SendMessageCommand(id, id, new MessageCommand { Header = "Watch party", Text = text, TimeoutMs = 5000 }, CancellationToken.None).ConfigureAwait(false); }
        catch { Warn("native_notice_unavailable"); }
    }
    private void Queue(Guid id, string kind, string payload, DateTimeOffset now, TimeSpan freshness)
    { try { outbox.Enqueue(id, kind, payload, now, freshness); } catch { Warn("outbox_write_failed"); } }
    private void NativeSettingsCommitted() => Publish(new("party.settings.changed", Guid.Empty, Guid.Empty, []), "syncplay.policy.changed", new { scope = "native_configuration" });
    private void PlaybackStart(object? sender, PlaybackProgressEventArgs args)
    {
        lock (store.Gate)
        {
            var party = store.ForSession(args.Session.Id, args.Session.UserId);
            if (party is null || party.StartPublished) return;
            party.StartPublished = true;store.NotifyDomain(party, "party.started");
        }
    }
    private void Publish(HubDomainEvent value, string? auditType = null, object? policy = null)
    {
        try
        {
            var config = configuration();if (!config.HubIntegrationEnabled) return;
            var now = DateTimeOffset.UtcNow;var id = Guid.NewGuid();
            var metadata = new { partyId = value.PartyId, guestLabel = value.GuestLabel, reason = value.Reason, policy };
            if (config.NotificationMode is NotificationMode.Auto or NotificationMode.Hub)
            {
                var participants = value.Type switch
                {
                    "party.started" => value.Participants.Append(value.Owner).ToArray(),
                    "party.invite.created" or "party.invite.revoked" => [value.Owner],
                    "party.ended" => value.Participants.Append(value.Owner).Concat(policies.Administrators()).ToArray(),
                    _ => new[] { value.Owner }.Concat(policies.Administrators()).ToArray()
                };
                var noticeMetadata = new Dictionary<string, string> { ["partyId"] = value.PartyId.ToString("D") };
                if (value.GuestLabel is not null) noticeMetadata["guestLabel"] = value.GuestLabel;
                if (value.Reason is not null) noticeMetadata["reason"] = value.Reason;
                var notification = new { schemaVersion = 1, eventId = id, source = "private-syncplay", type = value.Type, severity = "info",
                    title = "Watch party update", body = value.Type == "party.guest.joined" ? value.GuestLabel + " joined a watch party." : "A watch party event occurred. Open Private SyncPlay for details.",
                    audience = participants.Where(u => u != Guid.Empty).Distinct().ToArray(), adminOnly = false, dedupeKey = "syncplay:" + id,
                    occurredAt = now, correlationId = id, metadata = noticeMetadata };
                Queue(id, "notification", JsonSerializer.Serialize(notification, HubWire.Json), now, TimeSpan.FromMinutes(2));
            }
            if (config.PublishAudit)
            {
                auditType ??= value.Type switch { "party.guest.joined" => "syncplay.guest.joined", "party.guest.left" => "syncplay.guest.left", "party.guest.revoked" => "syncplay.guest.revoked", "party.invite.created" => "syncplay.invite.created", "party.invite.revoked" => "syncplay.invite.revoked", "party.moderation" => "syncplay.moderation", "party.settings.changed" => "syncplay.policy.changed", _ => "syncplay." + value.Type };
                var audit = new { schemaVersion = 1, eventId = id, source = "private-syncplay", eventType = auditType,
                    actorUserId = value.Type.StartsWith("party.guest.", StringComparison.Ordinal) || value.Owner == Guid.Empty ? (Guid?)null : value.Owner,
                    subjectType = policy is null ? "party" : "config", subjectId = value.PartyId.ToString(), outcome = "success", occurredAt = now, correlationId = id, metadata };
                Queue(id, "audit", JsonSerializer.Serialize(audit, HubWire.Json), now, TimeSpan.FromDays(30));
            }
        }
        catch { Warn("outbox_write_failed"); }
    }
    public async Task DispatchOnce(CancellationToken token)
    {
        var config = configuration();
        var rows = outbox.Claim(DateTimeOffset.UtcNow);
        _ready = false;
        if (config.HubIntegrationEnabled)
        {
            var hello = await client.InvokeAsync("hello", JsonSerializer.SerializeToElement(new { }), token).ConfigureAwait(false);
            if (hello is { } h && h.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                && h.TryGetProperty("data", out var data) && data.TryGetProperty("apiVersion", out var version) && version.GetString() == "1"
                && data.TryGetProperty("operations", out var operations) && operations.ValueKind == JsonValueKind.Array
                && new[] { "health", "notifications.publish", "audit.publish" }.All(op => operations.EnumerateArray().Any(o => o.ValueKind == JsonValueKind.String && o.GetString() == op)))
            {
                var health = await client.InvokeAsync("health", JsonSerializer.SerializeToElement(new { }), token).ConfigureAwait(false);
                _ready = health is { } result && result.TryGetProperty("ok", out var success) && success.ValueKind == JsonValueKind.True
                    && result.TryGetProperty("data", out var status) && status.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.True;
            }
        }
        foreach (var row in rows)
        {
            var result = "unavailable";
            if (row.Kind == "notification" && (config.NotificationMode is NotificationMode.Local or NotificationMode.Disabled
                || !config.HubIntegrationEnabled || (!_ready && config.NotificationMode == NotificationMode.Auto))) result = "local_fallback";
            else if (row.Kind == "audit" && (!config.HubIntegrationEnabled || !config.PublishAudit)) result = "local_fallback";
            else if (_ready)
            {
                using var document = JsonDocument.Parse(row.Payload);
                var reply = await client.InvokeAsync(row.Kind == "audit" ? "audit.publish" : "notifications.publish", document.RootElement, token).ConfigureAwait(false);
                if (reply is { } r && r.TryGetProperty("ok", out var success) && success.ValueKind == JsonValueKind.True
                    && r.TryGetProperty("data", out var data) && data.TryGetProperty("status", out var status) && status.GetString() is { } s && s is "accepted" or "duplicate" or "rejected") result = s;
                else if (reply is { } failure && failure.TryGetProperty("error", out var error) && error.TryGetProperty("retryable", out var retry) && retry.ValueKind == JsonValueKind.False) result = "rejected";
            }
            outbox.Complete(row, result, DateTimeOffset.UtcNow);
            if (result is "rejected" or "local_fallback")
            {
                Warn("local_fallback");
                if (row.Kind == "notification" && config.NotificationMode != NotificationMode.Disabled && DateTimeOffset.UtcNow - row.Created < TimeSpan.FromMinutes(2))
                {
                    using var document = JsonDocument.Parse(row.Payload);var payload = document.RootElement;
                    NativeNotice(payload.GetProperty("type").GetString()!, payload.GetProperty("audience").EnumerateArray().Select(v => v.GetGuid()).ToArray(), payload.GetProperty("body").GetString()!);
                    if (config.HubIntegrationEnabled && config.PublishAudit)
                    {
                        var id = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("fallback:" + row.Id)).AsSpan(0, 16));
                        var now = DateTimeOffset.UtcNow;
                        Queue(id, "audit", JsonSerializer.Serialize(new { schemaVersion = 1, eventId = id, source = "private-syncplay", eventType = "syncplay.notification.fallback", actorUserId = (Guid?)null,
                            subjectType = "event", subjectId = row.Id, outcome = "failure", occurredAt = now, correlationId = id, metadata = new { reason = "hub_unavailable" } }, HubWire.Json), now, TimeSpan.FromDays(30));
                    }
                }
            }
        }
    }
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        store.DomainEvent += Event;policies.Committed += PolicyCommitted;sessions.PlaybackStart += PlaybackStart;
        _plugin = Plugin.Instance;
        if (_plugin is not null) { _plugin.NativeSettingsCommitted += NativeSettingsCommitted;_plugin.ProtectNativeDefaults = policies.ProtectNative; }
        return base.StartAsync(cancellationToken);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            { try { await DispatchOnce(stoppingToken).ConfigureAwait(false); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; } catch { Warn("dispatch_unavailable"); } }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            store.DomainEvent -= Event;policies.Committed -= PolicyCommitted;sessions.PlaybackStart -= PlaybackStart;
            if (_plugin is not null) { _plugin.NativeSettingsCommitted -= NativeSettingsCommitted;_plugin.ProtectNativeDefaults = null; }
        }
    }
}
