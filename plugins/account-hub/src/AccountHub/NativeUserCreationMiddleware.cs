using AccountHub.Core;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;

namespace AccountHub;

public sealed class NativeUserCreationMiddleware(RequestDelegate next, IServerApplicationHost app)
{
    public async Task InvokeAsync(HttpContext context, UserCreationOwnership ownership)
    {
        var prefix = new Uri(app.GetLocalApiUrl("127.0.0.1", "http", app.HttpPort)).AbsolutePath.TrimEnd('/');
        var path = context.Request.Path.Value?.TrimEnd('/');
        if (!HttpMethods.IsPost(context.Request.Method) || !string.Equals(path, prefix + "/Users/New", StringComparison.OrdinalIgnoreCase))
        { await next(context); return; }
        try { await ownership.RunAsync(true, async () => { await next(context); return true; }, context.RequestAborted); }
        catch (HubException ex)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new { apiVersion = "1", ok = false, error = new { code = ex.Code, message = ex.Message, retryable = false }, accountHubPath = prefix + "/web/index.html#/configurationpage?name=AccountHub" }, context.RequestAborted);
        }
    }
}
