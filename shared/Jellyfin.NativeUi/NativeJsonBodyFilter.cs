using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.PluginSupport;

public sealed class NativeJsonBodyFilter : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        if (HttpMethods.IsGet(request.Method)) { await next(); return; }
        request.EnableBuffering(16384, 16384);
        try
        {
            if (request.Body.CanSeek) request.Body.Position = 0;
            using var document = await JsonDocument.ParseAsync(request.Body, new JsonDocumentOptions { MaxDepth = 16 }, context.HttpContext.RequestAborted);
            Validate(document.RootElement);
            request.Body.Position = 0;
        }
        catch (Exception error) when (error is JsonException or IOException)
        { context.Result = new BadRequestObjectResult(new { apiVersion = "1", ok = false, error = new { code = "invalid_request", message = "Invalid, duplicate-property or oversized JSON body" } }); return; }
        await next();
    }
    private static void Validate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw new JsonException(); Validate(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Validate(item);
    }
}
