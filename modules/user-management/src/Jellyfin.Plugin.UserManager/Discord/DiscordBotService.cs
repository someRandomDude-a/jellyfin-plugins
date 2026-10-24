using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Jellyfin.Plugin.UserManager.Services;

namespace Jellyfin.Plugin.UserManager.Discord;

/// <summary>
/// Hosts the embedded Discord bot. The bot reconnects automatically when settings change.
/// </summary>
public sealed class DiscordBotService : IHostedService, IDiscordMessageSender, IAsyncDisposable
{
    private readonly PluginSettingsService _settings;
    private readonly IServiceProvider _services;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private CancellationTokenSource? _sessionCts;
    private DiscordSocketClient? _client;
    private InteractionService? _interactions;
    private volatile bool _connected;

    public DiscordBotService(
        PluginSettingsService settings,
        IServiceProvider services,
        ILogger<DiscordBotService> logger)
    {
        _settings = settings;
        _services = services;
        _logger = logger;
    }

    public bool IsConnected => _connected;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionCts?.Cancel();
        if (_client is not null)
        {
            try
            {
                await _client.StopAsync().ConfigureAwait(false);
                await _client.LogoutAsync().ConfigureAwait(false);
            }
            catch
            {
                // Shutdown is best effort.
            }
        }
    }

    /// <summary>
    /// Stops the current gateway session and lets the host loop reconnect with fresh settings.
    /// </summary>
    public void RequestRestart()
    {
        try
        {
            _sessionCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already gone
        }
    }

    public async Task<bool> SendDirectMessageAsync(string discordUserId, string message, CancellationToken cancellationToken = default)
    {
        if (!ulong.TryParse(discordUserId, out var id))
        {
            return false;
        }

        var client = _client;
        if (client is null || client.ConnectionState != ConnectionState.Connected)
        {
            _logger.LogDebug("Cannot DM {DiscordId}: bot is not connected", discordUserId);
            return false;
        }

        try
        {
            IUser? user = client.GetUser(id);
            user ??= await client.Rest.GetUserAsync(id).ConfigureAwait(false);
            if (user is null)
            {
                return false;
            }

            var channel = await user.CreateDMChannelAsync().ConfigureAwait(false);
            await channel.SendMessageAsync(message).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to DM Discord user {DiscordId}", discordUserId);
            return false;
        }
    }

    /// <summary>
    /// Main reconnect loop. This is invoked by the host through <see cref="DiscordBotHostedService"/>.
    /// </summary>
    public async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            PluginSettings settings;
            try
            {
                settings = await _settings.GetAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not read Discord settings");
                await SafeDelayAsync(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (string.IsNullOrWhiteSpace(settings.DiscordBotToken))
            {
                await SafeDelayAsync(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
                continue;
            }

            await _sessionLock.WaitAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _sessionCts = linked;
                await RunSessionAsync(settings, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Discord bot restarting");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Discord bot session failed");
            }
            finally
            {
                _connected = false;
                _client = null;
                _interactions = null;
                _sessionCts = null;
                _sessionLock.Release();
            }

            await SafeDelayAsync(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunSessionAsync(PluginSettings settings, CancellationToken sessionToken)
    {
        var config = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.DirectMessages,
            AlwaysDownloadUsers = false,
            MessageCacheSize = 0,
            LogLevel = LogSeverity.Info
        };

        var client = new DiscordSocketClient(config);
        var interactions = new InteractionService(client, new InteractionServiceConfig
        {
            DefaultRunMode = RunMode.Async,
            LogLevel = LogSeverity.Info,
            UseCompiledLambda = false
        });

        _client = client;
        _interactions = interactions;

        client.Log += OnDiscordLogAsync;
        interactions.Log += OnDiscordLogAsync;

        client.Ready += async () =>
        {
            try
            {
                if (ulong.TryParse(settings.DiscordGuildId, out var guildId))
                {
                    await interactions.RegisterCommandsToGuildAsync(guildId, true).ConfigureAwait(false);
                    _logger.LogInformation("Discord slash commands registered to guild {GuildId}", guildId);
                }
                else
                {
                    await interactions.RegisterCommandsGloballyAsync(true).ConfigureAwait(false);
                    _logger.LogInformation("Discord slash commands registered globally");
                }

                _connected = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register Discord slash commands");
            }
        };

        client.InteractionCreated += async interaction =>
        {
            try
            {
                var context = new SocketInteractionContext(client, interaction);
                await interactions.ExecuteCommandAsync(context, _services).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Discord interaction failed");
                if (!interaction.HasResponded)
                {
                    try
                    {
                        await interaction.RespondAsync("Something went wrong. Please try again.", ephemeral: true)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // interaction may have expired
                    }
                }
                else
                {
                    try
                    {
                        await interaction.FollowupAsync("Something went wrong. Please try again.", ephemeral: true)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // interaction may have expired
                    }
                }
            }
        };

        try
        {
            await interactions.AddModulesAsync(typeof(DiscordBotService).Assembly, _services).ConfigureAwait(false);
            await client.LoginAsync(TokenType.Bot, settings.DiscordBotToken).ConfigureAwait(false);
            await client.StartAsync().ConfigureAwait(false);
            _logger.LogInformation("Discord bot connected as {User}", client.CurrentUser?.Username ?? "unknown");

            await Task.Delay(Timeout.InfiniteTimeSpan, sessionToken).ConfigureAwait(false);
        }
        finally
        {
            _connected = false;
            try
            {
                await client.StopAsync().ConfigureAwait(false);
                await client.LogoutAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignore shutdown errors
            }

            client.Log -= OnDiscordLogAsync;
            interactions.Log -= OnDiscordLogAsync;
            client.Dispose();
        }
    }

    private Task OnDiscordLogAsync(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            _ => LogLevel.Trace
        };

        _logger.Log(level, message.Exception, "Discord: {Message}", message.Message);
        return Task.CompletedTask;
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _sessionLock.Dispose();
    }
}

/// <summary>
/// Hosted-service adapter for the reconnect loop.
/// </summary>
public sealed class DiscordBotHostedService : BackgroundService
{
    private readonly DiscordBotService _bot;

    public DiscordBotHostedService(DiscordBotService bot)
    {
        _bot = bot;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _bot.RunLoopAsync(stoppingToken);
}
