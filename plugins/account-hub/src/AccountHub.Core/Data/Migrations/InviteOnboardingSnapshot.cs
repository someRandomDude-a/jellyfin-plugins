using Microsoft.EntityFrameworkCore;

namespace AccountHub.Core.Data.Migrations;

internal static class InviteOnboardingSnapshot
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity("AccountHub.Core.Data.OnboardingInvite", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("TEXT");
            b.Property<Guid>("CreatedBy").HasColumnType("TEXT");
            b.Property<Guid?>("ApprovedBy").HasColumnType("TEXT");
            b.Property<string>("TokenHash").HasColumnType("TEXT");
            b.Property<string>("Status").IsRequired().HasColumnType("TEXT");
            b.Property<long>("CreatedAt").HasColumnType("INTEGER");
            b.Property<long>("ExpiresAt").HasColumnType("INTEGER");
            b.Property<int>("MaxUses").HasColumnType("INTEGER");
            b.Property<int>("ReservedUses").HasColumnType("INTEGER");
            b.Property<int>("CompletedUses").HasColumnType("INTEGER");
            b.Property<string>("Note").IsRequired().HasColumnType("TEXT");
            b.Property<string>("PolicyJson").IsRequired().HasColumnType("TEXT");
            b.Property<string>("GroupId").HasColumnType("TEXT");
            b.Property<string>("DiscordUserId").HasColumnType("TEXT");
            b.Property<bool>("CopyLink").HasColumnType("INTEGER");
            b.HasKey("Id"); b.HasIndex("TokenHash").IsUnique(); b.ToTable("onboarding_invites");
        });
        model.Entity("AccountHub.Core.Data.InviteProvisioning", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("TEXT");
            b.Property<Guid>("InviteId").HasColumnType("TEXT");
            b.Property<Guid>("UserId").HasColumnType("TEXT");
            b.Property<string>("ClaimHash").IsRequired().HasColumnType("TEXT");
            b.Property<string>("Username").IsRequired().HasColumnType("TEXT");
            b.Property<string>("State").IsRequired().HasColumnType("TEXT");
            b.Property<long>("CreatedAt").HasColumnType("INTEGER");
            b.Property<long>("UpdatedAt").HasColumnType("INTEGER");
            b.HasKey("Id"); b.HasIndex("ClaimHash").IsUnique(); b.HasIndex("UserId").IsUnique(); b.HasIndex("InviteId"); b.ToTable("invite_provisioning");
        });
        model.Entity("AccountHub.Core.Data.InviteSecretDelivery", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("TEXT");
            b.Property<Guid>("InviteId").HasColumnType("TEXT");
            b.Property<string>("Destination").IsRequired().HasColumnType("TEXT");
            b.Property<string>("Ciphertext").HasColumnType("TEXT");
            b.Property<string>("State").IsRequired().HasColumnType("TEXT");
            b.Property<long>("ExpiresAt").HasColumnType("INTEGER");
            b.HasKey("Id"); b.ToTable("invite_secret_deliveries");
        });
    }
}
