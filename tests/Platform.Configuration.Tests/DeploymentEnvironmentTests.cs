using MinhHuy.AIOffice.Platform.Configuration;

namespace MinhHuy.AIOffice.Platform.Configuration.Tests;

public sealed class DeploymentEnvironmentTests
{
    [Theory]
    [InlineData("Development", DeploymentEnvironmentKind.Development)]
    [InlineData("development", DeploymentEnvironmentKind.Development)]
    [InlineData("Staging", DeploymentEnvironmentKind.Staging)]
    [InlineData("Production", DeploymentEnvironmentKind.Production)]
    public void Parse_accepts_supported_environments(
        string value,
        DeploymentEnvironmentKind expected)
    {
        Assert.Equal(expected, DeploymentEnvironment.Parse(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Test")]
    [InlineData("Production-West")]
    public void Parse_rejects_unsupported_environments(string? value)
    {
        Assert.Throws<InvalidOperationException>(() => DeploymentEnvironment.Parse(value));
    }
}
