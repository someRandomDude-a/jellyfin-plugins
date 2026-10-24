using System.Text;
using System.Text.Json;
using DeletionQueue.Core;
using DeletionQueue.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DeletionQueue.Tests;

public sealed class CheckpointTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dq-checkpoint-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(directory, "queue.sqlite3");
    private string Seal => Path.Combine(directory, "head.seal");

    private long Scalar(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Database};Mode=ReadOnly;Pooling=False");
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private void Alter(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    private static void FillToRotation(AuthenticatedStore store)
    {
        for (var i = 1; i < 128; i++)
            store.Write(Guid.NewGuid(), "collector", "Heartbeat", state => { state.LastHeartbeat = DateTimeOffset.UnixEpoch.AddSeconds(i); return true; });
    }

    [Fact]
    public void RepeatedRotationsBoundGrowthPreserveStateHistoryAndOldCommandReplayProtection()
    {
        var oldCommand = Guid.NewGuid();
        string stateBefore;
        JournalEntry[] historyBefore;
        using (var store = new AuthenticatedStore(directory))
        {
            store.Write(oldCommand, "admin", "Configure", state =>
            {
                state.Schema = 2;
                state.Seerr.ApiKey = "private-checkpoint-secret";
                for (var i = 0; i < 1500; i++) state.Notices.Add(new Notice(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('n', 100)));
                return true;
            });
            for (var i = 0; i < 1024; i++)
                store.Write(Guid.NewGuid(), "collector", "CoverageHeartbeatAndEvaluation", state =>
                { state.LastHeartbeat = DateTimeOffset.UnixEpoch.AddSeconds(i); return true; });
            Assert.Equal(1024L, Scalar("SELECT seq FROM checkpoint"));
            Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM journal"));
            Assert.True(Scalar("SELECT MAX(length(ciphertext)) FROM journal") < 1024);
            Assert.Throws<QueueException>(() => store.Write(oldCommand, "admin", "Replay", _ => true));
            stateBefore = JsonSerializer.Serialize(store.Read()); historyBefore = store.History().ToArray();
            Assert.Equal(200, historyBefore.Length);
        }
        Assert.True(new FileInfo(Database).Length < 4 * 1024 * 1024, "Heartbeat traffic must not multiply the full retained state.");
        Assert.DoesNotContain("private-checkpoint-secret", Encoding.UTF8.GetString(File.ReadAllBytes(Database)));
        using var reopened = new AuthenticatedStore(directory);
        Assert.Equal(stateBefore, JsonSerializer.Serialize(reopened.Read())); Assert.Equal(historyBefore, reopened.History());
        Assert.Equal("CommandReplay", Assert.Throws<QueueException>(() => reopened.Write(oldCommand, "admin", "Replay", _ => true)).Code);
    }

    [Fact]
    public void ByteThresholdRotatesBeforeRecordThresholdAndRetainsFullChanges()
    {
        using (var store = new AuthenticatedStore(directory))
        {
            for (var i = 0; i < 3; i++)
                store.Write(Guid.NewGuid(), "admin", "LargeChange", state => { state.Seerr.ApiKey = new string((char)('a' + i), 3 * 1024 * 1024); return true; });
            Assert.Equal(4L, Scalar("SELECT seq FROM checkpoint"));
            Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM journal"));
        }
        using var reopened = new AuthenticatedStore(directory);
        Assert.Equal(new string('c', 3 * 1024 * 1024), reopened.Read().Seerr.ApiKey);
    }

    [Fact]
    public void HeartbeatDeltaDoesNotHideOtherStateChangesAndNoOpsStillHaveReplayEvidence()
    {
        using var store = new AuthenticatedStore(directory);
        var id = Guid.NewGuid();
        store.Write(id, "scheduler", "EligibilityEvaluated", _ => true);
        Assert.Equal("EligibilityEvaluated", store.History().Last().Kind);
        Assert.Throws<QueueException>(() => store.Write(id, "scheduler", "Replay", _ => true));
        store.Write(Guid.NewGuid(), "collector", "Heartbeat", state => { state.LastHeartbeat = DateTimeOffset.UnixEpoch; state.CoverageHealthy = true; return true; });
        Assert.True(store.Read().CoverageHealthy);
        Assert.Equal(DateTimeOffset.UnixEpoch, store.Read().LastHeartbeat);
        store.Write(Guid.NewGuid(), "collector", "Heartbeat", state => { state.LastHeartbeat = null; return true; });
        Assert.Null(store.Read().LastHeartbeat);
        Assert.True(store.Read().CoverageHealthy);
    }

    [Theory]
    [InlineData("CheckpointBeforeCommit")]
    [InlineData("CheckpointCommitted")]
    public void RotationFaultLeavesEitherAuthenticatedRepresentationWithSameSeal(string stage)
    {
        var armed = false;
        byte[]? sealAtRotation = null;
        using (var store = new AuthenticatedStore(directory, current =>
        {
            if (!armed || current != stage) return;
            sealAtRotation = File.ReadAllBytes(Seal);
            throw new IOException("Synthetic rotation interruption");
        }))
        {
            armed = true;
            Assert.Throws<IOException>(() => FillToRotation(store));
            Assert.Equal(sealAtRotation, File.ReadAllBytes(Seal));
        }
        using var reopened = new AuthenticatedStore(directory);
        Assert.Equal(128, reopened.History().Last().Sequence);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(127), reopened.Read().LastHeartbeat);
        reopened.Write(Guid.NewGuid(), "collector", "Continued", _ => true);
        Assert.Equal(129, reopened.History().Last().Sequence);
    }

    [Fact]
    public void AppendCommitBeforeSealStillFreezesWithoutRotationRepair()
    {
        using (var initial = new AuthenticatedStore(directory)) FillToRotation(initial);
        var seal = File.ReadAllBytes(Seal);
        var armed = false;
        using (var store = new AuthenticatedStore(directory, stage =>
        {
            if (armed && stage == "AppendCommitted") throw new IOException("Synthetic append interruption");
        }))
        {
            armed = true;
            Assert.Throws<IOException>(() => store.Write(Guid.NewGuid(), "admin", "Interrupted", state => { state.Policy.AutomaticDeletion = true; return true; }));
            Assert.Throws<IntegrityException>(() => store.Read());
        }
        var bytes = File.ReadAllBytes(Database);
        Assert.Throws<IntegrityException>(() => new AuthenticatedStore(directory));
        Assert.Equal(seal, File.ReadAllBytes(Seal)); Assert.Equal(bytes, File.ReadAllBytes(Database));
    }

    [Theory]
    [InlineData("UPDATE checkpoint SET ciphertext=zeroblob(length(ciphertext))")]
    [InlineData("UPDATE checkpoint SET nonce=zeroblob(12)")]
    [InlineData("UPDATE checkpoint SET tag=zeroblob(16)")]
    [InlineData("UPDATE checkpoint SET seq=seq-1")]
    [InlineData("UPDATE checkpoint SET digest=printf('%064d', 0)")]
    [InlineData("DELETE FROM checkpoint")]
    [InlineData("DROP TABLE checkpoint")]
    [InlineData("DELETE FROM journal WHERE seq=(SELECT MAX(seq) FROM journal)")]
    public void CheckpointTamperingAndSuffixOmissionFreezeWithoutChangingEvidence(string sql)
    {
        using (var store = new AuthenticatedStore(directory))
        {
            FillToRotation(store);
            store.Write(Guid.NewGuid(), "admin", "Tail", _ => true);
            Alter(sql);
            Assert.Throws<IntegrityException>(() => store.Read());
            Assert.Throws<IntegrityException>(() => store.Write(Guid.NewGuid(), "admin", "NoRepair", _ => true));
        }
        var bytes = File.ReadAllBytes(Database); var seal = File.ReadAllBytes(Seal);
        Assert.Throws<IntegrityException>(() => new AuthenticatedStore(directory));
        Assert.Equal(bytes, File.ReadAllBytes(Database)); Assert.Equal(seal, File.ReadAllBytes(Seal));
    }

    [Fact]
    public void OldHeadCannotAuthenticateNewCheckpoint()
    {
        using var store = new AuthenticatedStore(directory);
        var old = File.ReadAllBytes(Seal);
        FillToRotation(store);
        File.WriteAllBytes(Seal, old);
        Assert.Throws<IntegrityException>(() => store.Read());
    }

    [Fact]
    public void RestoredOlderCheckpointDatabaseCannotMatchCurrentHead()
    {
        using (var store = new AuthenticatedStore(directory)) FillToRotation(store);
        var oldDatabase = File.ReadAllBytes(Database);
        using (var store = new AuthenticatedStore(directory))
        {
            for (var i = 0; i < 128; i++) store.Write(Guid.NewGuid(), "admin", "NextGeneration", _ => true);
        }
        var currentSeal = File.ReadAllBytes(Seal);
        File.WriteAllBytes(Database, oldDatabase);
        Assert.Throws<IntegrityException>(() => new AuthenticatedStore(directory));
        Assert.Equal(oldDatabase, File.ReadAllBytes(Database)); Assert.Equal(currentSeal, File.ReadAllBytes(Seal));
    }

    [Fact]
    public void CheckpointFromAnotherInstallationCannotAuthenticate()
    {
        var other = Path.Combine(directory, "other-installation");
        using var store = new AuthenticatedStore(directory);
        FillToRotation(store);
        using (var otherStore = new AuthenticatedStore(other)) FillToRotation(otherStore);
        using (var connection = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "ATTACH DATABASE $other AS foreign_store; DELETE FROM checkpoint; INSERT INTO checkpoint SELECT * FROM foreign_store.checkpoint";
            command.Parameters.AddWithValue("$other", Path.Combine(other, "queue.sqlite3")); command.ExecuteNonQuery();
        }
        Assert.Throws<IntegrityException>(() => store.Read());
        Assert.Throws<IntegrityException>(() => store.Write(Guid.NewGuid(), "admin", "NoRepair", _ => true));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
