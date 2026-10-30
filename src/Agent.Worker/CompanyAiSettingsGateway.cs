using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Agent.Worker;

/// <summary>
/// Uses the company's "AI dùng chung" settings from Kết nối (the same settings the Zalo and
/// WhatsApp bots use) when they hold a model and an API key; otherwise the server-configured
/// gateway answers, when there is one.
/// </summary>
public sealed class CompanyAiSettingsGateway(
    PlatformDbContext database,
    BotSecretProtector protector,
    HttpClient httpClient,
    IAiGateway? fallback) : IAiGateway
{
    public const string ProviderId = "company-shared";

    public async Task<AiGatewayResponse> ExecuteAsync(AiGatewayRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var adapter = await CompanyAdapterAsync(request, cancellationToken).ConfigureAwait(false);
        if (adapter is null)
        {
            return fallback is not null
                ? await fallback.ExecuteAsync(request, cancellationToken).ConfigureAwait(false)
                : throw new AiProviderExecutionException("No AI provider is configured for this company.", false);
        }

        if (!adapter.Supports(request.Capability))
            throw new InvalidOperationException($"Company AI provider does not support capability {request.Capability}.");
        var response = await adapter.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        response.ValidateFor(request);
        return response;
    }

    private async Task<OpenAiCompatibleChatCompletionsAdapter?> CompanyAdapterAsync(AiGatewayRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.TenantId, out var tenantId) || !Guid.TryParse(request.CompanyId, out var companyId)) return null;
        var settings = await database.BotAiSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.CompanyId == companyId, cancellationToken).ConfigureAwait(false);
        if (settings?.ProtectedApiKey is null || string.IsNullOrWhiteSpace(settings.Model)) return null;
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var baseUrl) || baseUrl.Scheme != Uri.UriSchemeHttps)
            throw new AiProviderExecutionException("Company AI address must be an absolute HTTPS URL.", false);
        string key;
        try { key = await protector.UnprotectAsync(tenantId, companyId, settings.ProtectedApiKey, cancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException error) { throw new AiProviderExecutionException("Company AI key is unavailable.", false, error); }
        return new OpenAiCompatibleChatCompletionsAdapter(httpClient,
            new Uri($"{settings.BaseUrl.TrimEnd('/')}/chat/completions", UriKind.Absolute), settings.Model.Trim(), $"Bearer {key}", ProviderId);
    }
}

/// <summary>
/// Adapter for the OpenAI-compatible Chat Completions API, which Gemini, OpenAI and most
/// gateways accept. Like the Responses adapter it sends only the bounded model input.
/// </summary>
public sealed class OpenAiCompatibleChatCompletionsAdapter(
    HttpClient httpClient,
    Uri endpoint,
    string model,
    string authorizationHeader,
    string providerId,
    int maxOutputTokens = 1024) : IAiProviderAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ProviderId => providerId;

    public bool Supports(AiCapability capability) => capability == AiCapability.Reasoning;

    public async Task<AiGatewayResponse> ExecuteAsync(AiGatewayRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                model,
                messages = new[] { new { role = "user", content = request.Input } },
                max_tokens = maxOutputTokens
            }, options: JsonOptions)
        };
        if (authorizationHeader.Any(character => character is '\r' or '\n')
            || !message.Headers.TryAddWithoutValidation("Authorization", authorizationHeader))
            throw new AiProviderExecutionException("AI provider authorization header is invalid.", false);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException exception) { throw new AiProviderExecutionException("AI provider request timed out.", true, exception); }
        catch (HttpRequestException exception) { throw new AiProviderExecutionException("AI provider request failed before receiving a response.", true, exception); }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var transient = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                throw new AiProviderExecutionException($"AI provider returned HTTP {(int)response.StatusCode}.", transient);
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                var output = ReadOutputText(root);
                if (string.IsNullOrWhiteSpace(output) || output.Length > 64_000)
                    throw new InvalidOperationException("AI provider returned an empty or oversized text result.");
                var (inputTokens, outputTokens) = ReadUsage(root);
                return new AiGatewayResponse(request.RequestId, request.Capability, output, ReadString(root, "model") ?? model, inputTokens, outputTokens)
                {
                    ProviderId = providerId
                };
            }
            catch (JsonException exception) { throw new AiProviderExecutionException("AI provider returned malformed JSON.", false, exception); }
            catch (InvalidOperationException exception) { throw new AiProviderExecutionException("AI provider returned an incompatible response payload.", false, exception); }
        }
    }

    private static string ReadOutputText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new InvalidOperationException("AI response choices are missing.");
        var first = choices[0];
        if (!first.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("AI response message is missing.");
        return ReadString(message, "content") ?? "";
    }

    // Some compatible providers omit usage; a missing count is recorded as zero, never guessed.
    private static (long InputTokens, long OutputTokens) ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return (0, 0);
        var input = usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt64(out var a) && a >= 0 ? a : 0;
        var output = usage.TryGetProperty("completion_tokens", out var completion) && completion.TryGetInt64(out var b) && b >= 0 ? b : 0;
        return (input, output);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
