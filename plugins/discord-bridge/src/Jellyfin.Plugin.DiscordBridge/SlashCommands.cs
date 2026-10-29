using Discord;
using Discord.Interactions;
using Jellyfin.Plugin.PrivateSyncPlay.Api;

namespace Jellyfin.Plugin.DiscordBridge;

public sealed class SlashCommands(BridgeRuntime runtime, BridgeStore store, WatchPartyCommands parties) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("help", "Explain Jellyfin self-service commands")]
    public async Task Help()
    {
        await RespondAsync("Use /link to confirm your Jellyfin identity, /unlink, /reset or /status. /invite accepts optional uses and expires-in-minutes within administrator limits. /watchparty posts a native link in this approved channel; /watchparty user:@user sends a DM instead. Open Jellyfin on your playback device first. Run commands in this server; recovery links arrive by DM. Administration stays in Jellyfin. Never post a password, token or 2FA code here.", ephemeral: true, allowedMentions: AllowedMentions.None);
        runtime.Interactions.Record("help", "completed");
    }
    [SlashCommand("link", "Link through a signed-in Jellyfin confirmation page")]
    public Task Link() => Identity("link.prepare");
    [SlashCommand("unlink", "Remove your verified Jellyfin link")]
    public async Task Unlink([Summary("confirm", "Confirm unlinking your own Discord account")] bool confirm = false)
    { if (!confirm) { await RespondAsync("Run again with confirm:true to unlink your own account.", ephemeral: true); return; } await Identity("unlink"); }
    [SlashCommand("reset", "Send your own one-time password recovery link by DM")]
    public Task Reset() => Identity("reset");
    [SlashCommand("status", "Show your own link status")]
    public Task Status() => Identity("status");
    [SlashCommand("invite", "Create a signup invitation using administrator-approved defaults and limits")]
    public async Task AccountInvite([Summary("uses", "Maximum account signups; administrator limits apply")] int? uses = null, [Summary("expires-in-minutes", "Invitation lifetime in minutes; administrator limits apply")] int? minutes = null)
    {
        await DeferAsync(ephemeral:true);
        try {
            var c=store.Read().Settings; var count=uses??c.InviteDefaultUses; var expiry=minutes??c.InviteDefaultExpiryMinutes;
            if(count<1||count>c.InviteMaxUses||expiry<15||expiry>c.InviteMaxExpiryMinutes) { await FollowupAsync(runtime.Failure("invite_limits",$"Choose uses from 1–{c.InviteMaxUses} and expiry from 15–{c.InviteMaxExpiryMinutes} minutes. No invitation was created."),ephemeral:true,allowedMentions:AllowedMentions.None);runtime.Interactions.Record("invite.create","rejected_limits"); return; }
            var result=await runtime.IdentityAsync("invite.create",Context.User.Id,CancellationToken.None,Context.User.Username,new{uses=count,expiresInMinutes=expiry});
            await FollowupAsync(result.GetProperty("message").GetString(),ephemeral:true,allowedMentions:AllowedMentions.None);
            runtime.Interactions.Record("invite.create","completed");
        } catch(AccountCommandException error) { await FollowupAsync(runtime.Failure(error.Code,error.Message,error.ErrorId),ephemeral:true,allowedMentions:AllowedMentions.None); }
        catch { await FollowupAsync(runtime.Failure("invite_uncertain","Invitation could not be confirmed. Check existing invitations before creating another."),ephemeral:true,allowedMentions:AllowedMentions.None); }
    }
    [SlashCommand("watchparty", "Create a Private SyncPlay link in this channel, or DM an explicitly selected user")]
    public async Task WatchParty([Summary("user", "Optional @user: DM the link instead of posting it in this approved channel")] IUser? user = null)
    {
        await DeferAsync(ephemeral:true);
        try {
            var c=store.Read().Settings;
            if(user?.IsBot==true)throw AccountCommandException.FromCode("invalid_party_recipient");
            if(user is null&&!c.AllowedChannelIds.Contains(Context.Channel.Id.ToString())) { await FollowupAsync("This channel is not approved. Use an approved channel or specify @user for a DM.",ephemeral:true,allowedMentions:AllowedMentions.None);return; }
            var identity=await runtime.IdentityAsync("identity",Context.User.Id,CancellationToken.None);var native=identity.GetProperty("userId").GetGuid();
            BridgePermissions.Require(c,user is null?"party.share.channel":"party.share.dm",native);
            var link=await parties.InviteAsync(native,CancellationToken.None);
            var currentIdentity=await runtime.IdentityAsync("identity",Context.User.Id,CancellationToken.None);
            if(currentIdentity.GetProperty("userId").GetGuid()!=native)throw new UnauthorizedAccessException();
            var current=store.Read();var latest=current.Settings;if(!latest.Enabled||current.Revision!=link.BridgeRevision)throw new UnauthorizedAccessException();
            BridgePermissions.Require(latest,"party.invite",native);if(latest.WatchPartyGuests)BridgePermissions.Require(latest,"party.guests",native);
            BridgePermissions.Require(latest,user is null?"party.share.channel":"party.share.dm",native);
            await runtime.SendAsync((user?.Id??Context.Channel.Id).ToString(),user is not null,"Jellyfin watch-party invitation:\n"+link.Url,CancellationToken.None);
            await FollowupAsync(user is null?"Invitation posted in this channel.":"Invitation sent by DM to the selected user.",ephemeral:true,allowedMentions:AllowedMentions.None);
            runtime.Interactions.Record("watchparty",user is null?"delivered_channel":"delivered_dm");
        } catch(AccountCommandException error){runtime.Interactions.Record("watchparty","unavailable");await FollowupAsync(runtime.Failure(error.Code,error.Message,error.ErrorId),ephemeral:true,allowedMentions:AllowedMentions.None);}
        catch(WatchPartyException error)when(error.StatusCode is 400 or 403 or 409 or 429){runtime.Interactions.Record("watchparty","unavailable");var safe=AccountCommandException.FromCode("native_party_denied");await FollowupAsync(runtime.Failure(safe.Code,safe.Message,error.ErrorId),ephemeral:true,allowedMentions:AllowedMentions.None);}
        catch(Exception error) {runtime.Interactions.Record("watchparty","unavailable"); await FollowupAsync(runtime.Failure("party_invite_uncertain","Unable to confirm the invitation or delivery. Check the selected destination and native party before trying again; no automatic resend was made.",(error as WatchPartyException)?.ErrorId),ephemeral:true,allowedMentions:AllowedMentions.None); }
    }
    private async Task Identity(string operation)
    {
        await DeferAsync(ephemeral: true);
        try
        {
            var result = await runtime.IdentityAsync(operation, Context.User.Id, CancellationToken.None,Context.User.Username);
            var message=result.GetProperty("message").GetString();
            await FollowupAsync(message, ephemeral: true, allowedMentions: AllowedMentions.None);
            runtime.Interactions.Record(operation, "completed");
        }
        catch (AccountCommandException error) { runtime.Interactions.Record(operation, "unavailable"); await FollowupAsync(runtime.Failure(error.Code,error.Message,error.ErrorId), ephemeral: true, allowedMentions: AllowedMentions.None); }
        catch (Exception error) { runtime.Interactions.Record(operation, "unavailable");var timeout=error is OperationCanceledException or TimeoutException;await FollowupAsync(runtime.Failure(timeout?"account_timeout":"account_unavailable",timeout?"The request timed out. Check your account.":"Account service unavailable. Contact an administrator."), ephemeral: true, allowedMentions: AllowedMentions.None); }
    }
}
