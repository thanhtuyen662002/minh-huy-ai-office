extern alias RuntimeWorker;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MinhHuy.AIOffice.Platform.Configuration;
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
    if (args.Length != 1 || args[0] is not ("publish" or "publish-existing" or "inspect-pending" or "statistics" or "consume-hold" or "consume-replay" or "duplicates" or "deny" or "rollback" or "unsafe" or "recovery-startup" or "allocation-hold" or "allocation-replay" or "allocation-deny" or "allocation-rollback" or "allocation-unsafe" or "claim-crash" or "claim-replay" or "claim-fence" or "claim-deny" or "claim-unsafe" or "claim-rollback"))
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
    if (args[0].StartsWith("claim-", StringComparison.Ordinal))
    {
        await GroupBatchClaimRuntimeProof.RunAsync(args[0], scope, config.EventId, worker, databaseOptions, lifetime.Token);
        return 0;
    }
    if (args[0].StartsWith("allocation-", StringComparison.Ordinal))
    {
        var allocation = new GroupBatchAllocationStore(database, worker, new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)), TimeProvider.System);
        if (args[0] is "allocation-deny" or "allocation-unsafe" or "allocation-rollback")
        {
            var refused = false;
            try { await allocation.AllocateDueAsync(scope, config.EventId, lifetime.Token); }
            catch (Exception error) when (OwnedGroupReferenceProofGuard.IsExpectedRefusal(args[0], error)) { refused = true; }
            if (!refused || database.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            Console.WriteLine("PASS owned allocation runtime refusal " + args[0]);
            return 0;
        }
        GroupBatchAllocationReceipt original;
        if (args[0] == "allocation-hold")
        {
            // Competing fresh SQL contexts start together against an empty reservation.
            // The source transaction lock must produce one original and stable replays.
            var creators = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
            {
                await using var creator = new PlatformDbContext(databaseOptions);
                return await new GroupBatchAllocationStore(creator, worker,
                    new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)), TimeProvider.System)
                    .AllocateDueAsync(scope, config.EventId, lifetime.Token) ?? throw new InvalidOperationException();
            }));
            original = creators.Single(x => !x.WasAlreadyAllocated);
            if (creators.Any(x => x.BatchId != original.BatchId || x.AllocatedAtUtc != original.AllocatedAtUtc
                || !x.Revisions.SequenceEqual(original.Revisions))) throw new InvalidOperationException();
        }
        else original = await allocation.AllocateDueAsync(scope, config.EventId, lifetime.Token) ?? throw new InvalidOperationException();
        if (original.WasAlreadyAllocated != (args[0] == "allocation-replay") || original.AfterSequence != 0
            || original.AllocatedThroughSequence != 2 || original.Revisions.Count != 2 || database.ChangeTracker.HasChanges()) throw new InvalidOperationException();
        if (args[0] == "allocation-hold")
        {
            Console.WriteLine("CHECKPOINT owned allocation committed before receipt delivery"); Console.Out.Flush();
            await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token); // Coordinator kills this inspected owned child only.
            throw new InvalidOperationException();
        }
        using var allocationSlots = new SemaphoreSlim(4);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(async _ =>
        {
            await allocationSlots.WaitAsync(lifetime.Token);
            try
            {
                await using var restarted = new PlatformDbContext(databaseOptions);
                var replay = await new GroupBatchAllocationStore(restarted, worker,
                    new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)), TimeProvider.System).AllocateDueAsync(scope, config.EventId, lifetime.Token)
                    ?? throw new InvalidOperationException();
                if (!replay.WasAlreadyAllocated || replay.BatchId != original.BatchId || replay.AllocatedAtUtc != original.AllocatedAtUtc
                    || !replay.Revisions.SequenceEqual(original.Revisions) || restarted.ChangeTracker.HasChanges()) throw new InvalidOperationException();
                if (await new GroupBatchAllocationStore(restarted, worker, GroupBatchTiming.InitialTuning, TimeProvider.System)
                    .AllocateDueAsync(scope, Guid.NewGuid(), lifetime.Token) is not null) throw new InvalidOperationException();
            }
            finally { allocationSlots.Release(); }
        }));
        Console.WriteLine("PASS owned allocation runtime100 concurrent original receipts and caught-up cursor");
        return 0;
    }
    if (args[0] is "deny" or "rollback" or "unsafe")
    {
        var refused = false;
        try { await inbox.ReceiveAsync(reference, lifetime.Token); }
        catch (Exception error) when (OwnedGroupReferenceProofGuard.IsExpectedRefusal(args[0], error)) { refused = true; }
        if (!refused || database.ChangeTracker.HasChanges()) throw new InvalidOperationException();
        Console.WriteLine("PASS owned reference runtime refusal " + args[0]);
        return 0;
    }
    if (args[0] == "recovery-startup")
    {
        // Exact recovery-only startup dependency with the actual owned runtime
        // SQL principal. Resolution remains inert: no provider, keys or volume.
        var privateRoot = Path.Combine(Path.GetTempPath(), "aioffice-inert-native-recovery-" + Guid.NewGuid().ToString("N"));
        var settings = new Dictionary<string, string?>
        {
            ["AIOffice:GroupIntake:Enabled"] = "true",
            ["AIOffice:GroupIntake:PipelineEnabled"] = "false",
            ["AIOffice:GroupIntake:ConnectorRecoveryEnabled"] = "true",
            ["AIOffice:GroupIntake:Connector:TenantId"] = config.TenantId.ToString("D"),
            ["AIOffice:GroupIntake:Connector:CompanyId"] = config.CompanyId.ToString("D"),
            ["AIOffice:GroupIntake:Connector:ServiceId"] = config.ServiceId.ToString("D"),
            ["AIOffice:GroupIntake:Connector:ConnectorAccountId"] = Guid.NewGuid().ToString("D"),
            ["AIOffice:GroupIntake:Connector:CredentialEpoch"] = "1",
            ["AIOffice:GroupIntake:Connector:BackendOrigin"] = "https://owned.invalid/",
            ["AIOffice:GroupIntake:Connector:PrivateRoot"] = privateRoot,
            ["AIOffice:GroupIntake:Connector:SigningSecretRef"] = "secretref://env/OWNED_NOT_RESOLVED_SIGNING",
            ["AIOffice:GroupIntake:Connector:SpoolSecretRef"] = "secretref://env/OWNED_NOT_RESOLVED_SPOOL",
            ["AIOffice:GroupIntake:Connector:SpoolKeyId"] = "owned-inert-v1",
            ["AIOffice:GroupIntake:Connector:Provider"] = "owned-inert",
            ["AIOffice:GroupIntake:Connector:ExternalAccountId"] = "owned-inert-account",
            ["AIOffice:GroupIntake:Connector:Sources:0:SourceBindingId"] = config.SourceId.ToString("D"),
            ["AIOffice:GroupIntake:Connector:Sources:0:ExternalGroupId"] = "owned-inert-group"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection(); services.AddLogging();
        var inertSecrets = new InertStartupProofSecrets();
        services.AddSingleton(new CompositeSecretResolver([inertSecrets]));
        services.AddDbContext<PlatformDbContext>(options => options.UseSqlServer(connection));
        if (services.AddGroupIngressReferenceConsumer(configuration, true) || !services.AddGroupConnectorRecovery(configuration, "Production", true))
            throw new InvalidOperationException();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var serviceScope = provider.CreateScope();
        await serviceScope.ServiceProvider.GetRequiredService<GroupIngressPermissionVerifier>().RequireSafeRuntimeAsync(lifetime.Token);
        _ = provider.GetRequiredService<GroupConnectorRecoveryRuntime>();
        if (provider.GetServices<IHostedService>().Single() is not GroupConnectorRecoveryHostedService || Directory.Exists(privateRoot) || inertSecrets.Calls != 0)
            throw new InvalidOperationException();
        Console.WriteLine("PASS owned reference runtime recovery-only startup SQL permission and inert DI");
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
    if (args[0] == "inspect-pending")
    {
        if (!(await inbox.ReceiveAsync(reference, lifetime.Token)).WasAlreadyReceived || database.ChangeTracker.HasChanges())
            throw new InvalidOperationException();
        using var inspectionDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        inspectionDeadline.CancelAfter(TimeSpan.FromSeconds(10));
        var pending = await channel.BasicGetAsync(queue, false, inspectionDeadline.Token) ?? throw new InvalidOperationException();
        Exception? inspectionFailure = null;
        try
        {
            if (pending.BasicProperties.DeliveryMode != DeliveryModes.Persistent ||
                !pending.Body.Span.SequenceEqual(reference.ToBytes()) ||
                GroupIngressDispatchReference.Parse(pending.Body.Span, pending.BasicProperties.Type,
                    pending.BasicProperties.MessageId, pending.BasicProperties.ContentType) != reference)
                throw new InvalidOperationException();
        }
        catch (Exception error) { inspectionFailure = error; }
        // Preserve the original queued delivery even when exact validation fails.
        try { await channel.BasicNackAsync(pending.DeliveryTag, false, true, inspectionDeadline.Token).AsTask().WaitAsync(inspectionDeadline.Token); }
        catch (Exception error) { inspectionFailure ??= error; }
        if (inspectionFailure is not null) throw new InvalidOperationException();
        Console.WriteLine("PASS owned reference runtime exact persistent original queued reference retained");
        return 0;
    }
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

internal sealed class InertStartupProofSecrets : ISecretResolver
{
    internal int Calls { get; private set; }
    public string Provider => EnvironmentVariableSecretResolver.ProviderName;
    public ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken = default)
    {
        Calls++;
        throw new InvalidOperationException();
    }
}
