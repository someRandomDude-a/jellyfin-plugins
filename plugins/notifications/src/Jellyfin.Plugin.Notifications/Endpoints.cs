using AccountHub.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;

namespace Jellyfin.Plugin.Notifications;

[ApiController, Authorize, RequestSizeLimit(262144)]
public sealed class NotificationsController(EndpointAdapter adapter) : ControllerBase
{
    [Route("Notifications/api/v1/notifications/{**tail}")]
    [Route("Notifications/api/v1/settings/notifications/{**tail}")]
    public Task Protected() => adapter.ExecuteAsync(HttpContext);
    [AllowAnonymous, HttpPost("Notifications/api/v1/producer/notifications")]
    [HttpPost("Notifications/api/v1/producer/audit")]
    public Task Producer() => adapter.ExecuteAsync(HttpContext);
    [AllowAnonymous, HttpGet("Notifications/assets/bell.js")]
    [HttpGet("Notifications/assets/bell.css")]
    [HttpGet("Notifications/assets/settings.js")]
    public Task Assets() => adapter.ExecuteAsync(HttpContext);
}
public sealed class EndpointAdapter(IServiceProvider services)
{
    private readonly Lazy<RouteEndpoint[]> routes=new(()=>{
        var builder=new Routes(services);
        builder.MapAccountHubNotifications(false,"/Notifications/api/v1","/Notifications/assets");
        return builder.DataSources.SelectMany(s=>s.Endpoints).Cast<RouteEndpoint>().ToArray();
    });
    public async Task ExecuteAsync(HttpContext context)
    {
        foreach(var route in routes.Value)
        {
            var methods=route.Metadata.GetMetadata<HttpMethodMetadata>();
            if(methods is not null && !methods.HttpMethods.Contains(context.Request.Method,StringComparer.OrdinalIgnoreCase))continue;
            var values=new RouteValueDictionary();
            if(!new TemplateMatcher(TemplateParser.Parse(route.RoutePattern.RawText!),new RouteValueDictionary()).TryMatch(context.Request.Path,values))continue;
            var previous=context.Request.RouteValues; context.Request.RouteValues=values;
            try { await route.RequestDelegate!(context); } finally { context.Request.RouteValues=previous; }
            return;
        }
        context.Response.StatusCode=404;
    }
    private sealed class Routes(IServiceProvider services):IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider=>services;
        public ICollection<EndpointDataSource> DataSources { get; }=new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder()=>new ApplicationBuilder(services);
    }
}
