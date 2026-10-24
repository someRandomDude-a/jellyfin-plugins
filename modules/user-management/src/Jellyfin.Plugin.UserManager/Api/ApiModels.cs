using System.Text.Json;

namespace Jellyfin.Plugin.UserManager.Api;

public sealed class CreateUserInput
{
    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string? PresetId { get; set; }

    public bool IsAdministrator { get; set; }

    public bool SyncSeerr { get; set; } = true;
}

public sealed class UpdateUserInput
{
    public bool? IsDisabled { get; set; }

    public bool? IsAdministrator { get; set; }

    public string? PresetId { get; set; }

    public bool SyncSeerr { get; set; } = true;
}

public sealed class ResetPasswordInput
{
    public string? NewPassword { get; set; }

    public bool SendDiscordLink { get; set; } = true;
}

public sealed class ChangePasswordInput
{
    public string CurrentPassword { get; set; } = string.Empty;

    public string NewPassword { get; set; } = string.Empty;
}

public sealed class PresetInput
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public JsonElement? JellyfinPolicy { get; set; }

    public long SeerrPermissions { get; set; }

    public bool IsDefault { get; set; }
}

public sealed class ImportPresetInput
{
    public string? Name { get; set; }

    public string? Description { get; set; }
}

public sealed class CreateInviteInput
{
    public string? PresetId { get; set; }

    public int? MaxUses { get; set; }

    public int? ExpiresInDays { get; set; }

    public string? Note { get; set; }
}

public sealed class SettingsInput
{
    public string? SeerrUrl { get; set; }

    public string? SeerrApiKey { get; set; }

    public string? DiscordBotToken { get; set; }

    public string? DiscordGuildId { get; set; }

    public string? DiscordAdminWebhookUrl { get; set; }

    public string? DiscordAdminRoleIds { get; set; }

    public string? PublicBaseUrl { get; set; }

    public int? InviteDefaultExpiryDays { get; set; }

    public int? InviteDefaultMaxUses { get; set; }

    public bool? AllowPublicInviteRequests { get; set; }

    public bool? NotifyAdminsOnJoin { get; set; }

    public bool? NotifyAdminsOnPasswordReset { get; set; }

    public bool? NotifyAdminsOnInviteUsed { get; set; }

    public bool? SyncSeerrOnCreate { get; set; }

    public bool? SyncSeerrOnPresetApply { get; set; }
}

public sealed class RegisterInput
{
    public string Token { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}

public sealed class ForgotPasswordInput
{
    public string Username { get; set; } = string.Empty;
}

public sealed class ResetWithTokenInput
{
    public string Token { get; set; } = string.Empty;

    public string NewPassword { get; set; } = string.Empty;
}

public sealed class InviteRequestInput
{
    public string Name { get; set; } = string.Empty;

    public string Contact { get; set; } = string.Empty;

    public string? Message { get; set; }
}

public sealed class ValidateResetInput
{
    public string Token { get; set; } = string.Empty;
}

public sealed class SettingsDto
{
    public string? SeerrUrl { get; set; }

    public bool SeerrApiKeySet { get; set; }

    public string? DiscordGuildId { get; set; }

    public bool DiscordBotTokenSet { get; set; }

    public bool DiscordAdminWebhookSet { get; set; }

    public string? DiscordAdminRoleIds { get; set; }

    public string? PublicBaseUrl { get; set; }

    public int InviteDefaultExpiryDays { get; set; }

    public int InviteDefaultMaxUses { get; set; }

    public bool AllowPublicInviteRequests { get; set; }

    public bool NotifyAdminsOnJoin { get; set; }

    public bool NotifyAdminsOnPasswordReset { get; set; }

    public bool NotifyAdminsOnInviteUsed { get; set; }

    public bool SyncSeerrOnCreate { get; set; }

    public bool SyncSeerrOnPresetApply { get; set; }
}

public sealed class PresetDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public JsonElement JellyfinPolicy { get; set; }

    public long SeerrPermissions { get; set; }

    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class UserSummaryDto
{
    public string Id { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public bool IsAdministrator { get; set; }

    public bool IsDisabled { get; set; }

    public bool IsHidden { get; set; }

    public DateTimeOffset? LastLogin { get; set; }

    public DateTimeOffset? LastActivity { get; set; }

    public string? DiscordUserId { get; set; }

    public string? DiscordUsername { get; set; }

    public DateTimeOffset? DiscordLinkedAt { get; set; }

    public int? SeerrUserId { get; set; }

    public long? SeerrPermissions { get; set; }

    public string? PresetId { get; set; }

    public string? PresetName { get; set; }
}

public sealed class UsersResponse
{
    public List<UserSummaryDto> Users { get; set; } = [];

    public string? SeerrError { get; set; }
}

public sealed class UserDetailDto
{
    public UserSummaryDto Summary { get; set; } = new();

    public JsonElement Policy { get; set; }

    public List<string> SeerrPermissionNames { get; set; } = [];
}

public sealed class InviteUseDto
{
    public string UserId { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public DateTimeOffset UsedAt { get; set; }

    public string? RemoteIp { get; set; }
}

public sealed class InviteDto
{
    public string Id { get; set; } = string.Empty;

    public string CreatedByName { get; set; } = string.Empty;

    public string? PresetId { get; set; }

    public string? PresetName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public int MaxUses { get; set; }

    public int UseCount { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? Note { get; set; }

    public List<InviteUseDto> Uses { get; set; } = [];
}

public sealed class InviteCreatedDto
{
    public InviteDto Invite { get; set; } = new();

    public string Url { get; set; } = string.Empty;
}

public sealed class AuditEntryDto
{
    public string Id { get; set; } = string.Empty;

    public DateTimeOffset Timestamp { get; set; }

    public string Action { get; set; } = string.Empty;

    public string ActorUserId { get; set; } = string.Empty;

    public string ActorName { get; set; } = string.Empty;

    public string? TargetUserId { get; set; }

    public string? TargetName { get; set; }

    public string? Details { get; set; }

    public string? IpAddress { get; set; }
}

public sealed class AuditPageDto
{
    public List<AuditEntryDto> Entries { get; set; } = [];

    public long Total { get; set; }

    public List<string> Actions { get; set; } = [];
}

public sealed class PublicInviteInfoDto
{
    public bool Valid { get; set; }

    public string? Reason { get; set; }

    public string? PresetName { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public int RemainingUses { get; set; }
}

public sealed class RegisterResultDto
{
    public Guid UserId { get; set; }

    public string Username { get; set; } = string.Empty;

    public string LoginUrl { get; set; } = string.Empty;

    public bool SeerrSynced { get; set; }

    public string? SeerrWarning { get; set; }
}

public sealed class LinkCodeDto
{
    public string Code { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }

    public string Instructions { get; set; } = string.Empty;
}

public sealed class MeDto
{
    public string Id { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public bool IsAdministrator { get; set; }

    public bool IsDisabled { get; set; }

    public string? DiscordUserId { get; set; }

    public string? DiscordUsername { get; set; }

    public bool SeerrConfigured { get; set; }

    public bool DiscordConfigured { get; set; }

    public string? PublicBaseUrl { get; set; }
}

public sealed class StatusDto
{
    public string Version { get; set; } = string.Empty;

    public bool FileTransformationRegistered { get; set; }

    public string? FileTransformationError { get; set; }

    public DateTimeOffset? FileTransformationLastAttempt { get; set; }

    public bool DiscordConnected { get; set; }

    public bool SeerrConfigured { get; set; }

    public bool DiscordConfigured { get; set; }

    public string? PublicBaseUrl { get; set; }

    public long Invites { get; set; }

    public long LinkedAccounts { get; set; }

    public long AuditEntries { get; set; }

    public long Presets { get; set; }
}
