using DiscordBridge.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DiscordBridge;

public static class BridgePublicUrl
{
    public static async Task<Uri?> ResolveAsync(IServiceProvider services,Configuration config,CancellationToken ct){
        var value=config.PublicServerUrl;
        if(string.IsNullOrWhiteSpace(value)){
            var provider=services.GetKeyedService<Func<CancellationToken,Task<string?>>>(DiscordBridgeClient.PublicUrlKey);
            if(provider is null)return null;
            value=await provider(ct).WaitAsync(TimeSpan.FromSeconds(5),ct);
        }
        return Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.UserInfo.Length==0&&uri.Query.Length==0&&uri.Fragment.Length==0?new Uri(uri.AbsoluteUri.TrimEnd('/')+'/'):null;
    }
}
