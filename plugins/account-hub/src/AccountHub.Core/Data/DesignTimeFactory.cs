using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AccountHub.Core.Data;

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AccountHubDbContext>
{
    public AccountHubDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<AccountHubDbContext>().UseSqlite("Data Source=account-hub-design-only.db").Options);
}
