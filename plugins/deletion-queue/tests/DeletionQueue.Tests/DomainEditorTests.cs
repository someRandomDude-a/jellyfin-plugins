using System.Text.Json;
using DeletionQueue.Core;
using Jellyfin.Plugin.DeletionQueue;

namespace DeletionQueue.Tests;

public sealed class DomainEditorTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, HubWire.Json);

    [Fact] public void PagedPolicyIsStableBoundedAndContainsDefaultOverrideEffectiveWithoutEvidence()
    {
        var state = QueueRulesTests.State();
        var accounts = Enumerable.Range(0, 601).Select(_ => new Account(Guid.NewGuid(), false, true)).ToArray();
        state.Policy.Accounts[accounts[0].Id] = ProposalAuthority.Denied;
        var first = Json(HubAdapter.Policy(state, accounts, 0, 100));
        var second = Json(HubAdapter.Policy(state, accounts.Reverse().ToArray(), 100, 100));
        Assert.Equal(601, first.GetProperty("total").GetInt32());
        Assert.Equal(100, first.GetProperty("users").GetArrayLength());
        var ids = first.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("userId").GetGuid()).ToHashSet();
        Assert.DoesNotContain(second.GetProperty("users").EnumerateArray(), u => ids.Contains(u.GetProperty("userId").GetGuid()));
        var user = Json(HubAdapter.UserView(state, accounts[0]));
        Assert.Equal("Denied", user.GetProperty("policy").GetProperty("authority").GetString());
        Assert.True(user.TryGetProperty("defaults", out _)); Assert.True(user.TryGetProperty("effective", out _));
        Assert.DoesNotContain("subscriptions", first.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Throws<QueueException>(() => HubAdapter.Policy(state, accounts, 0, 101));
    }

    [Fact] public void BulkPreviewMatchesNativeApplyWithoutMutatingAndRejectsStaleRevision()
    {
        var state = QueueRulesTests.State(); var admin = new Account(Guid.NewGuid(), true, true); var user = QueueRulesTests.Owner;
        var accounts = new[] { admin, user }; var before = JsonSerializer.Serialize(state);
        var changes = new[] { new HubUserChange(admin.Id, new(ProposalAuthority.Denied)), new HubUserChange(user.Id, new(ProposalAuthority.Denied, new(200, false))) };
        var request = new HubPreviewRequest([admin.Id, user.Id], Revision: HubAdapter.Revision(state), Users: changes);
        var preview = Json(HubAdapter.Preview(state, request, accounts, "fixture", QueueRulesTests.Now));
        Assert.Equal(before, JsonSerializer.Serialize(state));
        Assert.Contains(admin.Id.ToString(), preview.GetProperty("skippedAdmins").GetRawText());
        HubAdapter.ApplyPolicy(state, new(HubAdapter.Revision(state), Guid.NewGuid(), Users: changes), accounts);
        var expected = preview.GetProperty("users").EnumerateArray().Single(u => u.GetProperty("userId").GetGuid() == user.Id).GetProperty("after");
        Assert.Equal(expected.GetRawText(), Json(HubAdapter.UserView(state, user)).GetRawText());
        Assert.Equal("revision_changed", Assert.Throws<QueueException>(() => HubAdapter.Preview(state, request, accounts, "fixture", QueueRulesTests.Now)).Code);
    }

    [Fact] public void AllResetPreviewIsBoundedProtectsAdminsAndKeepsCredentialsEvidenceAndFloorsPrivate()
    {
        var state = QueueRulesTests.State(); var proposal = QueueRulesTests.Propose(state); proposal.NotBefore = QueueRulesTests.Now.AddHours(200); proposal.DelayFloorHours = 200; var floor = proposal.NotBefore;
        state.Seerr.ApiKey = "never-return-key";
        var admin = new Account(Guid.NewGuid(), true, true); state.Policy.Accounts[admin.Id] = ProposalAuthority.AnyAccessibleItem;
        var accounts = Enumerable.Range(0, 550).Select(_ => new Account(Guid.NewGuid(), false, true)).Append(admin).Append(QueueRulesTests.Owner).ToArray();
        var before = JsonSerializer.Serialize(state);
        var preview = Json(HubAdapter.Preview(state, new([], Revision: HubAdapter.Revision(state), ResetScope: "all"), accounts, "fixture", QueueRulesTests.Now));
        Assert.Equal(before, JsonSerializer.Serialize(state)); Assert.True(preview.GetProperty("usersTruncated").GetBoolean()); Assert.Equal(100, preview.GetProperty("users").GetArrayLength());
        foreach (var forbidden in new[] { "never-return-key", "consents", "voters", "requesters", "subscriptions", "fingerprint" }) Assert.DoesNotContain(forbidden, preview.GetRawText(), StringComparison.OrdinalIgnoreCase);
        HubAdapter.Reset(state, new(HubAdapter.Revision(state), Guid.NewGuid(), "all"), accounts);
        Assert.Equal("never-return-key", state.Seerr.ApiKey); Assert.Equal(ProposalAuthority.AnyAccessibleItem, QueueRules.Authority(state, admin.Id)); Assert.True(state.Proposals[0].NotBefore >= floor);
    }

    [Fact] public void PreviewValidationFailureDoesNotPartiallyApplyEarlierUserChanges()
    {
        var state = QueueRulesTests.State(); var user = QueueRulesTests.Owner; var before = JsonSerializer.Serialize(state);
        var request = new HubPreviewRequest([user.Id], Users: [new(user.Id, new(ProposalAuthority.Denied, new(1, true)))]);
        Assert.Throws<QueueException>(() => HubAdapter.Preview(state, request, [user], "fixture", QueueRulesTests.Now));
        Assert.Equal(before, JsonSerializer.Serialize(state));
    }
}
