using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.UserManager.Data;

/// <summary>
/// SQLite-backed storage for invites, audit records, Discord links, reset tokens, presets and settings.
/// </summary>
public sealed class PluginStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _resetEffectsLock = new(1, 1);
    private bool _initialized;

    public PluginStore(string dataFolder, string databaseFileName = "usermanager.db")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);
        Directory.CreateDirectory(dataFolder);
        var path = Path.Combine(dataFolder, databaseFileName);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 10
        }.ToString();
        DatabasePath = path;
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;")
                .ConfigureAwait(false);

            var version = await connection.ExecuteScalarAsync<long>("PRAGMA user_version;").ConfigureAwait(false);
            if (version > 3) throw new InvalidDataException("Unsupported lifecycle schema. Restore matching binaries and backup.");
            if (version < 1)
            {
                await connection.ExecuteAsync(SchemaV1).ConfigureAwait(false);
                await connection.ExecuteAsync("PRAGMA user_version=1;").ConfigureAwait(false);
            }
            if (version < 2)
            {
                using var migration = connection.BeginTransaction();
                await connection.ExecuteAsync("ALTER TABLE reset_tokens ADD COLUMN Stage TEXT NOT NULL DEFAULT 'issued'; PRAGMA user_version=2;", transaction: migration).ConfigureAwait(false);
                migration.Commit();
            }
            if (version < 3)
            {
                using var migration = connection.BeginTransaction();
                await connection.ExecuteAsync("""
                    ALTER TABLE reset_tokens ADD COLUMN SessionsRevokedAt INTEGER NULL;
                    ALTER TABLE reset_tokens ADD COLUMN RevocationAttemptOrder INTEGER NOT NULL DEFAULT 0;
                    UPDATE reset_tokens SET SessionsRevokedAt = COALESCE(UsedAt, CreatedAt) WHERE Stage = 'sessions_revoked';
                    UPDATE reset_tokens SET Stage = 'claimed' WHERE UsedAt IS NOT NULL AND Stage = 'issued';
                    CREATE INDEX idx_reset_revocation ON reset_tokens(SessionsRevokedAt, RevocationAttemptOrder, TokenHash);
                    PRAGMA user_version=3;
                    """, transaction: migration).ConfigureAwait(false);
                migration.Commit();
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        if (File.Exists(Path.Combine(Path.GetDirectoryName(DatabasePath)!, "installation.id"))) builder.Mode = SqliteOpenMode.ReadWrite;
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync("PRAGMA busy_timeout=10000; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;").ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // ---------------------------------------------------------------- invites
    // One host owns this store. Hold this fence across native password/session effects,
    // not just SQL transitions; an expired lease could revoke before a live password write.
    public async Task<IDisposable> AcquireResetEffectsAsync(CancellationToken ct = default)
    {
        await _resetEffectsLock.WaitAsync(ct).ConfigureAwait(false);
        return new ResetEffectsLease(_resetEffectsLock);
    }

    private sealed class ResetEffectsLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    public async Task SetResetStageAsync(string tokenHash, string stage, CancellationToken ct = default)
    {
        if (stage is not ("claimed" or "password_changed" or "sessions_revoked" or "remediation_pending")) throw new ArgumentException("Unknown reset stage.");
        if (stage == "sessions_revoked")
        {
            await MarkResetSessionsRevokedAsync(tokenHash, ct).ConfigureAwait(false);
            return;
        }
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await connection.ExecuteAsync("UPDATE reset_tokens SET Stage=@Stage WHERE TokenHash=@Hash AND UsedAt IS NOT NULL", new { Stage = stage, Hash = tokenHash });
    }
    public async Task MarkResetSessionsRevokedAsync(string tokenHash, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await connection.ExecuteAsync("""
            UPDATE reset_tokens
            SET SessionsRevokedAt = COALESCE(SessionsRevokedAt, @Now),
                Stage = CASE WHEN Stage = 'claimed' THEN Stage ELSE 'sessions_revoked' END
            WHERE TokenHash = @Hash AND UsedAt IS NOT NULL
            """, new { Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Hash = tokenHash });
    }

    /// <summary>Selects and durably rotates at most 50 attempts, including failures.</summary>
    public async Task<IReadOnlyList<ResetTokenRecord>> PendingSessionRevocationsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        var records = (await connection.QueryAsync<ResetTokenRecord>("""
            SELECT * FROM reset_tokens
            WHERE UsedAt IS NOT NULL AND SessionsRevokedAt IS NULL
              AND Stage IN ('claimed','password_changed','remediation_pending')
            ORDER BY RevocationAttemptOrder, TokenHash LIMIT 50
            """, transaction: transaction)).ToArray();
        if (records.Length > 0)
        {
            await connection.ExecuteAsync("""
                UPDATE reset_tokens
                SET RevocationAttemptOrder = (SELECT COALESCE(MAX(RevocationAttemptOrder), 0) + 1 FROM reset_tokens)
                WHERE TokenHash IN @Hashes
                """, new { Hashes = records.Select(r => r.TokenHash).ToArray() }, transaction);
        }
        transaction.Commit();
        return records;
    }

    public async Task CreateInviteAsync(InviteRecord invite, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            """
            INSERT INTO invites (Id, TokenHash, CreatedByUserId, CreatedByName, PresetId, CreatedAt, ExpiresAt, MaxUses, UseCount, Revoked, Note)
            VALUES (@Id, @TokenHash, @CreatedByUserId, @CreatedByName, @PresetId, @CreatedAt, @ExpiresAt, @MaxUses, @UseCount, @Revoked, @Note);
            """,
            invite).ConfigureAwait(false);
    }

    public async Task<InviteRecord?> GetInviteByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<InviteRecord>(
            "SELECT i.*, p.Name AS PresetName FROM invites i LEFT JOIN presets p ON p.Id = i.PresetId WHERE i.TokenHash = @TokenHash;",
            new { TokenHash = tokenHash }).ConfigureAwait(false);
    }

    public async Task<InviteRecord?> GetInviteAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<InviteRecord>(
            "SELECT i.*, p.Name AS PresetName FROM invites i LEFT JOIN presets p ON p.Id = i.PresetId WHERE i.Id = @Id;",
            new { Id = id }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InviteRecord>> GetInvitesAsync(int limit = 200, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var invites = (await connection.QueryAsync<InviteRecord>(
            """
            SELECT i.*, p.Name AS PresetName
            FROM invites i LEFT JOIN presets p ON p.Id = i.PresetId
            ORDER BY i.CreatedAt DESC
            LIMIT @Limit;
            """,
            new { Limit = Math.Clamp(limit, 1, 1000) }).ConfigureAwait(false)).ToList();

        if (invites.Count == 0)
        {
            return invites;
        }

        var ids = invites.Select(i => i.Id).ToArray();
        var uses = await connection.QueryAsync<InviteUseRecord>(
            "SELECT * FROM invite_uses WHERE InviteId IN @Ids ORDER BY UsedAt DESC;",
            new { Ids = ids }).ConfigureAwait(false);

        var grouped = uses.GroupBy(u => u.InviteId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var invite in invites)
        {
            if (grouped.TryGetValue(invite.Id, out var list))
            {
                invite.Uses = list;
            }
        }

        return invites;
    }

    /// <summary>
    /// Atomically reserves one use of an invite. Returns false when the invite is expired, revoked or exhausted.
    /// </summary>
    public async Task<bool> TryReserveInviteAsync(string tokenHash, long now, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(
            """
            UPDATE invites
            SET UseCount = UseCount + 1
            WHERE TokenHash = @TokenHash
              AND Revoked = 0
              AND ExpiresAt > @Now
              AND UseCount < MaxUses;
            """,
            new { TokenHash = tokenHash, Now = now }).ConfigureAwait(false);
        return affected == 1;
    }

    public async Task ReleaseInviteAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "UPDATE invites SET UseCount = MAX(0, UseCount - 1) WHERE TokenHash = @TokenHash;",
            new { TokenHash = tokenHash }).ConfigureAwait(false);
    }

    public async Task AddInviteUseAsync(InviteUseRecord use, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "INSERT INTO invite_uses (Id, InviteId, UserId, Username, UsedAt, RemoteIp) VALUES (@Id, @InviteId, @UserId, @Username, @UsedAt, @RemoteIp);",
            use).ConfigureAwait(false);
    }

    public async Task RevokeInviteAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync("UPDATE invites SET Revoked = 1 WHERE Id = @Id;", new { Id = id }).ConfigureAwait(false);
    }

    public async Task DeleteInviteAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync("DELETE FROM invites WHERE Id = @Id;", new { Id = id }).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ audit

    public async Task AddAuditAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            """
            INSERT INTO audit (Id, Timestamp, Action, ActorUserId, ActorName, TargetUserId, TargetName, Details, IpAddress)
            VALUES (@Id, @Timestamp, @Action, @ActorUserId, @ActorName, @TargetUserId, @TargetName, @Details, @IpAddress);
            """,
            record).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditRecord>> GetAuditAsync(
        int limit,
        int offset,
        string? action = null,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var sql = new StringBuilder("SELECT * FROM audit WHERE 1=1");
        var parameters = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(action))
        {
            sql.Append(" AND Action = @Action");
            parameters.Add("Action", action);
        }

        if (!string.IsNullOrWhiteSpace(userId))
        {
            sql.Append(" AND (ActorUserId = @UserId OR TargetUserId = @UserId)");
            parameters.Add("UserId", userId);
        }

        sql.Append(" ORDER BY Timestamp DESC LIMIT @Limit OFFSET @Offset;");
        parameters.Add("Limit", Math.Clamp(limit, 1, 2000));
        parameters.Add("Offset", Math.Max(0, offset));

        var rows = await connection.QueryAsync<AuditRecord>(sql.ToString(), parameters).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<long> CountAuditAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM audit;").ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetAuditActionsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<string>("SELECT DISTINCT Action FROM audit ORDER BY Action;").ConfigureAwait(false);
        return rows.ToList();
    }

    // ------------------------------------------------------------------ links

    public async Task SetLinkCodeAsync(LinkCodeRecord code, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "DELETE FROM link_codes WHERE JellyfinUserId = @JellyfinUserId AND UsedAt IS NULL;",
            new { code.JellyfinUserId }).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "INSERT INTO link_codes (CodeHash, JellyfinUserId, CreatedAt, ExpiresAt, UsedAt) VALUES (@CodeHash, @JellyfinUserId, @CreatedAt, @ExpiresAt, @UsedAt);",
            code).ConfigureAwait(false);
    }

    public async Task<LinkCodeRecord?> GetLinkCodeAsync(string codeHash, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<LinkCodeRecord>(
            "SELECT * FROM link_codes WHERE CodeHash = @CodeHash;",
            new { CodeHash = codeHash }).ConfigureAwait(false);
    }

    public async Task<LinkedAccountRecord?> GetLinkByJellyfinUserIdAsync(string jellyfinUserId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<LinkedAccountRecord>(
            "SELECT * FROM linked_accounts WHERE JellyfinUserId = @JellyfinUserId;",
            new { JellyfinUserId = jellyfinUserId }).ConfigureAwait(false);
    }

    public async Task<LinkedAccountRecord?> GetLinkByDiscordUserIdAsync(string discordUserId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<LinkedAccountRecord>(
            "SELECT * FROM linked_accounts WHERE DiscordUserId = @DiscordUserId;",
            new { DiscordUserId = discordUserId }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LinkedAccountRecord>> GetLinksAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<LinkedAccountRecord>("SELECT * FROM linked_accounts;").ConfigureAwait(false);
        return rows.ToList();
    }

    /// <summary>
    /// Redeems a link code and stores the Discord mapping atomically.
    /// </summary>
    public async Task<LinkCodeRecord?> ConsumeLinkCodeAsync(
        string codeHash,
        string discordUserId,
        string? discordUsername,
        long now,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var code = await connection.QuerySingleOrDefaultAsync<LinkCodeRecord>(
            "SELECT * FROM link_codes WHERE CodeHash = @CodeHash AND UsedAt IS NULL AND ExpiresAt > @Now;",
            new { CodeHash = codeHash, Now = now },
            transaction).ConfigureAwait(false);

        if (code is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        await connection.ExecuteAsync(
            "UPDATE link_codes SET UsedAt = @Now WHERE CodeHash = @CodeHash;",
            new { Now = now, CodeHash = codeHash },
            transaction).ConfigureAwait(false);

        // A Discord account can only be linked to one Jellyfin account.
        await connection.ExecuteAsync(
            "DELETE FROM linked_accounts WHERE DiscordUserId = @DiscordUserId OR JellyfinUserId = @JellyfinUserId;",
            new { DiscordUserId = discordUserId, JellyfinUserId = code.JellyfinUserId },
            transaction).ConfigureAwait(false);

        await connection.ExecuteAsync(
            "INSERT INTO linked_accounts (JellyfinUserId, DiscordUserId, DiscordUsername, LinkedAt) VALUES (@JellyfinUserId, @DiscordUserId, @DiscordUsername, @Now);",
            new { code.JellyfinUserId, DiscordUserId = discordUserId, DiscordUsername = discordUsername, Now = now },
            transaction).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return code;
    }

    public async Task DeleteLinkAsync(string jellyfinUserId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "DELETE FROM linked_accounts WHERE JellyfinUserId = @JellyfinUserId;",
            new { JellyfinUserId = jellyfinUserId }).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ resets

    public async Task CreateResetTokenAsync(ResetTokenRecord token, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "DELETE FROM reset_tokens WHERE JellyfinUserId = @JellyfinUserId AND UsedAt IS NULL;",
            new { token.JellyfinUserId }).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "INSERT INTO reset_tokens (TokenHash, JellyfinUserId, CreatedByUserId, CreatedAt, ExpiresAt, UsedAt) VALUES (@TokenHash, @JellyfinUserId, @CreatedByUserId, @CreatedAt, @ExpiresAt, @UsedAt);",
            token).ConfigureAwait(false);
    }

    public async Task<ResetTokenRecord?> GetResetTokenAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<ResetTokenRecord>(
            "SELECT * FROM reset_tokens WHERE TokenHash = @TokenHash;",
            new { TokenHash = tokenHash }).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically marks a reset token as used. Returns the token record when it was valid.
    /// </summary>
    public async Task<ResetTokenRecord?> ConsumeResetTokenAsync(string tokenHash, long now, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var token = await connection.QuerySingleOrDefaultAsync<ResetTokenRecord>(
            "SELECT * FROM reset_tokens WHERE TokenHash = @TokenHash AND UsedAt IS NULL AND ExpiresAt > @Now;",
            new { TokenHash = tokenHash, Now = now },
            transaction).ConfigureAwait(false);

        if (token is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        await connection.ExecuteAsync(
            "UPDATE reset_tokens SET UsedAt = @Now, Stage = 'claimed' WHERE TokenHash = @TokenHash;",
            new { Now = now, TokenHash = tokenHash },
            transaction).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return token;
    }

    // ---------------------------------------------------------------- presets

    public async Task<IReadOnlyList<PresetRecord>> GetPresetsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<PresetRecord>(
            "SELECT * FROM presets ORDER BY IsDefault DESC, Name COLLATE NOCASE;").ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<PresetRecord?> GetPresetAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<PresetRecord>(
            "SELECT * FROM presets WHERE Id = @Id;",
            new { Id = id }).ConfigureAwait(false);
    }

    public async Task<PresetRecord?> GetDefaultPresetAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<PresetRecord>(
            "SELECT * FROM presets ORDER BY IsDefault DESC, CreatedAt LIMIT 1;").ConfigureAwait(false);
    }

    public async Task UpsertPresetAsync(PresetRecord preset, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (preset.IsDefault)
        {
            await connection.ExecuteAsync(
                "UPDATE presets SET IsDefault = 0 WHERE Id <> @Id;",
                new { preset.Id },
                transaction).ConfigureAwait(false);
        }

        await connection.ExecuteAsync(
            """
            INSERT INTO presets (Id, Name, Description, JellyfinPolicyJson, SeerrPermissions, IsDefault, CreatedAt, UpdatedAt)
            VALUES (@Id, @Name, @Description, @JellyfinPolicyJson, @SeerrPermissions, @IsDefault, @CreatedAt, @UpdatedAt)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                Description = excluded.Description,
                JellyfinPolicyJson = excluded.JellyfinPolicyJson,
                SeerrPermissions = excluded.SeerrPermissions,
                IsDefault = excluded.IsDefault,
                UpdatedAt = excluded.UpdatedAt;
            """,
            preset,
            transaction).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeletePresetAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync("DELETE FROM presets WHERE Id = @Id;", new { Id = id }).ConfigureAwait(false);
    }

    // ----------------------------------------------------------- user presets

    public async Task SetUserPresetAsync(string jellyfinUserId, string? presetId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        if (presetId is null)
        {
            await connection.ExecuteAsync(
                "DELETE FROM user_presets WHERE JellyfinUserId = @JellyfinUserId;",
                new { JellyfinUserId = jellyfinUserId }).ConfigureAwait(false);
            return;
        }

        await connection.ExecuteAsync(
            """
            INSERT INTO user_presets (JellyfinUserId, PresetId, UpdatedAt) VALUES (@JellyfinUserId, @PresetId, @UpdatedAt)
            ON CONFLICT(JellyfinUserId) DO UPDATE SET PresetId = excluded.PresetId, UpdatedAt = excluded.UpdatedAt;
            """,
            new
            {
                JellyfinUserId = jellyfinUserId,
                PresetId = presetId,
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetUserPresetsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<UserPresetRecord>(
            "SELECT * FROM user_presets;").ConfigureAwait(false);
        return rows.ToDictionary(r => r.JellyfinUserId, r => r.PresetId, StringComparer.OrdinalIgnoreCase);
    }

    // --------------------------------------------------------------- settings

    public async Task<IReadOnlyDictionary<string, string?>> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<SettingRecord>("SELECT * FROM settings;").ConfigureAwait(false);
        return rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);
    }

    public async Task SaveSettingsAsync(IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var pair in values)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO settings (Key, Value) VALUES (@Key, @Value)
                ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
                """,
                new { Key = pair.Key, Value = pair.Value },
                transaction).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ stats

    public async Task<(long Invites, long LinkedAccounts, long AuditEntries, long Presets)> GetStatsAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var invites = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM invites;").ConfigureAwait(false);
        var links = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM linked_accounts;").ConfigureAwait(false);
        var audit = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM audit;").ConfigureAwait(false);
        var presets = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM presets;").ConfigureAwait(false);
        return (invites, links, audit, presets);
    }

    private const string SchemaV1 =
        """
        CREATE TABLE IF NOT EXISTS settings (
            Key TEXT PRIMARY KEY NOT NULL,
            Value TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS presets (
            Id TEXT PRIMARY KEY NOT NULL,
            Name TEXT NOT NULL UNIQUE COLLATE NOCASE,
            Description TEXT NULL,
            JellyfinPolicyJson TEXT NOT NULL DEFAULT '{}',
            SeerrPermissions INTEGER NOT NULL DEFAULT 0,
            IsDefault INTEGER NOT NULL DEFAULT 0,
            CreatedAt INTEGER NOT NULL,
            UpdatedAt INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS user_presets (
            JellyfinUserId TEXT PRIMARY KEY NOT NULL,
            PresetId TEXT NOT NULL REFERENCES presets(Id) ON DELETE CASCADE,
            UpdatedAt INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS invites (
            Id TEXT PRIMARY KEY NOT NULL,
            TokenHash TEXT NOT NULL UNIQUE,
            CreatedByUserId TEXT NOT NULL,
            CreatedByName TEXT NOT NULL,
            PresetId TEXT NULL REFERENCES presets(Id) ON DELETE SET NULL,
            CreatedAt INTEGER NOT NULL,
            ExpiresAt INTEGER NOT NULL,
            MaxUses INTEGER NOT NULL DEFAULT 1,
            UseCount INTEGER NOT NULL DEFAULT 0,
            Revoked INTEGER NOT NULL DEFAULT 0,
            Note TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS invite_uses (
            Id TEXT PRIMARY KEY NOT NULL,
            InviteId TEXT NOT NULL REFERENCES invites(Id) ON DELETE CASCADE,
            UserId TEXT NOT NULL,
            Username TEXT NOT NULL,
            UsedAt INTEGER NOT NULL,
            RemoteIp TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_invite_uses_invite ON invite_uses(InviteId);

        CREATE TABLE IF NOT EXISTS audit (
            Id TEXT PRIMARY KEY NOT NULL,
            Timestamp INTEGER NOT NULL,
            Action TEXT NOT NULL,
            ActorUserId TEXT NOT NULL,
            ActorName TEXT NOT NULL,
            TargetUserId TEXT NULL,
            TargetName TEXT NULL,
            Details TEXT NULL,
            IpAddress TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_audit_timestamp ON audit(Timestamp DESC);
        CREATE INDEX IF NOT EXISTS idx_audit_action ON audit(Action);
        CREATE INDEX IF NOT EXISTS idx_audit_actor ON audit(ActorUserId);
        CREATE INDEX IF NOT EXISTS idx_audit_target ON audit(TargetUserId);

        CREATE TABLE IF NOT EXISTS linked_accounts (
            JellyfinUserId TEXT PRIMARY KEY NOT NULL,
            DiscordUserId TEXT NOT NULL UNIQUE,
            DiscordUsername TEXT NULL,
            LinkedAt INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS link_codes (
            CodeHash TEXT PRIMARY KEY NOT NULL,
            JellyfinUserId TEXT NOT NULL,
            CreatedAt INTEGER NOT NULL,
            ExpiresAt INTEGER NOT NULL,
            UsedAt INTEGER NULL
        );
        CREATE INDEX IF NOT EXISTS idx_link_codes_user ON link_codes(JellyfinUserId);

        CREATE TABLE IF NOT EXISTS reset_tokens (
            TokenHash TEXT PRIMARY KEY NOT NULL,
            JellyfinUserId TEXT NOT NULL,
            CreatedByUserId TEXT NOT NULL,
            CreatedAt INTEGER NOT NULL,
            ExpiresAt INTEGER NOT NULL,
            UsedAt INTEGER NULL
        );
        CREATE INDEX IF NOT EXISTS idx_reset_tokens_user ON reset_tokens(JellyfinUserId);
        """;
}
