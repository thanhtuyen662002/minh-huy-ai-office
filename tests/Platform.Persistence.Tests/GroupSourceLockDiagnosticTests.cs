extern alias GroupReferenceProof;

using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;
using Proof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupModelAttemptResultRuntimeProof;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupSourceLockDiagnosticTests
{
    [Theory]
    [InlineData(-1, nameof(GroupSourceLockFailure.WaitTimeout), "source-lock-wait-timeout")]
    [InlineData(-2, nameof(GroupSourceLockFailure.Cancelled), "source-lock-cancelled")]
    [InlineData(-3, nameof(GroupSourceLockFailure.Deadlock), "source-lock-deadlock")]
    [InlineData(-999, nameof(GroupSourceLockFailure.Rejected), "source-lock-rejected")]
    [InlineData(int.MinValue, nameof(GroupSourceLockFailure.Rejected), "source-lock-rejected")]
    [InlineData(-77, nameof(GroupSourceLockFailure.Rejected), "source-lock-rejected")]
    public async Task Actual_negative_result_maps_only_to_a_closed_owned_diagnostic(int result,
        string expected, string category)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => GroupSourceTransactionLock.RequireResult(result));
        Assert.Equal(typeof(InvalidOperationException), failure.GetType());
        Assert.Equal("Group source transaction is not available.", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.Equal(expected, GroupSourceTransactionLock.FailureOf(failure)?.ToString());
        var diagnostic = new Proof.ResultDiagnostic();
        var projected = await Assert.ThrowsAsync<Proof.PhaseFailure>(() => Proof.ObserveAsync<int>(
            Proof.ResultPhase.ConcurrentRead, () => Task.FromException<int>(failure), diagnostic));
        Assert.Equal("FAIL owned attempt result runtime phase-concurrent-read kind-" + category, Proof.FailureDiagnostic(projected));
        Assert.DoesNotContain(result.ToString(System.Globalization.CultureInfo.InvariantCulture), Proof.FailureDiagnostic(projected));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Existing_nonnegative_results_remain_accepted(int result) => GroupSourceTransactionLock.RequireResult(result);

    [Fact]
    public async Task Actual_missing_transaction_is_distinct_and_does_not_execute_a_scalar()
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(1);
        await using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(connection).Options);
        var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => GroupSourceTransactionLock.RequireAsync(database, scope, default));
        Assert.Equal(GroupSourceLockFailure.MissingTransaction, GroupSourceTransactionLock.FailureOf(failure));
        Assert.Equal(0, connection.Calls);
        var projected = await Assert.ThrowsAsync<Proof.PhaseFailure>(() => Proof.ObserveAsync<int>(
            Proof.ResultPhase.ConcurrentRead, () => Task.FromException<int>(failure), new Proof.ResultDiagnostic()));
        Assert.Equal("FAIL owned attempt result runtime phase-concurrent-read kind-source-lock-missing-transaction", Proof.FailureDiagnostic(projected));
        Assert.DoesNotContain(scope.TenantId.ToString(), Proof.FailureDiagnostic(projected));
    }

    [Theory]
    [InlineData("source-lock-wait-timeout")]
    [InlineData("PRIVATE_SOURCE_LOCK_DIAGNOSTIC")]
    [InlineData(1)]
    [InlineData(null)]
    public async Task Unqualified_metadata_cannot_be_interpolated_into_a_failure(object? metadata)
    {
        var failure = new InvalidOperationException("Group source transaction is not available.");
        failure.Data["AIOffice.GroupSourceLockFailure.v1"] = metadata;
        Assert.Null(GroupSourceTransactionLock.FailureOf(failure));
        var projected = await Assert.ThrowsAsync<Proof.PhaseFailure>(() => Proof.ObserveAsync<int>(
            Proof.ResultPhase.ConcurrentRead, () => Task.FromException<int>(failure), new Proof.ResultDiagnostic()));
        Assert.Equal("FAIL owned attempt result runtime phase-concurrent-read kind-source-lock", Proof.FailureDiagnostic(projected));
    }

    [Fact]
    public void Undefined_enum_or_wrong_exception_cannot_supply_actual_site_metadata()
    {
        var failure = new InvalidOperationException("Group source transaction is not available.");
        failure.Data["AIOffice.GroupSourceLockFailure.v1"] = (GroupSourceLockFailure)999;
        Assert.Null(GroupSourceTransactionLock.FailureOf(failure));
        var other = new InvalidOperationException("PRIVATE_EXCEPTION");
        other.Data["AIOffice.GroupSourceLockFailure.v1"] = GroupSourceLockFailure.WaitTimeout;
        Assert.Null(GroupSourceTransactionLock.FailureOf(other));
    }
}
