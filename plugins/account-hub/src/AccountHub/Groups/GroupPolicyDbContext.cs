using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AccountHub.Groups;

public sealed class PolicyGroup
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string NameKey { get; set; } = "";
    public string Description { get; set; } = "";
    public string PolicyJson { get; set; } = "{}";
    public bool IsSystem { get; set; }
    public long Revision { get; set; } = 1;
}
public sealed class PolicyAssignment
{
    public Guid UserId { get; set; }
    public Guid? GroupId { get; set; }
    public string OverridesJson { get; set; } = "{}";
    // Original managed values are captured only at explicit first assignment, never discovery.
    public string BaseJson { get; set; } = "{}";
    public long Revision { get; set; } = 1;
}
public sealed class PolicySnapshot
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid CommandId { get; set; }
    public string BeforeJson { get; set; } = "{}";
    public string AfterJson { get; set; } = "{}";
    public string Outcome { get; set; } = "prepared";
    public long CreatedAt { get; set; }
}
public sealed class GroupPolicyDbContext(DbContextOptions<GroupPolicyDbContext> options) : DbContext(options)
{
    public DbSet<PolicyGroup> Groups => Set<PolicyGroup>();
    public DbSet<PolicyAssignment> Assignments => Set<PolicyAssignment>();
    public DbSet<PolicySnapshot> Snapshots => Set<PolicySnapshot>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<PolicyGroup>().ToTable("hub_policy_groups").HasKey(x => x.Id);
        b.Entity<PolicyGroup>().HasIndex(x => x.NameKey).IsUnique();
        b.Entity<PolicyGroup>().Property(x => x.Revision).IsConcurrencyToken();
        b.Entity<PolicyAssignment>().ToTable("hub_policy_assignments").HasKey(x => x.UserId);
        b.Entity<PolicyAssignment>().Property(x => x.Revision).IsConcurrencyToken();
        b.Entity<PolicyAssignment>().HasOne<PolicyGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PolicySnapshot>().ToTable("hub_policy_snapshots").HasKey(x => x.Id);
        b.Entity<PolicySnapshot>().HasIndex(x => new { x.UserId, x.CreatedAt });
    }
}

[DbContext(typeof(GroupPolicyDbContext)), Migration("202610240001_GroupIntent")]
public sealed class GroupIntentMigration : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
        CREATE TABLE hub_policy_groups (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, NameKey TEXT NOT NULL, Description TEXT NOT NULL, PolicyJson TEXT NOT NULL, IsSystem INTEGER NOT NULL, Revision INTEGER NOT NULL);
        CREATE UNIQUE INDEX IX_hub_policy_groups_NameKey ON hub_policy_groups(NameKey);
        CREATE TABLE hub_policy_assignments (UserId TEXT NOT NULL PRIMARY KEY, GroupId TEXT NULL REFERENCES hub_policy_groups(Id) ON DELETE RESTRICT, OverridesJson TEXT NOT NULL, BaseJson TEXT NOT NULL, Revision INTEGER NOT NULL);
        CREATE INDEX IX_hub_policy_assignments_GroupId ON hub_policy_assignments(GroupId);
        CREATE TABLE hub_policy_snapshots (Id TEXT NOT NULL PRIMARY KEY, UserId TEXT NOT NULL, CommandId TEXT NOT NULL, BeforeJson TEXT NOT NULL, AfterJson TEXT NOT NULL, Outcome TEXT NOT NULL, CreatedAt INTEGER NOT NULL);
        CREATE INDEX IX_hub_policy_snapshots_UserId_CreatedAt ON hub_policy_snapshots(UserId, CreatedAt);
        """);
    protected override void Down(MigrationBuilder b) => throw new NotSupportedException("Restore backup instead of downgrading policy intent");
}
