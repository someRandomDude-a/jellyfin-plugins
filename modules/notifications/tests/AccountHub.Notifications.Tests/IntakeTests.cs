using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using AccountHub.Api;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AccountHub.Notifications.Tests;

public sealed class IntakeTests
{
    [Fact]
    public async Task Library_audience_skips_deleted_or_inaccessible_users_and_respects_kind_preferences()
    {
        var f = new Fixture(); f.Host.LibraryDenied.Add(f.Host.Bob);
        var e = f.Event() with { Source = "account-hub", Type = "library.episode.added", Audience = [f.Host.Alice, f.Host.Bob, Guid.NewGuid()] };
        await f.Publish(e); Assert.Single(f.Store.Inbox(f.Host.Alice, 0, 100, null, false)); Assert.Empty(f.Store.Inbox(f.Host.Bob, 0, 100, null, false));
        f.Store.Preferences(f.Host.Alice, [new("account-hub", "library.episode.added", "in-app", false)]);
        await f.Publish(e with { EventId = Guid.NewGuid() }); Assert.Single(f.Store.Inbox(f.Host.Alice, 0, 100, null, false));
        await f.Publish(e with { EventId = Guid.NewGuid(), Type = "library.movie.added" }); Assert.Equal(2, f.Store.Inbox(f.Host.Alice, 0, 100, null, false).Count);
    }
    [Fact]
    public async Task Accepted_event_and_frozen_audience_survive_restart()
    {
        var f = new Fixture(); var e = f.Event(true);
        var accepted = await f.Publish(e);
        var restarted = new NotificationStore(f.Options, f.Protection, f.Clock);
        f.Host.Admins.Add(f.Host.Alice);
        var service = new NotificationService(restarted, f.Host, f.Options);
        var retry = await service.PublishAsync(e, Fixture.Hash(e), default);
        Assert.Equal("duplicate", retry.Status); Assert.Equal(accepted.NotificationIds, retry.NotificationIds);
        Assert.Single(restarted.Inbox(f.Host.Bob, 0, 100, null, false));
        Assert.Empty(restarted.Inbox(f.Host.Alice, 0, 100, null, false));
    }
    [Fact]
    public async Task Conflict_rejected_and_logical_key_dedupes_distinct_event_ids()
    {
        var f = new Fixture(); var e = f.Event() with { DedupeKey = "notice:one" };
        await f.Publish(e);
        var conflict = await Assert.ThrowsAsync<NotificationException>(() => f.Publish(e with { Body = "changed" }));
        Assert.Equal("event_conflict", conflict.Message);
        Assert.Equal("duplicate", (await f.Publish(e with { EventId = Guid.NewGuid() })).Status);
        Assert.Single(f.Store.Inbox(f.Host.Alice, 0, 100, null, false));
    }
    [Fact]
    public async Task Acknowledged_event_retries_do_not_revalidate_deleted_recipients()
    {
        var f = new Fixture(); var e = f.Event() with { DedupeKey = "notice:stable" }; await f.Publish(e);
        f.Host.Deleted.Add(f.Host.Alice);
        Assert.Equal("duplicate", (await f.Publish(e)).Status);
        Assert.Equal("duplicate", (await f.Publish(e with { EventId = Guid.NewGuid() })).Status);
    }
    [Fact]
    public async Task Concurrent_retries_insert_one_row()
    {
        var f = new Fixture(); var e = f.Event();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => f.Publish(e))));
        Assert.Single(results, r => r.Status == "accepted");
        Assert.Single(f.Store.Inbox(f.Host.Alice, 0, 100, null, false));
    }
    [Fact]
    public async Task Inbox_enforces_ownership_and_hides_recipient_list()
    {
        var f = new Fixture(); var result = await f.Publish(f.Event() with { Audience = [f.Host.Alice, f.Host.Bob] });
        var row = Assert.Single(f.Store.Inbox(f.Host.Alice, 0, 100, null, false));
        Assert.Empty(row.Event.Audience);
        Assert.False(f.Store.Mark(f.Host.Bob, row.Id, true));
        Assert.True(f.Store.Mark(f.Host.Alice, row.Id, true));
        Assert.Equal(0, f.Store.Unread(f.Host.Alice));
        Assert.Equal(2, result.NotificationIds.Length);
    }
    [Fact]
    public async Task Admin_demotion_hides_sensitive_old_inbox()
    {
        var f = new Fixture(); await f.Publish(f.Event(true));
        Assert.Empty(f.Store.Inbox(f.Host.Bob, 0, 100, null, false, false));
        Assert.Equal(0, f.Store.Unread(f.Host.Bob, false));
    }
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("//evil.example")]
    [InlineData("https://evil.example")]
    [InlineData("http://example.com")]
    [InlineData("web/index.html#/reset?token=x")]
    [InlineData("\\evil.example")]
    public async Task Unsafe_links_rejected(string link)
    { var f = new Fixture(); await Assert.ThrowsAsync<NotificationException>(() => f.Publish(f.Event() with { Link = link })); }
    [Fact]
    public async Task Plain_text_xss_is_not_html_and_metadata_secrets_rejected()
    {
        var f = new Fixture(); await f.Publish(f.Event() with { Title = "<img onerror=alert(1)>" });
        Assert.Contains("onerror", f.Store.Inbox(f.Host.Alice, 0, 100, null, false)[0].Event.Title);
        await Assert.ThrowsAsync<NotificationException>(() => f.Publish(f.Event() with { Metadata = new() { ["PairingCODE"] = "123456" } }));
    }
    [Fact]
    public async Task Producer_scopes_and_reserved_source_cannot_be_spoofed()
    {
        var f = new Fixture(); var token = f.Store.IssueToken("deletion-queue", ["notifications:write"]);
        var identity = f.Store.Authenticate(token)!;
        Fixture.Ok(await f.Dispatch("notifications.publish", f.Event(), identity));
        Fixture.Ok(await f.Dispatch("notifications.publish", f.Event(true), identity), false);
        Fixture.Ok(await f.Dispatch("notifications.publish", f.Event() with { Source = "jellyfin-security" }, identity), false);
        Fixture.Ok(await f.Dispatch("audit.publish", new AuditEvent(1, Guid.NewGuid(), "deletion-queue", "proposal.updated", "success", f.Clock.Now), identity), false);
        f.Store.RevokeToken(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))));
        Assert.Null(f.Store.Authenticate(token));
    }
    [Fact]
    public async Task Batch_results_are_independent_and_preserve_order()
    {
        var f = new Fixture(); var good = f.Event(); var bad = f.Event() with { Severity = "invalid" };
        var reply = await f.Dispatch("notifications.publishBatch", new { events = new[] { good, bad, good } });
        Fixture.Ok(reply); using var json = JsonDocument.Parse(reply);
        var results = json.RootElement.GetProperty("data").GetProperty("results");
        Assert.Equal(good.EventId.ToString(), results[0].GetProperty("eventId").GetString());
        Assert.True(results[0].GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.False(results[1].GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.Equal("duplicate", results[2].GetProperty("result").GetProperty("data").GetProperty("status").GetString());
        Fixture.Ok(await f.Dispatch("notifications.publishBatch", new { events = Enumerable.Repeat(good, 51) }), false);
        Fixture.Ok(await f.Dispatch("notifications.publish", good with { Body = new string('a', 66000) }), false);
    }
    [Fact]
    public async Task Audit_ack_is_independent_of_notification_and_no_metadata_secret_aliases()
    {
        var f = new Fixture(); var e = f.Event(); await f.Publish(e);
        Fixture.Ok(await f.Dispatch("audit.publish", new AuditEvent(1, e.EventId, e.Source, "proposal.updated", "success", f.Clock.Now)));
        f.Host.FailAudit = true;
        Fixture.Ok(await f.Dispatch("audit.publish", new AuditEvent(1, Guid.NewGuid(), e.Source, "proposal.updated", "success", f.Clock.Now)), false);
        Assert.Equal("duplicate", (await f.Publish(e)).Status);
        f.Host.FailAudit = false;
        Fixture.Ok(await f.Dispatch("audit.publish", new AuditEvent(1, Guid.NewGuid(), e.Source, "proposal.updated", "success", f.Clock.Now,
            Metadata: JsonSerializer.SerializeToElement(new { nested = new { AccessToken = "secret" } }))), false);
    }
    [Fact]
    public async Task Separate_ALC_client_discovers_exact_framework_delegate_and_handles_absence_and_incompatible_host()
    {
        var f = new Fixture(); var services = new ServiceCollection();
        services.AddSingleton(f.Options); services.AddSingleton(f.Protection); services.AddSingleton<TimeProvider>(f.Clock);
        services.AddSingleton<INotificationHost>(f.Host); services.AddSingleton(f.Store); services.AddSingleton(f.Service);
        services.AddKeyedSingleton<Func<string, CancellationToken, Task<string>>>(Contract.ServiceKey, (_, _) => (json, ct) => f.Service.DispatchAsync(json, ct));
        using var provider = services.BuildServiceProvider();
        var alc = new AssemblyLoadContext("producer", isCollectible: true);
        try
        {
            var assembly = alc.LoadFromAssemblyPath(typeof(AccountHubClient).Assembly.Location);
            Assert.NotSame(assembly, typeof(AccountHubClient).Assembly);
            var type = assembly.GetType("AccountHub.Api.AccountHubClient")!;
            async Task<string> Invoke(Func<Func<string, CancellationToken, Task<string>>?> resolve)
            {
                var instance = Activator.CreateInstance(type, resolve)!;
                return await (Task<string>)type.GetMethod("InvokeAsync", [typeof(string), typeof(object), typeof(CancellationToken)])!.Invoke(instance, ["hello", new { }, CancellationToken.None])!;
            }
            Fixture.Ok(await Invoke(() => provider.GetKeyedService<Func<string, CancellationToken, Task<string>>>(Contract.ServiceKey)));
            Fixture.Ok(await Invoke(() => null), false);
            Fixture.Ok(await Invoke(() => (_, _) => Task.FromResult("{\"apiVersion\":\"2\",\"ok\":true}")), false);
        }
        finally { alc.Unload(); }
    }
}
