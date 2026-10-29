using System.Security.Cryptography;
using AccountHub.Api.Contracts;
using AccountHub.Core.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AccountHub.Core;

public sealed record HubPaths(string DataDirectory)
{
    public string Database => Path.Combine(DataDirectory, "account-hub.db");
    public string Keys => Path.Combine(DataDirectory, "keys");
}

/// <summary>One writer and consistent read snapshots. No Jellyfin/external writes inside this transaction.</summary>
public sealed class HubStore(HubPaths paths, TimeProvider clock) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _ready;
    private IDataProtectionProvider? _protection;
    private byte[]? _fingerprintKey;
    public bool Ready => _ready;
    public IDataProtectionProvider Protection => _ready ? _protection! : throw new HubException("unavailable", "Storage is not ready", true);
    public async Task<string?> ReadSecretAsync(string name, CancellationToken ct = default)
    {
        var cipher = await ReadAsync(async (db, token) => (await db.Secrets.FindAsync([name], token))?.Ciphertext, ct);
        return cipher is null ? null : Protector(name).Unprotect(cipher);
    }
    public string? FailureCode { get; private set; }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_ready) return;
            Directory.CreateDirectory(paths.DataDirectory);
            var installation = new Jellyfin.PluginSupport.InstallationIdentity(paths.DataDirectory, paths.Database, paths.Keys);
            installation.BeforeOpen();
            await using var db = CreateContext();
            await db.Database.OpenConnectionAsync(ct);
            installation.Verify((SqliteConnection)db.Database.GetDbConnection());
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=FULL;", ct);
            await using (var command = db.Database.GetDbConnection().CreateCommand())
            {
                command.CommandText = "PRAGMA quick_check;";
                if (!string.Equals(await command.ExecuteScalarAsync(ct) as string, "ok", StringComparison.Ordinal))
                    throw new InvalidDataException("Database integrity check failed");
            }
            // Explicit versioned migrations only. Never EnsureCreated or silently recreate a bad DB.
            var knownMigrations = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            var appliedMigrations = (await db.Database.GetAppliedMigrationsAsync(ct)).ToHashSet(StringComparer.Ordinal);
            if (appliedMigrations.Any(x => !knownMigrations.Contains(x)))
                throw new InvalidDataException("Database schema is newer than this plugin");
            // EF's SQLite migration lock survives an interrupted process. An
            // already-current schema needs verification, not a new migration
            // lock. Never delete/steal a lock or skip any pending migration.
            if (!knownMigrations.SetEquals(appliedMigrations)) await db.Database.MigrateAsync(ct);
            if (!await db.State.AnyAsync(ct))
            {
                db.State.Add(new HubState());
                await db.SaveChangesAsync(ct);
            }
            var secrets = await db.Secrets.AsNoTracking().ToListAsync(ct);
            if (secrets.Count != 0 && (!Directory.Exists(paths.Keys) || !Directory.EnumerateFiles(paths.Keys, "key-*.xml").Any()))
                throw new CryptographicException("Restore the original Account Hub keys");
            Directory.CreateDirectory(paths.Keys);
            _protection = DataProtectionProvider.Create(new DirectoryInfo(paths.Keys),
                builder => builder.SetApplicationName("AccountHub"));
            foreach (var secret in secrets) _ = Protector(secret.Name).Unprotect(secret.Ciphertext);
            var fingerprint = secrets.SingleOrDefault(x => x.Name == "internal.commandFingerprintKey");
            if (fingerprint is null)
            {
                if (secrets.Count != 0) throw new CryptographicException("Command fingerprint key is missing");
                _fingerprintKey = RandomNumberGenerator.GetBytes(32);
                db.Secrets.Add(new IntegrationSecret
                {
                    Name = "internal.commandFingerprintKey",
                    Ciphertext = Protector("internal.commandFingerprintKey").Protect(Convert.ToBase64String(_fingerprintKey)),
                    UpdatedAtUnixMs = clock.GetUtcNow().ToUnixTimeMilliseconds()
                });
                await db.SaveChangesAsync(ct);
            }
            else _fingerprintKey = Convert.FromBase64String(Protector(fingerprint.Name).Unprotect(fingerprint.Ciphertext));
            var verification = await AuditLedger.VerifyAsync(db, ct);
            if (!verification.Valid) throw new InvalidDataException("Audit chain integrity check failed");
            installation.Seal((SqliteConnection)db.Database.GetDbConnection());
            FailureCode = null;
            _ready = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            _ready = false;
            FailureCode = "storage_recovery_required";
            throw;
        }
        finally { _gate.Release(); }
    }

    internal async Task<T> WriteAsync<T>(Func<AccountHubDbContext, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureReady();
            await using var db = CreateContext();
            await db.Database.OpenConnectionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=FULL;", ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var result = await action(db, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (Exception ex) when (ex is SqliteException or DbUpdateException or IOException or CryptographicException)
        {
            _ready = false;
            FailureCode = "storage_recovery_required";
            throw new HubException("unavailable", "Account Hub storage needs administrator recovery", true);
        }
        finally { _gate.Release(); }
    }

    public async Task<T> ReadAsync<T>(Func<AccountHubDbContext, CancellationToken, Task<T>> query, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureReady();
            await using var db = CreateContext();
            return await query(db, ct);
        }
        catch (Exception ex) when (ex is SqliteException or DbUpdateException or IOException)
        {
            _ready = false;
            FailureCode = "storage_recovery_required";
            throw new HubException("unavailable", "Account Hub storage needs administrator recovery", true);
        }
        finally { _gate.Release(); }
    }

    public Task<AuditAcceptance> AppendAuditAsync(AuditEvent value, CancellationToken ct = default) =>
        WriteAsync((db, token) => AuditLedger.AppendAsync(db, value, clock, token), ct);

    internal Task<AuditAcceptance> AppendProducerAuditAsync(AuditEvent value, string intakeHash, CancellationToken ct) =>
        WriteAsync((db, token) => AuditLedger.AppendAsync(db, value, clock, token, intakeHash), ct);

    public async Task<ChainVerification> VerifyAuditAsync(CancellationToken ct = default)
    {
        var result = await ReadAsync(AuditLedger.VerifyAsync, ct);
        if (!result.Valid)
        {
            _ready = false;
            FailureCode = "audit_integrity_failed";
        }
        return result;
    }

    public Task<int> PruneAuditAsync(Guid actor, CancellationToken ct = default) =>
        WriteAsync((db, token) => AuditLedger.PruneAsync(db, clock, actor, token), ct);

    internal string Protect(string name, string value) => Protector(name).Protect(value);
    internal string SecretFingerprint(string name, string? value) => Convert.ToHexStringLower(HMACSHA256.HashData(
        _fingerprintKey ?? throw new InvalidOperationException("Store not initialized"),
        System.Text.Encoding.UTF8.GetBytes(CanonicalJson.Serialize(new { name, value }))));
    private IDataProtector Protector(string name) => (_protection ?? throw new InvalidOperationException("Store not initialized"))
        .CreateProtector("AccountHub.IntegrationSecret.v1", name);

    private AccountHubDbContext CreateContext()
    {
        if (_ready && !File.Exists(paths.Database))
        {
            _ready = false; FailureCode = "storage_recovery_required";
            throw new HubException("unavailable", "Restore the original plugin database and installation artifacts", true);
        }
        return new(new DbContextOptionsBuilder<AccountHubDbContext>()
        .UseSqlite(new SqliteConnectionStringBuilder { DataSource = paths.Database, Pooling = false, DefaultTimeout = 10,
            Mode = _ready || File.Exists(Path.Combine(paths.DataDirectory, "installation.id")) ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate }.ToString())
        .Options);
    }

    private void EnsureReady()
    {
        if (!_ready) throw new HubException("unavailable", "Account Hub storage is not ready; restore database and keys if required", true);
    }

    public void Dispose()
    {
        if (_fingerprintKey is not null) CryptographicOperations.ZeroMemory(_fingerprintKey);
        _gate.Dispose();
    }
}
