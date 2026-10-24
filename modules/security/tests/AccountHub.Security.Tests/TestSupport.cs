using System.Net;
using System.Text;

namespace AccountHub.Security.Tests;

internal sealed class CompatibilityStub(string state = "ok") : ISecurityCompatibility
{
    public string State { get; set; } = state;
    public Compatibility Check() => new(State, "2.6.4", DateTimeOffset.UtcNow);
}

internal sealed class HttpStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<(string Method, string Url, string? Token, string? Body)> Requests { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.Method.Method, request.RequestUri!.AbsoluteUri,
            request.Headers.Authorization?.Scheme == "MediaBrowser" ? request.Headers.Authorization.Parameter?[7..^1] : null,
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
        return await respond(request);
    }
    public static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

internal sealed class FixedClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-10-22T12:00:00Z");
}

internal sealed class Budget : ISecurityIngressBudget
{
    public bool Allowed { get; set; } = true;
    public bool TryAcquire(string sourceAddress) => Allowed;
}

// Test-only memory store; production MUST supply Notifications' durable implementation.
internal sealed class ReceiptStore : ISecurityReceiptStore
{
    public Dictionary<string, SecurityReceipt> Receipts { get; } = [];
    public bool Fail { get; set; }
    public Task<ReceiptResult> AcceptAsync(SecurityReceipt receipt, CancellationToken ct)
    {
        if (Fail) throw new IOException();
        return Task.FromResult(new ReceiptResult(Receipts.TryAdd(receipt.BodyHash, receipt) ? "accepted" : "duplicate"));
    }
}
