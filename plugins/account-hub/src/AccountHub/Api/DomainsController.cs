using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccountHub.Core;
using AccountHub.Security;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AccountHub.Api;

[ApiController, Authorize(Policy = Policies.RequiresElevation), Route("AccountHub/api/v1/domains"), TypeFilter(typeof(HubBodyFilter)), RequestSizeLimit(65536)]
public sealed class DomainsController(HubHost host, ExternalCommandGate commands, IAuthorizationContext authorization, IServerApplicationHost app, IHttpClientFactory clients) : ControllerBase
{
    private static string Prefix(string key) => key switch { "deletion-queue" => "DeletionQueue", "private-syncplay" => "PrivateSyncPlay", _ => throw new HubException("invalid_request", "Unknown domain") };
    private static bool Allowed(string route) => route is "capabilities" or "status" or "settings" or "settings/import-enhanced" or "policy" or "policy/preview" or "policy/reset" ||
        System.Text.RegularExpressions.Regex.IsMatch(route, "^users/[0-9a-fA-F-]{36}/(effective-policy|policy)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    public static bool AllowedCommand(string route, string method) => Allowed(route) && (route switch
    {
        "settings" or "policy" => method == "PUT",
        "settings/import-enhanced" or "policy/preview" or "policy/reset" => method == "POST",
        _ => route.EndsWith("/policy", StringComparison.Ordinal) && method is "PUT" or "DELETE"
    });
    [HttpGet("{key}/{**route}")]
    public async Task<IActionResult> Get(string key, string route, CancellationToken ct)
    {
        try
        {
            await host.RequireAdministratorAsync(HttpContext, ct);
            Response.Headers.CacheControl = "no-store, private";
            if (!Allowed(route) || route is "policy/preview" or "policy/reset" or "settings/import-enhanced" || Request.Query.Keys.Any(k => k is not ("skip" or "limit" or "refresh"))) throw new HubException("invalid_request", "Unsupported domain read");
            var result = await Forward(key, route + Request.QueryString, HttpMethod.Get, null, null, null, ct);
            return new ContentResult { StatusCode = result.Status, ContentType = "application/json", Content = result.Body };
        }
        catch (SecurityFailure ex) { return Failure(ex.Code, ex.Status); }
        catch (HubException ex) { return Failure(ex.Code, 400); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException) { return Failure("unavailable", 503); }
    }
    [HttpPost("{key}/command")]
    public async Task<IActionResult> Command(string key, JsonElement body, CancellationToken ct)
    {
        try
        {
            var caller = await host.RequireAdministratorAsync(HttpContext, ct);
            Response.Headers.CacheControl = "no-store, private";
            _ = Prefix(key);
            var route = body.GetProperty("route").GetString()!; var method = body.GetProperty("method").GetString()!;
            if (!AllowedCommand(route, method)) throw new HubException("invalid_request", "Unsupported domain command");
            var payload = JsonNode.Parse(body.GetProperty("payload").GetRawText())!.AsObject();
            var commandId = body.GetProperty("commandId").GetGuid();
            payload.Remove("stepUp"); payload.Remove("stepUpVerified"); payload.Remove("confirmation");
            payload["commandId"] = commandId.ToString("D");
            var domainRevision = payload["revision"]?.GetValue<string>();
            if (string.IsNullOrEmpty(domainRevision)) throw new HubException("invalid_request", "Domain revision required");
            async Task<(int Status, string Body)> Send(CancellationToken token) => await Forward(key, route, new HttpMethod(method), method == "DELETE" ? null : payload.ToJsonString(), domainRevision, commandId, token);
            if (route == "policy/preview")
            {
                payload.Remove("commandId");
                var preview = await Send(ct); return new ContentResult { StatusCode = preview.Status, ContentType = "application/json", Content = preview.Body };
            }
            var result = await commands.ExecuteAsync(caller.UserId, "domain.command", commandId, body.GetProperty("hubRevision").GetInt64(), new { key, route, method, payload },
                (_, token) => host.AuthorizeAsync(HttpContext, CommandRisk.Destructive, token), async token =>
                {
                    var reply = await Send(token);
                    using var document = JsonDocument.Parse(reply.Body);
                    return JsonSerializer.SerializeToElement(new { httpStatus = reply.Status, body = document.RootElement }, Contracts.Wire.Options);
                }, ct: ct);
            if (!result.TryGetProperty("httpStatus", out var status)) return Failure("remediation_pending", 409);
            return new ContentResult { StatusCode = status.GetInt32(), ContentType = "application/json", Content = result.GetProperty("body").GetRawText() };
        }
        catch (SecurityFailure ex) { return Failure(ex.Code, ex.Status); }
        catch (HubException ex) { return Failure(ex.Code, ex.Code is "revision_changed" or "command_conflict" ? 409 : 400); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) { return Failure("invalid_request", 400); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException) { return Failure("remediation_pending", 503); }
    }
    private async Task<(int Status, string Body)> Forward(string key, string route, HttpMethod method, string? body, string? revision, Guid? commandId, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(25)); ct = deadline.Token;
        var info = await authorization.GetAuthorizationInfo(HttpContext);
        var target = app.GetLocalApiUrl("127.0.0.1", "http", app.HttpPort).TrimEnd('/') + '/' + Prefix(key) + "/admin/account-hub/v1/" + route;
        using var request = new HttpRequestMessage(method, target);
        request.Headers.Authorization = new AuthenticationHeaderValue("MediaBrowser", "Token=\"" + info.Token + "\"");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (revision is not null) request.Headers.TryAddWithoutValidation("If-Match", revision);
        if (commandId is not null) request.Headers.Add("X-Command-Id", commandId.Value.ToString("D"));
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await clients.CreateClient("AccountHub.Local").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0) { if (memory.Length + count > 262144) throw new IOException("Domain response exceeds limit"); memory.Write(buffer, 0, count); }
        var json = Encoding.UTF8.GetString(memory.ToArray());
        if (string.IsNullOrWhiteSpace(json)) return ((int)response.StatusCode, JsonSerializer.Serialize(new { apiVersion = "1", ok = false, error = new { code = "domain_unavailable", retryable = (int)response.StatusCode >= 500 } }));
        using var valid = JsonDocument.Parse(json);
        return ((int)response.StatusCode, json);
    }
    private IActionResult Failure(string code, int status) => new JsonResult(new { apiVersion = "1", ok = false, error = new { code, message = code, retryable = status >= 500 } }, Contracts.Wire.Options) { StatusCode = status };
}
