using System.Text.Json;
using Jellyfin.Plugin.UserManager.Data;
using Jellyfin.Plugin.UserManager.Services;
using MediaBrowser.Model.Users;

namespace Jellyfin.Plugin.UserManager.Api;

public static class ApiMapping
{
    public static PresetDto ToDto(PresetRecord preset) => new()
    {
        Id = preset.Id,
        Name = preset.Name,
        Description = preset.Description,
        JellyfinPolicy = JsonSerializer.SerializeToElement(
            PolicyTools.Deserialize(preset.JellyfinPolicyJson),
            PolicyTools.JsonOptions),
        SeerrPermissions = preset.SeerrPermissions,
        IsDefault = preset.IsDefault,
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(preset.CreatedAt),
        UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(preset.UpdatedAt)
    };

    public static InviteDto ToDto(InviteRecord invite) => new()
    {
        Id = invite.Id,
        CreatedByName = invite.CreatedByName,
        PresetId = invite.PresetId,
        PresetName = invite.PresetName,
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(invite.CreatedAt),
        ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(invite.ExpiresAt),
        MaxUses = invite.MaxUses,
        UseCount = invite.UseCount,
        Status = invite.Status,
        Note = invite.Note,
        Uses = invite.Uses.Select(u => new InviteUseDto
        {
            UserId = u.UserId,
            Username = u.Username,
            UsedAt = DateTimeOffset.FromUnixTimeMilliseconds(u.UsedAt),
            RemoteIp = u.RemoteIp
        }).ToList()
    };

    public static AuditEntryDto ToDto(AuditRecord entry) => new()
    {
        Id = entry.Id,
        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(entry.Timestamp),
        Action = entry.Action,
        ActorUserId = entry.ActorUserId,
        ActorName = entry.ActorName,
        TargetUserId = entry.TargetUserId,
        TargetName = entry.TargetName,
        Details = entry.Details,
        IpAddress = entry.IpAddress
    };

    public static UserSummaryDto ToSummary(
        MediaBrowser.Model.Dto.UserDto user,
        LinkedAccountRecord? link,
        SeerrUser? seerr,
        string? presetId,
        string? presetName)
    {
        return new UserSummaryDto
        {
            Id = user.Id.ToString("N"),
            Username = user.Name,
            IsAdministrator = user.Policy.IsAdministrator,
            IsDisabled = user.Policy.IsDisabled,
            IsHidden = user.Policy.IsHidden,
            LastLogin = user.LastLoginDate,
            LastActivity = user.LastActivityDate,
            DiscordUserId = link?.DiscordUserId,
            DiscordUsername = link?.DiscordUsername,
            DiscordLinkedAt = link is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(link.LinkedAt),
            SeerrUserId = seerr?.Id,
            SeerrPermissions = seerr?.Permissions,
            PresetId = presetId,
            PresetName = presetName
        };
    }
}
