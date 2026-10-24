using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using DeletionQueue.Core;
using Microsoft.Data.Sqlite;

namespace DeletionQueue.Infrastructure;

public sealed class IntegrityException(string message) : Exception(message);

/// <summary>
/// Encrypted journal and authenticated checkpoints are the state, with no mutable SQL projection.
/// The local head seal detects suffix omission, but is deliberately not advertised as independent rollback protection.
/// </summary>
public sealed class AuthenticatedStore : IStateStore, IDisposable
{
    private readonly object sync = new();
    private readonly string directory;
    private readonly FileStream instanceLock;
    private readonly byte[] encryptionKey;
    private readonly byte[] sealKey;
    private readonly Guid installation;
    private readonly string database;
    private readonly Action<string>? fault;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private const int MaximumPayloadBytes = 4 * 1024 * 1024;
    private const long MaximumJournalBytes = 256L * 1024 * 1024;
    private const int MaximumCheckpointBytes = 64 * 1024 * 1024;
    private const long RotationBytes = 8 * 1024 * 1024;
    private const int RotationRecords = 128;
    private const string JournalSchema = "CREATE TABLE journal (seq INTEGER PRIMARY KEY, command TEXT NOT NULL UNIQUE, previous TEXT NOT NULL, nonce BLOB NOT NULL, tag BLOB NOT NULL, ciphertext BLOB NOT NULL)";
    private const string CheckpointSchema = "CREATE TABLE checkpoint (seq INTEGER PRIMARY KEY, digest TEXT NOT NULL, nonce BLOB NOT NULL, tag BLOB NOT NULL, ciphertext BLOB NOT NULL)";

    private sealed record KeyMaterial(Guid Installation, string Key);
    private sealed record Event(Guid Command, string Actor, string Kind, DateTimeOffset At, QueueState? State,
        bool HeartbeatOnly = false, DateTimeOffset? Heartbeat = null);
    private sealed record Checkpoint(QueueState State, JournalEntry[] History, Guid[] Commands);
    private sealed record Loaded(QueueState State, long Sequence, string Digest, IReadOnlyList<JournalEntry> History,
        HashSet<Guid> Commands, long Bytes, int Records);
    private sealed record Head(long Sequence, string Digest, string Mac);

    public AuthenticatedStore(string directory) : this(directory, null) { }

    internal AuthenticatedStore(string directory, Action<string>? fault)
    {
        this.fault = fault;
        this.directory = directory;
        Directory.CreateDirectory(directory);
        RejectLink(directory);
        instanceLock = new FileStream(Path.Combine(directory, "writer.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            database = Path.Combine(directory, "queue.sqlite3");
            var keyPath = Path.Combine(directory, "keys.json");
            var marker = Path.Combine(directory, "initialized");
            var fresh = !File.Exists(marker);
            if (fresh)
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any(p => Path.GetFileName(p) != "writer.lock"))
                    throw new IntegrityException("Unrecognized existing state; initialization refused.");
                DurableCreate(marker, Encoding.UTF8.GetBytes("DeletionQueue schema 1\n"));
                DurableCreate(keyPath, JsonSerializer.SerializeToUtf8Bytes(new KeyMaterial(Guid.NewGuid(),
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            if (!File.Exists(keyPath)) throw new IntegrityException("Key missing; never regenerate keys for existing state.");
            var material = JsonSerializer.Deserialize<KeyMaterial>(ReadSmallFile(keyPath), Json)
                ?? throw new IntegrityException("Invalid key material.");
            installation = material.Installation;
            var master = Convert.FromBase64String(material.Key);
            if (master.Length != 32 || installation == Guid.Empty) throw new IntegrityException("Invalid key material.");
            encryptionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: "dq/encryption/v1"u8.ToArray());
            sealKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: "dq/head/v1"u8.ToArray());
            CryptographicOperations.ZeroMemory(master);
            if (!File.Exists(database))
            {
                if (!fresh) throw new IntegrityException("Partial existing initialization; automatic recovery refused.");
                if (File.Exists(SealPath) || new FileInfo(marker).Length != "DeletionQueue schema 1\n"u8.Length)
                    throw new IntegrityException("Database missing from an existing installation.");
                // A key-only partial initialization is not recoverable automatically.
                // A fresh marker and key created in THIS constructor are required below.
                if (File.Exists(Path.Combine(directory, "database-created")))
                    throw new IntegrityException("Database missing from an existing installation.");
                DurableCreate(Path.Combine(directory, "database-created"), "1"u8.ToArray());
                using var connection = Open(create: true);
                using var schema = connection.CreateCommand();
                schema.CommandText = JournalSchema + "; PRAGMA user_version=1;";
                schema.ExecuteNonQuery();
                WriteInitialHead();
                Write(Guid.NewGuid(), "installation", "Initialized", _ => true);
            }
            _ = Read();
        }
        catch
        {
            instanceLock.Dispose();
            throw;
        }
    }

    private string SealPath => Path.Combine(directory, "head.seal");

    public QueueState Read()
    {
        lock (sync)
        {
            try { using var connection = Open(readOnly: true); return Load(connection).State; }
            catch (Exception e) when (e is not IntegrityException)
            { throw new IntegrityException("State authentication failed; authorization is frozen."); }
        }
    }

    public IReadOnlyList<JournalEntry> History()
    {
        lock (sync) { using var connection = Open(readOnly: true); return Load(connection).History; }
    }

    public T Write<T>(Guid commandId, string actor, string kind, Func<QueueState, T> command)
    {
        if (commandId == Guid.Empty) throw new QueueException("CommandIdRequired");
        lock (sync)
        {
            // Authenticate with a read-only connection before opening any existing database for writes.
            using (var verification = Open(readOnly: true)) { _ = Load(verification); }
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var loaded = Load(connection, transaction);
            if (loaded.Commands.Contains(commandId)) throw new QueueException("CommandReplay");
            var before = FingerprintWithoutHeartbeat(loaded.State);
            var previousSchema = loaded.State.Schema;
            var result = command(loaded.State);
            if (loaded.State.Schema is not (1 or 2) || previousSchema == 2 && loaded.State.Schema != 2)
                throw new QueueException("InvalidSchemaTransition");
            var next = loaded.Sequence + 1;
            var nonce = RandomNumberGenerator.GetBytes(12);
            var heartbeatOnly = before.AsSpan().SequenceEqual(FingerprintWithoutHeartbeat(loaded.State));
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(new Event(commandId, actor, kind, DateTimeOffset.UtcNow,
                heartbeatOnly ? null : loaded.State, heartbeatOnly, loaded.State.LastHeartbeat), Json);
            if (plaintext.Length > MaximumPayloadBytes)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new QueueException("StateCapacityReached");
            }
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            try
            {
                using var aes = new AesGcm(encryptionKey, 16);
                aes.Encrypt(nonce, plaintext, ciphertext, tag, Aad(next, commandId, loaded.Digest));
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO journal VALUES ($seq,$command,$previous,$nonce,$tag,$ciphertext)";
            insert.Parameters.AddWithValue("$seq", next);
            insert.Parameters.AddWithValue("$command", commandId.ToString("D"));
            insert.Parameters.AddWithValue("$previous", loaded.Digest);
            insert.Parameters.AddWithValue("$nonce", nonce);
            insert.Parameters.AddWithValue("$tag", tag);
            insert.Parameters.AddWithValue("$ciphertext", ciphertext);
            insert.ExecuteNonQuery();
            transaction.Commit();
            fault?.Invoke("AppendCommitted");
            // Crash between the database commit and head replacement freezes the store. Never roll the head back.
            SaveHead(next, Digest(next, commandId, loaded.Digest, nonce, tag, ciphertext));
            // Rotate only after the existing commit/seal protocol has completed. Rotation itself never changes
            // the head: both the old journal and the replacement checkpoint authenticate to the SAME head.
            if (loaded.Bytes + ciphertext.Length >= RotationBytes || loaded.Records + 1 >= RotationRecords)
                Rotate();
            return result;
        }
    }

    private static byte[] FingerprintWithoutHeartbeat(QueueState state)
    {
        var heartbeat = state.LastHeartbeat;
        byte[]? clear = null;
        try
        {
            state.LastHeartbeat = null;
            clear = JsonSerializer.SerializeToUtf8Bytes(state, Json);
            return SHA256.HashData(clear);
        }
        finally
        {
            state.LastHeartbeat = heartbeat;
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
        }
    }

    private Loaded Load(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        try
        {
            using var version = connection.CreateCommand();
            version.Transaction = transaction;
            version.CommandText = "PRAGMA user_version";
            if ((long)version.ExecuteScalar()! != 1) throw new IntegrityException("Unknown schema; additive migration required.");
            using var schema = connection.CreateCommand(); schema.Transaction = transaction;
            schema.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE NOT ((type='table' AND name='journal' AND sql=$schema) OR (type='index' AND name='sqlite_autoindex_journal_1') OR (type='table' AND name='checkpoint' AND sql=$checkpoint));";
            schema.Parameters.AddWithValue("$schema", JournalSchema);
            schema.Parameters.AddWithValue("$checkpoint", CheckpointSchema);
            if ((long)schema.ExecuteScalar()! != 0) throw new IntegrityException("Unexpected private database schema; no query or write attempted.");
            var state = new QueueState();
            var history = new Queue<JournalEntry>();
            var replay = Stopwatch.StartNew();
            long sequence = 0;
            var digest = "genesis";
            var commands = new HashSet<Guid>();
            long bytes = 0;
            var records = 0;
            using (var exists = connection.CreateCommand())
            {
                exists.Transaction = transaction;
                exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='checkpoint'";
                if ((long)exists.ExecuteScalar()! != 0)
                {
                    using var readCheckpoint = connection.CreateCommand();
                    readCheckpoint.Transaction = transaction;
                    readCheckpoint.CommandText = "SELECT seq,digest,nonce,tag,ciphertext FROM checkpoint";
                    using var row = readCheckpoint.ExecuteReader();
                    if (!row.Read()) throw new IntegrityException("Missing checkpoint.");
                    sequence = row.GetInt64(0); digest = row.GetString(1);
                    if (sequence < 1 || digest.Length != 64 || row.GetBytes(2, 0, null, 0, 0) != 12 ||
                        row.GetBytes(3, 0, null, 0, 0) != 16 || row.GetBytes(4, 0, null, 0, 0) > MaximumCheckpointBytes)
                        throw new IntegrityException("Invalid checkpoint metadata.");
                    var clear = new byte[row.GetBytes(4, 0, null, 0, 0)];
                    Checkpoint checkpoint;
                    try
                    {
                        using var aes = new AesGcm(encryptionKey, 16);
                        aes.Decrypt((byte[])row[2], (byte[])row[4], (byte[])row[3], clear, CheckpointAad(sequence, digest));
                        checkpoint = JsonSerializer.Deserialize<Checkpoint>(clear, Json) ?? throw new IntegrityException("Invalid checkpoint.");
                    }
                    finally { CryptographicOperations.ZeroMemory(clear); }
                    state = checkpoint.State;
                    commands = checkpoint.Commands.ToHashSet();
                    if (state.Schema is not (1 or 2) || commands.Count != sequence || commands.Contains(Guid.Empty) ||
                        checkpoint.History.Length != Math.Min(sequence, 200) ||
                        checkpoint.History.Where((entry, index) => entry.Sequence != sequence - checkpoint.History.Length + index + 1 || !commands.Contains(entry.CommandId)).Any() || row.Read())
                        throw new IntegrityException("Invalid checkpoint context.");
                    history = new Queue<JournalEntry>(checkpoint.History);
                }
            }
            using var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = "SELECT seq,command,previous,nonce,tag,ciphertext,length(command),length(previous) FROM journal ORDER BY seq";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                if (replay.Elapsed > TimeSpan.FromSeconds(2)) throw new IntegrityException("Journal replay budget exceeded; plugin stopped without resetting state.");
                if (reader.GetFieldType(0) != typeof(long) || reader.GetInt64(6) != 36 || reader.GetInt64(7) > 64)
                    throw new IntegrityException("Invalid journal metadata size/type.");
                var next = reader.GetInt64(0);
                var id = Guid.Parse(reader.GetString(1));
                if (next != sequence + 1 || reader.GetString(2) != digest) throw new IntegrityException("Journal omission or reordering.");
                if (reader.GetBytes(3, 0, null, 0, 0) != 12 || reader.GetBytes(4, 0, null, 0, 0) != 16 ||
                    reader.GetBytes(5, 0, null, 0, 0) > MaximumPayloadBytes) throw new IntegrityException("Invalid or excessive journal record size.");
                var nonce = (byte[])reader[3];
                var tag = (byte[])reader[4];
                var ciphertext = (byte[])reader[5];
                var plaintext = new byte[ciphertext.Length];
                Event evt;
                try
                {
                    using var aes = new AesGcm(encryptionKey, 16);
                    aes.Decrypt(nonce, ciphertext, tag, plaintext, Aad(next, id, digest));
                    evt = JsonSerializer.Deserialize<Event>(plaintext, Json) ?? throw new IntegrityException("Invalid journal event.");
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
                if (evt.Command != id || !commands.Add(id) || id == Guid.Empty ||
                    evt.HeartbeatOnly && evt.State is not null || !evt.HeartbeatOnly && evt.State is null)
                    throw new IntegrityException("Invalid or downgraded journal context.");
                if (evt.HeartbeatOnly) state.LastHeartbeat = evt.Heartbeat;
                else
                {
                    if (evt.State!.Schema is not (1 or 2) || state.Schema == 2 && evt.State.Schema == 1)
                        throw new IntegrityException("Invalid or downgraded journal context.");
                    state = evt.State;
                }
                bytes += ciphertext.Length; records++;
                if (history.Count == 200) history.Dequeue();
                history.Enqueue(new JournalEntry(next, evt.Command, evt.Actor, evt.Kind, evt.At));
                digest = Digest(next, id, digest, nonce, tag, ciphertext);
                sequence = next;
            }
            var head = JsonSerializer.Deserialize<Head>(ReadSmallFile(SealPath), Json) ?? throw new IntegrityException("Missing head seal.");
            if (head.Sequence != sequence || head.Digest != digest ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(head.Mac), HeadMac(sequence, digest)))
                throw new IntegrityException("Head mismatch: possible truncation, rollback or interrupted commit. No repair performed.");
            return new Loaded(state, sequence, digest, history.TakeLast(200).ToArray(), commands, bytes, records);
        }
        catch (Exception e) when (e is not IntegrityException)
        { throw new IntegrityException("Journal authentication failed; original state preserved."); }
    }

    private byte[] CheckpointAad(long sequence, string digest) =>
        Encoding.UTF8.GetBytes($"dq/checkpoint/v1|{installation:D}|{sequence}|{digest}");

    private void Rotate()
    {
        using (var verification = Open(readOnly: true)) { _ = Load(verification); }
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var loaded = Load(connection, transaction);
        var clear = JsonSerializer.SerializeToUtf8Bytes(new Checkpoint(loaded.State, loaded.History.ToArray(), loaded.Commands.ToArray()), Json);
        if (clear.Length > MaximumCheckpointBytes)
        {
            CryptographicOperations.ZeroMemory(clear);
            throw new QueueException("CheckpointCapacityReached");
        }
        var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var cipher = new byte[clear.Length];
        try
        {
            using var aes = new AesGcm(encryptionKey, 16);
            aes.Encrypt(nonce, clear, cipher, tag, CheckpointAad(loaded.Sequence, loaded.Digest));
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
        using var replace = connection.CreateCommand(); replace.Transaction = transaction;
        replace.CommandText = CheckpointSchema.Replace("CREATE TABLE", "CREATE TABLE IF NOT EXISTS") +
            "; DELETE FROM checkpoint; INSERT INTO checkpoint VALUES ($seq,$digest,$nonce,$tag,$cipher); DELETE FROM journal;";
        replace.Parameters.AddWithValue("$seq", loaded.Sequence); replace.Parameters.AddWithValue("$digest", loaded.Digest);
        replace.Parameters.AddWithValue("$nonce", nonce); replace.Parameters.AddWithValue("$tag", tag); replace.Parameters.AddWithValue("$cipher", cipher);
        replace.ExecuteNonQuery();
        fault?.Invoke("CheckpointBeforeCommit");
        transaction.Commit();
        fault?.Invoke("CheckpointCommitted");
        // Reuse freed pages rather than VACUUM/replacing the database or discarding rollback evidence.
    }

    private SqliteConnection Open(bool create = false, bool readOnly = false)
    {
        RejectLink(directory);
        foreach (var file in new[] { database, database + "-wal", database + "-shm" })
        {
            RejectLink(file);
            if (File.Exists(file) && new FileInfo(file).Length > MaximumJournalBytes) throw new IntegrityException("Plugin storage budget exceeded; no state reset performed.");
        }
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database, Mode = create ? SqliteOpenMode.ReadWriteCreate : readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false, DefaultTimeout = 2 }.ToString());
        try
        {
            connection.Open();
            using var safety = connection.CreateCommand();
            safety.CommandText = "PRAGMA trusted_schema=OFF; PRAGMA synchronous=FULL;";
            safety.ExecuteNonQuery();
            if (create)
            {
                using var pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
                pragma.ExecuteNonQuery();
            }
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IntegrityException("Linked plugin state is not supported; no target opened.");
    }
    private static byte[] ReadSmallFile(string path)
    {
        RejectLink(path);
        if (new FileInfo(path).Length > 8192) throw new IntegrityException("Invalid key/seal length.");
        return File.ReadAllBytes(path);
    }

    private byte[] Aad(long sequence, Guid command, string previous) =>
        Encoding.UTF8.GetBytes($"dq/v1|{installation:D}|{sequence}|{command:D}|{previous}");

    private string Digest(long sequence, Guid command, string previous, byte[] nonce, byte[] tag, byte[] ciphertext)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Aad(sequence, command, previous));
        hash.AppendData(nonce); hash.AppendData(tag); hash.AppendData(ciphertext);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private byte[] HeadMac(long sequence, string digest) => HMACSHA256.HashData(sealKey,
        Encoding.UTF8.GetBytes($"dq/head/v1|{installation:D}|{sequence}|{digest}"));

    private void WriteInitialHead() => SaveHead(0, "genesis");

    private void SaveHead(long sequence, string digest)
    {
        var temporary = Path.Combine(directory, $"head-{Guid.NewGuid():N}.tmp");
        DurableCreate(temporary, JsonSerializer.SerializeToUtf8Bytes(new Head(sequence, digest,
            Convert.ToHexString(HeadMac(sequence, digest))), Json));
        File.Move(temporary, SealPath, overwrite: true);
    }

    private static void DurableCreate(string path, byte[] content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        lock (sync)
        {
            CryptographicOperations.ZeroMemory(encryptionKey);
            CryptographicOperations.ZeroMemory(sealKey);
            instanceLock.Dispose();
        }
    }
}
