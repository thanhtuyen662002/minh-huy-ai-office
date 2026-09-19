namespace MinhHuy.AIOffice.Shared.Contracts;

public static class DataSourceConnectionTestCodes
{
    public const string Success = "success";
    public const string NotFound = "not_found";
    public const string Disabled = "disabled";
    public const string InvalidConfiguration = "invalid_configuration";
    public const string SecretUnavailable = "secret_unavailable";
    public const string ConnectionFailed = "connection_failed";
}

public sealed record DataSourceConnectionTestResult(
    bool Succeeded,
    string Code,
    string Message)
{
    public static DataSourceConnectionTestResult Success() =>
        new(true, DataSourceConnectionTestCodes.Success, "Connection test succeeded.");

    public static DataSourceConnectionTestResult NotFound() =>
        new(false, DataSourceConnectionTestCodes.NotFound, "Data source was not found in the authorized company scope.");

    public static DataSourceConnectionTestResult Disabled() =>
        new(false, DataSourceConnectionTestCodes.Disabled, "Data source is disabled.");

    public static DataSourceConnectionTestResult InvalidConfiguration() =>
        new(false, DataSourceConnectionTestCodes.InvalidConfiguration, "Data source configuration is invalid.");

    public static DataSourceConnectionTestResult SecretUnavailable() =>
        new(false, DataSourceConnectionTestCodes.SecretUnavailable, "Connection credential could not be resolved.");

    public static DataSourceConnectionTestResult ConnectionFailed() =>
        new(false, DataSourceConnectionTestCodes.ConnectionFailed, "Connection test failed.");
}
