using System.Diagnostics;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Configuration.Tests;

public sealed class ProductionEnvironmentPreflightTests
{
    [Fact]
    public async Task Preflight_rejects_unsafe_quoted_values()
    {
        var unsafeValues = new[]
        {
            (Name: "RABBITMQ_DEFAULT_PASS", Value: DoubleQuoted(string.Empty)),
            (Name: "RABBITMQ_DEFAULT_PASS", Value: SingleQuoted("   ")),
            (Name: "RABBITMQ_DEFAULT_USER", Value: DoubleQuoted("ai-office-dev")),
            (Name: "RABBITMQ_DEFAULT_PASS", Value: DoubleQuoted("replace-with-production-secret"))
        };

        foreach (var (name, value) in unsafeValues)
        {
            var environment = CreateValidEnvironment();
            environment[name] = value;

            var result = await RunPreflightAsync(environment);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(name, result.Output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Preflight_accepts_valid_quoted_values()
    {
        var environment = CreateValidEnvironment();
        environment["RABBITMQ_DEFAULT_USER"] = DoubleQuoted("aioffice-production");
        environment["RABBITMQ_DEFAULT_PASS"] = SingleQuoted("unit-test-value");
        environment["AIOFFICE_DB_CONNECTION"] = DoubleQuoted(
            "Server=127.0.0.1,1433;Database=AIOfficeValidation;Integrated Security=true;TrustServerCertificate=true");

        var result = await RunPreflightAsync(environment);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "Production environment preflight passed",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preflight_rejects_unmatched_quotes()
    {
        var environment = CreateValidEnvironment();
        environment["RABBITMQ_DEFAULT_PASS"] =
            string.Concat((char)34, "unit-test-value");

        var result = await RunPreflightAsync(environment);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "RABBITMQ_DEFAULT_PASS has unmatched or mixed quotes",
            result.Output,
            StringComparison.Ordinal);
    }

    private static Dictionary<string, string> CreateValidEnvironment() =>
        new(StringComparer.Ordinal)
        {
            ["RABBITMQ_DEFAULT_USER"] = "aioffice-production",
            ["RABBITMQ_DEFAULT_PASS"] = "unit-test-value",
            ["AIOFFICE_DB_CONNECTION"] =
                "Server=127.0.0.1,1433;Database=AIOfficeValidation;Integrated Security=true;TrustServerCertificate=true"
        };

    private static string DoubleQuoted(string value) =>
        string.Concat((char)34, value, (char)34);

    private static string SingleQuoted(string value) =>
        string.Concat((char)39, value, (char)39);

    private static async Task<ProcessResult> RunPreflightAsync(
        IReadOnlyDictionary<string, string> environment)
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "infra",
            "validate-production-env.ps1");
        var environmentPath = Path.Combine(
            Path.GetTempPath(),
            $"aioffice-production-{Guid.NewGuid():N}.env");

        try
        {
            await File.WriteAllLinesAsync(
                environmentPath,
                environment.Select(pair => $"{pair.Key}={pair.Value}"));

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "pwsh",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(scriptPath);
            process.StartInfo.ArgumentList.Add("-EnvironmentFile");
            process.StartInfo.ArgumentList.Add(environmentPath);

            Assert.True(process.Start());

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            return new ProcessResult(
                process.ExitCode,
                string.Concat(await standardOutput, await standardError));
        }
        finally
        {
            File.Delete(environmentPath);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "infra",
                    "validate-production-env.ps1")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            "Could not locate repository root for production environment preflight tests.");
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}
