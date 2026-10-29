namespace Jellyfin.Plugin.PrivateSyncPlay;

public static class WatchPartyNotice
{
    public static (string Title,string Body) Text(string type,string? guestLabel=null,string? reason=null)
    {
        var guest=new string((guestLabel??"A guest").Where(c=>!char.IsControl(c)).Take(80).ToArray());
        if(string.IsNullOrWhiteSpace(guest))guest="A guest";
        return type switch {
            "party.created" => ("Watch party created","Your private watch party is ready. Start playback and use the SyncPlay menu to invite viewers."),
            "party.started" => ("Watch party started","Playback has started. Open SyncPlay on your Jellyfin device to view the party and playback controls."),
            "party.ended" => ("Watch party ended","This watch party has closed. Its guest sessions can no longer join; create a new party to watch again."),
            "party.invite.created" => ("Watch-party invitation created","A new invitation was issued for your party. Only its configured uses, expiry and native access permissions apply."),
            "party.invite.revoked" => ("Watch-party invitation revoked","An invitation was revoked. It can no longer admit viewers, and guest access associated with it has ended."),
            "party.guest.joined" => ("Guest joined your watch party",guest+" joined. View current participants in the SyncPlay menu."),
            "party.guest.left" => ("Guest left your watch party",guest+(reason=="lease_ended"?" disconnected after their guest session expired.":reason=="switched_party"?" switched to another party.":reason=="party_ended"?" disconnected because the party ended.":" left the party.")+" Open SyncPlay to review current participants."),
            "party.guest.revoked" => ("Guest access ended",guest+" can no longer access this watch party. Their invitation or guest access was revoked."),
            "party.moderation" => ("Watch-party permissions changed","Viewer access or playback permissions changed. Review the current participants and controls in SyncPlay."),
            "party.settings.changed" => ("Watch-party settings changed","Party or guest-access settings were updated. Review Private SyncPlay settings before sharing new invitations."),
            _ => ("Watch-party activity","Your watch party changed. Open SyncPlay to review its current participants, invitation access and playback state.")
        };
    }
}
