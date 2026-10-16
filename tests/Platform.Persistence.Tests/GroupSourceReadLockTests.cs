using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupSourceReadLockTests
{
    [Fact]
    public async Task Shared_read_lock_executes_fresh_commands_on_the_same_serializable_transaction()
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(0);
        await using var database = Database(connection);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var scope = Scope();
        var read = await GroupSourceReadLock.AcquireAsync(database, scope, default);
        await read.RequireAsync(database, scope, default);
        Assert.Equal(2, connection.Calls);
        Assert.All(connection.Commands, text =>
        {
            Assert.Contains("@LockMode='Shared'", text);
            Assert.Contains("@LockOwner='Transaction',@LockTimeout=5000", text);
            Assert.DoesNotContain("Exclusive", text);
        });
        Assert.Same(transaction.GetDbTransaction(), connection.LastTransaction);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Theory]
    [InlineData("foreign-context")]
    [InlineData("foreign-scope")]
    [InlineData("later-transaction")]
    public async Task Read_lock_cannot_be_reused_for_another_context_scope_or_transaction(string changed)
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(0);
        await using var database = Database(connection);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var scope = Scope();
        var read = await GroupSourceReadLock.AcquireAsync(database, scope, default);
        if (changed == "foreign-context")
        {
            await using var foreign = Database(new BindingStorePermissionTests.SyntheticConnection(0));
            await Assert.ThrowsAsync<InvalidOperationException>(() => read.RequireAsync(foreign, scope, default));
        }
        else if (changed == "foreign-scope")
            await Assert.ThrowsAsync<InvalidOperationException>(() => read.RequireAsync(database, Scope(), default));
        else
        {
            await transaction.DisposeAsync();
            await using var next = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            await Assert.ThrowsAsync<InvalidOperationException>(() => read.RequireAsync(database, scope, default));
        }
        Assert.Equal(1, connection.Calls);
    }

    [Fact]
    public async Task Default_writer_refuses_shared_transaction_before_executing_an_upgrade()
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(0);
        await using var database = Database(connection);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var scope = Scope();
        _ = await GroupSourceReadLock.AcquireAsync(database, scope, default);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => GroupSourceTransactionLock.RequireAsync(database, scope, default));
        Assert.Equal(GroupSourceLockFailure.Rejected, GroupSourceTransactionLock.FailureOf(failure));
        Assert.Equal(1, connection.Calls);
        await transaction.DisposeAsync();
        await using var fresh = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await GroupSourceTransactionLock.RequireAsync(database, scope, default);
        Assert.Contains("@LockMode='Exclusive'", connection.Commands[^1]);
        Assert.Equal(2, connection.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-3)]
    [InlineData(-999)]
    public async Task Failed_shared_acquisition_does_not_register_a_read_transaction(int result)
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(result);
        await using var database = Database(connection);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => GroupSourceReadLock.AcquireAsync(database, Scope(), default));
        Assert.False(GroupSourceReadLock.IsReadTransaction(transaction.GetDbTransaction()));
        connection.Result = 0;
        await GroupSourceTransactionLock.RequireAsync(database, Scope(), default);
        Assert.Contains("@LockMode='Exclusive'", connection.Commands[^1]);
    }

    [Theory]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    public async Task Read_lock_refuses_weaker_transaction_before_lock_command(IsolationLevel isolation)
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(0);
        await using var database = Database(connection);
        await using var transaction = await database.Database.BeginTransactionAsync(isolation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => GroupSourceReadLock.AcquireAsync(database, Scope(), default));
        Assert.Equal(0, connection.Calls);
    }

    [Fact]
    public async Task Read_lock_is_not_a_permission_verdict_and_revocation_is_rechecked()
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(1);
        await using var database = Database(connection);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var read = await GroupSourceReadLock.AcquireAsync(database, Scope(), default);
        await new GroupModelAttemptResultPermissionVerifier(database).RequireSafeRuntimeAsync(default);
        connection.Result = 0;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new GroupModelAttemptResultPermissionVerifier(database).RequireSafeRuntimeAsync(default));
        Assert.Equal(3, connection.Calls);
        Assert.True(GroupSourceReadLock.IsReadTransaction(transaction.GetDbTransaction()));
    }

    [Fact]
    public async Task Cancellation_does_not_open_a_connection_or_touch_a_disposed_context()
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(0);
        await using var database = Database(connection);
        await database.DisposeAsync();
        using var stopped = new CancellationTokenSource(); stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GroupSourceReadLock.AcquireAsync(database, Scope(), stopped.Token));
        Assert.Equal(0, connection.Calls);
    }

    private static PlatformDbContext Database(BindingStorePermissionTests.SyntheticConnection connection) =>
        new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(connection).Options);
    private static GroupScope Scope() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
}
