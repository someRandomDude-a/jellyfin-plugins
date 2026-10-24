using AccountHub.Api;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AccountHub.Tests;

public sealed class InviteRouteTests
{
    [Fact]
    public void InviteCommandsDoNotUseMvcReservedActionRouteValue()
    {
        var method = typeof(InviteOnboardingController).GetMethod(nameof(InviteOnboardingController.Change))!;
        var route = Assert.Single(method.GetCustomAttributes(typeof(HttpPostAttribute), false).Cast<HttpPostAttribute>());
        Assert.Equal("api/v1/invites/{id:guid}/{operation}", route.Template);
        Assert.Contains(method.GetParameters(), parameter => parameter.Name == "operation");
    }
}
