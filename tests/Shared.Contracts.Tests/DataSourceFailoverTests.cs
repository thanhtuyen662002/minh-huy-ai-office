using MinhHuy.AIOffice.Shared.Contracts.Erp;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class DataSourceFailoverTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Company = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Source = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void Select_PrefersHealthyPrimary_ThenDeterministicFallback()
    {
        var authority = Authority();
        var fallbackB = Candidate("fallback-b", DataSourceFailoverRole.Fallback, DataSourceAccessMode.ReadWrite, 10, true);
        var fallbackA = Candidate("fallback-a", DataSourceFailoverRole.Fallback, DataSourceAccessMode.ReadWrite, 10, true);
        var primary = Candidate("primary", DataSourceFailoverRole.Primary, DataSourceAccessMode.ReadWrite, 50, true);
        Assert.Equal("primary", DataSourceFailoverContract.Select(authority, DataSourceOperationKind.Read, new[] { fallbackB, primary, fallbackA }).EndpointId);
        var recovered = DataSourceFailoverContract.Select(authority, DataSourceOperationKind.Read, new[] { fallbackB, primary with { Healthy = false }, fallbackA });
        Assert.Equal("fallback-a", recovered.EndpointId);
        Assert.Equal("primary-unavailable-fallback-selected", recovered.Reason);
    }

    [Fact]
    public void Select_RejectsCrossAuthorityBeforeFailover()
    {
        var candidate = Candidate("other-company", DataSourceFailoverRole.Fallback, DataSourceAccessMode.ReadWrite, 0, true) with { CompanyId = Guid.NewGuid() };
        Assert.Throws<UnauthorizedAccessException>(() => DataSourceFailoverContract.Select(Authority(), DataSourceOperationKind.Read, new[] { candidate }));
    }

    [Fact]
    public void Select_RejectsStaleRegistryAndSchemaEvidence()
    {
        Assert.Throws<InvalidOperationException>(() => DataSourceFailoverContract.Select(Authority(), DataSourceOperationKind.Read,
            new[] { Candidate("stale", DataSourceFailoverRole.Primary, DataSourceAccessMode.ReadWrite, 0, true) with { RegistryVersion = "registry-6" } }));
        Assert.Throws<InvalidOperationException>(() => DataSourceFailoverContract.Select(Authority(), DataSourceOperationKind.Read,
            new[] { Candidate("schema", DataSourceFailoverRole.Primary, DataSourceAccessMode.ReadWrite, 0, true) with { SchemaVersion = "schema-42" } }));
    }

    [Fact]
    public void Write_FailsClosedWhenOnlyHealthyReplicaIsReadOnly()
    {
        var replica = Candidate("replica", DataSourceFailoverRole.Fallback, DataSourceAccessMode.ReadOnly, 0, true);
        Assert.Throws<InvalidOperationException>(() => DataSourceFailoverContract.Select(Authority(), DataSourceOperationKind.Write, new[] { replica }));
        Assert.Equal("replica", DataSourceFailoverContract.Select(Authority(), DataSourceOperationKind.Read, new[] { replica }).EndpointId);
    }

    [Fact]
    public void Candidate_RejectsConnectionStringMaterial()
    {
        var exposed = Candidate("primary", DataSourceFailoverRole.Primary, DataSourceAccessMode.ReadWrite, 0, true) with { CredentialReference = "Server=db;Password=secret" };
        Assert.Throws<ArgumentException>(() => DataSourceFailoverContract.Select(Authority(), DataSourceOperationKind.Read, new[] { exposed }));
    }

    [Fact]
    public void Select_RejectsDuplicateEndpointIdentity()
    {
        var first = Candidate("primary", DataSourceFailoverRole.Primary, DataSourceAccessMode.ReadWrite, 0, true);
        var conflicting = first with { Role = DataSourceFailoverRole.Fallback, Priority = 99, HealthEvidenceReference = "health-ref:conflicting" };
        Assert.Throws<InvalidOperationException>(() => DataSourceFailoverContract.Select(Authority(), DataSourceOperationKind.Read, new[] { first, conflicting }));
    }

    [Fact]
    public void Select_RejectsUndefinedOperationRoleAndAccessMode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DataSourceFailoverContract.Select(Authority(), (DataSourceOperationKind)99,
            new[] { Candidate("primary", DataSourceFailoverRole.Primary, DataSourceAccessMode.ReadWrite, 0, true) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => DataSourceFailoverContract.Select(Authority(), DataSourceOperationKind.Read,
            new[] { Candidate("role", (DataSourceFailoverRole)99, DataSourceAccessMode.ReadWrite, 0, true) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => DataSourceFailoverContract.Select(Authority(), DataSourceOperationKind.Read,
            new[] { Candidate("access", DataSourceFailoverRole.Primary, (DataSourceAccessMode)99, 0, true) }));
    }

    private static DataSourceFailoverAuthority Authority() => new(Tenant, Company, Source, "registry-7", "schema-43", "catalog-12");

    private static DataSourceFailoverCandidate Candidate(string endpoint, DataSourceFailoverRole role, DataSourceAccessMode access, int priority, bool healthy)
        => new(Tenant, Company, Source, endpoint, $"secret-ref:{endpoint}", role, access, priority,
            "registry-7", "schema-43", "catalog-12", healthy, $"health-ref:{endpoint}:v7");
}
