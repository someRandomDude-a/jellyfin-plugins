using DeletionQueue.Core;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DeletionQueue.Api;

public sealed record SavePresentation(MediaPresentationSettings Settings, bool ConfirmChanges);
public sealed record PreviewOverlay(OverlayTemplate Template, bool Portrait, string SampleDate);
[ApiController, Authorize, Route("DeletionQueue/admin/presentation"), TypeFilter(typeof(QueueApiFilter)), RequestSizeLimit(32768)]
public sealed class MediaPresentationController(QueueRuntime runtime, LiveIdentity identity, ILibraryManager library,
    MediaPresentationService service, ArtworkRenderer renderer) : ControllerBase
{
    [HttpGet] public IActionResult Get() {
        _ = identity.Caller(User, administrator: true);
        var presentation = runtime.Read().Presentation;
        var available = library.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery { IncludeItemTypes = [BaseItemKind.BoxSet], Recursive = true, Limit = 501 });
        return Ok(new { presentation.Settings, service.Status,
            Collections = available.Take(500).Select(item => new { item.Id, item.Name }), CollectionsTruncated = available.Count > 500,
            Artwork = presentation.Artwork.Values.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count()),
            ManagedCollectionId = presentation.CreatedCollectionId });
    }
    [HttpPut] public async Task<IActionResult> Save(SavePresentation body, CancellationToken ct) {
        _ = identity.Caller(User, administrator: true);
        if ((body.Settings.CollectionEnabled || body.Settings.OverlaysEnabled) && !body.ConfirmChanges) throw new QueueException("ConfirmationRequired");
        return Ok(await service.SaveAsync(body.Settings, () => { _ = identity.Caller(User, administrator: true); }, ct));
    }
    [HttpPost("preview")] public IActionResult Preview(PreviewOverlay body) {
        _ = identity.Caller(User, administrator: true); body.Template.Validate();
        if (!DateTimeOffset.TryParseExact(body.SampleDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var date)) throw new QueueException("InvalidDate");
        Response.Headers.CacheControl = "no-store";
        return File(renderer.Preview(body.Template, body.Portrait, body.Template.Format(date, false), date), "image/png");
    }
    [HttpPost("sync")] public async Task<IActionResult> Sync(CancellationToken ct) {
        _ = identity.Caller(User, administrator: true); await service.SyncAsync(ct); return NoContent();
    }
}
