using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.UserManager.Data;
using Jellyfin.Plugin.UserManager.Security;
using Jellyfin.Plugin.UserManager.Services;
using Xunit;

namespace Jellyfin.Plugin.UserManager.Tests;

public sealed class PluginStoreTests
{
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static PresetRecord NewPreset(string name, bool isDefault = false, long seerr = 0) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        Description = name + " description",
        JellyfinPolicyJson = """{"enableMediaPlayback":true}""",
        SeerrPermissions = seerr,
        IsDefault = isDefault,
        CreatedAt = Now,
        UpdatedAt = Now
    };

    [Fact]
    public async Task Initialize_CreatesDatabase()
    {
        using var folder = new TempFolder();
        var store = new PluginStore(folder.Path);
        await store.InitializeAsync();
        await store.InitializeAsync();

        Assert.True(System.IO.File.Exists(store.DatabasePath));

        var stats = await store.GetStatsAsync();
        Assert.Equal(0, stats.Invites);
        Assert.Equal(0, stats.AuditEntries);
        Assert.Equal(0, stats.Presets);
    }

    [Fact]
    public async Task Presets_RoundTripAndDefaultIsExclusive()
    {
        using var folder = new TempFolder();
        var store = new PluginStore(folder.Path);
        var first = NewPreset("Basic", isDefault: true, seerr: SeerrPermissions.DefaultRequester);
        var second = NewPreset("Family", isDefault: true);

        await store.UpsertPresetAsync(first);
        await store.UpsertPresetAsync(second);

        var all = await store.GetPresetsAsync();
        Assert.Equal(2, all.Count);
        Assert.Single(all, p => p.IsDefault);
        Assert.Equal("Family", all.Single(p => p.IsDefault).Name);

        var loaded = await store.GetPresetAsync(first.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Basic", loaded!.Name);
        Assert.Equal(SeerrPermissions.DefaultRequester, loaded.SeerrPermissions);
        Assert.False(loaded.IsDefault);

        var defaultPreset = await store.GetDefaultPresetAsync();
        Assert.NotNull(defaultPreset);
        Assert.Equal(second.Id, defaultPreset!.Id);
    }

    [Fact]
    public async Task UserPresets_SetAndRemove()
    {
        using var folder = new TempFolder();
        var store = new PluginStore(folder.Path);
        var preset = NewPreset("Basic", isDefault: true);
        await store.UpsertPresetAsync(preset);

        await store.SetUserPresetAsync("user-1", preset.Id);
        var map = await store.GetUserPresetsAsync();
        Assert.Equal(preset.Id, map["user-1"]);

        await store.SetUserPresetAsync("user-1", null);
        map = await store.GetUserPresetsAsync();
        Assert.False(map.ContainsKey("user-1"));
    }

    [Fact]
    public async Task Settings_RoundTripNulls()
    {
        using var folder = new TempFolder();
        var store = new PluginStore(folder.Path);

        await store.SaveSettingsAsync(new System.Collections.Generic.Dictionary<string, string?>
        {
            ["a"] = "value",
            ["b"] = null
        });
        await store.SaveSettingsAsync(new System.Collections.Generic.Dictionary<string, string?>
        {
            ["a"] = "updated"
        });

        var settings = await store.GetSettingsAsync();
        Assert.Equal("updated", settings["a"]);
        Assert.True(settings.ContainsKey("b"));
        Assert.Null(settings["b"]);
    }

    [Fact]
    public async Task Invites_ReservationRespectsUsesExpiryAndRevocation()
    {
        using var folder = new TempFolder();
        var store = new PluginStore(folder.Path);
        var token = TokenTools.CreateUrlToken();
        var hash = TokenTools.HashToken(token);
        var invite = new InviteRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            TokenHash = hash,
            CreatedByUserId = "admin",
            CreatedByName = "Admin",
            CreatedAt = Now,
            ExpiresAt = Now + 3_600_000,
            MaxUses = 2
        };

        await store.CreateInviteAsync(invite);

        var loaded = await store.GetInviteByTokenHashAsync(hash);
        Assert.NotNull(loaded);
        Assert.Equal("active", loaded!.Status);
        Assert.Equal(0, loaded.UseCount);

        Assert.True(await store.TryReserveInviteAsync(hash, Now));
        Assert.True(await store.TryReserveInviteAsync(hash, Now));
        Assert.False(await store.TryReserveInviteAsync(hash, Now));

        loaded = await store.GetInviteByTokenHashAsync(hash);
        Assert.Equal("used", loaded!.Status);

        await store.ReleaseInviteAsync(hash);
        Assert.True(await store.TryReserveInviteAsync(hash, Now));

        await store.RevokeInviteAsync(invite.Id);
        Assert.False(await store.TryReserveInviteAsync(hash, Now));
        loaded = await store.GetInviteByTokenHashAsync(hash);
        Assert.Equal("revoked", loaded!.Status);
    }

    [Fact]
    public async Task Invites_ExpiredCannotBeReserved()
    {
        using var folder = new TempFolder();
        var store = new PluginStore(folder.Path);
        var hash = TokenTools.HashToken(TokenTools.CreateUrlToken());

        await store.CreateInviteAsync(new InviteRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            TokenHash = hash,
            CreatedByUserId = "admin",
            CreatedByName = "Admin",
            CreatedAt = Now - 7_200_000,
            ExpiresAt = Now - 3_600_000,
            MaxUses = 1
        });

        Assert.False(await store.TryReserveInviteAsync(hash, Now));
        var loaded = await store.GetInviteByTokenHashAsync(hash);
        Assert.Equal("expired", loaded!.Status);
    }

    [Fact]
    public async Task InviteUses_AreStoredAndCascadeOnDelete()
    {
        using var folder = new TempFolder();
        var store = new PluginStore(folder.Path);
        var invite = new InviteRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            TokenHash = TokenTools.HashToken(TokenTools.CreateUrlToken()),
            CreatedByUserId = "admin",
            CreatedByName = "Admin",
            CreatedAt = Now,
            ExpiresAt = Now + 3_600_000,
            MaxUses = 2
        };
        await store.CreateInviteAsync(invite);
        await store.TryReserveInviteAsync(invite.TokenHash, Now);
        await store.AddInviteUseAsync(new InviteUseRecord
        {
            InviteId = invite.Id,
            UserId = "user-1",
            Username = "alice",
            UsedAt = Now,
            RemoteIp = "127.0.0.1"
        });

        var loaded = (await store.GetInvitesAsync()).Single(i => i.Id == invite.Id);
        Assert.Single(loaded.Uses);
        Assert.Equal("alice", loaded.Uses[0].Username);
        Assert.Equal(1, loaded.UseCount);

        await store.DeleteInviteAsync(invite.Id);
        Assert.Null(await store.GetInviteAsync(invite.Id));
    }

    [Fact]
    public async Task InvitePreset_IsSetNullWhenPresetDeleted()
    {
        using var folder = new TempFolder();
        var store = new PluginStore(folder.Path);
        var preset = NewPreset("Basic", isDefault: true);
        await store.UpsertPresetAsync(preset);

        var invite = new InviteRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            TokenHash = TokenTools.HashToken(TokenTools.CreateUrlToken()),
            CreatedByUserId = "admin",
            CreatedByName = "Admin",
            PresetId = preset.Id,
            CreatedAt = Now,
            ExpiresAt = Now + 3_600_000,
            MaxUses = 1
        };
        await store.CreateInviteAsync(invite);
        await store.SetUserPresetAsync("user-1", preset.Id);

        await store.DeletePresetAsync(preset.Id);

        var loaded = await store.GetInviteAsync(invite.Id);
        Assert.NotNull(loaded);
        Assert.Null(loaded!.PresetId);
        Assert.Empty(await store.GetUserPresetsAsync());
    }

    [Fact]
    public async Task ResetTokens_AreSingleUseAndReplacePrevious()
    {
        using var folder = new TempFolder();
        var store = new PluginStore(folder.Path);
        var firstToken = TokenTools.CreateUrlToken();
        var secondToken = TokenTools.CreateUrlToken();

        await store.CreateResetTokenAsync(new ResetTokenRecord
        {
            TokenHash = TokenTools.HashToken(firstToken),
            JellyfinUserId = "user-1",
            CreatedByUserId = "user-1",
            CreatedAt = Now,
            ExpiresAt = Now + 3_600_000
        });

        await store.CreateResetTokenAsync(new ResetTokenRecord
        {
            TokenHash = TokenTools.HashToken(secondToken),
            JellyfinUserId = "user-1",
            CreatedByUserId = "user-1",
            CreatedAt = Now,
            ExpiresAt = Now + 3_600_000
        });

        Assert.Null(await store.GetResetTokenAsync(TokenTools.HashToken(firstToken)));
        var consumed = await store.ConsumeResetTokenAsync(TokenTools.HashToken(secondToken), Now);
        Assert.NotNull(consumed);
        Assert.Equal("user-1", consumed!.JellyfinUserId);

        Assert.Null(await store.ConsumeResetTokenAsync(TokenTools.HashToken(secondToken), Now));
        var record = await store.GetResetTokenAsync(TokenTools.HashToken(secondToken));
        Assert.NotNull(record!.UsedAt);
    }

    [Fact]
    public async Task ResetTokens_ExpiredCannotBeConsumed()
    {
        using var folder = new TempFolder();
        var store = new PluginStore(folder.Path);
        var token = TokenTools.CreateUrlToken();

        await store.CreateResetTokenAsync(new ResetTokenRecord
        {
            TokenHash = TokenTools.HashToken(token),
            JellyfinUserId = "user-1",
            CreatedByUserId = "user-1",
            CreatedAt = Now - 7_200_000,
            ExpiresAt = Now - 3_600_000
        });

        Assert.Null(await store.ConsumeResetTokenAsync(TokenTools.HashToken(token), Now));
    }

    [Fact]
    public async Task LinkCodes_AreSingleUseAndCreateLink()
    {
        using var folder = new TempFolder();
        var store = new PluginStore(folder.Path);
        var hash = TokenTools.HashCode("K7P2-Q9XM");

        await store.SetLinkCodeAsync(new LinkCodeRecord
        {
            CodeHash = hash,
            JellyfinUserId = "user-1",
            CreatedAt = Now,
            ExpiresAt = Now + 600_000
        });

        var consumed = await store.ConsumeLinkCodeAsync(hash, "discord-1", "alice#1", Now);
        Assert.NotNull(consumed);
        Assert.Equal("user-1", consumed!.JellyfinUserId);

        var link = await store.GetLinkByJellyfinUserIdAsync("user-1");
        Assert.NotNull(link);
        Assert.Equal("discord-1", link!.DiscordUserId);
        Assert.Equal("alice#1", link.DiscordUsername);

        Assert.Null(await store.ConsumeLinkCodeAsync(hash, "discord-2", "bob#2", Now));
    }

    [Fact]
    public async Task LinkCodes_RejectExpiredCodes()
    {
        using var folder = new TempFolder();
        var store = new PluginStore(folder.Path);
        var hash = TokenTools.HashCode("AAAA-BBBB");

        await store.SetLinkCodeAsync(new LinkCodeRecord
        {
            CodeHash = hash,
            JellyfinUserId = "user-1",
            CreatedAt = Now - 1_200_000,
            ExpiresAt = Now - 600_000
        });

        Assert.Null(await store.ConsumeLinkCodeAsync(hash, "discord-1", "alice", Now));
        Assert.Null(await store.GetLinkByJellyfinUserIdAsync("user-1"));
    }

    [Fact]
    public async Task LinkCodes_NewCodeReplacesPrevious()
    {
        using var folder = new TempFolder();
        var store = new PluginStore(folder.Path);
        var firstHash = TokenTools.HashCode("AAAA-BBBB");
        var secondHash = TokenTools.HashCode("CCCC-DDDD");

        await store.SetLinkCodeAsync(new LinkCodeRecord
        {
            CodeHash = firstHash,
            JellyfinUserId = "user-1",
            CreatedAt = Now,
            ExpiresAt = Now + 600_000
        });
        await store.SetLinkCodeAsync(new LinkCodeRecord
        {
            CodeHash = secondHash,
            JellyfinUserId = "user-1",
            CreatedAt = Now,
            ExpiresAt = Now + 600_000
        });

        Assert.Null(await store.GetLinkCodeAsync(firstHash));
        Assert.NotNull(await store.GetLinkCodeAsync(secondHash));
    }

    [Fact]
    public async Task Links_CanOnlyMapOneDiscordAccountToOneJellyfinUser()
    {
        using var folder = new TempFolder();
        var store = new PluginStore(folder.Path);

        await store.SetLinkCodeAsync(new LinkCodeRecord
        {
            CodeHash = TokenTools.HashCode("AAAA-BBBB"),
            JellyfinUserId = "user-1",
            CreatedAt = Now,
            ExpiresAt = Now + 600_000
        });
        await store.ConsumeLinkCodeAsync(TokenTools.HashCode("AAAA-BBBB"), "discord-1", "alice", Now);

        await store.SetLinkCodeAsync(new LinkCodeRecord
        {
            CodeHash = TokenTools.HashCode("CCCC-DDDD"),
            JellyfinUserId = "user-2",
            CreatedAt = Now,
            ExpiresAt = Now + 600_000
        });
        await store.ConsumeLinkCodeAsync(TokenTools.HashCode("CCCC-DDDD"), "discord-1", "alice-renamed", Now);

        Assert.Null(await store.GetLinkByJellyfinUserIdAsync("user-1"));
        var link = await store.GetLinkByJellyfinUserIdAsync("user-2");
        Assert.NotNull(link);
        Assert.Equal("alice-renamed", link!.DiscordUsername);

        await store.DeleteLinkAsync("user-2");
        Assert.Null(await store.GetLinkByJellyfinUserIdAsync("user-2"));
    }

    [Fact]
    public async Task Audit_CanBeFilteredCountedAndListed()
    {
        using var folder = new TempFolder();
        var store = new PluginStore(folder.Path);

        await store.AddAuditAsync(new AuditRecord
        {
            Timestamp = Now - 2000,
            Action = "user.created",
            ActorUserId = "admin",
            ActorName = "Admin",
            TargetUserId = "user-1",
            TargetName = "alice",
            Details = "created",
            IpAddress = "127.0.0.1"
        });
        await store.AddAuditAsync(new AuditRecord
        {
            Timestamp = Now - 1000,
            Action = "password.reset",
            ActorUserId = "user-1",
            ActorName = "alice",
            TargetUserId = "user-1",
            TargetName = "alice",
            Details = "reset"
        });

        Assert.Equal(2, await store.CountAuditAsync());
        Assert.Equal(new[] { "password.reset", "user.created" }, await store.GetAuditActionsAsync());

        var byAction = await store.GetAuditAsync(10, 0, action: "user.created");
        Assert.Single(byAction);
        Assert.Equal("alice", byAction[0].TargetName);

        var byUser = await store.GetAuditAsync(10, 0, userId: "user-1");
        Assert.Equal(2, byUser.Count);

        var page = await store.GetAuditAsync(1, 1);
        Assert.Single(page);
        Assert.Equal("user.created", page[0].Action);
    }

    [Fact]
    public async Task Stats_CountsEverything()
    {
        using var folder = new TempFolder();
        var store = new PluginStore(folder.Path);
        var preset = NewPreset("Basic", isDefault: true);
        await store.UpsertPresetAsync(preset);
        await store.CreateInviteAsync(new InviteRecord
        {
            TokenHash = TokenTools.HashToken(TokenTools.CreateUrlToken()),
            CreatedByUserId = "admin",
            CreatedByName = "Admin",
            CreatedAt = Now,
            ExpiresAt = Now + 1000,
            MaxUses = 1
        });
        await store.AddAuditAsync(new AuditRecord
        {
            Timestamp = Now,
            Action = "test",
            ActorUserId = "admin",
            ActorName = "Admin"
        });

        var stats = await store.GetStatsAsync();
        Assert.Equal(1, stats.Invites);
        Assert.Equal(1, stats.AuditEntries);
        Assert.Equal(1, stats.Presets);
        Assert.Equal(0, stats.LinkedAccounts);
    }
}
