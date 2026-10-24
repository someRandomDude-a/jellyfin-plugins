using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordBridge.Api;

/// <summary>Optional bridge. Only framework strings/delegates cross plugin load contexts.</summary>
public sealed class DiscordBridgeClient(IServiceProvider services)
{
    public const string ServiceKey = "Jellyfin.DiscordBridge.Api.1";
    public const string IdentityKey = "AccountHub.DiscordIdentity.1";
    public const string LibraryCaptureKey = "Jellyfin.DiscordBridge.LibraryCapture.1";
    private readonly SemaphoreSlim gate = new(1, 1);
    private Task<string>? inflight;
    public async Task<JsonElement?> CallAsync(string operation, object payload, CancellationToken ct = default)
    {
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false)) return null;
        try
        {
            if (inflight is { IsCompleted: false }) return null;
            var handler = services.GetKeyedService<Func<string, CancellationToken, Task<string>>>(ServiceKey);
            if (handler is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            inflight = handler(JsonSerializer.Serialize(new { apiVersion = "1", operation, payload }), timeout.Token);
            _ = inflight.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var reply = await inflight.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (System.Text.Encoding.UTF8.GetByteCount(reply) > 65536) return null;
            using var doc = JsonDocument.Parse(reply);
            return doc.RootElement.GetProperty("apiVersion").GetString() == "1" ? doc.RootElement.Clone() : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
        finally { gate.Release(); }
    }
}
