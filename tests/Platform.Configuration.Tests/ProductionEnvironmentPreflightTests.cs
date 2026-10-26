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

    [Fact]
    public async Task Preflight_accepts_self_hosted_identity_with_file_backed_secrets()
    {
        using var fixture = new KeycloakEnvironment();
        fixture.Values["AIOFFICE_KEYCLOAK_DB_PASSWORD_FILE"] = DoubleQuoted(
            fixture.Values["AIOFFICE_KEYCLOAK_DB_PASSWORD_FILE"]);

        var result = await RunPreflightAsync(fixture.Values);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(KeycloakEnvironment.SecretValue, result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AIOFFICE_KEYCLOAK_HOSTNAME", "http://identity.example.test")]
    [InlineData("AIOFFICE_KEYCLOAK_HOSTNAME", "https://identity.example.test/")]
    [InlineData("AIOFFICE_KEYCLOAK_HOSTNAME", "https://user:private@identity.example.test")]
    [InlineData("AIOFFICE_KEYCLOAK_HOSTNAME", "https://identity.example.test/path")]
    [InlineData("AIOFFICE_KEYCLOAK_HOSTNAME", "https://identity.example.test?query=private")]
    [InlineData("AIOFFICE_KEYCLOAK_HOSTNAME", "https://localhost")]
    [InlineData("AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN", "https://office.example.test/")]
    [InlineData("AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN", "https://office.example.invalid")]
    [InlineData("AIOFFICE_AUTHORITY", "https://identity.example.test/realms/other")]
    [InlineData("AIOFFICE_BROWSER_OIDC_ISSUER", "https://identity.example.test/realms/aioffice/")]
    [InlineData("AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT", "https://foreign.example.test/auth")]
    [InlineData("AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT", "http://keycloak:8080/realms/aioffice/protocol/openid-connect/token")]
    [InlineData("AIOFFICE_BROWSER_OIDC_JWKS_URI", "https://foreign.example.test/certs")]
    [InlineData("AIOFFICE_BROWSER_OIDC_CLIENT_ID", "other-client")]
    [InlineData("AIOFFICE_BROWSER_OIDC_CLIENT_SECRET", "private-confidential-secret")]
    [InlineData("AIOFFICE_BROWSER_OIDC_LOCAL_HTTP", "true")]
    [InlineData("AIOFFICE_KEYCLOAK_DB_HOST", "sql.example.test;databaseName=AI_Office")]
    [InlineData("AIOFFICE_KEYCLOAK_DB_HOST", "sql.example.test;trustServerCertificate=true")]
    [InlineData("AIOFFICE_KEYCLOAK_DB_HOST", "replace-with-private-sql-hostname")]
    [InlineData("AIOFFICE_KEYCLOAK_DB_PORT", "0")]
    [InlineData("AIOFFICE_KEYCLOAK_DB_PORT", "65536")]
    [InlineData("AIOFFICE_KEYCLOAK_DB_PORT", "1433;databaseName=other")]
    [InlineData("AIOFFICE_KEYCLOAK_PORT", "-1")]
    [InlineData("AIOFFICE_KEYCLOAK_ENABLED", "yes")]
    public async Task Preflight_rejects_inconsistent_or_unsafe_keycloak_configuration(string name, string value)
    {
        using var fixture = new KeycloakEnvironment();
        fixture.Values[name] = value;

        var result = await RunPreflightAsync(fixture.Values);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(name, result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(KeycloakEnvironment.SecretValue, result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AIOFFICE_KEYCLOAK_DB_USERNAME_FILE")]
    [InlineData("AIOFFICE_KEYCLOAK_DB_PASSWORD_FILE")]
    [InlineData("AIOFFICE_KEYCLOAK_ADMIN_USERNAME_FILE")]
    [InlineData("AIOFFICE_KEYCLOAK_ADMIN_PASSWORD_FILE")]
    [InlineData("AIOFFICE_BROWSER_OIDC_ISSUER")]
    [InlineData("AIOFFICE_BROWSER_OIDC_CLIENT_ID")]
    public async Task Preflight_requires_each_keycloak_secret_reference_and_browser_identity(string name)
    {
        using var fixture = new KeycloakEnvironment();
        fixture.Values.Remove(name);

        var result = await RunPreflightAsync(fixture.Values);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(name, result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("replace-with-secret-file")]
    [InlineData("secretref://env/PRIVATE_KEYCLOAK_PASSWORD")]
    [InlineData("relative-secret.txt")]
    public async Task Preflight_rejects_unresolved_keycloak_secret_files(string reference)
    {
        using var fixture = new KeycloakEnvironment();
        fixture.Values["AIOFFICE_KEYCLOAK_DB_PASSWORD_FILE"] = reference;

        var result = await RunPreflightAsync(fixture.Values);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("AIOFFICE_KEYCLOAK_DB_PASSWORD_FILE", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preflight_rejects_missing_and_empty_keycloak_secret_files()
    {
        using var fixture = new KeycloakEnvironment();
        var path = fixture.Values["AIOFFICE_KEYCLOAK_ADMIN_PASSWORD_FILE"];
        await File.WriteAllTextAsync(path, string.Empty);
        var empty = await RunPreflightAsync(fixture.Values);
        File.Delete(path);
        var missing = await RunPreflightAsync(fixture.Values);

        Assert.NotEqual(0, empty.ExitCode);
        Assert.NotEqual(0, missing.ExitCode);
        Assert.Contains("AIOFFICE_KEYCLOAK_ADMIN_PASSWORD_FILE", empty.Output, StringComparison.Ordinal);
        Assert.Contains("AIOFFICE_KEYCLOAK_ADMIN_PASSWORD_FILE", missing.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preflight_keeps_external_identity_compatible_when_keycloak_is_disabled()
    {
        var environment = CreateValidEnvironment();
        environment["AIOFFICE_KEYCLOAK_ENABLED"] = "false";
        environment["AIOFFICE_KEYCLOAK_DB_HOST"] = "replace-with-unused-host";

        var result = await RunPreflightAsync(environment);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Provisioning_is_explicit_and_uses_a_protected_secret_file()
    {
        using var fixture = new KeycloakEnvironment();
        fixture.Values["AIOFFICE_KEYCLOAK_PROVISIONING_ENABLED"] = "false";
        Assert.Equal(0, (await RunPreflightAsync(fixture.Values)).ExitCode);
        var path = fixture.Values["AIOFFICE_KEYCLOAK_PROVISIONING_CLIENT_SECRET_FILE"];
        fixture.Values.Remove("AIOFFICE_KEYCLOAK_PROVISIONING_CLIENT_SECRET_FILE");
        fixture.Values["AIOFFICE_KEYCLOAK_PROVISIONING_ENABLED"] = "true";
        Assert.NotEqual(0, (await RunPreflightAsync(fixture.Values)).ExitCode);
        fixture.Values["AIOFFICE_KEYCLOAK_PROVISIONING_CLIENT_SECRET_FILE"] = path;
        var valid = await RunPreflightAsync(fixture.Values);
        Assert.Equal(0, valid.ExitCode);
        Assert.DoesNotContain(KeycloakEnvironment.SecretValue, valid.Output);
        foreach (var content in new[] { "", "short", "replace-with-private-secret-value-that-is-long", "first-line\nsecond-line-long-enough-to-not-pass" })
        {
            await File.WriteAllTextAsync(path, content);
            var invalid = await RunPreflightAsync(fixture.Values);
            Assert.NotEqual(0, invalid.ExitCode);
            Assert.Contains("AIOFFICE_KEYCLOAK_PROVISIONING_CLIENT_SECRET_FILE", invalid.Output);
            if (content.Length != 0) Assert.DoesNotContain(content, invalid.Output);
        }
    }

    [Theory]
    [InlineData("yes", "true")]
    [InlineData("true", "false")]
    public async Task Provisioning_rejects_invalid_flag_or_disabled_identity(string provisioning, string identity)
    {
        using var fixture = new KeycloakEnvironment();
        var environment = fixture.Values;
        environment["AIOFFICE_KEYCLOAK_PROVISIONING_ENABLED"] = provisioning;
        environment["AIOFFICE_KEYCLOAK_ENABLED"] = identity;
        var result = await RunPreflightAsync(environment);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("AIOFFICE_KEYCLOAK_PROVISIONING_ENABLED", result.Output);
    }

    private sealed class KeycloakEnvironment : IDisposable
    {
        public const string SecretValue = "unit-test-only-private-identity-value";
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"aioffice identity {Guid.NewGuid():N}");
        public Dictionary<string, string> Values { get; } = CreateValidEnvironment();

        public KeycloakEnvironment()
        {
            Directory.CreateDirectory(directory);
            Values["AIOFFICE_KEYCLOAK_ENABLED"] = "true";
            Values["AIOFFICE_KEYCLOAK_HOSTNAME"] = "https://identity.example.test";
            Values["AIOFFICE_AUTHORITY"] = "https://identity.example.test/realms/aioffice";
            Values["AIOFFICE_BROWSER_OIDC_ISSUER"] = Values["AIOFFICE_AUTHORITY"];
            Values["AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN"] = "https://office.example.test";
            Values["AIOFFICE_BROWSER_OIDC_CLIENT_ID"] = "aioffice-browser";
            Values["AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT"] = Values["AIOFFICE_AUTHORITY"] + "/protocol/openid-connect/auth";
            Values["AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT"] = Values["AIOFFICE_AUTHORITY"] + "/protocol/openid-connect/token";
            Values["AIOFFICE_BROWSER_OIDC_JWKS_URI"] = Values["AIOFFICE_AUTHORITY"] + "/protocol/openid-connect/certs";
            Values["AIOFFICE_KEYCLOAK_DB_HOST"] = "private-sql.example.test";
            Values["AIOFFICE_KEYCLOAK_DB_PORT"] = "1433";
            Values["AIOFFICE_KEYCLOAK_PORT"] = "8081";
            foreach (var name in new[] { "DB_USERNAME", "DB_PASSWORD", "ADMIN_USERNAME", "ADMIN_PASSWORD", "PROVISIONING_CLIENT_SECRET" })
            {
                var path = Path.Combine(directory, name + ".txt");
                File.WriteAllText(path, SecretValue);
                Values[$"AIOFFICE_KEYCLOAK_{name}_FILE"] = path;
            }
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    private static Dictionary<string, string> CreateValidEnvironment() =>
        new(StringComparer.Ordinal)
        {
            ["RABBITMQ_DEFAULT_USER"] = "aioffice-production",
            ["RABBITMQ_DEFAULT_PASS"] = "unit-test-value",
            ["AIOFFICE_DB_CONNECTION"] =
                "Server=127.0.0.1,1433;Database=AIOfficeValidation;Integrated Security=true;TrustServerCertificate=true",
            ["AIOFFICE_AUTHORITY"] = "https://identity.example.test/",
            ["AIOFFICE_AUDIENCE"] = "minh-huy-ai-office-tests",
            ["PILOT_ERP_CONNECTION"] = "Server=127.0.0.1,1433;Database=Pilot;Integrated Security=true"
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
