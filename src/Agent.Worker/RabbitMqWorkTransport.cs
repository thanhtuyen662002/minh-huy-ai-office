using System.Text.Json;
using Microsoft.Extensions.Options;
using MinhHuy.AIOffice.Shared.Contracts;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MinhHuy.AIOffice.Agent.Worker;

public sealed class RabbitMqWorkOptions
{
    public const string SectionName = "RabbitMqWork";
    public const ushort MaximumPrefetchCount = 256;

    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = AmqpTcpEndpoint.UseDefaultPort;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";
    public string QueueName { get; set; } = "minhhuy.work.v1";
    public ushort PrefetchCount { get; set; } = 8;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(HostName)) throw new InvalidOperationException("RabbitMQ host is required.");
        if (string.IsNullOrWhiteSpace(QueueName)) throw new InvalidOperationException("RabbitMQ queue is required.");
        if (PrefetchCount is 0 || PrefetchCount > MaximumPrefetchCount)
        {
            throw new InvalidOperationException($"RabbitMQ prefetch must be between 1 and {MaximumPrefetchCount}.");
        }
    }
}

public sealed record WorkDeliveryResult(
    WorkDeliveryOutcome Outcome,
    WorkFailureClass? FailureClass,
    int MaxAttempts,
    long? DurableCheckpointVersion = null,
    WorkDispatchEnvelope? DurableRetryEnvelope = null);

public interface IWorkDeliveryHandler
{
    /// <summary>Returns only after the delivery outcome and any checkpoint/completion/retry state are durably committed.</summary>
    Task<WorkDeliveryResult> HandleAsync(WorkDispatchEnvelope envelope, CancellationToken cancellationToken);
}

public interface IWorkPublisher
{
    Task PublishAsync(WorkDispatchEnvelope envelope, CancellationToken cancellationToken);
}

public sealed class RabbitMqWorkPublisher(IOptions<RabbitMqWorkOptions> options) : IWorkPublisher
{
    private readonly RabbitMqWorkOptions _options = options.Value;

    public async Task PublishAsync(WorkDispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        _options.Validate();
        var factory = CreateFactory(_options);
        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(CreateConfirmingChannelOptions(), cancellationToken);
        await DeclareQueueAsync(channel, _options, cancellationToken);
        await PublishEnvelopeAsync(channel, _options, envelope, cancellationToken);
    }

    internal static ConnectionFactory CreateFactory(RabbitMqWorkOptions options) => new()
    {
        HostName = options.HostName,
        Port = options.Port,
        UserName = options.UserName,
        Password = options.Password,
        VirtualHost = options.VirtualHost,
        AutomaticRecoveryEnabled = true,
        ConsumerDispatchConcurrency = 1
    };

    internal static CreateChannelOptions CreateConfirmingChannelOptions() => new(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);

    internal static Task<QueueDeclareOk> DeclareQueueAsync(IChannel channel, RabbitMqWorkOptions options, CancellationToken cancellationToken) =>
        channel.QueueDeclareAsync(queue: options.QueueName, durable: true, exclusive: false, autoDelete: false, arguments: null, cancellationToken: cancellationToken);

    internal static ValueTask PublishEnvelopeAsync(IChannel channel, RabbitMqWorkOptions options, WorkDispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(envelope);
        var properties = new BasicProperties { ContentType = "application/json", DeliveryMode = DeliveryModes.Persistent, MessageId = envelope.MessageId.ToString("N") };
        return channel.BasicPublishAsync(exchange: string.Empty, routingKey: options.QueueName, mandatory: true, basicProperties: properties, body: body, cancellationToken: cancellationToken);
    }
}

public sealed class RabbitMqWorkConsumer(IOptions<RabbitMqWorkOptions> options, IWorkDeliveryHandler handler, ILogger<RabbitMqWorkConsumer> logger) : BackgroundService
{
    private readonly RabbitMqWorkOptions _options = options.Value;
    private IConnection? _connection;
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _options.Validate();
        var factory = RabbitMqWorkPublisher.CreateFactory(_options);
        _connection = await factory.CreateConnectionAsync(stoppingToken);
        _channel = await _connection.CreateChannelAsync(RabbitMqWorkPublisher.CreateConfirmingChannelOptions(), stoppingToken);
        await RabbitMqWorkPublisher.DeclareQueueAsync(_channel, _options, stoppingToken);
        await _channel.BasicQosAsync(0, _options.PrefetchCount, false, stoppingToken);
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnReceivedAsync;
        await _channel.BasicConsumeAsync(_options.QueueName, autoAck: false, consumer, stoppingToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs args)
    {
        if (_channel is null) return;
        try
        {
            var envelope = JsonSerializer.Deserialize<WorkDispatchEnvelope>(args.Body.Span) ?? throw new JsonException("Work envelope is empty.");
            ValidateEnvelope(envelope);
            var result = await handler.HandleAsync(envelope, CancellationToken.None);
            var settlement = WorkDeliverySettlement.Resolve(result.Outcome, result.FailureClass, envelope.Attempt, result.MaxAttempts);
            switch (settlement)
            {
                case BrokerSettlement.Acknowledge:
                    await _channel.BasicAckAsync(args.DeliveryTag, multiple: false);
                    break;
                case BrokerSettlement.Requeue:
                    var retry = result.DurableRetryEnvelope
                        ?? throw new InvalidOperationException("Retry settlement requires a durably persisted retry envelope.");
                    await RabbitMqWorkPublisher.PublishEnvelopeAsync(_channel, _options, retry, CancellationToken.None);
                    await _channel.BasicAckAsync(args.DeliveryTag, multiple: false);
                    break;
                case BrokerSettlement.DeadLetter:
                    await _channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: false);
                    break;
                default:
                    throw new InvalidOperationException("Unknown broker settlement.");
            }
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Rejecting malformed RabbitMQ work delivery");
            await _channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: false);
        }
        catch (OperationCanceledException)
        {
            await _channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "RabbitMQ work delivery failed before durable settlement");
            await _channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: true);
        }
    }

    private static void ValidateEnvelope(WorkDispatchEnvelope envelope)
    {
        if (envelope.MessageId == Guid.Empty || envelope.TenantId == Guid.Empty || envelope.CompanyId == Guid.Empty || envelope.TaskId == Guid.Empty || envelope.StepId == Guid.Empty || envelope.Attempt < 1 || envelope.CheckpointVersion < 0)
            throw new JsonException("Work envelope contains invalid execution metadata.");
        var expectedIdempotencyKey = WorkIdempotencyKey.ForStep(envelope.TenantId, envelope.CompanyId, envelope.TaskId, envelope.StepId);
        if (!string.Equals(envelope.IdempotencyKey, expectedIdempotencyKey, StringComparison.Ordinal))
            throw new JsonException("Work envelope idempotency identity is invalid.");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
    }
}
