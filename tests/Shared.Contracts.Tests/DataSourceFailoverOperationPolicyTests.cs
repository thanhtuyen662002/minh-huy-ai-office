using MinhHuy.AIOffice.Shared.Contracts.Erp;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class DataSourceFailoverOperationPolicyTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Company = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Source = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-24T04:00:00Z");

    [Fact]
    public void ValidateForExecution_AcceptsMatchingEffectivePolicyAndExactReplay()
    {
        var (decision, evidence) = Authorized();
        var authorization = new DataSourceFailoverOperationAuthorization(evidence, 7);
        var policy = Policy();

        DataSourceFailoverOperationPolicyContract.ValidateForExecution(Authority(), DataSourceOperationKind.Read, decision, policy, authorization, Now.AddMinutes(1));
        DataSourceFailoverOperationPolicyContract.ValidateForExecution(Authority(), DataSourceOperationKind.Read, decision, policy, authorization, Now.AddMinutes(1));
    }

    [Fact]
    public void ValidateForExecution_RejectsPolicyBeforeEffectiveTimeAndInvalidTimestamp()
    {
        var (decision, evidence) = Authorized();
        var authorization = new DataSourceFailoverOperationAuthorization(evidence, 7);

        Assert.Throws<UnauthorizedAccessException>(() => DataSourceFailoverOperationPolicyContract.ValidateForExecution(
            Authority(), DataSourceOperationKind.Read, decision, Policy() with { EffectiveAt = Now.AddMinutes(2) }, authorization, Now.AddMinutes(1)));
        Assert.Throws<ArgumentException>(() => DataSourceFailoverOperationPolicyContract.ValidateForExecution(
            Authority(), DataSourceOperationKind.Read, decision, Policy() with { EffectiveAt = default }, authorization, Now.AddMinutes(1)));
    }

    [Fact]
    public void ValidateForExecution_RejectsCrossAuthorityPolicyScopeEvenWhenVersionMatches()
    {
        var (decision, evidence) = Authorized();
        var authorization = new DataSourceFailoverOperationAuthorization(evidence, 7);

        Assert.Throws<UnauthorizedAccessException>(() => DataSourceFailoverOperationPolicyContract.ValidateForExecution(
            Authority(), DataSourceOperationKind.Read, decision, Policy() with { TenantId = Guid.NewGuid() }, authorization, Now.AddMinutes(1)));
        Assert.Throws<UnauthorizedAccessException>(() => DataSourceFailoverOperationPolicyContract.ValidateForExecution(
            Authority(), DataSourceOperationKind.Read, decision, Policy() with { CompanyId = Guid.NewGuid() }, authorization, Now.AddMinutes(1)));
        Assert.Throws<UnauthorizedAccessException>(() => DataSourceFailoverOperationPolicyContract.ValidateForExecution(
            Authority(), DataSourceOperationKind.Read, decision, Policy() with { DataSourceId = Guid.NewGuid() }, authorization, Now.AddMinutes(1)));
    }

    [Fact]
    public void ValidateForExecution_RejectsCrossOperationPolicyAndUndefinedOperation()
    {
        var (decision, evidence) = Authorized();
        var authorization = new DataSourceFailoverOperationAuthorization(evidence, 7);

        Assert.Throws<UnauthorizedAccessException>(() => DataSourceFailoverOperationPolicyContract.ValidateForExecution(
            Authority(), DataSourceOperationKind.Read, decision, Policy() with { Operation = DataSourceOperationKind.Write }, authorization, Now.AddMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DataSourceFailoverOperationPolicyContract.ValidateForExecution(
            Authority(), DataSourceOperationKind.Read, decision, Policy() with { Operation = (DataSourceOperationKind)999 }, authorization, Now.AddMinutes(1)));
    }

    [Fact]
    public void ValidateForExecution_RejectsInvalidPolicyIdentityAndVersions()
    {
        var (decision, evidence) = Authorized();
        var authorization = new DataSourceFailoverOperationAuthorization(evidence, 7);

        Assert.Throws<ArgumentException>(() => DataSourceFailoverOperationPolicyContract.ValidateForExecution(
            Authority(), DataSourceOperationKind.Read, decision, Policy() with { TenantId = Guid.Empty }, authorization, Now.AddMinutes(1)));
        Assert.Throws<ArgumentException>(() => DataSourceFailoverOperationPolicyContract.ValidateForExecution(
            Authority(), DataSourceOperationKind.Read, decision, Policy() with { CompanyId = Guid.Empty }, authorization, Now.AddMinutes(1)));
        Assert.Throws<ArgumentException>(() => DataSourceFailoverOperationPolicyContract.ValidateForExecution(
            Authority(), DataSourceOperationKind.Read, decision, Policy() with { DataSourceId = Guid.Empty }, authorization, Now.AddMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DataSourceFailoverOperationPolicyContract.ValidateForExecution(
            Authority(), DataSourceOperationKind.Read, decision, Policy() with { Version = 0 }, authorization, Now.AddMinutes(1)));
        Assert.Throws<UnauthorizedAccessException>(() => DataSourceFailoverOperationPolicyContract.ValidateForExecution(
            Authority(), DataSourceOperationKind.Read, decision, Policy() with { Version = 8 }, authorization, Now.AddMinutes(1)));
        Assert.Throws<InvalidOperationException>(() => DataSourceFailoverOperationPolicyContract.ValidateForExecution(
            Authority(), DataSourceOperationKind.Read, decision, Policy(), new DataSourceFailoverOperationAuthorization(evidence, 0), Now.AddMinutes(1)));
    }

    [Fact]
    public void ValidateForExecution_PreservesAuthorityOperationFreshnessAndTamperFences()
    {
        var (decision, evidence) = Authorized();
        var policy = Policy();
        var authorization = new DataSourceFailoverOperationAuthorization(evidence, 7);

        Assert.Throws<UnauthorizedAccessException>(() => DataSourceFailoverOperationPolicyContract.ValidateForExecution(
            Authority() with { CompanyId = Guid.NewGuid() }, DataSourceOperationKind.Read, decision, policy, authorization, Now.AddMinutes(1)));
        Assert.Throws<UnauthorizedAccessException>(() => DataSourceFailoverOperationPolicyContract.ValidateForExecution(
            Authority(), DataSourceOperationKind.Write, decision, policy, authorization, Now.AddMinutes(1)));
        Assert.Throws<UnauthorizedAccessException>(() => DataSourceFailoverOperationPolicyContract.ValidateForExecution(
            Authority(), DataSourceOperationKind.Read, decision, policy, authorization, Now.AddMinutes(5)));
        Assert.Throws<InvalidOperationException>(() => DataSourceFailoverOperationPolicyContract.ValidateForExecution(
            Authority(), DataSourceOperationKind.Read, decision, policy,
            authorization with { Evidence = evidence with { ExecutionEvidenceId = "execution-sha256:tampered" } }, Now.AddMinutes(1)));
    }

    private static (DataSourceFailoverDecision Decision, DataSourceFailoverExecutionEvidence Evidence) Authorized()
    {
        var health = new DataSourceHealthEvidence(Tenant, Company, Source, "primary", "registry-7", "schema-43", "catalog-12", DataSourceHealthState.Healthy, Now.AddSeconds(-30), Now.AddMinutes(1), "health-ref:primary:v9");
        var authority = Authority();
        var decision = DataSourceFailoverContract.Select(authority, DataSourceOperationKind.Read,
            new[] { new DataSourceFailoverCandidate(Tenant, Company, Source, "primary", "secret-ref:primary", DataSourceFailoverRole.Primary, DataSourceAccessMode.ReadWrite, 0, "registry-7", "schema-43", "catalog-12") },
            new[] { health }, Now.AddSeconds(-10));
        return (decision, DataSourceFailoverContract.CreateExecutionEvidence(authority, DataSourceOperationKind.Read, decision, health, Now));
    }

    private static DataSourceFailoverAuthority Authority()
        => new(Tenant, Company, Source, "registry-7", "schema-43", "catalog-12", TimeSpan.FromMinutes(5));

    private static DataSourceFailoverOperationPolicy Policy()
        => new(Tenant, Company, Source, DataSourceOperationKind.Read, 7, Now.AddMinutes(-1));
}
