using System.Reflection;
using DeletionQueue.Core;
using Jellyfin.Plugin.DeletionQueue;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace DeletionQueue.Tests;

public sealed class MediaPresentationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dq-art-test-" + Guid.NewGuid().ToString("N"));
    private readonly QueueRuntime runtime;
    private readonly Movie movie;
    private readonly BoxSet collection = new() { Id = Guid.NewGuid(), Name = "Existing collection" };
    private readonly HashSet<Guid> members = [];
    private readonly Guid proposal;
    private readonly string original;
    private readonly byte[] bytes;
    private readonly MediaPresentationService service;
    private bool collectionAvailable = true;
    private int failImageSaves;
    private int imageSaves;
    private static T Fake<T>(Func<MethodInfo, object?[], object?> call) where T : class {
        var proxy = DispatchProxy.Create<T, ProductionGatewayTests.Proxy>(); ((ProductionGatewayTests.Proxy)(object)proxy).Call = call; return proxy;
    }
    public MediaPresentationTests() {
        Directory.CreateDirectory(root);
        var paths = Fake<IApplicationPaths>((m, _) => m.Name == "get_DataPath" ? root : throw new NotSupportedException());
        runtime = new(paths, NullLogger<QueueRuntime>.Instance, TimeProvider.System);
        var p = runtime.Write(Guid.NewGuid(), "fixture", "Seed", s => QueueRulesTests.Propose(s)); proposal = p.Id;
        original = Path.Combine(root, "poster.png");
        using (var bitmap = new SKBitmap(240, 360)) { using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.Navy); using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); bytes = data.ToArray(); }
        File.WriteAllBytes(original, bytes);
        movie = new() { Id = p.Target.JellyfinItemId, Name = p.Target.Title, ImageInfos = [new() { Path = original, Type = ImageType.Primary, DateModified = File.GetLastWriteTimeUtc(original) }] };
        var library = Fake<ILibraryManager>((m, a) => m.Name == "GetItemById" ? (Guid)a[0]! == movie.Id ? movie : (Guid)a[0]! == collection.Id && collectionAvailable ? collection : null : throw new NotSupportedException(m.Name));
        var manager = Fake<ICollectionManager>((m, a) => m.Name switch {
            "GetCollectionsContainingItem" => members.Contains((Guid)a[1]!) ? new[] { collection } : Array.Empty<BoxSet>(),
            "AddToCollectionAsync" => Add((IEnumerable<Guid>)a[1]!),
            "RemoveFromCollectionAsync" => Remove((IEnumerable<Guid>)a[1]!),
            _ => throw new NotSupportedException(m.Name)
        });
        var persistence = Fake<IItemPersistenceService>((m, _) => {
            if (m.Name != "SaveImagesAsync") throw new NotSupportedException(m.Name);
            imageSaves++;
            return failImageSaves-- > 0 ? Task.FromException(new IOException("Disposable save failure")) : Task.CompletedTask;
        });
        service = new(runtime, library, manager, persistence, paths, new ArtworkRenderer(), TimeProvider.System);
    }
    private Task Add(IEnumerable<Guid> ids) { foreach (var id in ids) members.Add(id); return Task.CompletedTask; }
    private Task Remove(IEnumerable<Guid> ids) { foreach (var id in ids) members.Remove(id); return Task.CompletedTask; }
    private void Undo() => runtime.Write(Guid.NewGuid(), "fixture", "Undo", s => { var p = QueueRules.Find(s, proposal); QueueRules.Undo(s, p.Id, p.OwnerId, p.Revision, DateTimeOffset.UtcNow); return true; });
    [Fact] public async Task OverlayAndCollectionAreReversibleWithoutTouchingOriginalBytesOrUnrelatedMembers() {
        var unrelated = Guid.NewGuid(); members.Add(unrelated);
        await service.SaveAsync(new(CollectionEnabled: true, CollectionId: collection.Id, OverlaysEnabled: true), () => {}, default);
        await service.SyncAsync(default);
        Assert.NotEqual(original, movie.PrimaryImagePath); Assert.Equal(bytes, File.ReadAllBytes(original)); Assert.Contains(movie.Id, members);
        var first = movie.PrimaryImagePath; await service.SyncAsync(default); Assert.Equal(first, movie.PrimaryImagePath);
        Undo(); await service.SyncAsync(default);
        Assert.Equal(original, movie.PrimaryImagePath); Assert.Equal(bytes, File.ReadAllBytes(original)); Assert.DoesNotContain(movie.Id, members); Assert.Contains(unrelated, members);
        Assert.Equal("restored", Assert.Single(runtime.Read().Presentation.Artwork).Value.Status);
    }
    [Fact] public async Task ExistingMembershipIsNotRemovedAndAdministratorArtworkIsNotOverwritten() {
        members.Add(movie.Id);
        await service.SaveAsync(new(CollectionEnabled: true, CollectionId: collection.Id, OverlaysEnabled: true), () => {}, default); await service.SyncAsync(default);
        var edited = Path.Combine(root, "admin.png"); File.WriteAllBytes(edited, bytes); movie.GetImageInfo(ImageType.Primary, 0).Path = edited;
        Undo(); await service.SyncAsync(default);
        Assert.Equal(edited, movie.PrimaryImagePath); Assert.Contains(movie.Id, members); Assert.Equal("conflict", Assert.Single(runtime.Read().Presentation.Artwork).Value.Status);
    }
    [Fact] public async Task MissingOriginalCanBeRestoredFromThePreservedBackup() {
        await service.SaveAsync(new(OverlaysEnabled: true), () => {}, default); await service.SyncAsync(default); File.Delete(original);
        Undo(); await service.SyncAsync(default);
        Assert.EndsWith(".png", movie.PrimaryImagePath); Assert.Equal(bytes, File.ReadAllBytes(movie.PrimaryImagePath));
    }
    [Fact] public async Task PreparedReceiptRecoversWithoutPaintingOverAnOverlay() {
        await service.SaveAsync(new(OverlaysEnabled: true), () => {}, default); await service.SyncAsync(default);
        var output = movie.PrimaryImagePath;
        runtime.Write(Guid.NewGuid(), "fixture", "Interrupted", s => { foreach (var r in s.Presentation.Artwork.Values) r.Status = "prepared"; return true; });
        movie.GetImageInfo(ImageType.Primary, 0).Path = original;
        await service.SyncAsync(default); Assert.Equal(output, movie.PrimaryImagePath); Assert.Equal(bytes, File.ReadAllBytes(original));
    }
    [Fact] public void TemplatesAreBoundedAndPendingDatesAreNeverInvented() {
        var template = new OverlayTemplate(); Assert.Contains("pending", template.Format(null, false));
        Assert.Throws<QueueException>(() => (template with { Text = "{path}" }).Validate());
        Assert.Throws<QueueException>(() => (template with { BandHeight = float.NaN }).Validate());
        var renderer = new ArtworkRenderer(); var preview = renderer.Preview(template, false, "Earliest 2026-10-30");
        using var bitmap = SKBitmap.Decode(preview); Assert.Equal(800, bitmap.Width); Assert.Equal(450, bitmap.Height);
    }
    [Fact] public void LayeredTemplatesValidateGeometryAndRenderWithoutInventingDates() {
        var layer = new OverlayLayer("shape", Kind: "rectangle", X: 0, Y: 0, Width: 1, Height: 1, Color: "#123456");
        var template = new OverlayTemplate { Layers = [layer] }; template.Validate();
        var renderer = new ArtworkRenderer(); using var rendered = SKBitmap.Decode(renderer.Render(bytes, template, "sample"));
        Assert.Equal(SKColor.Parse("#123456"), rendered.GetPixel(10, 10));
        Assert.Contains("pending", new OverlayLayer("text").Format(null, false));
        using var unchanged = SKBitmap.Decode(renderer.Render(bytes, template with { Layers = [] }, "sample"));
        Assert.Equal(SKColors.Navy, unchanged.GetPixel(10, 10));
        Assert.Throws<QueueException>(() => (template with { Layers = [layer, layer] }).Validate());
        Assert.Throws<QueueException>(() => (layer with { X = float.NaN }).Validate());
        Assert.Throws<QueueException>(() => (layer with { X = .5f }).Validate());
        Assert.Throws<QueueException>(() => (layer with { Text = "{path}" }).Validate());
        Assert.Throws<QueueException>(() => (template with { Layers = Enumerable.Range(0, 13).Select(i => layer with { Id = i.ToString() }).ToArray() }).Validate());
    }
    [Fact] public async Task FreePositionLayeredOverlaysStillRestoreTheOriginalNativePointerAndBytes() {
        var template = new OverlayTemplate { Layers = [new("date", Text: "Removal {date}", X: .1f, Y: .2f, Width: .8f, Height: .2f, Rotation: 12)] };
        await service.SaveAsync(new MediaPresentationSettings(OverlaysEnabled: true) { Portrait = template }, () => {}, default);
        await service.SyncAsync(default); Assert.NotEqual(original, movie.PrimaryImagePath); Assert.Equal(bytes, File.ReadAllBytes(original));
        Undo(); await service.SyncAsync(default); Assert.Equal(original, movie.PrimaryImagePath); Assert.Equal(bytes, File.ReadAllBytes(original));
    }
    [Fact] public async Task FailedNativeSaveRestoresOriginalAndCanRetry() {
        await service.SaveAsync(new(OverlaysEnabled: true), () => {}, default); failImageSaves = 1;
        await service.SyncAsync(default);
        Assert.Equal(original, movie.PrimaryImagePath); Assert.Equal(2, imageSaves);
        Assert.Equal("restored", Assert.Single(runtime.Read().Presentation.Artwork).Value.Status);
        await service.SyncAsync(default); Assert.NotEqual(original, movie.PrimaryImagePath);
        Assert.Equal(bytes, File.ReadAllBytes(original));
    }
    [Fact] public async Task PreparedCachedPointerIsPersistedAgainBeforeRecoveryIsAcknowledged() {
        await service.SaveAsync(new(OverlaysEnabled: true), () => {}, default); await service.SyncAsync(default);
        runtime.Write(Guid.NewGuid(), "fixture", "Interrupted", s => { foreach (var r in s.Presentation.Artwork.Values) r.Status = "prepared"; return true; });
        var before = imageSaves; await service.SyncAsync(default);
        Assert.Equal(before + 1, imageSaves); Assert.Equal("applied", Assert.Single(runtime.Read().Presentation.Artwork).Value.Status);
    }
    [Fact] public async Task DeletedSelectedCollectionDoesNotPreventDisablingOrRestoringArtwork() {
        await service.SaveAsync(new(CollectionEnabled: true, CollectionId: collection.Id, OverlaysEnabled: true), () => {}, default); await service.SyncAsync(default);
        collectionAvailable = false;
        await service.SaveAsync(new(Revision: 1, CollectionId: collection.Id), () => {}, default); await service.SyncAsync(default);
        Assert.Equal(original, movie.PrimaryImagePath); Assert.Empty(runtime.Read().Presentation.AddedMembers[collection.Id]);
    }
    [Fact] public async Task OriginalFileEditsArePreservedAndReportedAsConflicts() {
        await service.SaveAsync(new(OverlaysEnabled: true), () => {}, default); await service.SyncAsync(default);
        var edited = bytes.Concat(new byte[] { 1 }).ToArray(); File.WriteAllBytes(original, edited);
        Undo(); await service.SyncAsync(default);
        Assert.Equal(edited, File.ReadAllBytes(original)); Assert.Equal("conflict", Assert.Single(runtime.Read().Presentation.Artwork).Value.Status);
    }
    [Fact] public async Task TemplateChangeRendersFromOriginalAndRejectsStaleSettings() {
        var saved = await service.SaveAsync(new(OverlaysEnabled: true), () => {}, default); await service.SyncAsync(default); var first = movie.PrimaryImagePath;
        await Assert.ThrowsAsync<QueueException>(() => service.SaveAsync(new(), () => {}, default));
        await service.SaveAsync(saved with { Portrait = new(Background: "#123456") }, () => {}, default); await service.SyncAsync(default);
        Assert.NotEqual(first, movie.PrimaryImagePath); Assert.Equal(bytes, File.ReadAllBytes(original));
        Undo(); await service.SyncAsync(default); Assert.Equal(original, movie.PrimaryImagePath);
    }
    [Fact] public async Task ArtworkDiskCapacityStopsNewGenerationButNeverRestoration() {
        await service.SaveAsync(new(OverlaysEnabled: true), () => {}, default); await service.SyncAsync(default); var first = movie.PrimaryImagePath;
        var occupied = Path.Combine(root, "deletion-queue", "artwork", "capacity-fixture");
        using (var file = File.Create(occupied)) file.SetLength(512L * 1024 * 1024);
        var settings = runtime.Read().Presentation.Settings;
        await service.SaveAsync(settings with { Portrait = new(Background: "#123456") }, () => {}, default); await service.SyncAsync(default);
        Assert.Equal(first, movie.PrimaryImagePath); Assert.Contains("storage exceeds", service.Status); Assert.True(runtime.Available);
        Undo(); await service.SyncAsync(default); Assert.Equal(original, movie.PrimaryImagePath); Assert.True(runtime.Available);
    }
    [Fact] public async Task FailedRestoreRemainsRetryableWithoutFreezingAuthorization() {
        await service.SaveAsync(new(OverlaysEnabled: true), () => {}, default); await service.SyncAsync(default);
        Undo(); failImageSaves = 1; await service.SyncAsync(default);
        Assert.Equal("restoring", Assert.Single(runtime.Read().Presentation.Artwork).Value.Status);
        Assert.Contains("restoration pending", service.Status); Assert.True(runtime.Available);
        await service.SyncAsync(default); Assert.Equal(original, movie.PrimaryImagePath);
        Assert.Equal("restored", Assert.Single(runtime.Read().Presentation.Artwork).Value.Status);
    }
    [Fact] public async Task ExcessiveArtworkInventoryStopsGenerationWithoutFreezingQueueOrRestoration() {
        await service.SaveAsync(new(OverlaysEnabled: true), () => {}, default); await service.SyncAsync(default);
        var first = movie.PrimaryImagePath;
        var inventory = Path.Combine(root, "deletion-queue", "artwork", "inventory"); Directory.CreateDirectory(inventory);
        for (var i = 0; i < 10001; i++) File.WriteAllBytes(Path.Combine(inventory, i.ToString()), []);
        var settings = runtime.Read().Presentation.Settings;
        await service.SaveAsync(settings with { Portrait = new(Background: "#123456") }, () => {}, default); await service.SyncAsync(default);
        Assert.Equal(first, movie.PrimaryImagePath); Assert.Contains("storage exceeds", service.Status); Assert.True(runtime.Available);
        Undo(); await service.SyncAsync(default); Assert.Equal(original, movie.PrimaryImagePath);
    }
    public void Dispose() { runtime.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
