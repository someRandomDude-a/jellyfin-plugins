using System.Reflection;
using System.Text;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AccountHub.Tests;

public sealed class HostedWebBootstrapTests
{
    public class Host:DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name switch{"get_HttpPort"=>8096,"GetLocalApiUrl"=>"http://127.0.0.1:8096/suite",_=>throw new NotSupportedException(method?.Name)};
    }
    [Theory,InlineData(true),InlineData(false)] public async Task NativeReturnHelperIsInjectedWithEitherBellOwnerAndNeverDuplicated(bool notifications)
    {
        using var services=new ServiceCollection().AddKeyedSingleton<Func<bool>>("Notifications.Owner.1",(_,_)=>()=>notifications).BuildServiceProvider();
        var host=DispatchProxy.Create<IServerApplicationHost,Host>();
        async Task<string> Adapt(string html){var context=new DefaultHttpContext();context.Request.Method="GET";context.Request.Path="/suite/web/index.html";context.Response.Body=new MemoryStream();context.Response.ContentType="text/html";
            var bootstrap=new HostedWebBootstrap(async http=>await http.Response.WriteAsync(html),host,services);await bootstrap.InvokeAsync(context);Assert.Equal("no-store",context.Response.Headers.CacheControl.ToString());return Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());}
        var output=await Adapt("<html><body>Native web</body></html>");Assert.Contains("src=\"/suite/AccountHub/assets/link-return.js\"",output);Assert.Equal(!notifications,output.Contains("data-account-hub-bootstrap"));
        // Repeated adapters preserve one helper. There is no response rewrite if no tags change.
        var context=new DefaultHttpContext();context.Request.Method="GET";context.Request.Path="/suite/web/index.html";context.Response.Body=new MemoryStream();context.Response.ContentType="text/html";
        await new HostedWebBootstrap(async http=>await http.Response.WriteAsync(output),host,services).InvokeAsync(context);
        var repeated=Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());Assert.Equal(output,repeated);
    }
}
