using MinhHuy.AIOffice.Shared.Contracts.Erp;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class DataSourceFailoverExecutionAuthorizationTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Company = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Source = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-24T04:00:00Z");

    [Fact]
    public void Validate_AcceptsExactAuthorizedEvidenceAndReplay()
    {
        var (decision, evidence) = Authorized();
        DataSourceFailoverExecutionAuthorization.Validate(Authority(), DataSourceOperationKind.Read, decision, evidence);
        DataSourceFailoverExecutionAuthorization.Validate(Authority(), DataSourceOperationKind.Read, decision, evidence);
    }

    [Fact]
    public void ValidateAtOperationTime_AcceptsFreshAuthorizedEvidence()
    {
        var (decision, evidence) = Authorized();
        DataSourceFailoverExecutionAuthorization.ValidateAtOperationTime(
            Authority(), DataSourceOperationKind.Read, decision, evidence, Now.AddMinutes(4));
    }

    [Fact]
    public void ValidateAtOperationTime_RejectsStaleEvidenceAndOperationBeforeRevalidation()
    {
        var (decision, evidence) = Authorized();
        Assert.Throws<UnauthorizedAccessException>(() => DataSourceFailoverExecutionAuthorization.ValidateAtOperationTime(
            Authority(), DataSourceOperationKind.Read, decision, evidence, Now.AddMinutes(5)));
        Assert.Throws<InvalidOperationException>(() => DataSourceFailoverExecutionAuthorization.ValidateAtOperationTime(
            Authority(), DataSourceOperationKind.Read, decision, evidence, Now.AddTicks(-1)));
    }

    [Fact]
    public void ValidateAtOperationTime_PreservesAuthorityOperationAndIdentityFences()
    {
        var (decision, evidence) = Authorized();
        Assert.Throws<UnauthorizedAccessException>(() => DataSourceFailoverExecutionAuthorization.ValidateAtOperationTime(
            Authority() with { CompanyId = Guid.NewGuid() }, DataSourceOperationKind.Read, decision, evidence, Now.AddMinutes(1)));
        Assert.Throws<InvalidOperationException>(() => DataSourceFailoverExecutionAuthorization.ValidateAtOperationTime(
            Authority() with { CatalogVersion = "catalog-13" }, DataSourceOperationKind.Read, decision, evidence, Now.AddMinutes(1)));
        Assert.Throws<UnauthorizedAccessException>(() => DataSourceFailoverExecutionAuthorization.ValidateAtOperationTime(
            Authority(), DataSourceOperationKind.Write, decision, evidence, Now.AddMinutes(1)));
        Assert.Throws<InvalidOperationException>(() => DataSourceFailoverExecutionAuthorization.ValidateAtOperationTime(
            Authority(), DataSourceOperationKind.Read, decision, evidence with { ExecutionEvidenceId = "execution-sha256:tampered" }, Now.AddMinutes(1)));
    }

    [Fact]
    public void Validate_RejectsDeniedEvidence()
    {
        var health = Health();
        var decision = Decision(health);
        var denied = DataSourceFailoverContract.CreateExecutionEvidence(Authority(TimeSpan.FromSeconds(5)), DataSourceOperationKind.Read, decision, health, Now);
        Assert.Equal(DataSourceFailoverExecutionOutcome.Denied, denied.Outcome);
        Assert.Throws<UnauthorizedAccessException>(() => DataSourceFailoverExecutionAuthorization.Validate(Authority(TimeSpan.FromSeconds(5)), DataSourceOperationKind.Read, decision, denied));
    }

    [Fact]
    public void Validate_RejectsCrossAuthorityVersionAndOperation()
    {
        var (decision, evidence) = Authorized();
        Assert.Throws<UnauthorizedAccessException>(() => DataSourceFailoverExecutionAuthorization.Validate(Authority() with { CompanyId = Guid.NewGuid() }, DataSourceOperationKind.Read, decision, evidence));
        Assert.Throws<InvalidOperationException>(() => DataSourceFailoverExecutionAuthorization.Validate(Authority() with { CatalogVersion = "catalog-13" }, DataSourceOperationKind.Read, decision, evidence));
        Assert.Throws<UnauthorizedAccessException>(() => DataSourceFailoverExecutionAuthorization.Validate(Authority(), DataSourceOperationKind.Write, decision, evidence));
    }

    [Fact]
    public void Validate_RejectsDecisionAndEvidenceSubstitution()
    {
        var (decision, evidence) = Authorized();
        Assert.Throws<InvalidOperationException>(() => DataSourceFailoverExecutionAuthorization.Validate(Authority(), DataSourceOperationKind.Read, decision with { EndpointId = "fallback" }, evidence));
        Assert.Throws<InvalidOperationException>(() => DataSourceFailoverExecutionAuthorization.Validate(Authority(), DataSourceOperationKind.Read, decision, evidence with { HealthEvidenceReference = "health-ref:substituted" }));
        Assert.Throws<InvalidOperationException>(() => DataSourceFailoverExecutionAuthorization.Validate(Authority(), DataSourceOperationKind.Read, decision, evidence with { DecisionIdentity = "decision-sha256:tampered" }));
        Assert.Throws<InvalidOperationException>(() => DataSourceFailoverExecutionAuthorization.Validate(Authority(), DataSourceOperationKind.Read, decision, evidence with { ExecutionEvidenceId = "execution-sha256:tampered" }));
    }

    [Fact]
    public void Validate_RejectsSecretMaterialAndInvalidAuthorizedReason()
    {
        var (decision, evidence) = Authorized();
        Assert.Throws<ArgumentException>(() => DataSourceFailoverExecutionAuthorization.Validate(Authority(), DataSourceOperationKind.Read, decision, evidence with { HealthEvidenceReference = "Server=db;Password=secret" }));
        Assert.Throws<InvalidOperationException>(() => DataSourceFailoverExecutionAuthorization.Validate(Authority(), DataSourceOperationKind.Read, decision, evidence with { Reason = "health-evidence-unhealthy" }));
    }

    private static (DataSourceFailoverDecision Decision, DataSourceFailoverExecutionEvidence Evidence) Authorized()
    {
        var health = Health();
        var decision = Decision(health);
        var evidence = DataSourceFailoverContract.CreateExecutionEvidence(Authority(), DataSourceOperationKind.Read, decision, health, Now);
        return (decision, evidence);
    }

    private static DataSourceFailoverDecision Decision(DataSourceHealthEvidence health)
        => DataSourceFailoverContract.Select(Authority(), DataSourceOperationKind.Read,
            new[] { new DataSourceFailoverCandidate(Tenant, Company, Source, "primary", "secret-ref:primary", DataSourceFailoverRole.Primary, DataSourceAccessMode.ReadWrite, 0, "registry-7", "schema-43", "catalog-12") },
            new[] { health }, Now.AddSeconds(-10));

    private static DataSourceHealthEvidence Health()
        => new(Tenant, Company, Source, "primary", "registry-7", "schema-43", "catalog-12", DataSourceHealthState.Healthy, Now.AddSeconds(-30), Now.AddMinutes(1), "health-ref:primary:v9");

    private static DataSourceFailoverAuthority Authority(TimeSpan? maxAge = null)
        => new(Tenant, Company, Source, "registry-7", "schema-43", "catalog-12", maxAge ?? TimeSpan.FromMinutes(5));
}
