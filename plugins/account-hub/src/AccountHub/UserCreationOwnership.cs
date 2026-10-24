using AccountHub.Core;

namespace AccountHub;

/// <summary>One process-local owner for HTTP native creation versus Hub creation. Restart fails closed to Hub.</summary>
public sealed class UserCreationOwnership(TimeProvider clock)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private sealed class Scope { public bool Active = true; }
    private readonly AsyncLocal<Scope?> hubScope = new();
    private long expiresAt;
    private long deadline;
    public object Snapshot
    {
        get { var enabled = NativeEnabled; return new { owner = enabled ? "native" : "hub", nativeOverrideUntil = enabled ? (long?)Interlocked.Read(ref expiresAt) : null, hubOnboardingPaused = enabled }; }
    }
    private bool NativeEnabled => clock.GetTimestamp() < Interlocked.Read(ref deadline);
    public void RequireHub()
    {
        if (NativeEnabled) throw new HubException("hub_onboarding_paused", "Native user creation is temporarily enabled. Retry onboarding after the five-minute override ends", true);
    }
    public async Task<object> SetNativeOverrideAsync(bool enabled, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            Interlocked.Exchange(ref expiresAt, enabled ? clock.GetUtcNow().AddMinutes(5).ToUnixTimeMilliseconds() : 0);
            Interlocked.Exchange(ref deadline, enabled ? checked(clock.GetTimestamp() + clock.TimestampFrequency * 300) : 0);
            return Snapshot;
        }
        finally { gate.Release(); }
    }
    public async Task<T> RunAsync<T>(bool native, Func<Task<T>> action, CancellationToken ct)
    {
        if (!native && hubScope.Value is { Active: true }) return await action();
        await gate.WaitAsync(ct);
        Scope? scope = null;
        try
        {
            if (native != NativeEnabled) throw new HubException(native ? "use_account_hub" : "hub_onboarding_paused", native ? "Create users through Account Hub Invitations. An administrator can enable native creation for five minutes in Account Hub Settings." : "Hub onboarding is paused during the temporary native creation override", !native);
            if (!native) { scope = new Scope(); hubScope.Value = scope; }
            return await action();
        }
        finally { if (scope is not null) { scope.Active = false; hubScope.Value = null; } gate.Release(); }
    }
}
