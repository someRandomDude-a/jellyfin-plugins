using Microsoft.EntityFrameworkCore;

namespace AccountHub.Core.Data;

public sealed class AccountHubDbContext(DbContextOptions<AccountHubDbContext> options) : DbContext(options)
{
    public DbSet<HubState> State => Set<HubState>();
    public DbSet<AuditRow> Audit => Set<AuditRow>();
    public DbSet<EventReceipt> Receipts => Set<EventReceipt>();
    public DbSet<CommandResult> Commands => Set<CommandResult>();
    public DbSet<IntegrationSecret> Secrets => Set<IntegrationSecret>();
    public DbSet<DiscordChallenge> DiscordChallenges => Set<DiscordChallenge>();
    public DbSet<OnboardingInvite> OnboardingInvites => Set<OnboardingInvite>();
    public DbSet<InviteProvisioning> InviteProvisioning => Set<InviteProvisioning>();
    public DbSet<InviteSecretDelivery> InviteSecretDeliveries => Set<InviteSecretDelivery>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        InviteOnboardingModel.Configure(model);
        model.Entity<HubState>().ToTable("hub_state").HasKey(x => x.Id);
        model.Entity<AuditRow>().ToTable("audit_events").HasKey(x => x.Sequence);
        model.Entity<AuditRow>().Property(x => x.Sequence).ValueGeneratedNever();
        model.Entity<AuditRow>().HasIndex(x => x.Id).IsUnique();
        model.Entity<AuditRow>().HasIndex(x => x.RecordedAtUnixMs);
        model.Entity<EventReceipt>().ToTable("event_receipts").HasKey(x => new { x.Source, x.EventId });
        model.Entity<CommandResult>().ToTable("command_results").HasKey(x => new { x.ActorUserId, x.Operation, x.CommandId });
        model.Entity<IntegrationSecret>().ToTable("integration_secrets").HasKey(x => x.Name);
        model.Entity<DiscordChallenge>().ToTable("discord_challenges").HasKey(x => x.TokenHash);
        model.Entity<DiscordChallenge>().HasIndex(x => x.ExpiresAtUnixMs);
    }
}
