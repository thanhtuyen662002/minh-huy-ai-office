using MinhHuy.AIOffice.Platform.Configuration;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Configuration.Tests;

public sealed class SecretReferenceTests
{
    [Theory]
    [InlineData("secretref://env/AIOFFICE_DB_CONNECTION", "env", "AIOFFICE_DB_CONNECTION")]
    [InlineData("secretref://azure-key-vault/minh-huy/production/sql/aioffice", "azure-key-vault", "minh-huy/production/sql/aioffice")]
    public void Parse_accepts_opaque_secret_references(
        string value,
        string provider,
        string resource)
    {
        var reference = SecretReference.Parse(value);

        Assert.Equal(provider, reference.Provider);
        Assert.Equal(resource, reference.Resource);
        Assert.StartsWith("secretref://", reference.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Server=sql;Database=AIOffice;User Id=sa;Password=plaintext;")]
    [InlineData("https://vault.example/secret")]
    [InlineData("secretref://env/")]
    [InlineData("secretref://user:password@env/AIOFFICE_DB_CONNECTION")]
    [InlineData("secretref://env/AIOFFICE_DB_CONNECTION?token=value")]
    [InlineData("secretref://env/AIOFFICE_DB_CONNECTION#password")]
    [InlineData("secretref://env/CONNECTION STRING")]
    public void Parse_rejects_plaintext_or_ambiguous_values(string? value)
    {
        Assert.False(SecretReference.TryParse(value, out _));
        Assert.Throws<FormatException>(() => SecretReference.Parse(value));
    }
}
