using System.Net;
using AccountHub.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AccountHub.Notifications.Tests;

public sealed class DeliveryTests
{
    [Fact]
    public async Task Library_access_revocation_prevents_already_queued_DM()
    {
        var f = new Fixture(); f.Host.Links[f.Host.Alice] = "111"; f.Store.SaveSettings(new(0, [Dm]));
        f.Store.Preferences(f.Host.Alice, [new("*", "*", "discord", true)]);
        await f.Publish(f.Event() with { Type = "library.episode.added" }); var delivery = f.Store.Claim()!;
        f.Host.LibraryDenied.Add(f.Host.Alice); var result = await Worker(f).DeliverAsync(delivery, default);
        Assert.False(result.Delivered); Assert.Equal("library_access_revoked", result.Error); Assert.Empty(f.Bot.Sent);
    }
    private static DeliveryWorker Worker(Fixture f) => new(f.Store, f.Service, f.Host, f.Bot, new(f.Clock), f.Clock, NullLogger<DeliveryWorker>.Instance);
    private static Route Dm => new("dm", "*", "*", "discord", "linked-user", AdminOnly: false, DirectMessage: true);
    private static Route Channel => new("channel", "*", "*", "discord", "12345");
    [Fact]
    public async Task Private_event_requires_DM_optin_and_never_goes_to_shared_route()
    {
        var f = new Fixture(); f.Host.Links[f.Host.Alice] = "111";
        f.Store.SaveSettings(new(0, [Dm, Channel]));
        await f.Publish(f.Event()); Assert.Null(f.Store.Claim());
        f.Store.Preferences(f.Host.Alice, [new("*", "*", "discord", true)]);
        await f.Publish(f.Event());
        var delivery = f.Store.Claim()!; Assert.True(delivery.Route.DirectMessage); Assert.Equal("111", delivery.Route.Destination);
        Assert.Null(f.Store.Claim()); Assert.True((await Worker(f).DeliverAsync(delivery, default)).Delivered);
    }
    [Fact]
    public async Task Unlink_or_demotion_prevents_queued_delivery()
    {
        var f = new Fixture(); f.Host.Links[f.Host.Alice] = "111";
        f.Store.SaveSettings(new(0, [Dm, Channel]));
        f.Store.Preferences(f.Host.Alice, [new("*", "*", "discord", true)]);
        await f.Publish(f.Event()); var dm = f.Store.Claim()!;
        f.Host.Links[f.Host.Alice] = "222";
        Assert.False((await Worker(f).DeliverAsync(dm, default)).Delivered); Assert.Empty(f.Bot.Sent);
        await f.Publish(f.Event(true)); var channel = f.Store.Claim()!;
        f.Host.Admins.Clear(); Assert.False((await Worker(f).DeliverAsync(channel, default)).Delivered);
    }
    [Fact]
    public async Task Expired_lease_recovers_same_ID_and_body_and_stale_worker_cannot_commit()
    {
        var f = new Fixture(); f.Store.SaveSettings(new(0, [Channel])); await f.Publish(f.Event(true));
        var first = f.Store.Claim()!; Assert.Null(f.Store.Claim());
        f.Clock.Now = f.Clock.Now.AddMinutes(3);
        var recovered = f.Store.Claim()!;
        Assert.Equal(first.Id, recovered.Id); Assert.Equal(first.Body, recovered.Body); Assert.NotEqual(first.Lease, recovered.Lease);
        f.Store.Complete(first, new(true));
        Assert.Contains("delivering", Contract.Serialize(f.Store.Deliveries(0, 10)));
        f.Store.Complete(recovered, new(true)); Assert.Contains("sent", Contract.Serialize(f.Store.Deliveries(0, 10)));
    }
    [Fact]
    public async Task Retry_budget_and_provider_hint_end_in_durable_nonrecursive_failure_summary()
    {
        var f = new Fixture(); f.Store.SaveSettings(new(0, [Channel])); await f.Publish(f.Event(true));
        var first = f.Store.Claim()!;
        f.Store.Complete(first, new(false, true, "not retained", TimeSpan.FromHours(2)));
        f.Clock.Now = f.Clock.Now.AddHours(1); Assert.Null(f.Store.Claim());
        f.Clock.Now = f.Clock.Now.AddHours(2);
        for (var i = 2; i <= 6; i++)
        {
            var next = f.Store.Claim()!; Assert.Equal(first.Id, next.Id); Assert.Equal(i, next.Attempts);
            f.Store.Complete(next, new(false, true)); f.Clock.Now = f.Clock.Now.AddDays(1);
        }
        Assert.Contains("dead_letter", Contract.Serialize(f.Store.Deliveries(0, 10)));
        var receipt = Assert.Single(f.Store.PendingReceipts()); await f.Publish(receipt.Event!);
        Assert.Null(f.Store.Claim()); // Failure alert cannot recursively create failed external deliveries.
        Assert.True(f.Store.Retry(first.Id)); Assert.False(f.Store.Retry(first.Id));
    }
    [Fact]
    public async Task Mandatory_admin_and_critical_inbox_cannot_be_muted_but_normal_can()
    {
        var f = new Fixture(); f.Store.Preferences(f.Host.Alice, [new("*", "*", "in-app", false)]);
        await f.Publish(f.Event()); Assert.Empty(f.Store.Inbox(f.Host.Alice, 0, 10, null, false));
        await f.Publish(f.Event() with { Severity = "critical" }); Assert.Single(f.Store.Inbox(f.Host.Alice, 0, 10, null, false));
    }
    [Theory]
    [InlineData("127.0.0.1", false, false)]
    [InlineData("127.0.0.1", true, true)]
    [InlineData("169.254.169.254", true, false)]
    [InlineData("10.0.0.1", false, false)]
    [InlineData("::ffff:127.0.0.1", false, false)]
    [InlineData("::1", false, false)]
    [InlineData("fd00::1", false, false)]
    [InlineData("ff02::1", true, false)]
    [InlineData("8.8.8.8", false, true)]
    [InlineData("2606:4700:4700::1111", false, true)]
    public void SSRF_address_policy(string address, bool approve, bool expected) => Assert.Equal(expected, WebhookSender.AllowedAddress(IPAddress.Parse(address), approve));
    [Fact]
    public void Webhook_URL_rejects_HTTP_credentials_and_query_secrets()
    {
        var route = new Route("web", "*", "*", "webhook", "https://example.com", Secret: new('s', 32));
        foreach (var url in new[] { "http://example.com", "https://user:pass@example.com", "https://example.com?secret=x", "https://metadata.google.internal" })
            Assert.Throws<NotificationException>(() => WebhookSender.ValidateUri(new(url), route));
    }
    [Fact]
    public async Task Destinations_and_secrets_are_not_read_back_and_bad_revisions_fail()
    {
        var f = new Fixture(); var secret = new string('s', 32);
        await f.Service.SaveSettingsAsync(new(0, [new("web", "*", "*", "webhook", "https://example.com/hook", Secret: secret)]), f.Host.Bob, default);
        var read = f.Store.Settings(); Assert.Null(read.Routes[0].Secret); Assert.Equal("[configured]", read.Routes[0].Destination);
        await Assert.ThrowsAsync<NotificationException>(() => f.Service.SaveSettingsAsync(new(0, []), f.Host.Bob, default));
        await f.Service.SaveSettingsAsync(read, f.Host.Bob, default);
        Assert.Equal(secret, f.Store.Settings(false).Routes[0].Secret);
        await f.Publish(f.Event(true));
        using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + f.Options.DatabasePath); db.Open();
        using var command = db.CreateCommand(); command.CommandText = "SELECT json FROM nh_settings UNION ALL SELECT route FROM nh_deliveries";
        using var reader = command.ExecuteReader();
        while (reader.Read()) { Assert.DoesNotContain(secret, reader.GetString(0)); Assert.DoesNotContain("example.com", reader.GetString(0)); }
    }
    [Fact]
    public async Task Retention_keeps_dedupe_after_inbox_pruned()
    {
        var f = new Fixture(); var e = f.Event(); await f.Publish(e);
        f.Clock.Now = f.Clock.Now.AddDays(91); f.Store.PrunePresentation(90);
        Assert.Empty(f.Store.Inbox(f.Host.Alice, 0, 100, null, false)); Assert.Equal("duplicate", (await f.Publish(e)).Status);
    }
    [Fact]
    public async Task Secret_capsule_is_separate_encrypted_DM_to_unlinked_recipient()
    {
        var f = new Fixture(); var path = new SecretDelivery(f.Protection, f.Bot, f.Clock);
        var capsule = path.Seal("111", new("https://example.com/reset#token-secret"), TimeSpan.FromMinutes(5));
        Assert.DoesNotContain("token-secret", capsule);
        Assert.True((await path.SendAsync(capsule, Guid.NewGuid(), default)).Delivered);
        Assert.Contains("111:", Assert.Single(f.Bot.Sent));
        f.Clock.Now = f.Clock.Now.AddMinutes(6);
        Assert.False((await path.SendAsync(capsule, Guid.NewGuid(), default)).Delivered);
        Assert.Empty(f.Store.Inbox(f.Host.Alice, 0, 10, null, false));
    }
}
