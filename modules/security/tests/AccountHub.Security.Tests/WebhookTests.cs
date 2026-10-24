using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AccountHub.Security.Tests;

public sealed class WebhookTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("test-only-shared-signing-secret-32bytes");
    private static readonly byte[] PrivacyKey = Encoding.UTF8.GetBytes("test-only-separate-ip-privacy-key-32bytes");
    private static readonly FixedClock Clock = new();
    private static string Timestamp => Clock.GetUtcNow().ToUnixTimeSeconds().ToString();
    private static string Sign(byte[] body, string? timestamp = null) => "sha256=" + Convert.ToHexStringLower(
        HMACSHA256.HashData(Secret, Encoding.UTF8.GetBytes((timestamp ?? Timestamp) + ".").Concat(body).ToArray()));
    private static byte[] Body(string type) => Encoding.UTF8.GetBytes($$$"""{"event":"{{{type}}}","timestamp":"2026-10-22T12:00:00Z","payload":{"username":"alice","ip":"203.0.113.10"}}""");

    [Theory]
    [InlineData("lockout", "security.lockout", "high")]
    [InlineData("suspicious_login", "security.suspicious_login", "high")]
    [InlineData("emergency_lockout", "security.emergency_lockout", "critical")]
    [InlineData("recovery_code_used", "security.recovery_code_used", "high")]
    [InlineData("passkey_registered", "security.passkey_registered", "info")]
    [InlineData("totp_rotated", "security.totp_rotated", "info")]
    [InlineData("admin_force_logout", "security.admin_force_logout", "high")]
    [InlineData("login_attempt", "security.login_attempt", "info")]
    [InlineData("failed_attempts", "security.failed_attempts", "warning")]
    [InlineData("test", "security.webhook.test", "info")]
    [InlineData("new device", "security.unknown", "warning")]
    [InlineData("suspicious login", "security.unknown", "warning")]
    public void ExplicitWireKeysOnly(string wire, string expected, string severity)
    {
        var result = SecurityEventParser.Parse(Body(wire), Clock.GetUtcNow(), PrivacyKey);
        Assert.Equal(expected, result.Type);
        Assert.Equal(severity, result.Severity);
        Assert.True(result.AdminOnly);
        Assert.DoesNotContain("203.0.113.10", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void PairingFixtureDropsProseAndAllPayload()
    {
        var raw = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "pairing_request.json"));
        var result = SecurityEventParser.Parse(raw, Clock.GetUtcNow(), PrivacyKey);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("CANARY", json);
        Assert.DoesNotContain("123456", json);
        Assert.Null(result.Username);
        Assert.Null(result.IpHash);
    }

    [Fact]
    public void SignsExactBytesNotReserializedJson()
    {
        var body = Body("test");
        SecurityWebhook.Authenticate(body, Timestamp, Sign(body), Secret, Clock.GetUtcNow());
        var changed = body.Concat(new byte[] { 32 }).ToArray();
        Assert.Throws<SecurityFailure>(() => SecurityWebhook.Authenticate(changed, Timestamp, Sign(body), Secret, Clock.GetUtcNow()));
    }

    [Theory]
    [InlineData(-301, false)][InlineData(301, false)][InlineData(-300, true)][InlineData(300, true)]
    public void SkewIsBounded(int offset, bool valid)
    {
        var body = Body("test");
        var timestamp = (Clock.GetUtcNow().ToUnixTimeSeconds() + offset).ToString();
        if (valid) SecurityWebhook.Authenticate(body, timestamp, Sign(body, timestamp), Secret, Clock.GetUtcNow());
        else Assert.Throws<SecurityFailure>(() => SecurityWebhook.Authenticate(body, timestamp, Sign(body, timestamp), Secret, Clock.GetUtcNow()));
    }

    [Theory]
    [InlineData(null)][InlineData("")][InlineData("sha256=abc")]
    public void MissingOrMalformedSignatureFails(string? signature) => Assert.Throws<SecurityFailure>(() =>
        SecurityWebhook.Authenticate(Body("test"), Timestamp, signature, Secret, Clock.GetUtcNow()));

    [Theory]
    [InlineData("native", "suspicious_login", false)]
    [InlineData("hub", "suspicious_login", true)]
    [InlineData("both", "suspicious_login", true)]
    [InlineData("hub", "test", false)]
    public async Task DeliveryModesAndDuplicateAreSafe(string mode, string type, bool deliver)
    {
        var store = new ReceiptStore();
        var receiver = new SecurityWebhook(new CompatibilityStub(), store, new Budget(), Clock);
        var body = Body(type);
        var first = await receiver.ReceiveAsync(new MemoryStream(body), Timestamp, Sign(body), "127.0.0.1", Secret, PrivacyKey, mode, default);
        var second = await receiver.ReceiveAsync(new MemoryStream(body), Timestamp, Sign(body), "127.0.0.1", Secret, PrivacyKey, mode, default);
        Assert.Equal("accepted", first.Status);
        Assert.Equal("duplicate", second.Status);
        Assert.Equal(deliver, Assert.Single(store.Receipts).Value.DeliverAlerts);
    }

    [Fact]
    public async Task MismatchQuarantinesWithoutParsingAndNeverReplays()
    {
        var store = new ReceiptStore();
        var compat = new CompatibilityStub("incompatible");
        var receiver = new SecurityWebhook(compat, store, new Budget(), Clock);
        var body = Body("suspicious_login");
        await receiver.ReceiveAsync(new MemoryStream(body), Timestamp, Sign(body), "source", Secret, PrivacyKey, "hub", default);
        var receipt = Assert.Single(store.Receipts).Value;
        Assert.Null(receipt.Event);
        Assert.False(receipt.DeliverAlerts);
        Assert.Equal("incompatible", receipt.CompatibilityReason);
        compat.State = "ok";
        var retry = await receiver.ReceiveAsync(new MemoryStream(body), Timestamp, Sign(body), "source", Secret, PrivacyKey, "hub", default);
        Assert.Equal("duplicate", retry.Status);
        Assert.Null(Assert.Single(store.Receipts).Value.Event);
    }

    [Theory]
    [InlineData("ok", 503)][InlineData("incompatible", 409)]
    public async Task StorageFailureNeverAcknowledged(string state, int expected)
    {
        var receiver = new SecurityWebhook(new CompatibilityStub(state), new ReceiptStore { Fail = true }, new Budget(), Clock);
        var body = Body("test");
        var failure = await Assert.ThrowsAsync<SecurityFailure>(() => receiver.ReceiveAsync(new MemoryStream(body), Timestamp, Sign(body),
            "source", Secret, PrivacyKey, "hub", default));
        Assert.Equal(expected, failure.Status);
    }

    [Fact]
    public async Task SizeLimitAppliesWithoutContentLength()
    {
        var receiver = new SecurityWebhook(new CompatibilityStub(), new ReceiptStore(), new Budget(), Clock);
        var body = new byte[SecurityWebhook.MaxBodyBytes + 1];
        var failure = await Assert.ThrowsAsync<SecurityFailure>(() => receiver.ReceiveAsync(new MemoryStream(body), Timestamp, Sign(body),
            "source", Secret, PrivacyKey, "hub", default));
        Assert.Equal(413, failure.Status);
    }

    [Fact]
    public async Task BudgetRejectsBeforeReading()
    {
        var receiver = new SecurityWebhook(new CompatibilityStub(), new ReceiptStore(), new Budget { Allowed = false }, Clock);
        var failure = await Assert.ThrowsAsync<SecurityFailure>(() => receiver.ReceiveAsync(Stream.Null, null, null,
            "source", Secret, PrivacyKey, "hub", default));
        Assert.Equal(429, failure.Status);
    }

    [Fact]
    public async Task MissingSecretNeverAcceptsEvenSignedPayload()
    {
        var store = new ReceiptStore();
        var receiver = new SecurityWebhook(new CompatibilityStub(), store, new Budget(), Clock);
        var body = Body("test");
        var failure = await Assert.ThrowsAsync<SecurityFailure>(() => receiver.ReceiveAsync(new MemoryStream(body), Timestamp, Sign(body),
            "source", ReadOnlyMemory<byte>.Empty, PrivacyKey, "hub", default));
        Assert.Equal(401, failure.Status);
        Assert.Empty(store.Receipts);
    }

    [Fact]
    public async Task MismatchQuarantineNeverParsesEvenNonJson()
    {
        var store = new ReceiptStore();
        var receiver = new SecurityWebhook(new CompatibilityStub("incompatible"), store, new Budget(), Clock);
        var body = Encoding.UTF8.GetBytes("not JSON CANARY");
        await receiver.ReceiveAsync(new MemoryStream(body), Timestamp, Sign(body), "source", Secret, PrivacyKey, "hub", default);
        var receipt = Assert.Single(store.Receipts).Value;
        Assert.Null(receipt.Event);
        Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(receipt));
    }
}
