using System.Net;
using System.Text.Json;
using Xunit;

namespace AccountHub.Security.Tests;

public sealed class ClientAndAuthorizationTests
{
    private static readonly Guid Actor = Guid.Parse("f61091cf-75bf-420c-9b5e-afb375d8f6de");
    private static CallerSession Caller => new(Actor, "transient-session", new Uri("https://jellyfin.invalid/jellyfin/"));
    private static string MyStatus(string factor = "true") => $$"""{"userId":"{{Actor}}","totpEnabled":{{factor}},"emailOtpEnabled":false,"passkeyCount":0,"recoveryCodesRemaining":0}""";

    [Theory]
    [InlineData("missing")][InlineData("disabled")][InlineData("incompatible")][InlineData("unavailable")]
    public async Task NonOkMakesNoCalls(string state)
    {
        var http = new HttpStub(_ => throw new Exception("must not call"));
        using var cache = new ReadModelCache();
        var client = new SecurityClient(new HttpClient(http), new CompatibilityStub(state), cache);
        await Assert.ThrowsAsync<SecurityFailure>(() => client.MyStatusAsync(Caller, default));
        await Assert.ThrowsAsync<SecurityFailure>(() => client.UsersAsync(Caller, default));
        await Assert.ThrowsAsync<SecurityFailure>(() => client.DevicesAsync(Caller, default));
        await Assert.ThrowsAsync<SecurityFailure>(() => client.VerifyAsync(Caller, new StepUpProof { Code = "123456" }, default));
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData(401, true, "two_factor_required")][InlineData(403, true, "two_factor_required")]
    [InlineData(401, false, "unauthorized")][InlineData(403, false, "forbidden")]
    public void OnlyMarkedDenialsMeanPending(int status, bool marker, string expected)
    {
        using var doc = JsonDocument.Parse($$"""{"twoFactorRequired":{{marker.ToString().ToLowerInvariant()}}}""");
        Assert.Equal(expected, SecurityJson.ErrorCode(status, doc.RootElement));
    }

    [Theory]
    [InlineData("{\"verified\":true}", true)]
    [InlineData("{}", false)][InlineData("{\"verified\":false}", false)]
    [InlineData("{\"verified\":\"true\"}", false)]
    public async Task VerificationRequiresExplicitTrue(string body, bool success)
    {
        var http = new HttpStub(_ => Task.FromResult(HttpStub.Json(body)));
        using var cache = new ReadModelCache();
        var client = new SecurityClient(new HttpClient(http), new CompatibilityStub(), cache);
        var proof = new StepUpProof { Code = "123456" };
        if (success) await client.VerifyAsync(Caller, proof, default);
        else await Assert.ThrowsAsync<SecurityFailure>(() => client.VerifyAsync(Caller, proof, default));
        var request = Assert.Single(http.Requests);
        Assert.Equal("https://jellyfin.invalid/jellyfin/TwoFactorAuth/StepUp/Verify", request.Url);
        Assert.Equal("transient-session", request.Token);
        Assert.DoesNotContain("123456", request.Url);
        Assert.Contains("123456", request.Body!);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "step_up_failed")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limited")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "unavailable")]
    public async Task FailedProofNeverDowngrades(HttpStatusCode status, string code)
    {
        var http = new HttpStub(r => Task.FromResult(HttpStub.Json(r.Method == HttpMethod.Get ? MyStatus() : "{}",
            r.Method == HttpMethod.Get ? HttpStatusCode.OK : status)));
        using var cache = new ReadModelCache();
        var compat = new CompatibilityStub();
        var gate = new CommandAuthorization(new SecurityClient(new HttpClient(http), compat, cache), compat, new FixedClock());
        var proof = new StepUpProof { Code = "123456" };
        var failure = await Assert.ThrowsAsync<SecurityFailure>(() => gate.AuthorizeAsync(Caller, new(), CommandRisk.Destructive,
            false, "CONFIRM", "CONFIRM", proof, default));
        Assert.Equal(code, failure.Code);
        Assert.Null(proof.Code);
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task FreshProofIsNeverCached()
    {
        var http = new HttpStub(r => Task.FromResult(HttpStub.Json(r.Method == HttpMethod.Get ? MyStatus() : "{\"verified\":true}")));
        using var cache = new ReadModelCache();
        var compat = new CompatibilityStub();
        var gate = new CommandAuthorization(new SecurityClient(new HttpClient(http), compat, cache), compat, new FixedClock());
        for (var i = 0; i < 2; i++)
        {
            var result = await gate.AuthorizeAsync(Caller, new(), CommandRisk.Destructive, true, "YES", "YES",
                new StepUpProof { Code = "123456" }, default);
            Assert.Equal("fresh_proof", result.Method);
        }
        Assert.Equal(4, http.Requests.Count);
    }

    [Fact]
    public async Task VerifiedNoFactorUsesFreshSelfStatusNotUsersList()
    {
        var http = new HttpStub(_ => Task.FromResult(HttpStub.Json(MyStatus("false"))));
        using var cache = new ReadModelCache();
        var compat = new CompatibilityStub();
        var gate = new CommandAuthorization(new SecurityClient(new HttpClient(http), compat, cache), compat, new FixedClock());
        var result = await gate.AuthorizeAsync(Caller, new(), CommandRisk.Destructive, false, "YES", "YES", null, default);
        Assert.Equal("verified_no_factor", result.FallbackReason);
        Assert.EndsWith("/MyStatus", Assert.Single(http.Requests).Url);
    }

    [Fact]
    public async Task UnknownMetadataDoesNotPermitCoreFallback()
    {
        var http = new HttpStub(_ => throw new Exception("must not call"));
        using var cache = new ReadModelCache();
        var compat = new CompatibilityStub("unavailable");
        var gate = new CommandAuthorization(new SecurityClient(new HttpClient(http), compat, cache), compat, new FixedClock());
        var failure = await Assert.ThrowsAsync<SecurityFailure>(() => gate.AuthorizeAsync(Caller, new(), CommandRisk.Destructive,
            false, "YES", "YES", null, default));
        Assert.Equal("step_up_unavailable", failure.Code);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task VerifierTransportFailureDoesNotDowngrade()
    {
        var http = new HttpStub(r => r.Method == HttpMethod.Get
            ? Task.FromResult(HttpStub.Json(MyStatus())) : throw new HttpRequestException("private error"));
        using var cache = new ReadModelCache();
        var compat = new CompatibilityStub();
        var gate = new CommandAuthorization(new SecurityClient(new HttpClient(http), compat, cache), compat, new FixedClock());
        var proof = new StepUpProof { Code = "123456" };
        var failure = await Assert.ThrowsAsync<SecurityFailure>(() => gate.AuthorizeAsync(Caller, new(), CommandRisk.Destructive,
            false, "YES", "YES", proof, default));
        Assert.Equal("unavailable", failure.Code);
        Assert.Null(proof.Code);
        Assert.DoesNotContain("private error", failure.ToString());
    }

    [Fact]
    public async Task SummaryDoesNotLeakExtraUpstreamFields()
    {
        var http = new HttpStub(_ => Task.FromResult(HttpStub.Json($$$"""{"userId":"{{{Actor}}}","devices":{"trusted":[{"deviceName":"browser","token":"CANARY"}]},"twoFactor":{"appPasswords":["CANARY"]}}""")));
        using var cache = new ReadModelCache();
        var client = new SecurityClient(new HttpClient(http), new CompatibilityStub(), cache);
        var result = await client.SummaryAsync(Caller, Actor, default);
        Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(result));
        Assert.Equal("browser", result.Devices!.Trusted![0].DeviceName);
    }

    [Theory]
    [InlineData("{}", "step_up_unavailable")]
    [InlineData("{\"totpEnabled\":false}", "step_up_unavailable")]
    public async Task UnknownIsNotNoFactor(string fields, string expected)
    {
        var json = fields.Replace("{", $$"""{"userId":"{{Actor}}",""");
        if (json.EndsWith(",}")) json = json.Replace(",}", "}");
        var http = new HttpStub(_ => Task.FromResult(HttpStub.Json(json)));
        using var cache = new ReadModelCache();
        var compat = new CompatibilityStub();
        var gate = new CommandAuthorization(new SecurityClient(new HttpClient(http), compat, cache), compat, new FixedClock());
        var failure = await Assert.ThrowsAsync<SecurityFailure>(() => gate.AuthorizeAsync(Caller, new(), CommandRisk.Destructive,
            false, "YES", "YES", null, default));
        Assert.Equal(expected, failure.Code);
    }

    [Theory]
    [InlineData("missing", false, "security_missing")]
    [InlineData("incompatible", false, "security_incompatible")]
    [InlineData("disabled", false, "security_disabled")]
    public async Task KnownAbsenceAllowsCoreConfirmationOnly(string state, bool securityAction, string reason)
    {
        var http = new HttpStub(_ => throw new Exception());
        using var cache = new ReadModelCache();
        var compat = new CompatibilityStub(state);
        var gate = new CommandAuthorization(new SecurityClient(new HttpClient(http), compat, cache), compat, new FixedClock());
        var result = await gate.AuthorizeAsync(Caller, new(), CommandRisk.Destructive, securityAction, "YES", "YES", null, default);
        Assert.Equal(reason, result.FallbackReason);
        await Assert.ThrowsAsync<SecurityFailure>(() => gate.AuthorizeAsync(Caller, new(), CommandRisk.Destructive,
            true, "YES", "YES", null, default));
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData(StepUpLevel.Off, CommandRisk.Destructive, false)]
    [InlineData(StepUpLevel.Destructive, CommandRisk.Destructive, true)]
    [InlineData(StepUpLevel.Destructive, CommandRisk.Configuration, false)]
    [InlineData(StepUpLevel.AllConfigChanges, CommandRisk.Configuration, true)]
    [InlineData(StepUpLevel.AllConfigChanges, CommandRisk.Audit, false)]
    [InlineData(StepUpLevel.Everything, CommandRisk.Audit, false)]
    [InlineData(StepUpLevel.Everything, CommandRisk.Operational, false)]
    public void CumulativeMirror(StepUpLevel level, CommandRisk risk, bool expected) =>
        Assert.Equal(expected, CommandAuthorization.RequiresProof(level, risk));

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    public async Task OperationalSettingsNeverRequestOrConsumeFreshProof(string? code)
    {
        var http = new HttpStub(_ => throw new Exception("Operational writes must not call Security"));
        using var cache = new ReadModelCache();
        var compat = new CompatibilityStub();
        var gate = new CommandAuthorization(new SecurityClient(new HttpClient(http), compat, cache), compat, new FixedClock());
        var proof = code is null ? null : new StepUpProof { Code = code };
        var result = await gate.AuthorizeAsync(Caller, new(StepUpLevel.Everything), CommandRisk.Operational,
            false, null, "CONFIRM", proof, default);
        Assert.Equal("administrator", result.Method);
        Assert.Equal("non_security_operation", result.FallbackReason);
        Assert.Empty(http.Requests);
        if (proof is not null) Assert.Null(proof.Code);
    }

    [Fact]
    public async Task SafeCacheChecksCompatibilityAndRefreshes()
    {
        var http = new HttpStub(_ => Task.FromResult(HttpStub.Json("[]")));
        using var cache = new ReadModelCache();
        var compat = new CompatibilityStub();
        var client = new SecurityClient(new HttpClient(http), compat, cache);
        await client.UsersAsync(Caller, default);
        await client.UsersAsync(Caller, default);
        Assert.Single(http.Requests);
        await client.UsersAsync(Caller, default, refresh: true);
        Assert.Equal(2, http.Requests.Count);
        compat.State = "incompatible";
        await Assert.ThrowsAsync<SecurityFailure>(() => client.UsersAsync(Caller, default));
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public void SafeProjectionDiscardsSecretsAndPreservesUnknown()
    {
        var user = JsonSerializer.Deserialize<UserStatus>("{\"username\":\"alice\",\"secret\":\"CANARY\",\"recoveryCodes\":[\"CANARY\"]}", SecurityJson.Options)!;
        Assert.Null(user.HasFactor);
        Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(user));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(null, null)]
    public void RecoveryCodesAreProofCapableAndUnknownCountIsNotNoFactor(int? remaining, bool? expected)
    {
        var status = new UserStatus(Actor, "alice", false, false, 0, 0, remaining, false);
        Assert.Equal(expected, status.HasFactor);
    }
}
