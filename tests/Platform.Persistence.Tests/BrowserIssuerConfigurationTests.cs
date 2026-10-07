extern alias CoreApi;

using System.Security.Cryptography;
using CoreApi::MinhHuy.AIOffice.Core.Api.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class BrowserIssuerConfigurationTests
{
    private const string PublicIssuer = "https://identity.example.invalid/realms/office";
    private const string InternalIssuer = "https://internal.example.invalid/realms/office";
    private const string Metadata = InternalIssuer + "/.well-known/openid-configuration";
    private static JwtBearerOptions Options() => new()
    {
        Authority = PublicIssuer,
        Audience = "office-api",
        TokenValidationParameters = new()
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidAudience = "office-api"
        }
    };

    [Fact]
    public void CoreWiresExplicitPublicIssuerAndInternalMetadata()
    {
        using var factory = new WebApplicationFactory<CoreApi::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("AIOffice:Authentication:Authority", PublicIssuer);
            builder.UseSetting("AIOffice:Authentication:Audience", "office-api");
            builder.UseSetting("AIOffice:Authentication:MetadataAddress", Metadata);
        });
        var options = factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        Assert.Equal(PublicIssuer, options.Authority);
        Assert.Equal(Metadata, options.MetadataAddress);
        Assert.True(options.RequireHttpsMetadata);
        Assert.NotNull(options.TokenValidationParameters.IssuerValidator);
        Assert.Equal(PublicIssuer, options.TokenValidationParameters.ValidIssuer);
        Assert.False(options.MapInboundClaims);
    }

    [Fact]
    public async Task SignedTokenCannotUseMetadataIssuerOrAliasToExpandTrust()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "owned-test-key" };
        var metadata = new OpenIdConnectConfiguration { Issuer = InternalIssuer };
        metadata.SigningKeys.Add(key);
        var options = Options();
        var parameters = options.TokenValidationParameters;
        parameters.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
        parameters.ValidIssuer = PublicIssuer;
        var handler = new JsonWebTokenHandler();
        string Token(string issuer, SecurityKey signingKey, string audience = "office-api", int seconds = 300) => handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = new Dictionary<string, object> { ["sub"] = "opaque-owner", ["idp"] = "local-keycloak" },
            IssuedAt = DateTime.UtcNow.AddMinutes(-10),
            NotBefore = DateTime.UtcNow.AddMinutes(-10),
            Expires = DateTime.UtcNow.AddSeconds(seconds),
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)
        });
        // The framework default accepts the issuer reported by metadata as well
        // as ValidIssuer. Preserve this negative control with a real signature.
        var wrongIssuer = Token(InternalIssuer, key);
        Assert.True((await handler.ValidateTokenAsync(wrongIssuer, parameters)).IsValid);
        BrowserIssuerConfiguration.Apply(options, PublicIssuer, Metadata, false, false);
        Assert.False((await handler.ValidateTokenAsync(wrongIssuer, parameters)).IsValid);
        Assert.True((await handler.ValidateTokenAsync(Token(PublicIssuer, key), parameters)).IsValid);
        foreach (var alias in new[] { PublicIssuer + "/", PublicIssuer + " ", PublicIssuer.Replace("/office", "/Office") })
            Assert.False((await handler.ValidateTokenAsync(Token(alias, key), parameters)).IsValid);
        using var foreignRsa = RSA.Create(2048);
        Assert.False((await handler.ValidateTokenAsync(Token(PublicIssuer, new RsaSecurityKey(foreignRsa) { KeyId = key.KeyId }), parameters)).IsValid);
        Assert.False((await handler.ValidateTokenAsync(Token(PublicIssuer, key, "wrong-api"), parameters)).IsValid);
        parameters.ClockSkew = TimeSpan.Zero;
        Assert.False((await handler.ValidateTokenAsync(Token(PublicIssuer, key, seconds: -1), parameters)).IsValid);
    }

    [Fact]
    public void ExplicitDevelopmentLoopbackIssuerUsesOnlyFixedLocalBackchannel()
    {
        var options = Options();
        const string issuer = "http://127.0.0.1:8081/realms/aioffice-local";
        const string metadata = "http://identity:8080/realms/aioffice-local/.well-known/openid-configuration";
        BrowserIssuerConfiguration.Apply(options, issuer, metadata, true, true);
        Assert.False(options.RequireHttpsMetadata);
        Assert.Equal(issuer, options.TokenValidationParameters.IssuerValidator!(issuer, null!, null!));
        Assert.Throws<SecurityTokenInvalidIssuerException>(() => options.TokenValidationParameters.IssuerValidator!(
            "http://identity:8080/realms/aioffice-local", null!, null!));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8081/realms/aioffice-local", "http://identity:8080/realms/aioffice-local/.well-known/openid-configuration", false, true)]
    [InlineData("http://127.0.0.1:8081/realms/aioffice-local", "http://identity:8080/realms/aioffice-local/.well-known/openid-configuration", true, false)]
    [InlineData("http://remote.invalid/realms/aioffice-local", "http://identity:8080/realms/aioffice-local/.well-known/openid-configuration", true, true)]
    [InlineData(PublicIssuer, "http://foreign:8080/realms/aioffice-local/.well-known/openid-configuration", true, true)]
    [InlineData(PublicIssuer, "http://identity:8081/realms/aioffice-local/.well-known/openid-configuration", true, true)]
    [InlineData(PublicIssuer, "http://identity:8080/other/.well-known/openid-configuration", true, true)]
    [InlineData(PublicIssuer, "https://user:password@internal.invalid/metadata", false, false)]
    [InlineData(PublicIssuer, Metadata + "?redirect=foreign", false, false)]
    [InlineData(PublicIssuer, Metadata + "#fragment", false, false)]
    [InlineData(PublicIssuer + " ", Metadata, false, false)]
    [InlineData(PublicIssuer, "", false, false)]
    public void UnsafeMetadataRoutingIsRefusedGenerically(string issuer, string metadata, bool development, bool local)
    {
        var error = Assert.Throws<InvalidOperationException>(() => BrowserIssuerConfiguration.Apply(Options(), issuer, metadata, development, local));
        Assert.Equal("Identity metadata configuration is invalid.", error.Message);
    }

    [Fact]
    public void RetainedAuthorityOnlyOptionsAreUnchanged()
    {
        var options = Options();
        var parameters = options.TokenValidationParameters;
        BrowserIssuerConfiguration.Apply(options, PublicIssuer, null, false, false);
        Assert.Null(options.MetadataAddress);
        Assert.Null(parameters.IssuerValidator);
        Assert.Same(parameters, options.TokenValidationParameters);
    }
}
