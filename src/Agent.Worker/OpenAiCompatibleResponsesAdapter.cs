using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Agent.Worker;

public sealed record OpenAiCompatibleResponsesOptions(
    Uri Endpoint,
    string Model,
    string AuthorizationHeader,
    string ProviderId,
    int MaxOutputTokens = 1024)
{
    public TimeSpan StructuredTimeout { get; init; } = TimeSpan.FromSeconds(60);

    // Explicitly owned development/test loopback only; never an arbitrary HTTP provider.
    public bool AllowInsecureLoopback { get; init; }

    public static OpenAiCompatibleResponsesOptions? FromEnvironment(
        string prefix = "AIOFFICE_AI",
        string defaultProviderId = "openai-compatible")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultProviderId);

        var baseUrl = Environment.GetEnvironmentVariable($"{prefix}_BASE_URL");
        var model = Environment.GetEnvironmentVariable($"{prefix}_MODEL");
        var authorization = Environment.GetEnvironmentVariable($"{prefix}_AUTHORIZATION");
        var providerId = Environment.GetEnvironmentVariable($"{prefix}_PROVIDER_ID");

        var configured = new[] { baseUrl, model, authorization }.Count(value => !string.IsNullOrWhiteSpace(value));
        if (configured == 0)
        {
            return null;
        }

        if (configured != 3)
        {
            throw new InvalidOperationException(
                $"AI runtime configuration must provide {prefix}_BASE_URL, {prefix}_MODEL and {prefix}_AUTHORIZATION together.");
        }

        baseUrl = baseUrl!.Trim();
        model = model!.Trim();
        authorization = authorization!.Trim();
        providerId = string.IsNullOrWhiteSpace(providerId) ? defaultProviderId : providerId.Trim();

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"{prefix}_BASE_URL must be an absolute HTTP or HTTPS URL.");
        }

        if (string.IsNullOrWhiteSpace(model)
            || model.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(providerId)
            || providerId.Any(char.IsControl)
            || authorization.Any(character => character is '\r' or '\n'))
        {
            throw new InvalidOperationException("AI runtime configuration contains invalid canonical values.");
        }

        var endpoint = new Uri($"{baseUrl.TrimEnd('/')}/responses", UriKind.Absolute);
        var allowLoopback = Environment.GetEnvironmentVariable($"{prefix}_ALLOW_INSECURE_LOOPBACK") == "true";
        var result = new OpenAiCompatibleResponsesOptions(endpoint, model, authorization, providerId)
        {
            AllowInsecureLoopback = allowLoopback
        };
        StructuredResponsesPolicy.ValidateOptions(result);
        return result;
    }
}

public sealed class AiProviderExecutionException(
    string message,
    bool isTransient,
    Exception? innerException = null) : Exception(message, innerException)
{
    public bool IsTransient { get; } = isTransient;
}

/// <summary>
/// Ordered primary/backup gateway. Backup is eligible only after a retryable primary failure.
/// Permanent primary failures (for example invalid credentials or malformed requests) fail closed
/// so a configuration error cannot be silently hidden by another provider.
/// </summary>
public sealed class OrderedFailoverAiGateway(
    IAiProviderAdapter primary,
    IAiProviderAdapter? backup = null) : IAiGateway
{
    public async Task<AiGatewayResponse> ExecuteAsync(
        AiGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        if (!primary.Supports(request.Capability))
        {
            throw new InvalidOperationException(
                $"Primary AI provider does not support capability {request.Capability}.");
        }

        if (backup is not null)
        {
            if (string.Equals(primary.ProviderId, backup.ProviderId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Primary and backup AI provider identities must differ.");
            }

            if (!backup.Supports(request.Capability))
            {
                throw new InvalidOperationException(
                    $"Backup AI provider does not support capability {request.Capability}.");
            }
        }

        try
        {
            return await ExecuteAndValidateAsync(primary, request, cancellationToken).ConfigureAwait(false);
        }
        catch (AiProviderExecutionException primaryFailure) when (primaryFailure.IsTransient && backup is not null)
        {
            try
            {
                return await ExecuteAndValidateAsync(backup, request, cancellationToken).ConfigureAwait(false);
            }
            catch (AiProviderExecutionException backupFailure)
            {
                throw new AiProviderExecutionException(
                    "Primary and backup AI providers both failed.",
                    backupFailure.IsTransient,
                    new AggregateException(primaryFailure, backupFailure));
            }
        }
    }

    private static async Task<AiGatewayResponse> ExecuteAndValidateAsync(
        IAiProviderAdapter adapter,
        AiGatewayRequest request,
        CancellationToken cancellationToken)
    {
        var response = await adapter.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(response);
        response.ValidateFor(request);

        if (!string.Equals(response.ProviderId, adapter.ProviderId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AI provider response identity does not match the selected adapter.");
        }

        return response;
    }
}

/// <summary>
/// Minimal provider adapter for the OpenAI-compatible Responses API. The adapter deliberately sends
/// only the bounded model input. Tenant/company/database credentials and secret references are never
/// serialized into the provider request.
/// </summary>
public sealed class OpenAiCompatibleResponsesAdapter(
    HttpClient httpClient,
    OpenAiCompatibleResponsesOptions options) : IAiProviderAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly BoundedStructuredResponses structured = new(httpClient, options);

    public string ProviderId => options.ProviderId;

    public bool Supports(AiCapability capability) =>
        capability is AiCapability.Reasoning or AiCapability.StructuredGeneration;

    public async Task<AiGatewayResponse> ExecuteAsync(
        AiGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (!Supports(request.Capability))
        {
            throw new AiProviderExecutionException("AI capability is unsupported.", false);
        }

        if (request.Capability == AiCapability.StructuredGeneration)
        {
            return await structured.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
        {
            Content = JsonContent.Create(new
            {
                model = options.Model,
                input = request.Input,
                store = false,
                max_output_tokens = options.MaxOutputTokens
            }, options: JsonOptions)
        };
        if (!message.Headers.TryAddWithoutValidation("Authorization", options.AuthorizationHeader))
        {
            throw new AiProviderExecutionException("AI provider authorization header is invalid.", false);
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new AiProviderExecutionException("AI provider request timed out.", true, exception);
        }
        catch (HttpRequestException exception)
        {
            throw new AiProviderExecutionException("AI provider request failed before receiving a response.", true, exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var transient = response.StatusCode is HttpStatusCode.RequestTimeout
                    or HttpStatusCode.TooManyRequests
                    || (int)response.StatusCode >= 500;
                throw new AiProviderExecutionException(
                    $"AI provider returned HTTP {(int)response.StatusCode}.",
                    transient);
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                var output = ReadOutputText(root);
                var model = ReadOptionalString(root, "model") ?? options.Model;
                var (inputTokens, outputTokens) = ReadUsage(root);

                if (string.IsNullOrWhiteSpace(output) || output.Length > 64_000)
                {
                    throw new InvalidOperationException("AI provider returned an empty or oversized text result.");
                }

                return new AiGatewayResponse(
                    request.RequestId,
                    request.Capability,
                    output,
                    model,
                    inputTokens,
                    outputTokens)
                {
                    ProviderId = options.ProviderId
                };
            }
            catch (JsonException exception)
            {
                throw new AiProviderExecutionException("AI provider returned malformed JSON.", false, exception);
            }
            catch (InvalidOperationException exception)
            {
                throw new AiProviderExecutionException("AI provider returned an incompatible response payload.", false, exception);
            }
        }
    }

    private static string ReadOutputText(JsonElement root)
    {
        var topLevel = ReadOptionalString(root, "output_text");
        if (!string.IsNullOrWhiteSpace(topLevel))
        {
            return topLevel;
        }

        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("AI response output collection is missing.");
        }

        var text = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in content.EnumerateArray())
            {
                if (ReadOptionalString(part, "type") != "output_text")
                {
                    continue;
                }

                var value = ReadOptionalString(part, "text");
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (text.Length > 0)
                {
                    text.AppendLine();
                }

                text.Append(value);
            }
        }

        return text.ToString();
    }

    private static (long InputTokens, long OutputTokens) ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object
            || !usage.TryGetProperty("input_tokens", out var input)
            || !input.TryGetInt64(out var inputTokens)
            || !usage.TryGetProperty("output_tokens", out var output)
            || !output.TryGetInt64(out var outputTokens)
            || inputTokens < 0
            || outputTokens < 0)
        {
            throw new InvalidOperationException("AI response usage metadata is missing or invalid.");
        }

        return (inputTokens, outputTokens);
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
