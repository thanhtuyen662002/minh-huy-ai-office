using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.Erp;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class DataSourceFailoverOperationPolicyStoreTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Company = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Source = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-25T15:00:00Z");

    [Fact]
    public async Task Resolve_SelectsCurrentRevisionAndIgnoresFuture()
    {
        await using var db = CreateContext(); Seed(db, 7, Now.AddMinutes(-5)); Seed(db, 8, Now.AddMinutes(5)); await db.SaveChangesAsync();
        var resolved = await new DataSourceFailoverOperationPolicyStore(db).ResolveEffectiveAsync(Authority(), DataSourceOperationKind.Read, Now);
        Assert.Equal(7, resolved.Version);
    }

    [Fact]
    public async Task Resolve_FailsClosedForNoCurrentCrossAuthorityAndOperation()
    {
        await using var db = CreateContext(); Seed(db, 7, Now.AddMinutes(5)); await db.SaveChangesAsync();
        var store = new DataSourceFailoverOperationPolicyStore(db);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ResolveEffectiveAsync(Authority(), DataSourceOperationKind.Read, Now));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ResolveEffectiveAsync(Authority() with { CompanyId = Guid.NewGuid() }, DataSourceOperationKind.Read, Now.AddMinutes(10)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ResolveEffectiveAsync(Authority(), DataSourceOperationKind.Write, Now.AddMinutes(10)));
    }

    [Fact]
    public async Task Resolve_FailsClosedForAmbiguousActiveRevision()
    {
        await using var db = CreateContext(); Seed(db, 7, Now.AddMinutes(-1)); Seed(db, 8, Now.AddMinutes(-1)); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DataSourceFailoverOperationPolicyStore(db).ResolveEffectiveAsync(Authority(), DataSourceOperationKind.Read, Now));
    }

    [Fact]
    public async Task Persist_IsIdempotentAndRejectsConflictingReplay()
    {
        await using var db = CreateContext();
        var store = new DataSourceFailoverOperationPolicyStore(db);
        var policy = new DataSourceFailoverOperationPolicy(Tenant, Company, Source, DataSourceOperationKind.Read, 7, Now.AddMinutes(-1));
        await store.PersistAsync(policy); await store.PersistAsync(policy);
        Assert.Single(db.DataSourceFailoverOperationPolicies);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PersistAsync(policy with { EffectiveAt = Now }));
    }

    private static PlatformDbContext CreateContext() => new(new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static void Seed(PlatformDbContext db, long version, DateTimeOffset effectiveAt) => db.DataSourceFailoverOperationPolicies.Add(new DataSourceFailoverOperationPolicyRecord { TenantId = Tenant, CompanyId = Company, DataSourceId = Source, Operation = DataSourceOperationKind.Read, Version = version, EffectiveAt = effectiveAt });
    private static DataSourceFailoverAuthority Authority() => new(Tenant, Company, Source, "registry-7", "schema-43", "catalog-12", TimeSpan.FromMinutes(5));
}
