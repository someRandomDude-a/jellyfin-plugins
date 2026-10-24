using AccountHub.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;

namespace AccountHub.Api;

/// <summary>
/// Adapts the embedded module's HTTP handlers to Jellyfin's native MVC router. No middleware
/// branch, extra listener or authorization exemptions: only the two producer routes are anonymous.
/// </summary>
[ApiController, Authorize, RequestSizeLimit(262144)]
public sealed class NotificationEndpointsController(NotificationEndpointAdapter adapter) : ControllerBase
{
    [Route("AccountHub/api/v1/notifications/{**tail}")]
    [Route("AccountHub/api/v1/settings/notifications/{**tail}")]
    public Task Protected() => adapter.ExecuteAsync(HttpContext);

    [AllowAnonymous, HttpPost("AccountHub/api/v1/producer/notifications")]
    [HttpPost("AccountHub/api/v1/producer/audit")]
    public Task Producer() => adapter.ExecuteAsync(HttpContext);

    [AllowAnonymous, HttpGet("AccountHub/assets/notifications/bell.js")]
    [HttpGet("AccountHub/assets/notifications/bell.css")]
    [HttpGet("AccountHub/assets/notifications/settings.js")]
    public Task Asset() => adapter.ExecuteAsync(HttpContext);
}

public sealed class NotificationEndpointAdapter(IServiceProvider services)
{
    private readonly Lazy<RouteEndpoint[]> endpoints = new(() =>
    {
        var builder = new ModuleRoutes(services);
        builder.MapAccountHubNotifications(mapSecurityReceiver: false);
        return builder.DataSources.SelectMany(source => source.Endpoints).Cast<RouteEndpoint>().ToArray();
    });
    public async Task ExecuteAsync(HttpContext context)
    {
        foreach (var endpoint in endpoints.Value)
        {
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>();
            if (methods is not null && !methods.HttpMethods.Contains(context.Request.Method, StringComparer.OrdinalIgnoreCase)) continue;
            var values = new RouteValueDictionary();
            var matcher = new TemplateMatcher(TemplateParser.Parse(endpoint.RoutePattern.RawText!), new RouteValueDictionary());
            if (!matcher.TryMatch(context.Request.Path, values)) continue;
            var old = context.Request.RouteValues;
            context.Request.RouteValues = values;
            try { await endpoint.RequestDelegate!(context); }
            finally { context.Request.RouteValues = old; }
            return;
        }
        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }
    private sealed class ModuleRoutes(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider => services;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(services);
    }
}
