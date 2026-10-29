using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LibraryNotifications;

public sealed class LibraryNoticeWorker(IServiceProvider services, ILibraryNoticeHost host, ILogger<LibraryNoticeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        ILibraryManager? library = null; LibraryNoticeStore? store = null; bool subscribed = false;
        void Added(object? sender, ItemChangeEventArgs change)
        {
            try { if (host.Ready && host.CaptureLocally && change.Item is { } item && LibraryNoticeFactory.Kind(item) is { } kind && !store!.Capture(item.Id, kind, DateTimeOffset.UtcNow)) logger.LogWarning("Library notification intake is full; inspect notification health. No media paths are logged."); }
            catch { logger.LogWarning("Library notification capture failed; inspect storage health. No item details are logged."); }
        }
        try
        {
            while (!services.GetRequiredService<IServerApplicationHost>().CoreStartupHasCompleted || !host.Ready) await Task.Delay(1000, ct);
            store = services.GetRequiredService<LibraryNoticeStore>(); library = services.GetRequiredService<ILibraryManager>();
            // Keep the handler attached, but gate capture at event time. Opting in must not
            // lose the first additions while waiting for the worker's next polling cycle.
            if(host.ObserveNative) { library.ItemAdded += Added; subscribed = true; }
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (host.Ready) foreach (var job in store.Due(DateTimeOffset.UtcNow))
                    {
                        try
                        {
                            ct.ThrowIfCancellationRequested();
                            var notice = job.Notice;
                            if (notice is null)
                            {
                                var item = library.GetItemById(job.ItemId);
                                if (item is null || LibraryNoticeFactory.Kind(item) is null) { store.InboxDone(job.Id); store.DiscordDone(job.Id); continue; }
                                var audience = host.IncludeAudience ? services.GetRequiredService<IUserManager>().GetUsers().Where(user => !user.HasPermission(PermissionKind.IsDisabled) && item.IsVisibleStandalone(user)).Take(10000).Select(user => user.Id).ToArray() : [];
                                notice = LibraryNoticeFactory.Create(job.Id, item, library.GetCollectionFolders(item).Select(folder => folder.Id).ToArray(), audience, job.Created);
                                if (job.LegacyInbox) notice = notice with { LegacySource = "account-hub" };
                                if(host.IncludeAudience){
                                    var favourites=new List<Guid>();var watchlisted=new List<Guid>();var manager=services.GetRequiredService<IUserManager>();var data=services.GetRequiredService<IUserDataManager>();
                                    var parent=item is MediaBrowser.Controller.Entities.TV.Episode episode?library.GetItemById(episode.SeriesId):null;
                                    foreach(var id in audience){try{var user=manager.GetUserById(id);if(user is null)continue;
                                        var interests=LibraryNoticeFactory.Interests(data.GetUserData(user,item),parent is not null&&parent.IsVisibleStandalone(user)?data.GetUserData(user,parent):null);
                                        if(interests.Favourite)favourites.Add(id);if(interests.Watchlisted)watchlisted.Add(id);
                                    }catch{/* Missing personal interest evidence does not affect other categories. */}}
                                    Guid[]? requesters=null;var provider=services.GetKeyedService<Func<Guid,CancellationToken,Task<Guid[]?>>>("Notifications.LibraryRequesters.1");
                                    if(provider is not null)try{requesters=await provider(parent?.Id??item.Id,ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch{/* Requested category stays unknown. */}
                                    notice=notice with{EnrichmentVersion=2,FavouriteAudience=favourites.ToArray(),RequesterAudience=requesters,WatchlistedAudience=watchlisted.ToArray()};
                                }
                                store.Prepare(job.Id, notice);
                            }
                            if (!job.InboxDone) { if (await host.InboxAsync(notice, ct)) store.InboxDone(job.Id); else store.Defer(job.Id, DateTimeOffset.UtcNow); }
                            if (job.DiscordState == 0)
                            {
                                if (host.DirectDiscord)
                                {
                                    if (!host.DiscordConnected) continue;
                                    store.DiscordClaim(job.Id); // Persist ambiguity BEFORE an external send; never blindly replay it.
                                    try { if (await host.DiscordAsync(notice, ct)) store.DiscordDone(job.Id); else store.DiscordUncertain(job.Id); }
                                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { store.DiscordUncertain(job.Id); throw; }
                                    catch { store.DiscordUncertain(job.Id); }
                                }
                                else if (await host.DiscordAsync(notice, ct)) store.DiscordDone(job.Id);
                                else store.Defer(job.Id, DateTimeOffset.UtcNow);
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch
                        {
                            store.Defer(job.Id, DateTimeOffset.UtcNow);
                            logger.LogWarning("A library notification was deferred; inspect intake health. No media details are logged.");
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch { logger.LogWarning("Library notification processing deferred; inspect dependency/storage health. No media details are logged."); }
                await Task.Delay(5000, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { logger.LogWarning("Library notification observer is unavailable; Jellyfin remains available."); }
        finally { if (subscribed) library!.ItemAdded -= Added; }
    }
}
