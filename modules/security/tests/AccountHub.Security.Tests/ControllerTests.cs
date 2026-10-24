using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AccountHub.Security.Tests;

public sealed class ControllerTests
{
    private static readonly Guid Actor = Guid.Parse("f61091cf-75bf-420c-9b5e-afb375d8f6de");
    private static readonly Guid Target = Guid.Parse("a1020eab-afd6-447a-9296-05080b322734");
    private static readonly CallerSession Caller = new(Actor, "transient-session", new Uri("https://jf.invalid/base/"));

    private sealed class Host : ISecurityHubHost
    {
        public SecuritySettings Settings { get; set; } = new();
        public bool Deny { get; set; }
        public List<(CommandDescriptor Command, AuthorizationEvidence Evidence)> Audits { get; } = [];
        private readonly Dictionary<Guid, CommandOutcome> _results = [];
        public Task<CallerSession> RequireAdministratorAsync(HttpContext context, CancellationToken ct) =>
            Deny ? throw new SecurityFailure("forbidden", 403) : Task.FromResult(Caller);
        public Task<(SecuritySettings Settings, long Revision)> ReadSettingsAsync(CancellationToken ct) => Task.FromResult((Settings, 1L));
        public async Task<CommandOutcome> ExecuteAsync(CallerSession caller, CommandDescriptor command,
            Func<SecuritySettings, CancellationToken, Task<(CommandOutcome Outcome, AuthorizationEvidence Evidence)>> execute, CancellationToken ct)
        {
            // Test seam only: not a production command ledger.
            if (_results.TryGetValue(command.CommandId, out var prior)) return prior;
            if (command.Revision != 1) throw new SecurityFailure("revision_changed", 409);
            var (outcome, evidence) = await execute(Settings, ct);
            Audits.Add((command, evidence));
            _results.Add(command.CommandId, outcome);
            if (command.NewSettings is not null) Settings = command.NewSettings;
            return outcome;
        }
    }

    private static SecurityController Controller(Host host, SecurityClient client, ISecurityCompatibility compatibility) =>
        new(host, compatibility, client, new CommandAuthorization(client, compatibility, new FixedClock()))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
    private static JsonElement Data(IActionResult response) => JsonSerializer.SerializeToElement(((JsonResult)response).Value, SecurityJson.Options);

    [Fact]
    public async Task LoweringMirrorUsesOldLevelNotRequestedOff()
    {
        var http = new HttpStub(_ => Task.FromResult(HttpStub.Json($$"""{"userId":"{{Actor}}","totpEnabled":true}""")));
        using var cache = new ReadModelCache();
        var compatibility = new CompatibilityStub();
        var host = new Host();
        var controller = Controller(host, new SecurityClient(new HttpClient(http), compatibility, cache), compatibility);
        var response = Data(await controller.Settings(new SettingsRequest
        {
            CommandId = Guid.NewGuid(), Revision = 1, Settings = new(StepUpLevel.Off), Confirmation = "CHANGE SECURITY SETTINGS"
        }, default));
        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal("step_up_required", response.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(StepUpLevel.Destructive, host.Settings.StepUpLevelMirror);
        Assert.Empty(host.Audits);
    }

    [Fact]
    public async Task DeviceUsesRecordIdAndRetryDoesNotConsumeAnotherProof()
    {
        var http = new HttpStub(request => Task.FromResult(HttpStub.Json(request.Method == HttpMethod.Delete ? "" :
            request.RequestUri!.AbsolutePath.EndsWith("AllTrustedDevices")
                ? $$"""[{"userId":"{{Target}}","id":"record-123","deviceId":"NOT-THE-RECORD-ID","deviceName":"browser"}]"""
                : request.Method == HttpMethod.Get ? $$"""{"userId":"{{Actor}}","totpEnabled":true}""" : "{\"verified\":true}")));
        using var cache = new ReadModelCache();
        var compatibility = new CompatibilityStub();
        var host = new Host();
        var controller = Controller(host, new SecurityClient(new HttpClient(http), compatibility, cache), compatibility);
        var request = new ActionRequest { CommandId = Guid.NewGuid(), Revision = 1,
            Confirmation = $"REVOKE TRUST {Target:D}", StepUp = new StepUpProof { Code = "123456" } };
        Assert.True(Data(await controller.Revoke(Target, "record-123", request, default)).GetProperty("ok").GetBoolean());
        Assert.Null(request.StepUp.Code);
        var count = http.Requests.Count;
        await controller.Revoke(Target, "record-123", request, default);
        Assert.Equal(count, http.Requests.Count);
        var deletion = Assert.Single(http.Requests, r => r.Method == "DELETE");
        Assert.EndsWith($"/TwoFactorAuth/Users/{Target:D}/Devices/record-123", deletion.Url);
        Assert.DoesNotContain("NOT-THE-RECORD-ID", deletion.Url);
        Assert.Null(deletion.Body);
        Assert.Single(host.Audits);
    }

    [Fact]
    public async Task AmbiguousForceLogoutIsRemediationNotCompleteSuccess()
    {
        var http = new HttpStub(request => request.RequestUri!.AbsolutePath.EndsWith("ForceLogout")
            ? throw new HttpRequestException("sensitive transport details")
            : Task.FromResult(HttpStub.Json(request.Method == HttpMethod.Get
                ? $$"""{"userId":"{{Actor}}","totpEnabled":true}""" : "{\"verified\":true}")));
        using var cache = new ReadModelCache();
        var compatibility = new CompatibilityStub();
        var host = new Host();
        var controller = Controller(host, new SecurityClient(new HttpClient(http), compatibility, cache), compatibility);
        var response = Data(await controller.ForceLogout(Target, new ActionRequest { CommandId = Guid.NewGuid(), Revision = 1,
            Confirmation = $"FORCE LOGOUT {Target:D}", StepUp = new StepUpProof { Code = "123456" } }, default));
        Assert.True(response.GetProperty("data").GetProperty("remediationPending").GetBoolean());
        Assert.DoesNotContain("sensitive transport", response.ToString());
    }

    [Theory]
    [InlineData("..")][InlineData(".")][InlineData("%2e%2e")][InlineData("record/../Toggle")]
    public async Task RecordIdCannotEscapeAllowedUpstreamRoute(string recordId)
    {
        var http = new HttpStub(_ => throw new Exception());
        using var cache = new ReadModelCache();
        var compatibility = new CompatibilityStub();
        var controller = Controller(new Host(), new SecurityClient(new HttpClient(http), compatibility, cache), compatibility);
        var response = Data(await controller.Revoke(Target, recordId,
            new ActionRequest { CommandId = Guid.NewGuid(), Revision = 1 }, default));
        Assert.Equal("invalid_request", response.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task HostDenialPreventsReadEvenWithCompatibleSecurity()
    {
        var http = new HttpStub(_ => throw new Exception());
        using var cache = new ReadModelCache();
        var compatibility = new CompatibilityStub();
        var controller = Controller(new Host { Deny = true }, new SecurityClient(new HttpClient(http), compatibility, cache), compatibility);
        var response = Data(await controller.Users());
        Assert.Equal("forbidden", response.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(http.Requests);
    }
}
