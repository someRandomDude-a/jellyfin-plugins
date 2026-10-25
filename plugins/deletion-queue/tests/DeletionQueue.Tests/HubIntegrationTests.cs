using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using AccountHub.Api;
using DeletionQueue.Core;
using DeletionQueue.Infrastructure;
using Jellyfin.Plugin.DeletionQueue;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeletionQueue.Tests;

public sealed class HubIntegrationTests
{
    [Fact] public void AdapterNormalizesNativeCompactGuidsWithoutChangingTheV1WireShape()
    {
        var id = Guid.NewGuid();
        using var doc = JsonDocument.Parse($$$"""{"userIds":["{{{id:N}}}"],"changes":{"defaultAuthority":"OwnRequests"}}""");
        var parsed = HubWire.Parse<HubPreviewRequest>(doc.RootElement);
        Assert.Equal(id, Assert.Single(parsed.UserIds));
        Assert.Contains(id.ToString("D"), HubWire.Serialize(parsed));
    }
    [Fact] public async Task ClientIsOptionalAndWorksAcrossSeparateClientAndHostLoadContexts()
    {
        using var absent = new ServiceCollection().BuildServiceProvider();
        Assert.False(await new AccountHubClient(absent).ReadyAsync(default));
        var clientContext = new AssemblyLoadContext("client-fixture-" + Guid.NewGuid(), true);
        var hostContext = new AssemblyLoadContext("host-fixture-" + Guid.NewGuid(), true);
        try
        {
            var assembly = hostContext.LoadFromAssemblyPath(typeof(AccountHub.HostFixture.Host).Assembly.Location);
            var dispatch = (Func<string, CancellationToken, Task<string>>)assembly.GetType("AccountHub.HostFixture.Host")!.GetMethod("Create")!.Invoke(null, null)!;
            using var services = new ServiceCollection().AddKeyedSingleton(AccountHubClient.ServiceKey, dispatch).BuildServiceProvider();
            var clientType = clientContext.LoadFromAssemblyPath(typeof(AccountHubClient).Assembly.Location).GetType(typeof(AccountHubClient).FullName!)!;
            Assert.NotSame(typeof(AccountHubClient), clientType);
            var client = Activator.CreateInstance(clientType, services)!;
            Assert.True(await (Task<bool>)clientType.GetMethod("ReadyAsync")!.Invoke(client, [CancellationToken.None])!);
            var payload = JsonSerializer.Serialize(new { schemaVersion = 1, eventId = Guid.NewGuid(), source = "deletion-queue", type = "proposal.updated" });
            var publish = clientType.GetMethod("PublishAsync")!;
            Assert.Equal("delivered", await (Task<string>)publish.Invoke(client, ["notification", payload, CancellationToken.None])!);
            Assert.Equal("delivered", await (Task<string>)publish.Invoke(client, ["notification", payload, CancellationToken.None])!);
        }
        finally { clientContext.Unload(); hostContext.Unload(); }
    }

    private static QueueState Routed(bool available = true)
    {
        var s = QueueRulesTests.State(); s.Schema = 2;
        var p = QueueRulesTests.Propose(s);
        NoticeRules.Publish(s, [p], QueueRulesTests.Now);
        HubIntegration.Capture(s, new Dictionary<Guid, long>(), [], "scheduler", "ProposalCreatedOrJoined", QueueRulesTests.Now, available);
        return s;
    }

    [Fact] public void CriticalAdminNotificationsAreIndependentOfAuditPublishing()
    {
        var s = Routed(); s.Outbox.Clear(); s.Hub.PublishAudit = false;
        var p = s.Proposals[0]; p.Status = ProposalStatus.Uncertain;
        HubIntegration.Capture(s, new Dictionary<Guid, long> { [p.Id] = p.Revision - 1 }, s.Notices.Select(n => n.Id).ToHashSet(),
            "executor", "ExecutionUncertain", QueueRulesTests.Now, true);
        var alert = Assert.Single(s.Outbox); Assert.Equal("notification", alert.Kind);
        var root = JsonDocument.Parse(alert.PayloadJson).RootElement;
        Assert.True(root.GetProperty("adminOnly").GetBoolean()); Assert.Equal("critical", root.GetProperty("severity").GetString());
        Assert.Equal(0, root.GetProperty("audience").GetArrayLength());
        Assert.Equal("alert.removal_uncertain", root.GetProperty("type").GetString());
        Assert.Equal("normal", root.GetProperty("actions")[0].GetProperty("size").GetString());
    }
    [Fact] public void UserOutcomeUpdatesAndAdministratorAlertsUseSeparateRecipientTypes()
    {
        var s = Routed(); s.Outbox.Clear(); s.Hub.PublishAudit = false;
        s.Notices.Clear(); // Allow a fresh user notice rather than coalescing the existing unread one.
        var before = s.Notices.Select(n => n.Id).ToHashSet(); var p = s.Proposals[0]; var oldRevision = p.Revision;
        p.Status = ProposalStatus.Uncertain; p.Revision++;
        NoticeRules.Publish(s, [p], QueueRulesTests.Now.AddMinutes(1));
        HubIntegration.Capture(s, new Dictionary<Guid, long> { [p.Id] = oldRevision }, before, "executor", "ExecutionUncertain", QueueRulesTests.Now.AddMinutes(1), true);
        var entries = s.Outbox.Where(entry => entry.Kind == "notification").Select(entry => JsonDocument.Parse(entry.PayloadJson).RootElement).ToArray();
        Assert.Contains(entries, entry => !entry.GetProperty("adminOnly").GetBoolean() && entry.GetProperty("type").GetString() == "proposal.uncertain");
        Assert.Contains(entries, entry => entry.GetProperty("adminOnly").GetBoolean() && entry.GetProperty("type").GetString() == "alert.removal_uncertain");
    }

    [Fact] public async Task UnsupportedOrThrowingHostsAreUnavailableNotDomainFailures()
    {
        Func<string, CancellationToken, Task<string>> incompatible = (_, _) => Task.FromResult("{\"apiVersion\":\"2\",\"ok\":true}");
        using var services = new ServiceCollection().AddKeyedSingleton(AccountHubClient.ServiceKey, incompatible).BuildServiceProvider();
        Assert.False(await new AccountHubClient(services).ReadyAsync(default));
        Func<string, CancellationToken, Task<string>> broken = (_, _) => throw new InvalidOperationException("Host failed");
        using var failed = new ServiceCollection().AddKeyedSingleton(AccountHubClient.ServiceKey, broken).BuildServiceProvider();
        Assert.False(await new AccountHubClient(failed).ReadyAsync(default));
    }

    [Fact] public void StableIndependentIntentsDoNotContainDomainEvidenceOrSecrets()
    {
        var s = Routed();
        Assert.Equal(2, s.Outbox.Count); Assert.Single(s.Outbox, e => e.Kind == "notification"); Assert.Single(s.Outbox, e => e.Kind == "audit");
        Assert.Equal("pending", Assert.Single(s.Notices).Delivery);
        foreach (var e in s.Outbox)
        {
            var json = e.PayloadJson;
            Assert.DoesNotContain("Consents", json); Assert.DoesNotContain("Fingerprint", json); Assert.DoesNotContain("ApiKey", json);
            Assert.DoesNotContain(s.Proposals[0].Target.Title, json); Assert.DoesNotContain("OwnSnapshot", json);
            Assert.Equal(e.EventId, JsonDocument.Parse(json).RootElement.GetProperty("eventId").GetGuid());
        }
    }
    [Fact] public void AcceptedNotificationAndFailedAuditKeepIndependentAcknowledgments()
    {
        var s = Routed(); var lease = Guid.NewGuid();
        foreach (var e in s.Outbox) { e.Status = "delivering"; e.LeaseId = lease; }
        var n = s.Outbox.Single(e => e.Kind == "notification"); var a = s.Outbox.Single(e => e.Kind == "audit");
        HubIntegration.Complete(s, n.EventId, lease, "delivered", QueueRulesTests.Now);
        HubIntegration.Complete(s, a.EventId, lease, "unavailable", QueueRulesTests.Now);
        Assert.Equal("hub", s.Notices[0].Delivery); Assert.Equal("delivered", n.Status); Assert.Equal("pending", a.Status);
        Assert.Equal(QueueRulesTests.Now.AddMinutes(1), a.NextAttemptAt);
        Assert.Equal(2, s.Outbox.Select(e => e.EventId).Distinct().Count());
    }
    [Fact] public void HubReadOwnershipDoesNotSuppressFutureMaterialUpdates()
    {
        var s = Routed(); var old = s.Notices[0]; var evt = s.Outbox.Single(e => e.Kind == "notification"); var lease = Guid.NewGuid();
        evt.Status = "delivering"; evt.LeaseId = lease;
        HubIntegration.Complete(s, evt.EventId, lease, "delivered", QueueRulesTests.Now);
        var revisions = s.Proposals.ToDictionary(p => p.Id, p => p.Revision);
        s.Proposals[0].Revision++;
        NoticeRules.Publish(s, s.Proposals, QueueRulesTests.Now.AddMinutes(1));
        HubIntegration.Capture(s, revisions, [old.Id], "fixture", "Changed", QueueRulesTests.Now.AddMinutes(1), true);
        Assert.Equal(2, s.Notices.Count); Assert.Equal("hub", s.Notices[0].Delivery); Assert.Null(s.Notices[0].ReadAt);
        Assert.Equal("pending", s.Notices[1].Delivery); Assert.NotEqual(old.Id, s.Notices[1].Id);
        Assert.Equal(2, s.Outbox.Count(e => e.Kind == "notification"));
    }
    [Theory] [InlineData("rejected", 0)] [InlineData("unavailable", 20)]
    public void TerminalFallbackStaysLocalAndNeverAutomaticallyRepublishes(string result, int minutes)
    {
        var s = Routed(); var e = s.Outbox.Single(e => e.Kind == "notification"); var lease = Guid.NewGuid();
        var payload = e.PayloadJson; e.Status = "delivering"; e.LeaseId = lease;
        HubIntegration.Complete(s, e.EventId, lease, result, QueueRulesTests.Now.AddMinutes(minutes));
        Assert.Equal("local_fallback", e.Status); Assert.Equal("local", s.Notices[0].Delivery);
        HubIntegration.Complete(s, e.EventId, lease, "delivered", QueueRulesTests.Now.AddHours(1));
        Assert.Equal("local_fallback", e.Status); Assert.Equal(payload, e.PayloadJson);
    }
    [Fact] public void AutoWithoutHubAndExistingLocalHistoryStayLocal()
    {
        var s = Routed(false); Assert.DoesNotContain(s.Outbox, e => e.Kind == "notification"); Assert.Equal("local", s.Notices[0].Delivery);
        HubIntegration.Capture(s, s.Proposals.ToDictionary(p => p.Id, p => p.Revision), s.Notices.Select(n => n.Id).ToHashSet(),
            "scheduler", "tick", QueueRulesTests.Now, true);
        Assert.DoesNotContain(s.Outbox, e => e.Kind == "notification");
    }
    [Fact] public void OutboxAndProposalRollbackTogetherAndSurviveReopenAfterCommit()
    {
        var root = Path.Combine(Path.GetTempPath(), "dq-outbox-" + Guid.NewGuid());
        try
        {
            using (var store = new AuthenticatedStore(root))
            {
                Assert.Throws<QueueException>(() => store.Write<bool>(Guid.NewGuid(), "fixture", "Abort", s => {
                    s.Schema = 2; var p = QueueRulesTests.Propose(s); NoticeRules.Publish(s, [p], QueueRulesTests.Now);
                    HubIntegration.Capture(s, new Dictionary<Guid, long>(), [], "fixture", "Proposal", QueueRulesTests.Now, true);
                    throw new QueueException("abort"); }));
                Assert.Empty(store.Read().Outbox); Assert.Empty(store.Read().Proposals);
                store.Write(Guid.NewGuid(), "fixture", "Commit", s => {
                    s.Schema = 2; var p = QueueRulesTests.Propose(s); NoticeRules.Publish(s, [p], QueueRulesTests.Now);
                    HubIntegration.Capture(s, new Dictionary<Guid, long>(), [], "fixture", "Proposal", QueueRulesTests.Now, true); return true; });
            }
            using var reopened = new AuthenticatedStore(root); Assert.Equal(2, reopened.Read().Outbox.Count);
            Assert.Single(reopened.Read().Proposals);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public void LegacyStateMigrationPreservesKeysNoticesConsentAndRejectsDowngrade()
    {
        var root = Path.Combine(Path.GetTempPath(), "dq-migration-" + Guid.NewGuid()); var data = Path.Combine(root, "deletion-queue");
        try
        {
            using (var old = new AuthenticatedStore(data)) old.Write(Guid.NewGuid(), "fixture", "Legacy", s => {
                var p = QueueRulesTests.Propose(s); NoticeRules.Publish(s, [p], QueueRulesTests.Now); return true; });
            var keys = File.ReadAllBytes(Path.Combine(data, "keys.json"));
            var paths = DispatchProxy.Create<IApplicationPaths, ProductionGatewayTests.Proxy>();
            ((ProductionGatewayTests.Proxy)(object)paths).Call = (m, _) => m.Name == "get_DataPath" ? root : throw new NotSupportedException();
            using (var runtime = new QueueRuntime(paths, NullLogger<QueueRuntime>.Instance, TimeProvider.System))
            {
                Assert.Equal(2, runtime.Read().Schema); Assert.Empty(runtime.Read().Outbox);
                Assert.Single(runtime.Read().Proposals[0].Consents); Assert.Equal("local", runtime.Read().Notices[0].Delivery);
                Assert.Equal(keys, File.ReadAllBytes(Path.Combine(data, "keys.json")));
            }
            using var upgraded = new AuthenticatedStore(data);
            Assert.Throws<QueueException>(() => upgraded.Write(Guid.NewGuid(), "fixture", "DowngradeAttempt", s => { s.Schema = 1; return true; }));
            Assert.Equal(2, upgraded.Read().Schema);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public void BulkAndResetProtectAdminsAndPreserveSafetyFloors()
    {
        var s = QueueRulesTests.State(); var p = QueueRulesTests.Propose(s);
        var admin = new Account(Guid.NewGuid(), true, true); var user = QueueRulesTests.Owner;
        s.Policy.Accounts[admin.Id] = ProposalAuthority.AnyAccessibleItem;
        QueueRules.UpdatePreferences(s, user.Id, 0, 200, false); p.NotBefore = QueueRulesTests.Now.AddHours(200);
        var result = HubAdapter.ApplyPolicy(s, new(HubAdapter.Revision(s), Guid.NewGuid(), new(DefaultAuthority: ProposalAuthority.Denied),
            [new(admin.Id, new(ProposalAuthority.Denied)), new(user.Id, new(ProposalAuthority.OwnRequests))]), [admin, user]);
        Assert.Contains(admin.Id.ToString(), HubWire.Serialize(result)); Assert.Equal(ProposalAuthority.AnyAccessibleItem, QueueRules.Authority(s, admin.Id));
        var deadline = p.NotBefore; HubAdapter.Reset(s, new(HubAdapter.Revision(s), Guid.NewGuid(), "all"), [admin, user]);
        Assert.Equal(ProposalAuthority.AnyAccessibleItem, QueueRules.Authority(s, admin.Id)); Assert.True(p.NotBefore >= deadline);
        Assert.True(p.DelayFloorHours >= 200); Assert.False(s.Policy.AutomaticDeletion); Assert.False(s.Policy.SeerrManagedDeletionAcknowledged);
        Assert.Single(p.Consents);
    }
    [Fact] public void UnchangedConnectionDoesNotRestartCoverageAndKeyClearingRequiresConfirmation()
    {
        var s = QueueRulesTests.State(); s.Seerr = new() { BaseUrl = "https://seerr.invalid", ApiKey = "fixture-secret", JellyfinServerId = "fixture" };
        var coverage = s.CoverageSince;
        HubAdapter.SettingsChange(s, new(HubAdapter.Revision(s), Guid.NewGuid(), Seerr: new(ApiKey: null)), "fixture", QueueRulesTests.Now);
        Assert.Equal("fixture-secret", s.Seerr.ApiKey); Assert.Equal(coverage, s.CoverageSince); Assert.Equal(0, s.Seerr.Revision);
        var before = JsonSerializer.Serialize(s);
        Assert.Throws<QueueException>(() => HubAdapter.SettingsChange(s,
            new(HubAdapter.Revision(s), Guid.NewGuid(), Seerr: new(ClearApiKey: true)), "fixture", QueueRulesTests.Now));
        Assert.Equal(before, JsonSerializer.Serialize(s));
        HubAdapter.SettingsChange(s, new(HubAdapter.Revision(s), Guid.NewGuid(), Seerr: new(ClearApiKey: true, ConfirmClear: true)), "fixture", QueueRulesTests.Now);
        Assert.Empty(s.Seerr.ApiKey); Assert.Equal(1, s.Seerr.Revision); Assert.False(s.CoverageHealthy);
    }
}
