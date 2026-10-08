using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class CompanyMembershipAuditPermissionTests
{
    [Fact]
    public async Task IndirectEscalationFailsEvenWhenDirectAuditRightsAreAppendOnly()
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(1)
        { ResultForCommand = sql => sql.Contains("sys.triggers", StringComparison.Ordinal) ? 0 : 1 };
        await using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(connection).Options);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new CompanyMembershipAuditPermissionVerifier(database).RequireAppendOnlyAsync());
        Assert.Single(connection.Commands);
        Assert.Contains("sys.triggers", connection.Commands[0]);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }
    [Fact]
    public async Task BothProofsUseThePinnedTransactionAndGlobalRevocationIsCheckedAgain()
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(1);
        await using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(connection).Options);
        await using var transaction = await database.Database.BeginTransactionAsync();
        var verifier = new CompanyMembershipAuditPermissionVerifier(database);
        await verifier.RequireAppendOnlyAsync();
        Assert.Equal(2, connection.Calls); Assert.Contains("sys.triggers", connection.Commands[0]);
        Assert.Contains("CompanyMembershipAccessAudits", connection.Commands[1]);
        Assert.Same(transaction.GetDbTransaction(), connection.LastTransaction); Assert.Equal(ConnectionState.Open, connection.State);
        connection.ResultForCommand = sql => sql.Contains("sys.triggers", StringComparison.Ordinal) ? 0 : 1;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => verifier.RequireAppendOnlyAsync());
        Assert.Equal(3, connection.Calls); Assert.Same(transaction.GetDbTransaction(), connection.LastTransaction);
    }
}
