using System.Text.Json;
using AccountHub.Core.Data;

namespace AccountHub.Core;

public sealed class IntegrationSecretService(HubStore store, CommandGate commands, TimeProvider clock)
{
    private static readonly HashSet<string> Names = ["discord.botToken", "seerr.apiKey", "webhook.secret", "security.webhookSecret"];

    public Task<JsonElement> ReplaceAsync(Guid actor, Guid commandId, long revision, string name, string? secret,
        Func<CancellationToken, Task> authorize, CancellationToken ct = default)
    {
        if (!Names.Contains(name) || secret is { Length: < 1 or > 8192 })
            throw new HubException("invalid_request", "Invalid integration secret");
        if (name is "webhook.secret" or "security.webhookSecret" && secret is { Length: < 32 })
            throw new HubException("invalid_request", "Signing secrets require at least 32 characters");
        // Keyed digest binds retries without storing a guessable password/secret hash.
        var ciphertext = secret is null ? null : store.Protect(name, secret);
        return commands.ExecuteAsync(actor, "settings.secret.changed", commandId, revision,
            new { name, clear = secret is null, changeFingerprint = store.SecretFingerprint(name, secret) },
            authorize, async (db, token) =>
            {
                var existing = await db.Secrets.FindAsync([name], token);
                if (ciphertext is null)
                {
                    if (existing is not null) db.Secrets.Remove(existing);
                }
                else if (existing is not null)
                {
                    existing.Ciphertext = ciphertext;
                    existing.UpdatedAtUnixMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
                }
                else db.Secrets.Add(new IntegrationSecret
                { Name = name, Ciphertext = ciphertext, UpdatedAtUnixMs = clock.GetUtcNow().ToUnixTimeMilliseconds() });
                return JsonSerializer.SerializeToElement(new { configured = ciphertext is not null });
            }, ct);
    }
}
