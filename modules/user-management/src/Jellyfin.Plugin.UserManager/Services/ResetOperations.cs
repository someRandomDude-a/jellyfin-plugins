using Jellyfin.Plugin.UserManager.Data;

namespace Jellyfin.Plugin.UserManager.Services;

/// <summary>Serializes reset effects with recovery. Password effects are never replayed.</summary>
public sealed class ResetOperations(PluginStore store)
{
    public async Task CompleteAsync(string hash, Func<Task> changePassword, Func<Task> revokeSessions, CancellationToken ct = default, Func<CancellationToken, Task>? authorize = null)
    {
        using var fence = await store.AcquireResetEffectsAsync(ct).ConfigureAwait(false);
        var current = await store.GetResetTokenAsync(hash, ct).ConfigureAwait(false);
        if (current is null || current.UsedAt is not null || current.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            throw new UserManagerException(400, "This reset link is invalid or has expired.");
        if (authorize is not null) await authorize(ct).ConfigureAwait(false);
        var consumed = await store.ConsumeResetTokenAsync(hash, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ct).ConfigureAwait(false);
        if (consumed is null) throw new UserManagerException(400, "This reset link has already been used.");

        // A throw/cancellation may mean the native password write succeeded. Leave claimed
        // durable and let maintenance retry only revocation after this fence is released.
        await changePassword().ConfigureAwait(false);
        await store.SetResetStageAsync(hash, "password_changed", CancellationToken.None).ConfigureAwait(false);
        try
        {
            await revokeSessions().ConfigureAwait(false);
            await store.MarkResetSessionsRevokedAsync(hash, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            await store.SetResetStageAsync(hash, "remediation_pending", CancellationToken.None).ConfigureAwait(false);
            throw new UserManagerException(409, "The password changed, but session revocation is pending administrator remediation. The recovery link is consumed; do not retry the password change.");
        }
    }

    public async Task RevokePendingAsync(Func<Guid, Task> revokeSessions, CancellationToken ct = default)
    {
        using var fence = await store.AcquireResetEffectsAsync(ct).ConfigureAwait(false);
        var failed = false;
        foreach (var reset in await store.PendingSessionRevocationsAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!Guid.TryParse(reset.JellyfinUserId, out var userId)) throw new InvalidDataException("Invalid reset user ID.");
                await revokeSessions(userId).ConfigureAwait(false);
                await store.MarkResetSessionsRevokedAsync(reset.TokenHash, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { failed = true; }
        }
        if (failed) throw new InvalidOperationException("Session revocation remains pending; password writes are never retried.");
    }
}
