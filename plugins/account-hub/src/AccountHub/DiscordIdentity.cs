using System.Text.Json;
using AccountHub.Core;
using DiscordBridge.Api;
using Jellyfin.Plugin.UserManager.Data;
using Jellyfin.Plugin.UserManager.Services;

namespace AccountHub;

public sealed class DiscordIdentity(PluginStore lifecycle, DiscordLinkService links, AccountService accounts, PluginSettingsService settings, HubHost host, DiscordBridgeClient bridge,InviteDefaultsService inviteDefaults,HubStore store,AccountHub.Groups.GroupPolicyService groups,AccountHub.Notifications.IngressLimiter limiter)
{
    public async Task<string> DispatchAsync(string request, CancellationToken ct)
    {
        try
        {
            if (request.Length > 4096) throw new ArgumentException();
            using var doc = JsonDocument.Parse(request);
            var root = doc.RootElement;
            if (root.GetProperty("apiVersion").GetString() != "1") throw new ArgumentException();
            var discord = root.GetProperty("discordId").GetString()!;
            if (!ulong.TryParse(discord, out var snowflake) || snowflake == 0) throw new ArgumentException();
            var incomingName=root.TryGetProperty("discordUsername",out var suppliedName)&&suppliedName.ValueKind==JsonValueKind.String?DiscordIdentityLabel.Username(suppliedName.GetString()):null;
            var operation = root.GetProperty("operation").GetString();
            var config = await settings.GetAsync(ct);
            if (operation == "link.prepare")
            {
                RequirePublicUrl(config.PublicBaseUrl);
                if(!limiter.Allow("discord-link:"+discord,3))throw new HubException("rate_limited","Link preparation limit reached");
                var token = await links.PrepareAsync(discord, ct);
                return Reply(new { message = "Review and confirm linking "+DiscordIdentityLabel.Format(incomingName,discord)+" to Jellyfin using this five-minute link. Your existing signed-in session is used when available:\n" + config.PublicBaseUrl!.TrimEnd('/') + "/AccountHub/discord/link#token=" + token });
            }
            var linked = await lifecycle.GetLinkByDiscordUserIdAsync(discord, ct);
            if (linked is null || !Guid.TryParse(linked.JellyfinUserId, out var user) || !await host.UserExistsAsync(user, ct)) throw new HubException("link_required","A verified enabled account is required");
            if (operation == "identity") return Reply(new { userId = user });
            if (!bridge.Permitted(user, operation!)) throw new HubException("forbidden", "Discord action not permitted");
            var identityLabel=DiscordIdentityLabel.Format(incomingName??linked.DiscordUsername,discord);
            if (operation == "status") return Reply(new { message = identityLabel+" is linked to your enabled Jellyfin account." });
            if(operation=="invite.create"){
                var revision=await store.ReadAsync(async(db,token)=>(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.State,token)).Revision,ct);
                var defaults=await inviteDefaults.ReadAsync(ct);
                if(!defaults.DiscordCreationEnabled||defaults.DefaultGroupId is not {} groupId||defaults.AllowedCreators?.Contains(user)!=true)throw new HubException("forbidden","Discord invitations are not permitted");
                RequirePublicUrl(config.PublicBaseUrl);
                if(!await host.LocalPasswordFlowsAllowedAsync(ct))throw new HubException("password_login_disabled","Local account creation is disabled");
                var group=await groups.GroupAsync(groupId,ct);
                var options=root.TryGetProperty("options",out var supplied)&&supplied.ValueKind==JsonValueKind.Object?supplied:default;
                var uses=options.ValueKind==JsonValueKind.Object&&options.TryGetProperty("uses",out var count)?count.GetInt32():defaults.DiscordDefaultUses;
                var expiry=options.ValueKind==JsonValueKind.Object&&options.TryGetProperty("expiresInMinutes",out var duration)?duration.GetInt32():defaults.DiscordExpiryMinutes;
                if(!bridge.InviteOptionsPermitted(user,uses,expiry))throw new HubException("forbidden","Discord invitation options are not permitted");
                _=Api.ManagedPolicy.Apply(new MediaBrowser.Model.Users.UserPolicy(),JsonSerializer.Deserialize<JsonElement>(group.PolicyJson));
                var result=await inviteDefaults.IssueDiscordAsync(user,Guid.NewGuid(),revision,groupId,group.PolicyJson,async token=>{
                    var current=await lifecycle.GetLinkByDiscordUserIdAsync(discord,token);if(current is null||!Guid.TryParse(current.JellyfinUserId,out var id)||id!=user||!await host.UserExistsAsync(user,token)||!bridge.Permitted(user,"invite.create")||!bridge.InviteOptionsPermitted(user,uses,expiry))throw new UnauthorizedAccessException();
                },ct,expectedDiscordId:discord,uses:uses,expiryMinutes:expiry);
                return Reply(new{message=result.Token is null?"The invitation was already issued and cannot be read back.":"Account signup invitation (maximum "+uses+" uses, expires in "+expiry+" minutes):\n"+config.PublicBaseUrl!.TrimEnd('/')+"/AccountHub/invite#token="+result.Token});
            }
            if (operation == "unlink")
            {
                await accounts.UnlinkAsync(user, user.ToString("N"), "Discord self-service", null, ct);
                return Reply(new { message = identityLabel+" was unlinked. Unused recovery links were revoked." });
            }
            if (operation == "reset")
            {
                if(!limiter.Allow("discord-reset:"+user.ToString("N"),3))throw new HubException("rate_limited","Recovery issuance limit reached");
                RequirePublicUrl(config.PublicBaseUrl);
                if (!await host.LocalPasswordFlowsAllowedAsync(ct)) throw new HubException("password_login_disabled","Local password recovery is disabled");
                var reset = await accounts.CreateResetTokenAsync(user, user, config.PublicBaseUrl!, TimeSpan.FromMinutes(5), ct, expectedDiscordUserId: discord);
                try {
                    var delivered = await bridge.CallAsync("send.secret", new { destination = discord, directMessage = true, text = "Your one-time Jellyfin password reset (five minutes):\nFor "+identityLabel+"\n" + reset.Url.Replace("/UserManager/reset", "/AccountHub/reset", StringComparison.Ordinal), expiresAt = reset.ExpiresAt }, ct);
                    if (delivered is not { } response || !response.GetProperty("ok").GetBoolean()) throw new HubException("delivery_unavailable","Private recovery delivery could not be confirmed");
                } catch {
                    // Even an uncertain DM must not leave an unacknowledged usable
                    // credential behind. Never erase a claimed password effect.
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await lifecycle.RevokeUnusedResetTokenAsync(DiscordLinkService.Hash(reset.Token),cleanup.Token);
                    throw;
                }
                return Reply(new { message = "Recovery for "+identityLabel+" was sent by DM. Check your private messages. The link lasts five minutes, preserves 2FA and does not sign you in automatically." });
            }
            throw new ArgumentException();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HubException error) { return JsonSerializer.Serialize(new { apiVersion = "1", ok = false, error = new { code = error.Code, retryable = false } }); }
        catch { return JsonSerializer.Serialize(new { apiVersion = "1", ok = false, error = new { code = "unavailable", retryable = false } }); }
    }
    private static string Reply(object data) => JsonSerializer.Serialize(new { apiVersion = "1", ok = true, data });
    private static void RequirePublicUrl(string? value) {
        if(!Uri.TryCreate(value,UriKind.Absolute,out var url)||url.Scheme!="https"||url.UserInfo.Length!=0||url.Query.Length!=0||url.Fragment.Length!=0)
            throw new HubException("public_url_required","Configure a trusted HTTPS public URL");
    }
}
