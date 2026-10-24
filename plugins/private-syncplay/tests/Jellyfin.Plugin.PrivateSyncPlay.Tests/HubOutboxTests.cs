using AccountHub.Api;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Loader;
using System.Text.Json;
using Xunit;

namespace Jellyfin.Plugin.PrivateSyncPlay.Tests;

public sealed class HubOutboxTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "psp-hub-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private HubOutbox Store() { SQLitePCL.Batteries_V2.Init();return new(Path.Combine(_directory, "integration.db")); }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    [Fact] public void RetryIdsAndIndependentAcknowledgmentsSurviveRestartAndExpiredLeasesRecover()
    {
        var store = Store();var id = Guid.NewGuid();
        store.Enqueue(id, "notification", "{}", _now, TimeSpan.FromMinutes(2));store.Enqueue(id, "audit", "{}", _now, TimeSpan.FromDays(30));
        var rows = store.Claim(_now);Assert.Equal(2, rows.Length);Assert.Empty(Store().Claim(_now.AddSeconds(10)));
        var recovered = Store().Claim(_now.AddSeconds(31));Assert.Equal(rows.Select(r => r.Id), recovered.Select(r => r.Id));
        var notice = recovered.Single(r => r.Kind == "notification");store.Complete(notice, "duplicate", _now.AddSeconds(31));
        var audit = recovered.Single(r => r.Kind == "audit");store.Complete(audit, "unavailable", _now.AddSeconds(31));
        Assert.Single(store.Claim(_now.AddMinutes(1)));Assert.Equal(1, store.Counts()["delivered"]);
    }
    [Fact] public void StaleNotificationsAreNotReplayedButAuditRemainsPendingAndTerminalRowsPrune()
    {
        var store = Store();var id = Guid.NewGuid();store.Enqueue(id, "notification", "{}", _now, TimeSpan.FromSeconds(120));store.Enqueue(id, "audit", "{}", _now, TimeSpan.FromDays(30));
        var rows = store.Claim(_now.AddMinutes(3));Assert.Single(rows);Assert.Equal("audit", rows[0].Kind);Assert.Equal(1, store.Counts()["local_fallback"]);
        store.Complete(rows[0], "accepted", _now.AddMinutes(3));store.Claim(_now.AddDays(31));Assert.Empty(store.Counts());
    }
    [Fact] public void UnknownStoreSchemaAndOversizedEventsFailClosed()
    {
        var store = Store();Assert.Throws<InvalidDataException>(() => store.Enqueue(Guid.NewGuid(), "audit", new string('x', 65537), _now, TimeSpan.FromDays(1)));
        store.Counts();using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Pooling=False;Data Source=" + Path.Combine(_directory, "integration.db")))
        { db.Open();using var command = db.CreateCommand();command.CommandText = "PRAGMA user_version=999";command.ExecuteNonQuery(); }
        Assert.Throws<InvalidDataException>(() => store.Counts());
    }
    [Fact] public void VersionOneMigrationKeepsPendingEvents()
    {
        var store = Store();var id = Guid.NewGuid();store.Enqueue(id, "audit", "{}", _now, TimeSpan.FromDays(30));
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Pooling=False;Data Source=" + Path.Combine(_directory, "integration.db")))
        {
            db.Open();using var command = db.CreateCommand();command.CommandText = "ALTER TABLE events DROP COLUMN last_error; PRAGMA user_version=1";command.ExecuteNonQuery();
        }
        var row = Assert.Single(Store().Claim(_now));Assert.Equal(id + ":audit", row.Id);
        store.Complete(row, "accepted", _now);Assert.Equal(1, store.Counts()["delivered"]);
    }
    [Fact] public async Task FrameworkOnlyKeyedClientWorksAcrossSeparateAssemblyContextsAndWithAbsentHost()
    {
        var context = new AssemblyLoadContext("hub-client-test", isCollectible: true);
        try
        {
            var assembly = context.LoadFromAssemblyPath(typeof(AccountHubClient).Assembly.Location);
            var type = assembly.GetType(typeof(AccountHubClient).FullName!)!;
            using var absent = new ServiceCollection().BuildServiceProvider();
            var client = Activator.CreateInstance(type, absent)!;Assert.False((bool)type.GetProperty("IsAvailable")!.GetValue(client)!);
            var services = new ServiceCollection();var requests = new List<string>();
            services.AddKeyedSingleton<Func<string, CancellationToken, Task<string>>>("AccountHub.Api.1", (_, _) => (json, _) =>
            { requests.Add(json);return Task.FromResult("{\"apiVersion\":\"1\",\"ok\":true,\"data\":{\"status\":\"duplicate\"}}"); });
            using var host = services.BuildServiceProvider();client = Activator.CreateInstance(type, host)!;
            var result = await (Task<JsonElement?>)type.GetMethod("InvokeAsync", [typeof(string), typeof(JsonElement), typeof(CancellationToken)])!.Invoke(client, ["audit.publish", JsonSerializer.SerializeToElement(new { eventId = Guid.NewGuid() }), CancellationToken.None])!;
            Assert.Equal("duplicate", result!.Value.GetProperty("data").GetProperty("status").GetString());
            Assert.Equal("1", JsonDocument.Parse(requests.Single()).RootElement.GetProperty("apiVersion").GetString());
        }
        finally { context.Unload(); }
    }
}
