using System.Text;
using System.Text.Json;
using AccountHub.Api;
using Microsoft.Data.Sqlite;

namespace AccountHub.Notifications;

public sealed class NotificationService(NotificationStore store, INotificationHost host, NotificationOptions options)
{
    public static readonly string[] Operations = ["hello", "health", "notifications.register", "notifications.publish", "notifications.upsert", "notifications.publishBatch", "audit.publish"];
    public NotificationCatalogEntry[] Catalog()
    {
        foreach (var registration in host.NotificationRegistrations) {
            try { Register(registration); }
            catch (NotificationException) { /* An invalid optional descriptor must not hide other producers' inboxes. Publication for that producer still fails validation. */ }
        }
        return store.NotificationTypes();
    }
    public void Register(NotificationRegistration registration) {
        Validation.Registration(registration, options);
        var catalogs = store.NotificationTypes();
        Validation.Require(catalogs.Length < 100 || catalogs.Any(catalog => catalog.Source == registration.Source), "Producer registry capacity reached");
        if (catalogs.Any(catalog => catalog.Source == registration.Source && catalog.RequireAccess)) registration = registration with { RequireAccess = true };
        store.RegisterTypes(registration);
    }
    public void SaveLayout(NotificationLayoutUpdate update) { Validation.Layout(update.Layout); store.SaveNotificationLayout(update); }
    private static NotificationTypeDefinition? Definition(NotificationEvent entry, IEnumerable<NotificationCatalogEntry> catalogs) => catalogs.FirstOrDefault(catalog => catalog.Source == entry.Source)?.Types
        .Where(type => type.Type == entry.Type || type.Type == "*" || type.Type.EndsWith(".*", StringComparison.Ordinal) && entry.Type.StartsWith(type.Type[..^1], StringComparison.Ordinal))
        .OrderByDescending(type => type.Type == entry.Type ? int.MaxValue : type.Type.Length).FirstOrDefault();
    public static NotificationEvent Present(NotificationEvent entry, IEnumerable<NotificationCatalogEntry> catalogs) => Definition(entry, catalogs) is { } definition ? entry with {
        Layout = definition.Layout,
        Body = string.IsNullOrWhiteSpace(definition.Layout.MessageTemplate) ? entry.Body : definition.Layout.MessageTemplate.Replace("{media}", entry.Title, StringComparison.Ordinal).Replace("{days}", entry.Metadata?.GetValueOrDefault("days") ?? "", StringComparison.Ordinal)
    } : entry;
    public async Task<NotificationEvent> PresentAsync(Guid user, NotificationEvent entry, NotificationCatalogEntry[] catalogs, CancellationToken ct)
    {
        var presented = Present(entry, catalogs);
        var definition = Definition(entry, catalogs);
        if (definition?.Commands.Length > 0) {
            try {
                var commands = await host.NotificationCommandsAsync(user, entry, null, ct);
                Validation.Require(commands.Length <= 5 && commands.All(command => command is not null && definition.Commands.Contains(command.Id) && command.Label is { Length: > 0 and <= 60 } && command.Snapshot is { Length: <= 200 }) && commands.Select(command => command.Id).Distinct().Count() == commands.Length, "Invalid projected commands");
                presented = presented with { Commands = commands, Actions = definition.KeepNavigationActions || commands.Length == 0 ? entry.Actions : [] };
            } catch (Exception error) when (error is not OperationCanceledException) {
                // An unavailable optional action provider must not hide other inbox entries.
                presented = presented with { Commands = [] };
            }
        }
        return presented;
    }
    public async Task InvokeCommandAsync(Guid user, NotificationEvent entry, NotificationCommandRequest request, CancellationToken ct)
    {
        var catalogs = Catalog();
        Validation.Require(request.CommandId != Guid.Empty && request.Snapshot is { Length: <= 200 }, "Invalid action request");
        var projected = await PresentAsync(user, entry, catalogs, ct);
        if (projected.Commands?.Any(command => command.Id == request.Id && command.Enabled && command.Revision == request.Revision && command.Snapshot == request.Snapshot) != true)
            throw new NotificationException("action_changed", "Request changed. Refresh and try again.", 409);
        await host.NotificationCommandsAsync(user, entry, request, ct);
    }

    public async Task<string> DispatchAsync(string json, CancellationToken ct, ProducerIdentity? producer = null, string? requiredOperation = null)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(json) > Contract.BatchLimit) throw new NotificationException("invalid_request", "Request too large", 413);
            var request = JsonSerializer.Deserialize<Request>(json, Contract.Json);
            if (request is null || request.ApiVersion != "1" || request.RequestId == Guid.Empty || request.Payload.ValueKind != JsonValueKind.Object)
                throw new NotificationException("invalid_request", "Invalid v1 envelope");
            if (requiredOperation is not null && request.Operation != requiredOperation && !(requiredOperation == "notifications.publish" && request.Operation is "notifications.publishBatch" or "notifications.upsert"))
                throw new NotificationException("unauthorized", "Operation not permitted", 403);
            object data;
            switch (request.Operation)
            {
                case "hello": data = new { hostVersion = "0.1.0", apiVersion = "1", operations = Operations }; break;
                case "health": data = new { ready = store.Ready, hostVersion = "0.1.0", schemaVersion = 1, features = new[] { "inbox", "discord", "webhook", "registered-types", "layouts", "admin-alerts", "scoped-access", "commands", "upsert" } }; break;
                case "notifications.register":
                    if (producer is not null) throw new NotificationException("unauthorized", "Only internal plugins can register types", 403);
                    Register(request.Payload.Deserialize<NotificationRegistration>(Contract.Json) ?? throw new NotificationException("invalid_request", "Missing registration"));
                    data = new { status = "registered" }; break;
                case "notifications.publish": data = await PublishAsync(request.Payload, ct, producer); break;
                case "notifications.upsert": data = await PublishAsync(request.Payload, ct, producer, true); break;
                case "notifications.publishBatch":
                    if (!request.Payload.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array || events.GetArrayLength() is < 1 or > 50)
                        throw new NotificationException("invalid_request", "Batch requires 1-50 events");
                    var results = new List<object>();
                    foreach (var element in events.EnumerateArray())
                    {
                        var eventId = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("eventId", out var id) ? id.ToString() : null;
                        try { results.Add(new { eventId, result = Contract.Success(await PublishAsync(element, ct, producer)) }); }
                        catch (NotificationException ex) { results.Add(new { eventId, result = Contract.Failure(ex.Code, ex.Message, ex.Status >= 500) }); }
                        catch (JsonException) { results.Add(new { eventId, result = Contract.Failure("invalid_request", "Invalid event JSON") }); }
                        catch (SqliteException) { results.Add(new { eventId, result = Contract.Failure("unavailable", "Storage unavailable", true) }); }
                    }
                    data = new { results }; break;
                case "audit.publish":
                    if (Encoding.UTF8.GetByteCount(request.Payload.GetRawText()) > Contract.EventLimit) throw new NotificationException("rejected", "Audit too large");
                    var audit = request.Payload.Deserialize<AuditEvent>(Contract.Json) ?? throw new NotificationException("invalid_request", "Missing audit");
                    Validation.Audit(audit, options, producer);
                    data = await host.AppendAuditAsync(audit, ct); break;
                default: return Contract.Serialize(Contract.Failure("unsupported_operation", "Unknown operation"));
            }
            return Contract.Serialize(Contract.Success(data));
        }
        catch (NotificationException ex) { return Contract.Serialize(Contract.Failure(ex.Code, ex.Message, ex.Status >= 500)); }
        catch (JsonException) { return Contract.Serialize(Contract.Failure("invalid_request", "Invalid JSON")); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Contract.Serialize(Contract.Failure("unavailable", "Host unavailable", true)); }
    }

    private async Task<PublishResult> PublishAsync(JsonElement payload, CancellationToken ct, ProducerIdentity? producer, bool upsert = false)
    {
        Validation.Require(Encoding.UTF8.GetByteCount(payload.GetRawText()) <= Contract.EventLimit, "Event too large");
        var entry = payload.Deserialize<NotificationEvent>(Contract.Json) ?? throw new NotificationException("invalid_request", "Missing event");
        return await PublishAsync(entry, Validation.Hash(payload.GetRawText()), ct, producer, upsert);
    }

    public async Task<PublishResult> PublishAsync(NotificationEvent entry, string hash, CancellationToken ct, ProducerIdentity? producer = null, bool upsert = false)
    {
        var registrations = host.NotificationRegistrations.Where(registration => registration.Source == entry.Source).ToArray();
        var registered = store.NotificationTypes().FirstOrDefault(catalog => catalog.Source == entry.Source);
        Validation.Event(entry, options, producer, registered is not null || registrations.Length > 0);
        // Acknowledged events do not depend on today's directory/link/preferences availability.
        if (upsert) Validation.Require(!string.IsNullOrWhiteSpace(entry.DedupeKey), "Upsert requires a stable dedupe key");
        if (store.TryDuplicate(entry, hash, upsert) is { } duplicate) return duplicate;
        foreach (var registration in registrations) Register(registration);
        registered = store.NotificationTypes().FirstOrDefault(catalog => catalog.Source == entry.Source);
        var definition = Definition(entry, registered is null ? [] : [registered]);
        Validation.Require(registered is null ? options.Sources.Contains(entry.Source) : definition is not null, "Unregistered notification type");
        if (definition is not null) {
            Validation.Require(definition.Recipients == "event" || entry.AdminOnly == (definition.Recipients == "administrators"), "Registered recipient policy denied");
            Validation.Require(entry.Actions is null || entry.Actions.All(action => definition.Actions.Any(allowed => allowed.Label == action.Label)), "Undeclared notification action");
            entry = entry with { Layout = definition.Layout, Actions = entry.Actions?.Select(action => action with { Size = definition.Actions.First(allowed => allowed.Label == action.Label).Size }).ToArray() };
        }
        if (registered?.RequireAccess == true && !entry.AdminOnly) {
            if (!host.NotificationAccessAvailable(entry.Source)) throw new NotificationException("unavailable", "Producer permission provider unavailable", 503);
            var metadata = new Dictionary<string, string>(entry.Metadata ?? []) { ["notificationAccess"] = "required" };
            entry = entry with { Metadata = metadata };
        }
        var audience = (entry.AdminOnly ? await host.AdministratorsAsync(ct) : entry.Audience).Distinct().ToArray();
        Validation.Require(audience.Length <= options.MaxAudience, "Audience exceeds host limit");
        var routes = store.Settings(false).Routes;
        var fanout = new List<(Guid, bool, Route[], string?)>();
        var publicRoutes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var user in audience)
        {
            ct.ThrowIfCancellationRequested();
            if (!await host.UserExistsAsync(user, ct)) { if (entry.Type.StartsWith("library.", StringComparison.Ordinal)) continue; Validation.Require(false, "Unknown recipient"); }
            if (!await host.CanAccessNotificationAsync(user, entry, ct)) continue;
            if (entry.AdminOnly && !await host.IsAdministratorAsync(user, ct)) continue;
            var prefs = store.Preferences(user);
            var mandatory = entry.AdminOnly || entry.Severity == "critical";
            var inbox = mandatory || Enabled(prefs, entry, "in-app", true)&&CategoryEnabled(prefs,entry,"in-app");
            var link = await host.DiscordLinkAsync(user, ct);
            var selected = new List<Route>();
            foreach (var route in routes.Where(r => Matches(r.Source, entry.Source) && Matches(r.Type, entry.Type)))
            {
                // Failure summaries remain local: never recursively fan out another delivery failure.
                if (entry.Type == "notifications.delivery.failed" || (route.AdminOnly && !entry.AdminOnly)) continue;
                if (route.DirectMessage)
                {
                    if (link is null || (!mandatory && (!Enabled(prefs, entry, "discord", false)||!CategoryEnabled(prefs,entry,"discord")))) continue;
                    selected.Add(route with { Destination = link });
                }
                // Private user events NEVER enter shared channels or webhooks.
                else if (entry.AdminOnly && route.AdminOnly && publicRoutes.Add(route.Id)) selected.Add(route);
            }
            fanout.Add((user, inbox, selected.ToArray(), link));
        }
        return store.Accept(entry, hash, fanout, upsert);
    }
    internal static bool Matches(string rule, string value) => rule == "*" || rule == value || rule.EndsWith(".*",StringComparison.Ordinal)&&value.StartsWith(rule[..^1],StringComparison.Ordinal);
    internal static bool Enabled(Preference[] prefs, NotificationEvent e, string channel, bool fallback) {
        if(prefs.Any(p=>p.Channel==channel&&!p.Enabled&&Matches(p.Source,e.Source)&&(p.Type=="*"||p.Type.EndsWith(".*",StringComparison.Ordinal)&&Matches(p.Type,e.Type))))return false;
        return prefs.Where(p => p.Channel == channel && Matches(p.Source, e.Source) && Matches(p.Type, e.Type))
            .OrderByDescending(p => (p.Source != "*" ? 1 : 0) + (p.Type != "*" ? 1 : 0)).ThenByDescending(p=>p.Type.Length).Select(p => (bool?)p.Enabled).FirstOrDefault() ?? fallback;
    }
    internal static bool CategoryEnabled(Preference[] prefs,NotificationEvent e,string channel){
        if(e.Source!="account-hub"||!e.Type.StartsWith("library.",StringComparison.Ordinal))return true;
        if(!Enabled(prefs,e with{Type="category.library.*"},channel,true))return false;
        return (e.Metadata?.GetValueOrDefault("libraryCategories")??"all").Split(',').Where(c=>c is "all" or "requested" or "favourite")
            .Any(c=>Enabled(prefs,e with{Type="category.library."+c},channel,c=="all"));
    }

    public async Task SaveSettingsAsync(Settings settings, Guid actor, CancellationToken ct)
    {
        if (settings.Routes is null || settings.Routes.Any(r => r is null || r.Id is null || r.Source is null || r.Type is null || r.Destination is null))
            throw new NotificationException("invalid_request", "Missing route fields");
        Validation.Require(settings.NotificationDays is >= 7 and <= 365 && settings.Routes.Length <= 50, "Invalid settings limits");
        Validation.Require(settings.Routes.Select(r => r.Id).Distinct().Count() == settings.Routes.Length, "Duplicate route ID");
        var previous = store.Settings(false);
        var routes = settings.Routes.Select(r =>
        {
            Validation.Require(r.Id.Length is > 0 and <= 100 && r.Channel is "discord" or "webhook" && (r.Channel != "webhook" || !r.DirectMessage), "Invalid route");
            Validation.Require(r.AdminOnly || r.DirectMessage, "Shared routes must be admin-only");
            var old = previous.Routes.FirstOrDefault(x => x.Id == r.Id);
            var route = r with { Secret = r.Secret ?? old?.Secret, Destination = r.Destination == "[configured]" ? old?.Destination ?? "" : r.Destination };
            if (route.Channel == "webhook")
            {
                Validation.Require(route.Secret?.Length is >= 32 and <= 1024, "Webhook secret must be at least 32 characters");
                WebhookSender.ValidateUri(new Uri(route.Destination, UriKind.Absolute), route);
            }
            else Validation.Require(route.DirectMessage || (route.Destination.Length is >= 1 and <= 30 && route.Destination.All(char.IsAsciiDigit)), "Invalid Discord channel");
            return route;
        }).ToArray();
        if (settings.Revision != previous.Revision) throw new NotificationException("revision_changed", "Reload settings", 409);
        // A durable audit intent precedes mutation. Core command gate can additionally record completion.
        await host.AppendAuditAsync(new(1, Guid.NewGuid(), "account-hub", "notifications.settings.changing", "success", DateTimeOffset.UtcNow, actor), ct);
        store.SaveSettings(settings with { Routes = routes });
    }
}
