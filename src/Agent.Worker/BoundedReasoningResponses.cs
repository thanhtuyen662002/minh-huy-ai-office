using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Agent.Worker;

internal sealed class BoundedReasoningResponses(HttpClient client, OpenAiCompatibleResponsesOptions options)
{
    internal const int EntityByteLimit = 524_288;
    private readonly SemaphoreSlim capacity = new(StructuredResponsesPolicy.OperationLimit);

    public async Task<AiGatewayResponse> ExecuteAsync(AiGatewayRequest request, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        StructuredResponsesPolicy.ValidateOptions(options);
        if (!capacity.Wait(0)) throw new AiProviderExecutionException("AI reasoning capacity is occupied.", true);
        var operation = new BoundedStructuredResponses.OwnedCancellation();
        Task<AiGatewayResponse> pending;
        try
        {
            // Run synchronous injected headers/content/cleanup off the caller; the permit belongs to this whole task.
            pending = Task.Run(async () =>
            {
                byte[]? prepared = null;
                try
                {
                    try { prepared = Prepare(request); }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        throw new AiProviderExecutionException("AI reasoning input is invalid.", false);
                    }
                    return await RunAsync(request, prepared, operation.Token).ConfigureAwait(false);
                }
                catch (AiProviderExecutionException) { throw; }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    throw new AiProviderExecutionException("AI reasoning transport did not complete.", true);
                }
                finally
                {
                    await operation.FinishAsync().ConfigureAwait(false);
                    if (prepared is not null)
                    {
                        CryptographicOperations.ZeroMemory(prepared);
                    }
                    capacity.Release();
                }
            }, CancellationToken.None);
        }
        catch
        {
            await operation.FinishAsync().ConfigureAwait(false);
            capacity.Release();
            throw new AiProviderExecutionException("AI reasoning operation could not start.", true);
        }

        // Observe late faults without logging private exception data. This continuation cannot free capacity.
        _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            var result = await pending.WaitAsync(options.StructuredTimeout, caller).ConfigureAwait(false);
            caller.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        {
            operation.RequestCancellation();
            throw new OperationCanceledException(caller);
        }
        catch (TimeoutException)
        {
            operation.RequestCancellation();
            throw new AiProviderExecutionException("AI reasoning deadline expired.", true);
        }
    }

    private byte[] Prepare(AiGatewayRequest request)
    {
        StructuredResponsesPolicy.ValidateOptions(options);
        var input = StructuredResponsesPolicy.BoundedText(request.Input, StructuredResponsesPolicy.InputByteLimit);
        CryptographicOperations.ZeroMemory(input);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = options.Model,
            input = request.Input,
            store = false,
            max_output_tokens = options.MaxOutputTokens
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private async Task<AiGatewayResponse> RunAsync(AiGatewayRequest request, byte[] body, CancellationToken cancellation)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, options.Endpoint) { Content = new ByteArrayContent(body) };
        HttpResponseMessage? response = null;
        var accepted = false;
        try
        {
            message.Content.Headers.ContentType = new("application/json");
            if (!message.Headers.TryAddWithoutValidation("Authorization", options.AuthorizationHeader))
                throw new AiProviderExecutionException("AI reasoning authorization is invalid.", false);
            response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new AiProviderExecutionException("AI reasoning provider refused the request.",
                    response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500);
            try
            {
                if (response.Content.Headers.ContentEncoding.Count != 0) throw new InvalidOperationException();
                var declared = response.Content.Headers.ContentLength;
                if (declared is < 0 or > EntityByteLimit) throw new InvalidOperationException();
                using var sink = new FiniteResponseSink(cancellation, EntityByteLimit);
                try { await response.Content.CopyToAsync(sink, cancellation).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (error is IOException or HttpRequestException)
                { throw new AiProviderExecutionException("AI reasoning body transfer failed.", !sink.HasFailed); }
                var bytes = sink.Seal();
                cancellation.ThrowIfCancellationRequested();
                if (bytes.Length == 0 || declared is not null && declared != bytes.Length) throw new InvalidOperationException();
                StructuredResponsesPolicy.StrictUtf8.GetCharCount(bytes.Span);
                using var document = StructuredResponsesPolicy.Parse(bytes);
                var root = document.RootElement;
                var output = ReadOutputText(root);
                var model = ReadOptionalString(root, "model") ?? options.Model;
                var (inputTokens, outputTokens) = ReadUsage(root);
                if (string.IsNullOrWhiteSpace(output) || output.Length > 64_000
                    || !StructuredResponsesPolicy.Canonical(model, 256) || outputTokens > options.MaxOutputTokens)
                    throw new InvalidOperationException();
                var result = new AiGatewayResponse(request.RequestId, request.Capability, output, model, inputTokens, outputTokens)
                { ProviderId = options.ProviderId };
                result.ValidateFor(request);
                cancellation.ThrowIfCancellationRequested();
                accepted = true;
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (AiProviderExecutionException) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { throw new AiProviderExecutionException("AI reasoning response is invalid.", false); }
        }
        finally
        {
            var cleanupFailed = false;
            try { response?.Dispose(); }
            catch (Exception error) when (error is not OutOfMemoryException) { cleanupFailed = true; }
            try { message.Dispose(); }
            catch (Exception error) when (error is not OutOfMemoryException) { cleanupFailed = true; }
            if (accepted && cleanupFailed)
                throw new AiProviderExecutionException("AI reasoning cleanup failed.", true);
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
