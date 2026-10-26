using DeletionQueue.Core;
using DeletionQueue.Infrastructure;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using System.Text.Json;

namespace Jellyfin.Plugin.DeletionQueue;

// Trusted in-process metadata only; no public endpoint, permission grant or Arr write.
public sealed class LibraryRequesterMetadata(QueueRuntime runtime,ILibraryManager library,IUserManager users,IServerApplicationHost host,ISeerrClientFactory clients)
{
    public async Task<Guid[]?> ReadAsync(Guid itemId,CancellationToken ct){
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(10));
        try{
            var item=library.GetItemById(itemId);if(item is not (Movie or Series))return null;
            if(!item.ProviderIds.TryGetValue("Tmdb",out var raw)||!int.TryParse(raw,out var tmdb)||tmdb<=0)return null;
            var config=runtime.Read().Seerr;if(config.JellyfinServerId!=host.SystemId||string.IsNullOrWhiteSpace(config.ApiKey)||string.IsNullOrWhiteSpace(config.BaseUrl))return null;
            using var seerr=clients.Create(config);var settings=await seerr.JellyfinSettingsAsync(budget.Token);
            if(!settings.TryGetProperty("serverId",out var server)||!string.Equals(server.GetString(),host.SystemId,StringComparison.OrdinalIgnoreCase))return null;
            var media=await seerr.MediaAsync(item is Series,tmdb,budget.Token);
            if(!media.TryGetProperty("id",out var id)||id.GetInt32()!=tmdb||!media.TryGetProperty("mediaInfo",out var info)||!info.TryGetProperty("tmdbId",out var mapped)||mapped.GetInt32()!=tmdb||!info.TryGetProperty("mediaType",out var kind)||kind.GetString()!=(item is Series?"tv":"movie"))return null;
            var result=new HashSet<Guid>();var found=false;
            foreach(var is4k in new[]{false,true}){
                if(!info.TryGetProperty(is4k?"jellyfinMediaId4k":"jellyfinMediaId",out var native)||native.ValueKind!=JsonValueKind.String||!Guid.TryParse(native.GetString(),out var nativeId)||nativeId!=itemId)continue;
                found=true;var owners=await SeerrOwnership.RequesterIdsAsync(info,is4k,seerr.UserAsync,userId=>users.GetUserById(userId) is {} user&&!user.HasPermission(PermissionKind.IsDisabled),budget.Token);
                if(owners is null)return null;result.UnionWith(owners);
            }
            return found?result.Order().ToArray():null;
        }catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch{return null;}
    }
}
