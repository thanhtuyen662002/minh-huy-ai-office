extern alias RuntimeWorker;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

        public ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(value);
        }
    }

    private sealed class RecordingProbe : IDataSourceConnectionProbe
    {
        public List<string> Connections { get; } = [];

        public ValueTask ProbeAsync(string connectionString, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Connections.Add(connectionString);
            return ValueTask.CompletedTask;
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
