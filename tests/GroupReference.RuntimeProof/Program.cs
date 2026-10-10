extern alias RuntimeWorker;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MinhHuy.AIOffice.GroupReference.RuntimeProof;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;

var phase = "owned-guard";
try
{
    // Test-only executable: guard before stdin, configuration, credentials,
    // SQL, broker or any resource. It has no operator/customer credentials.
    OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
    if (args.Length != 1 || args[0] is not ("publish" or "publish-existing" or "statistics" or "consume-hold" or "consume-replay" or "duplicates" or "deny" or "rollback" or "unsafe"))
        throw new InvalidOperationException();
    phase = "owned-config";
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 4
    };
    var bytes = new byte[8193]; var length = 0;
    using (var input = Console.OpenStandardInput())
    {
        while (length < bytes.Length)
        {
            var read = await input.ReadAsync(bytes.AsMemory(length));
            if (read == 0) break;
            length += read;
        }
    }
    if (length is < 1 or > 8192) throw new InvalidOperationException();
    var config = JsonSerializer.Deserialize<ProofConfiguration>(bytes.AsSpan(0, length), json) ?? throw new InvalidOperationException();
    var worker = new GroupExtractionWorkerBinding(config.TenantId, config.CompanyId, config.ServiceId, 1); worker.Validate();
    var scope = new GroupScope(config.TenantId, config.CompanyId, config.SourceId); scope.Validate();
    if (config.EventId == Guid.Empty) throw new InvalidOperationException();
    var connection = OwnedGroupReferenceProofGuard.RequireRuntimeConnection(Environment.GetEnvironmentVariable("AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION"));
    var databaseOptions = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(connection).Options;
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(150));
    await using var database = new PlatformDbContext(databaseOptions);
    var outbox = await database.GroupIngressOutbox.AsNoTracking().Where(row => row.TenantId == scope.TenantId && row.CompanyId == scope.CompanyId &&
        row.BindingId == scope.SourceBindingId && row.Id == config.EventId)
        .Select(row => new { row.Id, row.MessageId, row.Revision, row.CommittedSequence }).SingleAsync(lifetime.Token);
    var reference = new GroupIngressDispatchReference(1, scope, outbox.Id, outbox.MessageId, outbox.Revision, outbox.CommittedSequence);
    var inbox = new GroupIngressInboxStore(database, worker, TimeProvider.System);
    phase = args[0];
    if (args[0] is "deny" or "rollback" or "unsafe")
    {
        var refused = false;
        try { await inbox.ReceiveAsync(reference, lifetime.Token); }
        catch (UnauthorizedAccessException) when (args[0] == "deny") { refused = true; }
        catch (DbUpdateException) when (args[0] == "rollback") { refused = true; }
        catch (InvalidOperationException) when (args[0] == "unsafe") { refused = true; }
        if (!refused || database.ChangeTracker.HasChanges()) throw new InvalidOperationException();
        Console.WriteLine("PASS owned reference runtime refusal " + args[0]);
        return 0;
    }
    await new GroupIngressPermissionVerifier(database).RequireSafeRuntimeAsync(lifetime.Token);
    if (args[0] == "duplicates")
    {
        var previous = await inbox.ReceiveAsync(reference, lifetime.Token);
        if (!previous.WasAlreadyReceived) throw new InvalidOperationException();
        using var slots = new SemaphoreSlim(8);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(async _ =>
        {
            await slots.WaitAsync(lifetime.Token);
            try
            {
                await using var duplicateDatabase = new PlatformDbContext(databaseOptions);
                var duplicate = await new GroupIngressInboxStore(duplicateDatabase, worker, TimeProvider.System).ReceiveAsync(reference, lifetime.Token);
                if (duplicate != previous || duplicateDatabase.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            }
            finally { slots.Release(); }
        }));
        Console.WriteLine("PASS owned reference runtime100 concurrent original inbox receipts");
        return 0;
    }
    var password = Environment.GetEnvironmentVariable("AIOFFICE_GROUP_REFERENCE_PROOF_BROKER_PASSWORD");
    if (password is null || password.Length is < 32 or > 128 || password.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
        throw new InvalidOperationException();
    var options = new RabbitMqWorkOptions { HostName = "rabbitmq", Port = 5672, UserName = "aioffice-local", Password = password, VirtualHost = "/" };
    if (args[0] == "publish-existing")
    {
        if (!(await inbox.ReceiveAsync(reference, lifetime.Token)).WasAlreadyReceived) throw new InvalidOperationException();
        await new RabbitMqGroupIngressPublisher(Options.Create(options), worker).PublishAsync(reference, lifetime.Token);
        Console.WriteLine("PASS owned reference runtime shipping original accepted reference publication");
        return 0;
    }
    if (args[0] == "publish")
    {
        var dispatcher = new GroupIngressOutboxDispatcher(database, worker, new RabbitMqGroupIngressPublisher(Options.Create(options), worker), TimeProvider.System);
        if (!await dispatcher.PublishNextAsync(scope, lifetime.Token)) throw new InvalidOperationException();
        Console.WriteLine("PASS owned reference runtime shipping outbox publication");
        return 0;
    }
    // Use shipping factory/queue derivation; the barrier exists only here.
    var factory = (ConnectionFactory)typeof(RabbitMqGroupIngressPublisher).GetMethod("CreateFactory", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [options])!;
    var queue = (string)typeof(RabbitMqGroupIngressPublisher).GetMethod("QueueName", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [worker])!;
    if (args[0] == "statistics")
    {
        if (!(await inbox.ReceiveAsync(reference, lifetime.Token)).WasAlreadyReceived) throw new InvalidOperationException();
        using var statisticsDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        statisticsDeadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://rabbitmq:15672/api/queues/%2F/" + queue);
        request.Headers.Authorization = new("Basic", Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(options.UserName + ":" + password)));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, statisticsDeadline.Token);
        if (response.StatusCode != System.Net.HttpStatusCode.OK) throw new InvalidOperationException();
        await using var stream = await response.Content.ReadAsStreamAsync(statisticsDeadline.Token);
        var metrics = new byte[32769]; var used = 0;
        while (used < metrics.Length)
        {
            var read = await stream.ReadAsync(metrics.AsMemory(used), statisticsDeadline.Token);
            if (read == 0) break;
            used += read;
        }
        if (used is < 1 or > 32768) throw new InvalidOperationException();
        using var parsed = JsonDocument.Parse(metrics.AsMemory(0, used));
        var root = parsed.RootElement;
        static long Counter(JsonElement element, string property) => element.TryGetProperty(property, out var value) ? value.GetInt64() : 0;
        var statistics = root.TryGetProperty("message_stats", out var counts) ? counts : default;
        var ack = statistics.ValueKind == JsonValueKind.Object ? Counter(statistics, "ack") : 0;
        var deliver = statistics.ValueKind == JsonValueKind.Object ? Counter(statistics, "deliver") : 0;
        var consumers = Counter(root, "consumers");
        if (ack < 0 || deliver < 0 || consumers < 0) throw new InvalidOperationException();
        Console.WriteLine(JsonSerializer.Serialize(new { ack, deliver, consumers }));
        return 0;
    }
    await using var broker = await factory.CreateConnectionAsync(lifetime.Token);
    await using var channel = await broker.CreateChannelAsync(cancellationToken: lifetime.Token);
    await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, arguments: null, cancellationToken: lifetime.Token);
    await channel.BasicQosAsync(0, 1, false, lifetime.Token);
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var consumer = new AsyncEventingBasicConsumer(channel);
    consumer.ReceivedAsync += async (_, delivery) =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var receipt = await GroupIngressDeliveryBoundary.ReceiveAsync(delivery.Body, delivery.BasicProperties.Type,
                delivery.BasicProperties.MessageId, delivery.BasicProperties.ContentType, worker,
                async (received, cancellationToken) =>
                {
                    if (received != reference) throw new InvalidOperationException();
                    await using var receivedDatabase = new PlatformDbContext(databaseOptions);
                    return await new GroupIngressInboxStore(receivedDatabase, worker, TimeProvider.System).ReceiveAsync(received, cancellationToken);
                }, TimeProvider.System, deadline.Token);
            if (receipt.Reference != reference || receipt.WasAlreadyReceived != (args[0] == "consume-replay")) throw new InvalidOperationException();
            if (args[0] == "consume-hold")
            {
                Console.WriteLine("CHECKPOINT owned reference inbox committed before broker ACK"); Console.Out.Flush();
                await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token); // Coordinator kills only this owned container.
                throw new InvalidOperationException();
            }
            if (!delivery.Redelivered) throw new InvalidOperationException();
            await channel.BasicAckAsync(delivery.DeliveryTag, false, deadline.Token).AsTask().WaitAsync(deadline.Token);
            completion.TrySetResult();
        }
        catch { completion.TrySetException(new InvalidOperationException()); }
    };
    await channel.BasicConsumeAsync(queue, false, consumer, lifetime.Token);
    await completion.Task.WaitAsync(lifetime.Token);
    Console.WriteLine("PASS owned reference runtime redelivery original SQL receipt and broker ACK");
    return 0;
}
catch
{
    Console.WriteLine("FAIL owned reference runtime " + phase);
    return 1;
}

internal sealed record ProofConfiguration(Guid TenantId, Guid CompanyId, Guid ServiceId, Guid SourceId, Guid EventId);
