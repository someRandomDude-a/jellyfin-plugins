namespace Jellyfin.Plugin.UserManager.Data;

public sealed class InviteRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string TokenHash { get; set; } = string.Empty;

    public string CreatedByUserId { get; set; } = string.Empty;

    public string CreatedByName { get; set; } = string.Empty;

    public string? PresetId { get; set; }

    public long CreatedAt { get; set; }

    public long ExpiresAt { get; set; }

    public int MaxUses { get; set; } = 1;

    public int UseCount { get; set; }

    public bool Revoked { get; set; }

    public string? Note { get; set; }

    public string? PresetName { get; set; }

    public List<InviteUseRecord> Uses { get; set; } = [];

    public bool IsExpired => ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public bool IsExhausted => UseCount >= MaxUses;

    public string Status => Revoked ? "revoked" : IsExpired ? "expired" : IsExhausted ? "used" : "active";
}

public sealed class InviteUseRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string InviteId { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public long UsedAt { get; set; }

    public string? RemoteIp { get; set; }
}

public sealed class AuditRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public long Timestamp { get; set; }

    public string Action { get; set; } = string.Empty;

    public string ActorUserId { get; set; } = string.Empty;

    public string ActorName { get; set; } = string.Empty;

    public string? TargetUserId { get; set; }

    public string? TargetName { get; set; }

    public string? Details { get; set; }

    public string? IpAddress { get; set; }
}

public sealed class LinkedAccountRecord
{
    public string JellyfinUserId { get; set; } = string.Empty;

    public string DiscordUserId { get; set; } = string.Empty;

    public string? DiscordUsername { get; set; }

    public long LinkedAt { get; set; }
}

public sealed class LinkCodeRecord
{
    public string CodeHash { get; set; } = string.Empty;

    public string JellyfinUserId { get; set; } = string.Empty;

    public long CreatedAt { get; set; }

    public long ExpiresAt { get; set; }

    public long? UsedAt { get; set; }
}

public sealed class ResetTokenRecord
{
    public long? SessionsRevokedAt { get; set; }
    public long RevocationAttemptOrder { get; set; }
    public string Stage { get; set; } = "issued";
    public string TokenHash { get; set; } = string.Empty;

    public string JellyfinUserId { get; set; } = string.Empty;

    public string CreatedByUserId { get; set; } = string.Empty;

    public long CreatedAt { get; set; }

    public long ExpiresAt { get; set; }

    public long? UsedAt { get; set; }
}

public sealed class PresetRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string JellyfinPolicyJson { get; set; } = "{}";

    public long SeerrPermissions { get; set; }

    public bool IsDefault { get; set; }

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }
}

public sealed class UserPresetRecord
{
    public string JellyfinUserId { get; set; } = string.Empty;

    public string PresetId { get; set; } = string.Empty;

    public long UpdatedAt { get; set; }
}

public sealed class SettingRecord
{
    public string Key { get; set; } = string.Empty;

    public string? Value { get; set; }
}
