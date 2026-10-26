using System.Net;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using Microsoft.AspNetCore.DataProtection;

namespace Jellyfin.Plugin.AutomaticDeletionRequests;

public sealed record ArrConnection(string Url,string ProtectedKey,long Revision);
public sealed record ArrConnectionUpdate(long Revision,string Url,string? ApiKey);
public sealed class ArrConnections
{
    private readonly string directory;
    private readonly IDataProtector protector;
    private readonly IPluginManager plugins;
    private readonly object gate=new();
    public ArrConnections(IApplicationPaths paths,IPluginManager plugins) {
        directory=Path.Combine(paths.DataPath,"automatic-deletion-requests","metadata");
        this.plugins=plugins;
        protector=DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory,"keys")),b=>b.SetApplicationName("AutomaticDeletionRequests.Metadata.1")).CreateProtector("arr-keys");
    }
    private static void Source(string source) {if(source is not ("sonarr" or "radarr"))throw new InvalidOperationException("Unknown metadata source");}
    public static bool Valid(string url,string key)=>url.Length<=2048&&Uri.TryCreate(url,UriKind.Absolute,out var uri)&&uri.Scheme is "http" or "https"&&uri.UserInfo.Length==0&&uri.Query.Length==0&&uri.Fragment.Length==0&&key.Length is >0 and <=512&&key.All(c=>c is >=(char)33 and <=(char)126);
    public ArrConnection? Read(string source) {
        Source(source);lock(gate){var file=Path.Combine(directory,source+".json");if(!File.Exists(file)){if(File.Exists(file+".installed")||File.Exists(file+".pending"))throw new InvalidOperationException("Connection storage unavailable");return null;}
            if(new FileInfo(file).Length>16384)throw new InvalidOperationException("Connection storage unavailable");
            var saved=JsonSerializer.Deserialize<ArrConnection>(File.ReadAllText(file))??throw new InvalidOperationException("Connection storage unavailable");
            if(saved.Revision<1||!Valid(saved.Url,protector.Unprotect(saved.ProtectedKey)))throw new InvalidOperationException("Connection storage unavailable");return saved;}
    }
    public object Status(string source){var saved=Read(source);return new{source,url=saved?.Url??"",keyConfigured=saved is not null,revision=saved?.Revision??0};}
    public void Save(string source,ArrConnectionUpdate body) {
        Source(source);lock(gate){var saved=Read(source);if((saved?.Revision??0)!=body.Revision)throw new InvalidOperationException("Revision changed");
            var key=string.IsNullOrEmpty(body.ApiKey)?saved is null?"":protector.Unprotect(saved.ProtectedKey):body.ApiKey;
            // Never forward a retained key to a different host/address.
            if(string.IsNullOrEmpty(body.ApiKey)&&saved is not null&&saved.Url.TrimEnd('/')!=body.Url.TrimEnd('/'))throw new InvalidOperationException("Enter a key for the new endpoint");
            if(!Valid(body.Url,key))throw new InvalidOperationException("Invalid metadata connection");
            Directory.CreateDirectory(directory);var file=Path.Combine(directory,source+".json");File.WriteAllText(file+".installed","1");
            var value=new ArrConnection(new Uri(body.Url).AbsoluteUri.TrimEnd('/'),protector.Protect(key),body.Revision+1);
            File.WriteAllText(file+".pending",JsonSerializer.Serialize(value));File.Move(file+".pending",file,true);
        }
    }
    public async Task<JsonElement> GetAsync(string source,string path,CancellationToken ct) {
        var saved=Read(source)??throw new InvalidOperationException("Metadata source not configured");
        return await GetAsync(saved.Url,protector.Unprotect(saved.ProtectedKey),path,ct);
    }
    private static async Task<JsonElement> GetAsync(string url,string key,string path,CancellationToken ct) {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var handler=new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false,Credentials=null,AutomaticDecompression=DecompressionMethods.None};
        using var client=new HttpClient(handler);using var request=new HttpRequestMessage(HttpMethod.Get,url.TrimEnd('/')+"/api/v3/"+path);
        request.Headers.Add("X-Api-Key",key);
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
        if(response.StatusCode!=HttpStatusCode.OK||response.Content.Headers.ContentLength>8_000_000)throw new InvalidOperationException("Metadata response unavailable");
        await using var input=await response.Content.ReadAsStreamAsync(deadline.Token);using var buffer=new MemoryStream();var chunk=new byte[16384];int size;
        while((size=await input.ReadAsync(chunk,deadline.Token))>0){if(buffer.Length+size>8_000_000)throw new InvalidOperationException("Metadata response too large");buffer.Write(chunk,0,size);}
        using var json=JsonDocument.Parse(buffer.ToArray(),new(){MaxDepth=64});return json.RootElement.Clone();
    }
    public async Task TestAsync(string source,CancellationToken ct){var status=await GetAsync(source,"system/status",ct);if(status.ValueKind!=JsonValueKind.Object||!status.TryGetProperty("appName",out var app)||!string.Equals(app.GetString(),source,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Wrong metadata application");}
    public async Task ImportAsync(string source,long revision,CancellationToken ct) {
        Source(source);
        var plugin=plugins.Plugins.SingleOrDefault(p=>p.IsEnabledAndSupported&&p.Id==Guid.Parse("f69e946a-4b3c-4e9a-8f0a-8d7c1b2c4d9b"))?.Instance as IHasPluginConfiguration;
        if(plugin is null)throw new InvalidOperationException("Enhanced connection unavailable");
        var found=Discover(plugin.Configuration,source)??throw new InvalidOperationException("One enabled Enhanced connection is required");
        var status=await GetAsync(found.Url,found.Key,"system/status",ct);
        if(!status.TryGetProperty("appName",out var app)||!string.Equals(app.GetString(),source,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Wrong metadata application");
        Save(source,new(revision,found.Url,found.Key));
    }
    public static (string Url,string Key)? Discover(object config,string source) {
        Source(source);var prefix=source=="sonarr"?"Sonarr":"Radarr";
        try {
            object? Get(object o,string name)=>o.GetType().GetProperty(name)?.GetValue(o);
            var corrupt=config.GetType().GetMethod("Is"+prefix+"InstancesCorrupt")?.Invoke(config,null);
            if(corrupt is true)return null;
            var method=config.GetType().GetMethod("GetEnabled"+prefix+"Instances");
            if(method is not null){var values=(method.Invoke(config,null) as System.Collections.IEnumerable)?.Cast<object>().Take(2).ToArray();if(values is not {Length:1})return null;var url=Get(values[0],"Url") as string;var key=Get(values[0],"ApiKey") as string;return url is not null&&key is not null&&Valid(url,key)?(url,key):null;}
            var instances=Get(config,prefix+"Instances") as string;
            if(!string.IsNullOrWhiteSpace(instances)){using var data=JsonDocument.Parse(instances);if(data.RootElement.ValueKind!=JsonValueKind.Array||data.RootElement.GetArrayLength()!=0)return null;}
            var legacyUrl=Get(config,prefix+"Url") as string;var legacyKey=Get(config,prefix+"ApiKey") as string;
            return legacyUrl is not null&&legacyKey is not null&&Valid(legacyUrl,legacyKey)?(legacyUrl,legacyKey):null;
        }catch{return null;}
    }
}
