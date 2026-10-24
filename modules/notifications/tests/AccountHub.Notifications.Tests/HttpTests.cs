using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AccountHub.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AccountHub.Notifications.Tests;

public sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Headers.Authorization.ToString();
        if (!token.StartsWith("Bearer ") || !Guid.TryParse(token[7..], out var user)) return Task.FromResult(AuthenticateResult.NoResult());
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.ToString())], "test"));
        return Task.FromResult(AuthenticateResult.Success(new(principal, "test")));
    }
}
public sealed class HttpTests
{
    [Fact]
    public async Task Library_permission_revocation_invalidates_cached_inbox_and_unread_count()
    {
        var f = new Fixture(); await f.Publish(f.Event() with { Type = "library.episode.added", Title = "Private episode" });
        await using var app = await App(f); using var client = Client(app, f.Host.Alice);
        var first = await client.GetAsync(Base + "/notifications"); Assert.Contains("Private episode", await first.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.IfNoneMatch.Add(first.Headers.ETag!); f.Host.LibraryDenied.Add(f.Host.Alice);
        var revoked = await client.GetAsync(Base + "/notifications"); Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.DoesNotContain("Private episode", await revoked.Content.ReadAsStringAsync());
        Assert.Contains("\"count\":0", await client.GetStringAsync(Base + "/notifications/unread-count"));
    }
    private const string Base = "/AccountHub/api/v1";
    private static async Task<WebApplication> App(Fixture f, bool basePath = false)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddAccountHubNotifications(f.Options);
        builder.Services.AddSingleton<INotificationHost>(f.Host); builder.Services.AddSingleton<IDiscordTransport>(f.Bot);
        builder.Services.AddSingleton(f.Store); builder.Services.AddSingleton(f.Service);
        builder.Services.RemoveAll<IHostedService>();
        var app = builder.Build();
        if (basePath) app.UsePathBase("/jellyfin");
        // Fixture of the documented middleware response; not a claim of live Security verification.
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers["X-Test-Pending"] == "true")
            { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { twoFactorRequired = true, marker = "raw-middleware-body" }); return; }
            await next(context);
        });
        app.UseAuthentication(); app.UseAuthorization(); app.MapAccountHubNotifications();
        await app.StartAsync(); return app;
    }
    private static HttpClient Client(WebApplication app, Guid? user = null)
    {
        var client = app.GetTestClient();
        if (user is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", user.ToString());
        return client;
    }
    [Fact]
    public async Task Anonymous_and_cross_user_access_and_ordinary_management_are_denied()
    {
        var f = new Fixture(); var accepted = await f.Publish(f.Event());
        await using var app = await App(f);
        using var anonymous = Client(app); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Base + "/notifications")).StatusCode);
        using var bob = Client(app, f.Host.Bob);
        var inbox = await bob.GetStringAsync(Base + "/notifications"); Assert.Contains("\"notifications\":[]", inbox);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync(Base + "/notifications/" + accepted.NotificationIds[0] + "/read", null)).StatusCode);
        using var alice = Client(app, f.Host.Alice);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync(Base + "/settings/notifications")).StatusCode);
        Assert.Contains("Proposal updated", await alice.GetStringAsync(Base + "/notifications"));
    }
    [Fact]
    public async Task Base_path_ETag_and_raw_pending_status_survive()
    {
        var f = new Fixture(); await f.Publish(f.Event()); await using var app = await App(f, true);
        using var client = Client(app, f.Host.Alice);
        var first = await client.GetAsync("/jellyfin" + Base + "/notifications"); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        client.DefaultRequestHeaders.IfNoneMatch.Add(first.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await client.GetAsync("/jellyfin" + Base + "/notifications")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Pending", "true");
        var pending = await client.GetAsync("/jellyfin" + Base + "/notifications");
        Assert.Equal(HttpStatusCode.Forbidden, pending.StatusCode); Assert.Contains("raw-middleware-body", await pending.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task Exact_producer_route_is_token_scoped_not_Jellyfin_authenticated_and_has_body_limit()
    {
        var f = new Fixture(); await using var app = await App(f); using var client = Client(app);
        var token = f.Store.IssueToken("deletion-queue", ["notifications:write"]);
        var body = Contract.Serialize(new { apiVersion = "1", operation = "notifications.publish", requestId = Guid.NewGuid(), payload = f.Event() });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(Base + "/producer/notifications", new StringContent(body))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(Base + "/producer/notifications", new StringContent(body))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Base + "/settings/notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync(Base + "/producer/notifications", new StringContent(new string('a', 262145)))).StatusCode);
    }
    [Fact]
    public async Task Stepup_denial_cross_origin_and_token_one_time_no_store()
    {
        var f = new Fixture(); await using var app = await App(f); using var client = Client(app, f.Host.Bob);
        f.Host.DenyStepUp = true;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(Base + "/settings/notifications/test", null)).StatusCode);
        f.Host.DenyStepUp = false;
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(Base + "/settings/notifications/test", null)).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        var issued = await client.PostAsync(Base + "/settings/notifications/producer-tokens", new StringContent(Contract.Serialize(new { source = "deletion-queue", scopes = new[] { "notifications:write" } }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, issued.StatusCode); Assert.True(issued.Headers.CacheControl!.NoStore);
        var json = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());
        var id = json.RootElement.GetProperty("data").GetProperty("credentialId").GetString();
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync(Base + "/settings/notifications/producer-tokens/" + id)).StatusCode);
    }
}
