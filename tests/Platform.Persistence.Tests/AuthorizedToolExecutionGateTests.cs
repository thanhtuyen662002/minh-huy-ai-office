using MinhHuy.AIOffice.Platform.Persistence;
using Platform.Persistence;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class AuthorizedToolExecutionGateTests
{
    [Fact]
    public async Task Allowed_request_is_audited_before_execution()
    {
        var sink = new RecordingSink();
        var gate = new AuthorizedToolExecutionGate(new ToolAuthorizationPolicy(), new ToolExecutionAuditService(sink));
        var request = new ToolAuthorizationRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "ledger", "post", ToolRiskLevel.Medium);
        var permission = new ToolPermission(request.TenantId, request.CompanyId, request.UserId, request.Resource, request.Action, ToolRiskLevel.High);

        var result = await gate.ExecuteAsync(request, [permission], _ =>
        {
            Assert.True(Assert.Single(sink.Entries).Authorized);
            return Task.FromResult("executed");
        });

        Assert.Equal("executed", result);
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
