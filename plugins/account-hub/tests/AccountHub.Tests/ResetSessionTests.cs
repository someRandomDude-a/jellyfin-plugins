using System.Net;
using System.Reflection;
using System.Text.Json;
using AccountHub.Core;
using AccountHub.Security;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AccountHub.Tests;

public sealed class ResetSessionTests
{
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Call = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args ?? []);
    }
    private static T Fake<T>(Func<MethodInfo, object?[], object?> call) where T : class
    { var value = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)value).Call = call; return value; }
    private sealed class CompatibilityStub(string state) : ISecurityCompatibility
    { public Compatibility Check() => new(state, "2.6.4.1", DateTimeOffset.UtcNow); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request)); }
    private sealed class SessionVerdict : ISecuritySessionVerification
    {
        public bool? Verified;
        public bool? IsVerified(CallerSession caller) => Verified;
    }

    [Theory]
    [InlineData("missing", true, false, false, true, false, "reset_verification_unavailable")]
    [InlineData("ok", false, false, false, true, false, "reset_session_required")]
    [InlineData("ok", null, false, false, true, false, "reset_verification_unavailable")]
    [InlineData("ok", true, true, false, true, false, "reset_session_required")]
    [InlineData("ok", true, null, false, true, false, "reset_session_required")]
    [InlineData("ok", true, false, true, true, false, "reset_account_mismatch")]
    [InlineData("ok", true, false, false, true, true, "reset_session_required")]
    [InlineData("ok", true, false, false, true, false, null)]
    [InlineData("ok", true, false, false, false, false, null)]
    public async Task ResetRequiresTheSameAccountsNativeSessionVerdictWithoutFreshProof(string compatibility, bool? verified, bool? locked, bool otherAccount, bool factor, bool revokeDuringRead, string? expected)
    {
        await using var fixture = new StoreFixture(); await fixture.InitializeAsync();
        await fixture.ExecuteSqlAsync("UPDATE hub_state SET StepUpLevelMirror='Off', PasswordLoginPolicy='allow_local';");
        var target = new User("target", "auth", "reset") { Id = Guid.NewGuid() };
        var actor = otherAccount ? new User("other", "auth", "reset") { Id = Guid.NewGuid() } : target;
        var sessions = new SessionVerdict { Verified = verified }; var statusReads = 0;
        using var http = new HttpClient(new Handler(request => {
            Assert.Contains("native-session", request.Headers.Authorization!.ToString());
            Assert.Equal(HttpMethod.Get, request.Method); Assert.EndsWith("/MyStatus", request.RequestUri!.AbsolutePath); statusReads++;
            if (revokeDuringRead) sessions.Verified = false;
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { userId = actor.Id, totpEnabled = factor, emailOtpEnabled = false, passkeyCount = 0, recoveryCodesRemaining = 0, isLockedOut = locked })) };
        }));
        var health = new CompatibilityStub(compatibility);
        using var cache = new ReadModelCache();
        using var services = new ServiceCollection()
            .AddSingleton(Fake<IUserManager>((m, _) => m.Name == "GetUserById" ? actor : throw new NotSupportedException()))
            .AddSingleton(Fake<IAuthorizationContext>((m, _) => m.Name == "GetAuthorizationInfo" ? Task.FromResult(new AuthorizationInfo { IsAuthenticated = true, IsApiKey = false, User = actor, Token = "native-session" }) : throw new NotSupportedException()))
            .AddSingleton(Fake<IServerApplicationHost>((m, _) => m.Name switch { "GetLocalApiUrl" => "http://127.0.0.1:8096/suite", "get_HttpPort" => 8096, _ => throw new NotSupportedException(m.Name) }))
            .AddSingleton<ISecurityCompatibility>(health)
            .AddSingleton<ISecuritySessionVerification>(sessions)
            .AddSingleton(new SecurityClient(http, health, cache))
            .BuildServiceProvider();
        var context = new DefaultHttpContext(); context.Request.Headers.Authorization = "MediaBrowser Token=\"native-session\"";
        var host = new HubHost(services, fixture.Store, new ExternalCommandGate(fixture.Store, fixture.Clock));
        if (expected is null) { await host.RequireResetSessionAsync(context, target.Id, default); Assert.Equal(1, statusReads); }
        else Assert.Equal(expected, (await Assert.ThrowsAsync<SecurityFailure>(() => host.RequireResetSessionAsync(context, target.Id, default))).Code);
    }
}
