using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace MinhHuy.AIOffice.Core.Api.Authorization;

public static class BrowserIssuerConfiguration
{
    // Additive opt-in: retained Authority-only pilot configuration is unchanged.
    // Metadata routing never expands the trusted token issuer to its internal URL.
    public static void Apply(JwtBearerOptions options, string authority, string? metadataAddress,
        bool development, bool localHttp)
    {
        if (metadataAddress is null) return;
        static InvalidOperationException Refused() => new("Identity metadata configuration is invalid.");
        static Uri Endpoint(string raw)
        {
            if (raw.Length > 2048 || raw.Any(char.IsWhiteSpace) || raw.Contains('\\')
                || !Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.AbsoluteUri != raw
                || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw Refused();
            return uri;
        }
        static bool Loopback(Uri uri) => uri.Host is "127.0.0.1" or "localhost" or "[::1]";
        var issuer = Endpoint(authority);
        var metadata = Endpoint(metadataAddress);
        var allowLocal = development && localHttp;
        if (issuer.Scheme != "https" && !(allowLocal && issuer.Scheme == "http" && Loopback(issuer)
            && issuer.AbsolutePath == "/realms/aioffice-local")) throw Refused();
        if (metadata.Scheme != "https" && !(allowLocal && metadata.Scheme == "http"
            && (Loopback(metadata) || metadata.Host == "identity" && metadata.Port == 8080)
            && metadata.AbsolutePath == "/realms/aioffice-local/.well-known/openid-configuration")) throw Refused();
        if (localHttp && !development) throw Refused();
        options.MetadataAddress = metadataAddress;
        options.RequireHttpsMetadata = !allowLocal;
        options.TokenValidationParameters.ValidIssuer = authority;
        options.TokenValidationParameters.IssuerValidator = (value, _, _) =>
            string.Equals(value, authority, StringComparison.Ordinal) ? authority
                : throw new SecurityTokenInvalidIssuerException("Token issuer could not be verified.");
    }
}
