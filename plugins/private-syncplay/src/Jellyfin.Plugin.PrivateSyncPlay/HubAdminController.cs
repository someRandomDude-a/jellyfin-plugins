using System.Text.Json;
using System.Text.Json.Nodes;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PrivateSyncPlay;

[Route("PrivateSyncPlay/admin/account-hub/v1"), Authorize(Policy = Policies.RequiresElevation), RequestSizeLimit(131072)]
public sealed class HubAdminController(HubPolicyService policies, HubIntegration integration, PartyStore store) : ControllerBase
{
    private Guid Actor()
    {
        if (User.Identity?.IsAuthenticated != true || !bool.TryParse(User.FindFirst("Jellyfin-IsApiKey")?.Value, out var apiKey) || apiKey ||
            !Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var id) || !policies.IsAdministrator(id))
            throw new HubAdapterException(403, "administrator_protected", "A current Jellyfin administrator is required.");
        return id;
    }
    private IActionResult Respond(Func<object> operation)
    {
        Response.Headers.CacheControl = "no-store, private";
        try { Actor();if (!ModelState.IsValid) throw new HubAdapterException(400, "invalid_request", "Invalid JSON request.");return new JsonResult(HubWire.Success(operation()), HubWire.Json); }
        catch (HubAdapterException e) { return new JsonResult(HubWire.Failure(e.Code, e.Message, e.Status == 503), HubWire.Json) { StatusCode = e.Status }; }
        catch (JsonException) { return new JsonResult(HubWire.Failure("invalid_request", "Invalid JSON field or enum value."), HubWire.Json) { StatusCode = 400 }; }
        catch (Exception) { return new JsonResult(HubWire.Failure("unavailable", "Private SyncPlay adapter is unavailable.", true), HubWire.Json) { StatusCode = 503 }; }
    }
    private IActionResult Write(string operation, JsonElement value, Guid? user = null, bool preview = false) => Respond(() =>
        policies.Execute(Actor(), operation, value.ValueKind == JsonValueKind.Undefined ? new JsonObject() : JsonNode.Parse(value.GetRawText()) as JsonObject
            ?? throw new HubAdapterException(400, "invalid_request", "A JSON object is required."), Request.Headers.IfMatch.FirstOrDefault(), Request.Headers["X-Command-Id"].FirstOrDefault(), user, preview));
    [HttpGet("capabilities")] public IActionResult Capabilities() => Respond(policies.Capabilities);
    [HttpGet("settings")] public IActionResult Settings() => Respond(policies.Settings);
    [HttpPut("settings")] public IActionResult Settings([FromBody] JsonElement request) => Write("settings", request);
    [HttpGet("policy")] public IActionResult Policy([FromQuery] int skip = 0, [FromQuery] int limit = 50) => Respond(() => policies.Policy(skip, limit));
    [HttpPut("policy")] public IActionResult Policy([FromBody] JsonElement request) => Write("policy", request);
    [HttpPost("policy/preview")] public IActionResult Preview([FromBody] JsonElement request) => Write("preview", request, preview: true);
    [HttpPost("policy/reset")] public IActionResult Reset([FromBody] JsonElement request) => Write("reset", request);
    [HttpGet("users/{userId:guid}/effective-policy")] public IActionResult Effective(Guid userId) => Respond(() => policies.Effective(userId));
    [HttpPut("users/{userId:guid}/policy")] public IActionResult UserPolicy(Guid userId, [FromBody] JsonElement request) => Write("user-put", request, userId);
    [HttpDelete("users/{userId:guid}/policy")] public IActionResult DeletePolicy(Guid userId) => Write("user-delete", default, userId);
    [HttpGet("status")] public IActionResult Status() => Respond(() =>
    {
        lock (store.Gate) return new { revision = HubWire.Revision(Plugin.Instance!.Configuration), parties = store.Parties.Count,
            guests = store.Parties.Keys.Sum(store.GuestCount), integration = integration.Status() };
    });
}
