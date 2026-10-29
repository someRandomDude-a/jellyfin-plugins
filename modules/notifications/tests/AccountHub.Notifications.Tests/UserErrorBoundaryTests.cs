using AccountHub.Api;
using Xunit;

namespace AccountHub.Notifications.Tests;

public sealed class UserErrorBoundaryTests
{
    [Theory]
    [InlineData("invalid_username",400,"username")]
    [InlineData("forbidden",403,"authorization")]
    [InlineData("step_up_required",403,"authentication")]
    [InlineData("SeerrNotConfigured",409,"unavailable")]
    [InlineData("NotificationsRequired",409,"unavailable")]
    [InlineData("TargetEditionUnverified",409,"action_unavailable")]
    [InlineData("remediation_pending",409,"uncertain")]
    [InlineData("outcome_uncertain",500,"uncertain")]
    [InlineData("link_expired",400,"expired")]
    [InlineData("rate_limited",429,"rate_limited")]
    [InlineData("RevisionChanged",409,"changed")]
    public void UsersGetOnlyTheSafeNatureOfTheFailure(string code,int status,string nature)
    {
        Assert.Equal(nature,UserErrors.Nature(code,status));var message=UserErrors.Message(code,status);
        Assert.DoesNotContain(code,message,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("Seerr",message);Assert.DoesNotContain("stack",message,StringComparison.OrdinalIgnoreCase);
    }
    [Fact] public void UnknownTechnicalCodesAreNeverEchoedToUsers()
    {
        const string code="provider-secret-or-internal-sql-value";Assert.DoesNotContain(code,UserErrors.Message(code,500));Assert.Equal("unavailable",UserErrors.Nature(code,500));
    }
}
