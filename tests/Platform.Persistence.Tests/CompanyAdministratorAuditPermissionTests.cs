using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class CompanyAdministratorAuditPermissionTests
{
    [Fact]
    public async Task IndirectEscalationDeniesBeforeAuditReadAndUsesPinnedTransaction()
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(1);
        await using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(connection).Options);
        await using var transaction = await database.Database.BeginTransactionAsync();
        var proof = new CompanyAdministratorAuditPermissionVerifier(database);
        await proof.RequireAppendOnlyAsync();
        Assert.Equal(2, connection.Calls); Assert.Contains("sys.triggers", connection.Commands[0]);
        Assert.Contains("CompanyAdministratorAudits", connection.Commands[1]);
        Assert.Same(transaction.GetDbTransaction(), connection.LastTransaction); Assert.Equal(ConnectionState.Open, connection.State);
        connection.ResultForCommand = sql => sql.Contains("sys.triggers", StringComparison.Ordinal) ? 0 : 1;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => proof.RequireAppendOnlyAsync());
        Assert.Equal(3, connection.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    [InlineData("1")]
    public async Task NonExactAuditProofRefusedAndOwnedConnectionClosed(object? result)
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(1)
        { ResultForCommand = sql => sql.Contains("CompanyAdministratorAudits", StringComparison.Ordinal) ? result : 1 };
        await using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(connection).Options);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new CompanyAdministratorAuditPermissionVerifier(database).RequireAppendOnlyAsync());
        Assert.Equal(2, connection.Calls); Assert.Equal(ConnectionState.Closed, connection.State);
    }
}
