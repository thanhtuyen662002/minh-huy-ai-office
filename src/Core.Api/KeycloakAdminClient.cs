using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;

namespace MinhHuy.AIOffice.Core.Api;

public sealed class KeycloakProvisioningOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "";
    public string ClientId { get; set; } = "aioffice-member-provisioner";
    public string ClientSecretFile { get; set; } = "";
    public string ClientSecretRef { get; set; } = "";
    // Browser client named in setup emails so Keycloak's "back to application" link returns to AI Office.
    public string BrowserClientId { get; set; } = "aioffice-browser";

    public void Validate()
    {
        if (!Enabled) return;
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.IsLoopback || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            BaseUrl != uri.GetLeftPart(UriPartial.Authority) || ClientId != "aioffice-member-provisioner" ||
            (string.IsNullOrEmpty(ClientSecretFile) == string.IsNullOrEmpty(ClientSecretRef)) ||
            !System.Text.RegularExpressions.Regex.IsMatch(BrowserClientId, "^[A-Za-z0-9._-]{1,64}$") ||
            (ClientSecretFile.Length != 0 && !Path.IsPathFullyQualified(ClientSecretFile)) ||
            (ClientSecretRef.Length != 0 && (!SecretReference.TryParse(ClientSecretRef, out var reference) || reference!.Provider != "env")))
            throw new InvalidOperationException("Invalid Keycloak provisioning configuration. Use an HTTPS origin and one file or environment secret reference.");
    }
}

public sealed class KeycloakAdminClient(HttpClient http, KeycloakProvisioningOptions options) : IKeycloakAdminClient
{
    public bool Enabled => options.Enabled;

    public async Task<bool> UserExistsAsync(string email, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            options.BaseUrl + "/admin/realms/aioffice/users?exact=true&max=2&email=" + Uri.EscapeDataString(email));
        using var response = await SendAsync(request, cancellationToken);
        await RequireSuccessAsync(response);
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new KeycloakProvisioningException();
            var users = document.RootElement.EnumerateArray().ToArray();
            if (users.Length == 0) return false;
            if (users.Length != 1 || !users[0].TryGetProperty("email", out var found) || found.ValueKind != JsonValueKind.String ||
                !string.Equals(found.GetString(), email, StringComparison.OrdinalIgnoreCase)) throw new KeycloakProvisioningException();
            return true;
        }
        catch (Exception error) when (error is JsonException or HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
        { throw new KeycloakProvisioningException(true); }
    }

    public async Task<bool> CreateUserAsync(string email, string temporaryPassword, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.BaseUrl + "/admin/realms/aioffice/users")
        {
            Content = JsonContent.Create(new
            {
                username = email,
                email,
                emailVerified = true,
                enabled = true,
                credentials = new[] { new { type = "password", value = temporaryPassword, temporary = true } },
                requiredActions = new[] { "UPDATE_PASSWORD" }
            })
        };
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict && await UserExistsAsync(email, cancellationToken)) return false;
        if (response.StatusCode != HttpStatusCode.Created) throw Failure(response.StatusCode);
        return true;
    }

    // Setup links stay valid for three days so a member can act on the invitation later.
    private const int PasswordSetupLifespanSeconds = 3 * 24 * 60 * 60;

    public async Task SendPasswordSetupEmailAsync(string email, CancellationToken cancellationToken)
    {
        using var lookup = new HttpRequestMessage(HttpMethod.Get,
            options.BaseUrl + "/admin/realms/aioffice/users?exact=true&max=2&briefRepresentation=true&email=" + Uri.EscapeDataString(email));
        using var found = await SendAsync(lookup, cancellationToken);
        await RequireSuccessAsync(found);
        string id;
        try
        {
            using var document = await JsonDocument.ParseAsync(await found.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var users = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().ToArray() : [];
            if (users.Length != 1 || !users[0].TryGetProperty("email", out var match) || match.ValueKind != JsonValueKind.String ||
                !string.Equals(match.GetString(), email, StringComparison.OrdinalIgnoreCase) ||
                !users[0].TryGetProperty("id", out var value) || !Guid.TryParse(value.GetString(), out var parsed))
                throw new KeycloakProvisioningException();
            id = parsed.ToString("D");
        }
        catch (Exception error) when (error is JsonException or HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
        { throw new KeycloakProvisioningException(true); }
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"{options.BaseUrl}/admin/realms/aioffice/users/{id}/execute-actions-email?lifespan={PasswordSetupLifespanSeconds}&client_id={Uri.EscapeDataString(options.BrowserClientId)}")
        { Content = JsonContent.Create(new[] { "UPDATE_PASSWORD" }) };
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NoContent) throw Failure(response.StatusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!Enabled) throw new KeycloakProvisioningException(true);
        try
        {
            var secret = options.ClientSecretFile.Length != 0
                ? (await File.ReadAllTextAsync(options.ClientSecretFile, cancellationToken)).TrimEnd('\r', '\n')
                : await new EnvironmentVariableSecretResolver().ResolveAsync(SecretReference.Parse(options.ClientSecretRef), cancellationToken);
            if (string.IsNullOrWhiteSpace(secret) || secret.Contains("replace-with-", StringComparison.OrdinalIgnoreCase))
                throw new KeycloakProvisioningException(true);
            using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, options.BaseUrl + "/realms/aioffice/protocol/openid-connect/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = options.ClientId,
                    ["client_secret"] = secret
                })
            };
            tokenRequest.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            using var tokenResponse = await http.SendAsync(tokenRequest, cancellationToken);
            await RequireSuccessAsync(tokenResponse);
            using var token = await JsonDocument.ParseAsync(await tokenResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var accessToken = token.RootElement.GetProperty("access_token").GetString();
            if (string.IsNullOrWhiteSpace(accessToken)) throw new KeycloakProvisioningException();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return await http.SendAsync(request, cancellationToken);
        }
        catch (KeycloakProvisioningException) { throw; }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException or
            UnauthorizedAccessException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException or ArgumentException)
        { throw new KeycloakProvisioningException(true); }
    }

    private static Task RequireSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode);
        return Task.CompletedTask;
    }
    private static KeycloakProvisioningException Failure(HttpStatusCode status) => new(status is HttpStatusCode.TooManyRequests or
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.RequestTimeout);
}

internal static class KeycloakProvisioningRegistration
{
    public static void AddKeycloakProvisioning(this WebApplicationBuilder builder)
    {
        var options = new KeycloakProvisioningOptions();
        builder.Configuration.GetSection("AIOffice:KeycloakProvisioning").Bind(options);
        options.Validate();
        builder.Services.AddSingleton(options);
        builder.Services.AddHttpClient<IKeycloakAdminClient, KeycloakAdminClient>(http =>
            { http.Timeout = TimeSpan.FromSeconds(15); http.MaxResponseContentBufferSize = 65536; })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .RemoveAllLoggers();
    }
}
