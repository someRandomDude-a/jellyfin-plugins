using Microsoft.EntityFrameworkCore;

namespace AccountHub.Core.Data;

public sealed class OnboardingInvite
{
    public Guid Id { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public string? TokenHash { get; set; }
    public string Status { get; set; } = "pending_approval";
    public long CreatedAt { get; set; }
    public long ExpiresAt { get; set; }
    public int MaxUses { get; set; }
    // Includes incomplete/uncertain reservations. Never release these automatically.
    public int ReservedUses { get; set; }
    public int CompletedUses { get; set; }
    public string Note { get; set; } = "";
    public string PolicyJson { get; set; } = "{}";
    public string? GroupId { get; set; }
    public string? DiscordUserId { get; set; }
    public bool CopyLink { get; set; } = true;
}

public sealed class InviteSecretDelivery
{
    public Guid Id { get; set; }
    public Guid InviteId { get; set; }
    public string Destination { get; set; } = "";
    public string? Ciphertext { get; set; }
    public string State { get; set; } = "queued";
    public long ExpiresAt { get; set; }
}

public sealed class InviteProvisioning
{
    public Guid Id { get; set; }
    public Guid InviteId { get; set; }
    public Guid UserId { get; set; }
    public string ClaimHash { get; set; } = "";
    public string Username { get; set; } = "";
    public string State { get; set; } = "reserved";
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

public static class InviteOnboardingModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<OnboardingInvite>().ToTable("onboarding_invites").HasKey(x => x.Id);
        model.Entity<OnboardingInvite>().HasIndex(x => x.TokenHash).IsUnique();
        model.Entity<InviteProvisioning>().ToTable("invite_provisioning").HasKey(x => x.Id);
        model.Entity<InviteProvisioning>().HasIndex(x => x.ClaimHash).IsUnique();
        model.Entity<InviteProvisioning>().HasIndex(x => x.UserId).IsUnique();
        model.Entity<InviteProvisioning>().HasIndex(x => x.InviteId);
        model.Entity<InviteSecretDelivery>().ToTable("invite_secret_deliveries").HasKey(x => x.Id);
    }
}
