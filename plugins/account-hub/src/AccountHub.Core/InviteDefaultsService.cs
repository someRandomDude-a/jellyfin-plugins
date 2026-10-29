using System.Security.Cryptography;
using System.Text.Json;
using AccountHub.Core.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AccountHub.Core;

public sealed record InviteDefaults(Guid? DefaultGroupId=null,bool DiscordCreationEnabled=false,Guid[]? AllowedCreators=null,int DiscordExpiryMinutes=60,int DailyLimit=5)
{
    public int DiscordMaxUses { get; init; } = 100;
    public int DiscordDefaultUses { get; init; } = 1;
    public int DiscordMaxExpiryMinutes { get; init; } = 43200;
}
public sealed class InviteDefaultsService(HubStore store,CommandGate commands,TimeProvider clock)
{
    private const string Key="invites.defaults";
    public async Task<InviteDefaults> ReadAsync(CancellationToken ct=default){var json=await store.ReadSecretAsync(Key,ct);return json is null?new():JsonSerializer.Deserialize<InviteDefaults>(json)!;}
    public Task<JsonElement> SaveAsync(Guid actor,Guid command,long revision,InviteDefaults value,Func<CancellationToken,Task> authorize,CancellationToken ct=default){
        if(value.DefaultGroupId==Guid.Empty||value.DiscordMaxUses is <1 or >100||value.DiscordDefaultUses<1||value.DiscordDefaultUses>value.DiscordMaxUses||value.DiscordMaxExpiryMinutes is <15 or >43200||value.DiscordExpiryMinutes<15||value.DiscordExpiryMinutes>value.DiscordMaxExpiryMinutes||value.DailyLimit is <1 or >20||value.AllowedCreators is {Length:>100}||value.AllowedCreators?.Any(id=>id==Guid.Empty)==true||value.DiscordCreationEnabled&&value.DefaultGroupId is null)throw new HubException("invalid_request","Select a template and valid invite limits");
        return commands.ExecuteAsync(actor,"invites.defaults.changed",command,revision,value,authorize,async(db,token)=>{
            var row=await db.Secrets.FindAsync([Key],token);if(row is null){row=new IntegrationSecret{Name=Key};db.Secrets.Add(row);}row.Ciphertext=store.Protect(Key,JsonSerializer.Serialize(value));row.UpdatedAtUnixMs=clock.GetUtcNow().ToUnixTimeMilliseconds();return JsonSerializer.SerializeToElement(new{status="saved"});
        },ct);
    }
    public async Task<InviteIssuance> IssueDiscordAsync(Guid actor,Guid command,long revision,Guid template,string policy,Func<CancellationToken,Task> authorize,CancellationToken ct=default,string? expectedDiscordId=null,int? uses=null,int? expiryMinutes=null){
        string? raw=null;var result=await commands.ExecuteAsync(actor,"invite.discord.issued",command,revision,new{template,policy,uses,expiryMinutes},authorize,async(db,token)=>{
            var row=await db.Secrets.FindAsync([Key],token)??throw new HubException("forbidden","Discord invitations are disabled");
            var json=store.Protection.CreateProtector("AccountHub.IntegrationSecret.v1",Key).Unprotect(row.Ciphertext);var settings=JsonSerializer.Deserialize<InviteDefaults>(json)!;
            if(!settings.DiscordCreationEnabled||settings.DefaultGroupId!=template||settings.AllowedCreators?.Contains(actor)!=true)throw new HubException("forbidden","Discord invitations are not permitted for this account");
            var count=uses??settings.DiscordDefaultUses;var expiry=expiryMinutes??settings.DiscordExpiryMinutes;
            if(count<1||count>settings.DiscordMaxUses||expiry<15||expiry>settings.DiscordMaxExpiryMinutes)throw new HubException("invite_limits","Choose invitation options within the administrator's limits");
            if(expectedDiscordId is not null&&await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM linked_accounts WHERE JellyfinUserId = {actor.ToString("N")} AND DiscordUserId = {expectedDiscordId}").SingleAsync(token)!=1)throw new HubException("link_required","The verified Discord link changed");
            var now=clock.GetUtcNow().ToUnixTimeMilliseconds();if(await db.OnboardingInvites.CountAsync(i=>i.CreatedBy==actor&&i.CreatedAt>=now-86400000,token)>=settings.DailyLimit)throw new HubException("rate_limited","Daily invitation limit reached");
            raw=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');
            var invite=new OnboardingInvite{Id=Guid.NewGuid(),CreatedBy=actor,ApprovedBy=actor,CreatedAt=now,ExpiresAt=now+expiry*60000L,MaxUses=count,Note="Discord invitation",GroupId=template.ToString("D"),PolicyJson=policy,CopyLink=true,Status="active",TokenHash=InviteOnboardingService.Hash(raw)};db.OnboardingInvites.Add(invite);
            return JsonSerializer.SerializeToElement(new{id=invite.Id,status="active",credentialReadback=false});
        },ct);return new(result,raw);
    }
}
