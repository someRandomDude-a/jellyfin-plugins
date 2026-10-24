using System.Text.Json;
using Jellyfin.Plugin.UserManager.Services;
using MediaBrowser.Model.Users;
using Xunit;

namespace Jellyfin.Plugin.UserManager.Tests;

public sealed class PolicyToolsTests
{
    [Fact]
    public void Deserialize_Null_ReturnsNormalizedPolicy()
    {
        var policy = PolicyTools.Deserialize(null);

        Assert.NotNull(policy.BlockedTags);
        Assert.NotNull(policy.AllowedTags);
        Assert.NotNull(policy.AccessSchedules);
        Assert.NotNull(policy.BlockUnratedItems);
        Assert.NotNull(policy.EnableContentDeletionFromFolders);
        Assert.NotNull(policy.EnabledDevices);
        Assert.NotNull(policy.EnabledChannels);
        Assert.NotNull(policy.EnabledFolders);
        Assert.NotNull(policy.BlockedMediaFolders);
        Assert.NotNull(policy.BlockedChannels);
        Assert.Equal(string.Empty, policy.AuthenticationProviderId);
        Assert.Equal(string.Empty, policy.PasswordResetProviderId);
    }

    [Fact]
    public void Deserialize_InvalidJson_ReturnsSafeDefaults()
    {
        var policy = PolicyTools.Deserialize("{ this is not json");

        Assert.False(policy.IsAdministrator);
        Assert.NotNull(policy.EnabledFolders);
    }

    [Fact]
    public void SerializeDeserialize_RoundTripsCommonFields()
    {
        var folderId = Guid.NewGuid();
        var original = new UserPolicy
        {
            EnableMediaPlayback = true,
            EnableContentDownloading = true,
            EnableAllFolders = false,
            MaxActiveSessions = 3,
            LoginAttemptsBeforeLockout = 5,
            EnabledFolders = [folderId]
        };

        var clone = PolicyTools.Deserialize(PolicyTools.Serialize(original));

        Assert.True(clone.EnableMediaPlayback);
        Assert.True(clone.EnableContentDownloading);
        Assert.False(clone.EnableAllFolders);
        Assert.Equal(3, clone.MaxActiveSessions);
        Assert.Equal(5, clone.LoginAttemptsBeforeLockout);
        Assert.Equal(new[] { folderId }, clone.EnabledFolders);
    }

    [Fact]
    public void Serialize_UsesCamelCasePropertyNames()
    {
        var json = PolicyTools.Serialize(new UserPolicy { EnableMediaPlayback = true });

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("enableMediaPlayback", out var value));
        Assert.True(value.GetBoolean());
    }

    [Fact]
    public void ApplyPreset_CopiesPermissionsButPreservesIdentityAndState()
    {
        var target = new UserPolicy
        {
            IsAdministrator = true,
            IsDisabled = true,
            AuthenticationProviderId = "auth-provider",
            PasswordResetProviderId = "reset-provider",
            InvalidLoginAttemptCount = 2,
            LoginAttemptsBeforeLockout = 9
        };

        var preset = new UserPolicy
        {
            IsAdministrator = false,
            IsDisabled = false,
            EnableMediaPlayback = true,
            EnableContentDownloading = true,
            AuthenticationProviderId = "preset-provider",
            PasswordResetProviderId = "preset-reset",
            InvalidLoginAttemptCount = 0,
            LoginAttemptsBeforeLockout = -1
        };

        var result = PolicyTools.ApplyPreset(target, preset);

        Assert.True(result.IsAdministrator);
        Assert.True(result.IsDisabled);
        Assert.Equal("auth-provider", result.AuthenticationProviderId);
        Assert.Equal("reset-provider", result.PasswordResetProviderId);
        Assert.Equal(2, result.InvalidLoginAttemptCount);
        Assert.Equal(9, result.LoginAttemptsBeforeLockout);
        Assert.True(result.EnableMediaPlayback);
        Assert.True(result.EnableContentDownloading);
    }

    [Fact]
    public void ApplyPreset_CanAllowAdministratorDemotion()
    {
        var target = new UserPolicy { IsAdministrator = true };
        var preset = new UserPolicy { IsAdministrator = false };

        var result = PolicyTools.ApplyPreset(target, preset, preserveAdministrator: false);

        Assert.False(result.IsAdministrator);
    }

    [Fact]
    public void Clone_DoesNotShareCollectionInstances()
    {
        var policy = PolicyTools.Deserialize(null);
        policy.EnabledFolders = [Guid.NewGuid()];

        var clone = PolicyTools.Clone(policy);
        clone.EnabledFolders = [.. clone.EnabledFolders, Guid.NewGuid()];

        Assert.Single(policy.EnabledFolders);
        Assert.Equal(2, clone.EnabledFolders.Length);
    }
}

public sealed class SeerrPermissionsTests
{
    [Fact]
    public void DefaultRequester_DescribesRequestPermissions()
    {
        var names = SeerrPermissions.Describe(SeerrPermissions.DefaultRequester);

        Assert.Contains("Request", names);
        Assert.Contains("Request movies", names);
        Assert.Contains("Request TV", names);
        Assert.DoesNotContain("Admin", names);
    }

    [Fact]
    public void Describe_None_ReturnsNoneLabel()
    {
        Assert.Equal(new[] { "None" }, SeerrPermissions.Describe(0));
    }

    [Fact]
    public void Catalog_ValuesAreUniquePowersOfTwo()
    {
        var values = SeerrPermissions.Catalog.Select(item => item.Value).ToList();

        Assert.Equal(values.Count, values.Distinct().Count());
        Assert.All(values, value => Assert.True(value > 0 && (value & (value - 1)) == 0));
    }

    [Fact]
    public void Describe_IgnoresUnknownBits()
    {
        var names = SeerrPermissions.Describe(1L << 40);

        Assert.Equal(new[] { "None" }, names);
    }
}
