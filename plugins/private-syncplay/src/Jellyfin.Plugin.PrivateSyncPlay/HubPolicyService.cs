using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.PrivateSyncPlay;

public sealed record HubUser(Guid Id, string Name, bool Administrator, bool Enabled = true);

// One projection over Configuration.Users. No separate policy database, party mutation, or Hub auth dependency.
public sealed class HubPolicyService(Func<Configuration> read, Action<Configuration> save, Func<HubUser[]> directory, PartyStore store)
{
    public static readonly string[] SettingsFields = ["enablePrivateGroups", "allowPublicGroups", "defaultMode", "enableGuestInvites", "defaultGuestPermission", "maxViewers", "maxInviteUses", "guestLeaseSeconds", "guestVideoBitrateKbps", "maxGuestStreams", "guestPlayer", "notificationMode", "hubIntegrationEnabled", "publishAudit", "nativeNotifications"];
    private static readonly string[] Defaults = ["enablePrivateGroups", "allowPublicGroups", "defaultMode", "enableGuestInvites", "defaultGuestPermission"];
    private static readonly string[] UserFields = ["defaultMode", "createPublic", "createPrivate", "guestInvites"];
    internal event Action<Guid, JsonObject>? Committed;
    public object Settings() { lock (Plugin.SettingsGate) { var c = read();return new { revision = HubWire.Revision(c), settings = Project(c, SettingsFields) }; } }
    public object Policy(int skip, int limit)
    {
        if (skip < 0 || limit is < 1 or > 100) throw Invalid();
        lock (Plugin.SettingsGate)
        {
            var c = read();var all = directory().OrderBy(u => u.Id).ToArray();
            return new { revision = HubWire.Revision(c), defaults = Project(c, Defaults), total = all.Length, skip, limit,
                users = all.Skip(skip).Take(limit).Select(u => UserView(c, u)).ToArray() };
        }
    }
    public object Effective(Guid id)
    {
        lock (Plugin.SettingsGate) { var c = read();return new { revision = HubWire.Revision(c), user = UserView(c, FindUser(id)) }; }
    }
    private HubUser FindUser(Guid id) => directory().FirstOrDefault(u => u.Id == id) ?? throw new HubAdapterException(404, "invalid_request", "User not found.");
    public bool IsAdministrator(Guid id) => directory().Any(u => u.Id == id && u.Administrator && u.Enabled);
    public Guid[] Administrators() => directory().Where(u => u.Administrator).Select(u => u.Id).ToArray();
    internal void ProtectNative(Configuration before, Configuration next) => ProtectAdministrators(before, next, directory(), [], []);

    public object Capabilities() => new
    {
        apiVersion = "1", pluginVersion = typeof(Plugin).Assembly.GetName().Version!.ToString(),
        features = new { settings = true, globalPolicy = true, perUserPolicy = true, preview = true, reset = true, adminExclusion = true, guestPlayer = true },
        adminExclusion = true, settingsFields = SettingsFields, policyFields = UserFields,
        factoryDefaults = Project(new Configuration(), SettingsFields), globalPolicyFields = Defaults,
        enums = new { defaultMode = new[] { "Public", "Private" }, permission = new[] { "Inherit", "Allow", "Deny" }, notificationMode = new[] { "Auto", "Local", "Hub", "Disabled" } },
        limits = new { maxViewers = new[] { 1, 1000 }, maxInviteUses = new[] { 1, 10000 }, guestLeaseSeconds = new[] { 10, 120 }, guestVideoBitrateKbps = new[] { 256, 10000 }, maxGuestStreams = new[] { 1, 32 }, policyPageSize = 100, commandRetentionDays = 30, maxRetainedCommands = 512 },
        guestPlayer = new { features = GuestPlayerPolicy.Features, maxControls = 24, controlIdPattern = "^[a-z][a-z0-9.-]{0,63}$", allowArbitraryHtml = false },
        semantics = new { inheritDefaultMode = "null", inheritPermission = "Inherit", nativeInPartySafetyFeedbackAlways = true,
            hardSafetySwitchesApplyToAdministrators = true, notificationFreshnessSeconds = 120, auditRetryDays = 30,
            resetLiveRevocation = "rejected: use an explicit settings change after reviewing preview", contractBaseline = "plan-defined-v1; canonical SDK/host acceptance pending" }
    };

    public JsonNode Execute(Guid actor, string operation, JsonObject request, string? match = null, string? commandHeader = null, Guid? user = null, bool preview = false)
    {
        lock (Plugin.SettingsGate)
        lock (store.Gate)
        {
            // Re-read elevation for every call, including completed retries; no caller-supplied actor is trusted.
            if (!IsAdministrator(actor)) throw new HubAdapterException(403, "administrator_protected", "A current Jellyfin administrator is required.");
            var current = read();
            var fields = operation switch
            {
                "settings" => new[] { "revision", "commandId", "settings" },
                "policy" => new[] { "revision", "commandId", "defaults", "users" },
                "reset" => new[] { "revision", "commandId", "scope", "userIds" },
                "user-put" => new[] { "revision", "commandId", "policy" },
                "user-delete" => Array.Empty<string>(),
                "preview" => new[] { "revision", "userIds", "changes", "resetScope" },
                _ => throw Invalid()
            };
            Only(request, fields);
            var bodyRevision = Text(request, "revision");
            match = match?.Trim().Trim('"');
            if (match is not null && bodyRevision is not null && match != bodyRevision) throw Invalid();
            var expected = bodyRevision ?? match;
            if (string.IsNullOrWhiteSpace(expected)) throw Invalid("A revision is required.");
            var bodyCommand = Text(request, "commandId");
            if (bodyCommand is not null && commandHeader is not null && bodyCommand != commandHeader) throw Invalid();
            var command = Guid.Empty;
            if (!preview && (!Guid.TryParse(bodyCommand ?? commandHeader, out command) || command == Guid.Empty)) throw Invalid("A commandId is required.");
            var hash = HubWire.Hash(operation + ":" + user + ":" + Canonical(request) + ":" + expected);
            var old = current.HubCommands.FirstOrDefault(c => c.Actor == actor && c.CommandId == command && c.Operation == operation + ":" + user);
            if (!preview && old is not null)
            {
                if (old.Hash != hash) throw new HubAdapterException(409, "command_conflict", "Command ID was used with a different request.");
                return JsonNode.Parse(old.Result)!;
            }
            if (expected != HubWire.Revision(current)) throw new HubAdapterException(409, "revision_changed", "Settings changed. Reload and preview again.");
            var next = HubWire.Clone(current);var skipped = new HashSet<Guid>();var warnings = new List<string>();var changedUsers = new HashSet<Guid>();
            var all = directory();
            void ChangeUser(Guid id, JsonObject? policy)
            {
                var target = all.FirstOrDefault(u => u.Id == id) ?? throw Invalid("Unknown user.");
                if (target.Administrator)
                {
                    if (user.HasValue) throw new HubAdapterException(403, "administrator_protected", "Administrator policy is protected.");
                    skipped.Add(id);return;
                }
                next.Users.RemoveAll(u => u.UserId == id);
                if (policy is not null) { Only(policy, UserFields);var value = policy.Deserialize<UserSettings>(HubWire.Json)!;value.UserId = id;next.Users.Add(value); }
                changedUsers.Add(id);
            }
            var resetScope = operation == "reset" ? Text(request, "scope") : Text(request, "resetScope");
            if (resetScope is not null)
            {
                if (resetScope is not ("global" or "users" or "all")) throw Invalid("Unknown reset scope.");
                if (resetScope is "global" or "all") Apply(next, Project(new Configuration(), SettingsFields), SettingsFields);
                if (resetScope is "users" or "all")
                {
                    var ids = resetScope == "all" ? all.Select(u => u.Id).ToArray() : Ids(request);
                    if (resetScope == "users" && ids.Length == 0) throw Invalid("Select users to reset.");
                    foreach (var id in ids) ChangeUser(id, null);
                }
            }
            else if (operation == "reset") throw Invalid("A reset scope is required.");
            if (operation == "settings") Apply(next, Object(request, "settings", true)!, SettingsFields);
            if (operation == "policy")
            {
                if (request["defaults"] is JsonObject defaults) Apply(next, defaults, Defaults);
                else if (request.ContainsKey("defaults")) throw Invalid();
                if (request["users"] is JsonArray users)
                {
                    if (users.Count > 1000) throw Invalid();
                    var seen = new HashSet<Guid>();
                    foreach (var item in users)
                    {
                        if (item is not JsonObject row) throw Invalid();Only(row, ["userId", "policy"]);
                        if (!Guid.TryParse(Text(row, "userId"), out var id) || !seen.Add(id)) throw Invalid();
                        ChangeUser(id, Object(row, "policy", true));
                    }
                }
                else if (request.ContainsKey("users")) throw Invalid();
                if (!request.ContainsKey("defaults") && !request.ContainsKey("users")) throw Invalid();
            }
            if (operation == "user-put") ChangeUser(user!.Value, Object(request, "policy", true));
            if (operation == "user-delete") ChangeUser(user!.Value, null);
            if (operation == "preview" && request["changes"] is JsonObject changes)
            {
                Only(changes, ["settings", "defaults", "policy"]);
                if (changes["settings"] is JsonObject settings) Apply(next, settings, SettingsFields);
                if (changes["defaults"] is JsonObject defaults) Apply(next, defaults, Defaults);
                if (changes["policy"] is JsonObject policy) foreach (var id in Ids(request)) ChangeUser(id, policy);
                if (changes.Any(p => p.Value is not JsonObject)) throw Invalid();
            }
            else if (operation == "preview" && request.ContainsKey("changes")) throw Invalid();
            ProtectAdministrators(current, next, all, skipped, warnings);
            try { next.Validate(); } catch (ArgumentException) { throw Invalid("The proposed settings or effective default mode are invalid."); }
            var effects = Effects(current, next);
            warnings.AddRange(effects);
            if (!preview && resetScope is not null && effects.Contains("Live guest invitations or leases would be revoked."))
                throw Invalid("Reset would revoke live guest access. Use an explicit settings change after reviewing preview.");
            var diff = SettingsFields.Where(f => !JsonNode.DeepEquals(Project(current, [f])[f], Project(next, [f])[f]))
                .Select(f => new { field = f, before = Project(current, [f])[f], after = Project(next, [f])[f] }).ToArray();
            var data = HubWire.Node(new { revision = HubWire.Revision(current), scope = resetScope ?? operation, changedFields = diff,
                changedUsers = changedUsers.Order().Take(100).ToArray(), changedUsersTruncated = changedUsers.Count > 100,
                skippedAdmins = skipped.Order().Take(100).ToArray(), skippedAdminCount = skipped.Count, warnings = warnings.Distinct().ToArray(),
                totalAffectedUsers = changedUsers.Count, usersTruncated = changedUsers.Count > 50,
                users = all.Where(u => changedUsers.Contains(u.Id)).OrderBy(u => u.Id).Take(50).Select(u => new { userId = u.Id, before = UserView(current, u), after = UserView(next, u) }).ToArray(), preview });
            if (preview) return data;
            next.PartyPolicyRevision = Guid.NewGuid().ToString("N");data["revision"] = next.PartyPolicyRevision;
            next.HubCommands = current.HubCommands.Where(c => c.CreatedAt > DateTime.UtcNow.AddDays(-30)).ToList();
            if (next.HubCommands.Count >= 512) throw new HubAdapterException(503, "unavailable", "Command retention capacity reached; retry after retention expires.");
            // Save the receipt with the settings in the SAME native XML configuration commit.
            var stored = data.ToJsonString();if (stored.Length > 131072) throw Invalid("Result too large; use a smaller user batch.");
            if (next.HubCommands.Sum(c => (long)System.Text.Encoding.UTF8.GetByteCount(c.Result)) + System.Text.Encoding.UTF8.GetByteCount(stored) > 8 * 1024 * 1024)
                throw new HubAdapterException(503, "unavailable", "Command retention size reached; retry after retention expires.");
            next.HubCommands.Add(new() { Actor = actor, CommandId = command, Operation = operation + ":" + user, Hash = hash, Result = stored, CreatedAt = DateTime.UtcNow });
            save(next);
            try { Committed?.Invoke(actor, (JsonObject)data.DeepClone()); } catch { /* Integration failure cannot undo a completed configuration write. */ }
            return data;
        }
    }

    private string[] Effects(Configuration old, Configuration next)
    {
        var result = new List<string>();
        lock (store.Gate)
        {
            if (store.Parties.Values.Any(p => !next.CanInviteGuests(p.Owner) && store.Invites(p.Id).Any(i => i.AllowGuests))
                || store.Parties.Values.SelectMany(p => store.Guests(p.Id)).Any(g => store.Now - g.LastSeen > TimeSpan.FromSeconds(next.GuestLeaseSeconds)))
                result.Add("Live guest invitations or leases would be revoked.");
        }
        if (old.MaxViewers != next.MaxViewers) result.Add("Viewer cap applies to future admissions; existing signed-in members are not removed.");
        if (old.MaxGuestStreams != next.MaxGuestStreams) result.Add("Guest stream maintenance applies the new live stream ceiling.");
        if (old.NotificationMode != next.NotificationMode) result.Add("Notification routing mode changes; native in-party safety feedback remains enabled.");
        if (old.EnableGuestInvites != next.EnableGuestInvites || old.AllowPublicGroups != next.AllowPublicGroups || old.EnablePrivateGroups != next.EnablePrivateGroups)
            result.Add("Global hard safety switches affect administrators too and cannot be overridden per user.");
        return result.ToArray();
    }
    private static void ProtectAdministrators(Configuration before, Configuration next, HubUser[] all, HashSet<Guid> skipped, List<string> warnings)
    {
        if (JsonNode.DeepEquals(Project(before, Defaults), Project(next, Defaults))) return;
        foreach (var user in all.Where(u => u.Administrator))
        {
            skipped.Add(user.Id);var prior = before.Users.FirstOrDefault(u => u.UserId == user.Id);
            var value = new UserSettings { UserId = user.Id,
                CreatePublic = before.CanCreate(user.Id, GroupMode.Public) ? PermissionOverride.Allow : PermissionOverride.Deny,
                CreatePrivate = before.CanCreate(user.Id, GroupMode.Private) ? PermissionOverride.Allow : PermissionOverride.Deny,
                GuestInvites = (prior?.GuestInvites ?? PermissionOverride.Inherit) switch { PermissionOverride.Allow => PermissionOverride.Allow, PermissionOverride.Deny => PermissionOverride.Deny, _ => before.DefaultGuestPermission ? PermissionOverride.Allow : PermissionOverride.Deny } };
            var mode = prior?.DefaultMode ?? before.DefaultMode;
            next.Users.RemoveAll(u => u.UserId == user.Id);next.Users.Add(value);
            if (next.CanCreate(user.Id, mode)) value.DefaultMode = mode;
        }
        warnings.Add("Administrator effective soft defaults were preserved in protected overrides; hard safety switches still apply.");
    }
    private static object UserView(Configuration c, HubUser user)
    {
        var value = c.Users.FirstOrDefault(u => u.UserId == user.Id);GroupMode? mode = null;
        try { mode = c.DefaultFor(user.Id); } catch (PartyException) { }
        return new { userId = user.Id, name = user.Name, administrator = user.Administrator, @override = value is not null,
            policy = Project(value ?? new UserSettings(), UserFields), defaults = new { defaultMode = c.DefaultMode, createPublic = c.AllowPublicGroups, createPrivate = c.EnablePrivateGroups, guestInvites = c.DefaultGuestPermission },
            effective = new { defaultMode = mode, createPublic = c.CanCreate(user.Id, GroupMode.Public), createPrivate = c.CanCreate(user.Id, GroupMode.Private), guestInvites = c.CanInviteGuests(user.Id) },
            sources = UserFields.ToDictionary(f => f, f => (f == "createPublic" && !c.AllowPublicGroups) || (f == "createPrivate" && !c.EnablePrivateGroups) || (f == "guestInvites" && !c.EnableGuestInvites) ? "globalSafety"
                : value is null || (f == "defaultMode" ? value.DefaultMode is null : (PermissionOverride)typeof(UserSettings).GetProperty(Upper(f))!.GetValue(value)! == PermissionOverride.Inherit) ? "default" : "override") };
    }
    private static JsonObject Project(object value, string[] fields)
    {
        var result = new JsonObject();foreach (var f in fields) result[f] = JsonSerializer.SerializeToNode(value.GetType().GetProperty(Upper(f))!.GetValue(value), HubWire.Json);return result;
    }
    private static string Upper(string name) => char.ToUpperInvariant(name[0]) + name[1..];
    private static void Apply(Configuration c, JsonObject values, string[] allowed)
    {
        Only(values, allowed);
        try { foreach (var field in values) { var p = typeof(Configuration).GetProperty(Upper(field.Key))!;if (field.Value is null) throw Invalid();p.SetValue(c, field.Value.Deserialize(p.PropertyType, HubWire.Json)); } }
        catch (Exception e) when (e is JsonException or ArgumentException or TargetInvocationException or InvalidOperationException) { throw Invalid(); }
    }
    private static void Only(JsonObject value, string[] fields) { if (value.Any(p => !fields.Contains(p.Key, StringComparer.Ordinal))) throw Invalid("Unknown request field."); }
    private static string? Text(JsonObject node, string field) { try { return node[field]?.GetValue<string>(); } catch (InvalidOperationException) { throw Invalid(); } }
    private static JsonObject? Object(JsonObject node, string field, bool required) => node[field] is JsonObject obj ? obj : required || node.ContainsKey(field) ? throw Invalid() : null;
    private static Guid[] Ids(JsonObject node)
    {
        if (!node.ContainsKey("userIds")) return [];
        if (node["userIds"] is not JsonArray array || array.Count > 1000) throw Invalid();
        return array.Select(n => Guid.TryParse(n?.ToString(), out var id) && id != Guid.Empty ? id : throw Invalid()).Distinct().ToArray();
    }
    private static string Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => "{" + string.Join(',', obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value))) + "}",
        JsonArray arr => "[" + string.Join(',', arr.Select(Canonical)) + "]", _ => node?.ToJsonString() ?? "null"
    };
    private static HubAdapterException Invalid(string message = "Invalid adapter request.") => new(400, "invalid_request", message);
}
