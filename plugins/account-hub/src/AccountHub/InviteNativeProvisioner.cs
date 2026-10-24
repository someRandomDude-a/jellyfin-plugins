using System.Text.Json;
using System.Text.RegularExpressions;
using AccountHub.Core;
using AccountHub.Api;
using Jellyfin.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;

namespace AccountHub;

/// <summary>Pinned 12.2 native schema adapter. CreateUserAsync cannot safely create an incomplete account.</summary>
public sealed class InviteNativeProvisioner(IUserManager users, MediaBrowser.Controller.Events.IEventManager events, AccountHub.Groups.GroupPolicyService groups, NativeUserWrites writes) : IInviteNativeProvisioner
{
    public async Task CreateDisabledAsync(Guid userId, string username, CancellationToken ct)
    {
        if (username is "." or ".." || !Regex.IsMatch(username, @"^(?!\s)[\w\ \-'._@+]+(?<!\s)$", RegexOptions.None, TimeSpan.FromMilliseconds(100))) throw new HubException("invalid_request", "Invalid Jellyfin username");
        var auth = users.GetAuthenticationProviders().SingleOrDefault(x => x.Id == "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider")?.Id;
        var reset = users.GetPasswordResetProviders().SingleOrDefault(x => x.Id == "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider")?.Id;
        if (auth is null || reset is null) throw new HubException("unavailable", "Native local authentication is unavailable", true);
        var user = await writes.TransactionAsync(async (db, token) =>
        {
        if (await db.Users.GroupBy(x => x.InternalId).AnyAsync(g => g.Count() > 1, token))
            throw new HubException("duplicate_native_internal_ids", "Native internal ID duplicates require reviewed offline remediation");
        // Never adopt a same-name account, and never recreate an ID on an ambiguous retry.
        if (await db.Users.AnyAsync(x => x.Id == userId || x.NormalizedUsername == username.ToUpperInvariant(), ct)) throw new HubException("username_unavailable", "Username unavailable");
        var internalId = checked((await db.Users.Select(x => (long?)x.InternalId).MaxAsync(ct) ?? 0) + 1);
        var created = new User(username, auth, reset) { Id = userId, InternalId = internalId };
        created.AddDefaultPermissions(); created.AddDefaultPreferences();
        created.SetPermission(PermissionKind.IsDisabled, true);
        created.SetPermission(PermissionKind.IsAdministrator, false);
        created.SetPermission(PermissionKind.IsHidden, true);
        db.Users.Add(created);
        return created;
        }, ct, creation: true);
        await events.PublishAsync(new Jellyfin.Data.Events.Users.UserCreatedEventArgs(user));
    }
    public static void RequireSupportedDatabase(Version? version, string? provider)
    {
        // 12.3 database/user sources are blob-identical to the reviewed 12.2
        // contract; see docs/native-database-12.3-review.md. No version ranges.
        if ((version != new Version(12, 2, 0, 0) && version != new Version(12, 3, 0, 0)) || provider != "Microsoft.EntityFrameworkCore.Sqlite")
            throw new HubException("native_database_unsupported", "Disabled-first onboarding requires the reviewed Jellyfin 12.2 SQLite schema", true);
    }
    private User Disabled(Guid id)
    {
        var user = users.GetUserById(id) ?? throw new HubException("remediation_pending", "Incomplete native user missing");
        if (!user.HasPermission(PermissionKind.IsDisabled) || user.HasPermission(PermissionKind.IsAdministrator)) throw new HubException("administrator_protected", "Incomplete account was modified outside onboarding");
        return user;
    }
    public async Task SetChosenPasswordAsync(Guid userId, string password, CancellationToken ct)
    {
        await writes.SerializeAsync(async token => { token.ThrowIfCancellationRequested(); _ = Disabled(userId); await users.ChangePassword(userId, password); return true; }, ct);
    }
    public async Task ApplyPolicyAsync(Guid userId, string policyJson, string? groupId, CancellationToken ct)
    {
        await writes.ApplyManagedAsync(userId, JsonSerializer.Deserialize<JsonElement>(policyJson), requireDisabled: true, enable: false, ct);
        if (groupId is not null)
        {
            if (!Guid.TryParse(groupId, out var id)) throw new HubException("remediation_pending", "Invite group is invalid");
            var group = await groups.GroupAsync(id, ct);
            var assignment = await groups.DriftAsync(userId, ct);
            if (assignment.Revision == 0) await groups.AssignAsync(userId, 0, id, group.Revision, JsonSerializer.Deserialize<JsonElement>("{}"), ct);
            else if (assignment.GroupId != id) throw new HubException("remediation_pending", "Incomplete account assignment changed outside onboarding");
        }
    }
    public async Task EnableAsync(Guid userId, CancellationToken ct)
    {
        await writes.ApplyManagedAsync(userId, JsonSerializer.Deserialize<JsonElement>("{}"), requireDisabled: true, enable: true, ct);
    }
    public Task<bool> IsReadyAsync(Guid userId, CancellationToken ct)
    {
        var user = users.GetUserById(userId);
        return Task.FromResult(user is not null && !user.HasPermission(PermissionKind.IsDisabled) && !user.HasPermission(PermissionKind.IsAdministrator) && !string.IsNullOrEmpty(user.Password));
    }
    public Task<bool> IsIncompleteAsync(Guid userId, string username, CancellationToken ct)
    {
        var user = users.GetUserById(userId);
        return Task.FromResult(user is not null && user.Username == username && user.HasPermission(PermissionKind.IsDisabled) && !user.HasPermission(PermissionKind.IsAdministrator));
    }
}
