using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Core.Api.Sla;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class CustomerSlaStatusProjectionTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CompanyId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid UserId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task GetCurrentAsync_ReturnsOnlyPresentationAllowlistForAuthenticatedAuthority()
    {
        var effectiveAt = DateTimeOffset.Parse("2026-09-24T12:00:00Z");
        var source = new StubSource(new CustomerSlaStatusSnapshot(
            new CustomerSlaStatusAuthority(TenantId, CompanyId, UserId, 7),
            "Premium",
            4,
            10,
            12,
            effectiveAt,
            "admission-42"));
        var projection = new CustomerSlaStatusProjection(source);

        var result = await projection.GetCurrentAsync(Accessor());

        Assert.NotNull(result);
        Assert.Equal("Premium", result.ServiceLabel);
        Assert.Equal(4, result.SchedulerPriority);
        Assert.Equal(10, result.PriorityCeiling);
        Assert.Equal(12, result.PolicyVersion);
        Assert.Equal(effectiveAt, result.EffectiveAt);
        Assert.Equal("admission-42", result.AdmissionId);
        Assert.DoesNotContain("Tenant", result.GetType().GetProperties().Select(property => property.Name));
        Assert.DoesNotContain("Company", result.GetType().GetProperties().Select(property => property.Name));
        Assert.DoesNotContain("User", result.GetType().GetProperties().Select(property => property.Name));
        Assert.DoesNotContain("Secret", result.GetType().GetProperties().Select(property => property.Name));
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("user")]
    public async Task GetCurrentAsync_RejectsCrossAuthoritySnapshot(string mismatch)
    {
        var authority = new CustomerSlaStatusAuthority(
            mismatch == "tenant" ? Guid.NewGuid() : TenantId,
            mismatch == "company" ? Guid.NewGuid() : CompanyId,
            mismatch == "user" ? Guid.NewGuid() : UserId,
            1);
        var projection = new CustomerSlaStatusProjection(new StubSource(Snapshot(authority)));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => projection.GetCurrentAsync(Accessor()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetCurrentAsync_RejectsInvalidAuthorityVersion(long authorityVersion)
    {
        var projection = new CustomerSlaStatusProjection(new StubSource(Snapshot(
            new CustomerSlaStatusAuthority(TenantId, CompanyId, UserId, authorityVersion))));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => projection.GetCurrentAsync(Accessor()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetCurrentAsync_RejectsInvalidOrStalePolicyRevision(long policyVersion)
    {
        var snapshot = Snapshot(new CustomerSlaStatusAuthority(TenantId, CompanyId, UserId, 1)) with
        {
            PolicyVersion = policyVersion,
        };
        var projection = new CustomerSlaStatusProjection(new StubSource(snapshot));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => projection.GetCurrentAsync(Accessor()));
    }

    [Fact]
    public async Task GetCurrentAsync_RejectsMissingAuthenticatedAuthorityBeforeSourceRead()
    {
        var source = new StubSource(Snapshot(new CustomerSlaStatusAuthority(TenantId, CompanyId, UserId, 1)));
        var projection = new CustomerSlaStatusProjection(source);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            projection.GetCurrentAsync(new RequestAuthorizationContextAccessor()));
        Assert.Equal(0, source.ReadCount);
    }

    [Fact]
    public async Task GetCurrentAsync_RejectsMalformedPriorityEvidence()
    {
        var snapshot = Snapshot(new CustomerSlaStatusAuthority(TenantId, CompanyId, UserId, 1)) with
        {
            SchedulerPriority = 11,
            PriorityCeiling = 10,
        };
        var projection = new CustomerSlaStatusProjection(new StubSource(snapshot));

        await Assert.ThrowsAsync<InvalidOperationException>(() => projection.GetCurrentAsync(Accessor()));
    }

    private static RequestAuthorizationContextAccessor Accessor() => new()
    {
        Current = new RequestAuthorizationContext(
            AuthorizationContext.Create(TenantId, CompanyId, UserId),
            Array.Empty<string>()),
    };

    private static CustomerSlaStatusSnapshot Snapshot(CustomerSlaStatusAuthority authority) => new(
        authority,
        "Standard",
        2,
        10,
        3,
        DateTimeOffset.Parse("2026-09-24T12:00:00Z"),
        "admission-1");

    private sealed class StubSource(CustomerSlaStatusSnapshot? snapshot) : ICustomerSlaStatusSource
    {
        public int ReadCount { get; private set; }

        public Task<CustomerSlaStatusSnapshot?> GetCurrentAsync(
            AuthorizationContext authority,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(snapshot);
        }
    }
}
