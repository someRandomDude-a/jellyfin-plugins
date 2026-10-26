using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeletionQueue.Core;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.DeletionQueue.Api;

[ApiController, Authorize, Route("DeletionQueue/admin/account-hub/v1")]
[TypeFilter(typeof(QueueApiFilter)), TypeFilter(typeof(HubEnvelopeFilter)), RequestSizeLimit(32768)]
public sealed class HubAdapterController(QueueRuntime runtime, LiveIdentity identity, IUserManager users,
    IServerApplicationHost host, HubAvailability hub, EnhancedConnection enhanced, TimeProvider clock) : ControllerBase
{
    private Account Caller() => identity.Caller(User, administrator: true);
    private T Parse<T>(JsonElement body)
    { _ = Caller(); try { return HubWire.Parse<T>(body); } catch (JsonException) { throw new QueueException("invalid_request"); } }
    private IReadOnlyList<Account> Accounts()
    {
        return users.GetUsers().Select(u => identity.Account(u.Id)).ToArray();
    }
    internal static ContentResult Success(object data) => new() { ContentType = "application/json", StatusCode = 200,
        Content = HubWire.Serialize(new { apiVersion = "1", ok = true, data }) };

    [HttpGet("capabilities")]
    public IActionResult Capabilities()
    {
        _ = Caller();
        return Success(new { apiVersion = "1", plugin = "deletion-queue", pluginVersion = typeof(Plugin).Assembly.GetName().Version!.ToString(),
            features = new[] { "settings", "globalPolicy", "perUserPolicy", "preview", "reset", "adminExclusion" }, adminExclusion = true,
            limits = new { bodyBytes = 32768, users = 500, outbox = 2000, adapterReceipts = 2000 },
            fields = new {
                hubIntegrationEnabled = new { type = "boolean", @default = true }, publishAudit = new { type = "boolean", @default = true },
                deviceBell = new { type = "boolean", @default = true }, hubFallbackAfterMinutes = new { type = "integer", min = 1, max = 1440, @default = 15 },
                defaultAuthority = new { type = "enum", values = Enum.GetNames<ProposalAuthority>(), @default = "OwnRequests" },
                activityDays = new { type = "integer", min = 1, max = 365, @default = 14 }, delayHours = new { type = "integer", min = 48, max = 8760, @default = 48 },
                activityWaitTimeoutEnabled = new { type = "boolean", @default = false }, activityWaitTimeoutDays = new { type = "integer", min = 1, max = 365, @default = 45 },
                recentHistoryDays=new{type="integer",min=1,max=365,@default=7},
                automaticDeletion = new { type = "boolean", @default = false }, requireAdministratorApproval = new { type = "boolean", @default = false },
                seerrManagedDeletionAcknowledged = new { type = "boolean", @default = false }, suspended = new { type = "boolean", @default = false },
                authority = new { type = "nullable-enum", values = Enum.GetNames<ProposalAuthority>(), @default = (string?)null },
                notifications = new { type = "boolean", @default = true },
                seerr = new { baseUrl = new { type = "url", maxLength = 2048 },
                    apiKey = new { type = "string", maxLength = 512, writeOnly = true, nullable = true },
                    clearApiKey = new { type = "boolean", @default = false, requiresConfirmation = true } } } });
    }
    [HttpGet("status")]
    public IActionResult Status()
    {
        _ = Caller(); if (!runtime.Available) return Success(new { apiVersion = "1", health = "unavailable", hubAvailable = false });
        var s = runtime.Read(); return Success(new { apiVersion = "1", health = "ok", notificationMode = s.Hub.NotificationMode,
            hubAvailable = hub.Ready, revision = HubAdapter.Revision(s), proposalCounts = new { open = s.Proposals.Count(p => !p.Terminal),
                pending = s.Proposals.Count(p => p.Status == ProposalStatus.Blocked) }, outbox = new { pending = s.Outbox.Count(e => e.Status is "pending" or "delivering"),
                    deadLetters = s.Outbox.Count(e => e.Status == "dead_letter"), localFallbacks = s.Outbox.Count(e => e.Status == "local_fallback"),
                    deliveryMetadataRolledOff = s.HubDeliveryMetadataRolledOff } });
    }
    [HttpGet("settings")]
    public IActionResult Settings() { _ = Caller(); return Success(HubAdapter.Settings(runtime.Read())); }
    [HttpGet("policy")]
    public IActionResult Policy([FromQuery] int skip = 0, [FromQuery] int limit = 50) { _ = Caller(); return Success(HubAdapter.Policy(runtime.Read(), Accounts(), skip, limit)); }
    [HttpGet("users/{id:guid}/effective-policy")]
    public IActionResult Effective(Guid id)
    { _ = Caller(); var account = HubAdapter.Find(Accounts(), id); var s = runtime.Read(); return Success(new { revision = HubAdapter.Revision(s), readOnly = account.Administrator, policy = HubAdapter.Effective(s, id), user = HubAdapter.UserView(s, account) }); }

    private IActionResult Write(Guid command, string revision, string operation, object input, Func<QueueState, IReadOnlyList<Account>, object> apply)
    {
        var caller = Caller();
        var headerRevision = Request.Headers.IfMatch.ToString().Trim('"');
        revision ??= headerRevision;
        if (headerRevision.Length > 0 && headerRevision != revision) throw new QueueException("invalid_request");
        if (command == Guid.Empty || revision is null || revision.Length != 64) throw new QueueException("invalid_request");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operation + "|" + revision + "|" + HubWire.Serialize(input))));
        string? Replay(QueueState s)
        {
            if (!s.AdapterReceipts.TryGetValue(command, out var receipt)) return null;
            if (receipt.ActorId != caller.Id || receipt.InputHash != hash) throw new QueueException("command_conflict");
            return receipt.ResultJson;
        }
        var initial = runtime.Read(); var replay = Replay(initial);
        if (replay is not null) return new ContentResult { ContentType = "application/json", StatusCode = 200, Content = replay };
        var kind = operation == "reset" || operation.StartsWith("reset-user:", StringComparison.Ordinal) ? "AccountHubPolicyReset" : "AccountHubAdapterChanged";
        var result = runtime.Write(Guid.NewGuid(), caller.Id.ToString(), kind, s =>
        {
            _ = Caller(); var existing = Replay(s); if (existing is not null) return existing;
            if (HubAdapter.Revision(s) != revision) throw new QueueException("revision_changed");
            if (s.AdapterReceipts.Count >= 2000) throw new QueueException("command_history_full");
            var data = apply(s, Accounts());
            var json = Success(data).Content!;
            s.AdapterReceipts.Add(command, new(caller.Id, hash, json)); return json;
        });
        return new ContentResult { ContentType = "application/json", StatusCode = 200, Content = result };
    }
    [HttpPut("settings")]
    public IActionResult Settings(JsonElement body)
    { var r = Parse<HubSettingsRequest>(body); return Write(r.CommandId, r.Revision, "settings", r, (s, _) => { HubAdapter.SettingsChange(s, r, host.SystemId, clock.GetUtcNow()); return HubAdapter.Settings(s); }); }
    [HttpPost("settings/import-enhanced")]
    public IActionResult Import(JsonElement body)
    {
        var r = Parse<HubPolicyRequest>(body);
        return Write(r.CommandId, r.Revision, "import-enhanced", r, (s, _) => {
            var found = enhanced.Discover() ?? throw new QueueException("enhanced_unavailable");
            HubAdapter.SettingsChange(s, new(r.Revision, r.CommandId, Seerr: new(found.Url, found.Key)), host.SystemId, clock.GetUtcNow()); return HubAdapter.Settings(s); });
    }
    [HttpPut("policy")]
    public IActionResult Policy(JsonElement body)
    { var r = Parse<HubPolicyRequest>(body); return Write(r.CommandId, r.Revision, "policy", r, (s, accounts) => HubAdapter.ApplyPolicy(s, r, accounts)); }
    [HttpPost("policy/reset")]
    public IActionResult Reset(JsonElement body)
    { var r = Parse<HubResetRequest>(body); return Write(r.CommandId, r.Revision, "reset", r, (s, accounts) => HubAdapter.Reset(s, r, accounts)); }
    [HttpPost("policy/preview")]
    public IActionResult Preview(JsonElement body)
    {
        _ = Caller(); var r = Parse<HubPreviewRequest>(body);
        return Success(HubAdapter.Preview(runtime.Read(), r, Accounts(), host.SystemId, clock.GetUtcNow()));
    }
    [HttpPut("users/{id:guid}/policy")]
    public IActionResult UserPolicy(Guid id, JsonElement body)
    {
        var r = Parse<HubUserRequest>(body);
        return Write(r.CommandId, r.Revision, "user:" + id, r, (s, accounts) => {
            if (HubAdapter.Find(accounts, id).Administrator) throw new QueueException("admin_target_read_only");
            if (r.Policy is null) throw new QueueException("invalid_request");
            return HubAdapter.ApplyPolicy(s, new(r.Revision, r.CommandId, Users: [new(id, r.Policy)]), accounts); });
    }
    [HttpDelete("users/{id:guid}/policy")]
    public IActionResult ResetUser(Guid id)
    {
        var revision = Request.Headers.IfMatch.ToString().Trim('"');
        if (!Guid.TryParse(Request.Headers["X-Command-Id"], out var command)) throw new QueueException("invalid_request");
        return Write(command, revision, "reset-user:" + id, new { revision, command }, (s, accounts) => {
            if (HubAdapter.Find(accounts, id).Administrator) throw new QueueException("admin_target_read_only");
            return HubAdapter.Reset(s, new(revision, command, "users", [id]), accounts); });
    }
}

// Preserve upstream Security middleware responses; normalize only this adapter's MVC results.
public sealed class HubEnvelopeFilter(QueueRuntime runtime) : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is ContentResult) return;
        var status = context.Result is ObjectResult o ? o.StatusCode ?? 500 : context.Result is StatusCodeResult s ? s.StatusCode : 500;
        var code = status switch { 400 => "invalid_request", 401 => "unauthorized", 403 => "forbidden", 404 => "not_found", 429 => "rate_limited", _ => "unavailable" };
        if (context.Result is ObjectResult { Value: { } value })
        {
            var doc = JsonSerializer.SerializeToElement(value);
            if (doc.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
            {
                code = c.GetString()!;
                code = System.Text.RegularExpressions.Regex.Replace(code, "([a-z])([A-Z])", "$1_$2").ToLowerInvariant();
                if (code == "admin_target_read_only") status = 403;
                if (code == "invalid_request") status = 400;
            }
        }
        object? details = null;
        if (code == "revision_changed") { try { details = new { revision = HubAdapter.Revision(runtime.Read()) }; } catch (Exception) { } }
        context.Result = new ContentResult { ContentType = "application/json", StatusCode = status,
            Content = HubWire.Serialize(new { apiVersion = "1", ok = false, error = new { code, message = "Operation unavailable; review authorization, inputs and revision.", retryable = status >= 500, details } }) };
    }
    public void OnResultExecuted(ResultExecutedContext context) { }
}
