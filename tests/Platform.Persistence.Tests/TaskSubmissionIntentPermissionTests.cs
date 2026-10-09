using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class TaskSubmissionIntentPermissionTests
{
    [Fact]
    public async Task GlobalIndirectEscalationDeniesBeforeIntentProofAndNeverUsesAnotherConnection()
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(1);
        await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(connection).Options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var proof = new TaskSubmissionIntentPermissionVerifier(db); await proof.RequireAppendOnlyAsync();
        Assert.Equal(2, connection.Calls); Assert.Contains("sys.triggers", connection.Commands[0]);
        Assert.Contains("TaskSubmissionIntents", connection.Commands[1]); Assert.Contains("N'UPDATE',c.name,N'COLUMN'", connection.Commands[1]);
        Assert.Same(transaction.GetDbTransaction(), connection.LastTransaction); Assert.Equal(ConnectionState.Open, connection.State);
        connection.ResultForCommand = sql => sql.Contains("sys.triggers", StringComparison.Ordinal) ? 0 : 1;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => proof.RequireAppendOnlyAsync()); Assert.Equal(3, connection.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    [InlineData("1")]
    public async Task UnsafeOrUnverifiableImmutableTableDeniesAndClosesOwnedConnection(object? result)
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(1)
        { ResultForCommand = sql => sql.Contains("TaskSubmissionIntents", StringComparison.Ordinal) ? result : 1 };
        await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(connection).Options);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new TaskSubmissionIntentPermissionVerifier(db).RequireAppendOnlyAsync());
        Assert.Equal(2, connection.Calls); Assert.Equal(ConnectionState.Closed, connection.State);
    }
}
