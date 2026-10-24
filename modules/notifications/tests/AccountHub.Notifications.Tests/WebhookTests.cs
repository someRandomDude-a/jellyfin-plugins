using System.Security.Cryptography;
using System.Text;
using AccountHub.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AccountHub.Notifications.Tests;

public sealed class WebhookTests
{
    [Fact]
    public async Task Real_loopback_transport_signs_exact_bytes_and_reuses_ID_and_body_with_new_timestamp()
    {
        var f = new Fixture();
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var received = new List<(string Body, string Timestamp, string Signature, string Id)>();
        app.MapPost("/hook", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            received.Add((await reader.ReadToEndAsync(), context.Request.Headers["X-AccountHub-Timestamp"].ToString(),
                context.Request.Headers["X-AccountHub-Signature"].ToString(), context.Request.Headers["Idempotency-Key"].ToString()));
            if (received.Count == 1) { context.Response.StatusCode = 429; context.Response.Headers.RetryAfter = "120"; }
            else context.Response.StatusCode = 204;
        });
        await app.StartAsync();
        var route = new Route("web", "*", "*", "webhook", app.Urls.Single() + "/hook", AllowPrivate: true, AllowHttp: true, Secret: new('s', 32));
        f.Store.SaveSettings(new(0, [route])); await f.Publish(f.Event(true));
        var delivery = f.Store.Claim()!; var sender = new WebhookSender(f.Clock);
        var first = await sender.SendAsync(delivery, default);
        Assert.True(first.Retryable); Assert.Equal(TimeSpan.FromSeconds(120), first.RetryAfter);
        f.Clock.Now = f.Clock.Now.AddMinutes(3);
        Assert.True((await sender.SendAsync(delivery, default)).Delivered);
        Assert.Equal(received[0].Body, received[1].Body); Assert.Equal(received[0].Id, received[1].Id); Assert.NotEqual(received[0].Timestamp, received[1].Timestamp);
        foreach (var request in received)
        {
            var signature = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(route.Secret!), Encoding.UTF8.GetBytes(request.Timestamp + "." + request.Body)));
            Assert.Equal(signature, request.Signature);
        }
        var denied = await sender.SendAsync(delivery with { Route = route with { AllowPrivate = false } }, default);
        Assert.False(denied.Delivered); Assert.False(denied.Retryable); Assert.Equal(2, received.Count);
    }
    [Theory]
    [InlineData(302, false)]
    [InlineData(400, false)]
    [InlineData(403, false)]
    [InlineData(500, true)]
    public async Task Redirects_are_not_followed_and_status_policy_is_enforced(int status, bool retry)
    {
        var f = new Fixture(); var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build(); var escaped = false;
        app.MapPost("/hook", (HttpContext context) => { context.Response.StatusCode = status; context.Response.Headers.Location = "/other"; return Task.CompletedTask; });
        app.MapPost("/other", () => { escaped = true; return Results.Ok(); });
        await app.StartAsync();
        var route = new Route("web", "*", "*", "webhook", app.Urls.Single() + "/hook", AllowPrivate: true, AllowHttp: true, Secret: new('s', 32));
        var delivery = new Delivery(Guid.NewGuid(), f.Host.Bob, true, f.Event(true), route, 1, "lease", "{}", null);
        var result = await new WebhookSender(f.Clock).SendAsync(delivery, default);
        Assert.False(result.Delivered); Assert.Equal(retry, result.Retryable); Assert.False(escaped);
    }
    [Fact]
    public async Task Unknown_schema_is_unavailable_without_startup_failure_or_migration_changes()
    {
        var f = new Fixture();
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + f.Options.DatabasePath))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE nh_schema SET version=2"; command.ExecuteNonQuery();
        }
        var store = new NotificationStore(f.Options, f.Protection, f.Clock); Assert.False(store.Ready);
        var service = new NotificationService(store, f.Host, f.Options);
        var request = Contract.Serialize(new { apiVersion = "1", operation = "notifications.publish", requestId = Guid.NewGuid(), payload = f.Event() });
        var reply = await service.DispatchAsync(request, default);
        Assert.Contains("unavailable", reply); Assert.Contains("\"retryable\":true", reply);
    }
}
