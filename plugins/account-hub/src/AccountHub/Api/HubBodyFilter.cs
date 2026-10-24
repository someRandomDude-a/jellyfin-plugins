using AccountHub.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System.Text.Json;

namespace AccountHub.Api;

public sealed class HubBodyFilter : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        if (context.HttpContext.Items.ContainsKey(typeof(HubBodyFilter))) { await next(); return; }
        context.HttpContext.Items[typeof(HubBodyFilter)] = true;
        if (!request.Path.StartsWithSegments("/AccountHub/api/v1") && !request.Path.StartsWithSegments("/AccountHub/public/v1") && !request.Path.StartsWithSegments("/DiscordBridge/admin/v1")) { await next(); return; }
        // The signed receiver owns raw-body limits and authentication before JSON parsing.
        if (request.Path.Equals("/AccountHub/api/v1/security/jellyfin-security")) { await next(); return; }
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsGet(request.Method) && request.ContentLength != 0)
        {
            request.EnableBuffering(65536, 65536);
            try
            {
                using var document = await JsonDocument.ParseAsync(request.Body, new JsonDocumentOptions { MaxDepth = 24 }, context.HttpContext.RequestAborted);
                _ = CanonicalJson.Normalize(document.RootElement);
                request.Body.Position = 0;
            }
            catch (Exception ex) when (ex is JsonException or HubException or IOException)
            { context.Result = new BadRequestObjectResult(new { apiVersion = "1", ok = false, error = new { code = "invalid_request", message = "Invalid, duplicate-property or oversized JSON body", retryable = false } }); return; }
        }
        await next();
    }
}
