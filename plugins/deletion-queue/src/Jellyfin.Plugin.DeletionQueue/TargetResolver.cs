using System.Globalization;
using System.Text.Json;
using DeletionQueue.Core;
using DeletionQueue.Infrastructure;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Jellyfin.Data.Enums;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Jellyfin.Data;

namespace Jellyfin.Plugin.DeletionQueue;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TargetSelection(Guid? ItemId, int? RequestId, bool Is4K) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if ((ItemId is null) == (RequestId is null) || ItemId == Guid.Empty || RequestId <= 0)
            yield return new ValidationResult("Select exactly one valid item or request identifier.");
    }
}

public sealed class TargetResolver(ILibraryManager library, IUserManager users, QueueRuntime runtime, IServerApplicationHost host,
    ISeerrClientFactory seerrClients)
{
    public bool Accessible(Guid userId, VerifiedTarget target) => target.JellyfinItemId != Guid.Empty ? Accessible(userId, target.JellyfinItemId) :
        target.RequestId > 0 && target.RequestOwnerId == userId && target.OwnRequestVerified && users.GetUserById(userId) is { } user && !user.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled);
    public bool Accessible(Guid userId, Proposal proposal)
    {
        if (Accessible(userId, proposal.Target)) return true;
        // A completed owner's history must survive actual library removal. This
        // does not bypass a visibility revocation while the native item exists.
        return proposal.Terminal && proposal.OwnerId == userId && proposal.Target.JellyfinItemId != Guid.Empty &&
            library.GetItemById(proposal.Target.JellyfinItemId) is null && users.GetUserById(userId) is { } user &&
            !user.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled);
    }
    public string? ActivityKey(Guid itemId) => library.GetItemById(itemId) is { } item ? ActivityKey(item) : null;
    public Guid ContextItem(Guid userId, Guid itemId) {
        var user=users.GetUserById(userId) ?? throw new QueueException("TargetUnavailable");
        var item=library.GetItemById<BaseItem>(itemId,user) ?? throw new QueueException("TargetUnavailable");
        var target=item switch {Episode episode=>episode.SeriesId,Season season=>season.SeriesId,_=>itemId};
        if(!Accessible(userId,target))throw new QueueException("TargetUnavailable");
        return target;
    }
    public bool Accessible(Guid userId, Guid itemId)
    {
        var user = users.GetUserById(userId);
        return user is not null && library.GetItemById<BaseItem>(itemId, user) is Movie or Series;
    }

    public string? ActivityKey(BaseItem item)
    {
        if (item is Episode episode) item = library.GetItemById(episode.SeriesId) ?? item;
        else if (item is Season season) item = library.GetItemById(season.SeriesId) ?? item;
        if (item is not (Movie or Series)) return null;
        if (!item.ProviderIds.TryGetValue("Tmdb", out var id) || id.Length > 16 || !int.TryParse(id, out var number) || number <= 0)
            throw new QueueException("ActivityTargetUnverified");
        // All editions and library aliases of this title share a protective activity key.
        return $"{(item is Series ? "tv" : "movie")}:{number}";
    }

    public async Task<VerifiedTarget> ResolveAsync(Account caller, TargetSelection selection, CancellationToken ct, Dictionary<string, object?>? metadata = null)
    {
        if (selection.ItemId == Guid.Empty || selection.RequestId <= 0) throw new QueueException("InvalidTarget");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(15)); ct = budget.Token;
        if ((selection.ItemId is null) == (selection.RequestId is null)) throw new QueueException("SelectExactlyOneTarget");
        var state = runtime.Read();
        if (QueueRules.Authority(state, caller.Id) == ProposalAuthority.Denied) throw new QueueException("ProposalNotPermitted");
        if (selection.ItemId is { } selectedItem && !Accessible(caller.Id, selectedItem)) throw new QueueException("TargetUnavailable");
        var config = state.Seerr;
        if (string.IsNullOrWhiteSpace(config.BaseUrl) || string.IsNullOrWhiteSpace(config.ApiKey) ||
            string.IsNullOrWhiteSpace(config.JellyfinServerId)) throw new QueueException("SeerrNotConfigured");
        using var seerr = seerrClients.Create(config);
        await VerifyServerAssociationAsync(seerr, config, ct);
        BaseItem? item;
        if (selection.ItemId is { } itemId)
        {
            var user = users.GetUserById(caller.Id) ?? throw new QueueException("TargetUnavailable");
            item = library.GetItemById<BaseItem>(itemId, user);
        }
        else
        {
            var request = await seerr.RequestAsync(selection.RequestId!.Value, ct);
            if (Number(request, "id") != selection.RequestId.Value) throw new QueueException("TargetUnavailable");
            if (!request.TryGetProperty("media", out var media) || Bool(request, "is4k") != selection.Is4K)
                throw new QueueException("TargetUnavailable");
            var type = Text(media, "mediaType");
            var id = Number(media, "tmdbId");
            if (type is not ("movie" or "tv") || id <= 0) throw new QueueException("TargetUnavailable");
            var requestDetails = await seerr.MediaAsync(type == "tv", id, ct);
            if (!requestDetails.TryGetProperty("mediaInfo", out var requestInfo) || Number(requestInfo, "id") != Number(media, "id") || Number(requestInfo, "tmdbId") != id || Text(requestInfo, "mediaType") != type)
                throw new QueueException("TargetUnavailable");
            var requestNativeProperty = selection.Is4K ? "jellyfinMediaId4k" : "jellyfinMediaId";
            if (!Guid.TryParse(Text(requestInfo, requestNativeProperty), out var mappedId) || mappedId == Guid.Empty)
                return await ResolveUnfulfilledAsync(caller, selection, request, requestDetails, requestInfo, type, id, config, seerr, ct);
            var user = users.GetUserById(caller.Id) ?? throw new QueueException("TargetUnavailable");
            item = library.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = [type == "movie" ? BaseItemKind.Movie : BaseItemKind.Series],
                HasAnyProviderIds = new() { ["Tmdb"] = [id.ToString(CultureInfo.InvariantCulture)] },
                Recursive = true, Limit = 1
            }).FirstOrDefault();
        }
        if (item is not (Movie or Series) || !Accessible(caller.Id, item.Id)) throw new QueueException("TargetUnavailable");
        var activityKey = ActivityKey(item) ?? throw new QueueException("TargetUnavailable");
        var tmdb = int.Parse(activityKey.Split(':')[1], CultureInfo.InvariantCulture);
        var details = await seerr.MediaAsync(item is Series, tmdb, ct);
        if (Number(details, "id") != tmdb || !details.TryGetProperty("mediaInfo", out var info) || info.ValueKind != JsonValueKind.Object ||
            Number(info, "tmdbId") != tmdb || Text(info, "mediaType") != (item is Series ? "tv" : "movie"))
            throw new QueueException("TargetUnavailable");
        var mediaId = Number(info, "id");
        if(Number(info,selection.Is4K?"status4k":"status")==7)throw new QueueException("TargetAlreadyRemoved");
        var serviceProperty = selection.Is4K ? "serviceId4k" : "serviceId";
        var externalProperty = selection.Is4K ? "externalServiceId4k" : "externalServiceId";
        if (mediaId <= 0 || !info.TryGetProperty(serviceProperty, out var service) || service.ValueKind != JsonValueKind.Number ||
            service.GetInt32() < 0 || Number(info, externalProperty) <= 0) throw new QueueException("SeerrTargetNotManaged");
        // Provider identity alone does NOT establish visibility of the requested quality edition.
        // In particular, access to a 4K alias must not authorize a hidden standard-library target.
        var nativeProperty = selection.Is4K ? "jellyfinMediaId4k" : "jellyfinMediaId";
        if (!Guid.TryParse(Text(info, nativeProperty), out var nativeId) || nativeId == Guid.Empty)
            throw new QueueException("TargetEditionUnverified");
        var currentUser = users.GetUserById(caller.Id) ?? throw new QueueException("TargetUnavailable");
        var mappedItem = library.GetItemById<BaseItem>(nativeId, currentUser);
        if (mappedItem is not (Movie or Series) || mappedItem.GetType() != item.GetType() || ActivityKey(mappedItem) != activityKey ||
            (selection.ItemId is { } selectedId && selectedId != nativeId))
            throw new QueueException("TargetEditionUnverified");
        item = mappedItem;
        // Do not include service ID in the lock key: remapping must not create a second active proposal.
        // There is one configured Seerr association. Changing its address must not evade active scope locks.
        var key = $"seerr|{activityKey}|{selection.Is4K}";
        var own = false;
        bool? ownEvidence = null;
        try { ownEvidence = own = await SeerrOwnership.OwnsAsync(info, caller.Id, selection.Is4K, item is Series, seerr.UserAsync, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch when (metadata is not null) { /* Failed owner reads remain unknown in rule metadata. */ }
        Guid? requester = null;
        if (metadata is not null) {
            try {
                var requesterIds = await SeerrOwnership.RequesterIdsAsync(info, selection.Is4K, seerr.UserAsync,
                    // Identity and authorization are separate: retain a known disabled
                    // requester so automation rejects it instead of falling back to its administrator.
                    id => users.GetUserById(id) is not null, ct);
                requester=requesterIds is {Length:1}?requesterIds[0]:null;
                metadata["seerr.requesters"]=requesterIds?.Select(id=>id.ToString("D")).ToArray();
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* Only requester-dependent conditions lose their evidence. */ }
            metadata["seerr.ownRequest"] = ownEvidence;
            metadata["seerr.requesterId"] = requester?.ToString("D");
            metadata["seerr.is4k"] = selection.Is4K;
            metadata["seerr.requestOnly"] = false;
            if(info.TryGetProperty(selection.Is4K?"status4k":"status",out var availability)&&availability.ValueKind==JsonValueKind.Number)metadata["seerr.status"]=availability.Clone();
            if(info.TryGetProperty("mediaAddedAt",out var added)&&added.ValueKind==JsonValueKind.String&&added.TryGetDateTimeOffset(out var addedDate))metadata["seerr.mediaAddedAt"]=addedDate;
            if(info.TryGetProperty("requests",out var requests)&&requests.ValueKind==JsonValueKind.Array&&requests.GetArrayLength()<=64&&requests.EnumerateArray().All(r=>r.ValueKind==JsonValueKind.Object&&Number(r,"id")>0&&r.TryGetProperty("is4k",out var edition)&&edition.ValueKind is JsonValueKind.True or JsonValueKind.False)) {
                var matching=requests.EnumerateArray().Where(r=>Bool(r,"is4k")==selection.Is4K).ToArray();
                metadata["seerr.requestCount"]=matching.Length;metadata["seerr.isRequested"]=matching.Length>0;
                var dates=matching.Select(r=>r.TryGetProperty("createdAt",out var date)&&date.ValueKind==JsonValueKind.String&&date.TryGetDateTimeOffset(out var parsed)?(DateTimeOffset?)parsed:null).ToArray();
                if(dates.Length>0&&dates.All(d=>d.HasValue))metadata["seerr.requestDate"]=dates.Min();
            }
        }
        // Seerr is explicitly trusted for Arr scope/files; the legacy FilesPinned field remains false and is not a gate.
        var title = item.Name ?? "Media";
        if (title.Length > 512) title = title[..512];
        return MediaIdentity.PreserveExistingFingerprint(new VerifiedTarget(key, activityKey, "", item.Id, title,
            item is Series ? MediaKind.Series : MediaKind.Movie, mediaId, selection.Is4K, true, own, false, config.Revision,
            $"{(item is Series ? "sonarr" : "radarr")}|{service.GetInt32()}|{Number(info, externalProperty)}"), state.Proposals);
    }

    private async Task<VerifiedTarget> ResolveUnfulfilledAsync(Account caller, TargetSelection selection, JsonElement request, JsonElement details,
        JsonElement info, string type, int tmdb, SeerrSettings config, SeerrClient seerr, CancellationToken ct)
    {
        var status = Number(request, "status");
        if (status is not (1 or 2 or 4) || Number(info, selection.Is4K ? "status4k" : "status") is not (1 or 2 or 3)) throw new QueueException("TargetUnavailable");
        if (!request.TryGetProperty("requestedBy", out var owner) || Number(owner, "id") <= 0) throw new QueueException("ProposalNotPermitted");
        var ownerRecord = await seerr.UserAsync(Number(owner, "id"), ct);
        if (Number(ownerRecord, "id") != Number(owner, "id") || !Guid.TryParse(Text(ownerRecord, "jellyfinUserId"), out var ownerId) || ownerId != caller.Id) throw new QueueException("ProposalNotPermitted");
        var serviceName = selection.Is4K ? "serviceId4k" : "serviceId";
        var external = Number(info, selection.Is4K ? "externalServiceId4k" : "externalServiceId");
        var managed = info.TryGetProperty(serviceName, out var service) && service.ValueKind == JsonValueKind.Number && service.TryGetInt32(out var serviceId) && serviceId >= 0 && external > 0;
        var requestOnly = !managed && status == 1;
        if (!managed && !requestOnly) throw new QueueException("SeerrTargetNotManaged");
        // File removal affects the entire managed edition. Every live request must belong to this caller.
        if (managed) {
            if (!info.TryGetProperty("requests", out var requests) || requests.ValueKind != JsonValueKind.Array || requests.GetArrayLength() is 0 or > 64) throw new QueueException("OwnershipScanIncomplete");
            if (!requests.EnumerateArray().Any(related => Number(related, "id") == selection.RequestId && Bool(related, "is4k") == selection.Is4K)) throw new QueueException("OwnershipScanIncomplete");
            foreach (var related in requests.EnumerateArray().Where(r => Bool(r, "is4k") == selection.Is4K && Number(r, "status") is 1 or 2 or 4 or 5)) {
                if (!related.TryGetProperty("requestedBy", out var relatedOwner) || Number(relatedOwner, "id") <= 0) throw new QueueException("ProposalNotPermitted");
                var record = await seerr.UserAsync(Number(relatedOwner, "id"), ct);
                if (Number(record, "id") != Number(relatedOwner, "id") || !Guid.TryParse(Text(record, "jellyfinUserId"), out var relatedId) || relatedId != caller.Id) throw new QueueException("ProposalNotPermitted");
            }
        }
        var activityKey = $"{type}:{tmdb}";
        var key = $"seerr|{activityKey}|{selection.Is4K}";
        var scope = requestOnly ? null : $"{(type == "tv" ? "sonarr" : "radarr")}|{Number(info, serviceName)}|{external}";
        var title = Text(details, type == "tv" ? "name" : "title");
        if (title.Length == 0) title = "Unfulfilled request";
        return MediaIdentity.PreserveExistingFingerprint(new(key, activityKey, "", Guid.Empty, title[..Math.Min(title.Length, 512)], type == "tv" ? MediaKind.Series : MediaKind.Movie,
            Number(info, "id"), selection.Is4K, true, true, false, config.Revision, scope) { RequestId = selection.RequestId, RequestOwnerId = ownerId, RequestOnly = requestOnly }, runtime.Read().Proposals);
    }

    public async Task<ExternalTargetChange?> InspectExternalChangeAsync(Proposal proposal,CancellationToken ct)
    {
        var state=runtime.Read();var target=proposal.Target;
        if(proposal.Terminal||proposal.Claim is not null||!target.MappingVerified)return null;
        using var seerr=seerrClients.Create(state.Seerr);
        ExternalTargetChange Observed(string reason) {
            if(runtime.Read().Seerr.Revision!=state.Seerr.Revision)throw new QueueException("RevisionChanged");
            return new(reason){ConnectionRevision=state.Seerr.Revision};
        }
        // Establish the configured server association before interpreting a 404.
        // Auth/network/schema failures remain verification failures, not deletion.
        await VerifyServerAssociationAsync(seerr,state.Seerr,ct);
        try {
            if(target.RequestOnly&&target.RequestId is {} requestId){
                var request=await seerr.RequestAsync(requestId,ct);
                if(Number(request,"id")!=requestId||!request.TryGetProperty("is4k",out var quality)||quality.ValueKind is not (JsonValueKind.True or JsonValueKind.False)||Bool(request,"is4k")!=target.Is4K||!request.TryGetProperty("media",out var media)||Number(media,"id")!=target.SeerrMediaId)return null;
                return Number(request,"status")==3?Observed("RequestClosedOutsideQueue"):null;
            }
            var record=await seerr.MediaRecordAsync(target.SeerrMediaId,ct);
            return MatchesMediaRecord(target,record)&&Number(record,target.Is4K?"status4k":"status")==7?Observed("RemovedOutsideQueue"):null;
        }catch(HttpRequestException error)when(error.StatusCode==System.Net.HttpStatusCode.NotFound){return Observed("UpstreamTargetMissing");}
    }
    public async Task VerifyServerAssociationAsync(SeerrClient seerr, SeerrSettings config, CancellationToken ct) {
        var settings=await seerr.JellyfinSettingsAsync(ct);
        if(!string.Equals(config.JellyfinServerId,host.SystemId,StringComparison.OrdinalIgnoreCase)||!string.Equals(Text(settings,"serverId"),host.SystemId,StringComparison.OrdinalIgnoreCase))throw new QueueException("SeerrServerAssociationUnverified");
    }
    public static bool MatchesMediaRecord(VerifiedTarget target,JsonElement record)
    {
        var kind=target.Kind==MediaKind.Series?"tv":"movie";
        if(Number(record,"id")!=target.SeerrMediaId||Number(record,"tmdbId")<=0||target.ActivityKey!=$"{kind}:{Number(record,"tmdbId")}"||
            record.TryGetProperty("mediaType",out _)&&Text(record,"mediaType")!=kind)return false;
        var native=Text(record,target.Is4K?"jellyfinMediaId4k":"jellyfinMediaId");
        if(native.Length>0&&(!Guid.TryParse(native,out var nativeId)||target.JellyfinItemId!=Guid.Empty&&nativeId!=target.JellyfinItemId))return false;
        var service=target.Is4K?"serviceId4k":"serviceId";var external=target.Is4K?"externalServiceId4k":"externalServiceId";
        if(record.TryGetProperty(service,out var serviceId)&&serviceId.ValueKind==JsonValueKind.Number&&serviceId.TryGetInt32(out var instance)&&instance>=0&&Number(record,external)>0&&
            target.DestructiveScopeKey is {} expected&&expected!=$"{(target.Kind==MediaKind.Series?"sonarr":"radarr")}|{instance}|{Number(record,external)}")return false;
        return true;
    }
    private static int Number(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : 0;
    private static bool Bool(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    private static string Text(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
}
