using System.Globalization;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.AutomaticDeletionRequests;

public sealed class ArrMetadata(ArrConnections connections)
{
    public async Task<Dictionary<string,JsonElement>> ReadAsync(BaseItem item,IEnumerable<string> fields,CancellationToken ct) {
        fields=fields.Distinct().ToArray();
        var result=new Dictionary<string,JsonElement>();
        var source=item is Movie?"radarr":item is Series?"sonarr":null;
        if(source is null||!fields.Any(f=>f.StartsWith(source+".",StringComparison.Ordinal)))return result;
        var provider=source=="radarr"?"Tmdb":"Tvdb";
        if(!item.ProviderIds.TryGetValue(provider,out var id)||!int.TryParse(id,out var number)||number<=0)return result;
        var data=await connections.GetAsync(source,(source=="radarr"?"movie?tmdbId=":"series?tvdbId=")+number.ToString(CultureInfo.InvariantCulture),ct);
        if(data.ValueKind!=JsonValueKind.Array||data.GetArrayLength()>10000)throw new InvalidOperationException("Metadata lookup incomplete");
        var property=source=="radarr"?"tmdbId":"tvdbId";
        var matches=data.EnumerateArray().Where(m=>Path(m,property) is {ValueKind:JsonValueKind.Number} v&&v.TryGetInt32(out var n)&&n==number).Take(2).ToArray();
        if(matches.Length==0){foreach(var field in fields.Where(f=>f.StartsWith(source+".",StringComparison.Ordinal)))result[field]=MetadataEvidence.Absent;return result;}
        if(matches.Length!=1)return result;
        var media=matches[0];
        void Put(string field,JsonElement? value){if(value is {} v&&v.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))result[source+"."+field]=v.ValueKind==JsonValueKind.Null?MetadataEvidence.Absent:v.Clone();}
        void Scalar(string field,object? value){if(value is not null)result[source+"."+field]=JsonSerializer.SerializeToElement(value);}
        foreach(var name in new[]{"title","id","added","path","monitored","qualityProfileId","status","runtime","ended","firstAired","seriesType","physicalRelease","digitalRelease","inCinemas"})Put(name,Path(media,name));
        Put("originalLanguage",Path(media,"originalLanguage.name"));
        var size=Path(media,source=="radarr"?"sizeOnDisk":"statistics.sizeOnDisk");if(size is {ValueKind:JsonValueKind.Number} bytes&&bytes.TryGetDouble(out var length))Scalar("sizeOnDisk",length/1048576d);
        foreach(var (name,path) in new (string,string)[]{("fileAdded","movieFile.dateAdded"),("filePath","movieFile.path"),("fileQuality","movieFile.quality.quality.resolution"),("fileQualityName","movieFile.quality.quality.name"),("fileAudioChannels","movieFile.mediaInfo.audioChannels"),("imdbRating","ratings.imdb.value"),("imdbVotes","ratings.imdb.votes"),("traktRating","ratings.trakt.value"),("traktVotes","ratings.trakt.votes"),("rottenTomatoesRating","ratings.rottenTomatoes.value"),("rottenTomatoesVotes","ratings.rottenTomatoes.votes"),("rating","ratings.value"),("ratingVotes","ratings.votes"),("seasonCount","statistics.seasonCount"),("episodeCount","statistics.episodeCount"),("totalEpisodeCount","statistics.totalEpisodeCount"),("episodeFileCount","statistics.episodeFileCount")})Put(name,Path(media,path));
        if(Path(media,"movieFile.qualityCutoffNotMet") is {ValueKind:JsonValueKind.True or JsonValueKind.False} cutoff)Scalar("fileQualityCutoffMet",!cutoff.GetBoolean());
        if(Path(media,"movieFile.mediaInfo.audioLanguages") is {} languages){if(languages.ValueKind==JsonValueKind.String)Scalar("fileAudioLanguages",languages.GetString()!.Split(['/',','],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries));else Put("fileAudioLanguages",languages);}
        if(Path(media,"seasons") is {ValueKind:JsonValueKind.Array} seasons&&seasons.GetArrayLength()<=1000)Scalar("monitoredSeasons",seasons.EnumerateArray().Count(s=>Path(s,"monitored") is {ValueKind:JsonValueKind.True}));
        if(source=="sonarr"&&Path(media,"statistics.episodeCount") is {ValueKind:JsonValueKind.Number} aired&&Path(media,"statistics.totalEpisodeCount") is {ValueKind:JsonValueKind.Number} total&&Path(media,"statistics.episodeFileCount") is {ValueKind:JsonValueKind.Number} files){Scalar("missingEpisodes",Math.Max(0,aired.GetInt32()-files.GetInt32()));Scalar("unairedEpisodes",Math.Max(0,total.GetInt32()-aired.GetInt32()));}
        if(fields.Contains(source+".tags")&&Path(media,"tags") is {ValueKind:JsonValueKind.Array} tags) {
            var catalog=await connections.GetAsync(source,"tag",ct);
            if(catalog.ValueKind==JsonValueKind.Array&&catalog.GetArrayLength()<=10000){var ids=tags.EnumerateArray().Select(t=>t.GetInt32()).ToHashSet();var values=catalog.EnumerateArray().Where(t=>Path(t,"id") is {} n&&ids.Contains(n.GetInt32())).ToArray();if(values.Length==ids.Count&&values.All(t=>Path(t,"label") is {ValueKind:JsonValueKind.String}))Scalar("tags",values.Select(t=>Path(t,"label")!.Value.GetString()).ToArray());}
        }
        if(fields.Contains(source+".qualityProfile")&&Path(media,"qualityProfileId") is {ValueKind:JsonValueKind.Number} profileId) {
            var catalog=await connections.GetAsync(source,"qualityprofile",ct);
            if(catalog.ValueKind==JsonValueKind.Array&&catalog.GetArrayLength()<=1000){var profiles=catalog.EnumerateArray().Where(p=>Path(p,"id") is {} n&&n.GetInt32()==profileId.GetInt32()).Take(2).ToArray();if(profiles.Length==1)Put("qualityProfile",Path(profiles[0],"name"));}
        }
        if(source=="sonarr"&&fields.Any(f=>f.StartsWith("sonarr.latestSeason",StringComparison.Ordinal)||new[]{"sonarr.lastEpisodeAired","sonarr.nextEpisodeAirs","sonarr.fileQualityNames","sonarr.fileVideoCodecs","sonarr.fileAudioCodecs","sonarr.fileAudioLanguages","sonarr.fileQualityCutoffMet","sonarr.latestFileAdded"}.Contains(f))){
            if(Path(media,"id") is not {ValueKind:JsonValueKind.Number} seriesId||!seriesId.TryGetInt32(out var sid)||sid<=0)return result;
            var episodes=await connections.GetAsync(source,"episode?seriesId="+sid.ToString(CultureInfo.InvariantCulture),ct);
            if(episodes.ValueKind!=JsonValueKind.Array||episodes.GetArrayLength()>5000)throw new InvalidOperationException("Episode scope incomplete");
            var rows=episodes.EnumerateArray().ToArray();
            if(rows.Any(e=>Path(e,"seriesId") is not {} owner||!owner.TryGetInt32(out var series)||series!=sid||Path(e,"seasonNumber") is not {} season||!season.TryGetInt32(out _)))throw new InvalidOperationException("Episode scope mismatch");
            DateTimeOffset? Air(JsonElement e)=>Path(e,"airDateUtc") is {ValueKind:JsonValueKind.String} date&&date.TryGetDateTimeOffset(out var value)?value:null;
            var now=DateTimeOffset.UtcNow;var airedDates=rows.Where(e=>Air(e)<=now).Select(Air).Where(d=>d.HasValue).ToArray();var next=rows.Select(Air).Where(d=>d>now).ToArray();
            if(airedDates.Length>0)Scalar("lastEpisodeAired",airedDates.Max());else if(rows.All(e=>Path(e,"airDateUtc") is not null))result["sonarr.lastEpisodeAired"]=MetadataEvidence.Absent;
            if(next.Length>0)Scalar("nextEpisodeAirs",next.Min());else if(rows.All(e=>Path(e,"airDateUtc") is not null))result["sonarr.nextEpisodeAirs"]=MetadataEvidence.Absent;
            var regular=rows.Where(e=>Path(e,"seasonNumber")!.Value.GetInt32()>0).ToArray();
            if(regular.Length>0){var latest=regular.Max(e=>Path(e,"seasonNumber")!.Value.GetInt32());Scalar("latestSeasonNumber",latest);var season=regular.Where(e=>Path(e,"seasonNumber")!.Value.GetInt32()==latest).ToArray();
                if(season.All(e=>Air(e).HasValue&&Path(e,"hasFile") is {ValueKind:JsonValueKind.True or JsonValueKind.False})){
                    Scalar("latestSeasonEpisodeCount",season.Count(e=>Air(e)<=now));Scalar("latestSeasonFileCount",season.Count(e=>Path(e,"hasFile")!.Value.GetBoolean()));Scalar("latestSeasonMissingEpisodes",season.Count(e=>Air(e)<=now&&!Path(e,"hasFile")!.Value.GetBoolean()));
                }
                var finales=season.Where(e=>Path(e,"finaleType") is {ValueKind:JsonValueKind.String} type&&type.GetString() is "season" or "series").ToArray();
                if(finales.Length==1&&Air(finales[0]) is {} finale&&finale<=now)Scalar("latestSeasonFinaleAired",finale);
            }
            if(fields.Any(f=>f.StartsWith("sonarr.file",StringComparison.Ordinal)||f=="sonarr.latestFileAdded")){
                var episodeFiles=await connections.GetAsync(source,"episodefile?seriesId="+sid.ToString(CultureInfo.InvariantCulture),ct);
                if(episodeFiles.ValueKind!=JsonValueKind.Array||episodeFiles.GetArrayLength()>5000)throw new InvalidOperationException("Episode file scope incomplete");var fileRows=episodeFiles.EnumerateArray().ToArray();
                if(fileRows.Any(f=>Path(f,"seriesId") is not {} owner||!owner.TryGetInt32(out var series)||series!=sid))throw new InvalidOperationException("Episode file scope mismatch");
                void Values(string field,string path){var values=fileRows.Select(f=>Path(f,path)).ToArray();if(values.All(v=>v is {ValueKind:JsonValueKind.String}))Scalar(field,values.Select(v=>v!.Value.GetString()).Distinct().ToArray());}
                Values("fileQualityNames","quality.quality.name");Values("fileVideoCodecs","mediaInfo.videoCodec");Values("fileAudioCodecs","mediaInfo.audioCodec");
                var fileLanguages=fileRows.Select(f=>Path(f,"mediaInfo.audioLanguages")).ToArray();if(fileLanguages.All(v=>v is {ValueKind:JsonValueKind.String}))Scalar("fileAudioLanguages",fileLanguages.SelectMany(v=>v!.Value.GetString()!.Split(['/',','],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)).Distinct().ToArray());
                var cutoffs=fileRows.Select(f=>Path(f,"qualityCutoffNotMet")).ToArray();if(fileRows.Length>0&&cutoffs.All(v=>v is {ValueKind:JsonValueKind.True or JsonValueKind.False}))Scalar("fileQualityCutoffMet",cutoffs.All(v=>!v!.Value.GetBoolean()));
                var dates=fileRows.Select(f=>Path(f,"dateAdded")).ToArray();if(dates.Length>0&&dates.All(v=>v is {ValueKind:JsonValueKind.String} date&&date.TryGetDateTimeOffset(out _)))Scalar("latestFileAdded",dates.Max(v=>v!.Value.GetDateTimeOffset()));
            }
        }
        return result;
    }
    private static JsonElement? Path(JsonElement value,string path){foreach(var part in path.Split('.')){if(value.ValueKind!=JsonValueKind.Object||!value.TryGetProperty(part,out value))return null;}return value;}
}
