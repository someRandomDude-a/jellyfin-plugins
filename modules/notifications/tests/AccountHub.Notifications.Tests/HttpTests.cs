using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
    [Theory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public async Task DiscordControlsRequireAnAvailableProviderAndPersonalLink(bool available,bool linked)
    {
        var f=new Fixture();f.Host.DiscordAvailable=available;if(linked)f.Host.Links[f.Host.Alice]="private-discord-id";
        await using var app=await App(f);using var client=Client(app,f.Host.Alice);
        var json=await client.GetStringAsync(Base+"/notifications/preferences");using var data=JsonDocument.Parse(json);
        Assert.Equal(available&&linked,data.RootElement.GetProperty("data").GetProperty("discordAvailable").GetBoolean());Assert.DoesNotContain("private-discord-id",json);
        Assert.Empty(f.Host.Audit);
    }
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
        Assert.Equal(4, f.Store.Preferences(f.Host.Alice).Length); Assert.Contains(global, f.Store.Preferences(f.Host.Alice)); Assert.Empty(f.Store.Preferences(f.Host.Bob));
        Assert.False(Assert.Single(f.Store.Preferences(f.Host.Alice),p=>p.Source=="notifications"&&p.Type=="library.episode.added").Enabled);
        await bob.PutAsync(Base + "/notifications/preferences/rule", Body(new("example", "*", "in-app", false)));
        Assert.Equal(4, f.Store.Preferences(f.Host.Alice).Length); Assert.Single(f.Store.Preferences(f.Host.Bob));
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
    [Fact] public async Task NotificationDefaultsRequireAdministratorProofAndRevisionAndPreviewsSendNothing()
    {
        var f=new Fixture();f.Service.Register(new("deletion-queue","1",[new("proposal.updated","Proposal","A change","explicit-users",new(),[]){Example=new("Simulated","Never sent")} ]));
        await using var app=await App(f);using var anonymous=Client(app);using var alice=Client(app,f.Host.Alice);using var bob=Client(app,f.Host.Bob);
        var update=new NotificationDefaultsUpdate("deletion-queue","proposal.updated",1,new(Importance:"low"));
        Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.PutAsJsonAsync(Base+"/settings/notifications/types/defaults",update)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await alice.PutAsJsonAsync(Base+"/settings/notifications/types/defaults",update)).StatusCode);
        f.Host.DenyStepUp=true;Assert.Equal(HttpStatusCode.Forbidden,(await bob.PutAsJsonAsync(Base+"/settings/notifications/types/defaults",update)).StatusCode);f.Host.DenyStepUp=false;
        Assert.Equal(HttpStatusCode.OK,(await bob.PutAsJsonAsync(Base+"/settings/notifications/types/defaults",update)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await bob.PutAsJsonAsync(Base+"/settings/notifications/types/defaults",update)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await bob.PutAsJsonAsync(Base+"/settings/notifications/types/defaults",update with{Revision=2,Defaults=new(Importance:"administrator")})).StatusCode);
        Assert.Contains("Simulated",await bob.GetStringAsync(Base+"/settings/notifications/types"));
        Assert.Contains("\"importance\":\"low\"",await alice.GetStringAsync(Base+"/notifications/categories"));
        Assert.Empty(f.Store.Inbox(f.Host.Alice,0,25,null,false));Assert.Null(f.Store.Claim());Assert.Empty(f.Bot.Sent);
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
    [Fact]public async Task BlockedTabIsRecipientScopedReadOnlyAndPersonalImportanceDoesNotRequireAdministrator(){
        var f=new Fixture();f.Store.Preferences(f.Host.Alice,[new("*","*","in-app",false)]);await f.Publish(f.Event());await using var app=await App(f);using var alice=Client(app,f.Host.Alice);using var bob=Client(app,f.Host.Bob);
        var blocked=await alice.GetStringAsync(Base+"/notifications?blocked=true");Assert.Contains("Proposal updated",blocked);using var parsed=JsonDocument.Parse(blocked);var id=parsed.RootElement.GetProperty("data").GetProperty("notifications")[0].GetProperty("id").GetGuid();
        Assert.DoesNotContain("Proposal updated",await bob.GetStringAsync(Base+"/notifications?blocked=true"));Assert.Equal(HttpStatusCode.NotFound,(await alice.PostAsJsonAsync(Base+"/notifications/"+id+"/actions",new{id="allow",commandId=Guid.NewGuid(),revision=0,snapshot=""})).StatusCode);
        (await alice.PutAsJsonAsync(Base+"/notifications/preferences/rule",new Preference("deletion-queue","*","in-app",true){Importance="high"})).EnsureSuccessStatusCode();
        (await bob.PutAsJsonAsync(Base+"/notifications/preferences/rule",new Preference("deletion-queue","*","in-app",true){Importance="silent"})).EnsureSuccessStatusCode();
        (await alice.PutAsJsonAsync(Base+"/notifications/preferences/rule",new Preference("*","*","in-app",true))).EnsureSuccessStatusCode();Assert.Contains("\"retroactive\":true",await alice.GetStringAsync(Base+"/notifications"));
    }
    [Fact]
    public async Task PersonalCategoriesExcludeAdministratorDefinitionsAndServerLayouts()
    {
        var f = new Fixture();
        f.Service.Register(new("deletion-queue", "1", [
            new("proposal.updated", "Your removal requests", "A request changes", "explicit-users", new() { MessageTemplate = "Private server template" }, []),
            new("approval.required", "Administrator approvals", "Private administrative workflow", "administrators", new(), [])
        ]));
        await using var app = await App(f); using var alice = Client(app, f.Host.Alice); using var bob = Client(app, f.Host.Bob); using var anonymous = Client(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Base + "/notifications/categories")).StatusCode);
        var personal = await alice.GetStringAsync(Base + "/notifications/categories");
        Assert.Contains("Your removal requests", personal); Assert.DoesNotContain("Administrator approvals", personal);
        Assert.DoesNotContain("Private server template", personal); Assert.DoesNotContain("messageTemplate", personal);
        Assert.Contains("Administrator approvals", await bob.GetStringAsync(Base + "/notifications/categories"));
        f.Host.Admins.Remove(f.Host.Bob);
        Assert.DoesNotContain("Administrator approvals", await bob.GetStringAsync(Base + "/notifications/categories"));
    }
    [Fact]
    public async Task AnyUserCanSaveOwnImportanceInSingleOrBatchRulesWithoutGainingAdministration()
    {
        var f = new Fixture(); await using var app = await App(f); using var alice = Client(app, f.Host.Alice); using var anonymous = Client(app);
        var rule = new Preference("account-hub", "category.library.watchlisted", "in-app", true) { Importance = "high" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(Base + "/notifications/preferences/rule", rule)).StatusCode);
        (await alice.PutAsJsonAsync(Base + "/notifications/preferences", new[] { rule })).EnsureSuccessStatusCode();
        Assert.Equal("high", Assert.Single(f.Store.Preferences(f.Host.Alice),p=>p.Source=="notifications").Importance); Assert.Empty(f.Store.Preferences(f.Host.Bob));
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync(Base + "/settings/notifications/types")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PutAsJsonAsync(Base + "/notifications/preferences/rule", rule with { Importance = "urgent-admin" })).StatusCode);
        Assert.Equal("high", Assert.Single(f.Store.Preferences(f.Host.Alice),p=>p.Source=="notifications").Importance);
    }
    [Fact]public async Task WebPushAdministrationRequiresNativeAdministratorAndWorkerContainsNoCredentials(){
        var f=new Fixture();await using var app=await App(f);using var anonymous=Client(app);using var alice=Client(app,f.Host.Alice);using var bob=Client(app,f.Host.Bob);
        Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.GetAsync(Base+"/notifications/push")).StatusCode);
        var worker=await anonymous.GetStringAsync(Base+"/notifications/push/sw.js");Assert.Contains("showNotification",worker);Assert.DoesNotContain("accessToken",worker);
        Assert.Equal(HttpStatusCode.Forbidden,(await alice.PutAsJsonAsync(Base+"/settings/notifications/push",new{revision=0,enabled=true})).StatusCode);
        f.Host.Admins.Add(f.Host.Bob);f.Host.DenyStepUp=true;
        Assert.Equal(HttpStatusCode.Forbidden,(await bob.PutAsJsonAsync(Base+"/settings/notifications/push",new{revision=0,enabled=true})).StatusCode);
        f.Host.DenyStepUp=false;(await bob.PutAsJsonAsync(Base+"/settings/notifications/push",new{revision=0,enabled=true})).EnsureSuccessStatusCode();
        Assert.Equal("https://localhost/",f.Store.PushSettings().Subject);
        (await bob.PutAsJsonAsync(Base+"/settings/notifications/push",new{revision=1,enabled=true,defaultOn=true})).EnsureSuccessStatusCode();
        Assert.Contains("\"defaultOn\":true",await alice.GetStringAsync(Base+"/notifications/push"));
        var settings=await bob.GetStringAsync(Base+"/settings/notifications/push");Assert.DoesNotContain("privateKey",settings);Assert.Contains("\"enabled\":true",settings);
    }
    [Theory]
    [InlineData("expired")]
    [InlineData("origin")]
    [InlineData("revoked")]
    public async Task LiveSocketRejectsExpiredCrossOriginAndRevokedGrants(string reason){
        var f=new Fixture();await using var app=await App(f);using var anonymous=Client(app);
        Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.PostAsJsonAsync(Base+"/notifications/live-ticket",new{})).StatusCode);
        using var client=Client(app,f.Host.Alice);using var reply=await client.PostAsJsonAsync(Base+"/notifications/live-ticket",new{});reply.EnsureSuccessStatusCode();
        using var grant=JsonDocument.Parse(await reply.Content.ReadAsStringAsync());var ticket=grant.RootElement.GetProperty("data").GetProperty("ticket").GetString()!;
        if(reason=="expired")f.Clock.Now=f.Clock.Now.AddSeconds(31);
        if(reason=="revoked")f.Host.Deleted.Add(f.Host.Alice);
        var ws=app.GetTestServer().CreateWebSocketClient();ws.SubProtocols.Add("notifications.v1");ws.SubProtocols.Add("ticket."+ticket);ws.ConfigureRequest=request=>request.Headers["Origin"]=reason=="origin"?"https://untrusted.invalid":"http://localhost";
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<Exception>(()=>ws.ConnectAsync(new Uri("http://localhost"+Base+"/notifications/live"),deadline.Token));
    }
    [Fact]public async Task LiveSocketUsesSingleUseNativeSessionGrantAndSendsNoNotificationContent(){
        var f=new Fixture();await using var app=await App(f);using var client=Client(app,f.Host.Alice);
        using var reply=await client.PostAsJsonAsync(Base+"/notifications/live-ticket",new{});reply.EnsureSuccessStatusCode();
        using var grant=JsonDocument.Parse(await reply.Content.ReadAsStringAsync());var ticket=grant.RootElement.GetProperty("data").GetProperty("ticket").GetString()!;
        var ws=app.GetTestServer().CreateWebSocketClient();ws.SubProtocols.Add("notifications.v1");ws.SubProtocols.Add("ticket."+ticket);ws.ConfigureRequest=request=>request.Headers["Origin"]="http://localhost";
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));using var socket=await ws.ConnectAsync(new Uri("http://localhost"+Base+"/notifications/live"),deadline.Token);
        var buffer=new byte[256];var first=await socket.ReceiveAsync(buffer.AsMemory(),deadline.Token);Assert.Equal("changed",Encoding.UTF8.GetString(buffer,0,first.Count));
        var before=f.Store.LiveDigest(f.Host.Alice);await f.Publish(f.Event() with{Audience=[f.Host.Bob],Title="Other user's secret"});Assert.Equal(before,f.Store.LiveDigest(f.Host.Alice));
        await f.Publish(f.Event() with{Title="Private title never sent on socket"});var second=await socket.ReceiveAsync(buffer.AsMemory(),deadline.Token);Assert.Equal("changed",Encoding.UTF8.GetString(buffer,0,second.Count));
        var replay=app.GetTestServer().CreateWebSocketClient();replay.SubProtocols.Add("notifications.v1");replay.SubProtocols.Add("ticket."+ticket);replay.ConfigureRequest=request=>request.Headers["Origin"]="http://localhost";
        await Assert.ThrowsAnyAsync<Exception>(()=>replay.ConnectAsync(new Uri("http://localhost"+Base+"/notifications/live"),deadline.Token));
        await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,"Done",deadline.Token);
    }
    private static async Task<WebApplication> App(Fixture f, bool basePath = false)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddAccountHubNotifications(f.Options);
        builder.Services.AddSingleton<INotificationHost>(f.Host); builder.Services.AddSingleton<IDiscordTransport>(f.Bot);
        builder.Services.AddSingleton(f.Store); builder.Services.AddSingleton(f.Service);
        builder.Services.AddSingleton<TimeProvider>(f.Clock);
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
        app.UseWebSockets();app.UseAuthentication(); app.UseAuthorization(); app.MapAccountHubNotifications();
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
