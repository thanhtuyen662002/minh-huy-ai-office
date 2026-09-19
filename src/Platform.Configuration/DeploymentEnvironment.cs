namespace MinhHuy.AIOffice.Platform.Configuration;

public enum DeploymentEnvironmentKind
{
    Development,
    Staging,
    Production
}

public static class DeploymentEnvironment
{
    public static DeploymentEnvironmentKind Parse(string? environmentName)
    {
        if (string.IsNullOrWhiteSpace(environmentName))
        {
            throw new InvalidOperationException(
                "Deployment environment is required. Allowed values: Development, Staging, Production.");
        }

        if (environmentName.Equals("Development", StringComparison.OrdinalIgnoreCase))
        {
            return DeploymentEnvironmentKind.Development;
        }

        if (environmentName.Equals("Staging", StringComparison.OrdinalIgnoreCase))
        {
            return DeploymentEnvironmentKind.Staging;
        }

        if (environmentName.Equals("Production", StringComparison.OrdinalIgnoreCase))
        {
            return DeploymentEnvironmentKind.Production;
        }

        throw new InvalidOperationException(
            $"Unsupported deployment environment '{environmentName}'. Allowed values: Development, Staging, Production.");
    }
}
