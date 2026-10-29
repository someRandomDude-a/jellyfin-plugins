using Jellyfin.Plugin.PrivateSyncPlay.Api;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DiscordBridge;

public sealed class WatchPartyCommands(IServiceProvider services)
{
    public async Task<WatchPartyInvitation> InviteAsync(Guid linkedUser, CancellationToken ct)
    {
        var snapshot = services.GetRequiredService<BridgeStore>().Read(); var settings=snapshot.Settings;
        if (!settings.EnableWatchPartyCommands) throw AccountCommandException.FromCode("party_commands_disabled");
        if(!BridgePermissions.Permitted(settings,"party.invite",linkedUser)||settings.WatchPartyGuests&&!BridgePermissions.Permitted(settings,"party.guests",linkedUser))throw AccountCommandException.FromCode("party_permission_denied");
        var guests = settings.WatchPartyGuests;
        if (guests) BridgePermissions.Require(settings, "party.guests", linkedUser);
        var sessions = services.GetRequiredService<ISessionManager>().Sessions.Where(s => s.UserId == linkedUser && s.SessionControllers.Any(c=>c.IsSessionActive))
            .OrderByDescending(s => s.NowPlayingItem is not null).ThenByDescending(s=>s.LastActivityDate).ThenBy(s=>s.Id,StringComparer.Ordinal).ToArray();
        if (sessions.Length == 0) throw AccountCommandException.FromCode("active_device_required");
        var publicUrl=await BridgePublicUrl.ResolveAsync(services,settings,ct)??throw AccountCommandException.FromCode("party_public_url_required");
        var parties = services.GetRequiredService<IWatchPartyService>();
        if (!parties.IsAvailable) throw AccountCommandException.FromCode("party_provider_missing");
        var session = sessions[0];
        var options = new CreateInviteOptions(publicUrl, AllowGuests: guests, MaxUses: settings.WatchPartyUses, ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(settings.WatchPartyExpiryMinutes));
        var current = await parties.GetCurrentPartyAsync(linkedUser, session.Id, ct);
        var invite = current is null
            ? (await parties.CreatePartyWithInviteAsync(linkedUser, session.Id, new CreatePartyOptions("Watch party", WatchPartyVisibility.Private), options, ct)).Invite
            : await parties.CreateInviteAsync(linkedUser, session.Id, current.PartyId, options, ct);
        return new(invite.InviteUrl,snapshot.Revision);
    }
}
public sealed record WatchPartyInvitation(Uri Url,long BridgeRevision);
