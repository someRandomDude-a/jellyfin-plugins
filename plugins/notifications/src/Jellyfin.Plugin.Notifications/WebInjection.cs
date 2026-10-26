using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using AccountHub.Notifications;

namespace Jellyfin.Plugin.Notifications;

public sealed class WebInjection:IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)=>app=>{
        // Jellyfin's native socket middleware consumes upgrades before MVC and
        // requires its own URL/session token. Intercept only this grant-bound
        // endpoint; native authorization is rechecked by LiveNotifications.
        app.UseWebSockets();
        app.Use(async(context,continuation)=>{
            if(HttpMethods.IsGet(context.Request.Method)&&(context.Request.Path.Value??"").EndsWith("/Notifications/api/v1/notifications/live",StringComparison.Ordinal)&&context.WebSockets.IsWebSocketRequest){
                var services=context.RequestServices;var result=await services.GetRequiredService<LiveNotifications>().ConnectAsync(context,services.GetRequiredService<INotificationHost>(),services.GetRequiredService<NotificationStore>());
                await result.ExecuteAsync(context);return;
            }
            await continuation();
        });
        app.Use(async(context,continuation)=>{
            var path=context.Request.Path.Value??"";
            var suffix=new[]{"/web/index.html","/web/","/web"}.FirstOrDefault(s=>path.EndsWith(s,StringComparison.OrdinalIgnoreCase));
            if(!HttpMethods.IsGet(context.Request.Method)||suffix is null){await continuation();return;}
            var prefix=(context.Request.PathBase.Value??"")+path[..^suffix.Length];
            context.Request.Headers.Remove("Accept-Encoding");
            foreach(var header in new[]{"Range","If-Range","If-None-Match","If-Modified-Since"})context.Request.Headers.Remove(header);
            var original=context.Response.Body;
            using var buffer=new MemoryStream(); context.Response.Body=buffer;
            try{await continuation();}finally{context.Response.Body=original;}
            var bytes=buffer.ToArray();
            if(bytes.Length<=2*1024*1024 && context.Response.StatusCode==200 && context.Response.ContentType?.StartsWith("text/html",StringComparison.OrdinalIgnoreCase)==true && string.IsNullOrEmpty(context.Response.Headers.ContentEncoding))
            {
                var html=Encoding.UTF8.GetString(bytes); var end=html.LastIndexOf("</body>",StringComparison.OrdinalIgnoreCase);
                if(end>=0&&!html.Contains("data-notifications-bootstrap",StringComparison.Ordinal))
                {
                    var root=System.Net.WebUtility.HtmlEncode(prefix+"/Notifications");
                    html=html.Insert(end,$"<link rel=\"stylesheet\" href=\"{root}/assets/bell.css\"><script defer data-notifications-bootstrap data-notifications-api=\"Notifications/api/v1/\" src=\"{root}/assets/bell.js\"></script>");
                    bytes=Encoding.UTF8.GetBytes(html);context.Response.ContentLength=bytes.Length;
                    context.Response.Headers.Remove("ETag");context.Response.Headers.Remove("Last-Modified");context.Response.Headers.CacheControl="no-store";
                }
            }
            await original.WriteAsync(bytes,context.RequestAborted);
        });next(app);
    };
}
