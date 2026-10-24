using System.Text.Json;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using AccountHub.Security;
using Jellyfin.PluginSupport;
using MediaBrowser.Controller;
using DiscordBridge.Api;
using Jellyfin.Plugin.PrivateSyncPlay.Api;

namespace Jellyfin.Plugin.DiscordBridge;

[ApiController, Authorize(Policy = Policies.RequiresElevation), Route("DiscordBridge/admin/v1"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None), TypeFilter(typeof(NativeJsonBodyFilter))]
public sealed class SettingsController(BridgeStore store, BridgeRuntime runtime, IAuthorizationContext authorization, IServiceProvider services) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private async Task<bool> Administrator()
    {
        try { await NativeAuthorization.RequireAsync(HttpContext, services, administrator: true, elevated: true); return true; }
        catch (NativeAuthorizationFailure) { return false; }
    }
    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings()
    {
        if (!await Administrator()) return Envelope(new { code = "unauthorized" }, 403, false);
        var value = store.Read();
        var hub = services.GetKeyedService<Func<string, CancellationToken, Task<string>>>(DiscordBridgeClient.IdentityKey) is not null;
        var syncplay = services.GetRequiredService<IWatchPartyService>().IsAvailable;
        var security = services.GetRequiredService<ISecurityCompatibility>().Check();
        var libraries = Array.Empty<object>(); var libraryStatus = "available";
        try { libraries = services.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>().GetVirtualFolders().Select(folder => new { id = Guid.TryParse(folder.ItemId, out var libraryId) ? libraryId.ToString("D") : null, name = folder.Name }).Where(folder => folder.id is not null).Cast<object>().ToArray(); }
        catch { libraryStatus = "unavailable"; }
        return Envelope(new { revision = value.Revision, settings = value.Settings, tokenConfigured = value.Token is not null, connected = runtime.Connected, state = runtime.State, interactions = runtime.Interactions.Snapshot(),
            authorization = hub ? "Account Hub policy" : "Standalone: native Jellyfin elevation plus fresh Security proof when available; explicit confirmation otherwise",
            security = security.State, libraryFolders = libraries, libraryStatus, libraryHealth = services.GetRequiredService<Jellyfin.LibraryNotifications.LibraryNoticeStore>().Health(), libraryCaptureOwner = hub ? "Account Hub (single intake; Discord Bridge owns channel delivery)" : "Discord Bridge (independent native library observer)",
            features = new[] {
                new { id = "transport", name = "Bot / generic Discord delivery", available = true, reason = "No other suite plugin is required.", fix = "Configure this bot and its Discord permissions." },
                new { id = "identity", name = "Link, unlink, reset and account status", available = hub, reason = hub ? "Account Hub identity provider is installed." : "Account Hub owns verified Jellyfin↔Discord links; Discord roles are not a replacement.", fix = "Install and enable Account Hub, restart Jellyfin, then link through /link." },
                new { id = "watchparty", name = "Watch-party devices and invitations", available = hub && syncplay, reason = !hub ? "A verified account link from Account Hub is required." : !syncplay ? "Private SyncPlay's native party provider is missing or unavailable." : "Required identity and native party providers are installed.", fix = "Install and enable Account Hub and Private SyncPlay, restart, link your account, then open a real Jellyfin session." }
            } });
    }
    [HttpPut("settings"), RequestSizeLimit(16384)]
    public async Task<IActionResult> Save(JsonElement body, CancellationToken ct)
    {
        if (!await Administrator()) return Envelope(new { code = "unauthorized" }, 403, false);
        var gate = services.GetKeyedService<Func<HttpContext, string, CancellationToken, Task<string>>>("AccountHub.ManagementAuthorization.1");
        try
        {
            AuthorizationEvidence evidence;
            // Model binding has consumed the stream. Restore the exact bounded command body for the host's proof gate.
            var original = Request.Body;
            await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body.GetRawText()));
            Request.Body = stream;
            if (gate is not null)
            {
                string decision;
                try { decision = await gate(HttpContext, "discord.settings", ct); } finally { Request.Body = original; }
                using var authorizationResult = JsonDocument.Parse(decision);
                if (!authorizationResult.RootElement.GetProperty("ok").GetBoolean())
                    return Envelope(new { code = authorizationResult.RootElement.GetProperty("code").GetString() }, authorizationResult.RootElement.GetProperty("status").GetInt32(), false);
                evidence = new("account_hub", null, null);
            }
            else
            {
                Request.Body = original;
                var caller = await NativeAuthorization.RequireAsync(HttpContext, services, administrator: true, elevated: true);
                var app = services.GetRequiredService<IServerApplicationHost>();
                var trusted = new Uri(app.GetLocalApiUrl("127.0.0.1", "http", app.HttpPort).TrimEnd('/') + '/');
                var proof = body.TryGetProperty("stepUp", out var step) ? step.Deserialize<StepUpProof>(SecurityJson.Options) : null;
                var confirmation = body.TryGetProperty("confirmation", out var confirm) ? confirm.GetString() : null;
                // Absence of Hub never lowers a configured Hub policy: standalone configuration
                // conservatively requires proof for every write when Security has a factor.
                evidence = await services.GetRequiredService<CommandAuthorization>().AuthorizeAsync(new(caller.UserId, caller.Token!, trusted), new(StepUpLevel.Everything), CommandRisk.Destructive, false, confirmation, "CONFIRM ACCOUNT HUB CHANGE", proof, ct);
            }
            var config = body.GetProperty("settings").Deserialize<Configuration>(Json) ?? throw new ArgumentException();
            var token = body.TryGetProperty("token", out var secret) && secret.ValueKind == JsonValueKind.String ? secret.GetString() : null;
            var clear = body.TryGetProperty("clearToken", out var clearToken) && clearToken.ValueKind == JsonValueKind.True;
            var info = await authorization.GetAuthorizationInfo(HttpContext);
            var audit = await new AccountHub.Api.AccountHubClient(services).CallAsync("audit.publish", new { schemaVersion = 1, eventId = Guid.NewGuid(), source = "account-hub", eventType = "discord.settings.changing", actorUserId = info.UserId, outcome = "success", occurredAt = DateTimeOffset.UtcNow, metadata = new { reason = "discord_configuration" } }, ct);
            // Core settings and their local non-secret audit are one durable transaction.
            // Optional Hub audit forwarding must never be a dependency of standalone settings.
            store.Save(config, body.GetProperty("revision").GetInt64(), token, clear, info.UserId, evidence.Method, evidence.FallbackReason);
            return Envelope(new { status = "saved", revision = body.GetProperty("revision").GetInt64() + 1 });
        }
        catch (SecurityFailure ex) { return Envelope(new { code = ex.Code }, ex.Status, false); }
        catch (NativeAuthorizationFailure) { return Envelope(new { code = "unauthorized" }, 403, false); }
        catch (InvalidOperationException) { return Envelope(new { code = "revision_changed" }, 409, false); }
        catch (Exception ex) when (ex is ArgumentException or JsonException or KeyNotFoundException) { return Envelope(new { code = "invalid_request" }, 400, false); }
    }
    private IActionResult Envelope(object data, int status = 200, bool ok = true) => new JsonResult(ok ? (object)new { apiVersion = "1", ok, data } : new { apiVersion = "1", ok, error = data }, Json) { StatusCode = status };
}
