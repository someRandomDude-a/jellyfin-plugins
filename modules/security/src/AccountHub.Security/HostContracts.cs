using Microsoft.AspNetCore.Http;
using System.Text.Json.Serialization;

namespace AccountHub.Security;

public sealed record CommandDescriptor(Guid CommandId, long Revision, string Operation, Guid? TargetUserId,
    string? DeviceRecordId, SecuritySettings? NewSettings);

public sealed record CommandOutcome(string Status, int? SessionsTerminated = null,
    bool RemediationPending = false, string? Detail = null);

/// <summary>Implemented by Account Hub core, not a second command/audit store.</summary>
public interface ISecurityHubHost
{
    // Must identify a real active elevated Jellyfin user from authorization context.
    // Reject API keys/service accounts, cookie-only auth and cross-origin management requests.
    Task<CallerSession> RequireAdministratorAsync(HttpContext context, CancellationToken ct);
    Task<(SecuritySettings Settings, long Revision)> ReadSettingsAsync(CancellationToken ct);

    // Must atomically claim actor/operation/commandId + canonical payload hash (NO proof/session),
    // check revision, return completed retry BEFORE invoking callback, reject conflicting retry,
    // serialize settings changes, recheck target/last-admin constraints and persist audit intent
    // BEFORE callback. Hold command lease through callback. Audit evidence/outcome and commit
    // settings/results durably. Ambiguous upstream mutation is remediation_pending, never blind retry.
    // Callback receives the CURRENT OLD settings (especially when lowering the mirror).
    Task<CommandOutcome> ExecuteAsync(CallerSession caller, CommandDescriptor command,
        Func<SecuritySettings, CancellationToken, Task<(CommandOutcome Outcome, AuthorizationEvidence Evidence)>> execute,
        CancellationToken ct);
}

public sealed class ActionRequest
{
    [JsonPropertyName("commandId")] public Guid CommandId { get; set; }
    [JsonPropertyName("revision")] public long Revision { get; set; }
    [JsonPropertyName("confirmation")] public string? Confirmation { get; set; }
    [JsonPropertyName("stepUp")] public StepUpProof? StepUp { get; set; }
    public override string ToString() => "[redacted command request]";
}

public sealed class SettingsRequest
{
    [JsonPropertyName("commandId")] public Guid CommandId { get; set; }
    [JsonPropertyName("revision")] public long Revision { get; set; }
    [JsonPropertyName("confirmation")] public string? Confirmation { get; set; }
    [JsonPropertyName("stepUp")] public StepUpProof? StepUp { get; set; }
    [JsonPropertyName("settings")] public SecuritySettings Settings { get; set; } = new();
    public override string ToString() => "[redacted settings request]";
}
