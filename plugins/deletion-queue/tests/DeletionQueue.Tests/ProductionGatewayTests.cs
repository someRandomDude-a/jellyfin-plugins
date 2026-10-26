using System.Net;
using System.Reflection;
using System.Text.Json;
using AccountHub.Api;
using DeletionQueue.Core;
using DeletionQueue.Infrastructure;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.DeletionQueue;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace DeletionQueue.Tests;

public sealed class ProductionGatewayTests : IDisposable
{
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Call = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args ?? []);
    }
    private static T Fake<T>(Func<MethodInfo, object?[], object?> call) where T : class
    { var fake = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)fake).Call = call; return fake; }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = QueueRulesTests.Now; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct) => Task.FromResult(send(message)); }
    private sealed class Factory(Func<HttpRequestMessage, HttpResponseMessage> send) : ISeerrClientFactory
    { public SeerrClient Create(SeerrSettings settings) => new(new Uri(settings.BaseUrl), settings.ApiKey, new Handler(send)); }

    private readonly string directory = Path.Combine(Path.GetTempPath(), "dq-production-gateway-" + Guid.NewGuid().ToString("N"));
    private readonly Clock clock = new();
    private readonly User owner = new("owner", "auth", "reset") { Id = Guid.NewGuid() };
    private readonly User restricted = new("restricted", "auth", "reset") { Id = Guid.NewGuid() };
    private readonly User requester = new("requester", "auth", "reset") { Id = Guid.NewGuid() };
    private bool attributeToRequester;
    private Guid lastRuleActor;
    private readonly Movie movie = new() { Id = Guid.NewGuid(), Name = "Fixture movie", ProviderIds = new() { ["Tmdb"] = "1" } };
    private readonly QueueRuntime runtime;
    private readonly TargetResolver targets;
    private readonly LiveIdentity identity;
    private readonly SeerrExecutionGateway gateway;
    private readonly ActivityFence fence = new();
    private DateTime? historical;
    private bool timeout;
    private bool deleted;
    private bool administrator;
    private int deletes;
    private bool unfulfilled;
    private bool pendingOnly;
    private bool otherRequestOwner;
    private int declines;
    private bool nativeGone;
    private bool wrongSeerrServer;
    private readonly ServiceProvider rulesProvider;
    private int ruleChecks;
    private int denyRuleAt = int.MaxValue;

    public ProductionGatewayTests()
    {
        var paths = Fake<IApplicationPaths>((m, _) => m.Name == "get_DataPath" ? directory : throw new NotSupportedException(m.Name));
        runtime = new QueueRuntime(paths, NullLogger<QueueRuntime>.Instance, clock);
        Assert.False(runtime.CollectorHealthy); // A persisted healthy heartbeat cannot bypass restart warm-up.
        runtime.MarkCollectorStarted();
        var users = Fake<IUserManager>((m, args) => m.Name switch
        {
            "GetUsers" => new[] { owner, restricted,requester },
            "GetUserById" => (Guid)args[0]! == owner.Id ? owner : (Guid)args[0]! == requester.Id?requester:restricted,
            "GetUserDto" => new UserDto { Id = ((User)args[0]!).Id, Policy = new UserPolicy { IsDisabled = ((User)args[0]!).Id == restricted.Id,
                IsAdministrator = ((User)args[0]!).Id == owner.Id && administrator } },
            _ => throw new NotSupportedException(m.Name)
        });
        var library = Fake<ILibraryManager>((m, _) => m.Name switch
        { "GetItemById" => nativeGone ? null : movie, "GetItemList" => new BaseItem[] { movie }, _ => throw new NotSupportedException(m.Name) });
        var data = Fake<IUserDataManager>((m, args) =>
        {
            if (m.Name != "GetUserData") throw new NotSupportedException(m.Name);
            var value = Activator.CreateInstance(m.ReturnType)!;
            if (((User)args[0]!).Id == restricted.Id) m.ReturnType.GetProperty("LastPlayedDate")!.SetValue(value, historical);
            return value;
        });
        var sessions = Fake<ISessionManager>((m, _) => m.Name == "get_Sessions" ? Array.Empty<SessionInfo>() : throw new NotSupportedException(m.Name));
        var host = Fake<IServerApplicationHost>((m, _) => m.Name == "get_SystemId" ? "fixture-server" : throw new NotSupportedException(m.Name));
        var clients = new Factory(Send);
        targets = new TargetResolver(library, users, runtime, host, clients);
        identity = new LiveIdentity(users);
        var inventory = new ActivityInventory(library, users, data, sessions, runtime, targets, clock);
        var ruleServices = new ServiceCollection();
        ruleServices.AddKeyedSingleton<Func<string, CancellationToken, Task<string>>>(AutomaticRuleValidationClient.ServiceKey,
            (_, _) => (json, _) => {using var body=JsonDocument.Parse(json);lastRuleActor=body.RootElement.GetProperty("actorId").GetGuid();return Task.FromResult(Contract.Serialize(new { matches = ++ruleChecks < denyRuleAt }));});
        rulesProvider = ruleServices.BuildServiceProvider();
        gateway = new SeerrExecutionGateway(runtime, targets, identity, inventory, fence, clock, clients, new(rulesProvider));
        runtime.Write(Guid.NewGuid(), "fixture", "Configure", state =>
        {
            state.Seerr = new() { BaseUrl = "https://seerr.invalid", ApiKey = "synthetic-key", JellyfinServerId = "fixture-server" };
            state.Policy.AutomaticDeletion = true; state.Policy.SeerrManagedDeletionAcknowledged = true;
            state.CoverageHealthy = true; state.CoverageSince = clock.Now.AddDays(-30); state.LastHeartbeat = clock.Now;
            return true;
        });
    }
    private HttpResponseMessage Send(HttpRequestMessage request)
    {
        if (request.Method == HttpMethod.Post) {
            Assert.Equal("/api/v1/request/9/decline", request.RequestUri!.AbsolutePath); declines++;
            return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
        if (request.Method == HttpMethod.Delete)
        {
            Assert.Equal("/api/v1/media/42/file?is4k=false", request.RequestUri!.PathAndQuery);
            var claimed = Assert.Single(runtime.Read().Proposals);
            Assert.Equal(ProposalStatus.Claimed, claimed.Status); Assert.NotNull(claimed.Claim);
            Assert.Contains(runtime.History(), e => e.Kind == "ExecutionClaimed");
            deletes++; deleted = true;
            if (timeout) throw new TaskCanceledException("Uncertain synthetic response");
            return new(HttpStatusCode.NoContent);
        }
        Assert.Equal(HttpMethod.Get, request.Method);
        var json = request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/settings/jellyfin" => wrongSeerrServer ? """{"serverId":"different-server"}""" : """{"serverId":"fixture-server"}""",
            "/api/v1/status" => """{"version":"3.0.0"}""",
            "/api/v1/user/1" => $$$"""{"id":1,"jellyfinUserId":"{{{(attributeToRequester?requester.Id:owner.Id)}}}"}""",
            "/api/v1/user/2" => $$$"""{"id":2,"jellyfinUserId":"{{{restricted.Id}}}"}""",
            "/api/v1/request/9" => JsonSerializer.Serialize(new { id = 9, status = declines > 0 ? 3 : pendingOnly ? 1 : 2, is4k = false, requestedBy = new { id = otherRequestOwner ? 2 : 1 }, media = new { id = 42, tmdbId = 1, mediaType = "movie" } }),
            "/api/v1/movie/1" when unfulfilled => JsonSerializer.Serialize(new { id = 1, title = "Pending movie", mediaInfo = new { id = 42, tmdbId = 1, mediaType = "movie", status = 3,
                serviceId = pendingOnly ? (int?)null : 0, externalServiceId = pendingOnly ? (int?)null : 123,
                requests = new[] { new { id = 9, is4k = false, status = pendingOnly ? 1 : 2, requestedBy = new { id = otherRequestOwner ? 2 : 1 } } } } }),
            "/api/v1/movie/1" => $$$"""{"id":1,"mediaInfo":{"id":42,"tmdbId":1,"mediaType":"movie","serviceId":0,"externalServiceId":123,"jellyfinMediaId":"{{{movie.Id}}}","requests":[{"id":9,"is4k":false,"status":2,"requestedBy":{"id":1}}]}}""",
            "/api/v1/media/42" => $$$"""{"id":42,"tmdbId":1,"status":{{{(deleted ? 7 : 5)}}}}""",
            _ => throw new NotSupportedException(request.RequestUri.AbsolutePath)
        };
        return new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    }
    [Fact] public async Task AutomaticRequestsAttributeVerifiedRequesterWithoutGrantingConsentOrLosingAuthorizerRechecks() {
        administrator=true;attributeToRequester=true;var ready=new HubAvailability();ready.Set(true);
        var service=new AutomaticRequests(runtime,targets,identity,ready,clock);
        await service.SubmitAsync(Contract.Serialize(new AutomaticRemovalSubmission(Guid.NewGuid(),owner.Id,movie.Id,"owned-rule")),default);
        var p=Assert.Single(runtime.Read().Proposals);Assert.Equal(requester.Id,p.OwnerId);Assert.Equal(requester.Id,p.RequesterId);Assert.Equal(owner.Id,p.AutomaticAuthorizerId);
        Assert.Empty(p.Consents);Assert.Null(p.AdministratorApproval);
        var check=await gateway.PreflightAsync(p,default);Assert.True(check.Evidence.OwnerAuthorized);Assert.Equal(owner.Id,lastRuleActor);
        attributeToRequester=false;check=await gateway.PreflightAsync(p,default);Assert.False(check.Evidence.OwnerAuthorized);Assert.Equal(0,deletes);
        attributeToRequester=true;administrator=false;check=await gateway.PreflightAsync(p,default);Assert.False(check.Evidence.OwnerAuthorized);
    }

    [Fact] public void EpisodeContextRequiresAccessToTheEpisodeAndItsParentSeries() {
        var series=new MediaBrowser.Controller.Entities.TV.Series{Id=Guid.NewGuid()};
        var episode=new MediaBrowser.Controller.Entities.TV.Episode{Id=Guid.NewGuid(),SeriesId=series.Id};
        var episodeVisible=true;var seriesVisible=true;
        var library=Fake<ILibraryManager>((m,a)=>m.Name=="GetItemById"?(Guid)a[0]! == episode.Id?(episodeVisible?episode:null):(Guid)a[0]! == series.Id?(seriesVisible?series:null):null:throw new NotSupportedException());
        var users=Fake<IUserManager>((m,_)=>m.Name=="GetUserById"?owner:throw new NotSupportedException());
        var resolver=new TargetResolver(library,users,null!,null!,null!);
        Assert.Equal(series.Id,resolver.ContextItem(owner.Id,episode.Id));
        seriesVisible=false;Assert.Throws<QueueException>(()=>resolver.ContextItem(owner.Id,episode.Id));
        seriesVisible=true;episodeVisible=false;Assert.Throws<QueueException>(()=>resolver.ContextItem(owner.Id,episode.Id));
    }
    [Fact] public async Task ContextMenuProjectionIsItemScopedAndPermissionsResetRequiresLiveAdministratorAndRevision()
    {
        var id = await Seed();
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
            new("Jellyfin-IsApiKey", "false"), new("Jellyfin-UserId", owner.Id.ToString())
        ], "fixture"));
        var controller = new Jellyfin.Plugin.DeletionQueue.Api.QueueController(runtime, identity, targets, null!, null!, clock, null!) {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = principal }
            }
        };
        var result = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.ItemRemoval(movie.Id));
        var json = JsonSerializer.SerializeToElement(result.Value, HubWire.Json);
        Assert.Equal(id, Assert.Single(json.GetProperty("proposals").EnumerateArray()).GetProperty("id").GetGuid());
        var unrelated = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.ItemRemoval(Guid.NewGuid()));
        Assert.Empty(JsonSerializer.SerializeToElement(unrelated.Value, HubWire.Json).GetProperty("proposals").EnumerateArray());
        var revision = runtime.Read().Policy.Revision;
        Assert.Throws<QueueException>(() => controller.ResetAllUserPermissions(new(Guid.NewGuid(), revision)));
        administrator = true;
        runtime.Write(Guid.NewGuid(), "fixture", "Override", s => { s.Policy.Accounts[owner.Id] = ProposalAuthority.AnyAccessibleItem; return true; });
        Assert.Throws<QueueException>(() => controller.ResetAllUserPermissions(new(Guid.NewGuid(), revision + 1)));
        Assert.Single(runtime.Read().Policy.Accounts);
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(controller.ResetAllUserPermissions(new(Guid.NewGuid(), revision)));
        Assert.Empty(runtime.Read().Policy.Accounts);
        Assert.Contains(runtime.History(), e => e.Kind == "AllUserPermissionsReset");
        nativeGone = true;
        await Assert.ThrowsAsync<QueueException>(() => controller.ItemRemoval(movie.Id));
    }

    private async Task<Guid> Seed()
    {
        var target = await targets.ResolveAsync(new Account(owner.Id, true), new TargetSelection(movie.Id, null, false), default);
        return runtime.Write(Guid.NewGuid(), "fixture", "Propose", state =>
        {
            var p = QueueRules.Propose(state, new Account(owner.Id, true), target, true, QueueRules.Snapshot(state, target, owner.Id), clock.Now);
            QueueRules.Evaluate(state, p, QueueRulesTests.Good, clock.Now); return p.Id;
        });
    }
    private void Due()
    {
        clock.Now = clock.Now.AddHours(48);
        runtime.Write(Guid.NewGuid(), "fixture", "Heartbeat", s => { s.LastHeartbeat = clock.Now; return true; });
    }
    [Fact] public async Task RealGatewayPersistsClaimAndSeparateOutcomeBeforeCompletion()
    {
        var id = await Seed(); Due();
        await new ExecutionCoordinator(runtime, gateway, clock).ExecuteAsync(id, default);
        Assert.Equal(1, deletes); Assert.Equal(ProposalStatus.Completed, QueueRules.Find(runtime.Read(), id).Status);
        Assert.NotNull(QueueRules.Find(runtime.Read(), id).FilesDeletedAt);
        nativeGone = true;
        var completed = QueueRules.Find(runtime.Read(), id);
        Assert.True(targets.Accessible(owner.Id, completed)); Assert.False(targets.Accessible(restricted.Id, completed));
    }
    [Fact] public async Task FreshNativeInventoryCanVerifyRestartWarmupButNeverAnUnhealthyCollector()
    {
        var id = await Seed();
        runtime.Write(Guid.NewGuid(), "fixture", "Restart", s => {
            QueueRules.BreakCoverage(s, "RestartWarmup", clock.Now); s.CoverageHealthy = true; s.LastHeartbeat = clock.Now; return true;
        });
        var p = QueueRules.Find(runtime.Read(), id);
        var check = await gateway.PreflightAsync(p, default);
        Assert.True(check.Evidence.NativeHistoryVerified);
        runtime.Write(Guid.NewGuid(), "fixture", "Evaluate", s => { QueueRules.Evaluate(s, QueueRules.Find(s, id), check.Evidence, clock.Now); return true; });
        Assert.Equal(ProposalStatus.Waiting, QueueRules.Find(runtime.Read(), id).Status);
        runtime.MarkCollectorFailed();
        Assert.False((await gateway.PreflightAsync(QueueRules.Find(runtime.Read(), id), default)).Evidence.NativeHistoryVerified);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task UnfulfilledOwnRequestsUseVerifiedOwnershipWithoutNeedingANativeItem(bool pending)
    {
        unfulfilled = true; pendingOnly = pending;
        var target = await targets.ResolveAsync(new Account(owner.Id, true), new(null, 9, false), default);
        Assert.Equal(Guid.Empty, target.JellyfinItemId); Assert.Equal(9, target.RequestId); Assert.Equal(pending, target.RequestOnly);
        Assert.True(targets.Accessible(owner.Id, target)); Assert.False(targets.Accessible(restricted.Id, target));
        var id = runtime.Write(Guid.NewGuid(), "fixture", "Propose", state => {
            var p = QueueRules.Propose(state, new(owner.Id, true), target, true, QueueRules.Snapshot(state, target, owner.Id), clock.Now);
            QueueRules.Evaluate(state, p, QueueRulesTests.Good, clock.Now); return p.Id;
        });
        Due(); await new ExecutionCoordinator(runtime, gateway, clock).ExecuteAsync(id, default);
        Assert.Equal(pending ? 1 : 0, declines); Assert.Equal(pending ? 0 : 1, deletes);
        Assert.Equal(ProposalStatus.Completed, QueueRules.Find(runtime.Read(), id).Status);
    }
    [Fact] public async Task AnotherUsersUnfulfilledRequestIsNeverAuthorizedByTheApiKey()
    {
        unfulfilled = true; pendingOnly = true; otherRequestOwner = true;
        await Assert.ThrowsAsync<QueueException>(() => targets.ResolveAsync(new Account(owner.Id, true), new(null, 9, false), default));
        Assert.Equal(0, declines); Assert.Equal(0, deletes);
    }
    [Fact] public async Task NotificationControlsProjectCallerConsentAndUseRevisionCheckedCommands()
    {
        var id = await Seed(); var adapter = new NotificationCommands(runtime, targets, identity, clock);
        var entry = new AccountHub.Api.NotificationEvent(1, Guid.NewGuid(), "deletion-queue", "proposal.updated", "info", "Movie", "Updated", [owner.Id], false, clock.Now, Metadata: new() { ["proposalId"] = id.ToString() });
        var json = AccountHub.Api.Contract.Serialize(entry);
        var commands = JsonSerializer.Deserialize<AccountHub.Api.NotificationCommand[]>(await adapter.DispatchAsync(owner.Id, json, null, default), AccountHub.Api.Contract.Json)!;
        Assert.Equal(3, commands.Length); Assert.True(commands.Single(command => command.Id == "allow").Selected);
        var keep = commands.Single(command => command.Id == "keep");
        var request = AccountHub.Api.Contract.Serialize(new AccountHub.Api.NotificationCommandRequest("keep", Guid.NewGuid(), keep.Revision, keep.Snapshot));
        var changed = await adapter.DispatchAsync(owner.Id, json, request, default);
        Assert.True(JsonSerializer.Deserialize<AccountHub.Api.NotificationCommand[]>(changed, AccountHub.Api.Contract.Json)!.Single(command => command.Id == "keep").Selected);
        await Assert.ThrowsAsync<QueueException>(() => adapter.DispatchAsync(owner.Id, json, AccountHub.Api.Contract.Serialize(new AccountHub.Api.NotificationCommandRequest("allow", Guid.NewGuid(), keep.Revision, keep.Snapshot)), default));
        await Assert.ThrowsAsync<QueueException>(() => adapter.DispatchAsync(restricted.Id, json, null, default));
    }
    [Fact] public async Task PlaybackProgressKeepsConsentProtectionWithoutSpammingReadNotices()
    {
        var id = await Seed(); var notice = Assert.Single(runtime.Read().Notices);
        runtime.Write(Guid.NewGuid(), "fixture", "NoticeMarkedRead", state => { NoticeRules.MarkRead(state, notice.Id, owner.Id, clock.Now); return true; });
        runtime.Write(Guid.NewGuid(), "activity", "PlaybackProgressCaptured", state => {
            QueueRules.RecordActivity(state, owner.Id, QueueRules.Find(state, id).Target.ActivityKey, "play", false, clock.Now); return true;
        });
        var current = runtime.Read(); Assert.Single(current.Notices); Assert.Equal(ProposalStatus.Blocked, QueueRules.Find(current, id).Status);
        Assert.Null(QueueRules.Project(current, QueueRules.Find(current, id), owner.Id, clock.Now).OwnAnswer);
    }
    [Fact] public async Task RealGatewayDoesNotResendAfterTimeoutAndReconcilesOnlyByReading()
    {
        var id = await Seed(); Due(); timeout = true;
        var coordinator = new ExecutionCoordinator(runtime, gateway, clock);
        await coordinator.ExecuteAsync(id, default);
        Assert.Equal(ProposalStatus.Uncertain, QueueRules.Find(runtime.Read(), id).Status);
        await Assert.ThrowsAsync<QueueException>(() => coordinator.ExecuteAsync(id, default));
        await coordinator.ReconcileAsync(id, default);
        Assert.Equal(1, deletes); Assert.Equal(ProposalStatus.FilesDeleted, QueueRules.Find(runtime.Read(), id).Status);
    }
    [Fact] public async Task RestrictedViewerHistoricalActivityBlocksRealGatewayWithoutRecordingConsent()
    {
        var id = await Seed(); Due(); historical = clock.Now.UtcDateTime.AddHours(-1);
        await Assert.ThrowsAsync<QueueException>(() => new ExecutionCoordinator(runtime, gateway, clock).ExecuteAsync(id, default));
        var state = runtime.Read(); Assert.Contains(state.Activity, a => a.UserId == restricted.Id);
        Assert.False(QueueRules.Find(state, id).Consents.ContainsKey(restricted.Id)); Assert.Equal(0, deletes);
    }
    [Fact] public async Task PendingNativeActivityBlocksDispatch()
    {
        var id = await Seed(); Due(); fence.Capturing();
        await Assert.ThrowsAsync<QueueException>(() => new ExecutionCoordinator(runtime, gateway, clock).ExecuteAsync(id, default));
        Assert.Equal(0, deletes);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task OptionalApprovalUsesTheApproversLiveRole(bool stillAdministrator)
    {
        runtime.Write(Guid.NewGuid(), "fixture", "RequireApproval", s => { s.Policy.RequireAdministratorApproval = true; return true; });
        var id = await Seed(); administrator = true;
        runtime.Write(Guid.NewGuid(), "fixture", "Approve", s =>
        {
            var p = QueueRules.Find(s, id);
            QueueRules.AdministratorDecision(s, id, new(owner.Id, true, true), p.Revision, QueueRules.AdministratorSnapshot(s, p), true, true, clock.Now);
            QueueRules.Evaluate(s, p, QueueRulesTests.Good with { AdministratorAuthorized = true }, clock.Now); return true;
        });
        Due(); administrator = stillAdministrator;
        var coordinator = new ExecutionCoordinator(runtime, gateway, clock);
        if (stillAdministrator) { await coordinator.ExecuteAsync(id, default); Assert.Equal(1, deletes); }
        else { await Assert.ThrowsAsync<QueueException>(() => coordinator.ExecuteAsync(id, default)); Assert.Equal(0, deletes); }
    }
    [Theory] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task ChangedAutomaticRuleBlocksPreclaimPreDispatchAndFinalDispatch(int denyAt) {
        administrator = true; var id = await Seed(); Due(); denyRuleAt = denyAt;
        runtime.Write(Guid.NewGuid(), "fixture", "AutomaticOrigin", state => { var p = QueueRules.Find(state, id); p.RequiresAutomaticRuleValidation = true; p.AutomaticRuleId = "fixture-rule"; return true; });
        var coordinator = new ExecutionCoordinator(runtime, gateway, clock);
        if (denyAt == 1) await Assert.ThrowsAsync<QueueException>(() => coordinator.ExecuteAsync(id, default));
        else await coordinator.ExecuteAsync(id, default);
        Assert.Equal(0, deletes); Assert.Equal(denyAt, ruleChecks);
        if (denyAt > 1) { Assert.Equal(ProposalStatus.Uncertain, QueueRules.Find(runtime.Read(), id).Status); await Assert.ThrowsAsync<QueueException>(() => coordinator.ExecuteAsync(id, default)); }
    }
    [Fact] public async Task SavedSeerrConnectionTestVerifiesAuthenticationIdentityAndVersionWithoutWrites() {
        administrator = true;
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new("Jellyfin-IsApiKey", "false"), new("Jellyfin-UserId", owner.Id.ToString())], "fixture"));
        var host = Fake<IServerApplicationHost>((m, _) => m.Name == "get_SystemId" ? "fixture-server" : throw new NotSupportedException());
        var controller = new Jellyfin.Plugin.DeletionQueue.Api.QueueController(runtime, identity, targets, null!, host, clock, null!) { ControllerContext = new() { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = principal } } };
        var before = runtime.History().Count;
        async Task<JsonElement> Test(ISeerrClientFactory factory) => JsonSerializer.SerializeToElement(Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.TestSeerr(factory, default)).Value, HubWire.Json);
        var good = await Test(new Factory(Send)); Assert.True(good.GetProperty("working").GetBoolean()); Assert.Equal("3.0.0", good.GetProperty("version").GetString());
        wrongSeerrServer = true; Assert.False((await Test(new Factory(Send))).GetProperty("working").GetBoolean());
        var failed = await Test(new Factory(_ => new(HttpStatusCode.Forbidden) { Content = new StringContent("synthetic-key must never be returned") }));
        Assert.False(failed.GetProperty("working").GetBoolean()); Assert.DoesNotContain("synthetic-key", failed.GetRawText());
        Assert.Equal(before, runtime.History().Count); Assert.Equal(0, deletes); Assert.Equal(0, declines);
        administrator = false; await Assert.ThrowsAsync<QueueException>(() => controller.TestSeerr(new Factory(Send), default));
    }
    public void Dispose() { rulesProvider.Dispose(); runtime.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
