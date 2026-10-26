using System.Text.Json;
using DeletionQueue.Core;

namespace DeletionQueue.Infrastructure;

public static class SeerrOwnership
{
    // A singular requester is metadata only, never a grant of consent/authority.
    // Missing, malformed or mixed owners must remain unknown, not guessed.
    public static async Task<Guid?> RequesterAsync(JsonElement info, bool is4K,
        Func<int, CancellationToken, Task<JsonElement>> loadUser, Func<Guid, bool> enabledUser, CancellationToken ct)
    {
        var owners=await RequesterIdsAsync(info,is4K,loadUser,enabledUser,ct);
        return owners is {Length:1}?owners[0]:null;
    }
    public static async Task<Guid[]?> RequesterIdsAsync(JsonElement info,bool is4K,
        Func<int,CancellationToken,Task<JsonElement>> loadUser,Func<Guid,bool> enabledUser,CancellationToken ct)
    {
        if (info.ValueKind != JsonValueKind.Object || !info.TryGetProperty("requests", out var requests) ||
            requests.ValueKind != JsonValueKind.Array || requests.GetArrayLength() > 64) return null;
        var requesters = new HashSet<Guid>();
        var owners = new Dictionary<int, Guid>();
        foreach (var request in requests.EnumerateArray()) {
            ct.ThrowIfCancellationRequested();
            if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("is4k", out var edition) ||
                edition.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || Number(request,"status") is not (1 or 2 or 3 or 4 or 5)) return null;
            if (edition.GetBoolean() != is4K) continue;
            if (Number(request,"id") <= 0 || !request.TryGetProperty("requestedBy", out var owner) || Number(owner,"id") <= 0) return null;
            var ownerId = Number(owner,"id");
            if (!owners.TryGetValue(ownerId, out var mapped)) {
                var record = await loadUser(ownerId,ct);
                if (Number(record,"id") != ownerId || !Guid.TryParse(Text(record,"jellyfinUserId"),out mapped) ||
                    mapped == Guid.Empty || !enabledUser(mapped)) return null;
                owners[ownerId] = mapped;
            }
            requesters.Add(mapped);
        }
        return requesters.Order().ToArray();
    }

    public static async Task<bool> OwnsAsync(JsonElement info, Guid caller, bool is4K, bool series,
        Func<int, CancellationToken, Task<JsonElement>> loadUser, CancellationToken ct)
    {
        if (!info.TryGetProperty("requests", out var requests) || requests.ValueKind != JsonValueKind.Array) return false;
        if (requests.GetArrayLength() > 64) throw new QueueException("OwnershipScanIncomplete");
        var anyOwned = false; var seasons = new HashSet<int>();
        var owners = new Dictionary<int, Guid>();
        foreach (var request in requests.EnumerateArray())
        {
            if (request.ValueKind != JsonValueKind.Object || Bool(request, "is4k") != is4K || Number(request, "status") is not (2 or 5) ||
                !request.TryGetProperty("requestedBy", out var owner)) continue;
            var ownerId = Number(owner, "id"); if (ownerId <= 0) return false;
            if (!owners.TryGetValue(ownerId, out var mapped))
            {
                var user = await loadUser(ownerId, ct);
                if (!Guid.TryParse(Text(user, "jellyfinUserId"), out mapped)) return false;
                owners[ownerId] = mapped;
            }
            if (mapped != caller) { if (series) return false; continue; }
            anyOwned = true;
            if (!series) continue;
            if (!request.TryGetProperty("seasons", out var requested) || requested.ValueKind != JsonValueKind.Array || requested.GetArrayLength() > 1000) return false;
            foreach (var season in requested.EnumerateArray())
            {
                var number = Number(season, "seasonNumber", -1); if (number < 0) return false; seasons.Add(number);
            }
        }
        if (!anyOwned || !series) return anyOwned;
        if (!info.TryGetProperty("seasons", out var inventory) || inventory.ValueKind != JsonValueKind.Array || inventory.GetArrayLength() is 0 or > 1000) return false;
        var managed = inventory.EnumerateArray().Where(s => Number(s, is4K ? "status4k" : "status") is 3 or 4 or 5).ToArray();
        return managed.Length > 0 && managed.All(s => Number(s, "seasonNumber", -1) >= 0 && seasons.Contains(Number(s, "seasonNumber")));
    }
    private static int Number(JsonElement e, string key, int fallback = 0) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;
    private static string Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    private static bool Bool(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
}
