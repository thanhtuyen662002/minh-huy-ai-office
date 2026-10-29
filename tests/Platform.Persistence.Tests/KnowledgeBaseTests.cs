using System.Text;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class KnowledgeBaseTests
{
    private static (PlatformDbContext Db, KnowledgeBase Pages, AuthorizationContext Scope) Setup()
    {
        var db = new PlatformDbContext(MemberDirectoryFixture.Options());
        var scope = MemberDirectoryFixture.Authority();
        MemberDirectoryFixture.Seed(db, scope, role: null); db.SaveChanges();
        return (db, new KnowledgeBase(db, new EfAuthorizationDirectory(db)), scope);
    }

    [Fact]
    public async Task MembersCreateListEditAndDeletePagesWithVersionChecks()
    {
        var (db, pages, scope) = Setup();
        await using var _ = db;
        var created = await pages.CreateAsync(scope, scope.CompanyId, new("Khoá sổ cuối tháng", "- [ ] Kiểm tra phiếu thu", "Cty Hoà Phát Đạt", null));
        Assert.Equal(scope.UserId, created.CreatedBy);
        var list = await pages.ListAsync(scope, scope.CompanyId);
        Assert.Equal("Khoá sổ cuối tháng", Assert.Single(list.Items).Title);
        var edited = await pages.UpdateAsync(scope, scope.CompanyId, created.Id, new("Khoá sổ", "- [x] Kiểm tra phiếu thu", null, created.RowVersion));
        Assert.Null(edited.CustomerName); Assert.NotEqual(created.RowVersion, edited.RowVersion);
        await Assert.ThrowsAsync<CustomerRequestConflictException>(() =>
            pages.UpdateAsync(scope, scope.CompanyId, created.Id, new("Cũ", "", null, created.RowVersion)));
        await Assert.ThrowsAsync<CustomerRequestConflictException>(() => pages.DeleteAsync(scope, scope.CompanyId, created.Id, created.RowVersion));
        await pages.DeleteAsync(scope, scope.CompanyId, created.Id, edited.RowVersion);
        Assert.Empty((await pages.ListAsync(scope, scope.CompanyId)).Items);
        await Assert.ThrowsAsync<KnowledgePageNotFoundException>(() => pages.GetAsync(scope, scope.CompanyId, created.Id));
    }

    [Fact]
    public async Task OtherCompaniesAndRevokedMembersCannotReachPages()
    {
        var (db, pages, scope) = Setup();
        await using var _ = db;
        var page = await pages.CreateAsync(scope, scope.CompanyId, new("Riêng", "nội dung", null, null));
        var foreign = AuthorizationContext.Create(scope.TenantId, Guid.NewGuid(), Guid.NewGuid());
        MemberDirectoryFixture.Seed(db, foreign, role: null); await db.SaveChangesAsync();
        Assert.Empty((await pages.ListAsync(foreign, foreign.CompanyId)).Items);
        await Assert.ThrowsAsync<KnowledgePageNotFoundException>(() => pages.GetAsync(foreign, foreign.CompanyId, page.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pages.ListAsync(scope, foreign.CompanyId));
        db.CompanyMemberships.Single(x => x.UserId == scope.UserId).IsActive = false; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pages.GetAsync(scope, scope.CompanyId, page.Id));
    }

    [Theory]
    [InlineData("{\"title\":\"A\",\"body\":\"\"}", false, true)]
    [InlineData("{\"title\":\"A\",\"body\":\"x\",\"customerName\":\"Cty\"}", false, true)]
    [InlineData("{\"title\":\" \",\"body\":\"x\"}", false, false)]
    [InlineData("{\"title\":\"A\"}", false, false)]
    [InlineData("{\"title\":\"A\",\"body\":\"x\",\"extra\":1}", false, false)]
    [InlineData("{\"title\":\"A\",\"body\":\"x\",\"rowVersion\":\"AAAAAAAAB9A=\"}", false, false)]
    [InlineData("{\"title\":\"A\",\"body\":\"x\"}", true, false)]
    [InlineData("{\"title\":\"A\",\"body\":\"x\",\"rowVersion\":\"AAAAAAAAB9A=\"}", true, true)]
    [InlineData("{\"title\":\"A\",\"title\":\"B\",\"body\":\"x\"}", false, false)]
    public void ReadsOnlyWellFormedPages(string json, bool update, bool valid)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (valid) Assert.Equal("A", KnowledgePageJson.Read(bytes, update).Title);
        else Assert.ThrowsAny<Exception>(() => KnowledgePageJson.Read(bytes, update));
    }

    [Fact]
    public void RejectsOversizedBodies()
    {
        var body = new string('a', KnowledgePageJson.MaximumBody + 1);
        Assert.Throws<ArgumentException>(() => KnowledgePageJson.Read(Encoding.UTF8.GetBytes($"{{\"title\":\"A\",\"body\":\"{body}\"}}"), false));
    }
}
