using System.Text.Json;
using AccountHub.Core;
using AccountHub.Security;
using AccountHub.Notifications;
using DiscordBridge.Api;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.UserManager.Data;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AccountHub;

public sealed class HubHost(IServiceProvider services, HubStore store, ExternalCommandGate commands) : ISecurityHubHost, INotificationHost
{
    public async Task<Guid?> CurrentUserAsync(HttpContext context, CancellationToken ct)
    {
        try { return (await Jellyfin.PluginSupport.NativeAuthorization.RequireAsync(context, services, administrator: false, elevated: false)).UserId; }
        catch (Jellyfin.PluginSupport.NativeAuthorizationFailure) { return null; }
    }
    public Task<bool> UserExistsAsync(Guid id, CancellationToken ct)
    {
        var user = services.GetRequiredService<IUserManager>().GetUserById(id);
        return Task.FromResult(user is not null && !user.HasPermission(PermissionKind.IsDisabled));
    }
    public Task<bool> IsAdministratorAsync(Guid id, CancellationToken ct)
    {
        var user = services.GetRequiredService<IUserManager>().GetUserById(id);
        return Task.FromResult(user is not null && !user.HasPermission(PermissionKind.IsDisabled) && user.HasPermission(PermissionKind.IsAdministrator));
    }
    public Task<bool> CanAccessNotificationAsync(Guid id, AccountHub.Api.NotificationEvent entry, CancellationToken ct)
    {
        if (!entry.Type.StartsWith("library.", StringComparison.Ordinal)) return Task.FromResult(true);
        if (entry.Metadata is null || !entry.Metadata.TryGetValue("itemId", out var value) || !Guid.TryParse(value, out var itemId)) return Task.FromResult(false);
        try
        {
            var user = services.GetRequiredService<IUserManager>().GetUserById(id);
            var item = services.GetRequiredService<ILibraryManager>().GetItemById(itemId);
            return Task.FromResult(user is not null && !user.HasPermission(PermissionKind.IsDisabled) && item is not null && item.IsVisibleStandalone(user));
        }
        catch { return Task.FromResult(false); }
    }
    public Task<IReadOnlyList<Guid>> AdministratorsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Guid>>(services.GetRequiredService<IUserManager>().GetUsers().Where(u => u.HasPermission(PermissionKind.IsAdministrator) && !u.HasPermission(PermissionKind.IsDisabled)).Select(u => u.Id).Take(1001).ToArray());
    public async Task<CallerSession> RequireAdministratorAsync(HttpContext context, CancellationToken ct)
    {
        MediaBrowser.Controller.Net.AuthorizationInfo info;
        try { info = await Jellyfin.PluginSupport.NativeAuthorization.RequireAsync(context, services, administrator: true, elevated: true); }
        catch (Jellyfin.PluginSupport.NativeAuthorizationFailure) { throw new SecurityFailure("unauthorized", 403); }
        var app = services.GetRequiredService<IServerApplicationHost>();
        // Native host configuration supplies listener/BaseUrl. Host/Forwarded headers never select the target.
        var trusted = new Uri(app.GetLocalApiUrl("127.0.0.1", "http", app.HttpPort).TrimEnd('/') + '/');
        if (string.IsNullOrEmpty(info.Token)) throw new SecurityFailure("unauthorized", 401);
        return new(info.UserId, info.Token, trusted);
    }
    public Task<(SecuritySettings Settings, long Revision)> ReadSettingsAsync(CancellationToken ct) => store.ReadAsync(async (db, token) =>
    {
        var value = await db.State.AsNoTracking().SingleAsync(token);
        if (!Enum.TryParse<StepUpLevel>(value.StepUpLevelMirror, out var level)) throw new SecurityFailure("unavailable", 503);
        return (new SecuritySettings(level, value.SecurityAlertMode, value.PasswordLoginPolicy), value.Revision);
    }, ct);
    public async Task<bool> LocalPasswordFlowsAllowedAsync(CancellationToken ct)
    {
        var policy = (await ReadSettingsAsync(ct)).Settings.PasswordLoginPolicy;
        if (policy == "local_disabled") return false;
        if (policy == "allow_local") return true;
        var health = services.GetRequiredService<ISecurityCompatibility>().Check();
        if (health.State is "missing" or "disabled") return true;
        if (!health.IsOk) throw new HubException("password_policy_unavailable", "Native password policy cannot be verified", true);
        var manager = services.GetRequiredService<MediaBrowser.Common.Plugins.IPluginManager>();
        var plugin = manager.Plugins.SingleOrDefault(p => p.Id == SupportedSecurity.PluginId && p.IsEnabledAndSupported);
        if (plugin?.Instance is not MediaBrowser.Common.Plugins.IHasPluginConfiguration configurable)
            throw new HubException("password_policy_unavailable", "Native password policy cannot be verified", true);
        // Pinned public plugin configuration only, never private factor records, cookies or keys.
        // Conservative global policy: do not infer LAN/admin exemptions for anonymous flows.
        return !NativePasswordPolicy.Disabled(configurable.Configuration);
    }
    public async Task<CommandOutcome> ExecuteAsync(CallerSession caller, CommandDescriptor command,
        Func<SecuritySettings, CancellationToken, Task<(CommandOutcome Outcome, AuthorizationEvidence Evidence)>> execute, CancellationToken ct)
    {
        var applySettings = false;
        AuthorizationEvidence? evidence = null;
        SecuritySettings? old = null;
        try
        {
            var result = await commands.ExecuteAsync(caller.UserId, command.Operation, command.CommandId, command.Revision, command,
                async (state, token) =>
                {
                    old = new(Enum.Parse<StepUpLevel>(state.StepUpLevelMirror), state.SecurityAlertMode, state.PasswordLoginPolicy);
                    if (!await IsAdministratorAsync(caller.UserId, token)) throw new SecurityFailure("forbidden", 403);
                    if (command.TargetUserId is { } target)
                    {
                        if (!await UserExistsAsync(target, token)) throw new SecurityFailure("not_found", 404);
                        if (command.Operation == "security.force_logout" && (target == caller.UserId || await IsAdministratorAsync(target, token) && (await AdministratorsAsync(token)).Count <= 1)) throw new SecurityFailure("administrator_protected", 409);
                    }
                }, async token =>
                {
                    try
                    {
                        var completed = await execute(old!, token);
                        evidence = completed.Evidence;
                        applySettings = command.NewSettings is not null && completed.Outcome.Status == "applied";
                        return JsonSerializer.SerializeToElement(completed.Outcome, SecurityJson.Options);
                    }
                    catch (SecurityFailure failure) { return JsonSerializer.SerializeToElement(new { failureCode = failure.Code, failureStatus = failure.Status }); }
                }, (state, _) =>
                {
                    if (applySettings)
                    {
                        state.StepUpLevelMirror = command.NewSettings!.StepUpLevelMirror.ToString();
                        state.SecurityAlertMode = command.NewSettings.AlertMode; state.PasswordLoginPolicy = command.NewSettings.PasswordLoginPolicy;
                    }
                    return Task.CompletedTask;
                }, ct, auditMetadata: () => JsonSerializer.SerializeToElement(new { reason = evidence?.Method ?? "authorization_pending", fallbackReason = evidence?.FallbackReason }));
            if (result.TryGetProperty("failureCode", out var failureCode)) throw new SecurityFailure(failureCode.GetString()!, result.GetProperty("failureStatus").GetInt32());
            return result.Deserialize<CommandOutcome>(SecurityJson.Options)!;
        }
        catch (HubException failure) { throw new SecurityFailure(failure.Code, failure.Code is "revision_changed" or "command_conflict" ? 409 : 503, failure.Retryable); }
    }
    public async Task AuthorizeAsync(HttpContext context, CommandRisk risk, CancellationToken ct)
    {
        var caller = await RequireAdministratorAsync(context, ct);
        var settings = (await ReadSettingsAsync(ct)).Settings;
        string? confirmation = null; StepUpProof? proof = null;
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Request.EnableBuffering(65536, 65536);
            if (context.Request.Body.CanSeek) context.Request.Body.Position = 0;
            try
            {
                using var body = await JsonDocument.ParseAsync(context.Request.Body, new JsonDocumentOptions { MaxDepth = 16 }, ct);
                if (body.RootElement.TryGetProperty("confirmation", out var value)) confirmation = value.GetString();
                if (body.RootElement.TryGetProperty("stepUp", out var step)) proof = step.Deserialize<StepUpProof>(SecurityJson.Options);
            }
            catch (JsonException) { throw new SecurityFailure("invalid_request", 400); }
            finally { context.Request.Body.Position = 0; }
        }
        else if (risk == CommandRisk.Audit && CommandAuthorization.RequiresProof(settings.StepUpLevelMirror, risk)) throw new SecurityFailure("step_up_required", 403);
        using var scope = services.CreateScope();
        try
        {
            var evidence = await scope.ServiceProvider.GetRequiredService<CommandAuthorization>().AuthorizeAsync(caller, settings, risk, false, confirmation, "CONFIRM ACCOUNT HUB CHANGE", proof, ct);
            if (risk != CommandRisk.Audit)
                await store.AppendAuditAsync(new AccountHub.Api.Contracts.AuditEvent { EventId = Guid.NewGuid(), Source = "account-hub", EventType = "management.authorized", ActorUserId = caller.UserId, OccurredAt = DateTimeOffset.UtcNow,
                    Metadata = JsonSerializer.SerializeToElement(new { reason = evidence.Method, fallbackReason = evidence.FallbackReason }) }, ct);
        }
        catch (SecurityFailure failure)
        {
            await store.AppendAuditAsync(new AccountHub.Api.Contracts.AuditEvent { EventId = Guid.NewGuid(), Source = "account-hub", EventType = "management.denied", Outcome = "denied", ActorUserId = caller.UserId, OccurredAt = DateTimeOffset.UtcNow,
                Metadata = JsonSerializer.SerializeToElement(new { errorCode = failure.Code }) }, CancellationToken.None);
            throw;
        }
    }
    public async Task AuthorizeManagementAsync(HttpContext context, bool configurationWrite, CancellationToken ct)
    {
        try { if (configurationWrite) await AuthorizeAsync(context, CommandRisk.Operational, ct); else await RequireAdministratorAsync(context, ct); }
        catch (SecurityFailure ex) { throw new NotificationException(ex.Code, ex.Code, ex.Status); }
    }
    public async Task AuthorizeAuditAsync(HttpContext context, CancellationToken ct)
    { try { await AuthorizeAsync(context, CommandRisk.Audit, ct); } catch (SecurityFailure ex) { throw new NotificationException(ex.Code, ex.Code, ex.Status); } }
    public async Task<string?> DiscordLinkAsync(Guid id, CancellationToken ct) => (await services.GetRequiredService<PluginStore>().GetLinkByJellyfinUserIdAsync(id.ToString("N"), ct))?.DiscordUserId;
    public async Task<object> AppendAuditAsync(AccountHub.Api.AuditEvent entry, CancellationToken ct)
    {
        var value = JsonSerializer.Deserialize<AccountHub.Api.Contracts.AuditEvent>(JsonSerializer.Serialize(entry, AccountHub.Api.Contract.Json), AccountHub.Api.Contracts.Wire.Options)!;
        if (value.Metadata.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) value = value with { Metadata = JsonSerializer.SerializeToElement(new { }) };
        var accepted = await store.AppendAuditAsync(value, ct);
        return new { status = accepted.Status, auditId = accepted.AuditId };
    }
    public async Task<object> ExportAuditAsync(string format, CancellationToken ct) => await store.ReadAsync(async (db, token) => (object)await db.Audit.AsNoTracking().OrderByDescending(e => e.Sequence).Take(10000).Select(e => e.EventJson).ToArrayAsync(token), ct);
    public bool SecurityCompatible => services.GetRequiredService<ISecurityCompatibility>().Check().IsOk;
    // Receiver properties are populated from persisted settings by the composite worker, never fallback to a weaker mode.
    public string SecurityAlertMode { get; internal set; } = "native";
    public string? SecurityWebhookSecret { get; internal set; }
}
