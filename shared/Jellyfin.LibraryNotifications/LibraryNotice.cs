using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.LibraryNotifications;

public sealed record LibraryNotice(Guid Id, Guid ItemId, Guid[] LibraryIds, string Kind, string Title, string Body, DateTimeOffset OccurredAt, Guid[] Audience)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]public int EnrichmentVersion{get;init;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]public string? MediaTitle{get;init;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]public Guid[]? FavouriteAudience{get;init;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]public Guid[]? RequesterAudience{get;init;}
}
public sealed record LibraryNoticeSettings(long Revision = 0, bool Enabled = false, bool Episodes = true, bool Movies = true, DateTimeOffset? EnabledSince = null)
{
    public bool Includes(LibraryNotice notice) => Enabled && EnabledSince is { } since && notice.OccurredAt >= since &&
        (notice.Kind == "episode" ? Episodes : notice.Kind == "movie" && Movies);
}
public sealed record LibraryJob(Guid Id, Guid ItemId, string Kind, DateTimeOffset Created, LibraryNotice? Notice, bool InboxDone, int DiscordState, int Attempts);

public interface ILibraryNoticeHost
{
    bool Ready { get; }
    bool CaptureLocally { get; }
    bool IncludeAudience { get; }
    bool DirectDiscord { get; }
    bool DiscordConnected { get; }
    Task<bool> InboxAsync(LibraryNotice notice, CancellationToken ct);
    Task<bool> DiscordAsync(LibraryNotice notice, CancellationToken ct);
}

public static class LibraryNoticeFactory
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string? Kind(BaseItem item) => item.IsVirtualItem ? null : item switch { Episode => "episode", Movie => "movie", _ => null };
    public static LibraryNotice Create(Guid id, BaseItem item, Guid[] libraries, Guid[] audience, DateTimeOffset created)
    {
        var kind = Kind(item) ?? throw new ArgumentException("Only physical episodes and movies are announced.");
        var name = Clean(item.Name, 180);
        var body = item is Episode episode
            ? Clean(episode.SeriesName, 180) + (episode.ParentIndexNumber is { } season ? $" · Season {season}" : "") + (episode.IndexNumber is { } number ? $", episode {number}" : "") + "\n" + name
            : name + (item.ProductionYear is { } year ? $" ({year})" : "");
        var mediaTitle=item is Episode ep?Clean(ep.SeriesName,120)+(ep.ParentIndexNumber is {} seasonIndex?" · S"+seasonIndex.ToString("D2"):"")+(ep.IndexNumber is {} episodeIndex?"E"+episodeIndex.ToString("D2"):"")+" · "+name:body.Trim();
        return new(id, item.Id, libraries.Distinct().Take(100).ToArray(), kind, kind == "episode" ? "New episode available" : "New movie available", body.Trim(), created, audience){EnrichmentVersion=1,MediaTitle=Clean(mediaTitle,200)};
    }
    private static string Clean(string? text, int max) => new((text ?? "").Where(c => !char.IsControl(c)).Take(max).ToArray());
    public static void Validate(LibraryNotice value)
    {
        if (value.Id == Guid.Empty || value.ItemId == Guid.Empty || value.Kind is not ("episode" or "movie") || value.Title is not { Length: > 0 and <= 200 } || value.Body is not { Length: <= 1000 } ||
            value.LibraryIds is null || value.LibraryIds.Length > 100 || value.LibraryIds.Contains(Guid.Empty) || value.Audience is null || value.Audience.Length > 10000 || value.Audience.Contains(Guid.Empty) || value.OccurredAt == default || value.OccurredAt.Offset != TimeSpan.Zero)
             throw new ArgumentException("Invalid library notice.");
        if(value.EnrichmentVersion is <0 or >1||value.MediaTitle is {Length:>200}||new[]{value.FavouriteAudience,value.RequesterAudience}.Any(ids=>ids is not null&&(ids.Length>10000||ids.Contains(Guid.Empty))))throw new ArgumentException("Invalid library enrichment.");
    }
}
