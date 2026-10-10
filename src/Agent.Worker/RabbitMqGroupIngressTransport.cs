using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MinhHuy.AIOffice.Agent.Worker;

public static class GroupIngressDeliveryBoundary
{
    // Parse and host-scope checks precede dependency creation. ACK is permitted
    // only after the SQL handler returns an exact durable committed receipt.
    public static async Task<GroupIngressInboxReceipt> ReceiveAsync(ReadOnlyMemory<byte> body, string? messageType,
        string? messageId, string? contentType, GroupExtractionWorkerBinding worker,
        Func<GroupIngressDispatchReference, CancellationToken, Task<GroupIngressInboxReceipt>> handler,
        TimeProvider clock, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); worker.Validate();
        var reference = GroupIngressDispatchReference.Parse(body.Span, messageType, messageId, contentType);
        if (reference.Source.TenantId != worker.TenantId || reference.Source.CompanyId != worker.CompanyId)
            throw new UnauthorizedAccessException("Group worker scope is not available.");
        var receipt = await handler(reference, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow();
        if (receipt is null || receipt.Reference != reference || receipt.ReceivedAtUtc.Offset != TimeSpan.Zero ||
            now.Offset != TimeSpan.Zero || receipt.ReceivedAtUtc > now)
            throw new InvalidOperationException("Group worker receipt is not available.");
        return receipt;
    }
}

public sealed class RabbitMqGroupIngressPublisher(IOptions<RabbitMqWorkOptions> options, GroupExtractionWorkerBinding worker)
    : IGroupIngressReferencePublisher
{
    public async Task PublishAsync(GroupIngressDispatchReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        worker.Validate(); reference.Validate(); options.Value.Validate();
        if (reference.Source.TenantId != worker.TenantId || reference.Source.CompanyId != worker.CompanyId)
            throw new UnauthorizedAccessException("Group worker scope is not available.");
        var body = reference.ToBytes();
        var factory = CreateFactory(options.Value);
        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(RabbitMqWorkPublisher.CreateConfirmingChannelOptions(), cancellationToken);
        await DeclareAsync(channel, worker, cancellationToken);
        await PublishReferenceAsync(channel, worker, reference, body, cancellationToken);
    }

    internal static string QueueName(GroupExtractionWorkerBinding worker)
    {
        worker.Validate();
        return $"minhhuy.group-ingress.v1.{worker.TenantId:N}.{worker.CompanyId:N}.{worker.ServiceId:N}";
    }

    internal static ConnectionFactory CreateFactory(RabbitMqWorkOptions options)
    {
        var factory = RabbitMqWorkPublisher.CreateFactory(options);
        factory.MaxInboundMessageBodySize = GroupIngressDispatchReference.MaximumBytes;
        factory.RequestedConnectionTimeout = GroupIngressOutboxDispatcher.PublishDeadline;
        factory.ConsumerDispatchConcurrency = 1;
        // This transport owns replacement attempts and their callbacks. Avoid
        // racing library recovery with disposal of the old delivery channel.
        factory.AutomaticRecoveryEnabled = false;
        return factory;
    }

    internal static Task<QueueDeclareOk> DeclareAsync(IChannel channel, GroupExtractionWorkerBinding worker, CancellationToken cancellationToken) =>
        channel.QueueDeclareAsync(QueueName(worker), durable: true, exclusive: false, autoDelete: false,
            arguments: null, cancellationToken: cancellationToken);

    internal static ValueTask PublishReferenceAsync(IChannel channel, GroupExtractionWorkerBinding worker,
        GroupIngressDispatchReference reference, byte[] body, CancellationToken cancellationToken) =>
        channel.BasicPublishAsync(exchange: string.Empty, routingKey: QueueName(worker), mandatory: true,
            basicProperties: new BasicProperties
            {
                ContentType = "application/json",
                Type = GroupIngressDispatchReference.MessageType,
                DeliveryMode = DeliveryModes.Persistent,
                MessageId = reference.EventId.ToString("N")
            }, body: body, cancellationToken: cancellationToken);
}

// This consumer is registered only by the separate, explicit group pipeline
// switch. WorkDispatchEnvelope/portal task consumers use a different queue.
public sealed class RabbitMqGroupIngressConsumer(IOptions<RabbitMqWorkOptions> options, GroupExtractionWorkerBinding worker,
    IServiceScopeFactory scopeFactory, TimeProvider clock, ILogger<RabbitMqGroupIngressConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        worker.Validate(); options.Value.Validate();
        var delay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var factory = RabbitMqGroupIngressPublisher.CreateFactory(options.Value);
                await using var connection = await factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(RabbitMqWorkPublisher.CreateConfirmingChannelOptions(), stoppingToken);
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.ChannelShutdownAsync += (_, _) => { closed.TrySetResult(); return Task.CompletedTask; };
                channel.CallbackExceptionAsync += (_, _) => { closed.TrySetResult(); return Task.CompletedTask; };
                connection.ConnectionShutdownAsync += (_, _) => { closed.TrySetResult(); return Task.CompletedTask; };
                await RabbitMqGroupIngressPublisher.DeclareAsync(channel, worker, stoppingToken);
                await channel.BasicQosAsync(0, options.Value.PrefetchCount, false, stoppingToken);
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, args) => await ReceiveAsync(channel, args, stoppingToken);
                await channel.BasicConsumeAsync(RabbitMqGroupIngressPublisher.QueueName(worker), autoAck: false, consumer, stoppingToken);
                // Connection/channel ownership stays in this attempt. A callback
                // never acknowledges a delivery on a later replacement channel.
                await closed.Task.WaitAsync(stoppingToken);
                throw new InvalidOperationException("Group broker connection closed.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch
            {
                logger.LogWarning("Group reference consumer connection is unavailable; reconnecting.");
                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
            }
        }
    }

    private async Task ReceiveAsync(IChannel channel, BasicDeliverEventArgs args, CancellationToken stoppingToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(GroupIngressOutboxDispatcher.PublishDeadline);
        try
        {
            await GroupIngressDeliveryBoundary.ReceiveAsync(args.Body, args.BasicProperties.Type, args.BasicProperties.MessageId,
                args.BasicProperties.ContentType, worker, async (reference, token) =>
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    return await scope.ServiceProvider.GetRequiredService<GroupIngressInboxStore>().ReceiveAsync(reference, token);
                }, clock, deadline.Token);
            await channel.BasicAckAsync(args.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Closing this owned channel returns an unsettled delivery. SQL
            // inbox/outbox state remains authoritative across shutdown.
        }
        catch
        {
            logger.LogWarning("Group reference delivery was not acknowledged; durable SQL recovery remains pending.");
            try
            {
                // No immediate poison/revocation retry loop. The producer retries
                // unchanged SQL references after its durable five-second delay.
                await channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: false, cancellationToken: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }
}
