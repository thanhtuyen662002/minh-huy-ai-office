using Platform.Persistence;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class ToolExecutionAuditServiceTests
{
    [Fact]
    public async Task Records_server_authority_scope_decision_and_execution_correlation()
    {
        var sink = new RecordingSink();
        var service = new ToolExecutionAuditService(sink);
        var request = new ToolAuthorizationRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "ledger", "post", ToolRiskLevel.High);
        var decision = ToolAuthorizationDecision.Deny("risk_exceeds_permission");
        var occurredAt = DateTimeOffset.Parse("2026-09-21T03:30:00Z");

        await service.RecordAsync(request, decision, occurredAt, "exec-42");

        var entry = Assert.Single(sink.Entries);
        Assert.NotEqual(Guid.Empty, entry.AuditId);
        Assert.Equal(request.TenantId, entry.TenantId);
        Assert.Equal(request.CompanyId, entry.CompanyId);
        Assert.Equal(request.UserId, entry.UserId);
        Assert.Equal(request.TaskId, entry.TaskId);
        Assert.Equal("ledger", entry.Resource);
        Assert.Equal("post", entry.Action);
        Assert.Equal(ToolRiskLevel.High, entry.Risk);
        Assert.False(entry.Authorized);
        Assert.Equal("risk_exceeds_permission", entry.DecisionReason);
        Assert.Equal(occurredAt, entry.OccurredAtUtc);
        Assert.Equal("exec-42", entry.ExecutionId);
    }

    [Fact]
    public async Task Missing_authority_is_rejected_before_audit_append()
    {
        var sink = new RecordingSink();
        var service = new ToolExecutionAuditService(sink);
        var request = new ToolAuthorizationRequest(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "ledger", "post", ToolRiskLevel.Low);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RecordAsync(request, ToolAuthorizationDecision.Deny("missing_authority"), DateTimeOffset.UtcNow));
        Assert.Empty(sink.Entries);
    }

    private sealed class RecordingSink : IToolExecutionAuditSink
    {
        public List<ToolExecutionAuditEntry> Entries { get; } = [];
        public Task AppendAsync(ToolExecutionAuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }
}
