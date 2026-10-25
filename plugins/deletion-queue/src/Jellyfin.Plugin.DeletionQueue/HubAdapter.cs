using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeletionQueue.Core;

namespace Jellyfin.Plugin.DeletionQueue;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HubPolicyPatch(ProposalAuthority? DefaultAuthority = null, int? ActivityDays = null, int? DelayHours = null,
    bool? AutomaticDeletion = null, bool? RequireAdministratorApproval = null, bool? SeerrManagedDeletionAcknowledged = null, bool? Suspended = null)
{
    public Policy Apply(Policy p) => new() { Accounts = new(p.Accounts), DefaultAuthority = DefaultAuthority ?? p.DefaultAuthority,
        ActivityDays = ActivityDays ?? p.ActivityDays, DelayHours = DelayHours ?? p.DelayHours, AutomaticDeletion = AutomaticDeletion ?? p.AutomaticDeletion,
        RequireAdministratorApproval = RequireAdministratorApproval ?? p.RequireAdministratorApproval,
        SeerrManagedDeletionAcknowledged = SeerrManagedDeletionAcknowledged ?? p.SeerrManagedDeletionAcknowledged, Suspended = Suspended ?? p.Suspended };
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HubPreferencesPatch(int DelayHours, bool Notifications);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HubUserPolicy(ProposalAuthority? Authority, HubPreferencesPatch? NotificationPreferences = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HubUserChange(Guid UserId, HubUserPolicy Policy);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HubPolicyRequest(string Revision, Guid CommandId, HubPolicyPatch? Defaults = null, HubUserChange[]? Users = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HubPreviewRequest(Guid[] UserIds, HubPolicyPatch? Changes = null, string? Revision = null,
    HubUserChange[]? Users = null, HubSettingsRequest? Settings = null, string? ResetScope = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HubResetRequest(string Revision, Guid CommandId, string Scope, Guid[]? UserIds = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HubUserRequest(string Revision, Guid CommandId, HubUserPolicy Policy);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HubSeerrPatch(string? BaseUrl = null, string? ApiKey = null, bool ClearApiKey = false, bool ConfirmClear = false);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HubSettingsRequest(string Revision, Guid CommandId, NotificationMode? NotificationMode = null,
    bool? HubIntegrationEnabled = null, int? HubFallbackAfterMinutes = null, bool? PublishAudit = null, bool? DeviceBell = null,
    HubSeerrPatch? Seerr = null);

public static class HubWire
{
    public static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new HubGuidConverter(), new JsonStringEnumConverter(allowIntegerValues: false) }, MaxDepth = 16 };
    public static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
    public static T Parse<T>(JsonElement value) => value.Deserialize<T>(Json) ?? throw new QueueException("invalid_request");
}

public sealed class HubGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Guid.TryParse(reader.GetString(), out var id) ? id : throw new JsonException("Invalid identifier.");
    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString("D"));
    public override Guid ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        Guid.TryParse(reader.GetString(), out var id) ? id : throw new JsonException("Invalid identifier.");
    public override void WriteAsPropertyName(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) => writer.WritePropertyName(value.ToString("D"));
}

public static class HubAdapter
{
    public static string Revision(QueueState s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{s.Policy.Revision}|{s.Seerr.Revision}|{s.Hub.Revision}|" + string.Join('|', s.Preferences.OrderBy(p => p.Key).Select(p => $"{p.Key:D}:{p.Value.Revision}")))));

    public static object Defaults(Policy p) => new { p.DefaultAuthority, p.ActivityDays, p.DelayHours, p.AutomaticDeletion,
        p.RequireAdministratorApproval, p.SeerrManagedDeletionAcknowledged, p.Suspended };
    public static object Effective(QueueState s, Guid id) => new { userId = id, authority = QueueRules.Authority(s, id),
        source = s.Policy.Accounts.ContainsKey(id) ? "override" : "default", notificationPreferences = new {
            delayHours = s.Preferences.GetValueOrDefault(id)?.DelayHours ?? 48, notifications = s.Preferences.GetValueOrDefault(id)?.Notifications ?? true },
        warnings = new[] { "Global activity, suspension, consent, approval and accumulated delay floors apply to everyone." } };

    public static object Settings(QueueState s) => new { revision = Revision(s), s.Hub.NotificationMode, s.Hub.HubIntegrationEnabled,
        s.Hub.HubFallbackAfterMinutes, s.Hub.PublishAudit, s.Hub.DeviceBell,
        seerr = new { s.Seerr.BaseUrl, s.Seerr.JellyfinServerId, keyConfigured = s.Seerr.ApiKey.Length > 0, health = "not_probed" } };

    public static void ValidateUsers(Guid[] ids)
    { if (ids.Length > 500 || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length) throw new QueueException("invalid_request"); }

    public static object UserView(QueueState s, Account a) => new { userId = a.Id, administrator = a.Administrator, readOnly = a.Administrator,
        @override = s.Policy.Accounts.ContainsKey(a.Id) || s.Preferences.ContainsKey(a.Id),
        policy = new HubUserPolicy(s.Policy.Accounts.TryGetValue(a.Id, out var authority) ? authority : null,
            new(s.Preferences.GetValueOrDefault(a.Id)?.DelayHours ?? 48, s.Preferences.GetValueOrDefault(a.Id)?.Notifications ?? true)),
        defaults = new { authority = s.Policy.DefaultAuthority, notificationPreferences = new { delayHours = 48, notifications = true } },
        effective = Effective(s, a.Id), sources = new { authority = s.Policy.Accounts.ContainsKey(a.Id) ? "override" : "default",
            notificationPreferences = s.Preferences.ContainsKey(a.Id) ? "override" : "default" } };

    public static object Policy(QueueState s, IReadOnlyList<Account> accounts, int skip = 0, int limit = 50)
    {
        if (skip < 0 || limit is < 1 or > 100) throw new QueueException("invalid_request");
        return new { revision = Revision(s), defaults = Defaults(s.Policy), total = accounts.Count, skip, limit,
            users = accounts.OrderBy(a => a.Id).Skip(skip).Take(limit).Select(a => UserView(s, a)).ToArray() };
    }

    // Preview always works on a detached native snapshot. Never expose proposal evidence, voters or subscriptions.
    public static object Preview(QueueState current, HubPreviewRequest r, IReadOnlyList<Account> accounts, string serverId, DateTimeOffset now)
    {
        if (r.UserIds is null) throw new QueueException("invalid_request");
        ValidateUsers(r.UserIds);
        if (r.Revision is not null && r.Revision != Revision(current)) throw new QueueException("revision_changed");
        foreach (var id in r.UserIds) _ = Find(accounts, id);
        var next = JsonSerializer.Deserialize<QueueState>(JsonSerializer.Serialize(current))!;
        object effects;
        if (r.ResetScope is not null)
        {
            if (r.Changes is not null || r.Users is not null || r.Settings is not null) throw new QueueException("invalid_request");
            effects = Reset(next, new(Revision(current), Guid.NewGuid(), r.ResetScope, r.UserIds), accounts);
        }
        else
        {
            if (r.Changes is null && r.Users is null && r.Settings is null) throw new QueueException("invalid_request");
            if (r.Users is not null && r.Users.Any(u => !r.UserIds.Contains(u.UserId))) throw new QueueException("invalid_request");
            effects = r.Changes is not null || r.Users is not null ? ApplyPolicy(next, new(Revision(current), Guid.NewGuid(), r.Changes, r.Users), accounts) : new { safetyEffects = "Native integration settings validation applies." };
            if (r.Settings is not null) SettingsChange(next, r.Settings, serverId, now);
        }
        var affected = r.ResetScope == "all" ? accounts.Select(a => a.Id).ToArray() : r.UserIds;
        return new { revision = Revision(current), preview = true, currentDefaults = Defaults(current.Policy), desiredDefaults = Defaults(next.Policy),
            currentSettings = Settings(current), desiredSettings = Settings(next),
            totalAffectedUsers = affected.Length, usersTruncated = affected.Length > 100,
            users = affected.Take(100).Select(id => new { userId = id, before = UserView(current, Find(accounts, id)), after = UserView(next, Find(accounts, id)) }).ToArray(),
            skippedAdmins = accounts.Where(a => a.Administrator && (r.UserIds.Contains(a.Id) || r.Changes is not null || r.ResetScope is "all" or "global")).Select(a => a.Id).ToArray(),
            proposalEffects = new { revalidated = next.Proposals.Count(p => !p.Terminal && p.Claim is null), blocked = next.Proposals.Count(p => !p.Terminal && p.Status == ProposalStatus.Blocked) },
            warnings = new[] { "Native safety revalidation applies; consent, claims and accumulated deadline floors are retained. Global hard safety settings apply to administrators too." }, effects };
    }

    public static object ApplyPolicy(QueueState state, HubPolicyRequest request, IReadOnlyList<Account> accounts)
    {
        var changes = request.Users ?? [];
        if (changes.Any(c => c is null)) throw new QueueException("invalid_request");
        ValidateUsers(changes.Select(c => c.UserId).ToArray());
        var skipped = changes.Where(c => Find(accounts, c.UserId).Administrator).Select(c => c.UserId).ToArray();
        var p = request.Defaults?.Apply(state.Policy) ?? new HubPolicyPatch().Apply(state.Policy);
        // Protect excluded admins from indirect global-default authority changes as well as explicit bulk edits.
        foreach (var admin in accounts.Where(a => a.Administrator))
            if (p.Accounts.GetValueOrDefault(admin.Id, p.DefaultAuthority) != QueueRules.Authority(state, admin.Id))
                p.Accounts[admin.Id] = QueueRules.Authority(state, admin.Id);
        foreach (var change in changes.Where(c => !skipped.Contains(c.UserId)))
        {
            if (change.Policy is null) throw new QueueException("invalid_request");
            ValidateUserPolicy(change.Policy);
            if (change.Policy.Authority is { } authority) p.Accounts[change.UserId] = authority; else p.Accounts.Remove(change.UserId);
        }
        QueueRules.UpdatePolicy(state, p, state.Policy.Revision);
        foreach (var c in changes.Where(c => !skipped.Contains(c.UserId) && c.Policy.NotificationPreferences is not null))
        {
            var preferences = c.Policy.NotificationPreferences!;
            QueueRules.UpdatePreferences(state, c.UserId, state.Preferences.GetValueOrDefault(c.UserId)?.Revision ?? 0, preferences.DelayHours, preferences.Notifications);
        }
        return new { revision = Revision(state), skippedAdmins = skipped, skippedAdminCount = skipped.Length,
            protectedAdmins = accounts.Where(a => a.Administrator).Select(a => a.Id).ToArray(),
            defaults = Defaults(state.Policy), safetyEffects = "Native proposals re-evaluated; delay floors never shortened. Global hard safety settings apply to excluded admins." };
    }

    public static object Reset(QueueState state, HubResetRequest request, IReadOnlyList<Account> accounts)
    {
        if (request.Scope is not ("all" or "global" or "users")) throw new QueueException("invalid_request");
        var ids = request.Scope == "all" || request.UserIds is null ? accounts.Select(a => a.Id).ToArray() : request.UserIds;
        if (request.Scope != "all" && request.UserIds is not null) ValidateUsers(ids);
        foreach (var id in ids) _ = Find(accounts, id);
        var skipped = accounts.Where(a => a.Administrator && (ids.Contains(a.Id) || request.Scope == "global")).Select(a => a.Id).ToArray();
        var policy = request.Scope is "all" or "global" ? new Policy() : new HubPolicyPatch().Apply(state.Policy);
        policy.Accounts = new(state.Policy.Accounts);
        foreach (var admin in accounts.Where(a => a.Administrator))
            if (policy.Accounts.GetValueOrDefault(admin.Id, policy.DefaultAuthority) != QueueRules.Authority(state, admin.Id))
                policy.Accounts[admin.Id] = QueueRules.Authority(state, admin.Id);
        if (request.Scope is "all" or "users")
            foreach (var id in ids.Except(skipped)) policy.Accounts.Remove(id);
        QueueRules.UpdatePolicy(state, policy, state.Policy.Revision);
        if (request.Scope is "global" or "all") state.Hub = new HubSettings { Revision = state.Hub.Revision + 1 };
        if (request.Scope is "all" or "users")
        foreach (var id in ids.Except(skipped))
        {
            QueueRules.UpdatePreferences(state, id, state.Preferences.GetValueOrDefault(id)?.Revision ?? 0, 48, true);
            state.Preferences[id].Subscriptions.Clear();
        }
        return new { revision = Revision(state), scope = request.Scope, skippedAdmins = skipped, skippedAdminCount = skipped.Length,
            safetyEffects = "Proposals, claims, consent, credentials and deadline floors retained; native safety revalidation applied." };
    }

    public static void ValidateUserPolicy(HubUserPolicy p)
    {
        if (p.Authority is { } a && !Enum.IsDefined(a) || p.NotificationPreferences?.DelayHours is < 48 or > 8760)
            throw new QueueException("invalid_request");
    }
    public static Account Find(IReadOnlyList<Account> accounts, Guid id) => accounts.SingleOrDefault(a => a.Id == id) ?? throw new QueueException("NotFound");

    public static void SettingsChange(QueueState s, HubSettingsRequest r, string serverId, DateTimeOffset now)
    {
        var h = s.Hub;
        if (r.NotificationMode is { } mode && !Enum.IsDefined(mode) || r.HubFallbackAfterMinutes is < 1 or > 1440) throw new QueueException("invalid_request");
        if (r.Seerr is { } seerr)
        {
            var url = seerr.BaseUrl ?? s.Seerr.BaseUrl;
            var key = seerr.ApiKey ?? s.Seerr.ApiKey;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || url.Length > 2048 ||
                uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Query.Length > 0 || key.Length > 512 || key.Any(c => c < 33 || c > 126) ||
                seerr.ClearApiKey && (!seerr.ConfirmClear || seerr.ApiKey is not null)) throw new QueueException("invalid_request");
            var normalizedUrl = uri.AbsoluteUri.TrimEnd('/'); var replacementKey = seerr.ClearApiKey ? "" : key;
            if (s.Seerr.BaseUrl != normalizedUrl || s.Seerr.ApiKey != replacementKey || s.Seerr.JellyfinServerId != serverId)
            {
                s.Seerr.BaseUrl = normalizedUrl; s.Seerr.ApiKey = replacementKey;
                s.Seerr.JellyfinServerId = serverId; s.Seerr.Revision++;
                QueueRules.BreakCoverage(s, "IntegrationChanged", now);
            }
        }
        // Retain the legacy request field for wire compatibility, not as a setting.
        h.NotificationMode = NotificationMode.Auto; h.HubIntegrationEnabled = r.HubIntegrationEnabled ?? h.HubIntegrationEnabled;
        h.HubFallbackAfterMinutes = r.HubFallbackAfterMinutes ?? h.HubFallbackAfterMinutes;
        h.PublishAudit = r.PublishAudit ?? h.PublishAudit; h.DeviceBell = r.DeviceBell ?? h.DeviceBell; h.Revision++;
    }
}
