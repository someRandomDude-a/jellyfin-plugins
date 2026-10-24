using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Model.Users;

namespace Jellyfin.Plugin.UserManager.Services;

/// <summary>
/// Serialization and normalization helpers for Jellyfin user policies.
/// </summary>
public static class PolicyTools
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static string Serialize(UserPolicy policy) => JsonSerializer.Serialize(policy, JsonOptions);

    public static UserPolicy Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Normalize(new UserPolicy());
        }

        try
        {
            var policy = JsonSerializer.Deserialize<UserPolicy>(json, JsonOptions) ?? new UserPolicy();
            return Normalize(policy);
        }
        catch (JsonException)
        {
            return Normalize(new UserPolicy());
        }
    }

    public static UserPolicy Clone(UserPolicy policy) => Deserialize(Serialize(policy));

    /// <summary>
    /// Fills null collections so Jellyfin's UpdatePolicyAsync never dereferences null.
    /// </summary>
    public static UserPolicy Normalize(UserPolicy policy)
    {
        policy.BlockedTags ??= [];
        policy.AllowedTags ??= [];
        policy.AccessSchedules ??= [];
        policy.BlockUnratedItems ??= [];
        policy.EnableContentDeletionFromFolders ??= [];
        policy.EnabledDevices ??= [];
        policy.EnabledChannels ??= [];
        policy.EnabledFolders ??= [];
        policy.BlockedMediaFolders ??= [];
        policy.BlockedChannels ??= [];
        policy.AuthenticationProviderId ??= string.Empty;
        policy.PasswordResetProviderId ??= string.Empty;
        return policy;
    }

    /// <summary>
    /// Applies a preset policy while preserving identity, provider and account-state fields.
    /// </summary>
    public static UserPolicy ApplyPreset(UserPolicy target, UserPolicy preset, bool preserveAdministrator = true)
    {
        var result = Clone(preset);
        Normalize(result);
        result.AuthenticationProviderId = target.AuthenticationProviderId;
        result.PasswordResetProviderId = target.PasswordResetProviderId;
        result.IsAdministrator = preserveAdministrator && target.IsAdministrator;
        result.IsDisabled = target.IsDisabled;
        result.InvalidLoginAttemptCount = target.InvalidLoginAttemptCount;
        result.LoginAttemptsBeforeLockout = target.LoginAttemptsBeforeLockout;
        return result;
    }
}
