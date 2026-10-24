using AccountHub.Core;
using Xunit;

namespace AccountHub.Tests;

public sealed class InviteDatabaseBoundaryTests
{
    [Fact]
    public void OnlyReviewedNativeSchemaAndProviderAreAllowed()
    {
        InviteNativeProvisioner.RequireSupportedDatabase(new Version(12, 2, 0, 0), "Microsoft.EntityFrameworkCore.Sqlite");
        foreach (var version in new Version?[] { null, new(12, 1, 0, 0), new(12, 2, 0, 1), new(12, 3, 0, 0) })
            Assert.Throws<HubException>(() => InviteNativeProvisioner.RequireSupportedDatabase(version, "Microsoft.EntityFrameworkCore.Sqlite"));
        foreach (var provider in new string?[] { null, "", "Npgsql.EntityFrameworkCore.PostgreSQL" })
            Assert.Throws<HubException>(() => InviteNativeProvisioner.RequireSupportedDatabase(new Version(12, 2, 0, 0), provider));
    }
}
