using System.Text.Json;
using AccountHub.Api;
using AccountHub.Notifications;
using DiscordBridge.Api;
using Jellyfin.LibraryNotifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Notifications;

public sealed class LibraryNotifications(IServiceProvider services, LibraryNoticeStore store) : ILibraryNoticeHost
{
    public bool Ready => services.GetRequiredService<NotificationStore>().Ready;
    public bool CaptureLocally => store.Settings().Enabled || services.GetKeyedService<Func<bool>>(DiscordBridgeClient.LibraryCaptureKey)?.Invoke() == true;
    public bool IncludeAudience => store.Settings().Enabled;
    public bool DirectDiscord => false;
    public bool DiscordConnected => true;
    private static Dictionary<string,string> Metadata(LibraryNotice notice,string categories)
    {
        var values=new Dictionary<string,string>{["itemId"]=notice.ItemId.ToString("D"),["imageItemId"]=notice.ItemId.ToString("D"),["libraryCategories"]=categories};
        if(notice.GroupId is {} id&&id!=Guid.Empty){values["groupId"]=id.ToString("D");if(notice.GroupLabel is {Length:>0} label)values["groupLabel"]=label;}
        return values;
    }
    public async Task<bool> InboxAsync(LibraryNotice notice, CancellationToken ct)
    {
        if (!store.Settings().Includes(notice)) return true;
        var source = notice.LegacySource == "account-hub" ? "account-hub" : "notifications";
        var publisher = services.GetRequiredService<NotificationService>();
        if (notice.EnrichmentVersion is 1 or 2) {
            var requested=(notice.RequesterAudience??[]).ToHashSet(); var favourite=(notice.FavouriteAudience??[]).ToHashSet(); var watched=(notice.WatchlistedAudience??[]).ToHashSet();
            foreach(var group in notice.Audience.Order().GroupBy(id=>"all"+(requested.Contains(id)?",requested":"")+(favourite.Contains(id)?",favourite":"")+(notice.EnrichmentVersion>=2&&watched.Contains(id)?",watchlisted":""))) {
                var index=0; foreach(var audience in group.Chunk(500)) {
                    var bytes=System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(notice.Id.ToString("D")+":inbox:"+group.Key+":"+index++));
                    await publisher.PublishLibraryAsync(new(1,new Guid(bytes.AsSpan(0,16)),source,"library."+notice.Kind+".added","info",notice.MediaTitle??notice.Title,notice.Body,audience,false,notice.OccurredAt,
                        Link:"web/index.html#/details?id="+notice.ItemId.ToString("D"),Metadata:Metadata(notice,group.Key)),ct);
                }
            }
        } else {
            var index=0; foreach(var audience in notice.Audience.Chunk(500)) {
                var bytes=System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(notice.Id.ToString("D")+":inbox:"+index++));
                await publisher.PublishLibraryAsync(new(1,new Guid(bytes.AsSpan(0,16)),source,"library."+notice.Kind+".added","info",notice.Title,notice.Body,audience,false,notice.OccurredAt,Link:"web/index.html#/details?id="+notice.ItemId.ToString("D"),Metadata:new Dictionary<string,string>{["itemId"]=notice.ItemId.ToString("D")}),ct);
            }
        }
        return true;
    }
    public async Task<bool> DiscordAsync(LibraryNotice notice, CancellationToken ct)
    {
        if (services.GetKeyedService<Func<string,CancellationToken,Task<string>>>(DiscordBridgeClient.ServiceKey) is null) return true;
        var reply = await services.GetRequiredService<DiscordBridgeClient>().CallAsync("library.publish",new { notice=notice with { Audience=[], FavouriteAudience=null,RequesterAudience=null,WatchlistedAudience=null,LegacySource=null,GroupId=null,GroupLabel=null } },ct);
        return reply is { } result && result.GetProperty("ok").GetBoolean();
    }
}
public sealed class NotificationLibraryWorker(IServiceProvider services, ILoggerFactory logging) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try {
            using var worker = new LibraryNoticeWorker(services,services.GetRequiredService<LibraryNotifications>(),logging.CreateLogger<LibraryNoticeWorker>());
            await worker.StartAsync(ct); try { if(worker.ExecuteTask is { } task) await task; } finally { await worker.StopAsync(CancellationToken.None); }
        } catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
        catch { logging.CreateLogger<NotificationLibraryWorker>().LogWarning("Library notifications unavailable; restore original storage. No state was reset."); }
    }
}
