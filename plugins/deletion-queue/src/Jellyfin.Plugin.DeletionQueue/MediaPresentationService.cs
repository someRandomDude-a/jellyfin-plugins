using System.Security.Cryptography;
using DeletionQueue.Core;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.DeletionQueue;

public sealed class MediaPresentationService(QueueRuntime runtime, ILibraryManager library, ICollectionManager collections,
    IItemPersistenceService persistence, IApplicationPaths paths, ArtworkRenderer renderer, TimeProvider clock)
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly string root = Path.Combine(paths.DataPath, "deletion-queue", "artwork");
    private static readonly ImageType[] Types = [ImageType.Primary, ImageType.Thumb, ImageType.Backdrop, ImageType.Banner];
    public string Status { get; private set; } = "idle";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static byte[] ReadImage(string path) {
        using var stream = File.OpenRead(path);
        if (stream.Length is <= 0 or > ArtworkRenderer.MaxBytes) throw new QueueException("ArtworkCapacityExceeded");
        var result = new byte[checked((int)stream.Length)]; stream.ReadExactly(result); return result;
    }
    private void DurableFile(string path, byte[] bytes) {
        if (!OwnedPath(path) || !OwnedPath(path + ".pending")) throw new QueueException("ArtworkConflict");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) { if (Hash(ReadImage(path)) != Hash(bytes)) throw new QueueException("ArtworkConflict"); return; }
        var pending = path + ".pending";
        using (var file = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { file.Write(bytes); file.Flush(true); }
        File.Move(pending, path, false);
    }
    private bool OwnedPath(string path) {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
        // Lexical containment alone does not exclude symlinks/junctions to another directory.
        for (var current = full; !Same(current, Path.GetFullPath(paths.DataPath)); current = Path.GetDirectoryName(current)!) {
            if (current is null) return false;
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false; }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return true;
    }
    private bool HasFileCapacity(string path, int bytes) {
        if (!OwnedPath(path)) throw new QueueException("ArtworkConflict");
        if (File.Exists(path)) return true;
        if (!Directory.Exists(root)) return true;
        long total = bytes; var count = 0;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory)) foreach (var entry in Directory.EnumerateFileSystemEntries(directory)) {
            if (++count > 10000) return false;
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new QueueException("ArtworkConflict");
            if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            else { total += new FileInfo(entry).Length; if (total > 512L * 1024 * 1024) return false; }
        }
        return true;
    }
    private static bool Same(string a, string b) => string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private void Change(string kind, Action<MediaPresentationState> change) => runtime.Write(Guid.NewGuid(), "media-presentation", kind, state => {
        change(state.Presentation);
        // Reserve room for domain state; presentation capacity must not freeze deletion authorization.
        if (kind is "ArtworkPrepared" or "CollectionMembershipPrepared" or "CollectionCreationPrepared" or "CollectionCreated" &&
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(state).Length > 3 * 1024 * 1024) throw new QueueException("PresentationStorageLimit");
        return true;
    });
    public async Task<MediaPresentationSettings> SaveAsync(MediaPresentationSettings settings, Action authorize, CancellationToken ct)
    {
        settings.Validate(); await gate.WaitAsync(ct);
        try {
            authorize();
            if (settings.CollectionEnabled && settings.CollectionId is { } id && library.GetItemById(id) is not BoxSet) throw new QueueException("CollectionUnavailable");
            return runtime.Write(Guid.NewGuid(), "administrator", "MediaPresentationConfigured", state => {
                authorize(); if (state.Presentation.Settings.Revision != settings.Revision) throw new QueueException("RevisionChanged");
                state.Presentation.Settings = settings with { Revision = settings.Revision + 1 }; return state.Presentation.Settings;
            });
        } finally { gate.Release(); }
    }

    public async Task SyncAsync(CancellationToken ct, bool syncCollections = true, bool syncArtwork = true, int maximumChanges = 20)
    {
        if (maximumChanges is < 1 or > 1000) throw new QueueException("InvalidPresentationBudget");
        await gate.WaitAsync(ct);
        try {
            Status = "syncing";
            var state = runtime.Read(); state.Presentation.Settings.Validate();
            var requests = state.Proposals.Where(p => !p.Terminal && p.Target.JellyfinItemId != Guid.Empty)
                .GroupBy(p => p.Target.JellyfinItemId).ToDictionary(g => g.Key, g => g.ToArray());
            try { if (syncCollections) await SyncCollectionAsync(state.Presentation.Settings, requests.Keys.ToHashSet(), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { Status = "Collection unavailable; artwork restoration remains active"; if (!syncArtwork) throw; }
            if (!syncArtwork) { if (Status == "syncing") Status = "ready"; return; }
            var wanted = new Dictionary<Guid, (DateTimeOffset? Date, bool Is4K)>();
            if (state.Presentation.Settings.OverlaysEnabled) foreach (var (id, proposals) in requests) {
                // A shared item never advertises a firm date while any edition is blocked.
                var dates = proposals.Select(p => QueueRules.Project(state, p, p.OwnerId, clock.GetUtcNow()).ScheduledFor).ToArray();
                var date = dates.All(d => d is not null) ? dates.Min() : null;
                wanted[id] = (date, proposals.All(p => p.Target.Is4K));
                if (proposals.Any(p => p.Target.Kind == MediaKind.Series)) {
                    var children = library.GetItemList(new InternalItemsQuery { ParentId = id, Recursive = true, IncludeItemTypes = [BaseItemKind.Season, BaseItemKind.Episode], Limit = 1001 });
                    if (children.Count > 1000) { Status = "Series artwork scope exceeds 1000 items; no descendant artwork changed"; continue; }
                    foreach (var child in children) wanted.TryAdd(child.Id, (date, proposals.All(p => p.Target.Is4K)));
                }
            }
            var changed = 0;
            foreach (var (key, receipt) in runtime.Read().Presentation.Artwork) {
                ct.ThrowIfCancellationRequested();
                if (wanted.ContainsKey(receipt.ItemId) || receipt.Status is "restored" or "conflict" or "missing") continue;
                if (changed >= maximumChanges) { Status = "More artwork restoration pending"; break; }
                await RestoreSafelyAsync(key, receipt, ct); changed++;
            }
            foreach (var (id, schedule) in wanted) {
                var item = library.GetItemById(id); if (item is null) continue;
                foreach (var type in Types) for (var index = 0; index < item.ImageInfos.Count(i => i.Type == type); index++) {
                    ct.ThrowIfCancellationRequested();
                    if (changed >= maximumChanges) { Status = "More artwork pending"; return; }
                    try { if (await ApplyAsync(item, type, index, state.Presentation.Settings, schedule.Date, schedule.Is4K, ct)) changed++; }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception) {
                        var key = $"{item.Id:N}:{(int)type}:{index}";
                        var receipt = runtime.Read().Presentation.Artwork.GetValueOrDefault(key);
                        if (receipt is not null && receipt.Status is not ("conflict" or "restored")) await RestoreSafelyAsync(key, receipt, ct);
                        Status = "Artwork unavailable; originals retained"; changed++;
                    }
                }
            }
            if (Status == "syncing") Status = "ready";
        } catch { Status = "Presentation unavailable; originals retained"; throw; }
        finally { gate.Release(); }
    }

    private async Task SyncCollectionAsync(MediaPresentationSettings settings, HashSet<Guid> wanted, CancellationToken ct)
    {
        Guid? selected = null;
        if (settings.CollectionEnabled) {
            selected = settings.CollectionId ?? runtime.Read().Presentation.CreatedCollectionId;
            if (selected is null) {
                var token = runtime.Read().Presentation.CreationToken;
                if (token == Guid.Empty) { token = Guid.NewGuid(); Change("CollectionCreationPrepared", s => s.CreationToken = token); }
                var candidates = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.BoxSet], Recursive = true, Limit = 1001 });
                if (candidates.Count > 1000) throw new QueueException("CollectionInventoryIncomplete");
                var found = candidates.Where(c => c.ProviderIds.GetValueOrDefault("DeletionQueue") == token.ToString("N")).ToArray();
                if (found.Length > 1) throw new QueueException("CollectionIdentityAmbiguous");
                var created = found.SingleOrDefault() ?? await collections.CreateCollectionAsync(new CollectionCreationOptions { Name = settings.CollectionName, ProviderIds = new() { ["DeletionQueue"] = token.ToString("N") }, ItemIdList = [] });
                selected = created.Id; Change("CollectionCreated", s => s.CreatedCollectionId = created.Id);
            }
            if (library.GetItemById(selected.Value) is not BoxSet) throw new QueueException("CollectionUnavailable");
        }
        foreach (var (collection, owned) in runtime.Read().Presentation.AddedMembers) foreach (var id in owned.ToArray()) {
            ct.ThrowIfCancellationRequested();
            if (collection == selected && wanted.Contains(id)) continue;
            if (library.GetItemById(collection) is BoxSet) await collections.RemoveFromCollectionAsync(collection, [id]);
            Change("CollectionMembershipRestored", s => s.AddedMembers[collection].Remove(id));
        }
        if (selected is not { } selectedId) return;
        foreach (var id in wanted) {
            ct.ThrowIfCancellationRequested();
            if (library.GetItemById(id) is null) continue;
            var alreadyPresent = collections.GetCollectionsContainingItem(null!, id).Any(c => c.Id == selectedId);
            if (alreadyPresent) continue;
            // Durable intent precedes the native write; recovery only owns these additions.
            Change("CollectionMembershipPrepared", s => { if (!s.AddedMembers.TryGetValue(selectedId, out var members)) s.AddedMembers[selectedId] = members = []; members.Add(id); });
            await collections.AddToCollectionAsync(selectedId, [id]);
        }
    }

    private async Task<bool> ApplyAsync(BaseItem item, ImageType type, int index, MediaPresentationSettings settings, DateTimeOffset? date, bool is4K, CancellationToken ct)
    {
        var key = $"{item.Id:N}:{(int)type}:{index}";
        var info = item.GetImageInfo(type, index); if (info is null || !info.IsLocalFile || !File.Exists(info.Path)) return false;
        var receipt = runtime.Read().Presentation.Artwork.GetValueOrDefault(key);
        if (receipt is not null && receipt.Status is "conflict" or "missing") return false;
        byte[] original;
        if (receipt is null || receipt.Status == "restored") {
            original = ReadImage(info.Path);
            using var probeStream = new SkiaSharp.SKMemoryStream(original);
            using var probe = SkiaSharp.SKCodec.Create(probeStream) ?? throw new QueueException("UnsupportedArtwork");
            var extension = probe.EncodedFormat.ToString().ToLowerInvariant() switch { "jpeg" => ".jpg", "png" => ".png", "webp" => ".webp", "gif" => ".gif", "bmp" => ".bmp", _ => throw new QueueException("UnsupportedArtwork") };
            var hash = Hash(original); var backup = Path.Combine(root, "originals", hash + extension);
            if (runtime.Read().Presentation.Artwork.Count >= 1000 && receipt is null) { Status = "Artwork receipt capacity exceeds limit"; return false; }
            if (!HasFileCapacity(backup, original.Length)) { Status = "Artwork storage exceeds limit"; return false; }
            DurableFile(backup, original);
            receipt = new() { ItemId = item.Id, ImageType = (int)type, Index = index, OriginalPath = info.Path, OriginalHash = hash, BackupPath = backup };
        } else {
            if (!OwnedPath(receipt.BackupPath)) throw new QueueException("ArtworkConflict");
            original = ReadImage(receipt.BackupPath); if (Hash(original) != receipt.OriginalHash) throw new QueueException("ArtworkConflict");
            if (!MatchesReceipt(info.Path, receipt)) { Change("ArtworkConflict", s => s.Artwork[key].Status = "conflict"); return false; }
        }
        using var input = new SkiaSharp.SKMemoryStream(original);
        using var bitmap = SkiaSharp.SKCodec.Create(input) ?? throw new QueueException("UnsupportedArtwork");
        var template = bitmap.Info.Height >= bitmap.Info.Width ? settings.Portrait : settings.Landscape;
        var bytes = renderer.Render(original, template, template.Format(date, is4K), date, is4K); var renderedHash = Hash(bytes);
        var output = Path.Combine(root, "rendered", item.Id.ToString("N"), $"{(int)type}-{index}-{renderedHash}.png");
        if (Same(info.Path, output) && receipt.OverlayHash == renderedHash && Hash(ReadImage(output)) == renderedHash) {
            if (receipt.Status != "applied") {
                // A failed native save may have changed the cached object only.
                await persistence.SaveImagesAsync(item, ct);
                Change("ArtworkAppliedRecovered", s => s.Artwork[key].Status = "applied");
            }
            return false;
        }
        if (!HasFileCapacity(output, bytes.Length)) { Status = "Artwork storage exceeds limit"; return false; }
        DurableFile(output, bytes);
        if (!MatchesReceipt(info.Path, receipt)) return false;
        receipt.PreviousOverlayPath = OwnedPath(info.Path) && !Same(info.Path, receipt.BackupPath) ? info.Path : null;
        receipt.PreviousOverlayHash = receipt.PreviousOverlayPath is null ? null : Hash(ReadImage(info.Path));
        receipt.OverlayPath = output; receipt.OverlayHash = renderedHash; receipt.Status = "prepared";
        Change("ArtworkPrepared", s => s.Artwork[key] = receipt);
        info.Path = output; info.DateModified = File.GetLastWriteTimeUtc(output); info.BlurHash = null!;
        await persistence.SaveImagesAsync(item, ct);
        Change("ArtworkApplied", s => s.Artwork[key].Status = "applied");
        return true;
    }
    private bool MatchesReceipt(string current, ArtworkReceipt receipt)
    {
        if (!File.Exists(current)) return false;
        var hash = Hash(ReadImage(current));
        return Same(current, receipt.OriginalPath) && hash == receipt.OriginalHash ||
            OwnedPath(current) && Same(current, receipt.BackupPath) && hash == receipt.OriginalHash ||
            OwnedPath(current) && (Same(current, receipt.OverlayPath) && hash == receipt.OverlayHash || Same(current, receipt.PreviousOverlayPath ?? "") && hash == receipt.PreviousOverlayHash);
    }
    private async Task RestoreAsync(string key, ArtworkReceipt receipt, CancellationToken ct)
    {
        var item = library.GetItemById(receipt.ItemId);
        if (item is null) { Change("ArtworkItemMissing", s => s.Artwork[key].Status = "missing"); return; }
        var info = item.GetImageInfo((ImageType)receipt.ImageType, receipt.Index);
        if (info is null || !MatchesReceipt(info.Path, receipt)) { Change("ArtworkConflict", s => s.Artwork[key].Status = "conflict"); return; }
        if (!OwnedPath(receipt.BackupPath) || !File.Exists(receipt.BackupPath) || Hash(ReadImage(receipt.BackupPath)) != receipt.OriginalHash) throw new QueueException("ArtworkConflict");
        if (File.Exists(receipt.OriginalPath) && Hash(ReadImage(receipt.OriginalPath)) != receipt.OriginalHash) { Change("ArtworkConflict", s => s.Artwork[key].Status = "conflict"); return; }
        var original = File.Exists(receipt.OriginalPath) && Hash(ReadImage(receipt.OriginalPath)) == receipt.OriginalHash ? receipt.OriginalPath : receipt.BackupPath;
        Change("ArtworkRestorePrepared", s => s.Artwork[key].Status = "restoring");
        info.Path = original; info.DateModified = File.GetLastWriteTimeUtc(original); info.BlurHash = null!;
        await persistence.SaveImagesAsync(item, ct);
        Change("ArtworkRestored", s => s.Artwork[key].Status = "restored");
    }
    private async Task RestoreSafelyAsync(string key, ArtworkReceipt receipt, CancellationToken ct)
    {
        try { await RestoreAsync(key, receipt, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { Status = "Artwork restoration pending; originals retained"; }
    }
}
