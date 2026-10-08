extern alias RuntimeWorker;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;
using MinhHuyAiOffice.Shared.Contracts;
using Platform.Persistence;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class PilotDataSourceWorkExecutionTests
{
    [Theory]
    [InlineData(false, "reserved-exact")]
    [InlineData(true, "reserved-exact")]
    [InlineData(false, "reserved-alias")]
    [InlineData(true, "reserved-alias")]
    [InlineData(false, "reserved-encoded-alias")]
    [InlineData(true, "reserved-encoded-alias")]
    [InlineData(false, "custom-exact")]
    [InlineData(true, "custom-exact")]
    [InlineData(false, "custom-alias")]
    [InlineData(true, "custom-alias")]
    [InlineData(false, "customer")]
    [InlineData(true, "customer")]
    [InlineData(false, "customer-grant-case")]
    [InlineData(true, "customer-grant-case")]
    public async Task Worker_DI_branches_exclude_protected_aliases_and_keep_exact_customer_grants(bool ai, string scenario)
    {
        var databaseName = $"worker-di-secret-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        if (ai)
            services.AddDurableRabbitMqWorkExecution<PilotAiQuestionExecutor,
                PilotAiQuestionToolMetadataProvider, PilotAiQuestionToolPermissionProvider>(
                options => options.UseInMemoryDatabase(databaseName), _ => { });
        else
            services.AddDurableRabbitMqWorkExecution<PilotDataSourceProbeExecutor,
                PilotDataSourceToolMetadataProvider, PilotDataSourceToolPermissionProvider>(
                options => options.UseInMemoryDatabase(databaseName), _ => { });
        if (scenario.StartsWith("custom", StringComparison.Ordinal))
            services.AddScoped<DataSourceSecretBindingService>(provider => new(
                provider.GetRequiredService<PlatformDbContext>(), provider.GetRequiredService<IAuthorizationDirectory>(),
                provider.GetRequiredService<BindingStorePermissionVerifier>(), "secretref://env/CUSTOM_PLATFORM_KEY"));
        var resolver = new SyntheticEnvironmentResolver();
        var probe = new RecordingProbe();
        var evidence = new FixedEvidenceReader();
        var model = new RecordingAiAdapter();
        services.AddSingleton(new CompositeSecretResolver([resolver]));
        services.AddSingleton<IDataSourceConnectionProbe>(probe);
        services.AddSingleton<IPilotErpEvidenceReader>(evidence);
        services.AddSingleton<IAiGateway>(new ProviderNeutralAiGateway([model]));
        services.AddSingleton(new PilotAiRuntimeDescriptor("provider-test", "model-test"));
        // Resolving the execution scope never starts a SQL connection or broker hosted service.
        await using var root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = root.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.False(db.Database.IsRelational());
        var seeded = Seed(db);
        var reference = scenario switch
        {
            "reserved-exact" => "secretref://env/AIOFFICE_DB_CONNECTION",
            "reserved-alias" => "secretref://env/aioffice_db_connection",
            "reserved-encoded-alias" => "secretref://ENV/%61ioffice_db_connection",
            "custom-exact" => "secretref://env/CUSTOM_PLATFORM_KEY",
            "custom-alias" => "secretref://env/custom_platform_key",
            _ => "secretref://env/CUSTOMER_KEY"
        };
        seeded.Source.ConnectionSecretReference = reference;
        (await db.DataSourceSecretBindings.SingleAsync()).CanonicalReference = scenario == "customer-grant-case"
            ? reference.ToLowerInvariant() : SecretReference.Parse(reference).Value;
        await db.SaveChangesAsync();
        var denied = scenario is "reserved-exact" or "custom-exact" or "customer-grant-case"
            || (OperatingSystem.IsWindows() && scenario is "reserved-alias" or "reserved-encoded-alias" or "custom-alias");
        var request = new ToolAuthorizationRequest(seeded.Authority.TenantId, seeded.Authority.CompanyId,
            seeded.Authority.UserId, seeded.TaskId, $"erp-data-source:{seeded.DataSourceId:N}",
            ai ? "ai-reasoning-readonly" : "connection-test", ToolRiskLevel.Low);
        var permissions = await scope.ServiceProvider.GetRequiredService<IToolPermissionProvider>()
            .GetAsync(request, CancellationToken.None);
        Assert.Equal(denied ? 0 : 1, permissions.Count);
        var result = await scope.ServiceProvider.GetRequiredService<IRawWorkStepExecutor>()
            .ExecuteAsync(seeded.Envelope, new(Guid.NewGuid(), "worker-di-fixture", 1,
                DateTimeOffset.UtcNow.AddMinutes(1)), CancellationToken.None);
        Assert.Equal(denied ? WorkDeliveryOutcome.Failed : WorkDeliveryOutcome.Completed, result.Outcome);
        if (denied) Assert.Equal(WorkFailureClass.Authorization, result.FailureClass);
        Assert.Equal(denied ? 0 : ai ? 2 : 1, resolver.Calls);
        Assert.Equal(denied ? 0 : 1, probe.Connections.Count);
        Assert.Equal(!denied && ai ? 1 : 0, evidence.Connections.Count);
        Assert.Equal(!denied && ai ? 1 : 0, model.CallCount);
    }

    [Theory]
    [InlineData(false, "grant")]
    [InlineData(true, "grant")]
    [InlineData(false, "membership")]
    [InlineData(true, "membership")]
    [InlineData(false, "user")]
    [InlineData(true, "user")]
    [InlineData(false, "company")]
    [InlineData(true, "company")]
    public async Task Revocation_after_permission_check_denies_every_queued_attempt_before_resolver(bool ai, string revoked)
    {
        await using var db = CreateContext();
        var seeded = Seed(db);
        IToolPermissionProvider provider = ai ? new PilotAiQuestionToolPermissionProvider(db) : new PilotDataSourceToolPermissionProvider(db);
        var request = new ToolAuthorizationRequest(seeded.Authority.TenantId, seeded.Authority.CompanyId,
            seeded.Authority.UserId, seeded.TaskId, $"erp-data-source:{seeded.DataSourceId:N}",
            ai ? "ai-reasoning-readonly" : "connection-test", ToolRiskLevel.Low);
        Assert.Single(await provider.GetAsync(request, CancellationToken.None));
        if (revoked == "grant") (await db.DataSourceSecretBindings.SingleAsync()).IsEnabled = false;
        if (revoked == "membership") (await db.CompanyMemberships.SingleAsync()).IsActive = false;
        if (revoked == "user") (await db.Users.SingleAsync()).IsActive = false;
        if (revoked == "company") (await db.Companies.SingleAsync()).IsActive = false;
        await db.SaveChangesAsync();
        var resolver = new FixedSecretResolver("synthetic-only");
        var secrets = new CompositeSecretResolver([resolver]);
        var probe = new RecordingProbe();
        var evidence = new FixedEvidenceReader();
        var model = new RecordingAiAdapter();
        IRawWorkStepExecutor executor = ai ? new PilotAiQuestionExecutor(db, secrets, probe, evidence,
            new ProviderNeutralAiGateway([model]), new("provider-test", "model-test")) : new PilotDataSourceProbeExecutor(db, secrets, probe);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            Assert.Empty(await provider.GetAsync(request, CancellationToken.None));
            var result = await executor.ExecuteAsync(seeded.Envelope,
                new(Guid.NewGuid(), "worker-fixture", attempt, DateTimeOffset.UtcNow.AddMinutes(1)), CancellationToken.None);
            Assert.Equal(WorkFailureClass.Authorization, result.FailureClass);
            Assert.Equal(WorkDeliveryOutcome.Failed, result.Outcome);
        }
        Assert.Equal(0, resolver.Calls);
        Assert.Empty(probe.Connections);
        Assert.Empty(evidence.Connections);
        Assert.Empty(model.Requests);
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("enabled")]
    [InlineData("writable")]
    [InlineData("membership")]
    [InlineData("version")]
    [InlineData("recreate")]
    public async Task Change_after_AI_probe_denies_evidence_before_second_resolution(string change)
    {
        await using var db = CreateContext();
        var seeded = Seed(db);
        var resolver = new FixedSecretResolver("synthetic-only");
        var probe = new RecordingProbe(async () =>
        {
            if (change == "grant") (await db.DataSourceSecretBindings.SingleAsync()).IsEnabled = false;
            if (change == "enabled") seeded.Source.IsEnabled = false;
            if (change == "writable") seeded.Source.AllowWrite = true;
            if (change == "membership") (await db.CompanyMemberships.SingleAsync()).IsActive = false;
            if (change == "version") (await db.DataSourceSecretBindings.SingleAsync()).Version++;
            if (change == "recreate")
            {
                db.Remove(await db.DataSourceSecretBindings.SingleAsync());
                await db.SaveChangesAsync();
                BindingFixture.Grant(db, seeded.Authority, "secretref://test/PILOT_CONNECTION");
            }
            await db.SaveChangesAsync();
        });
        var evidence = new FixedEvidenceReader();
        var model = new RecordingAiAdapter();
        var executor = new PilotAiQuestionExecutor(db, new CompositeSecretResolver([resolver]), probe,
            evidence, new ProviderNeutralAiGateway([model]), new("provider-test", "model-test"));
        var result = await executor.ExecuteAsync(seeded.Envelope,
            new(Guid.NewGuid(), "worker-fixture", 1, DateTimeOffset.UtcNow.AddMinutes(1)), CancellationToken.None);
        Assert.Equal(WorkFailureClass.Authorization, result.FailureClass);
        Assert.Equal(1, resolver.Calls);
        Assert.Single(probe.Connections);
        Assert.Empty(evidence.Connections);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task Permission_request_cannot_replace_durable_task_owner()
    {
        await using var db = CreateContext();
        var seeded = Seed(db);
        var task = await db.Tasks.SingleAsync();
        task.CreatedByUserId = Guid.NewGuid();
        await db.SaveChangesAsync();
        foreach (var ai in new[] { false, true })
        {
            IToolPermissionProvider provider = ai ? new PilotAiQuestionToolPermissionProvider(db) : new PilotDataSourceToolPermissionProvider(db);
            var request = new ToolAuthorizationRequest(seeded.Authority.TenantId, seeded.Authority.CompanyId,
                seeded.Authority.UserId, seeded.TaskId, $"erp-data-source:{seeded.DataSourceId:N}",
                ai ? "ai-reasoning-readonly" : "connection-test", ToolRiskLevel.Low);
            Assert.Empty(await provider.GetAsync(request, CancellationToken.None));
        }
    }
    [Fact]
    public async Task Authorized_probe_is_audited_before_read_only_data_source_execution()
    {
        await using var db = CreateContext();
        var seeded = Seed(db);
        var probe = new RecordingProbe();
        var raw = new PilotDataSourceProbeExecutor(
            db,
            new CompositeSecretResolver(new ISecretResolver[] { new FixedSecretResolver("test-connection") }),
            probe);
        var auditSink = new RecordingAuditSink();
        var executor = new AuthorizedWorkStepExecutor(
            raw,
            new PilotDataSourceToolMetadataProvider(db),
            new PilotDataSourceToolPermissionProvider(db),
            new TrustedToolAuthorizationRequestFactory(db),
            new AuthorizedToolExecutionGate(
                new ToolAuthorizationPolicy(),
                new ToolExecutionAuditService(auditSink)));

        var result = await executor.ExecuteAsync(
            seeded.Envelope,
            new WorkLeaseSnapshot(Guid.NewGuid(), "worker-1", 1, DateTimeOffset.UtcNow.AddMinutes(1)),
            CancellationToken.None);

        Assert.Equal(WorkDeliveryOutcome.Completed, result.Outcome);
        Assert.NotNull(result.CheckpointPayloadJson);
        Assert.Contains("read-only-connection-probe", result.CheckpointPayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("test-connection", result.CheckpointPayloadJson, StringComparison.Ordinal);
        Assert.Single(probe.Connections);
        Assert.Equal("test-connection", probe.Connections[0]);
        var audit = Assert.Single(auditSink.Entries);
        Assert.True(audit.Authorized);
        Assert.Equal($"erp-data-source:{seeded.DataSourceId:N}", audit.Resource);
        Assert.Equal("connection-test", audit.Action);
    }

    [Fact]
    public async Task Disabled_or_foreign_data_source_permission_fails_closed()
    {
        await using var db = CreateContext();
        var seeded = Seed(db);
        var provider = new PilotDataSourceToolPermissionProvider(db);
        seeded.Source.IsEnabled = false;
        await db.SaveChangesAsync();

        var request = new ToolAuthorizationRequest(
            seeded.Authority.TenantId,
            seeded.Authority.CompanyId,
            seeded.Authority.UserId,
            seeded.TaskId,
            $"erp-data-source:{seeded.DataSourceId:N}",
            "connection-test",
            ToolRiskLevel.Low);

        Assert.Empty(await provider.GetAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task Ai_question_executor_calls_model_after_probe_and_persists_redacted_answer()
    {
        await using var db = CreateContext();
        var seeded = Seed(db);
        var probe = new RecordingProbe();
        var adapter = new RecordingAiAdapter();
        var evidenceReader = new FixedEvidenceReader();
        var raw = new PilotAiQuestionExecutor(
            db,
            new CompositeSecretResolver(new ISecretResolver[] { new FixedSecretResolver("test-connection") }),
            probe,
            evidenceReader,
            new ProviderNeutralAiGateway(new IAiProviderAdapter[] { adapter }),
            new PilotAiRuntimeDescriptor("provider-test", "model-test"));
        var auditSink = new RecordingAuditSink();
        var executor = new AuthorizedWorkStepExecutor(
            raw,
            new PilotAiQuestionToolMetadataProvider(db),
            new PilotAiQuestionToolPermissionProvider(db),
            new TrustedToolAuthorizationRequestFactory(db),
            new AuthorizedToolExecutionGate(
                new ToolAuthorizationPolicy(),
                new ToolExecutionAuditService(auditSink)));

        var result = await executor.ExecuteAsync(
            seeded.Envelope,
            new WorkLeaseSnapshot(Guid.NewGuid(), "worker-1", 1, DateTimeOffset.UtcNow.AddMinutes(1)),
            CancellationToken.None);

        Assert.Equal(WorkDeliveryOutcome.Completed, result.Outcome);
        Assert.Single(probe.Connections);
        Assert.Equal("test-connection", probe.Connections[0]);
        Assert.Single(evidenceReader.Connections);
        Assert.Equal("test-connection", evidenceReader.Connections[0]);
        Assert.Single(adapter.Requests);
        Assert.Contains("read current balance", adapter.Requests[0].Input, StringComparison.Ordinal);
        Assert.Contains("pilot-erp", adapter.Requests[0].Input, StringComparison.Ordinal);
        Assert.Contains("dbo.Customer", adapter.Requests[0].Input, StringComparison.Ordinal);
        Assert.Contains("approximateRows=42", adapter.Requests[0].Input, StringComparison.Ordinal);
        Assert.Contains("business row contents", adapter.Requests[0].Input, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("test-connection", adapter.Requests[0].Input, StringComparison.Ordinal);

        using var checkpoint = JsonDocument.Parse(Assert.IsType<string>(result.CheckpointPayloadJson));
        var root = checkpoint.RootElement;
        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.Equal("provider-test-model", root.GetProperty("model").GetString());
        Assert.Equal("provider-test", root.GetProperty("provider").GetString());
        Assert.Equal("model processed question", root.GetProperty("answer").GetString());
        Assert.Equal(13, root.GetProperty("usage").GetProperty("inputTokens").GetInt64());
        Assert.Equal(5, root.GetProperty("usage").GetProperty("outputTokens").GetInt64());
        Assert.Equal(18, root.GetProperty("usage").GetProperty("totalTokens").GetInt64());
        var erpEvidence = root.GetProperty("erpEvidence");
        Assert.Equal("pilot-erp", erpEvidence.GetProperty("databaseName").GetString());
        Assert.Equal(2, erpEvidence.GetProperty("tableCount").GetInt64());
        Assert.Equal(1, erpEvidence.GetProperty("sampledTableCount").GetInt32());
        Assert.Equal("dbo", erpEvidence.GetProperty("topTables")[0].GetProperty("schema").GetString());
        Assert.Equal("Customer", erpEvidence.GetProperty("topTables")[0].GetProperty("table").GetString());
        Assert.Equal(
            "ai-provider-reasoning-after-bounded-read-only-erp-catalog",
            root.GetProperty("evidence").GetString());
        Assert.DoesNotContain("test-connection", result.CheckpointPayloadJson, StringComparison.Ordinal);

        var audit = Assert.Single(auditSink.Entries);
        Assert.True(audit.Authorized);
        Assert.Equal($"erp-data-source:{seeded.DataSourceId:N}", audit.Resource);
        Assert.Equal("ai-reasoning-readonly", audit.Action);
    }

    [Fact]
    public async Task Ai_question_executor_checkpoint_records_backup_provider_after_failover()
    {
        await using var db = CreateContext();
        var seeded = Seed(db);
        var probe = new RecordingProbe();
        var primary = new RecordingAiAdapter(
            "direct",
            failure: new AiProviderExecutionException("temporary", true));
        var backup = new RecordingAiAdapter(
            "backup",
            output: "backup processed question");
        var raw = new PilotAiQuestionExecutor(
            db,
            new CompositeSecretResolver(new ISecretResolver[] { new FixedSecretResolver("test-connection") }),
            probe,
            new FixedEvidenceReader(),
            new OrderedFailoverAiGateway(primary, backup),
            new PilotAiRuntimeDescriptor("direct", "direct-model"));

        var result = await raw.ExecuteAsync(
            seeded.Envelope,
            new WorkLeaseSnapshot(Guid.NewGuid(), "worker-1", 1, DateTimeOffset.UtcNow.AddMinutes(1)),
            CancellationToken.None);

        Assert.Equal(WorkDeliveryOutcome.Completed, result.Outcome);
        using var checkpoint = JsonDocument.Parse(Assert.IsType<string>(result.CheckpointPayloadJson));
        Assert.Equal("backup", checkpoint.RootElement.GetProperty("provider").GetString());
        Assert.Equal("backup-model", checkpoint.RootElement.GetProperty("model").GetString());
        Assert.Equal("backup processed question", checkpoint.RootElement.GetProperty("answer").GetString());
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(1, backup.CallCount);
    }

    [Fact]
    public void Erp_evidence_rejects_more_than_the_bounded_table_limit()
    {
        var tables = Enumerable.Range(0, PilotErpEvidence.MaximumTables + 1)
            .Select(index => new PilotErpTableEvidence("dbo", $"Table{index}", index))
            .ToArray();
        var evidence = new PilotErpEvidence("pilot-erp", tables.Length, tables);

        Assert.Throws<InvalidOperationException>(evidence.Validate);
    }

    [Fact]
    public async Task Ai_question_permission_requires_non_writable_source()
    {
        await using var db = CreateContext();
        var seeded = Seed(db);
        seeded.Source.AllowWrite = true;
        await db.SaveChangesAsync();
        var provider = new PilotAiQuestionToolPermissionProvider(db);
        var request = new ToolAuthorizationRequest(
            seeded.Authority.TenantId,
            seeded.Authority.CompanyId,
            seeded.Authority.UserId,
            seeded.TaskId,
            $"erp-data-source:{seeded.DataSourceId:N}",
            "ai-reasoning-readonly",
            ToolRiskLevel.Low);

        Assert.Empty(await provider.GetAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task Probe_permission_rejects_unexpected_action()
    {
        await using var db = CreateContext();
        var seeded = Seed(db);
        var provider = new PilotDataSourceToolPermissionProvider(db);
        var request = new ToolAuthorizationRequest(
            seeded.Authority.TenantId,
            seeded.Authority.CompanyId,
            seeded.Authority.UserId,
            seeded.TaskId,
            $"erp-data-source:{seeded.DataSourceId:N}",
            "unexpected-action",
            ToolRiskLevel.Low);

        Assert.Empty(await provider.GetAsync(request, CancellationToken.None));
    }

    private static PlatformDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase($"pilot-worker-{Guid.NewGuid():N}")
            .Options;
        return new PlatformDbContext(options);
    }

    private static SeededWork Seed(PlatformDbContext db)
    {
        var authority = AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var taskId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var dataSourceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var source = new DataSourceRecord
        {
            TenantId = authority.TenantId,
            CompanyId = authority.CompanyId,
            Id = dataSourceId,
            LogicalName = "pilot-source",
            Kind = "sqlserver",
            Environment = "test",
            Purpose = "pilot",
            ConnectionSecretReference = "secretref://test/PILOT_CONNECTION",
            AllowRead = true,
            AllowWrite = false,
            MaxConcurrency = 1,
            IsEnabled = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var task = new TaskRecord
        {
            TenantId = authority.TenantId,
            CompanyId = authority.CompanyId,
            Id = taskId,
            CreatedByUserId = authority.UserId,
            Status = TaskExecutionStatus.Running,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var step = new TaskStepRecord
        {
            TenantId = authority.TenantId,
            CompanyId = authority.CompanyId,
            TaskId = taskId,
            Id = stepId,
            StepKey = PilotTaskIdentity.StepKey,
            Status = TaskStepStatus.Running,
            Attempt = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var execution = WorkerExecutionStateMachine.Initialize(
            authority.TenantId,
            authority.CompanyId,
            taskId,
            stepId,
            now);
        execution.Attempt = 1;
        var envelope = WorkDispatchEnvelope.Create(
            Guid.NewGuid(),
            authority.TenantId,
            authority.CompanyId,
            taskId,
            stepId,
            1,
            null,
            now);
        var requestEvent = new PilotTaskRequestEvent(
            "customer-idempotency-1",
            dataSourceId,
            "read current balance",
            3);
        db.AddRange(
            source,
            task,
            step,
            execution,
            new TaskEventRecord
            {
                TenantId = authority.TenantId,
                CompanyId = authority.CompanyId,
                TaskId = taskId,
                StepId = stepId,
                Sequence = 1,
                EventType = PilotTaskRequestEvent.EventType,
                PayloadJson = JsonSerializer.Serialize(requestEvent, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                OccurredAtUtc = now
            });
        db.SaveChanges();
        db.Add(new PlatformUserRecord
        {
            TenantId = authority.TenantId,
            Id = authority.UserId,
            IdentityProvider = "test",
            Subject = "worker-fixture",
            DisplayName = "Worker fixture",
            IsActive = true
        });
        db.Add(new CompanyRecord
        {
            TenantId = authority.TenantId,
            Id = authority.CompanyId,
            Code = "WORKER",
            Name = "Worker fixture",
            IsActive = true
        });
        db.Add(new CompanyMembershipRecord
        {
            TenantId = authority.TenantId,
            CompanyId = authority.CompanyId,
            UserId = authority.UserId,
            IsActive = true
        });
        BindingFixture.Grant(db, authority, "secretref://test/PILOT_CONNECTION");
        db.SaveChanges();
        return new(authority, taskId, dataSourceId, source, envelope);
    }

    private sealed record SeededWork(
        AuthorizationContext Authority,
        Guid TaskId,
        Guid DataSourceId,
        DataSourceRecord Source,
        WorkDispatchEnvelope Envelope);

    private sealed class FixedSecretResolver(string value) : ISecretResolver
    {
        public string Provider => "test";
        public int Calls { get; private set; }

        public ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(value);
        }
    }

    private sealed class SyntheticEnvironmentResolver : ISecretResolver
    {
        public string Provider => "env";
        public int Calls { get; private set; }
        public ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult("synthetic-only-value");
        }
    }

    private sealed class RecordingProbe(Func<Task>? after = null) : IDataSourceConnectionProbe
    {
        public List<string> Connections { get; } = [];
        public ValueTask ProbeAsync(string connectionString, bool requireReadOnly, CancellationToken cancellationToken = default)
        {
            Assert.True(requireReadOnly);
            return ProbeAsync(connectionString, cancellationToken);
        }

        public async ValueTask ProbeAsync(string connectionString, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Connections.Add(connectionString);
            if (after is not null) await after();
        }
    }

    private sealed class FixedEvidenceReader : IPilotErpEvidenceReader
    {
        public List<string> Connections { get; } = [];

        public ValueTask<PilotErpEvidence> ReadAsync(
            string connectionString,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Connections.Add(connectionString);
            return ValueTask.FromResult(new PilotErpEvidence(
                "pilot-erp",
                2,
                [new PilotErpTableEvidence("dbo", "Customer", 42)]));
        }
    }

    private sealed class RecordingAiAdapter(
        string providerId = "provider-test",
        string output = "model processed question",
        AiProviderExecutionException? failure = null) : IAiProviderAdapter
    {
        public string ProviderId => providerId;

        public List<AiGatewayRequest> Requests { get; } = [];

        public int CallCount => Requests.Count;

        public bool Supports(AiCapability capability) => capability == AiCapability.Reasoning;

        public Task<AiGatewayResponse> ExecuteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (failure is not null)
            {
                throw failure;
            }

            return Task.FromResult(
                new AiGatewayResponse(
                    request.RequestId,
                    request.Capability,
                    output,
                    $"{providerId}-model",
                    13,
                    5)
                {
                    ProviderId = providerId
                });
        }
    }

    private sealed class RecordingAuditSink : IToolExecutionAuditSink
    {
        public List<ToolExecutionAuditEntry> Entries { get; } = [];

        public Task AppendAsync(ToolExecutionAuditEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }
}
