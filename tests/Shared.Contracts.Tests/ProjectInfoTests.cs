using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class ProjectInfoTests
{
    [Fact]
    public void ProductName_IsStable()
    {
        Assert.Equal("Minh Huy AI Office", ProjectInfo.ProductName);
    }
}
