using System.Text;
using Discord;
using Discord.Interactions;
using Jellyfin.Plugin.UserManager.Data;
using Jellyfin.Plugin.UserManager.Services;
using JfUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.UserManager.Discord;

/// <summary>
/// Slash commands for self-service and administration.
/// </summary>
public sealed class DiscordCommandModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly AccountService _accounts;
    private readonly JellyfinUserService _users;
    private readonly PresetService _presets;
    private readonly InviteService _invites;
    private readonly AuditService _audit;
    private readonly SeerrClient _seerr;
    private readonly PluginSettingsService _settings;
    private readonly IDiscordMessageSender _sender;

    public DiscordCommandModule(
        AccountService accounts,
        JellyfinUserService users,
        PresetService presets,
        InviteService invites,
        AuditService audit,
        SeerrClient seerr,
        PluginSettingsService settings,
        IDiscordMessageSender sender)
    {
        _accounts = accounts;
        _users = users;
        _presets = presets;
        _invites = invites;
        _audit = audit;
        _seerr = seerr;
        _settings = settings;
        _sender = sender;
    }

    [SlashCommand("link", "Link your Discord account to your Jellyfin account")]
    public async Task LinkAsync([Summary("code", "The code shown on the User Manager account page")] string code)
    {
        await DeferAsync(ephemeral: true).ConfigureAwait(false);
        var link = await _accounts.CompleteLinkAsync(
            code,
            Context.User.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Context.User.Username).ConfigureAwait(false);

        if (link is null)
        {
            await FollowupAsync("That code is invalid or expired. Generate a new code on your account page.", ephemeral: true)
                .ConfigureAwait(false);
            return;
        }

        var user = Guid.TryParse(link.JellyfinUserId, out var userId) ? _users.GetUser(userId) : null;
        await FollowupAsync($"Linked to Jellyfin user **{user?.Username ?? "unknown"}**. You can now use `/resetpassword`.", ephemeral: true)
            .ConfigureAwait(false);
    }

    [SlashCommand("unlink", "Remove the link between Discord and your Jellyfin account")]
    public async Task UnlinkAsync()
    {
        await DeferAsync(ephemeral: true).ConfigureAwait(false);
        var discordId = Context.User.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var link = await _accounts.GetLinkByDiscordIdAsync(discordId).ConfigureAwait(false);
        if (link is null)
        {
            await FollowupAsync("Your Discord account is not linked.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        if (!Guid.TryParse(link.JellyfinUserId, out var userId))
        {
            await FollowupAsync("The stored link is invalid. Ask an administrator to clear it.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        var user = _users.GetUser(userId);
        await _accounts.UnlinkAsync(userId, userId.ToString("N"), user?.Username ?? "unknown", null).ConfigureAwait(false);
        await FollowupAsync("Your Discord account has been unlinked.", ephemeral: true).ConfigureAwait(false);
    }

    [SlashCommand("whoami", "Show the Jellyfin account linked to this Discord user")]
    public async Task WhoAmIAsync()
    {
        var (user, link) = await RequireLinkedAsync().ConfigureAwait(false);
        if (user is null || link is null)
        {
            return;
        }

        var policy = _users.GetPolicy(user.Id);
        var builder = new StringBuilder();
        builder.AppendLine($"Jellyfin user: **{user.Username}**");
        builder.AppendLine($"Administrator: **{(policy.IsAdministrator ? "yes" : "no")}**");
        builder.AppendLine($"Account disabled: **{(policy.IsDisabled ? "yes" : "no")}**");
        builder.AppendLine($"Linked: <t:{link.LinkedAt / 1000}:R>");

        var settings = await _settings.GetAsync().ConfigureAwait(false);
        if (settings.SeerrConfigured)
        {
            try
            {
                var seerr = await _seerr.GetUserByJellyfinIdAsync(user.Id).ConfigureAwait(false);
                builder.AppendLine(seerr is null
                    ? "Seerr: **not imported yet**"
                    : $"Seerr permissions: **{string.Join(", ", SeerrPermissions.Describe(seerr.Permissions))}**");
            }
            catch (Exception ex)
            {
                builder.AppendLine($"Seerr: unavailable ({ex.Message})");
            }
        }

        await RespondAsync(builder.ToString(), ephemeral: true).ConfigureAwait(false);
    }

    [SlashCommand("resetpassword", "Send a one-time password reset link over Discord")]
    public async Task ResetPasswordAsync([Summary("user", "Jellyfin username (administrators only; omit for yourself)")] string? username = null)
    {
        await DeferAsync(ephemeral: true).ConfigureAwait(false);
        var (self, _) = await RequireLinkedAsync(followup: true).ConfigureAwait(false);
        if (self is null)
        {
            return;
        }

        var target = self;
        if (!string.IsNullOrWhiteSpace(username) && !string.Equals(username.Trim(), self.Username, StringComparison.OrdinalIgnoreCase))
        {
            if (!await EnsureAdminAsync(self, followup: true).ConfigureAwait(false))
            {
                return;
            }

            target = _users.GetUser(username.Trim());
            if (target is null)
            {
                await FollowupAsync("No Jellyfin user with that name exists.", ephemeral: true).ConfigureAwait(false);
                return;
            }
        }

        var link = await _accounts.GetLinkAsync(target.Id).ConfigureAwait(false);
        if (link is null)
        {
            await FollowupAsync($"**{target.Username}** has not linked a Discord account.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        var settings = await _settings.GetAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.PublicBaseUrl))
        {
            await FollowupAsync("An administrator must configure the public base URL in User Manager settings.", ephemeral: true)
                .ConfigureAwait(false);
            return;
        }

        var reset = await _accounts.CreateResetTokenAsync(target.Id, self.Id, settings.PublicBaseUrl).ConfigureAwait(false);
        var sent = await _sender.SendDirectMessageAsync(
            link.DiscordUserId,
            $"**Jellyfin password reset**\nUse this one-time link within 30 minutes:\n{reset.Url}",
            CancellationToken.None).ConfigureAwait(false);

        await _audit.LogAsync(
            AuditService.ActionPasswordReset,
            self.Id.ToString("N"),
            self.Username,
            target.Id.ToString("N"),
            target.Username,
            sent ? "Reset link delivered over Discord." : "Reset link created but Discord delivery failed.",
            null).ConfigureAwait(false);

        await FollowupAsync(
            sent
                ? $"A reset link was sent to **{target.Username}** over Discord."
                : "The reset link was created, but Discord delivery failed. Check the bot connection and the user's privacy settings.",
            ephemeral: true).ConfigureAwait(false);
    }

    [SlashCommand("invite", "Create a single-use or multi-use invite link")]
    public async Task InviteAsync(
        [Summary("preset", "Permission preset name")] string? preset = null,
        [Summary("uses", "Maximum number of uses")] int? uses = null,
        [Summary("days", "Days until expiry")] int? days = null,
        [Summary("note", "Internal note")] string? note = null)
    {
        await DeferAsync(ephemeral: true).ConfigureAwait(false);
        var (self, _) = await RequireLinkedAsync(followup: true).ConfigureAwait(false);
        if (self is null || !await EnsureAdminAsync(self, followup: true).ConfigureAwait(false))
        {
            return;
        }

        var settings = await _settings.GetAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.PublicBaseUrl))
        {
            await FollowupAsync("Configure the public base URL in User Manager settings first.", ephemeral: true)
                .ConfigureAwait(false);
            return;
        }

        PresetRecord? presetRecord = null;
        if (!string.IsNullOrWhiteSpace(preset))
        {
            var all = await _presets.GetAllAsync().ConfigureAwait(false);
            presetRecord = all.FirstOrDefault(p => string.Equals(p.Name, preset.Trim(), StringComparison.OrdinalIgnoreCase));
            if (presetRecord is null)
            {
                await FollowupAsync($"No preset named **{preset}**. Use `/preset` to list them.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }
        }

        var (invite, url) = await _invites.CreateAsync(
            self.Id.ToString("N"),
            self.Username,
            presetRecord?.Id,
            uses,
            days,
            note,
            settings.PublicBaseUrl).ConfigureAwait(false);

        await FollowupAsync(
            $"Invite created for **{invite.PresetName}** ({invite.MaxUses} use(s), expires <t:{invite.ExpiresAt / 1000}:R>):\n{url}",
            ephemeral: true).ConfigureAwait(false);
    }

    [SlashCommand("user", "Show Jellyfin and Seerr details for a user")]
    public async Task UserAsync([Summary("username", "Jellyfin username")] string username)
    {
        var (self, _) = await RequireLinkedAsync().ConfigureAwait(false);
        if (self is null || !await EnsureAdminAsync(self).ConfigureAwait(false))
        {
            return;
        }

        var user = _users.GetUser(username.Trim());
        if (user is null)
        {
            await RespondAsync("No Jellyfin user with that name exists.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        var policy = _users.GetPolicy(user.Id);
        var link = await _accounts.GetLinkAsync(user.Id).ConfigureAwait(false);
        var builder = new StringBuilder();
        builder.AppendLine($"**{user.Username}** (`{user.Id:N}`)");
        builder.AppendLine($"Admin: {(policy.IsAdministrator ? "yes" : "no")} | Disabled: {(policy.IsDisabled ? "yes" : "no")} | Hidden: {(policy.IsHidden ? "yes" : "no")}");
        builder.AppendLine($"Discord: {(link is null ? "not linked" : $"<@{link.DiscordUserId}>")}");

        var settings = await _settings.GetAsync().ConfigureAwait(false);
        if (settings.SeerrConfigured)
        {
            try
            {
                var seerr = await _seerr.GetUserByJellyfinIdAsync(user.Id).ConfigureAwait(false);
                builder.AppendLine(seerr is null
                    ? "Seerr: not imported"
                    : $"Seerr: id {seerr.Id}, permissions {seerr.Permissions} ({string.Join(", ", SeerrPermissions.Describe(seerr.Permissions))})");
            }
            catch (Exception ex)
            {
                builder.AppendLine($"Seerr: unavailable ({ex.Message})");
            }
        }

        await RespondAsync(builder.ToString(), ephemeral: true).ConfigureAwait(false);
    }

    [SlashCommand("audit", "Show recent audit entries")]
    public async Task AuditAsync([Summary("count", "Number of entries (1-20)")] int count = 10)
    {
        var (self, _) = await RequireLinkedAsync().ConfigureAwait(false);
        if (self is null || !await EnsureAdminAsync(self).ConfigureAwait(false))
        {
            return;
        }

        var entries = await _audit.RecentAsync(Math.Clamp(count, 1, 20)).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            await RespondAsync("No audit entries yet.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        var builder = new StringBuilder("**Recent audit entries**\n");
        foreach (var entry in entries)
        {
            var when = $"<t:{entry.Timestamp / 1000}:R>";
            builder.AppendLine($"{when} `{entry.Action}` {entry.ActorName} → {entry.TargetName ?? "-"}");
        }

        await RespondAsync(builder.ToString(), ephemeral: true).ConfigureAwait(false);
    }

    [SlashCommand("preset", "List presets, inspect one, or apply one to a user")]
    public async Task PresetAsync(
        [Summary("name", "Preset name")] string? name = null,
        [Summary("username", "Apply this preset to a Jellyfin user")] string? username = null)
    {
        var (self, _) = await RequireLinkedAsync().ConfigureAwait(false);
        if (self is null || !await EnsureAdminAsync(self).ConfigureAwait(false))
        {
            return;
        }

        var presets = await _presets.GetAllAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(name))
        {
            var list = presets.Count == 0
                ? "No presets configured."
                : string.Join('\n', presets.Select(p => $"- **{p.Name}**{(p.IsDefault ? " (default)" : string.Empty)}: Seerr {p.SeerrPermissions}"));
            await RespondAsync($"**Permission presets**\n{list}", ephemeral: true).ConfigureAwait(false);
            return;
        }

        var preset = presets.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (preset is null)
        {
            await RespondAsync($"No preset named **{name}**.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            var policy = PolicyTools.Deserialize(preset.JellyfinPolicyJson);
            await RespondAsync(
                $"**{preset.Name}**\n{PresetText(preset, policy)}",
                ephemeral: true).ConfigureAwait(false);
            return;
        }

        var user = _users.GetUser(username.Trim());
        if (user is null)
        {
            await RespondAsync("No Jellyfin user with that name exists.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        await DeferAsync(ephemeral: true).ConfigureAwait(false);
        var result = await _presets.ApplyToUserAsync(
            user.Id,
            preset.Id,
            self.Id.ToString("N"),
            self.Username,
            null).ConfigureAwait(false);

        var message = $"Applied **{preset.Name}** to **{user.Username}**.";
        if (result.SeerrError is not null)
        {
            message += $"\nSeerr sync failed: {result.SeerrError}";
        }

        await FollowupAsync(message, ephemeral: true).ConfigureAwait(false);
    }

    [SlashCommand("request", "Request a movie or TV show from Seerr")]
    public async Task RequestAsync(
        [Summary("tmdb_id", "TMDB identifier")] int tmdbId,
        [Summary("type", "movie or tv")]
        [Choice("Movie", "movie")]
        [Choice("TV", "tv")]
        string type = "movie")
    {
        await DeferAsync(ephemeral: true).ConfigureAwait(false);
        var (user, _) = await RequireLinkedAsync(followup: true).ConfigureAwait(false);
        if (user is null)
        {
            return;
        }

        var settings = await _settings.GetAsync().ConfigureAwait(false);
        if (!settings.SeerrConfigured)
        {
            await FollowupAsync("Seerr is not configured.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        if (tmdbId <= 0)
        {
            await FollowupAsync("Enter a valid TMDB id.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        try
        {
            await _seerr.CreateRequestAsync(user.Id, tmdbId, type).ConfigureAwait(false);
            await _audit.LogAsync(
                AuditService.ActionSeerrRequest,
                user.Id.ToString("N"),
                user.Username,
                null,
                null,
                $"Requested {type} {tmdbId} in Seerr.",
                null).ConfigureAwait(false);
            await FollowupAsync($"Request submitted for {type} **{tmdbId}**.", ephemeral: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await FollowupAsync($"Seerr request failed: {ex.Message}", ephemeral: true).ConfigureAwait(false);
        }
    }

    private async Task<(JfUser? User, LinkedAccountRecord? Link)> RequireLinkedAsync(bool followup = false)
    {
        var discordId = Context.User.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var link = await _accounts.GetLinkByDiscordIdAsync(discordId).ConfigureAwait(false);
        if (link is null || !Guid.TryParse(link.JellyfinUserId, out var userId))
        {
            var text = "Your Discord account is not linked. Open your Jellyfin account page under User Manager and generate a link code, then use `/link <code>`.";
            if (followup)
            {
                await FollowupAsync(text, ephemeral: true).ConfigureAwait(false);
            }
            else
            {
                await RespondAsync(text, ephemeral: true).ConfigureAwait(false);
            }

            return (null, null);
        }

        var user = _users.GetUser(userId);
        if (user is null)
        {
            var text = "The linked Jellyfin account no longer exists. Use `/unlink` and link again.";
            if (followup)
            {
                await FollowupAsync(text, ephemeral: true).ConfigureAwait(false);
            }
            else
            {
                await RespondAsync(text, ephemeral: true).ConfigureAwait(false);
            }

            return (null, null);
        }

        return (user, link);
    }

    private async Task<bool> EnsureAdminAsync(JfUser user, bool followup = false)
    {
        if (_users.IsAdministrator(user.Id))
        {
            return true;
        }

        var settings = await _settings.GetAsync().ConfigureAwait(false);
        var allowedRoles = (settings.DiscordAdminRoleIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => ulong.TryParse(value, out var roleId) ? roleId : 0)
            .Where(roleId => roleId != 0)
            .ToHashSet();

        if (allowedRoles.Count > 0
            && Context.User is IGuildUser guildUser
            && guildUser.RoleIds.Any(allowedRoles.Contains))
        {
            return true;
        }

        const string Text = "This command requires a linked Jellyfin administrator account or a configured Discord admin role.";
        if (followup)
        {
            await FollowupAsync(Text, ephemeral: true).ConfigureAwait(false);
        }
        else
        {
            await RespondAsync(Text, ephemeral: true).ConfigureAwait(false);
        }

        return false;
    }

    private static string PresetText(PresetRecord preset, MediaBrowser.Model.Users.UserPolicy policy)
    {
        var flags = new List<string>();
        if (policy.EnableMediaPlayback) flags.Add("playback");
        if (policy.EnableContentDownloading) flags.Add("downloads");
        if (policy.EnableContentDeletion) flags.Add("deletion");
        if (policy.EnableLiveTvAccess) flags.Add("live tv");
        if (policy.EnableRemoteAccess) flags.Add("remote");
        if (policy.IsAdministrator) flags.Add("admin");
        return $"Jellyfin: {string.Join(", ", flags)}\nSeerr: {string.Join(", ", SeerrPermissions.Describe(preset.SeerrPermissions))}";
    }
}
