using System.Diagnostics;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Jellyfin.Data.Enums;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;

namespace Jellyfin.Plugin.AutomaticDeletionRequests;

public sealed class JellyfinMetadata(ILibraryManager library,IUserManager users,IUserDataManager userData,IMediaSourceManager? mediaSources=null,MediaBrowser.Controller.Persistence.ILinkedChildrenService? links=null)
{
    private static readonly string[] WatchFields=["seenBy","favoritedBy","playCount","lastPlayedAt","viewedEpisodes","allEpisodesSeenBy","watchers","watchedByAll","unwatchedByAll","partiallyWatchedBy","resumePositionMinutes","lastViewedAt","seenSinceAddedBy","allEpisodesSeenSinceAddedBy","completedWatchCount","newestWatchedEpisodeAt"];
    public Dictionary<string,JsonElement> Read(BaseItem item,IEnumerable<string> fields,CancellationToken ct) {
        var wanted=fields.ToHashSet();var facts=new Dictionary<string,JsonElement>();
        if(wanted.Any(f=>f.StartsWith("jellyfin.collectionSibling",StringComparison.Ordinal)))foreach(var fact in ReadSiblings(item,ct))facts[fact.Key]=fact.Value;
        void Put(string name,object? value){if(value is not null)facts["jellyfin."+name]=JsonSerializer.SerializeToElement(value);}
        foreach(var (field,provider)in new[]{("tmdbId","Tmdb"),("tvdbId","Tvdb"),("imdbId","Imdb")})if(wanted.Contains("jellyfin."+field))facts["jellyfin."+field]=item.ProviderIds.TryGetValue(provider,out var id)&&!string.IsNullOrWhiteSpace(id)?JsonSerializer.SerializeToElement(id):MetadataEvidence.Absent;
        if(new[]{"people","actors","directors","writers"}.Any(f=>wanted.Contains("jellyfin."+f))){
            var people=library.GetPeople(item).Take(1001).ToArray();if(people.Length>1000)throw new InvalidOperationException("People metadata capacity exceeded");
            Put("people",people.Select(p=>p.Name).Distinct().ToArray());foreach(var (field,kind)in new[]{("actors","Actor"),("directors","Director"),("writers","Writer")})Put(field,people.Where(p=>p.Type.ToString()==kind).Select(p=>p.Name).Distinct().ToArray());
        }
        if(links is not null)foreach(var (field,kind)in new[]{("collections",BaseItemKind.BoxSet),("playlists",BaseItemKind.Playlist)})if(wanted.Contains("jellyfin."+field)||wanted.Contains("jellyfin."+(field=="collections"?"collectionIds":"playlistIds"))){
            var parents=links.GetManualLinkedParentIds(item.Id,kind).Take(1001).ToArray();if(parents.Length>1000)throw new InvalidOperationException("Membership metadata capacity exceeded");
            var containers=parents.Select(id=>library.GetItemById(id)??throw new InvalidOperationException("Membership metadata unavailable")).ToArray();
            Put(field,containers.Select(c=>c.Name).ToArray());Put(field=="collections"?"collectionIds":"playlistIds",parents.Select(id=>id.ToString("D")).ToArray());
        }
        var watch=WatchFields.Any(f=>wanted.Contains("jellyfin."+f));
        var streams=new[]{"videoCodecs","audioCodecs","audioLanguages","subtitleLanguages","videoRanges","maxVideoWidth","maxVideoHeight","maxAudioChannels"}.Any(f=>wanted.Contains("jellyfin."+f));
        var episodes=item is Series&&(watch||streams||new[]{"episodeCount","lastEpisodeAdded","lastEpisodeAired"}.Any(f=>wanted.Contains("jellyfin."+f)));
        if(!watch&&!episodes&&!streams)return facts;
        var started=Stopwatch.GetTimestamp();
        void Budget(){ct.ThrowIfCancellationRequested();if(Stopwatch.GetElapsedTime(started)>TimeSpan.FromSeconds(10))throw new InvalidOperationException("Native metadata scan budget exceeded");}
        BaseItem[] parts=[item];
        if(episodes){
            // A deleted/non-container ParentId can broaden native queries. Recheck
            // the concrete series immediately before every descendant query.
            if(library.GetItemById(item.Id) is not Series current||current.Id!=item.Id)throw new InvalidOperationException("Series scope unavailable");
            parts=library.GetItemList(new InternalItemsQuery{ParentId=item.Id,Recursive=true,IncludeItemTypes=[BaseItemKind.Episode],Limit=5001}).ToArray();
            if(parts.Length>5000||parts.Any(p=>p is not Episode e||e.SeriesId!=item.Id))throw new InvalidOperationException("Episode metadata incomplete");
            Put("episodeCount",parts.Length);
            if(parts.Length>0&&parts.All(p=>p.DateCreated!=default))Put("lastEpisodeAdded",parts.Max(p=>p.DateCreated));
            var aired=parts.Where(p=>p.PremiereDate is {} date&&date<=DateTime.UtcNow).ToArray();if(aired.Length>0)Put("lastEpisodeAired",aired.Max(p=>p.PremiereDate));
        }
        if(streams&&mediaSources is not null){
            var all=new List<MediaBrowser.Model.Entities.MediaStream>();foreach(var part in parts){Budget();all.AddRange(mediaSources.GetMediaStreams(part.Id));if(all.Count>50000)throw new InvalidOperationException("Stream metadata capacity exceeded");}
            var video=all.Where(s=>s.Type==MediaBrowser.Model.Entities.MediaStreamType.Video).ToArray();var audio=all.Where(s=>s.Type==MediaBrowser.Model.Entities.MediaStreamType.Audio).ToArray();var subs=all.Where(s=>s.Type==MediaBrowser.Model.Entities.MediaStreamType.Subtitle).ToArray();
            void List(string field,IEnumerable<string?> values)=>Put(field,values.Where(v=>!string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).Take(1001).ToArray());
            List("videoCodecs",video.Select(s=>s.Codec));List("audioCodecs",audio.Select(s=>s.Codec));List("audioLanguages",audio.Select(s=>s.Language));List("subtitleLanguages",subs.Select(s=>s.Language));List("videoRanges",video.Select(s=>s.VideoRange.ToString()));
            foreach(var (field,values)in new[]{("maxVideoWidth",video.Select(s=>s.Width)),("maxVideoHeight",video.Select(s=>s.Height)),("maxAudioChannels",audio.Select(s=>s.Channels))}){var known=values.Where(v=>v.HasValue).Select(v=>v!.Value).ToArray();if(known.Length>0)Put(field,known.Max());else facts["jellyfin."+field]=MetadataEvidence.Absent;}
        }
        if(!watch)return facts;
        var accounts=users.GetUsers().Where(u=>!u.HasPermission(PermissionKind.IsDisabled)).Take(501).ToArray();
        if(accounts.Length>500||(long)accounts.Length*Math.Max(1,parts.Length)>100000)throw new InvalidOperationException("Watch metadata capacity exceeded");
        var seen=new HashSet<Guid>();var favourites=new HashSet<Guid>();var watchers=new HashSet<Guid>();var allSeen=new HashSet<Guid>();var partial=new HashSet<Guid>();var viewed=new HashSet<Guid>();long plays=0;long resume=0;DateTime? last=null;
        var sinceAdded=new HashSet<Guid>();var allSinceAdded=new HashSet<Guid>();var completedDates=new Dictionary<Guid,DateTime>();long completedCount=0;var completedDatesKnown=true;
        foreach(var user in accounts){
            Budget();var top=userData.GetUserData(user,item)??throw new InvalidOperationException("Native user metadata unavailable");
            if(top.IsFavorite)favourites.Add(user.Id);if(top.Played)seen.Add(user.Id);
            var every=parts.Length>0;
            var everyAfter=parts.Length>0&&item.DateCreated!=default;
            foreach(var part in parts){
                Budget();var data=part.Id==item.Id?top:userData.GetUserData(user,part)??throw new InvalidOperationException("Native user metadata unavailable");
                if(data.PlayCount<0)throw new InvalidOperationException("Invalid native play count");plays=checked(plays+data.PlayCount);
                every&=data.Played;if(data.Played)viewed.Add(part.Id);if(data.Played||data.PlayCount>0)watchers.Add(user.Id);
                if(!data.Played&&data.PlaybackPositionTicks>0)partial.Add(user.Id);resume=Math.Max(resume,data.PlaybackPositionTicks);
                everyAfter&=data.Played&&data.LastPlayedDate>=item.DateCreated;
                if(data.Played){completedCount++;if(data.LastPlayedDate is {} completed){if(!completedDates.TryGetValue(part.Id,out var previous)||completed>previous)completedDates[part.Id]=completed;if(item.DateCreated!=default&&completed>=item.DateCreated)sinceAdded.Add(user.Id);}else completedDatesKnown=false;}
                if(data.LastPlayedDate is {} date&&(last is null||date>last))last=date;
            }
            if(every)allSeen.Add(user.Id);
            if(everyAfter)allSinceAdded.Add(user.Id);
        }
        Put("seenBy",seen.Order().Select(id=>id.ToString("D")).ToArray());Put("favoritedBy",favourites.Order().Select(id=>id.ToString("D")).ToArray());
        Put("playCount",plays);Put("lastPlayedAt",last);
        if(last is null)facts["jellyfin.lastPlayedAt"]=MetadataEvidence.Absent;
        if(accounts.Length>0){Put("watchedByAll",allSeen.Count==accounts.Length);Put("unwatchedByAll",watchers.Count==0&&partial.Count==0);}
        Put("partiallyWatchedBy",partial.Order().Select(id=>id.ToString("D")).ToArray());Put("resumePositionMinutes",TimeSpan.FromTicks(resume).TotalMinutes);
        Put("completedWatchCount",completedCount);
        if(completedDatesKnown){
            facts["jellyfin.lastViewedAt"]=completedDates.Count>0?JsonSerializer.SerializeToElement(completedDates.Values.Max()):MetadataEvidence.Absent;
            if(item.DateCreated!=default){Put("seenSinceAddedBy",sinceAdded.Order().Select(id=>id.ToString("D")).ToArray());if(item is Series)Put("allEpisodesSeenSinceAddedBy",allSinceAdded.Order().Select(id=>id.ToString("D")).ToArray());}
            if(item is Series){var watched=parts.Where(p=>completedDates.ContainsKey(p.Id)).Cast<Episode>().ToArray();if(watched.All(e=>e.ParentIndexNumber is not null&&e.IndexNumber is not null)){
                var newest=watched.OrderByDescending(e=>e.ParentIndexNumber).ThenByDescending(e=>e.IndexNumber).FirstOrDefault();facts["jellyfin.newestWatchedEpisodeAt"]=newest is null?MetadataEvidence.Absent:JsonSerializer.SerializeToElement(completedDates[newest.Id]);
            }}
        }
        if(item is Series){Put("viewedEpisodes",viewed.Count);Put("watchers",watchers.Order().Select(id=>id.ToString("D")).ToArray());Put("allEpisodesSeenBy",allSeen.Order().Select(id=>id.ToString("D")).ToArray());}
        return facts;
    }
    private Dictionary<string,JsonElement> ReadSiblings(BaseItem item,CancellationToken ct){
        var facts=new Dictionary<string,JsonElement>();if(links is null)return facts;
        var started=Stopwatch.GetTimestamp();void Budget(){ct.ThrowIfCancellationRequested();if(Stopwatch.GetElapsedTime(started)>TimeSpan.FromSeconds(10))throw new InvalidOperationException("Sibling metadata time budget exceeded");}
        var parents=links.GetManualLinkedParentIds(item.Id,BaseItemKind.BoxSet).Take(129).ToArray();if(parents.Length>128)throw new InvalidOperationException("Sibling collection capacity exceeded");
        var ids=new HashSet<Guid>();foreach(var parent in parents){Budget();if(library.GetItemById(parent) is not MediaBrowser.Controller.Entities.Movies.BoxSet)throw new InvalidOperationException("Sibling collection unavailable");var children=links.GetLinkedChildrenIds(parent,1001).ToArray();if(children.Length>1000)throw new InvalidOperationException("Sibling collection incomplete");ids.UnionWith(children);if(ids.Count>5000)throw new InvalidOperationException("Sibling capacity exceeded");}
        ids.Remove(item.Id);var siblings=ids.Select(id=>library.GetItemById(id)??throw new InvalidOperationException("Sibling unavailable")).ToArray();
        var accounts=users.GetUsers().Where(u=>!u.HasPermission(PermissionKind.IsDisabled)).Take(501).ToArray();if(accounts.Length is 0 or >500||(long)accounts.Length*Math.Max(1,siblings.Length)>100000)throw new InvalidOperationException("Sibling watch capacity exceeded");
        var seen=new HashSet<Guid>();long plays=0;DateTime? last=null;
        foreach(var user in accounts)foreach(var sibling in siblings){Budget();var data=userData.GetUserData(user,sibling)??throw new InvalidOperationException("Sibling watch metadata unavailable");if(data.PlayCount<0)throw new InvalidOperationException("Invalid sibling play count");plays=checked(plays+data.PlayCount);if(data.Played||data.PlayCount>0)seen.Add(user.Id);if(data.LastPlayedDate is {} date&&(last is null||date>last))last=date;}
        facts["jellyfin.collectionSiblingSeenBy"]=JsonSerializer.SerializeToElement(seen.Order().Select(id=>id.ToString("D")).ToArray());facts["jellyfin.collectionSiblingPlayCount"]=JsonSerializer.SerializeToElement(plays);
        facts["jellyfin.collectionSiblingLastPlayedAt"]=last is null?MetadataEvidence.Absent:JsonSerializer.SerializeToElement(last);return facts;
    }
}
