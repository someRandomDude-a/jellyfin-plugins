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
    private DiscordSocketClient? client;
    public string? ApplicationId => client?.CurrentUser?.Id.ToString();
    public InteractionHealth Interactions { get; } = new();
    public bool Connected => client?.ConnectionState == ConnectionState.Connected;
    public string State { get; private set; } = "starting";
    private BridgeStore Store => services.GetRequiredService<BridgeStore>();
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = Store.Read();
                if (!snapshot.Settings.Enabled || string.IsNullOrEmpty(snapshot.Token)) { State = "disabled"; await Task.Delay(5000, stoppingToken); continue; }
                using var socket = new DiscordSocketClient(new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds, AlwaysDownloadUsers = false, MessageCacheSize = 0 });
                using var interactions = new InteractionService(socket.Rest);
                client = socket; State = "connecting";
                await interactions.AddModulesAsync(typeof(SlashCommands).Assembly, services);
                socket.Ready += async () =>
                {
                    try { await interactions.RegisterCommandsToGuildAsync(ulong.Parse(snapshot.Settings.GuildId)); State = "connected"; }
                    catch { State = "command_registration_failed"; }
                };
                socket.InteractionCreated += async interaction =>
                {
                    try
                    {
                        var current = Store.Read();
                        if (!current.Settings.Enabled || interaction.GuildId?.ToString() != current.Settings.GuildId || !Store.Claim(interaction.Id.ToString()))
                        { await interaction.RespondAsync("This interaction is unavailable or was already handled. No action was retried.", ephemeral: true, allowedMentions: AllowedMentions.None); return; }
                        var result = await interactions.ExecuteCommandAsync(new SocketInteractionContext(socket, interaction), services);
                        if (!result.IsSuccess && !interaction.HasResponded) await interaction.RespondAsync("The command could not be completed. No automatic retry was made.", ephemeral: true, allowedMentions: AllowedMentions.None);
                    }
                    catch { if (!interaction.HasResponded) { try { await interaction.RespondAsync("Discord integration is unavailable.", ephemeral: true, allowedMentions: AllowedMentions.None); } catch { } } }
                };
                await socket.LoginAsync(TokenType.Bot, snapshot.Token).WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
                await socket.StartAsync().WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
                while (!stoppingToken.IsCancellationRequested && Store.Read().Revision == snapshot.Revision) await Task.Delay(5000, stoppingToken);
                await socket.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { State = "unavailable"; logger.LogWarning("Discord Bridge work deferred. Inspect configuration/credential and key health; no message, URL or credential is logged."); }
            finally { client = null; }
            try { await Task.Delay(5000, stoppingToken); } catch (OperationCanceledException) { break; }
        }
        State = "stopped";
    }

    public async Task<JsonElement> IdentityAsync(string operation, ulong discordId, CancellationToken ct)
    {
        if (!Store.Read().Settings.EnableSelfServiceCommands && operation != "identity") throw new InvalidOperationException("Enable account self-service commands in Discord Bridge settings.");
        var handler = services.GetKeyedService<Func<string, CancellationToken, Task<string>>>(DiscordBridgeClient.IdentityKey)
            ?? throw new InvalidOperationException("Install and enable Account Hub, restart Jellyfin, and use /link to verify your account. Bot transport remains available without it.");
        var reply = await handler(JsonSerializer.Serialize(new { apiVersion = "1", operation, discordId = discordId.ToString() }), ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
        if (reply.Length > 65536) throw new InvalidDataException();
        using var doc = JsonDocument.Parse(reply);
        if (doc.RootElement.GetProperty("apiVersion").GetString() != "1" || !doc.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidOperationException("Account Hub denied this operation.");
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
            else if (operation is "send" or "send.secret")
            {
                var payload = root.GetProperty("payload");
                var direct = payload.GetProperty("directMessage").GetBoolean();
                if (operation == "send.secret" && (!direct || payload.GetProperty("expiresAt").GetDateTimeOffset() <= DateTimeOffset.UtcNow || payload.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddHours(6))) throw new ArgumentException();
                await SendAsync(payload.GetProperty("destination").GetString()!, direct, payload.GetProperty("text").GetString()!, ct);
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
