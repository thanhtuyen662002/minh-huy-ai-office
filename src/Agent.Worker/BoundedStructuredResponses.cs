using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Agent.Worker;

internal sealed class BoundedStructuredResponses(HttpClient client, OpenAiCompatibleResponsesOptions options)
{
    private readonly SemaphoreSlim capacity = new(StructuredResponsesPolicy.OperationLimit);

    public async Task<AiGatewayResponse> ExecuteAsync(AiGatewayRequest request, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        StructuredResponsesPolicy.ValidateOptions(options);
        if (!capacity.Wait(0)) throw new AiProviderExecutionException("AI structured capacity is occupied.", true);
        var operation = new OwnedCancellation();
        Task<AiGatewayResponse> pending;
        try
        {
            // Run synchronous injected headers/content/cleanup off the caller; the permit belongs to this whole task.
            pending = Task.Run(async () =>
            {
                Prepared? prepared = null;
                try
                {
                    try { prepared = Prepare(request); }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        throw new AiProviderExecutionException("AI structured input is invalid.", false);
                    }
                    return await RunAsync(request, prepared, operation.Token).ConfigureAwait(false);
                }
                catch (AiProviderExecutionException) { throw; }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    throw new AiProviderExecutionException("AI structured transport did not complete.", true);
                }
                finally
                {
                    await operation.FinishAsync().ConfigureAwait(false);
                    if (prepared is not null)
                    {
                        CryptographicOperations.ZeroMemory(prepared.Body);
                        CryptographicOperations.ZeroMemory(prepared.Schema);
                    }
                    capacity.Release();
                }
            }, CancellationToken.None);
        }
        catch
        {
            await operation.FinishAsync().ConfigureAwait(false);
            capacity.Release();
            throw new AiProviderExecutionException("AI structured operation could not start.", true);
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
            throw new AiProviderExecutionException("AI structured deadline expired.", true);
        }
    }

    private Prepared Prepare(AiGatewayRequest request)
    {
        StructuredResponsesPolicy.ValidateOptions(options);
        var inputBytes = StructuredResponsesPolicy.BoundedText(request.Input, StructuredResponsesPolicy.InputByteLimit);
        CryptographicOperations.ZeroMemory(inputBytes);
        var schemaBytes = StructuredResponsesPolicy.BoundedText(request.ResponseSchema!, StructuredResponsesPolicy.SchemaByteLimit);
        try
        {
            using var schema = StructuredResponsesPolicy.Parse(schemaBytes);
            StructuredResponsesPolicy.ValidateSchema(schema.RootElement);
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                model = options.Model,
                input = request.Input,
                store = false,
                max_output_tokens = options.MaxOutputTokens,
                text = new { format = new { type = "json_schema", name = "group_work_notes_v1", strict = true, schema = schema.RootElement } }
            });
            return new Prepared(body, schemaBytes);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(schemaBytes);
            throw;
        }
    }

    private async Task<AiGatewayResponse> RunAsync(AiGatewayRequest request, Prepared prepared, CancellationToken cancellation)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, options.Endpoint) { Content = new ByteArrayContent(prepared.Body) };
        message.Content.Headers.ContentType = new("application/json");
        if (!message.Headers.TryAddWithoutValidation("Authorization", options.AuthorizationHeader))
            throw new AiProviderExecutionException("AI structured authorization is invalid.", false);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new AiProviderExecutionException("AI structured provider refused the request.",
                response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500);
        try
        {
            if (response.Content.Headers.ContentEncoding.Count != 0) throw new InvalidOperationException();
            var declared = response.Content.Headers.ContentLength;
            if (declared is < 0 or > StructuredResponsesPolicy.EntityByteLimit) throw new InvalidOperationException();
            using var sink = new FiniteResponseSink(cancellation);
            // Unlike generic ReadAsStreamAsync, this route never asks content to prebuffer into an unbounded stream.
            await response.Content.CopyToAsync(sink, cancellation).ConfigureAwait(false);
            var bytes = sink.Seal();
            cancellation.ThrowIfCancellationRequested();
            if (bytes.Length == 0 || (declared is not null && declared != bytes.Length)) throw new InvalidOperationException();
            StructuredResponsesPolicy.StrictUtf8.GetCharCount(bytes.Span);
            using var envelope = StructuredResponsesPolicy.Parse(bytes);
            var root = envelope.RootElement;
            if (root.ValueKind != JsonValueKind.Object || String(root, "status") != "completed"
                || NonNull(root, "error") || NonNull(root, "incomplete_details")) throw new InvalidOperationException();
            var output = ReadAssistant(root);
            var outputBytes = StructuredResponsesPolicy.BoundedText(output, StructuredResponsesPolicy.OutputByteLimit);
            try
            {
                using var outputDocument = StructuredResponsesPolicy.Parse(outputBytes);
                if (outputDocument.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
                using var schema = StructuredResponsesPolicy.Parse(prepared.Schema);
                StructuredResponsesPolicy.ValidateOutput(schema.RootElement, outputDocument.RootElement);
            }
            finally { CryptographicOperations.ZeroMemory(outputBytes); }
            var model = String(root, "model");
            if (!StructuredResponsesPolicy.Canonical(model, 256)) throw new InvalidOperationException();
            var usage = root.GetProperty("usage");
            var input = usage.GetProperty("input_tokens").GetInt64();
            var generated = usage.GetProperty("output_tokens").GetInt64();
            var total = usage.GetProperty("total_tokens").GetInt64();
            if (input < 0 || generated < 0 || generated > options.MaxOutputTokens || total != checked(input + generated))
                throw new InvalidOperationException();
            cancellation.ThrowIfCancellationRequested();
            return new(request.RequestId, request.Capability, output, model, input, generated) { ProviderId = options.ProviderId };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new AiProviderExecutionException("AI structured response is invalid.", false);
        }
    }

    private static string ReadAssistant(JsonElement root)
    {
        var collection = root.GetProperty("output");
        if (collection.ValueKind != JsonValueKind.Array) throw new InvalidOperationException();
        string? result = null;
        foreach (var item in collection.EnumerateArray())
        {
            if (String(item, "type") == "reasoning") continue;
            if (String(item, "type") != "message" || String(item, "role") != "assistant"
                || String(item, "status") != "completed" || result is not null) throw new InvalidOperationException();
            var content = item.GetProperty("content");
            if (content.ValueKind != JsonValueKind.Array || content.GetArrayLength() != 1) throw new InvalidOperationException();
            var part = content[0];
            if (String(part, "type") != "output_text") throw new InvalidOperationException();
            result = String(part, "text");
        }
        return result ?? throw new InvalidOperationException();
    }

    private static string String(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw new InvalidOperationException();
    private static bool NonNull(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;
    private sealed record Prepared(byte[] Body, byte[] Schema);

    private sealed class OwnedCancellation
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource source = new();
        private Task? cancellation;
        private bool finished;
        public CancellationToken Token => source.Token;

        public void RequestCancellation()
        {
            lock (gate)
            {
                if (!finished && cancellation is null) cancellation = source.CancelAsync();
            }
        }

        public async Task FinishAsync()
        {
            Task? pending;
            lock (gate) { finished = true; pending = cancellation; }
            if (pending is not null)
            {
                try { await pending.ConfigureAwait(false); }
                catch { /* Cancellation callbacks are untrusted; never log their private errors. */ }
            }
            source.Dispose();
        }
    }
}
