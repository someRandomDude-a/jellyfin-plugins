using System.Text.Json;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using DiscordBridge.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DiscordBridge;

public sealed class BridgeRuntime(IServiceProvider services, ILogger<BridgeRuntime> logger) : BackgroundService
{
    public string InstanceCode { get; }=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3));
    public string Failure(string code,string message,string? errorId=null)=>AccountHub.Api.UserErrors.Message(code)+"\nError ID: "+(Guid.TryParseExact(errorId,"D",out _)?errorId:Jellyfin.PluginSupport.SuiteDiagnostics.Record(services,"discord-bridge",code,"Discord command"));
    private DiscordSocketClient? client;
    private DateTimeOffset nextAlert;
    private async Task ReportAlertAsync(string type, long revision, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < nextAlert) return;
        nextAlert = DateTimeOffset.UtcNow.AddMinutes(5);
        try {
            await services.GetRequiredService<AccountHub.Api.NotificationsClient>().AlertAdministratorsAsync("discord-bridge", type, "Discord needs attention",
                "Check the saved bot settings and test the Discord connection.", link: "#/configurationpage?name=DiscordBridge",
                actions: [new("Open Discord settings", "#/configurationpage?name=DiscordBridge")],
                dedupeKey: $"discord-alert:{type}:{revision}:{DateTimeOffset.UtcNow:yyyy-MM-dd}", ct: ct);
        } catch { /* Optional reporting must not disrupt transport recovery. */ }
    }
    public string? ApplicationId => client?.CurrentUser?.Id.ToString();
    public string? BotUsername => client?.CurrentUser?.Username;
    public async Task TestConnectionAsync(CancellationToken ct)
    {
        var socket = client;
        if (!Store.Read().Settings.Enabled || socket?.ConnectionState != ConnectionState.Connected)
            throw new InvalidOperationException("Discord unavailable");
        // Authenticated read only: never create DMs, send messages or register commands.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await socket.GetApplicationInfoAsync(new RequestOptions { CancelToken = deadline.Token })
            .WaitAsync(TimeSpan.FromSeconds(15), ct);
    }
    public InteractionHealth Interactions { get; } = new();
    public bool Connected => client?.ConnectionState == ConnectionState.Connected;
    public string State { get; private set; } = "starting";
    private BridgeStore Store => services.GetRequiredService<BridgeStore>();
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            long revision = 0;
            try
            {
                var snapshot = Store.Read();
                revision = snapshot.Revision;
                if (!snapshot.Settings.Enabled) { State = "disabled"; await Task.Delay(5000, stoppingToken); continue; }
                if (string.IsNullOrEmpty(snapshot.Token)) {
                    State = "token_required"; await ReportAlertAsync("transport.token_missing", snapshot.Revision, stoppingToken);
                    await Task.Delay(5000, stoppingToken); continue;
                }
                using var socket = new DiscordSocketClient(new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds, AlwaysDownloadUsers = false, MessageCacheSize = 0 });
                using var interactions = new InteractionService(socket.Rest);
                client = socket; State = "connecting";
                await interactions.AddModulesAsync(typeof(SlashCommands).Assembly, services);
                socket.Ready += async () =>
                {
                    try { await interactions.RegisterCommandsToGuildAsync(ulong.Parse(snapshot.Settings.GuildId)); State = "connected"; }
                    catch { State = "command_registration_failed"; await ReportAlertAsync("transport.commands_failed", snapshot.Revision, stoppingToken); }
                };
                socket.InteractionCreated += async interaction =>
                {
                    try
                    {
                        await new InteractionGate(Store).HandleAsync(interaction.Id.ToString(),interaction.GuildId?.ToString(),async()=>{
                            var result = await interactions.ExecuteCommandAsync(new SocketInteractionContext(socket, interaction), services);
                            if(!result.IsSuccess){logger.LogWarning("Discord command dispatch was rejected ({Failure}); no interaction payload is logged.",result.Error);
                                if(!interaction.HasResponded)await interaction.RespondAsync(Failure("command_unavailable","Unable to complete this command."),ephemeral:true,allowedMentions:AllowedMentions.None);}
                        },denial=>interaction.RespondAsync(Failure(denial.Code,denial.Message),ephemeral:true,allowedMentions:AllowedMentions.None));
                    }
                    catch { if (!interaction.HasResponded) { try { await interaction.RespondAsync(Failure("gateway_unavailable","Discord is unavailable. Contact an administrator."), ephemeral: true, allowedMentions: AllowedMentions.None); } catch { } } }
                };
                await socket.LoginAsync(TokenType.Bot, snapshot.Token).WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
                await socket.StartAsync().WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
                while (!stoppingToken.IsCancellationRequested && Store.Read().Revision == snapshot.Revision) await Task.Delay(5000, stoppingToken);
                await socket.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { State = "unavailable"; logger.LogWarning("Discord Bridge work deferred. Inspect configuration/credential and key health; no message, URL or credential is logged."); await ReportAlertAsync("transport.unavailable", revision, stoppingToken); }
            finally { client = null; }
            try { await Task.Delay(5000, stoppingToken); } catch (OperationCanceledException) { break; }
        }
        State = "stopped";
    }

    public async Task<JsonElement> IdentityAsync(string operation, ulong discordId, CancellationToken ct,string? discordUsername=null, object? options=null)
    {
        if (!Store.Read().Settings.EnableSelfServiceCommands && operation != "identity") throw new AccountCommandException("Account commands are disabled. Ask your administrator to enable account self-service in Discord settings.","account_commands_disabled");
        if (operation != "identity")
        {
            Guid? user = null;
            if (operation != "link.prepare") user = (await InvokeIdentityAsync("identity", discordId, ct)).GetProperty("userId").GetGuid();
            try {
                var config = Store.Read().Settings;
                // Unlinked Discord users have no native ID yet. A challenge is not
                // a grant: confirmation must check the actual signed-in account.
                if (operation == "link.prepare") {
                    if (!BridgePermissions.Allowed(config, "link") && !config.Permissions.Users.Keys.Any(id => BridgePermissions.Allowed(config, "link", id))) throw new UnauthorizedAccessException();
                } else BridgePermissions.Require(config, operation, user);
            }
            catch (UnauthorizedAccessException) { throw new AccountCommandException("This command is disabled for your account. Ask the Jellyfin administrator to review Discord permissions.", "permission_denied"); }
        }
        return await InvokeIdentityAsync(operation, discordId, ct, discordUsername, options);
    }
    private async Task<JsonElement> InvokeIdentityAsync(string operation, ulong discordId, CancellationToken ct, string? discordUsername = null, object? options = null)
    {
        var handler = services.GetKeyedService<Func<string, CancellationToken, Task<string>>>(DiscordBridgeClient.IdentityKey)
            ?? throw new AccountCommandException("Account commands need Account Hub. Ask your administrator to install/enable it and restart Jellyfin, then use /link.","account_provider_missing");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var reply = await handler(JsonSerializer.Serialize(new { apiVersion = "1", operation, discordId = discordId.ToString(),discordUsername=DiscordIdentityLabel.Username(discordUsername),options }), deadline.Token).WaitAsync(deadline.Token);
        if (reply.Length > 65536) throw new InvalidDataException();
        using var doc = JsonDocument.Parse(reply);
        if (doc.RootElement.GetProperty("apiVersion").GetString() != "1") throw new InvalidOperationException("Unsupported account response.");
        if (!doc.RootElement.GetProperty("ok").GetBoolean()) {
            var error=doc.RootElement.TryGetProperty("error",out var failure)&&failure.ValueKind==JsonValueKind.Object?failure:default;
            throw AccountCommandException.FromCode(error.ValueKind==JsonValueKind.Object&&error.TryGetProperty("code",out var code)?code.GetString():null,error.ValueKind==JsonValueKind.Object&&error.TryGetProperty("errorId",out var id)?id.GetString():null);
        }
        return doc.RootElement.GetProperty("data").Clone();
    }

    public async Task SendAsync(string destination, bool directMessage, string text, CancellationToken ct, Embed? mediaCard = null)
    {
        if (!BridgeStore.Snowflake(destination) || text.Length is < 1 or > 2000) throw new ArgumentException("Invalid delivery.");
        var current = Store.Read();
        var socket = client;
        if (!current.Settings.Enabled || socket?.ConnectionState != ConnectionState.Connected) throw new InvalidOperationException("Discord unavailable.");
        IMessageChannel target;
        var options = new RequestOptions { CancelToken = ct };
        if (directMessage)
        {
            var recipient = await socket.Rest.GetUserAsync(ulong.Parse(destination), options);
            target = await recipient.CreateDMChannelAsync(options);
        }
        else
        {
            if (!current.Settings.AllowedChannelIds.Contains(destination)) throw new UnauthorizedAccessException("Channel not approved.");
            var channel = await socket.Rest.GetChannelAsync(ulong.Parse(destination), options);
            if (channel is not IMessageChannel messageChannel || channel is not IGuildChannel guildChannel || guildChannel.GuildId.ToString() != current.Settings.GuildId) throw new UnauthorizedAccessException("Wrong guild or channel type.");
            target = messageChannel;
        }
        await target.SendMessageAsync(text, embed: mediaCard, allowedMentions: AllowedMentions.None, options: options).WaitAsync(TimeSpan.FromSeconds(20), ct);
    }

    public async Task<string> DispatchAsync(string request, CancellationToken ct)
    {
        object response;
        try
        {
            if (System.Text.Encoding.UTF8.GetByteCount(request) > 16384) throw new ArgumentException();
            using var doc = JsonDocument.Parse(request, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.GetProperty("apiVersion").GetString() != "1") throw new ArgumentException();
            var operation = root.GetProperty("operation").GetString();
            if (operation == "health") response = new { apiVersion = "1", ok = true, data = new { connected = Connected, state = State } };
            else if (operation == "library.publish")
            {
                var notice = root.GetProperty("payload").GetProperty("notice").Deserialize<Jellyfin.LibraryNotifications.LibraryNotice>(Jellyfin.LibraryNotifications.LibraryNoticeFactory.Json) ?? throw new ArgumentException();
                var accepted = services.GetRequiredService<BridgeLibraryNotices>().Accept(notice);
                response = new { apiVersion = "1", ok = accepted, data = new { status = accepted ? "accepted_or_skipped" : "capacity_reached" } };
            }
            else if(operation=="identity.profile"){
                var id=root.GetProperty("payload").GetProperty("discordId").GetString();if(!BridgeStore.Snowflake(id))throw new ArgumentException();
                var socket=client;if(!Store.Read().Settings.Enabled||socket?.ConnectionState!=ConnectionState.Connected)throw new InvalidOperationException();
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(5));
                var user=await socket.Rest.GetUserAsync(ulong.Parse(id!),new RequestOptions{CancelToken=deadline.Token}).WaitAsync(deadline.Token);
                response=new{apiVersion="1",ok=true,data=new{discordId=user.Id.ToString(),username=DiscordIdentityLabel.Username(user.Username)}};
            }
            else if (operation is "send" or "send.secret")
            {
                var payload = root.GetProperty("payload");
                var direct = payload.GetProperty("directMessage").GetBoolean();
                if (operation == "send.secret" && (!direct || payload.GetProperty("expiresAt").GetDateTimeOffset() <= DateTimeOffset.UtcNow || payload.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddHours(6))) throw new ArgumentException();
                var destination=payload.GetProperty("destination").GetString()!;var text=payload.GetProperty("text").GetString()!;
                if(!BridgeStore.Snowflake(destination)||text is not {Length:>0 and <=2000})throw new ArgumentException();
                var current=Store.Read();if(!direct&&!current.Settings.AllowedChannelIds.Contains(destination))throw new UnauthorizedAccessException();
                Guid? deliveryId=null;
                if(payload.TryGetProperty("deliveryId",out var delivery)){
                    if(!Guid.TryParse(delivery.GetString(),out var parsed)||parsed==Guid.Empty)throw new ArgumentException();deliveryId=parsed;
                    var fingerprint=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{operation,destination,direct,text}))));
                    // Read an existing receipt even when offline; a delivered message
                    // may be acknowledged, but claimed/uncertain work is never resent.
                    ct.ThrowIfCancellationRequested();var status=Store.BeginDelivery(parsed,fingerprint,current.Settings.Enabled&&Connected);
                    if(status=="unavailable")return JsonSerializer.Serialize(new{apiVersion="1",ok=false,error=new{code="unavailable",retryable=true}});
                    if(status=="delivered")return JsonSerializer.Serialize(new{apiVersion="1",ok=true,data=new{delivered=true,deduplicated=true}});
                    if(status!="new")return JsonSerializer.Serialize(new{apiVersion="1",ok=false,error=new{code="outcome_uncertain",retryable=false}});
                }
                try{await SendAsync(destination,direct,text,ct);if(deliveryId is {} sent)Store.CompleteDelivery(sent,true);}
                catch{if(deliveryId is {} failed){Store.CompleteDelivery(failed,false);ct.ThrowIfCancellationRequested();return JsonSerializer.Serialize(new{apiVersion="1",ok=false,error=new{code="outcome_uncertain",retryable=false}});}throw;}
                response = new { apiVersion = "1", ok = true, data = new { delivered = true } };
            }
            else throw new ArgumentException();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { response = new { apiVersion = "1", ok = false, error = new { code = "denied", retryable = false } }; }
        catch (ArgumentException) { response = new { apiVersion = "1", ok = false, error = new { code = "invalid_request", retryable = false } }; }
        catch { response = new { apiVersion = "1", ok = false, error = new { code = "unavailable", retryable = true } }; }
        return JsonSerializer.Serialize(response);
    }
}
