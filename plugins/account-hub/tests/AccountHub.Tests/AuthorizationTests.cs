using AccountHub.Core;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AccountHub.Tests;

public sealed class AuthorizationTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ApiKeysAndUnauthenticatedCallersCannotUseAdminReadApi(bool authenticated, bool apiKey)
    {
        await using var f = new StoreFixture();
        var controller = new AccountHub.Api.AdminController(f.Store, new AuthorizationStub(new()
        { IsAuthenticated = authenticated, IsApiKey = apiKey }), new HubWorkerHealth())
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        var result = Assert.IsType<ContentResult>(await controller.Health());
        Assert.Equal(403, result.StatusCode);
        Assert.DoesNotContain("storage_recovery_required", result.Content!);
    }

    [Fact]
    public async Task HealthDistinguishesAvailableDatabaseFromDegradedBackgroundWork()
    {
        await using var f = new StoreFixture();
        await f.InitializeAsync();
        var workers = new HubWorkerHealth();
        workers.Set("notifications", "unavailable");
        workers.Set("lifecycle-maintenance", "running");
        var controller = new AccountHub.Api.AdminController(f.Store, new AuthorizationStub(new()
        { IsAuthenticated = true, User = new Jellyfin.Database.Implementations.Entities.User("worker-test", "auth", "reset") { Id = Guid.NewGuid() } }), workers)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        var result = Assert.IsType<ContentResult>(await controller.Health());
        Assert.Equal(200, result.StatusCode);
        using var document = System.Text.Json.JsonDocument.Parse(result.Content!);
        var data = document.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("ready").GetBoolean());
        Assert.False(data.GetProperty("backgroundWorkReady").GetBoolean());
        Assert.Equal("unavailable", data.GetProperty("workers").GetProperty("notifications").GetString());
    }

    private sealed class AuthorizationStub(AuthorizationInfo info) : IAuthorizationContext
    {
        public Task<AuthorizationInfo> GetAuthorizationInfo(HttpContext context) => Task.FromResult(info);
        public Task<AuthorizationInfo> GetAuthorizationInfo(HttpRequest request) => Task.FromResult(info);
    }
}
