namespace AccountHub.Security;

public sealed record AuthorizationEvidence(string Method, string? FallbackReason, DateTimeOffset? VerifiedAt);

public sealed class CommandAuthorization(SecurityClient client, ISecurityCompatibility compatibility, TimeProvider clock)
{
    public static bool RequiresProof(StepUpLevel level, CommandRisk risk)
    {
        if (!Enum.IsDefined(level) || !Enum.IsDefined(risk)) throw new SecurityFailure("invalid_request", 400);
        return risk is not (CommandRisk.Operational or CommandRisk.Audit) && (int)level >= (int)risk;
    }

    // Called inside the host's command/revision lease, after idempotency/target checks.
    // Evidence is for the immediate command only, not a reusable authorization ticket.
    public async Task<AuthorizationEvidence> AuthorizeAsync(CallerSession caller, SecuritySettings oldSettings,
        CommandRisk risk, bool securityAction, string? confirmation, string expectedConfirmation,
        StepUpProof? proof, CancellationToken ct)
    {
        try
        {
            oldSettings.Validate();
            if (securityAction && risk is CommandRisk.Operational or CommandRisk.Audit) throw new SecurityFailure("invalid_request", 400);
            if (!securityAction && risk is CommandRisk.Operational or CommandRisk.Audit)
                return new("administrator", "non_security_operation", null);
            var health = compatibility.Check();
            if (securityAction && !health.IsOk) throw new SecurityFailure("security_incompatible", 409);
            if (risk == CommandRisk.Destructive || securityAction)
                Confirm(confirmation, expectedConfirmation);
            if (!RequiresProof(oldSettings.StepUpLevelMirror, risk))
                return new("confirmation", oldSettings.StepUpLevelMirror == StepUpLevel.Off ? "mirror_off" : "below_mirror", null);

            if (!health.IsOk)
            {
                // Runtime unknown/transient failures do NOT qualify for absence fallback.
                if (health.State is not ("missing" or "disabled" or "incompatible"))
                    throw new SecurityFailure("step_up_unavailable", 503);
                Confirm(confirmation, expectedConfirmation);
                return new("confirmation", "security_" + health.State, null);
            }

            // Never use Users listing to infer no factors: upstream can fabricate zeroes on read failure.
            // MyStatus instead fails on unreadable data. Unknown fields fail closed.
            var status = await client.MyStatusAsync(caller, ct);
            if (status.UserId != caller.UserId) throw new SecurityFailure("step_up_unavailable", 503);
            if (status.HasFactor is null) throw new SecurityFailure("step_up_unavailable", 503);
            if (status.HasFactor == false)
            {
                Confirm(confirmation, expectedConfirmation);
                return new("confirmation", "verified_no_factor", null);
            }
            if (proof is null) throw new SecurityFailure("step_up_required", 403);
            await client.VerifyAsync(caller, proof, ct);
            return new("fresh_proof", null, clock.GetUtcNow());
        }
        finally { proof?.Dispose(); }
    }

    private static void Confirm(string? actual, string expected)
    {
        if (string.IsNullOrEmpty(expected) || !string.Equals(actual, expected, StringComparison.Ordinal))
            throw new SecurityFailure("confirmation_required", 400);
    }
}
