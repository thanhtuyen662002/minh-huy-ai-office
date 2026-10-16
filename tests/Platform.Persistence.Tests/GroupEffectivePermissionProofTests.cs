using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupEffectivePermissionProofTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Complete_effective_chain_is_one_fresh_command_in_the_current_transaction(int depth)
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(1);
        await using var database = Database(connection);
        await using var transaction = await database.Database.BeginTransactionAsync();
        await RequireAsync(database, depth);
        Assert.Equal(1, connection.Calls);
        Assert.Same(transaction.GetDbTransaction(), connection.LastTransaction);
        Assert.Equal(ConnectionState.Open, connection.State);
        var command = Assert.Single(connection.Commands);
        foreach (var proof in OriginalProofs().Take(depth + 1))
            Assert.Contains("(" + proof.Trim().TrimEnd(';') + ")=1", command, StringComparison.Ordinal);
        Assert.Equal(depth + 1, command.Split("SELECT CASE WHEN", StringSplitOptions.None).Length - 2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Effective_permission_result_is_never_reused_between_fence_invocations(int depth)
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(1);
        await using var database = Database(connection);
        await RequireAsync(database, depth);
        Assert.Equal(ConnectionState.Closed, connection.State);
        connection.Result = 0;
        var failure = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RequireAsync(database, depth));
        Assert.Equal("Data source is unavailable for authorized use.", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.Equal(2, connection.Calls);
        Assert.Equal(connection.Commands[0], connection.Commands[1]);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    [InlineData("1")]
    [InlineData(1L)]
    [InlineData(true)]
    public async Task Batched_result_requires_an_explicit_integer_one(object? result)
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(result);
        await using var database = Database(connection);
        var failure = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RequireAsync(database, 5));
        Assert.Equal("Data source is unavailable for authorized use.", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.Equal(1, connection.Calls);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task Already_cancelled_fence_does_not_execute_or_open_a_connection()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(1);
        await using var database = Database(connection);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RequireAsync(database, 5, cancellation.Token));
        Assert.Equal(0, connection.Calls);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task Storage_error_is_sanitized_and_owned_connection_is_closed()
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(1)
        { Fault = new InvalidOperationException("PRIVATE_PERMISSION_DIAGNOSTIC") };
        await using var database = Database(connection);
        var failure = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RequireAsync(database, 5));
        Assert.Equal("Data source is unavailable for authorized use.", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.Equal(1, connection.Calls);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void Every_proof_is_compared_to_one_in_the_same_scalar_case()
    {
        var sql = GroupEffectivePermissionProof.All("SELECT 1;", "SELECT NULL;", "SELECT 0;");
        Assert.Equal("SELECT CASE WHEN\n(SELECT 1)=1\n AND (SELECT NULL)=1\n AND (SELECT 0)=1\n THEN 1 ELSE 0 END;", sql);
        Assert.Throws<ArgumentException>(() => GroupEffectivePermissionProof.All());
        Assert.Throws<ArgumentException>(() => GroupEffectivePermissionProof.All(" "));
        Assert.Throws<ArgumentNullException>(() => GroupEffectivePermissionProof.All(null!));
    }

    private static PlatformDbContext Database(BindingStorePermissionTests.SyntheticConnection connection) =>
        new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(connection).Options);

    private static string[] OriginalProofs() =>
    [
        BindingStorePermissionVerifier.VerificationSql,
        GroupIngressPermissionVerifier.VerificationSql,
        GroupWorkNotePermissionVerifier.VerificationSql,
        GroupModelAttemptPermissionVerifier.VerificationSql,
        GroupAutomaticInputPlanPermissionVerifier.VerificationSql,
        GroupModelAttemptResultPermissionVerifier.VerificationSql
    ];

    private static Task RequireAsync(PlatformDbContext database, int depth, CancellationToken token = default) => depth switch
    {
        1 => new GroupIngressPermissionVerifier(database).RequireSafeRuntimeAsync(token),
        2 => new GroupWorkNotePermissionVerifier(database).RequireSafeRuntimeAsync(token),
        3 => new GroupModelAttemptPermissionVerifier(database).RequireSafeRuntimeAsync(token),
        4 => new GroupAutomaticInputPlanPermissionVerifier(database).RequireSafeRuntimeAsync(token),
        5 => new GroupModelAttemptResultPermissionVerifier(database).RequireSafeRuntimeAsync(token),
        _ => throw new ArgumentOutOfRangeException(nameof(depth))
    };
}
