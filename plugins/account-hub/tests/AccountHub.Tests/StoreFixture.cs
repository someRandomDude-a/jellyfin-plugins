using AccountHub.Core;
using Microsoft.Data.Sqlite;

namespace AccountHub.Tests;

internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public override long GetTimestamp() => Now.UtcTicks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
}

internal sealed class StoreFixture : IAsyncDisposable
{
    public HubPaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "AccountHub.Tests", Guid.NewGuid().ToString("N")));
    public TestClock Clock { get; } = new();
    public HubStore Store { get; }
    public StoreFixture() => Store = new(Paths, Clock);
    public Task InitializeAsync() => Store.InitializeAsync();
    public HubStore Reopen() => new(Paths, Clock);

    public async Task ExecuteSqlAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Paths.Database};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        Store.Dispose();
        // Only this fixture's random, self-created directory is removed.
        if (Directory.Exists(Paths.DataDirectory)) Directory.Delete(Paths.DataDirectory, true);
        return ValueTask.CompletedTask;
    }
}
