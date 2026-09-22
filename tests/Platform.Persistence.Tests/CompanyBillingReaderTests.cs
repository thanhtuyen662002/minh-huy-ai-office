extern alias CoreApi;

using Billing = CoreApi::MinhHuy.AIOffice.Core.Api.Billing;
using Authorization = CoreApi::MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class CompanyBillingReaderTests
{
    [Fact]
    public async Task GetCurrentAsync_UsesServerDerivedAuthorityAndPropagatesCancellation()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var accessor = CreateAccessor(tenantId, companyId);
        var expected = new CompanyPlan(new CompanyBillingAuthority(tenantId, companyId), "internal", 1_000_000, 250_000);
        using var cancellation = new CancellationTokenSource();
        var source = new RecordingSource(expected);
        var reader = new Billing.CompanyBillingReader(source);

        var actual = await reader.GetCurrentAsync(accessor, cancellation.Token);

        Assert.Same(expected, actual);
        Assert.Equal(new CompanyBillingAuthority(tenantId, companyId), source.Authority);
        Assert.Equal(cancellation.Token, source.CancellationToken);
    }

    [Fact]
    public async Task GetCurrentAsync_FailsClosedWithoutServerDerivedContext()
    {
        var source = new RecordingSource(null);
        var reader = new Billing.CompanyBillingReader(source);
        var accessor = new Authorization.RequestAuthorizationContextAccessor();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await reader.GetCurrentAsync(accessor));

        Assert.Null(source.Authority);
    }

    [Fact]
    public async Task GetCurrentAsync_RejectsSourceDataFromAnotherCompany()
    {
        var tenantId = Guid.NewGuid();
        var accessor = CreateAccessor(tenantId, Guid.NewGuid());
        var foreignPlan = new CompanyPlan(
            new CompanyBillingAuthority(tenantId, Guid.NewGuid()),
            "internal",
            1_000_000,
            0);
        var reader = new Billing.CompanyBillingReader(new RecordingSource(foreignPlan));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await reader.GetCurrentAsync(accessor));
    }

    [Fact]
    public async Task GetCurrentAsync_RejectsSourceDataFromAnotherTenant()
    {
        var companyId = Guid.NewGuid();
        var accessor = CreateAccessor(Guid.NewGuid(), companyId);
        var foreignPlan = new CompanyPlan(
            new CompanyBillingAuthority(Guid.NewGuid(), companyId),
            "internal",
            1_000_000,
            0);
        var reader = new Billing.CompanyBillingReader(new RecordingSource(foreignPlan));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await reader.GetCurrentAsync(accessor));
    }

    private static Authorization.RequestAuthorizationContextAccessor CreateAccessor(Guid tenantId, Guid companyId)
    {
        var context = AuthorizationContext.Create(tenantId, companyId, Guid.NewGuid());
        return new Authorization.RequestAuthorizationContextAccessor
        {
            Current = new Authorization.RequestAuthorizationContext(context, Array.Empty<string>())
        };
    }

    private sealed class RecordingSource(CompanyPlan? plan) : Billing.ICompanyBillingPlanSource
    {
        public CompanyBillingAuthority? Authority { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public ValueTask<CompanyPlan?> GetAsync(
            CompanyBillingAuthority authority,
            CancellationToken cancellationToken = default)
        {
            Authority = authority;
            CancellationToken = cancellationToken;
            return ValueTask.FromResult(plan);
        }
    }
}
