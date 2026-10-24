using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccountHub.Api;
using Xunit;

namespace AccountHub.Notifications.Tests;

public sealed class SecurityTests
{
    private static (string Stamp, string Signature) Sign(Fixture f, byte[] body, int offset = 0)
    {
        var stamp = (f.Clock.Now.ToUnixTimeSeconds() + offset).ToString();
        return (stamp, "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(f.Host.SecurityWebhookSecret!), Encoding.UTF8.GetBytes(stamp + ".").Concat(body).ToArray())));
    }
    private static byte[] Body(string key = "pairing_request") => Encoding.UTF8.GetBytes(Contract.Serialize(new { @event = key, title = "SECRET-123456", message = "pair with SECRET-123456", payload = new { PairingCODE = "SECRET-123456", nested = new { token = "SECRET-123456" }, username = "private-name", ip = "203.0.113.1" } }));
    [Theory]
    [InlineData("native", false)]
    [InlineData("hub", true)]
    [InlineData("both", true)]
    public void Modes_and_pairing_redaction(string mode, bool alerts)
    {
        var f = new Fixture(); f.Host.SecurityAlertMode = mode;
        var ingress = new SecurityIngress(f.Store, f.Host, f.Clock, new(f.Clock));
        var body = Body(); var signed = Sign(f, body);
        ingress.Receive(body, signed.Stamp, signed.Signature);
        var receipt = Assert.Single(f.Store.PendingReceipts());
        Assert.Equal(alerts, receipt.Event is not null);
        var saved = Contract.Serialize(receipt.Audit!) + Contract.Serialize(receipt.Event ?? (object)"none");
        foreach (var secret in new[] { "SECRET-123456", "private-name", "203.0.113.1", "PairingCODE" }) Assert.DoesNotContain(secret, saved);
        Assert.Contains("duplicate", Contract.Serialize(ingress.Receive(body, signed.Stamp, signed.Signature)));
        Assert.Single(f.Store.PendingReceipts());
    }
    [Fact]
    public void Signature_exact_bytes_skew_and_missing_secret_fail_before_parsing()
    {
        var f = new Fixture(); var ingress = new SecurityIngress(f.Store, f.Host, f.Clock, new(f.Clock));
        var body = Body(); var signed = Sign(f, body);
        Assert.Equal(401, Assert.Throws<NotificationException>(() => ingress.Receive(Encoding.UTF8.GetBytes("invalid json"), signed.Stamp, signed.Signature)).Status);
        var stale = Sign(f, body, -301);
        Assert.Equal(401, Assert.Throws<NotificationException>(() => ingress.Receive(body, stale.Stamp, stale.Signature)).Status);
        f.Host.SecurityWebhookSecret = null;
        Assert.Equal(401, Assert.Throws<NotificationException>(() => ingress.Receive(body, signed.Stamp, signed.Signature)).Status);
        Assert.Single(f.Store.PendingReceipts()); // bounded auth-rejection audit, no incident fanout
        Assert.Null(f.Store.PendingReceipts()[0].Event);
    }
    [Fact]
    public void Mismatch_quarantines_hash_without_parsing_and_never_auto_replays()
    {
        var f = new Fixture(); f.Host.SecurityCompatible = false;
        var ingress = new SecurityIngress(f.Store, f.Host, f.Clock, new(f.Clock));
        var body = Encoding.UTF8.GetBytes("not even json"); var signed = Sign(f, body);
        Assert.Contains("quarantined", Contract.Serialize(ingress.Receive(body, signed.Stamp, signed.Signature)));
        f.Host.SecurityCompatible = true;
        Assert.Empty(f.Store.PendingReceipts());
    }
    [Theory]
    [InlineData("test", "security.webhook.test", false)]
    [InlineData("new_device", "security.unknown", true)]
    [InlineData("lockout", "security.unknown", true)]
    [InlineData("emergency_lockout", "security.emergency_lockout", true)]
    public void Explicit_mapping_unknown_and_test_semantics(string key, string expected, bool alert)
    {
        var f = new Fixture(); f.Host.SecurityAlertMode = "hub";
        var ingress = new SecurityIngress(f.Store, f.Host, f.Clock, new(f.Clock));
        var body = Body(key); var signed = Sign(f, body); ingress.Receive(body, signed.Stamp, signed.Signature);
        var receipt = Assert.Single(f.Store.PendingReceipts()); Assert.Equal(expected, receipt.Audit!.EventType); Assert.Equal(alert, receipt.Event is not null);
    }
    [Fact]
    public void Body_limit_and_ingress_rate_limit_are_bounded()
    {
        var f = new Fixture(); var ingress = new SecurityIngress(f.Store, f.Host, f.Clock, new(f.Clock));
        Assert.Equal(413, Assert.Throws<NotificationException>(() => ingress.Receive(new byte[65537], "", "")).Status);
        var body = Body(); var signed = Sign(f, body);
        for (var i = 0; i < 120; i++) ingress.Receive(body, signed.Stamp, signed.Signature);
        Assert.Equal(429, Assert.Throws<NotificationException>(() => ingress.Receive(body, signed.Stamp, signed.Signature)).Status);
    }
    [Fact]
    public async Task Security_notification_ack_continues_while_core_audit_is_unavailable()
    {
        var f = new Fixture(); f.Host.SecurityAlertMode = "hub"; f.Host.FailAudit = true;
        var ingress = new SecurityIngress(f.Store, f.Host, f.Clock, new(f.Clock));
        var body = Body(); var signed = Sign(f, body); ingress.Receive(body, signed.Stamp, signed.Signature);
        var worker = new DeliveryWorker(f.Store, f.Service, f.Host, f.Bot, new(f.Clock), f.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeliveryWorker>.Instance);
        await worker.ProcessReceiptAsync(Assert.Single(f.Store.PendingReceipts()), default);
        Assert.Single(f.Store.Inbox(f.Host.Bob, 0, 100, null, false));
        var retry = Assert.Single(f.Store.PendingReceipts()); Assert.Null(retry.Event); Assert.NotNull(retry.Audit);
        f.Host.FailAudit = false; await worker.ProcessReceiptAsync(retry, default);
        Assert.Empty(f.Store.PendingReceipts()); Assert.Single(f.Host.Audit);
    }
    [Theory]
    [InlineData("=CMD()")]
    [InlineData("  +CMD()")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tformula")]
    public void CSV_formula_cells_are_escaped(string value) => Assert.StartsWith("\"'", AuditCsv.Cell(value));
}
