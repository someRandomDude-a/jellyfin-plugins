using AccountHub.Core;
using Jellyfin.Plugin.UserManager.Services;

namespace AccountHub;

public sealed class LifecycleSecretReader(HubStore store) : IIntegrationSecretReader
{
    public Task<string?> ReadAsync(string name, CancellationToken ct) => name == "seerr.apiKey"
        ? store.ReadSecretAsync(name, ct) : throw new ArgumentException("Unsupported lifecycle secret");
}
