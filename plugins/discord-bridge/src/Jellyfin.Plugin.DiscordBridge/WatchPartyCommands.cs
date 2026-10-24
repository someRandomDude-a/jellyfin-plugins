using Jellyfin.Plugin.PrivateSyncPlay.Api;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DiscordBridge;

public sealed class WatchPartyCommands(IServiceProvider services)
{
    public async Task<Uri> InviteAsync(Guid linkedUser, int device, bool guests, CancellationToken ct)
    {
        if (!services.GetRequiredService<BridgeStore>().Read().Settings.EnableWatchPartyCommands) throw new InvalidOperationException("Enable watch-party commands in Discord Bridge settings.");
        var sessions = services.GetRequiredService<ISessionManager>().Sessions.Where(s => s.UserId == linkedUser)
            .OrderBy(s => s.DeviceName, StringComparer.Ordinal).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray();
        if (device < 1 || device > sessions.Length) throw new InvalidOperationException("Open Jellyfin first and choose one of your active devices using /watchparty devices.");
        var config = services.GetRequiredService<BridgeStore>().Read().Settings;
        if (string.IsNullOrWhiteSpace(config.PublicServerUrl)) throw new InvalidOperationException("Public server URL is not configured.");
        var parties = services.GetRequiredService<IWatchPartyService>();
        if (!parties.IsAvailable) throw new InvalidOperationException("Private SyncPlay is unavailable.");
        var session = sessions[device - 1];
        var options = new CreateInviteOptions(new Uri(config.PublicServerUrl.TrimEnd('/') + '/'), AllowGuests: guests, MaxUses: 1, ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30));
        var current = await parties.GetCurrentPartyAsync(linkedUser, session.Id, ct);
        var invite = current is null
            ? (await parties.CreatePartyWithInviteAsync(linkedUser, session.Id, new CreatePartyOptions("Watch party", WatchPartyVisibility.Private), options, ct)).Invite
            : await parties.CreateInviteAsync(linkedUser, session.Id, current.PartyId, options, ct);
        return invite.InviteUrl;
    }
    public string Devices(Guid linkedUser) => string.Join('\n', services.GetRequiredService<ISessionManager>().Sessions.Where(s => s.UserId == linkedUser)
        .OrderBy(s => s.DeviceName, StringComparer.Ordinal).ThenBy(s => s.Id, StringComparer.Ordinal).Select((s, i) => $"{i + 1}: {s.DeviceName} ({s.Client})").Take(20));
}
