using Discord;
using Discord.Interactions;

namespace Jellyfin.Plugin.DiscordBridge;

public sealed class SlashCommands(BridgeRuntime runtime) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("help", "Explain Jellyfin self-service commands")]
    public async Task Help()
    {
        await RespondAsync("Use /link to confirm your Jellyfin identity, /unlink, /reset, /status, or /watchparty. Administration stays in Jellyfin. Never post a password, token or 2FA code here.", ephemeral: true, allowedMentions: AllowedMentions.None);
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
    private async Task Identity(string operation)
    {
        await DeferAsync(ephemeral: true);
        try
        {
            var result = await runtime.IdentityAsync(operation, Context.User.Id, CancellationToken.None);
            await FollowupAsync(result.GetProperty("message").GetString(), ephemeral: true, allowedMentions: AllowedMentions.None);
            runtime.Interactions.Record(operation, "completed");
        }
        catch { runtime.Interactions.Record(operation, "unavailable"); await FollowupAsync("Account self-service is unavailable. Check Discord Bridge settings for missing dependencies or disabled features. Install/enable Account Hub and restart, then use /link. No action was retried.", ephemeral: true, allowedMentions: AllowedMentions.None); }
    }
}

[Group("watchparty", "Create invitations using your own active Jellyfin session")]
public sealed class WatchPartySlashCommands(BridgeRuntime runtime, BridgeStore store, WatchPartyCommands parties) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("devices", "List your active Jellyfin devices privately")]
    public async Task Devices()
    {
        await DeferAsync(ephemeral: true);
        try
        {
            if (!store.Read().Settings.EnableWatchPartyCommands) { await FollowupAsync("Watch-party commands are disabled. Enable them in Discord Bridge settings after installing Account Hub and Private SyncPlay.", ephemeral: true, allowedMentions: AllowedMentions.None); return; }
            var identity = await runtime.IdentityAsync("identity", Context.User.Id, CancellationToken.None);
            var text = parties.Devices(identity.GetProperty("userId").GetGuid());
            await FollowupAsync(string.IsNullOrEmpty(text) ? "Open Jellyfin first; no active device was found." : text, ephemeral: true, allowedMentions: AllowedMentions.None);
            runtime.Interactions.Record("watchparty.devices", "completed");
        }
        catch { await FollowupAsync("A verified, enabled Jellyfin account link is required.", ephemeral: true, allowedMentions: AllowedMentions.None); }
    }
    [SlashCommand("invite", "Create a one-use watch-party invite, optionally shared to a user or approved channel")]
    public async Task Invite(int device = 1, bool guests = false, IUser? user = null, ITextChannel? channel = null, [Summary("confirm-share", "Confirm disclosing a bearer invitation to the selected channel")] bool confirmShare = false)
    {
        await DeferAsync(ephemeral: true);
        try
        {
            if (user is not null && channel is not null) throw new ArgumentException();
            var config = store.Read().Settings;
            if (channel is not null && (!confirmShare || channel.GuildId != Context.Guild.Id || channel.GuildId.ToString() != config.GuildId || !config.AllowedChannelIds.Contains(channel.Id.ToString())))
            { await FollowupAsync("Channel sharing requires confirm-share:true and an administrator-approved channel in this guild. Anyone receiving the bearer link may redeem it.", ephemeral: true, allowedMentions: AllowedMentions.None); return; }
            var identity = await runtime.IdentityAsync("identity", Context.User.Id, CancellationToken.None);
            var link = await parties.InviteAsync(identity.GetProperty("userId").GetGuid(), device, guests, CancellationToken.None);
            var text = $"One-use Jellyfin watch-party invitation (expires in 30 minutes):\n{link}";
            if (channel is not null) await runtime.SendAsync(channel.Id.ToString(), false, text, CancellationToken.None);
            else if (user is not null) await runtime.SendAsync(user.Id.ToString(), true, text, CancellationToken.None);
            await FollowupAsync(channel is not null || user is not null ? "Invitation created and sent to the selected destination." : text, ephemeral: true, allowedMentions: AllowedMentions.None);
            runtime.Interactions.Record("watchparty.invite", channel is not null ? "delivered_channel" : user is not null ? "delivered_dm" : "created_private");
        }
        catch { await FollowupAsync("Invite creation or delivery could not be confirmed. Check your active Jellyfin party before trying again; no action was automatically retried. Guest and owner permissions still apply.", ephemeral: true, allowedMentions: AllowedMentions.None); }
    }
}
