using System.Text;
using System.Text.Json;
using AccountHub.Core;
using AccountHub.Security;
using AccountHub.Notifications;
using DiscordBridge.Api;
using Jellyfin.Plugin.UserManager.Services;
using Jellyfin.Plugin.UserManager.Data;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AccountHub.Api;

[ApiController, Authorize(Policy = Policies.RequiresElevation), Route("AccountHub/api/v1"), TypeFilter(typeof(HubBodyFilter)), RequestSizeLimit(65536)]
public sealed class WorkspaceController(HubStore store, HubHost host, JellyfinUserService users, PluginStore lifecycle,
    PluginSettingsService settings, ExternalCommandGate commands, IntegrationSecretService secrets, DiscordBridgeClient discord, HubSettingsService hubSettings, Jellyfin.LibraryNotifications.LibraryNoticeStore libraryNotices, AccountHub.Groups.IGroupPolicyNative nativePolicies, NativeUserWrites writes) : ControllerBase
{
    [HttpGet("workspace")]
    public Task<IActionResult> Workspace(CancellationToken ct) => Run(async () =>
    {
        await host.RequireAdministratorAsync(HttpContext, ct);
        var state = await store.ReadAsync((db, token) => db.State.AsNoTracking().SingleAsync(token), ct);
        var config = await settings.GetAsync(ct);
        var bridge = await discord.CallAsync("health", new { }, ct);
        return new { revision = state.Revision, auditRetentionDays = state.AuditRetentionDays, notificationRetentionDays = state.NotificationRetentionDays,
            stepUpLevelMirror = state.StepUpLevelMirror, securityAlertMode = state.SecurityAlertMode, passwordLoginPolicy = state.PasswordLoginPolicy,
            publicBaseUrl = config.PublicBaseUrl, seerrConfigured = config.SeerrConfigured, discord = bridge, securityCompatible = host.SecurityCompatible,
            securityWebhookConfigured = await store.ReadSecretAsync("security.webhookSecret", ct) is not null };
    });
    [HttpGet("users")]
    public Task<IActionResult> People([FromQuery] string? search = null, [FromQuery] int skip = 0, [FromQuery] int limit = 50, CancellationToken ct = default) => Run(async () =>
    {
        await host.RequireAdministratorAsync(HttpContext, ct);
        if (skip < 0 || limit is < 1 or > 100 || search?.Length > 128) throw new HubException("invalid_request", "Invalid pagination");
        var all = users.GetUsers().Where(u => string.IsNullOrEmpty(search) || u.Username.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new { total = all.Length, items = all.Skip(skip).Take(limit).Select(u => new { id = u.Id, username = u.Username, administrator = users.IsAdministrator(u.Id), disabled = users.IsDisabled(u.Id), policy = ManagedPolicy.Project(users.GetPolicy(u.Id)) }) };
    });
    [HttpGet("groups")]
    public Task<IActionResult> Groups(CancellationToken ct) => Run(async () => { await host.RequireAdministratorAsync(HttpContext, ct); return new { items = await lifecycle.GetPresetsAsync(ct) }; });
    [HttpGet("library/settings")]
    public Task<IActionResult> LibrarySettings(CancellationToken ct) => Run(async () => { await host.RequireAdministratorAsync(HttpContext, ct); return new { settings = libraryNotices.Settings(), health = libraryNotices.Health() }; });
    [HttpPost("policy/preview")]
    public Task<IActionResult> Preview(JsonElement body, CancellationToken ct) => Run(async () =>
    {
        await host.RequireAdministratorAsync(HttpContext, ct);
        return PreviewPolicies(body);
    });
    [HttpPost("policy/snapshots")]
    public Task<IActionResult> Snapshots(JsonElement body, CancellationToken ct) => Run(async () =>
    {
        await host.AuthorizeAsync(HttpContext, CommandRisk.Audit, ct);
        var id = body.GetProperty("userId").GetGuid().ToString("D");
        return await store.ReadAsync(async (db, token) => new { items = (await db.Audit.AsNoTracking().Where(a => a.EventJson.Contains("\"eventType\":\"user.policy.snapshot\"") && a.EventJson.Contains(id)).OrderByDescending(a => a.Sequence).Take(50).Select(a => a.EventJson).ToArrayAsync(token)).Select(json => JsonSerializer.Deserialize<Contracts.AuditEvent>(json, Contracts.Wire.Options)).Where(a => a?.SubjectId == id).ToArray() }, ct);
    });
    private object PreviewPolicies(JsonElement body)
    {
        var ids = body.GetProperty("userIds").Deserialize<Guid[]>()!;
        if (ids.Length is < 1 or > 100) throw new HubException("invalid_request", "Select 1–100 users");
        var patch = body.GetProperty("managedFields");
        var skipped = ids.Where(users.IsAdministrator).ToArray();
        return new { skippedAdmins = skipped, users = ids.Except(skipped).Select(id => new { userId = id, before = ManagedPolicy.Project(users.GetPolicy(id)), after = ManagedPolicy.Project(ManagedPolicy.Apply(users.GetPolicy(id), patch)) }).ToArray() };
    }
    [HttpPost("commands/{operation}")]
    public Task<IActionResult> Command(string operation, JsonElement body, CancellationToken ct) => Run(async () =>
    {
        var caller = await host.RequireAdministratorAsync(HttpContext, ct);
        if (operation is not ("user.disable" or "user.enable" or "user.sessions.revoke" or "policy.apply" or "policy.rollback" or "group.save" or "group.delete" or "settings.public-url" or "settings.seerr" or "settings.library")) throw new HubException("invalid_request", "Unknown workspace command");
        var payload = body.GetProperty("payload");
        var revision = body.GetProperty("revision").GetInt64(); var command = body.GetProperty("commandId").GetGuid();
        return await commands.ExecuteAsync(caller.UserId, operation, command, revision, payload,
            async (_, token) => await host.AuthorizeAsync(HttpContext, operation is "settings.seerr" or "settings.library" ? CommandRisk.Operational : operation is "group.save" or "settings.public-url" ? CommandRisk.Configuration : CommandRisk.Destructive, token),
            async token =>
            {
                object result;
                if (operation.StartsWith("user.", StringComparison.Ordinal))
                {
                    var id = payload.GetProperty("userId").GetGuid();
                    if (users.GetUser(id) is null) throw new HubException("invalid_request", "Unknown user");
                    if (operation == "user.disable" && (id == caller.UserId || users.IsAdministrator(id) && (await host.AdministratorsAsync(token)).Count <= 1)) throw new SecurityFailure("administrator_protected", 403);
                    if (operation == "user.sessions.revoke") await users.RevokeSessionsAsync(id);
                    else { await writes.SetDisabledAsync(id, caller.UserId, operation == "user.disable", token); if (operation == "user.disable") await users.RevokeSessionsAsync(id); }
                    result = new { status = "applied", userId = id };
                }
                else if (operation is "policy.apply" or "policy.rollback")
                {
                    var ids = payload.GetProperty("userIds").Deserialize<Guid[]>()!;
                    if (ids.Length is < 1 or > 100) throw new HubException("invalid_request", "Select 1–100 users");
                    var outcomes = new List<object>();
                    foreach (var id in ids.Distinct())
                    {
                        if (users.IsAdministrator(id)) { outcomes.Add(new { userId = id, status = "administrator_skipped" }); continue; }
                        var patch = payload.TryGetProperty("managedFields", out var proposed) ? proposed : default;
                        if (operation == "policy.rollback")
                        {
                            var snapshotId = payload.GetProperty("snapshotId").GetGuid();
                            var json = await store.ReadAsync((db, cancellation) => db.Audit.AsNoTracking().Where(a => a.Id == snapshotId).Select(a => a.EventJson).SingleAsync(cancellation), token);
                            var previous = JsonSerializer.Deserialize<Contracts.AuditEvent>(json, Contracts.Wire.Options)!;
                            if (previous.EventType != "user.policy.snapshot" || previous.SubjectId != id.ToString("D")) throw new HubException("rejected", "Snapshot target mismatch");
                            patch = previous.Metadata.GetProperty("before");
                        }
                        var current = users.GetPolicy(id); var next = ManagedPolicy.Apply(current, patch);
                        var snapshot = await store.AppendAuditAsync(new Contracts.AuditEvent { EventId = Guid.NewGuid(), Source = "account-hub", EventType = "user.policy.snapshot", ActorUserId = caller.UserId, SubjectType = "user", SubjectId = id.ToString("D"), OccurredAt = DateTimeOffset.UtcNow, Metadata = JsonSerializer.SerializeToElement(new { before = ManagedPolicy.Project(current), after = ManagedPolicy.Project(next) }) }, token);
                        // Re-read current policy/admin status at the effect boundary. Never apply
                        // the pre-audit full-policy snapshot through the legacy preset copier.
                        try { await nativePolicies.WriteManagedAsync(id, patch, token); outcomes.Add(new { userId = id, status = "applied", snapshotId = snapshot.AuditId }); }
                        catch { outcomes.Add(new { userId = id, status = "remediation_pending", snapshotId = snapshot.AuditId }); }
                    }
                    result = new { status = "completed", outcomes };
                }
                else if (operation == "group.save")
                {
                    var name = payload.GetProperty("name").GetString()!.Trim();
                    if (name.Length is < 1 or > 100) throw new HubException("invalid_request", "Invalid group name");
                    _ = ManagedPolicy.Apply(new MediaBrowser.Model.Users.UserPolicy(), payload.GetProperty("managedFields"));
                    var id = payload.TryGetProperty("id", out var existing) && existing.ValueKind == JsonValueKind.String ? existing.GetString()! : Guid.NewGuid().ToString("N");
                    await lifecycle.UpsertPresetAsync(new PresetRecord { Id = id, Name = name, JellyfinPolicyJson = payload.GetProperty("managedFields").GetRawText(), CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, token);
                    result = new { status = "saved", id };
                }
                else if (operation == "group.delete")
                {
                    var id = payload.GetProperty("id").GetString()!;
                    if ((await lifecycle.GetUserPresetsAsync(token)).Values.Contains(id)) throw new HubException("rejected", "Group is assigned to users");
                    await lifecycle.DeletePresetAsync(id, token); result = new { status = "deleted" };
                }
                else if (operation == "settings.library")
                {
                    var value = payload.Deserialize<Jellyfin.LibraryNotifications.LibraryNoticeSettings>(Jellyfin.LibraryNotifications.LibraryNoticeFactory.Json) ?? throw new ArgumentException();
                    libraryNotices.SaveSettings(value); result = new { status = "saved" };
                }
                else if (operation == "settings.seerr")
                {
                    var value = payload.GetProperty("baseUrl").GetString()!;
                    var http = payload.GetProperty("allowHttp").GetBoolean(); var local = payload.GetProperty("allowPrivateNetwork").GetBoolean();
                    _ = SeerrDestination.ApiUri(value, "/user", http);
                    await settings.ApplyAsync(new SettingsUpdate { SeerrUrl = value.TrimEnd('/'), SeerrAllowHttp = http, SeerrAllowPrivateNetwork = local }, token);
                    result = new { status = "saved" };
                }
                else
                {
                    var value = payload.GetProperty("publicBaseUrl").GetString()!;
                    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw new HubException("invalid_request", "Use a public HTTPS base URL without credentials/query/fragment");
                    await settings.ApplyAsync(new SettingsUpdate { PublicBaseUrl = value.TrimEnd('/') }, token); result = new { status = "saved" };
                }
                return JsonSerializer.SerializeToElement(result, Contracts.Wire.Options);
            }, ct: ct);
    });
    [HttpPost("settings/secrets/{name}")]
    public Task<IActionResult> Secret(string name, JsonElement body, CancellationToken ct) => Run(async () =>
    {
        var caller = await host.RequireAdministratorAsync(HttpContext, ct);
        if (name is not ("security.webhookSecret" or "seerr.apiKey" or "webhook.secret")) throw new HubException("invalid_request", "Unsupported secret");
        var result = await secrets.ReplaceAsync(caller.UserId, body.GetProperty("commandId").GetGuid(), body.GetProperty("revision").GetInt64(), name,
            body.GetProperty("secret").ValueKind == JsonValueKind.Null ? null : body.GetProperty("secret").GetString(), token => host.AuthorizeAsync(HttpContext, CommandRisk.Configuration, token), ct);
        if (name == "seerr.apiKey") await settings.ReloadAsync(ct);
        return result;
    });
    [HttpPost("settings/retention")]
    public Task<IActionResult> Retention(JsonElement body, CancellationToken ct) => Run(async () =>
    {
        var caller = await host.RequireAdministratorAsync(HttpContext, ct);
        return await hubSettings.UpdateRetentionAsync(caller.UserId, body.GetProperty("commandId").GetGuid(), body.GetProperty("revision").GetInt64(),
            new(body.GetProperty("auditRetentionDays").GetInt32(), body.GetProperty("notificationRetentionDays").GetInt32()),
            token => host.AuthorizeAsync(HttpContext, CommandRisk.Configuration, token), ct);
    });
    [HttpPost("audit/query")]
    public Task<IActionResult> Audit(JsonElement body, CancellationToken ct) => Run(async () =>
    {
        await host.AuthorizeAsync(HttpContext, CommandRisk.Audit, ct);
        var skip = body.TryGetProperty("skip", out var from) ? from.GetInt32() : 0;
        var limit = body.TryGetProperty("limit", out var size) ? size.GetInt32() : 50;
        var type = body.TryGetProperty("eventType", out var eventType) ? eventType.GetString() : null;
        if (skip < 0 || limit is < 1 or > 200 || type?.Length > 100) throw new HubException("invalid_request", "Invalid audit filter");
        return await store.ReadAsync(async (db, token) =>
        {
            var query = db.Audit.AsNoTracking();
            if (!string.IsNullOrEmpty(type)) { var match = "\"eventType\":" + JsonSerializer.Serialize(type); query = query.Where(a => a.EventJson.Contains(match)); }
            return new { items = (await query.OrderByDescending(a => a.Sequence).Skip(skip).Take(limit).ToArrayAsync(token)).Select(a => new { sequence = a.Sequence, entryHash = a.EntryHash, previousHash = a.PreviousHash, @event = JsonSerializer.Deserialize<JsonElement>(a.EventJson) }).ToArray() };
        }, ct);
    });
    [HttpPost("audit/verify")]
    public Task<IActionResult> Verify(JsonElement body, CancellationToken ct) => Run(async () =>
    {
        await host.AuthorizeAsync(HttpContext, CommandRisk.Audit, ct);
        return await store.VerifyAuditAsync(ct);
    });
    [HttpPost("audit/export")]
    public Task<IActionResult> Export(JsonElement body, CancellationToken ct) => Run(async () =>
    {
        var caller = await host.RequireAdministratorAsync(HttpContext, ct); await host.AuthorizeAsync(HttpContext, CommandRisk.Audit, ct);
        await store.AppendAuditAsync(new Contracts.AuditEvent { EventId = Guid.NewGuid(), Source = "account-hub", EventType = "audit.exported", ActorUserId = caller.UserId, OccurredAt = DateTimeOffset.UtcNow }, ct);
        var rows = await store.ReadAsync((db, token) => db.Audit.AsNoTracking().OrderByDescending(a => a.Sequence).Take(10000).ToArrayAsync(token), ct);
        var format = body.GetProperty("format").GetString();
        if (format == "json") return new { format, content = "[" + string.Join(',', rows.Select(a => a.EventJson)) + "]" };
        if (format != "csv") throw new HubException("invalid_request", "Use csv or json");
        return new { format, content = "sequence,event,hash\r\n" + string.Join("\r\n", rows.Select(a => a.Sequence + "," + AuditCsv.Cell(a.EventJson) + "," + AuditCsv.Cell(a.EntryHash))) };
    });
    private async Task<IActionResult> Run<T>(Func<Task<T>> operation)
    {
        try { var data = await operation(); return new JsonResult(new { apiVersion = "1", ok = true, data }, Contracts.Wire.Options); }
        catch (SecurityFailure ex) { return Failure(ex.Code, ex.Status); }
        catch (SeerrException ex) { return Failure("seerr_validation_failed", ex.StatusCode); }
        catch (UserManagerException ex) { return Failure("lifecycle_request_failed", ex.StatusCode); }
        catch (HubException ex) { return Failure(ex.Code, ex.Code is "revision_changed" or "command_conflict" ? 409 : ex.Retryable ? 503 : 400); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or ArgumentException or InvalidOperationException) { return Failure("invalid_request", 400); }
    }
    private IActionResult Failure(string code, int status) => new JsonResult(new { apiVersion = "1", ok = false, error = new { code, message = code, retryable = status >= 500 } }, Contracts.Wire.Options) { StatusCode = status };
}
