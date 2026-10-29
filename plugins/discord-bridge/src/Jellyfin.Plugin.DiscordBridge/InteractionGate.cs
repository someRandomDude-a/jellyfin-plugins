namespace Jellyfin.Plugin.DiscordBridge;

public sealed record InteractionDenial(string Code,string Message);
// A redelivered gateway event must never race the original interaction's reply
// or cause another account effect. Durable receipts remain consumed on failure.
public sealed class InteractionGate(BridgeStore store)
{
    public async Task HandleAsync(string id,string? guild,Func<Task> dispatch,Func<InteractionDenial,Task> deny)
    {
        var current=store.Read().Settings;
        if(!current.Enabled){await deny(new("bot_disabled","The bot is disabled. Ask an administrator to check Discord settings."));return;}
        if(guild!=current.GuildId){await deny(new("wrong_server","Run this command in the configured Discord server, not in bot DMs or another server. Password recovery links are delivered privately by DM."));return;}
        var claim=store.TryClaim(id);
        if(claim==InteractionClaim.Duplicate)return; // The original attempt owns the acknowledgment.
        if(claim==InteractionClaim.CapacityReached){await deny(new("interaction_capacity","Discord interaction storage is full. Ask an administrator to review Bridge storage; no action was attempted."));return;}
        try{await dispatch();store.CompleteInteraction(id,true);}
        catch{store.CompleteInteraction(id,false);throw;}
    }
}
