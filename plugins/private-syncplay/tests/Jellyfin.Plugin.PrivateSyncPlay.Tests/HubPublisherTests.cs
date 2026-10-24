using System.Text.Json;
using AccountHub.Api;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PrivateSyncPlay.Tests;

public sealed class HubPublisherTests
{
    [Theory] [InlineData("accepted")] [InlineData("duplicate")]
    public async Task PublisherUsesPlanDefinedEnvelopesAndNeverFederatesPartySecrets(string acknowledgment)
    {
        SQLitePCL.Batteries_V2.Init();var path = Path.Combine(Path.GetTempPath(), "psp-hub-events-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new Configuration { EnableGuestInvites = true, DefaultGuestPermission = true };
            var store = new PartyStore(TimeProvider.System);var owner = Guid.NewGuid();var payloads = new List<string>();
            var services = new ServiceCollection();services.AddKeyedSingleton<Func<string, CancellationToken, Task<string>>>(AccountHubClient.ServiceKey, (_, _) => (request, _) =>
            {
                var root = JsonDocument.Parse(request).RootElement;var operation = root.GetProperty("operation").GetString();
                if (operation!.EndsWith(".publish", StringComparison.Ordinal)) payloads.Add(request);
                object data = operation switch
                {
                    "hello" => new { apiVersion = "1", hostVersion = "fixture", operations = new[] { "health", "notifications.publish", "audit.publish" } },
                    "health" => new { ready = true }, _ => new { status = acknowledgment }
                };
                return Task.FromResult(JsonSerializer.Serialize(new { apiVersion = "1", ok = true, data }));
            });
            using var provider = services.BuildServiceProvider();var outbox = new HubOutbox(Path.Combine(path, "events.db"));
            var policies = new HubPolicyService(() => config, _ => { }, () => [new(owner, "host-secret@example.invalid", true)], store);
            using var integration = new HubIntegration(store, outbox, new(provider), () => config, policies, NullLogger<HubIntegration>.Instance, Mock.Of<ISessionManager>());
            await integration.StartAsync(CancellationToken.None);
            var party = store.Create(Guid.NewGuid(), owner, "session-not-for-hub", GroupMode.Private, 4);
            var invite = store.Issue(party, owner, true, 2, null, config).Token;var cookie = store.RedeemGuest(invite, null, config);
            await integration.DispatchOnce(CancellationToken.None);
            Assert.Equal(6, payloads.Count);Assert.Equal(6, outbox.Counts()["delivered"]);
            var all = string.Join('\n', payloads);Assert.DoesNotContain(invite, all);Assert.DoesNotContain(cookie, all);
            Assert.DoesNotContain("session-not-for-hub", all);Assert.DoesNotContain("host-secret@example.invalid", all);
            Assert.Contains("Guest 1", all);Assert.Contains("syncplay.guest.joined", all);
            foreach (var request in payloads)
            {
                using var document = JsonDocument.Parse(request);
                if (document.RootElement.GetProperty("operation").GetString() != "notifications.publish") continue;
                foreach (var field in document.RootElement.GetProperty("payload").GetProperty("metadata").EnumerateObject())
                    Assert.Equal(JsonValueKind.String, field.Value.ValueKind);
            }
            store.RedeemGuest(invite, cookie, config);await integration.DispatchOnce(CancellationToken.None);Assert.Equal(6, payloads.Count);
            await integration.StopAsync(CancellationToken.None);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
    [Fact] public async Task AbsentHostFallsBackLocallyButRetainsIndependentAuditAndPartyAuthority()
    {
        SQLitePCL.Batteries_V2.Init();var path = Path.Combine(Path.GetTempPath(), "psp-hub-absent-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new Configuration();var owner = Guid.NewGuid();var store = new PartyStore(TimeProvider.System);
            using var provider = new ServiceCollection().BuildServiceProvider();var outbox = new HubOutbox(Path.Combine(path, "events.db"));
            var policies = new HubPolicyService(() => config, _ => { }, () => [new(owner, "Admin", true)], store);
            using var integration = new HubIntegration(store, outbox, new(provider), () => config, policies, NullLogger<HubIntegration>.Instance, Mock.Of<ISessionManager>());
            await integration.StartAsync(CancellationToken.None);var party = store.Create(Guid.NewGuid(), owner, "host", GroupMode.Private, 3);
            await integration.DispatchOnce(CancellationToken.None);
            Assert.Same(party, store.Require(party.Id));Assert.Equal(1, outbox.Counts()["local_fallback"]);Assert.True(outbox.Counts()["pending"] >= 1);
            await integration.StopAsync(CancellationToken.None);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
