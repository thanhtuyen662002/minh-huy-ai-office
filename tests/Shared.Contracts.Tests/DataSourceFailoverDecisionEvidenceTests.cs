using MinhHuy.AIOffice.Shared.Contracts.Erp;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class DataSourceFailoverDecisionEvidenceTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Company = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Source = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-23T06:00:00Z");

    [Fact]
    public void Select_PrimaryDecisionCarriesSelectedObservationTimeAndReadOperation()
    {
        var observedAt = Now.AddSeconds(-17);
        var decision = Select(
            DataSourceOperationKind.Read,
            new[] { Candidate("primary", DataSourceFailoverRole.Primary) },
            new[] { Evidence("primary", observedAt, "health-ref:primary:selected") });

        Assert.Equal("primary", decision.EndpointId);
        Assert.Equal(DataSourceOperationKind.Read, decision.Operation);
        Assert.Equal("health-ref:primary:selected", decision.EvidenceReference);
        Assert.Equal(observedAt, decision.ObservedAt);
    }

    [Fact]
    public void Select_FallbackDecisionCarriesNewestSelectedObservationTimeAndWriteOperation()
    {
        var older = Evidence("fallback", Now.AddSeconds(-40), "health-ref:fallback:older");
        var newer = Evidence("fallback", Now.AddSeconds(-5), "health-ref:fallback:newer");
        var decision = Select(
            DataSourceOperationKind.Write,
            new[]
            {
                Candidate("primary", DataSourceFailoverRole.Primary, DataSourceAccessMode.ReadOnly),
                Candidate("fallback", DataSourceFailoverRole.Fallback, DataSourceAccessMode.ReadWrite)
            },
            new[]
            {
                Evidence("primary", Now.AddSeconds(-3), "health-ref:primary:healthy"),
                newer,
                older
            });

        Assert.Equal("fallback", decision.EndpointId);
        Assert.Equal(DataSourceOperationKind.Write, decision.Operation);
        Assert.Equal("health-ref:fallback:newer", decision.EvidenceReference);
        Assert.Equal(newer.ObservedAt, decision.ObservedAt);
        Assert.Equal("primary-unavailable-fallback-selected", decision.Reason);
    }

    private static DataSourceFailoverDecision Select(DataSourceOperationKind operation, DataSourceFailoverCandidate[] candidates, DataSourceHealthEvidence[] evidence)
        => DataSourceFailoverContract.Select(
            new DataSourceFailoverAuthority(Tenant, Company, Source, "registry-7", "schema-43", "catalog-12", TimeSpan.FromMinutes(5)),
            operation,
            candidates,
            evidence,
            Now);

    private static DataSourceFailoverCandidate Candidate(string endpoint, DataSourceFailoverRole role, DataSourceAccessMode accessMode = DataSourceAccessMode.ReadWrite)
        => new(Tenant, Company, Source, endpoint, $"secret-ref:{endpoint}", role, accessMode, 0, "registry-7", "schema-43", "catalog-12");

    private static DataSourceHealthEvidence Evidence(string endpoint, DateTimeOffset observedAt, string reference, DataSourceHealthState state = DataSourceHealthState.Healthy)
        => new(Tenant, Company, Source, endpoint, "registry-7", "schema-43", "catalog-12", state, observedAt, Now.AddMinutes(1), reference);
}
