extern alias RuntimeWorker;

using System.Diagnostics;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupIngressConsumerSettlementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualConsumerSettlementUsesDeliveryDeadlineAndRetiresOnlyItsOriginalAttempt(bool malformed)
    {
        using var fixture = new GroupIngressOutboxDispatcherTests.Fixture();
        await using var services = new ServiceCollection().AddSingleton(fixture.Inbox).BuildServiceProvider();
        var consumer = new RabbitMqGroupIngressConsumer(Options.Create(new RabbitMqWorkOptions()), fixture.Worker,
            services.GetRequiredService<IServiceScopeFactory>(), fixture.Auth.Clock, NullLogger<RabbitMqGroupIngressConsumer>.Instance);
        var channel = DispatchProxy.Create<IChannel, SettlementProbe>();
        var probe = (SettlementProbe)(object)channel;
        var outbox = await fixture.OutboxAsync();
        var reference = new GroupIngressDispatchReference(1, fixture.Auth.Scope, outbox.Id, outbox.MessageId, outbox.Revision, outbox.CommittedSequence);
        var args = new BasicDeliverEventArgs("owned-consumer", 42, false, "", "owned-group-reference",
            new BasicProperties { Type = GroupIngressDispatchReference.MessageType, ContentType = "application/json", MessageId = outbox.Id.ToString("N") },
            malformed ? new byte[] { 0xff } : reference.ToBytes(), CancellationToken.None);
        var retired = false; var watch = Stopwatch.StartNew();
        var pending = (Task)typeof(RabbitMqGroupIngressConsumer).GetMethod("ReceiveAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(consumer, [channel, args, CancellationToken.None, (Action)(() => retired = true)])!;
        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(pending.IsCompleted); Assert.True(probe.Token.CanBeCanceled); Assert.False(probe.Token.IsCancellationRequested);
            Assert.Equal(malformed ? "BasicNackAsync" : "BasicAckAsync", probe.Method);
            Assert.Equal((ulong)42, probe.Tag);
            Assert.Equal(malformed ? 0 : 1, await fixture.Auth.Db.GroupIngressInbox.CountAsync());
            await pending.WaitAsync(TimeSpan.FromSeconds(13));
            Assert.InRange(watch.Elapsed.TotalSeconds, 9, 15);
            Assert.True(probe.Token.IsCancellationRequested); Assert.True(retired);
            Assert.Equal(1, probe.Calls); Assert.Single(await fixture.Auth.Db.GroupIngressOutbox.ToListAsync());
            Assert.False(fixture.Auth.Db.ChangeTracker.HasChanges());
            await fixture.RequireNoPrivateOrCursorEffectsAsync();
        }
        finally { probe.Held.TrySetException(new IOException("owned-late-private-settlement-failure")); }
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task ActualServerConsumerCancellationCompletesOwnedAttemptWithAnOtherwiseOpenConnectionAndChannel()
    {
        var channel = DispatchProxy.Create<IChannel, OpenResourceProbe>();
        var connection = DispatchProxy.Create<IConnection, OpenResourceProbe>();
        var consumer = new AsyncEventingBasicConsumer(channel);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(RabbitMqGroupIngressConsumer).GetMethod("BindAttemptCompletion", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [channel, connection, consumer, closed]);
        Assert.False(closed.Task.IsCompleted);
        await consumer.HandleBasicConsumeOkAsync("owned-consumer", CancellationToken.None);
        Assert.True(consumer.IsRunning);
        await consumer.HandleBasicCancelAsync("owned-consumer", CancellationToken.None);
        Assert.False(consumer.IsRunning); Assert.True(closed.Task.IsCompletedSuccessfully);
        Assert.True(channel.IsOpen); Assert.True(connection.IsOpen);
    }

    public class OpenResourceProbe : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "get_IsOpen") return true;
            if (targetMethod.Name.StartsWith("add_", StringComparison.Ordinal)) return null;
            throw new InvalidOperationException("Unexpected owned inert resource operation.");
        }
    }

    public class SettlementProbe : DispatchProxy
    {
        internal readonly TaskCompletionSource Held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token;
        internal string? Method;
        internal ulong Tag;
        internal int Calls;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name is not ("BasicAckAsync" or "BasicNackAsync")) throw new InvalidOperationException("Unexpected owned inert channel operation.");
            Calls++; Method = targetMethod.Name; Tag = (ulong)args![0]!;
            Token = (CancellationToken)args[^1]!;
            Assert.False((bool)args[1]!);
            if (Method == "BasicNackAsync") Assert.False((bool)args[2]!);
            Entered.TrySetResult();
            // Deliberately ignore the passed token: the shipping receiver must
            // still retire this original attempt at its overall deadline.
            return new ValueTask(Held.Task);
        }
    }
}
