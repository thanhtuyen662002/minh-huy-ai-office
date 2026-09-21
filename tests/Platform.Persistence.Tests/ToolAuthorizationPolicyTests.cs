using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class ToolAuthorizationPolicyTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _companyId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _taskId = Guid.NewGuid();
    private readonly ToolAuthorizationPolicy _policy = new();

    [Fact]
    public void Exact_server_authority_and_scope_allows_within_risk_ceiling()
    {
        var request = Request(ToolRiskLevel.Medium);
        var permissions = new[] { Permission(ToolRiskLevel.High) };

        var decision = _policy.Authorize(request, permissions);

        Assert.True(decision.Allowed);
        Assert.Equal("allowed", decision.Reason);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("user")]
    [InlineData("task")]
    public void Missing_authority_fails_closed(string missing)
    {
        var request = Request(ToolRiskLevel.Low) with
        {
            TenantId = missing == "tenant" ? Guid.Empty : _tenantId,
            CompanyId = missing == "company" ? Guid.Empty : _companyId,
            UserId = missing == "user" ? Guid.Empty : _userId,
            TaskId = missing == "task" ? Guid.Empty : _taskId,
        };

        var decision = _policy.Authorize(request, [Permission(ToolRiskLevel.Critical)]);

        Assert.False(decision.Allowed);
        Assert.Equal("missing_authority", decision.Reason);
    }

    [Fact]
    public void Cross_company_permission_is_denied()
    {
        var permission = Permission(ToolRiskLevel.Critical) with { CompanyId = Guid.NewGuid() };

        var decision = _policy.Authorize(Request(ToolRiskLevel.Low), [permission]);

        Assert.False(decision.Allowed);
        Assert.Equal("no_permission", decision.Reason);
    }

    [Fact]
    public void Duplicate_matching_permissions_fail_closed_as_ambiguous()
    {
        var permission = Permission(ToolRiskLevel.High);

        var decision = _policy.Authorize(Request(ToolRiskLevel.Low), [permission, permission]);

        Assert.False(decision.Allowed);
        Assert.Equal("ambiguous_permission", decision.Reason);
    }

    [Fact]
    public void Risk_above_permission_ceiling_is_denied()
    {
        var decision = _policy.Authorize(Request(ToolRiskLevel.Critical), [Permission(ToolRiskLevel.High)]);

        Assert.False(decision.Allowed);
        Assert.Equal("risk_exceeds_permission", decision.Reason);
    }

    [Fact]
    public void Scope_matching_is_exact_and_case_sensitive()
    {
        var request = Request(ToolRiskLevel.Low) with { Resource = "Ledger", Action = "POST" };

        var decision = _policy.Authorize(request, [Permission(ToolRiskLevel.High)]);

        Assert.False(decision.Allowed);
        Assert.Equal("no_permission", decision.Reason);
    }

    private ToolAuthorizationRequest Request(ToolRiskLevel risk) =>
        new(_tenantId, _companyId, _userId, _taskId, "ledger", "post", risk);

    private ToolPermission Permission(ToolRiskLevel maximumRisk) =>
        new(_tenantId, _companyId, _userId, "ledger", "post", maximumRisk);
}
