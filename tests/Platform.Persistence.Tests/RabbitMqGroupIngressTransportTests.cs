extern alias RuntimeWorker;

using System.Reflection;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class RabbitMqGroupIngressTransportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 1, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactDurableReceiptIsReturnedOnlyAfterHandlerCompletes(bool duplicate)
    {
        var (reference, worker) = Fixture();
        var held = new TaskCompletionSource<GroupIngressInboxReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var pending = Deliver(reference.ToBytes(), reference, worker, (value, _) =>
        { Assert.Equal(reference, value); calls++; return held.Task; });
        Assert.Equal(1, calls); Assert.False(pending.IsCompleted);
        var original = new GroupIngressInboxReceipt(reference, Now.AddSeconds(-1), duplicate);
        held.SetResult(original);
        Assert.Equal(original, await pending);
    }

    [Theory]
    [InlineData("oversize")]
    [InlineData("duplicate")]
    [InlineData("source-text")]
    [InlineData("invalid-utf8")]
    [InlineData("type")]
    [InlineData("message-id")]
    [InlineData("content-type")]
    [InlineData("foreign-company")]
    [InlineData("canceled")]
    public async Task UntrustedBrokerHintsFailBeforeHandlerCreation(string change)
    {
        var (reference, worker) = Fixture();
        var body = reference.ToBytes(); var type = GroupIngressDispatchReference.MessageType;
        var id = reference.EventId.ToString("N"); var contentType = "application/json";
        if (change == "oversize") body = new byte[GroupIngressDispatchReference.MaximumBytes + 1];
        else if (change == "duplicate") body = "{\"version\":1,\"v\u0065rsion\":1}"u8.ToArray();
        else if (change == "source-text") body = "{\"version\":1,\"text\":\"owned-private-source\"}"u8.ToArray();
        else if (change == "invalid-utf8") body = [0xff];
        else if (change == "type") type = "aioffice-work-dispatch-v1";
        else if (change == "message-id") id = Guid.NewGuid().ToString("N");
        else if (change == "content-type") contentType = "application/json; charset=utf-8";
        else if (change == "foreign-company") worker = worker with { CompanyId = Guid.NewGuid() };
        using var cancellation = new CancellationTokenSource();
        if (change == "canceled") cancellation.Cancel();
        var called = false;
        var task = GroupIngressDeliveryBoundary.ReceiveAsync(body, type, id, contentType, worker,
            (_, _) => { called = true; throw new InvalidOperationException("Handler must not open resources."); }, new Clock(), cancellation.Token);
        if (change == "foreign-company") await Assert.ThrowsAsync<UnauthorizedAccessException>(() => task);
        else if (change == "canceled") await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.False(called);
    }

    [Theory]
    [InlineData("wrong-reference")]
    [InlineData("future-time")]
    [InlineData("non-utc")]
    [InlineData("revocation")]
    [InlineData("sql-failure")]
    [InlineData("canceled-after-commit")]
    public async Task RefusedOrMismatchedHandlerCompletionCannotAuthorizeAcknowledgement(string change)
    {
        var (reference, worker) = Fixture();
        using var cancellation = new CancellationTokenSource();
        var task = Deliver(reference.ToBytes(), reference, worker, (value, _) =>
        {
            if (change == "revocation") throw new UnauthorizedAccessException("Current Extract is disabled.");
            if (change == "sql-failure") throw new InvalidOperationException("SQL commit is unavailable.");
            if (change == "canceled-after-commit") cancellation.Cancel();
            var receipt = new GroupIngressInboxReceipt(value, Now.AddSeconds(-1), false);
            if (change == "wrong-reference") receipt = receipt with { Reference = value with { EventId = Guid.NewGuid() } };
            else if (change == "future-time") receipt = receipt with { ReceivedAtUtc = Now.AddSeconds(1) };
            else if (change == "non-utc") receipt = receipt with { ReceivedAtUtc = receipt.ReceivedAtUtc.ToOffset(TimeSpan.FromHours(1)) };
            return Task.FromResult(receipt);
        }, cancellation.Token);
        if (change == "revocation") await Assert.ThrowsAsync<UnauthorizedAccessException>(() => task);
        else if (change == "canceled-after-commit") await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => task);
    }

    [Fact]
    public void QueueAndTransportSettingsAreBoundToHostWorkerAndSeparateFromPortalWork()
    {
        var (_, worker) = Fixture();
        var queue = (string)Invoke("QueueName", worker)!;
        Assert.StartsWith("minhhuy.group-ingress.v1.", queue);
        Assert.NotEqual(queue, Invoke("QueueName", worker with { CompanyId = Guid.NewGuid() }));
        Assert.NotEqual(queue, Invoke("QueueName", worker with { ServiceId = Guid.NewGuid() }));
        Assert.DoesNotContain("minhhuy.work.v1", queue); Assert.InRange(queue.Length, 1, 255);
        var factory = Invoke("CreateFactory", new RabbitMqWorkOptions { QueueName = "minhhuy.work.v1" })!;
        object? Property(string name) => factory.GetType().GetProperty(name)!.GetValue(factory);
        Assert.Equal((uint)GroupIngressDispatchReference.MaximumBytes, Property("MaxInboundMessageBodySize"));
        Assert.Equal(false, Property("AutomaticRecoveryEnabled"));
        Assert.Equal((ushort)1, Property("ConsumerDispatchConcurrency"));
        Assert.Equal(GroupIngressOutboxDispatcher.PublishDeadline, Property("RequestedConnectionTimeout"));
    }

    private static object? Invoke(string name, params object[] arguments) => typeof(RabbitMqGroupIngressPublisher)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments);

    private static Task<GroupIngressInboxReceipt> Deliver(byte[] body, GroupIngressDispatchReference reference,
        GroupExtractionWorkerBinding worker, Func<GroupIngressDispatchReference, CancellationToken, Task<GroupIngressInboxReceipt>> handler,
        CancellationToken cancellationToken = default) => GroupIngressDeliveryBoundary.ReceiveAsync(body,
            GroupIngressDispatchReference.MessageType, reference.EventId.ToString("N"), "application/json", worker, handler, new Clock(), cancellationToken);

    private static (GroupIngressDispatchReference, GroupExtractionWorkerBinding) Fixture()
    {
        var source = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        return (new(1, source, Guid.NewGuid(), Guid.NewGuid(), 1, 1), new(source.TenantId, source.CompanyId, Guid.NewGuid(), 1));
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
}
