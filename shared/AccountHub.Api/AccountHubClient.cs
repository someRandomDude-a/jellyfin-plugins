using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace AccountHub.Api;

/// <summary>Bounded optional transport, shared by every producer. Never call on a domain writer.</summary>
public sealed class AccountHubClient
{
    public const string ServiceKey = Contract.ServiceKey;
    private readonly Func<Func<string, CancellationToken, Task<string>>?> resolve;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Task<string>? inflight;

    public AccountHubClient(IServiceProvider services) : this(() => services.GetKeyedService<Func<string, CancellationToken, Task<string>>>(ServiceKey)) { }
    public AccountHubClient(Func<Func<string, CancellationToken, Task<string>>?> resolve) => this.resolve = resolve;
    public bool IsAvailable { get { try { return resolve() is not null; } catch { return false; } } }

    public async Task<JsonElement?> CallAsync(string operation, object payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false)) return null;
        try
        {
            // A host ignoring cancellation cannot accumulate abandoned tasks on repeated polls.
            if (inflight is { IsCompleted: false }) return null;
            var handler = resolve();
            if (handler is null) return null;
            var request = Contract.Serialize(new { apiVersion = "1", operation, requestId = Guid.NewGuid(), payload });
            if (Encoding.UTF8.GetByteCount(request) > Contract.BatchLimit) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            inflight = handler(request, timeout.Token);
            _ = inflight.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var response = await inflight.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (Encoding.UTF8.GetByteCount(response) > Contract.BatchLimit) return null;
            using var doc = JsonDocument.Parse(response, new JsonDocumentOptions { MaxDepth = 32 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("apiVersion").GetString() != "1"
                || !root.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            return root.Clone();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
        finally { gate.Release(); }
    }

    public Task<JsonElement?> InvokeAsync(string operation, JsonElement payload, CancellationToken ct) => CallAsync(operation, payload, ct);
    public async Task<string> InvokeAsync(string operation, object payload, CancellationToken ct = default) =>
        (await CallAsync(operation, payload, ct).ConfigureAwait(false))?.GetRawText()
        ?? Contract.Serialize(Contract.Failure("unavailable", "Host unavailable", true));

    public Task<bool> ReadyAsync(CancellationToken ct) => ReadyForAsync(ct, "notifications.publish");
    public async Task<bool> ReadyForAsync(CancellationToken ct, string operation)
    {
        var hello = await CallAsync("hello", new { }, ct).ConfigureAwait(false);
        try
        {
            if (hello is not { } h || !h.GetProperty("ok").GetBoolean()) return false;
            var data = h.GetProperty("data");
            if (data.GetProperty("apiVersion").GetString() != "1" ||
                !data.GetProperty("operations").EnumerateArray().Any(o => o.GetString() == operation)) return false;
            var health = await CallAsync("health", new { }, ct).ConfigureAwait(false);
            return health is { } s && s.GetProperty("ok").GetBoolean() && s.GetProperty("data").GetProperty("ready").GetBoolean();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    public async Task<string> PublishAsync(string kind, string payloadJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var response = await CallAsync(kind == "notification" ? "notifications.publish" : "audit.publish", doc.RootElement, ct).ConfigureAwait(false);
        try
        {
            if (response is not { } r) return "unavailable";
            if (!r.GetProperty("ok").GetBoolean()) return r.GetProperty("error").GetProperty("code").GetString()
                is "rejected" or "invalid_request" or "unauthorized" or "event_conflict" ? "rejected" : "unavailable";
            return r.GetProperty("data").GetProperty("status").GetString() is "accepted" or "duplicate" ? "delivered" : "rejected";
        }
        catch { return "unavailable"; }
    }
}
