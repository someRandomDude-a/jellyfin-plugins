using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AccountHub.Security;

public sealed record SecurityReceipt(string BodyHash, DateTimeOffset ReceivedAt,
    string? CompatibilityReason, SecurityEvent? Event, bool DeliverAlerts)
{
    public string Source => "jellyfin-security";
}

public sealed record ReceiptResult(string Status);

/// <summary>Implemented once by Notifications using its shared durable intake/outbox.</summary>
public interface ISecurityReceiptStore
{
    // Atomically dedupe by source/body hash, persist sanitized receipt + audit/fanout intent before return.
    // Retain dedupe >=10 minutes across restart; a quarantine duplicate NEVER becomes a live alert.
    // Bound queue/quarantine capacity; reject on full/unavailable. No raw body/signature/proof storage.
    Task<ReceiptResult> AcceptAsync(SecurityReceipt receipt, CancellationToken ct);
}

public interface ISecurityIngressBudget
{
    // Global and trusted-source-IP rate limits; bounded keys. Called before reading bytes.
    bool TryAcquire(string sourceAddress);
}

// Consumed by the ONE Notifications receiver. No second controller/listener/DB is introduced here.
public sealed class SecurityWebhook(ISecurityCompatibility compatibility, ISecurityReceiptStore store,
    ISecurityIngressBudget budget, TimeProvider clock)
{
    public const int MaxBodyBytes = 65536;
    private readonly SemaphoreSlim _concurrency = new(8, 8);

    public async Task<ReceiptResult> ReceiveAsync(Stream body, string? timestamp, string? signature,
        string sourceAddress, ReadOnlyMemory<byte> signingSecret, ReadOnlyMemory<byte> ipHashKey,
        string alertMode, CancellationToken ct)
    {
        if (!budget.TryAcquire(sourceAddress) || !await _concurrency.WaitAsync(0, ct))
            throw new SecurityFailure("rate_limited", 429, retryAfter: 1);
        byte[]? raw = null;
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        try
        {
            if (signingSecret.IsEmpty || signingSecret.Span.IndexOfAnyExcept((byte)32) < 0)
                throw new SecurityFailure("unauthorized", 401);
            if (alertMode is not ("native" or "hub" or "both")) throw new SecurityFailure("unavailable", 503);
            // Bounded read, even with no Content-Length. Auth happens before JSON parsing.
            using (var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                readDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    int count;
                    while ((count = await body.ReadAsync(chunk, readDeadline.Token)) > 0)
                    {
                        if (buffer.Length + count > MaxBodyBytes) throw new SecurityFailure("payload_too_large", 413);
                        buffer.Write(chunk, 0, count);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { throw new SecurityFailure("request_timeout", 408); }
            }
            raw = buffer.ToArray();
            var now = clock.GetUtcNow();
            Authenticate(raw, timestamp, signature, signingSecret.Span, now);
            var hash = Convert.ToHexStringLower(SHA256.HashData(raw));
            var health = compatibility.Check();
            if (!health.IsOk)
            {
                try { return await store.AcceptAsync(new(hash, now, health.State, null, false), ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { throw new SecurityFailure("security_incompatible", 409); }
            }
            SecurityEvent parsed;
            if (ipHashKey.Length < 32) throw new SecurityFailure("unavailable", 503);
            try { parsed = SecurityEventParser.Parse(raw, now, ipHashKey.Span); }
            catch (JsonException) { throw new SecurityFailure("invalid_request", 400); }
            try
            {
                return await store.AcceptAsync(new(hash, now, null, parsed,
                    alertMode != "native" && !parsed.IsTest), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { throw new SecurityFailure("unavailable", 503, true); }
        }
        finally
        {
            if (raw is not null) CryptographicOperations.ZeroMemory(raw);
            CryptographicOperations.ZeroMemory(chunk);
            CryptographicOperations.ZeroMemory(buffer.GetBuffer());
            _concurrency.Release();
        }
    }

    public static void Authenticate(ReadOnlySpan<byte> raw, string? timestamp, string? signature,
        ReadOnlySpan<byte> secret, DateTimeOffset now)
    {
        if (secret.IsEmpty || timestamp is null || timestamp.Length > 20
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unix)
            || Math.Abs((decimal)now.ToUnixTimeSeconds() - unix) > 300
            || signature is null || signature.Length != 71 || !signature.StartsWith("sha256=", StringComparison.Ordinal)
            || signature.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw new SecurityFailure("unauthorized", 401);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, secret);
        hmac.AppendData(Encoding.UTF8.GetBytes(timestamp + "."));
        hmac.AppendData(raw);
        var expected = hmac.GetHashAndReset();
        var supplied = Convert.FromHexString(signature.AsSpan(7));
        if (!CryptographicOperations.FixedTimeEquals(expected, supplied)) throw new SecurityFailure("unauthorized", 401);
    }
}
