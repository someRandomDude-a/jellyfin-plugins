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
    [Fact] public async Task InlineCommandsAreRecipientScopedAndRejectStaleSnapshotsAndUndeclaredActions()
    {
        var f = new Fixture(); var calls = 0;
        f.Service.Register(new("deletion-queue", "1", [new("proposal.updated", "Removal", "Changes", "explicit-users", new(), []) { Commands = ["allow"] }]));
        f.Host.Commands = (user, _, request) => {
            Assert.Equal(f.Host.Alice, user); if (request is not null) calls++;
            return [new("allow", "Allow", 3, "snapshot")];
        };
        await f.Publish(f.Event()); var id = f.Store.Inbox(f.Host.Alice, 0, 25, null, false).Single().Id;
        await using var app = await App(f); using var alice = Client(app, f.Host.Alice); using var bob = Client(app, f.Host.Bob);
        HttpContent Body(string action, long revision = 3) => new StringContent(Contract.Serialize(new NotificationCommandRequest(action, Guid.NewGuid(), revision, "snapshot")), Encoding.UTF8, "application/json");
        Assert.Contains("\"commands\"", await alice.GetStringAsync(Base + "/notifications"));
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync(Base + $"/notifications/{id}/actions", Body("allow"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsync(Base + $"/notifications/{id}/actions", Body("allow", 2))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsync(Base + $"/notifications/{id}/actions", Body("delete"))).StatusCode);
        Assert.Equal(0, calls);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync(Base + $"/notifications/{id}/actions", Body("allow"))).StatusCode);
        Assert.Equal(1, calls);
        f.Store.Mark(f.Host.Alice, id, true);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsync(Base + $"/notifications/{id}/actions", Body("allow"))).StatusCode);
        Assert.Empty(f.Store.Inbox(f.Host.Alice,0,25,null,false));
        Assert.Single(f.Store.Inbox(f.Host.Alice,0,25,null,false,history:true));
        using var archived=JsonDocument.Parse(await alice.GetStringAsync(Base+"/notifications?history=true"));
        var entry=archived.RootElement.GetProperty("data").GetProperty("notifications").EnumerateArray().Single();
        Assert.True(entry.GetProperty("dismissed").GetBoolean());Assert.True(entry.GetProperty("read").GetBoolean());
        Assert.Empty(entry.GetProperty("event").GetProperty("commands").EnumerateArray());
        using var other=JsonDocument.Parse(await bob.GetStringAsync(Base+"/notifications?history=true"));
        Assert.Empty(other.RootElement.GetProperty("data").GetProperty("notifications").EnumerateArray());
    }
    [Fact]
    public async Task InboxDoesNotExposeOtherRecipientsOfTheSameEvent()
    {
        var f = new Fixture(); await f.Publish(f.Event() with { Audience = [f.Host.Alice, f.Host.Bob] });
        await using var app = await App(f); using var alice = Client(app, f.Host.Alice); using var bob = Client(app, f.Host.Bob);
        var a = await alice.GetStringAsync(Base + "/notifications");
        var b = await bob.GetStringAsync(Base + "/notifications");
        Assert.DoesNotContain(f.Host.Bob.ToString(), a); Assert.DoesNotContain(f.Host.Alice.ToString(), b);
        Assert.Single(f.Store.Inbox(f.Host.Alice, 0, 25, null, false));
        Assert.Single(f.Store.Inbox(f.Host.Bob, 0, 25, null, false));
    }
    [Fact]
    public async Task ConcurrentPersonalRuleChangesPreserveOtherPreferencesAndCannotChangeAnotherUser()
    {
        var f = new Fixture(); var global = new Preference("*", "*", "discord", true); f.Store.Preferences(f.Host.Alice, [global]);
        await using var app = await App(f); using var alice = Client(app, f.Host.Alice); using var bob = Client(app, f.Host.Bob);
        HttpContent Body(Preference rule) => new StringContent(Contract.Serialize(rule), Encoding.UTF8, "application/json");
        var responses = await Task.WhenAll(
            alice.PutAsync(Base + "/notifications/preferences/rule", Body(new("account-hub", "library.episode.added", "in-app", false))),
            alice.PutAsync(Base + "/notifications/preferences/rule", Body(new("deletion-queue", "*", "in-app", false))));
        foreach (var response in responses) { Assert.Equal(HttpStatusCode.OK, response.StatusCode); response.Dispose(); }
        Assert.Equal(3, f.Store.Preferences(f.Host.Alice).Length); Assert.Contains(global, f.Store.Preferences(f.Host.Alice)); Assert.Empty(f.Store.Preferences(f.Host.Bob));
        await bob.PutAsync(Base + "/notifications/preferences/rule", Body(new("example", "*", "in-app", false)));
        Assert.Equal(3, f.Store.Preferences(f.Host.Alice).Length); Assert.Single(f.Store.Preferences(f.Host.Bob));
    }
    [Fact]
    public async Task OnlyAdministratorsCanReviewTypesAndChangeRevisionedLayouts()
    {
        var f = new Fixture();
        f.Service.Register(new("deletion-queue", "1", [new("proposal.updated", "Removal requests", "Status changes", "explicit-users", new(), [])]));
        await f.Publish(f.Event());
        await using var app = await App(f); using var alice = Client(app, f.Host.Alice); using var bob = Client(app, f.Host.Bob);
        var initial = await alice.GetAsync(Base + "/notifications"); alice.DefaultRequestHeaders.IfNoneMatch.Add(initial.Headers.ETag!);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync(Base + "/settings/notifications/types")).StatusCode);
        Assert.Contains("Removal requests", await bob.GetStringAsync(Base + "/settings/notifications/types"));
        var update = new NotificationLayoutUpdate("deletion-queue", "proposal.updated", 1, new("standard", 180));
        HttpContent Body() => new StringContent(Contract.Serialize(update), Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PutAsync(Base + "/settings/notifications/types/layout", Body())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.PutAsync(Base + "/settings/notifications/types/layout", Body())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await bob.PutAsync(Base + "/settings/notifications/types/layout", Body())).StatusCode);
        var updated = await alice.GetAsync(Base + "/notifications"); Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Contains("\"summaryLength\":180", await updated.Content.ReadAsStringAsync());
        Assert.Equal(140, f.Store.Inbox(f.Host.Alice, 0, 25, null, false).Single().Event.Layout!.SummaryLength);
    }
    [Fact]
    public async Task Administrator_details_and_actions_are_not_visible_to_ordinary_or_demoted_users()
    {
        var f = new Fixture();
        await f.Publish(f.Event(admin: true) with { Title = "Admin-only incident", Details = "Private diagnostic detail", Actions = [new("Review", "#/dashboard", "compact")] });
        await using var app = await App(f); using var alice = Client(app, f.Host.Alice); using var bob = Client(app, f.Host.Bob);
        var ordinary = await alice.GetStringAsync(Base + "/notifications"); Assert.DoesNotContain("Admin-only incident", ordinary); Assert.DoesNotContain("Private diagnostic detail", ordinary);
        var first = await bob.GetAsync(Base + "/notifications"); Assert.Contains("Private diagnostic detail", await first.Content.ReadAsStringAsync());
        bob.DefaultRequestHeaders.IfNoneMatch.Add(first.Headers.ETag!); f.Host.Admins.Remove(f.Host.Bob);
        var demoted = await bob.GetAsync(Base + "/notifications"); Assert.Equal(HttpStatusCode.OK, demoted.StatusCode);
        var text = await demoted.Content.ReadAsStringAsync(); Assert.DoesNotContain("Admin-only incident", text); Assert.DoesNotContain("Private diagnostic detail", text); Assert.DoesNotContain("#/dashboard", text);
        Assert.Contains("\"count\":0", await bob.GetStringAsync(Base + "/notifications/unread-count"));
    }
    [Theory]
    [InlineData("javascript:alert(1)", "compact")]
    [InlineData("#/home", "giant")]
    public async Task Rich_actions_reject_executable_links_and_arbitrary_sizes(string link, string size)
    {
        var f = new Fixture(); var reply = await f.Dispatch("notifications.publish", f.Event() with { Actions = [new("Review", link, size)] });
        Fixture.Ok(reply, expected: false);
        Assert.Empty(f.Store.Inbox(f.Host.Alice, 0, 25, null, false));
    }
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
