using System.Text;
using System.Text.Json;
using AccountHub.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AccountHub.Notifications;

public static class Endpoints
{
    public static IServiceCollection AddAccountHubNotifications(this IServiceCollection services, NotificationOptions options)
    {
        services.TryAddSingleton(options);
        return Register(services, true, true);
    }

    public static IServiceCollection AddAccountHubNotifications(this IServiceCollection services, Func<IServiceProvider, NotificationOptions> options, bool startWorker, bool registerProducer)
    {
        services.TryAddSingleton(options);
        return Register(services, startWorker, registerProducer);
    }
    private static IServiceCollection Register(IServiceCollection services, bool startWorker, bool registerProducer)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddDataProtection(); // Host MUST configure a persistent, protected key ring.
        services.TryAddSingleton<NotificationStore>();
        services.TryAddSingleton<NotificationService>();
        services.TryAddSingleton<LiveNotifications>();
        services.TryAddSingleton<WebPush>();
        services.TryAddSingleton<IngressLimiter>();
        services.TryAddSingleton<SecurityIngress>();
        services.TryAddSingleton<WebhookSender>();
        services.TryAddSingleton<SecretDelivery>();
        if (startWorker) services.AddHostedService<DeliveryWorker>();
        if (registerProducer) services.AddKeyedSingleton<Func<string, CancellationToken, Task<string>>>(Contract.ServiceKey,
            (sp, _) => (json, ct) => sp.GetRequiredService<NotificationService>().DispatchAsync(json, ct));
        return services;
    }

    public static IServiceCollection AddAccountHubDiscordTransport(this IServiceCollection services, Func<IServiceProvider, Func<string?>> botToken)
    {
        services.AddHttpClient("AccountHub.Discord", c => c.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddSingleton<IDiscordTransport>(sp => new DiscordBotTransport(sp.GetRequiredService<IHttpClientFactory>().CreateClient("AccountHub.Discord"), botToken(sp)));
        return services;
    }

    public static IEndpointRouteBuilder MapAccountHubNotifications(this IEndpointRouteBuilder endpoints, bool mapSecurityReceiver = true, string prefix = "/AccountHub/api/v1", string assets = "/AccountHub/assets/notifications")
    {
        endpoints.MapGet(assets + "/bell.css", () =>
        {
            var stream = typeof(Endpoints).Assembly.GetManifestResourceStream("AccountHub.Notifications.Web.bell.css")!;
            return Results.Stream(stream, "text/css");
        }).AllowAnonymous();
        endpoints.MapGet(assets + "/bell.js", () =>
        {
            var stream = typeof(Endpoints).Assembly.GetManifestResourceStream("AccountHub.Notifications.Web.bell.js")!;
            return Results.Stream(stream, "text/javascript");
        }).AllowAnonymous();
        endpoints.MapGet(assets + "/settings.js", () =>
        {
            var stream = typeof(Endpoints).Assembly.GetManifestResourceStream("AccountHub.Notifications.Web.settings.js")!;
            return Results.Stream(stream, "text/javascript");
        }).AllowAnonymous();
        foreach (var file in new[] { "notification-card.js", "notification-studio.js", "notification-studio.css" }) {
            var asset = file;
            endpoints.MapGet(assets + "/" + asset, () => Results.Stream(typeof(Endpoints).Assembly.GetManifestResourceStream("AccountHub.Notifications.Web." + asset)!, asset.EndsWith(".css",StringComparison.Ordinal) ? "text/css" : "text/javascript")).AllowAnonymous();
        }
        var user = endpoints.MapGroup(prefix + "/notifications").RequireAuthorization();
        endpoints.MapGet(prefix+"/notifications/push/sw.js",(HttpContext c)=>{
            c.Response.Headers.CacheControl="no-cache";
            return Results.Stream(typeof(Endpoints).Assembly.GetManifestResourceStream("AccountHub.Notifications.Web.push-worker.js")!,"text/javascript");
        }).AllowAnonymous();
        user.MapGet("/push",(HttpContext c,INotificationHost h,NotificationStore s)=>Guard(async()=>{var id=await User(c,h);var config=s.PushSettings();return Json(Contract.Success(new{config.Enabled,config.DefaultOn,config.PublicKey,devices=s.PushTargets(id).Select(t=>t.Id)}));}));
        user.MapPost("/push",(HttpContext c,INotificationHost h,NotificationStore s)=>Guard(async()=>{
            var id=await User(c,h);var subscription=JsonSerializer.Deserialize<PushSubscription>(await Body(c,4096),Contract.Json)??throw new NotificationException("invalid_request","Missing subscription");
            return Json(Contract.Success(new{id=s.SavePush(id,subscription)}));
        }));
        user.MapDelete("/push/{id}",(string id,HttpContext c,INotificationHost h,NotificationStore s)=>Guard(async()=>{s.RemovePush(await User(c,h),id);return Json(Contract.Success(new{status="disabled"}));}));
        user.MapPost("/live-ticket",(HttpContext c,INotificationHost h,LiveNotifications live)=>Guard(async()=>Json(Contract.Success(new{ticket=live.Issue(c,await User(c,h)),expiresInSeconds=30}))));
        endpoints.MapGet(prefix+"/notifications/live",(HttpContext c,INotificationHost h,NotificationStore s,LiveNotifications live)=>live.ConnectAsync(c,h,s)).AllowAnonymous();
        user.MapGet("", (HttpContext c, NotificationStore s, INotificationHost h, NotificationService service) => Guard(async () =>
        {
            var id = await User(c, h);
            var limit = Math.Clamp(Number(c, "limit", 25), 1, 100);
            var history=c.Request.Query["history"]=="true";
            var blocked=c.Request.Query["blocked"]=="true";Validation.Require(!history||!blocked,"Choose one notification view");
            await service.RestoreBlockedAsync(id,c.RequestAborted);
            var stored = s.Inbox(id, Number(c, "skip", 0), limit, c.Request.Query["source"], c.Request.Query["unread"] == "true", await h.IsAdministratorAsync(id, c.RequestAborted),history,blocked);
            var catalogs = service.Catalog();
            var rows = new List<InboxRow>();
            foreach (var row in stored) if (await h.CanAccessNotificationAsync(id, row.Event, c.RequestAborted)) {
                var presented = await service.PresentAsync(id, row.Event, catalogs, c.RequestAborted);
                if(history||blocked)presented=presented with{Commands=[]};
                // Fanout identities are server-owned, not notification content.
                rows.Add(row with { Event = presented with { Audience = presented.AdminOnly ? [] : [id] } });
            }
            var retention=s.Settings();var data = new { notifications = rows, hasMore = stored.Count == limit,blockedLimit=retention.BlockedNotificationLimit,retentionDays=retention.NotificationDays };
            var tag = "\"" + Validation.Hash(Contract.Serialize(data)) + "\"";
            c.Response.Headers.ETag = tag;
            c.Response.Headers.CacheControl = "private, no-cache";
            return c.Request.Headers.IfNoneMatch == tag ? Results.StatusCode(304) : Json(Contract.Success(data));
        }));
        user.MapPost("/{id:guid}/actions", (Guid id, HttpContext c, NotificationStore s, INotificationHost h, NotificationService service) => Guard(async () =>
        {
            var userId = await User(c, h);
            var entry = s.InboxEvent(userId, id, await h.IsAdministratorAsync(userId, c.RequestAborted));
            if (entry is null || !await h.CanAccessNotificationAsync(userId, entry, c.RequestAborted)) throw new NotificationException("not_found", "Notification not found", 404);
            var command = JsonSerializer.Deserialize<NotificationCommandRequest>(await Body(c, 4096), Contract.Json) ?? throw new NotificationException("invalid_request", "Missing action");
            await service.InvokeCommandAsync(userId, entry, command, c.RequestAborted);
            return Json(Contract.Success(new { status = "updated" }));
        }));
        user.MapGet("/unread-count", (HttpContext c, NotificationStore s, INotificationHost h) => Guard(async () =>
        {
            var id = await User(c, h); var administrator = await h.IsAdministratorAsync(id, c.RequestAborted); var count = 0;
            // Bound native permission checks; a capped count never exposes inaccessible media.
            for (var offset = 0; offset < 1000; offset += 100)
            {
                var rows = s.Inbox(id, offset, 100, null, unread: true, includeAdmin: administrator);
                foreach (var row in rows) if (await h.CanAccessNotificationAsync(id, row.Event, c.RequestAborted)) count++;
                if (rows.Count < 100) break;
            }
            return Json(Contract.Success(new { count, capped = s.Unread(id, administrator) > 1000 }));
        }));
        user.MapPut("/preferences/rule", (HttpContext c, NotificationStore s, INotificationHost h) => Guard(async () =>
        {
            var id = await User(c, h);
            var rule = JsonSerializer.Deserialize<Preference>(await Body(c, 4096), Contract.Json) ?? throw new NotificationException("invalid_request", "Missing preference");
            s.SetPreference(id, rule); return Json(Contract.Success(new { status = "updated" }));
        }));
        foreach (var action in new[] { "read", "viewed", "dismiss" })
        {
            var dismiss = action == "dismiss";
            user.MapPost("/{id:guid}/" + action, (Guid id, HttpContext c, NotificationStore s, INotificationHost h) => Guard(async () =>
                s.Mark(await User(c, h), id, dismiss) ? Json(Contract.Success(new { status = "updated" })) : Json(Contract.Failure("not_found", "Notification not found"), 404)));
        }
        user.MapPost("/read-all", (HttpContext c, NotificationStore s, INotificationHost h) => Guard(async () =>
        { s.ReadAll(await User(c, h)); return Json(Contract.Success(new { status = "updated", batchLimit = 1000 })); }));
        user.MapGet("/preferences", (HttpContext c, NotificationStore s, INotificationHost h) => Guard(async () => Json(Contract.Success(new { preferences = s.Preferences(await User(c, h)) }))));
        user.MapPut("/grouping",(HttpContext c,NotificationStore s,INotificationHost h,NotificationService service)=>Guard(async()=>{
            var id=await User(c,h);var preference=JsonSerializer.Deserialize<NotificationGroupingPreference>(await Body(c,4096),Contract.Json)??throw new NotificationException("invalid_request","Missing grouping preference");
            var administrator=await h.IsAdministratorAsync(id,c.RequestAborted);
            if(!service.Catalog().Any(catalog=>catalog.Source==preference.Source&&catalog.Grouping is not null&&catalog.Types.Any(type=>administrator||type.Recipients!="administrators")))throw new NotificationException("not_found","Grouping unavailable",404);
            s.SetGroupingPreference(id,preference);return Json(Contract.Success(new{status="updated"}));
        }));
        user.MapGet("/categories", (HttpContext c, INotificationHost h, NotificationService service, NotificationStore s) => Guard(async () => {
            var id = await User(c, h); var administrator = await h.IsAdministratorAsync(id, c.RequestAborted);
            // Personal controls disclose category names, not server-wide layouts,
            // action projections, message templates or administrator-only types.
            var catalogs = service.Catalog().Select(catalog => new {
                catalog.Source,
                grouping = catalog.Grouping is {} grouping ? new { grouping.Name, grouping.DefaultEnabled, enabled=s.GroupingPreferences(id).FirstOrDefault(value=>value.Source==catalog.Source)?.Enabled } : null,
                types = catalog.Types.Where(type => administrator || type.Recipients != "administrators")
                    .Select(type => new { type.Type, type.Name, description = type.Trigger, defaults = type.Defaults ?? new NotificationDefaults(Importance:type.Recipients == "administrators" ? "high" : "normal") }).ToArray()
            }).Where(catalog => catalog.types.Length > 0).ToArray();
            return Json(Contract.Success(new { isAdministrator = administrator, catalogs }));
        }));
        user.MapPut("/preferences", (HttpContext c, NotificationStore s, INotificationHost h) => Guard(async () =>
        {
            var id = await User(c, h);
            var preferences = JsonSerializer.Deserialize<Preference[]>(await Body(c, Contract.EventLimit), Contract.Json) ?? throw new NotificationException("invalid_request", "Missing preferences");
            s.Preferences(id, preferences); return Json(Contract.Success(new { status = "updated" }));
        }));

        var admin = endpoints.MapGroup(prefix + "/settings/notifications").RequireAuthorization();
        admin.MapGet("/push",(HttpContext c,INotificationHost h,NotificationStore s)=>Guard(async()=>{await Admin(c,h,false);var config=s.PushSettings();return Json(Contract.Success(new{config.Revision,config.Enabled,config.DefaultOn,config.PublicKey,automaticSetup=true}));}));
        admin.MapPut("/push",(HttpContext c,INotificationHost h,NotificationStore s)=>Guard(async()=>{
            await Admin(c,h,true);using var body=JsonDocument.Parse(await Body(c,4096));var revision=body.RootElement.GetProperty("revision").GetInt64();var enabled=body.RootElement.GetProperty("enabled").GetBoolean();
            var subject=WebPush.AutomaticSubject(h.PublicServerUrl(c),c.Request);
            bool? defaultOn=body.RootElement.TryGetProperty("defaultOn",out var initial)?initial.GetBoolean():null;
            await h.AppendAuditAsync(new(1,Guid.NewGuid(),"notifications","notifications.push.changing","success",DateTimeOffset.UtcNow,await User(c,h)),c.RequestAborted);s.ConfigurePush(revision,enabled,subject,defaultOn);return Json(Contract.Success(new{status="updated"}));
        }));
        admin.MapGet("", (HttpContext c, INotificationHost h, NotificationStore s) => Guard(async () =>
        { await Admin(c, h, false); return Json(Contract.Success(s.Settings())); }));
        admin.MapPut("", (HttpContext c, INotificationHost h, NotificationService s) => Guard(async () =>
        {
            await Admin(c, h, true);
            var settings = JsonSerializer.Deserialize<Settings>(await Body(c, Contract.EventLimit), Contract.Json) ?? throw new NotificationException("invalid_request", "Missing settings");
            await s.SaveSettingsAsync(settings, await User(c, h), c.RequestAborted);
            return Json(Contract.Success(new { status = "updated", revision = settings.Revision + 1 }));
        }));
        admin.MapGet("/types", (HttpContext c, INotificationHost h, NotificationService s) => Guard(async () =>
        { await Admin(c, h, false); return Json(Contract.Success(new { catalogs = s.Catalog() })); }));
        admin.MapPut("/grouping",(HttpContext c,INotificationHost h,NotificationService service)=>Guard(async()=>{
            await Admin(c,h,true);
            var update=JsonSerializer.Deserialize<NotificationGroupingUpdate>(await Body(c,4096),Contract.Json)??throw new NotificationException("invalid_request","Missing grouping default");
            await h.AppendAuditAsync(new(1,Guid.NewGuid(),"notifications","notifications.grouping.changing","success",DateTimeOffset.UtcNow,await User(c,h)),c.RequestAborted);
            service.SaveGrouping(update);return Json(Contract.Success(new{status="updated"}));
        }));
        admin.MapGet("/library",(HttpContext c,INotificationHost h,IServiceProvider services)=>Guard(async()=> {
            await Admin(c,h,false); var provider=services.GetKeyedService<Func<string,CancellationToken,Task<string>>>("Notifications.LibrarySettings.1")??throw new NotificationException("unavailable","Library owner unavailable",503);
            using var data=JsonDocument.Parse(await provider("read",c.RequestAborted)); return Json(Contract.Success(data.RootElement.Clone()));
        }));
        admin.MapPut("/library",(HttpContext c,INotificationHost h,IServiceProvider services)=>Guard(async()=> {
            await Admin(c,h,true); var provider=services.GetKeyedService<Func<string,CancellationToken,Task<string>>>("Notifications.LibrarySettings.1")??throw new NotificationException("unavailable","Library owner unavailable",503);
            var body=await Body(c,8192); await h.AppendAuditAsync(new(1,Guid.NewGuid(),"notifications","notifications.library.changing","success",DateTimeOffset.UtcNow,await User(c,h)),c.RequestAborted);
            try{using var data=JsonDocument.Parse(await provider(Encoding.UTF8.GetString(body),c.RequestAborted)); return Json(Contract.Success(data.RootElement.Clone()));}catch(InvalidOperationException){throw new NotificationException("revision_changed","Reload library settings",409);}
        }));
        admin.MapPut("/types/layout", (HttpContext c, INotificationHost h, NotificationService s) => Guard(async () =>
        {
            await Admin(c, h, true);
            var update = JsonSerializer.Deserialize<NotificationLayoutUpdate>(await Body(c, 16384), Contract.Json) ?? throw new NotificationException("invalid_request", "Missing layout");
            await h.AppendAuditAsync(new(1, Guid.NewGuid(), "notifications", "notifications.layout.changing", "success", DateTimeOffset.UtcNow, await User(c, h)), c.RequestAborted);
            s.SaveLayout(update); return Json(Contract.Success(new { status = "updated" }));
        }));
        admin.MapPut("/types/defaults", (HttpContext c, INotificationHost h, NotificationService s) => Guard(async () => {
            await Admin(c,h,true);
            var update = JsonSerializer.Deserialize<NotificationDefaultsUpdate>(await Body(c,4096),Contract.Json) ?? throw new NotificationException("invalid_request","Missing defaults");
            await h.AppendAuditAsync(new(1,Guid.NewGuid(),"notifications","notifications.defaults.changing","success",DateTimeOffset.UtcNow,await User(c,h)),c.RequestAborted);
            s.SaveDefaults(update); return Json(Contract.Success(new { status="updated" }));
        }));
        admin.MapGet("/deliveries", (HttpContext c, INotificationHost h, NotificationStore s) => Guard(async () =>
        { await Admin(c, h, false); return Json(Contract.Success(new { deliveries = s.Deliveries(Number(c, "skip", 0), Number(c, "limit", 25)) })); }));
        admin.MapPost("/deliveries/{id:guid}/retry", (Guid id, HttpContext c, INotificationHost h, NotificationStore s) => Guard(async () =>
        {
            await Admin(c, h, true);
            await h.AppendAuditAsync(new(1, Guid.NewGuid(), "account-hub", "notifications.delivery.retry", "success", DateTimeOffset.UtcNow, await User(c, h), "delivery", id.ToString()), c.RequestAborted);
            s.Retry(id); return Json(Contract.Success(new { status = "pending_or_unchanged" }));
        }));
        admin.MapPost("/test", (HttpContext c, INotificationHost h, NotificationService s) => Guard(async () =>
        {
            await Admin(c, h, true);
            var entry = new NotificationEvent(1, Guid.NewGuid(), "account-hub", "notifications.test", "info", "Account Hub delivery test", "Sanitized notification transport test.", [], true, DateTimeOffset.UtcNow);
            return Json(Contract.Success(await s.PublishAsync(entry, Validation.Hash(Contract.Serialize(entry)), c.RequestAborted)));
        }));
        admin.MapPost("/producer-tokens", (HttpContext c, INotificationHost h, NotificationStore s, NotificationOptions options, NotificationService service) => Guard(async () =>
        {
            await Admin(c, h, true);
            var issue = JsonSerializer.Deserialize<ProducerIdentity>(await Body(c, 4096), Contract.Json) ?? throw new NotificationException("invalid_request", "Missing credential parameters");
            Validation.Require((options.Sources.Contains(issue.Source) || service.Catalog().Any(catalog => catalog.Source == issue.Source)) && issue.Source != "jellyfin-security" && issue.Scopes is not null, "Invalid source/scopes");
            await h.AppendAuditAsync(new(1, Guid.NewGuid(), "account-hub", "notifications.producer.issuing", "success", DateTimeOffset.UtcNow, await User(c, h)), c.RequestAborted);
            var token = s.IssueToken(issue.Source, issue.Scopes!);
            return Json(Contract.Success(new { token, credentialId = Validation.Hash(token) }), 201);
        }));
        admin.MapDelete("/producer-tokens/{id}", (string id, HttpContext c, INotificationHost h, NotificationStore s) => Guard(async () =>
        {
            await Admin(c, h, true);
            Validation.Require(id.Length == 64 && id.All(char.IsAsciiHexDigit), "Invalid credential ID");
            await h.AppendAuditAsync(new(1, Guid.NewGuid(), "account-hub", "notifications.producer.revoking", "success", DateTimeOffset.UtcNow, await User(c, h)), c.RequestAborted);
            s.RevokeToken(id.ToLowerInvariant()); return Json(Contract.Success(new { status = "revoked" }));
        }));

        foreach (var kind in new[] { "notifications", "audit" })
        {
            var operation = kind == "audit" ? "audit.publish" : "notifications.publish";
            endpoints.MapPost(prefix + "/producer/" + kind, (HttpContext c, NotificationStore s, NotificationService service, IngressLimiter limiter) => Guard(async () =>
            {
                if (!limiter.Allow("producer-global", 600)) throw new NotificationException("rate_limited", "Ingress rate limit", 429);
                var authorization = c.Request.Headers.Authorization.ToString();
                var token = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? authorization[7..] : "";
                if (token.Length != 64) throw new NotificationException("unauthorized", "Invalid producer credential", 401);
                var producer = s.Authenticate(token) ?? throw new NotificationException("unauthorized", "Invalid producer credential", 401);
                if (!limiter.Allow("producer:" + Validation.Hash(token), 120) || !limiter.Allow("source:" + producer.Source, 240)) throw new NotificationException("rate_limited", "Source rate limit", 429);
                var reply = await service.DispatchAsync(Encoding.UTF8.GetString(await Body(c, Contract.BatchLimit)), c.RequestAborted, producer, operation);
                using var parsed = JsonDocument.Parse(reply);
                var status = 200;
                if (!parsed.RootElement.GetProperty("ok").GetBoolean())
                    status = parsed.RootElement.GetProperty("error").GetProperty("code").GetString() switch { "unavailable" => 503, "unauthorized" => 403, _ => 400 };
                return Results.Text(reply, "application/json", statusCode: status);
            })).AllowAnonymous(); // ONLY this exact route; never exempt the whole API from Security.
        }
        if (mapSecurityReceiver) endpoints.MapPost(prefix + "/security/jellyfin-security", (HttpContext c, SecurityIngress ingress) => Guard(async () =>
            Json(Contract.Success(ingress.Receive(await Body(c, Contract.EventLimit), c.Request.Headers["X-2FA-Timestamp"].ToString(), c.Request.Headers["X-2FA-Signature"].ToString())), 202))).AllowAnonymous();
        return endpoints;
    }
    // Optional: core owns /audit routes. Call only if the core has not already mapped this endpoint.
    public static IEndpointRouteBuilder MapNotificationAuditExport(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/AccountHub/api/v1/audit/export", (HttpContext c, INotificationHost h) => Guard(async () =>
        {
            await Admin(c, h, false);
            await h.AuthorizeAuditAsync(c, c.RequestAborted); // fresh proof in body, never URL
            var format = c.Request.Query["format"].ToString();
            Validation.Require(format is "json" or "csv", "Invalid export format");
            return Json(Contract.Success(await h.ExportAuditAsync(format, c.RequestAborted)));
        })).RequireAuthorization();
        return endpoints;
    }
    private static async Task<Guid> User(HttpContext c, INotificationHost h)
    {
        c.Response.Headers.CacheControl = "no-store";
        var id = await h.CurrentUserAsync(c, c.RequestAborted);
        if (id is null || !await h.UserExistsAsync(id.Value, c.RequestAborted)) throw new NotificationException("unauthorized", "A current Jellyfin user is required", 401);
        // No cookies-only mutation auth; the host verifies this header with Jellyfin, not merely its presence.
        if (!HttpMethods.IsGet(c.Request.Method) && string.IsNullOrEmpty(c.Request.Headers.Authorization))
            throw new NotificationException("unauthorized", "Authorization header required", 401);
        if (c.Request.Headers.TryGetValue("Origin", out var origin) && origin != $"{c.Request.Scheme}://{c.Request.Host}")
            throw new NotificationException("unauthorized", "Cross-origin requests denied", 403);
        return id.Value;
    }
    private static async Task Admin(HttpContext c, INotificationHost h, bool write)
    {
        if (!await h.IsAdministratorAsync(await User(c, h), c.RequestAborted)) throw new NotificationException("unauthorized", "Administrator required", 403);
        await h.AuthorizeManagementAsync(c, write, c.RequestAborted);
    }
    private static int Number(HttpContext c, string key, int fallback) => int.TryParse(c.Request.Query[key], out var n) ? n : fallback;
    private static IResult Json(object value, int status = 200) => status>=400?new SafeFailureResult(value,status):Results.Text(Contract.Serialize(value), "application/json", statusCode: status);
    private sealed class SafeFailureResult(object value,int status):IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            var original=JsonSerializer.SerializeToElement(value,Contract.Json);var error=original.TryGetProperty("error",out var failure)?failure:default;
            var code=error.ValueKind==JsonValueKind.Object&&error.TryGetProperty("code",out var kind)?kind.GetString()??"request_failed":"request_failed";
            var retryable=error.ValueKind==JsonValueKind.Object&&error.TryGetProperty("retryable",out var retry)&&retry.ValueKind==JsonValueKind.True;
            var id=Guid.NewGuid().ToString("D");context.Items["Suite.Diagnostics.1"]=id;
            var source=context.Request.Path.StartsWithSegments("/AccountHub")?"account-hub":"notifications";
            try{context.RequestServices.GetKeyedService<Func<string,string>>("Suite.Diagnostics.1")?.Invoke(Contract.Serialize(new{id,source,level=status>=500?"error":"warning",code,operation=context.GetEndpoint()?.DisplayName??"Notifications request",createdAt=DateTimeOffset.UtcNow,statusCode=status,isFailure=true,pluginVersion=context.RequestServices.GetKeyedService<Func<string>>("Suite.Diagnostics.1.Version."+source)?.Invoke()}));}catch{ /* No application effects are retried if diagnostics are unavailable. */ }
            context.Response.Headers["X-Suite-Error-Id"]=id;
            await Results.Text(Contract.Serialize(new{apiVersion="1",ok=false,error=new{code,nature=UserErrors.Nature(code,status),message=UserErrors.Message(code,status),errorId=id,retryable}}),"application/json",statusCode:status).ExecuteAsync(context);
        }
    }
    private static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (NotificationException ex) { return Json(Contract.Failure(ex.Code, ex.Message, ex.Status >= 500), ex.Status); }
        catch (JsonException) { return Json(Contract.Failure("invalid_request", "Invalid JSON"), 400); }
        catch (ArgumentException) { return Json(Contract.Failure("invalid_request", "Invalid input"), 400); }
        catch (FormatException) { return Json(Contract.Failure("invalid_request", "Invalid input"), 400); }
        catch (SqliteException) { return Json(Contract.Failure("unavailable", "Storage unavailable", true), 503); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Json(Contract.Failure("unavailable", "Host unavailable", true), 503); }
    }
    private static async Task<byte[]> Body(HttpContext c, int limit)
    {
        if (c.Request.ContentLength > limit) throw new NotificationException("invalid_request", "Body too large", 413);
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await c.Request.Body.ReadAsync(buffer, c.RequestAborted)) > 0)
        {
            if (output.Length + count > limit) throw new NotificationException("invalid_request", "Body too large", 413);
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
