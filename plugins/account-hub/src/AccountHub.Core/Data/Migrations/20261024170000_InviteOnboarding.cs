using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AccountHub.Core.Data.Migrations;

[DbContext(typeof(AccountHubDbContext))]
[Migration("20261024170000_InviteOnboarding")]
public sealed class InviteOnboarding : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE onboarding_invites (
              Id TEXT NOT NULL PRIMARY KEY, CreatedBy TEXT NOT NULL, ApprovedBy TEXT NULL,
              TokenHash TEXT NULL, Status TEXT NOT NULL, CreatedAt INTEGER NOT NULL,
              ExpiresAt INTEGER NOT NULL, MaxUses INTEGER NOT NULL CHECK(MaxUses BETWEEN 1 AND 100),
              ReservedUses INTEGER NOT NULL CHECK(ReservedUses >= 0 AND ReservedUses <= MaxUses),
              CompletedUses INTEGER NOT NULL CHECK(CompletedUses >= 0 AND CompletedUses <= ReservedUses),
              Note TEXT NOT NULL, PolicyJson TEXT NOT NULL, GroupId TEXT NULL, DiscordUserId TEXT NULL, CopyLink INTEGER NOT NULL);
            CREATE UNIQUE INDEX IX_onboarding_invites_TokenHash ON onboarding_invites(TokenHash);
            CREATE TABLE invite_provisioning (
              Id TEXT NOT NULL PRIMARY KEY, InviteId TEXT NOT NULL, UserId TEXT NOT NULL,
              ClaimHash TEXT NOT NULL, Username TEXT NOT NULL, State TEXT NOT NULL,
              CreatedAt INTEGER NOT NULL, UpdatedAt INTEGER NOT NULL);
            CREATE UNIQUE INDEX IX_invite_provisioning_ClaimHash ON invite_provisioning(ClaimHash);
            CREATE UNIQUE INDEX IX_invite_provisioning_UserId ON invite_provisioning(UserId);
            CREATE INDEX IX_invite_provisioning_InviteId ON invite_provisioning(InviteId);
            CREATE TABLE invite_secret_deliveries (
              Id TEXT NOT NULL PRIMARY KEY, InviteId TEXT NOT NULL, Destination TEXT NOT NULL,
              Ciphertext TEXT NULL, State TEXT NOT NULL, ExpiresAt INTEGER NOT NULL);
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Forward-only migration; restore a complete backup.");
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => InviteOnboardingSnapshot.Configure(modelBuilder);
}
