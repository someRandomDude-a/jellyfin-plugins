using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Jellyfin.Plugin.PrivateSyncPlay.Tests;

public sealed class HubAdapterTests
{
    private Configuration _config = new();
    private readonly Guid _admin = Guid.NewGuid(), _user = Guid.NewGuid();
    private readonly PartyStore _store = new(TimeProvider.System);
    private int _saves;
    private HubPolicyService Service() => new(() => _config, c => { c.Validate();_config = HubWire.Clone(c);_saves++; },
        () => [new(_admin, "Admin", true), new(_user, "Member", false)], _store);
    private JsonObject Request(object value) => (JsonObject)HubWire.Node(value);

    [Fact] public void PerUserPolicyUsesNativeEnumSemanticsAndDurableReceiptReplaysWithoutAnotherSave()
    {
        var service = Service();var request = Request(new { revision = HubWire.Revision(_config), commandId = Guid.NewGuid(), policy = new { defaultMode = "Private", createPublic = "Deny", createPrivate = "Allow", guestInvites = "Allow" } });
        var result = service.Execute(_admin, "user-put", request, user: _user);
        Assert.False(_config.CanCreate(_user, GroupMode.Public));Assert.Equal(GroupMode.Private, _config.DefaultFor(_user));
        Assert.False(_config.CanInviteGuests(_user)); // A stored Allow cannot bypass the master switch.
        _config = HubWire.Clone(_config); // Receipt survives configuration serialization/reload.
        Assert.True(JsonNode.DeepEquals(result, Service().Execute(_admin, "user-put", request, user: _user)));Assert.Equal(1, _saves);
        request["policy"]!["createPublic"] = "Allow";
        Assert.Equal("command_conflict", Assert.Throws<HubAdapterException>(() => service.Execute(_admin, "user-put", request, user: _user)).Code);
    }
    [Fact] public void StaleAndNonAdminCommandsCannotMutateAndUnknownFieldsAreRejected()
    {
        var service = Service();var request = Request(new { revision = "old", commandId = Guid.NewGuid(), settings = new { maxViewers = 20 } });
        Assert.Equal("revision_changed", Assert.Throws<HubAdapterException>(() => service.Execute(_admin, "settings", request)).Code);
        request["revision"] = HubWire.Revision(_config);
        Assert.Equal(403, Assert.Throws<HubAdapterException>(() => service.Execute(_user, "settings", request)).Status);
        request["stepUpVerified"] = true;
        Assert.Equal("invalid_request", Assert.Throws<HubAdapterException>(() => service.Execute(_admin, "settings", request)).Code);Assert.Equal(0, _saves);
    }
    [Fact] public void BulkSkipsAdministratorsAndGlobalDefaultsPreserveAdminSoftPolicy()
    {
        _config.EnableGuestInvites = true;
        var service = Service();
        var request = Request(new { revision = HubWire.Revision(_config), commandId = Guid.NewGuid(), defaults = new { defaultGuestPermission = true, defaultMode = "Private" },
            users = new[] { new { userId = _admin, policy = new { createPublic = "Deny" } }, new { userId = _user, policy = new { createPublic = "Deny" } } } });
        var result = service.Execute(_admin, "policy", request);
        Assert.Contains(_admin.ToString(), result["skippedAdmins"]!.ToJsonString());
        Assert.False(_config.CanInviteGuests(_admin));Assert.True(_config.CanInviteGuests(_user));
        Assert.Equal(GroupMode.Public, _config.DefaultFor(_admin));Assert.Equal(GroupMode.Private, _config.DefaultFor(_user));
        Assert.True(_config.CanCreate(_admin, GroupMode.Public));Assert.False(_config.CanCreate(_user, GroupMode.Public));
    }
    [Theory] [InlineData("user-put")] [InlineData("user-delete")]
    public void DirectAdministratorPolicyWritesAreForbidden(string operation)
    {
        var request = operation == "user-put" ? Request(new { revision = HubWire.Revision(_config), commandId = Guid.NewGuid(), policy = new { createPublic = "Deny" } }) : new JsonObject();
        var error = Assert.Throws<HubAdapterException>(() => Service().Execute(_admin, operation, request, HubWire.Revision(_config), operation == "user-delete" ? Guid.NewGuid().ToString() : null, _admin));
        Assert.Equal("administrator_protected", error.Code);Assert.Equal(0, _saves);
    }
    [Fact] public void PreviewHasNoWritesAndResetCannotImplicitlyRevokeLiveGuestLinks()
    {
        _config.EnableGuestInvites = true;_config.DefaultGuestPermission = true;
        var party = _store.Create(Guid.NewGuid(), _user, "host", GroupMode.Private, 4);
        var invite = _store.Issue(party, _user, true, 2, null, _config).Token;
        var credential = _store.RedeemGuest(invite, null, _config);
        var preview = Service().Execute(_admin, "preview", Request(new { revision = HubWire.Revision(_config), resetScope = "all" }), preview: true);
        Assert.Contains("would be revoked", preview["warnings"]!.ToJsonString());Assert.Equal(0, _saves);
        var reset = Request(new { revision = HubWire.Revision(_config), commandId = Guid.NewGuid(), scope = "all" });
        Assert.Equal("invalid_request", Assert.Throws<HubAdapterException>(() => Service().Execute(_admin, "reset", reset)).Code);
        Assert.Same(party, _store.GuestParty(credential, _config));Assert.Equal(0, _saves);
    }
    [Fact] public void UserResetAndPaginationDoNotTouchAdminOverridesOrParties()
    {
        _config.Users = [new() { UserId = _admin, CreatePublic = PermissionOverride.Deny }, new() { UserId = _user, CreatePrivate = PermissionOverride.Deny }];
        var party = _store.Create(Guid.NewGuid(), _user, "host", GroupMode.Public, 4);
        var result = Service().Execute(_admin, "reset", Request(new { revision = HubWire.Revision(_config), commandId = Guid.NewGuid(), scope = "users", userIds = new[] { _user, _admin } }));
        Assert.Single(_config.Users);Assert.Equal(_admin, _config.Users[0].UserId);Assert.Same(party, _store.Require(party.Id));
        Assert.Contains(_admin.ToString(), result["skippedAdmins"]!.ToJsonString());
        var page = HubWire.Node(Service().Policy(0, 1));Assert.Equal(2, page["total"]!.GetValue<int>());Assert.Single(page["users"]!.AsArray());
    }
    [Theory] [InlineData("Allow", "true")] [InlineData("Allow", "null")] [InlineData("Bogus", "\"Public\"")]
    public void InvalidWireTypesDoNotSilentlyBecomePermissions(string permission, string mode)
    {
        var request = Request(new { revision = HubWire.Revision(_config), commandId = Guid.NewGuid(), policy = new { createPublic = permission } });
        request["policy"]!["defaultMode"] = JsonNode.Parse(mode);
        if (mode == "null" && permission == "Allow")
        {
            Service().Execute(_admin, "user-put", request, user: _user);
            Assert.Null(_config.Users.Single().DefaultMode);Assert.Equal(_config.DefaultMode, _config.DefaultFor(_user));return;
        }
        Assert.ThrowsAny<Exception>(() => Service().Execute(_admin, "user-put", request, user: _user));Assert.Equal(0, _saves);
    }
    [Fact] public void ConcurrentWritersCannotBothCommitTheSameRevision()
    {
        var service = Service();var revision = HubWire.Revision(_config);var committed = 0;
        Parallel.For(0, 20, _ =>
        {
            try { service.Execute(_admin, "settings", Request(new { revision, commandId = Guid.NewGuid(), settings = new { maxViewers = 20 } }));Interlocked.Increment(ref committed); }
            catch (HubAdapterException e) { Assert.Equal("revision_changed", e.Code); }
        });
        Assert.Equal(1, committed);Assert.Equal(1, _saves);
    }
    [Fact] public void DemotedAdministratorCannotReplayAnOldSuccessfulCommand()
    {
        var administrator = true;
        var service = new HubPolicyService(() => _config, c => _config = c, () => [new(_admin, "Admin", administrator)], _store);
        var request = Request(new { revision = HubWire.Revision(_config), commandId = Guid.NewGuid(), settings = new { maxViewers = 20 } });
        service.Execute(_admin, "settings", request);administrator = false;
        Assert.Equal(403, Assert.Throws<HubAdapterException>(() => service.Execute(_admin, "settings", request)).Status);
    }

    [Fact] public void LargeResetHasBoundedDetailAndNoPartyParticipantsOrGuestCredentials()
    {
        var members = Enumerable.Range(0, 600).Select(n => new HubUser(Guid.NewGuid(), "Member " + n, false)).Append(new(_admin, "Admin", true)).ToArray();
        _config.Users = members.Where(u => !u.Administrator).Select(u => new UserSettings { UserId = u.Id, CreatePublic = PermissionOverride.Deny }).ToList();
        var service = new HubPolicyService(() => _config, c => { _config = c; _saves++; }, () => members, _store);
        var first = HubWire.Node(service.Policy(0, 100)); var second = HubWire.Node(service.Policy(100, 100));
        Assert.Equal(601, first["total"]!.GetValue<int>());
        Assert.Empty(first["users"]!.AsArray().Select(u => u!["userId"]!.ToString()).Intersect(second["users"]!.AsArray().Select(u => u!["userId"]!.ToString())));
        var preview = service.Execute(_admin, "preview", Request(new { revision = HubWire.Revision(_config), resetScope = "all" }), preview: true);
        Assert.True(preview["usersTruncated"]!.GetValue<bool>()); Assert.Equal(50, preview["users"]!.AsArray().Count); Assert.Equal(0, _saves);
        var result = service.Execute(_admin, "reset", Request(new { revision = HubWire.Revision(_config), commandId = Guid.NewGuid(), scope = "all" }));
        Assert.Equal(1, _saves); Assert.True(result.ToJsonString().Length < 131072);
        foreach (var forbidden in new[] { "participant", "inviteToken", "cookie", "guestSession", "deviceId" }) Assert.DoesNotContain(forbidden, result.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact] public void MixedValidAndUnknownTargetsFailAtomicallyWithoutSavingEarlierChanges()
    {
        var before = HubWire.Revision(_config); var service = Service();
        var request = Request(new { revision = before, commandId = Guid.NewGuid(), users = new[] { new { userId = _user, policy = new { createPublic = "Deny" } }, new { userId = Guid.NewGuid(), policy = new { createPublic = "Deny" } } } });
        Assert.Equal("invalid_request", Assert.Throws<HubAdapterException>(() => service.Execute(_admin, "policy", request)).Code);
        Assert.Equal(0, _saves); Assert.Equal(before, HubWire.Revision(_config)); Assert.Empty(_config.Users);
    }
}
