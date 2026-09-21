extern alias RuntimeWorker;

using System.Reflection;
using System.Text.Json;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class RabbitMqWorkTransportTests
{
    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)257)]
    public void Options_reject_unbounded_or_excessive_prefetch(ushort prefetch)
    {
        var options = new RabbitMqWorkOptions { PrefetchCount = prefetch };
        var exception = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("prefetch", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData((ushort)1)]
    [InlineData((ushort)8)]
    [InlineData((ushort)256)]
    public void Options_accept_bounded_prefetch(ushort prefetch)
    {
        var options = new RabbitMqWorkOptions { PrefetchCount = prefetch };
        options.Validate();
    }

    [Fact]
    public void Consumer_rejects_tampered_idempotency_identity_before_handler()
    {
        var valid = CreateEnvelope();
        var tampered = valid with { IdempotencyKey = "v1:tampered" };
        var exception = InvokeEnvelopeValidation(tampered);
        Assert.IsType<JsonException>(exception);
    }

    [Fact]
    public void Consumer_rejects_invalid_attempt_before_handler()
    {
        var invalid = CreateEnvelope() with { Attempt = 0 };
        var exception = InvokeEnvelopeValidation(invalid);
        Assert.IsType<JsonException>(exception);
    }

    [Fact]
    public void Consumer_accepts_contract_created_envelope()
    {
        var exception = InvokeEnvelopeValidation(CreateEnvelope());
        Assert.Null(exception);
    }

    [Fact]
    public void Settlement_keeps_transient_work_retryable_until_budget_is_exhausted()
    {
        Assert.Equal(BrokerSettlement.Requeue, WorkDeliverySettlement.Resolve(WorkDeliveryOutcome.Failed, WorkFailureClass.Transient, attempt: 1, maxAttempts: 3));
        Assert.Equal(BrokerSettlement.DeadLetter, WorkDeliverySettlement.Resolve(WorkDeliveryOutcome.Failed, WorkFailureClass.Transient, attempt: 3, maxAttempts: 3));
        Assert.Equal(BrokerSettlement.Acknowledge, WorkDeliverySettlement.Resolve(WorkDeliveryOutcome.AlreadyCompleted, null, attempt: 2, maxAttempts: 3));
    }

    private static WorkDispatchEnvelope CreateEnvelope() => WorkDispatchEnvelope.Create(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 0, DateTimeOffset.UtcNow);

    private static Exception? InvokeEnvelopeValidation(WorkDispatchEnvelope envelope)
    {
        var method = typeof(RabbitMqWorkConsumer).GetMethod("ValidateEnvelope", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("RabbitMQ envelope validator was not found.");
        try
        {
            method.Invoke(null, [envelope]);
            return null;
        }
        catch (TargetInvocationException exception)
        {
            return exception.InnerException;
        }
    }
}
