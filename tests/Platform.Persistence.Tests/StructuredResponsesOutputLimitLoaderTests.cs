extern alias RuntimeWorker;

using System.Globalization;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class StructuredResponsesOutputLimitLoaderTests
{
    private const string Refusal = "AI runtime maximum output tokens must be a canonical integer from 1 to 32768.";

    [Fact]
    public void Absent_optional_limit_retains_1024_and_all_existing_option_fields()
    {
        using var environment = new OwnedEnvironment();
        environment.Configure(environment.Prefix);
        var options = OpenAiCompatibleResponsesOptions.FromEnvironment(environment.Prefix, "owned-default-provider")!;
        Assert.Equal(1024, options.MaxOutputTokens);
        Assert.Equal(new Uri("https://owned-provider.example.invalid/v1/responses"), options.Endpoint);
        Assert.Equal("owned-model", options.Model);
        Assert.Equal("Bearer owned-placeholder", options.AuthorizationHeader);
        Assert.Equal("owned-default-provider", options.ProviderId);
        Assert.Equal(TimeSpan.FromSeconds(60), options.StructuredTimeout);
        Assert.False(options.AllowInsecureLoopback);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("42", 42)]
    [InlineData("1024", 1024)]
    [InlineData("16384", 16384)]
    [InlineData("32767", 32767)]
    [InlineData("32768", 32768)]
    public void Canonical_configured_limits_use_same_options_and_validate_boundaries(string raw, int expected)
    {
        using var environment = new OwnedEnvironment();
        environment.Configure(environment.Prefix);
        environment.Set(environment.Prefix, "MAX_OUTPUT_TOKENS", raw);
        Assert.Equal(expected, OpenAiCompatibleResponsesOptions.FromEnvironment(environment.Prefix)!.MaxOutputTokens);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("01")]
    [InlineData("001024")]
    [InlineData("32769")]
    [InlineData("99999")]
    [InlineData("2147483647")]
    [InlineData("2147483648")]
    [InlineData("999999999999999999999")]
    [InlineData("1.0")]
    [InlineData("1e3")]
    [InlineData("0x400")]
    [InlineData("1_024")]
    [InlineData("1,024")]
    [InlineData(" ")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("\t1")]
    [InlineData("1\r\n")]
    [InlineData("1\n")]
    [InlineData("1\u0001")]
    [InlineData("１")]
    [InlineData("١")]
    [InlineData("１024")]
    [InlineData("private-token-sentinel")]
    public void Invalid_limits_refuse_fixed_sanitized_reason_without_input_or_provider_values(string raw)
    {
        using var environment = new OwnedEnvironment();
        environment.Configure(environment.Prefix);
        environment.Set(environment.Prefix, "MAX_OUTPUT_TOKENS", raw);
        var failure = Assert.Throws<InvalidOperationException>(() => OpenAiCompatibleResponsesOptions.FromEnvironment(environment.Prefix));
        Assert.Equal(Refusal, failure.Message);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(environment.Prefix, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("owned-placeholder", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-token-sentinel", failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("broken")]
    [InlineData("32768")]
    [InlineData(" ")]
    public void Optional_only_configuration_still_returns_null_without_enabling_provider(string raw)
    {
        using var environment = new OwnedEnvironment();
        environment.Set(environment.Prefix, "MAX_OUTPUT_TOKENS", raw);
        environment.Set(environment.Prefix, "PROVIDER_ID", "optional-only-provider");
        environment.Set(environment.Prefix, "ALLOW_INSECURE_LOOPBACK", "true");
        Assert.Null(OpenAiCompatibleResponsesOptions.FromEnvironment(environment.Prefix));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Partial_required_provider_still_refuses_before_optional_limit_validation(int fields)
    {
        using var environment = new OwnedEnvironment();
        environment.Set(environment.Prefix, "BASE_URL", "https://owned-provider.example.invalid/v1");
        if (fields == 2) environment.Set(environment.Prefix, "MODEL", "owned-model");
        environment.Set(environment.Prefix, "MAX_OUTPUT_TOKENS", "invalid-private-sentinel");
        var failure = Assert.Throws<InvalidOperationException>(() => OpenAiCompatibleResponsesOptions.FromEnvironment(environment.Prefix));
        Assert.Contains("must provide", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid-private-sentinel", failure.ToString(), StringComparison.Ordinal);
        Assert.NotEqual(Refusal, failure.Message);
    }

    [Fact]
    public void Independent_primary_backup_and_legacy_prefixes_do_not_share_or_fallback_limits()
    {
        using var environment = new OwnedEnvironment();
        var primary = environment.Prefix + "_PRIMARY";
        var backup = environment.Prefix + "_BACKUP";
        var legacy = environment.Prefix;
        environment.Configure(primary);
        environment.Configure(backup);
        environment.Configure(legacy);
        environment.Set(primary, "MAX_OUTPUT_TOKENS", "1");
        environment.Set(backup, "MAX_OUTPUT_TOKENS", "32768");
        Assert.Equal(1, OpenAiCompatibleResponsesOptions.FromEnvironment(primary, "owned-primary")!.MaxOutputTokens);
        Assert.Equal(32768, OpenAiCompatibleResponsesOptions.FromEnvironment(backup, "owned-backup")!.MaxOutputTokens);
        var old = OpenAiCompatibleResponsesOptions.FromEnvironment(legacy, "owned-legacy")!;
        Assert.Equal(1024, old.MaxOutputTokens);
        Assert.Equal("owned-legacy", old.ProviderId);
        environment.Set(primary, "MAX_OUTPUT_TOKENS", null);
        Assert.Equal(1024, OpenAiCompatibleResponsesOptions.FromEnvironment(primary)!.MaxOutputTokens);
        Assert.Equal(32768, OpenAiCompatibleResponsesOptions.FromEnvironment(backup)!.MaxOutputTokens);
    }

    [Fact]
    public void Optional_limit_cannot_enable_missing_backup_or_leak_into_primary()
    {
        using var environment = new OwnedEnvironment();
        var primary = environment.Prefix + "_PRIMARY";
        var backup = environment.Prefix + "_BACKUP";
        environment.Configure(primary);
        environment.Set(backup, "MAX_OUTPUT_TOKENS", "8192");
        Assert.Null(OpenAiCompatibleResponsesOptions.FromEnvironment(backup));
        Assert.Equal(1024, OpenAiCompatibleResponsesOptions.FromEnvironment(primary)!.MaxOutputTokens);
    }

    [Fact]
    public void Existing_trimming_provider_override_loopback_option_and_constructor_default_are_unchanged()
    {
        using var environment = new OwnedEnvironment();
        environment.Configure(environment.Prefix);
        environment.Set(environment.Prefix, "PROVIDER_ID", "  owned-override  ");
        environment.Set(environment.Prefix, "ALLOW_INSECURE_LOOPBACK", "true");
        environment.Set(environment.Prefix, "MAX_OUTPUT_TOKENS", "2048");
        var options = OpenAiCompatibleResponsesOptions.FromEnvironment(environment.Prefix)!;
        Assert.Equal("owned-override", options.ProviderId);
        Assert.True(options.AllowInsecureLoopback);
        Assert.Equal(2048, options.MaxOutputTokens);
        Assert.Equal(1024, new OpenAiCompatibleResponsesOptions(options.Endpoint, options.Model, options.AuthorizationHeader, options.ProviderId).MaxOutputTokens);
    }

    [Fact]
    public void Canonical_decimal_is_culture_independent_without_accepting_locale_separators()
    {
        using var environment = new OwnedEnvironment();
        environment.Configure(environment.Prefix);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            environment.Set(environment.Prefix, "MAX_OUTPUT_TOKENS", "32768");
            Assert.Equal(32768, OpenAiCompatibleResponsesOptions.FromEnvironment(environment.Prefix)!.MaxOutputTokens);
            environment.Set(environment.Prefix, "MAX_OUTPUT_TOKENS", "1\u202f024");
            Assert.Equal(Refusal, Assert.Throws<InvalidOperationException>(() => OpenAiCompatibleResponsesOptions.FromEnvironment(environment.Prefix)).Message);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private sealed class OwnedEnvironment : IDisposable
    {
        private readonly HashSet<string> ownedNames = [];
        public string Prefix { get; } = "AIOFFICE_LOADER_TEST_" + Guid.NewGuid().ToString("N").ToUpperInvariant();

        public void Configure(string prefix)
        {
            Set(prefix, "BASE_URL", "  https://owned-provider.example.invalid/v1/  ");
            Set(prefix, "MODEL", "  owned-model  ");
            Set(prefix, "AUTHORIZATION", "  Bearer owned-placeholder  ");
        }

        public void Set(string prefix, string suffix, string? value)
        {
            Assert.True(prefix == Prefix || prefix.StartsWith(Prefix + "_", StringComparison.Ordinal));
            var name = prefix + "_" + suffix;
            ownedNames.Add(name);
            Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
        }

        public void Dispose()
        {
            foreach (var name in ownedNames)
                Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
        }
    }
}
