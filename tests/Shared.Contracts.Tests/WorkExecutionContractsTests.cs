using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class WorkExecutionContractsTests
{
    [Fact]
    public void DispatchEnvelope_UsesStableIdempotencyKeyAcrossRedelivery()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var stepId = Guid.NewGuid();

        var first = WorkDispatchEnvelope.Create(
            Guid.NewGuid(),
            tenantId,
            companyId,
            taskId,
            stepId,
            attempt: 1,
            checkpointVersion: null,
            DateTimeOffset.UtcNow);

        var retry = WorkDispatchEnvelope.Create(
            Guid.NewGuid(),
            tenantId,
            companyId,
            taskId,
            stepId,
            attempt: 2,
            checkpointVersion: 3,
            DateTimeOffset.UtcNow);

        Assert.NotEqual(first.MessageId, retry.MessageId);
        Assert.Equal(first.IdempotencyKey, retry.IdempotencyKey);
    }

    [Theory]
    [InlineData(WorkFailureClass.Transient, 1, 3, RetryDisposition.Retry)]
    [InlineData(WorkFailureClass.Transient, 3, 3, RetryDisposition.DeadLetter)]
    [InlineData(WorkFailureClass.Permanent, 1, 3, RetryDisposition.DeadLetter)]
    [InlineData(WorkFailureClass.Authorization, 1, 3, RetryDisposition.DeadLetter)]
    [InlineData(WorkFailureClass.Validation, 1, 3, RetryDisposition.DeadLetter)]
    public void RetryPolicy_OnlyRetriesTransientFailuresWithinAttemptBudget(
        WorkFailureClass failureClass,
        int attempt,
        int maxAttempts,
        RetryDisposition expected)
    {
        Assert.Equal(expected, WorkRetryPolicy.Classify(failureClass, attempt, maxAttempts));
    }

    [Theory]
    [InlineData(WorkDeliveryOutcome.Completed)]
    [InlineData(WorkDeliveryOutcome.AlreadyCompleted)]
    public void Settlement_AcknowledgesSuccessfulAndIdempotentRedelivery(
        WorkDeliveryOutcome outcome)
    {
        Assert.Equal(
            BrokerSettlement.Acknowledge,
            WorkDeliverySettlement.Resolve(outcome, null, attempt: 1, maxAttempts: 3));
    }

    [Fact]
    public void Settlement_RequeuesOnlyRetryableFailureWithinBudget()
    {
        Assert.Equal(
            BrokerSettlement.Requeue,
            WorkDeliverySettlement.Resolve(
                WorkDeliveryOutcome.Failed,
                WorkFailureClass.Transient,
                attempt: 1,
                maxAttempts: 3));

        Assert.Equal(
            BrokerSettlement.DeadLetter,
            WorkDeliverySettlement.Resolve(
                WorkDeliveryOutcome.Failed,
                WorkFailureClass.Transient,
                attempt: 3,
                maxAttempts: 3));

        Assert.Equal(
            BrokerSettlement.DeadLetter,
            WorkDeliverySettlement.Resolve(
                WorkDeliveryOutcome.Failed,
                WorkFailureClass.Authorization,
                attempt: 1,
                maxAttempts: 3));
    }

    [Fact]
    public void Settlement_RejectsContradictoryOutcomeMetadata()
    {
        Assert.Throws<ArgumentException>(() => WorkDeliverySettlement.Resolve(
            WorkDeliveryOutcome.Completed,
            WorkFailureClass.Transient,
            attempt: 1,
            maxAttempts: 3));

        Assert.Throws<ArgumentException>(() => WorkDeliverySettlement.Resolve(
            WorkDeliveryOutcome.Failed,
            null,
            attempt: 1,
            maxAttempts: 3));
    }

    [Fact]
    public void LeaseSnapshot_RejectsExpiredOrStaleWorker()
    {
        var leaseId = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(1);
        var lease = new WorkLeaseSnapshot(leaseId, "worker-a", 7, expiresAt);

        Assert.True(lease.Authorizes(leaseId, "worker-a", 7, expiresAt.AddSeconds(-1)));
        Assert.False(lease.Authorizes(leaseId, "worker-a", 6, expiresAt.AddSeconds(-1)));
        Assert.False(lease.Authorizes(leaseId, "worker-b", 7, expiresAt.AddSeconds(-1)));
        Assert.False(lease.Authorizes(leaseId, "worker-a", 7, expiresAt));
    }

    [Fact]
    public void IdempotencyKey_IsTenantAndCompanyScoped()
    {
        var tenant = Guid.NewGuid();
        var company = Guid.NewGuid();
        var task = Guid.NewGuid();
        var step = Guid.NewGuid();

        var baseline = WorkIdempotencyKey.ForStep(tenant, company, task, step);

        Assert.NotEqual(
            baseline,
            WorkIdempotencyKey.ForStep(Guid.NewGuid(), company, task, step));
        Assert.NotEqual(
            baseline,
            WorkIdempotencyKey.ForStep(tenant, Guid.NewGuid(), task, step));
    }
}
