using System.Text.Json;
using DeletionQueue.Core;
using DeletionQueue.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller;

namespace Jellyfin.Plugin.DeletionQueue.Api;

[ApiController, Authorize, Route("DeletionQueue/requests"), TypeFilter(typeof(QueueApiFilter))]
public sealed class RequestTargetsController(QueueRuntime runtime, LiveIdentity identity, ISeerrClientFactory clients, IServerApplicationHost host) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Own([FromQuery] int skip = 0, [FromQuery] int? tmdbId = null, [FromQuery] string? mediaType = null, CancellationToken ct = default)
    {
        var caller = identity.Caller(User);
        if (skip is < 0 or > 10000 || tmdbId <= 0 || mediaType is not (null or "movie" or "tv")) throw new QueueException("InvalidTarget");
        var state = runtime.Read();
        if (QueueRules.Authority(state, caller.Id) == ProposalAuthority.Denied) throw new QueueException("ProposalNotPermitted");
        if (string.IsNullOrWhiteSpace(state.Seerr.BaseUrl) || string.IsNullOrWhiteSpace(state.Seerr.ApiKey)) throw new QueueException("SeerrNotConfigured");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15)); ct = deadline.Token;
        using var seerr = clients.Create(state.Seerr);
        var settings = await seerr.JellyfinSettingsAsync(ct);
        if (!string.Equals(state.Seerr.JellyfinServerId, host.SystemId, StringComparison.OrdinalIgnoreCase) || !string.Equals(Text(settings, "serverId"), host.SystemId, StringComparison.OrdinalIgnoreCase)) throw new QueueException("SeerrServerAssociationUnverified");
        var page = await seerr.RequestsAsync(skip, ct);
        if (!page.TryGetProperty("results", out var requests) || requests.ValueKind != JsonValueKind.Array || requests.GetArrayLength() > 100) throw new QueueException("UpstreamUnavailable");
        var owners = new Dictionary<int, Guid>(); var result = new List<object>();
        foreach (var request in requests.EnumerateArray()) {
            if (Number(request, "status") is not (1 or 2 or 4) || !request.TryGetProperty("media", out var media) || !request.TryGetProperty("requestedBy", out var owner)) continue;
            var tmdb = Number(media, "tmdbId"); var type = Text(media, "mediaType"); var requestId = Number(request, "id");
            if (tmdb <= 0 || requestId <= 0 || type is not ("movie" or "tv") || tmdbId is not null && tmdbId != tmdb || mediaType is not null && mediaType != type) continue;
            var ownerId = Number(owner, "id"); if (ownerId <= 0) continue;
            if (!owners.TryGetValue(ownerId, out var mapped)) {
                if (owners.Count >= 64) throw new QueueException("OwnershipScanIncomplete");
                var user = await seerr.UserAsync(ownerId, ct);
                Guid.TryParse(Text(user, "jellyfinUserId"), out mapped); owners[ownerId] = mapped;
            }
            if (mapped != caller.Id) continue;
            result.Add(new { RequestId = requestId, TmdbId = tmdb, MediaType = type, Is4K = request.TryGetProperty("is4k", out var quality) && quality.ValueKind == JsonValueKind.True });
        }
        return Ok(new { Requests = result, HasMore = requests.GetArrayLength() == 100, NextSkip = skip + 100 });
    }
    private static int Number(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var child) && child.ValueKind == JsonValueKind.Number && child.TryGetInt32(out var number) ? number : 0;
    private static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var child) && child.ValueKind == JsonValueKind.String ? child.GetString()! : "";
}
