using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class AuthorizationContextTests
{
    [Fact]
    public void Create_RequiresTenantCompanyAndUser()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var context = AuthorizationContext.Create(tenantId, companyId, userId);

        Assert.Equal(tenantId, context.TenantId);
        Assert.Equal(companyId, context.CompanyId);
        Assert.Equal(userId, context.UserId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Create_RejectsEmptyIdentityComponent(int emptyComponent)
    {
        var values = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        values[emptyComponent] = Guid.Empty;

        Assert.Throws<ArgumentException>(() =>
            AuthorizationContext.Create(values[0], values[1], values[2]));
    }
}
