using System.Security.Cryptography;

namespace AccountHub.Core;

/// <summary>Volatile, bounded sign-in transport only. Never consumes or confirms a Discord challenge.</summary>
public sealed class LinkContinuationStore(TimeProvider clock)
{
    private sealed class Entry(string token,DateTimeOffset expires)
    {
        public string Token { get; }=token;
        public DateTimeOffset Expires { get; }=expires;
    }
    private readonly object gate=new();
    private readonly Dictionary<string,Entry> entries=new(StringComparer.Ordinal);
    public const int Capacity=1024;
    private static bool Valid(string? value)=>value is {Length:43}&&value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_');
    private void Prune(DateTimeOffset now){foreach(var key in entries.Where(value=>value.Value.Expires<=now).Select(value=>value.Key).ToArray())entries.Remove(key);}
    public string Prepare(string token,DateTimeOffset challengeExpires,string? previousNonce=null)
    {
        if(!Valid(token))throw new HubException("invalid_request","Invalid link input");
        lock(gate){
            var now=clock.GetUtcNow();Prune(now);
            if(challengeExpires<=now)throw new HubException("link_expired","Link unavailable");
            var previous=Valid(previousNonce)?DiscordLinkService.Hash(previousNonce!):null;
            if(entries.Count>=Capacity&&(previous is null||!entries.ContainsKey(previous)))throw new HubException("link_continuation_unavailable","Link continuation capacity reached",true);
            string nonce,key;
            do{nonce=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');key=DiscordLinkService.Hash(nonce);}while(entries.ContainsKey(key));
            if(previous is not null)entries.Remove(previous);
            var maximum=now.AddMinutes(5);entries.Add(key,new(token,challengeExpires<maximum?challengeExpires:maximum));return nonce;
        }
    }
    public string? Take(string? nonce)
    {
        if(!Valid(nonce))return null;
        lock(gate){Prune(clock.GetUtcNow());return entries.Remove(DiscordLinkService.Hash(nonce!),out var entry)?entry.Token:null;}
    }
}
