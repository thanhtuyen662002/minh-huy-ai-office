using MinhHuy.AIOffice.Shared.Contracts.Erp;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class DataSourceFailoverTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Company = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Source = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-23T06:00:00Z");

    [Fact]
    public void Select_PrefersHealthyPrimary_ThenDeterministicFallback()
    {
        var candidates = new[] { Candidate("fallback-b", DataSourceFailoverRole.Fallback, DataSourceAccessMode.ReadWrite, 10), Candidate("primary", DataSourceFailoverRole.Primary, DataSourceAccessMode.ReadWrite, 50), Candidate("fallback-a", DataSourceFailoverRole.Fallback, DataSourceAccessMode.ReadWrite, 10) };
        Assert.Equal("primary", Select(candidates, candidates.Select(x => Evidence(x.EndpointId)).ToArray()).EndpointId);
        var recovered = Select(candidates, new[] { Evidence("primary", DataSourceHealthState.Unhealthy), Evidence("fallback-b"), Evidence("fallback-a") });
        Assert.Equal("fallback-a", recovered.EndpointId);
        Assert.Equal("primary-unavailable-fallback-selected", recovered.Reason);
    }

    [Fact]
    public void Evidence_RejectsCrossAuthorityEndpointAndVersionMismatch()
    {
        var candidate = Candidate("primary", DataSourceFailoverRole.Primary, DataSourceAccessMode.ReadWrite, 0);
        Assert.Throws<UnauthorizedAccessException>(() => Select(new[] { candidate }, new[] { Evidence("primary") with { CompanyId = Guid.NewGuid() } }));
        Assert.Throws<InvalidOperationException>(() => Select(new[] { candidate }, new[] { Evidence("primary") with { RegistryVersion = "registry-6" } }));
        Assert.Throws<InvalidOperationException>(() => Select(new[] { candidate }, new[] { Evidence("other-endpoint") }));
    }

    [Fact]
    public void Evidence_RejectsExpiredFutureAndMalformedWindows()
    {
        var candidate = Candidate("primary", DataSourceFailoverRole.Primary, DataSourceAccessMode.ReadWrite, 0);
        Assert.Throws<InvalidOperationException>(() => Select(new[] { candidate }, new[] { Evidence("primary") with { ExpiresAt = Now } }));
        Assert.Throws<InvalidOperationException>(() => Select(new[] { candidate }, new[] { Evidence("primary") with { ObservedAt = Now.AddSeconds(1) } }));
        Assert.Throws<InvalidOperationException>(() => Select(new[] { candidate }, new[] { Evidence("primary") with { ObservedAt = Now.AddMinutes(-1), ExpiresAt = Now.AddMinutes(-2) } }));
    }

    [Fact]
    public void Evidence_RejectsUndefinedStateAndSecretMaterial()
    {
        var candidate = Candidate("primary", DataSourceFailoverRole.Primary, DataSourceAccessMode.ReadWrite, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => Select(new[] { candidate }, new[] { Evidence("primary") with { State = (DataSourceHealthState)99 } }));
        Assert.Throws<ArgumentException>(() => Select(new[] { candidate }, new[] { Evidence("primary") with { EvidenceReference = "Server=db;Password=secret" } }));
    }

    [Fact]
    public void Evidence_ReplayIsIdempotentAndConflictingReferenceFailsClosed()
    {
        var candidate = Candidate("primary", DataSourceFailoverRole.Primary, DataSourceAccessMode.ReadWrite, 0);
        var evidence = Evidence("primary");
        Assert.Equal("primary", Select(new[] { candidate }, new[] { evidence, evidence }).EndpointId);
        Assert.Throws<InvalidOperationException>(() => Select(new[] { candidate }, new[] { evidence, evidence with { State = DataSourceHealthState.Unhealthy } }));
    }

    [Fact]
    public void Evidence_NewestObservationWinsRegardlessOfInputOrder()
    {
        var candidate = Candidate("primary", DataSourceFailoverRole.Primary, DataSourceAccessMode.ReadWrite, 0);
        var olderHealthy = Evidence("primary") with { ObservedAt = Now.AddMinutes(-2), ExpiresAt = Now.AddMinutes(1), EvidenceReference = "health-ref:primary:old" };
        var newerUnhealthy = Evidence("primary", DataSourceHealthState.Unhealthy) with { ObservedAt = Now.AddSeconds(-10), EvidenceReference = "health-ref:primary:new" };
        Assert.Throws<InvalidOperationException>(() => Select(new[] { candidate }, new[] { olderHealthy, newerUnhealthy }));
        Assert.Throws<InvalidOperationException>(() => Select(new[] { candidate }, new[] { newerUnhealthy, olderHealthy }));
    }

    [Fact]
    public void Evidence_EqualTimeConflictFailsClosed()
    {
        var candidate = Candidate("primary", DataSourceFailoverRole.Primary, DataSourceAccessMode.ReadWrite, 0);
        var healthy = Evidence("primary") with { EvidenceReference = "health-ref:primary:healthy" };
        var unhealthy = Evidence("primary", DataSourceHealthState.Unhealthy) with { EvidenceReference = "health-ref:primary:unhealthy" };
        Assert.Throws<InvalidOperationException>(() => Select(new[] { candidate }, new[] { healthy, unhealthy }));
    }

    [Fact]
    public void Write_FailsClosedWhenOnlyHealthyReplicaIsReadOnly()
    {
        var replica = Candidate("replica", DataSourceFailoverRole.Fallback, DataSourceAccessMode.ReadOnly, 0);
        Assert.Throws<InvalidOperationException>(() => Select(new[] { replica }, new[] { Evidence("replica") }, DataSourceOperationKind.Write));
        Assert.Equal("replica", Select(new[] { replica }, new[] { Evidence("replica") }).EndpointId);
    }

    [Fact]
    public void Candidate_FencesAuthorityVersionsAndCredentialReference()
    {
        Assert.Throws<UnauthorizedAccessException>(() => Select(new[] { Candidate("primary", DataSourceFailoverRole.Primary, DataSourceAccessMode.ReadWrite, 0) with { CompanyId = Guid.NewGuid() } }, new[] { Evidence("primary") }));
        Assert.Throws<InvalidOperationException>(() => Select(new[] { Candidate("primary", DataSourceFailoverRole.Primary, DataSourceAccessMode.ReadWrite, 0) with { SchemaVersion = "schema-42" } }, new[] { Evidence("primary") }));
        Assert.Throws<ArgumentException>(() => Select(new[] { Candidate("primary", DataSourceFailoverRole.Primary, DataSourceAccessMode.ReadWrite, 0) with { CredentialReference = "Server=db;Password=secret" } }, new[] { Evidence("primary") }));
    }

    [Fact]
    public void Select_RejectsDuplicateCandidateAndUndefinedEnums()
    {
        var first = Candidate("primary", DataSourceFailoverRole.Primary, DataSourceAccessMode.ReadWrite, 0);
        Assert.Throws<InvalidOperationException>(() => Select(new[] { first, first with { Role = DataSourceFailoverRole.Fallback } }, new[] { Evidence("primary") }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Select(new[] { first }, new[] { Evidence("primary") }, (DataSourceOperationKind)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Select(new[] { first with { Role = (DataSourceFailoverRole)99 } }, new[] { Evidence("primary") }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Select(new[] { first with { AccessMode = (DataSourceAccessMode)99 } }, new[] { Evidence("primary") }));
    }

    private static DataSourceFailoverDecision Select(DataSourceFailoverCandidate[] candidates, DataSourceHealthEvidence[] evidence, DataSourceOperationKind operation = DataSourceOperationKind.Read)
        => DataSourceFailoverContract.Select(Authority(), operation, candidates, evidence, Now);

    private static DataSourceFailoverAuthority Authority() => new(Tenant, Company, Source, "registry-7", "schema-43", "catalog-12");

    private static DataSourceFailoverCandidate Candidate(string endpoint, DataSourceFailoverRole role, DataSourceAccessMode access, int priority)
        => new(Tenant, Company, Source, endpoint, $"secret-ref:{endpoint}", role, access, priority, "registry-7", "schema-43", "catalog-12");

    private static DataSourceHealthEvidence Evidence(string endpoint, DataSourceHealthState state = DataSourceHealthState.Healthy)
        => new(Tenant, Company, Source, endpoint, "registry-7", "schema-43", "catalog-12", state, Now.AddSeconds(-30), Now.AddMinutes(1), $"health-ref:{endpoint}:v8");
}
