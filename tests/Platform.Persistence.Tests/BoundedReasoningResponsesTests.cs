extern alias RuntimeWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using MinhHuyAiOffice.Shared.Contracts;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

// These controls deliberately block four pool workers. Isolate their real
// deadline and recovery assertions from unrelated parallel suite activity.
[Collection(nameof(BoundedReasoningTimingCollection))]
public sealed class BoundedReasoningResponsesTests
{
    private static AiGatewayRequest Request(string input = "owned synthetic input") =>
        new("private-tenant-id", "private-company-id", "private-task-id", "private-request-id", AiCapability.Reasoning, input);
    private static OpenAiCompatibleResponsesOptions Options(TimeSpan? timeout = null) => new(
        new("https://provider.example/v1/responses"), "owned-model", "Bearer synthetic-test-secret", "owned-provider")
    { StructuredTimeout = timeout ?? TimeSpan.FromSeconds(2) };
    private static string Envelope(string text = "ghi nhận 😀") => JsonSerializer.Serialize(new
    {
        model = "owned-model",
        output_text = text,
        usage = new { input_tokens = 3, output_tokens = 2 }
    });
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Actual_adapter_bounds_input_before_http_and_reuses_existing_request_without_scope_secrets()
    {
        string? body = null;
        var handler = new Handler(async (message, token) =>
        {
            body = await message.Content!.ReadAsStringAsync(token);
            Assert.Equal("Bearer synthetic-test-secret", message.Headers.GetValues("Authorization").Single());
            return Response(Envelope());
        });
        using var client = new HttpClient(handler);
        var adapter = new OpenAiCompatibleResponsesAdapter(client, Options(TimeSpan.FromSeconds(30)));
        var input = new string('中', 42666) + "ab";
        Assert.Equal(128000, Encoding.UTF8.GetByteCount(input));
        var response = await adapter.ExecuteAsync(Request(input));
        response.ValidateFor(Request(input));
        using var document = JsonDocument.Parse(body!);
        Assert.Equal(4, document.RootElement.EnumerateObject().Count());
        Assert.Equal(input, document.RootElement.GetProperty("input").GetString());
        Assert.Equal(1024, document.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.False(document.RootElement.GetProperty("store").GetBoolean());
        foreach (var secret in new[] { "private-tenant-id", "private-company-id", "private-task-id", "private-request-id", "synthetic-test-secret" })
            Assert.DoesNotContain(secret, body!, StringComparison.Ordinal);
        foreach (var invalid in new[] { input + "a", "broken" + '\uD800' })
        {
            var failure = await Assert.ThrowsAsync<AiProviderExecutionException>(() => adapter.ExecuteAsync(Request(invalid)));
            Assert.False(failure.IsTransient);
            Assert.Null(failure.InnerException);
        }
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Oversized_unknown_body_stops_transfer_without_reading_entire_two_megabyte_padding(bool known)
    {
        var total = 2_000_000;
        var bytesOffered = 0;
        var copies = 0;
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ScriptContent(async stream =>
            {
                Interlocked.Increment(ref copies);
                var prefix = Encoding.UTF8.GetBytes(Envelope());
                await stream.WriteAsync(prefix);
                Interlocked.Add(ref bytesOffered, prefix.Length);
                var padding = new byte[8192]; Array.Fill(padding, (byte)' ');
                while (Volatile.Read(ref bytesOffered) < total)
                {
                    Interlocked.Add(ref bytesOffered, padding.Length);
                    await stream.WriteAsync(padding);
                }
            }, known ? total : null)
        }));
        using var client = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<AiProviderExecutionException>(() =>
            new OpenAiCompatibleResponsesAdapter(client, Options(TimeSpan.FromSeconds(30))).ExecuteAsync(Request()));
        Assert.False(failure.IsTransient);
        Assert.Null(failure.InnerException);
        Assert.Equal(known ? 0 : 1, copies);
        Assert.InRange(bytesOffered, known ? 0 : 524289, known ? 0 : 524288 + 8192);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unpaired-unicode")]
    [InlineData("negative-usage")]
    [InlineData("float-usage")]
    [InlineData("output-tokens")]
    [InlineData("invalid-model")]
    [InlineData("empty-output")]
    public async Task Malformed_metadata_is_permanent_sanitized_and_cannot_advance_to_backup(string failure)
    {
        var body = Envelope();
        body = failure switch
        {
            "duplicate" => body.Insert(1, "\"output_text\":\"PRIVATE-PROVIDER-CANARY\","),
            "unpaired-unicode" => "{\"output_text\":\"\\uD800\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}",
            "negative-usage" => body.Replace("\"input_tokens\":3", "\"input_tokens\":-1", StringComparison.Ordinal),
            "float-usage" => body.Replace("\"input_tokens\":3", "\"input_tokens\":1.5", StringComparison.Ordinal),
            "output-tokens" => body.Replace("\"output_tokens\":2", "\"output_tokens\":1025", StringComparison.Ordinal),
            "invalid-model" => body.Replace("owned-model", " owned-model", StringComparison.Ordinal),
            _ => Envelope(" ")
        };
        var handler = new Handler((_, _) => Task.FromResult(Response(body)));
        using var client = new HttpClient(handler);
        var gateway = new OrderedFailoverAiGateway(new OpenAiCompatibleResponsesAdapter(client, Options()),
            new OpenAiCompatibleResponsesAdapter(client, Options() with { ProviderId = "backup-provider" }));
        var error = await Assert.ThrowsAsync<AiProviderExecutionException>(() => gateway.ExecuteAsync(Request()));
        Assert.False(error.IsTransient);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("PRIVATE-PROVIDER-CANARY", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false, 'a')]
    [InlineData(true, 'a')]
    [InlineData(false, '中')]
    [InlineData(true, '中')]
    public async Task Both_legacy_response_shapes_accept_64000_utf16_and_refuse_the_next_character(bool array, char value)
    {
        var text = new string(value, 64000);
        string Body() => array ? JsonSerializer.Serialize(new
        {
            output = new[] { new { content = new[] { new { type = "output_text", text } } } },
            usage = new { input_tokens = 3, output_tokens = 2 }
        }) : Envelope(text);
        if (value == '中') Assert.True(Encoding.UTF8.GetByteCount(Body()) > 262144);
        var handler = new Handler((_, _) => Task.FromResult(Response(Body())));
        using var client = new HttpClient(handler);
        var adapter = new OpenAiCompatibleResponsesAdapter(client, Options(TimeSpan.FromSeconds(30)));
        var result = await adapter.ExecuteAsync(Request());
        Assert.Equal(text, result.Output);
        Assert.Equal("owned-model", result.Model);
        text += "a";
        var failure = await Assert.ThrowsAsync<AiProviderExecutionException>(() => adapter.ExecuteAsync(Request()));
        Assert.False(failure.IsTransient);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_entity_exact_limit_and_plus_one_with_known_or_unknown_length(bool known)
    {
        var envelope = Encoding.UTF8.GetBytes(Envelope());
        var exact = new byte[524288]; Array.Fill(exact, (byte)' '); envelope.CopyTo(exact, 0);
        byte[] bytes = exact;
        var copies = 0;
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ScriptContent(stream => { Interlocked.Increment(ref copies); return stream.WriteAsync(bytes).AsTask(); }, known ? bytes.Length : null)
        }));
        using var client = new HttpClient(handler);
        // This tests exact entity bounds. Its finite deadline must tolerate the
        // intentionally blocking tests running in the same parallel CI suite.
        // Dedicated 250ms deadline/capacity/cancellation probes retain their budgets.
        var adapter = new OpenAiCompatibleResponsesAdapter(client, Options(TimeSpan.FromSeconds(30)));
        await adapter.ExecuteAsync(Request());
        Assert.Equal(1, copies);
        bytes = exact.Concat(new byte[] { (byte)' ' }).ToArray();
        var error = await Assert.ThrowsAsync<AiProviderExecutionException>(() => adapter.ExecuteAsync(Request()));
        Assert.False(error.IsTransient);
        Assert.Null(error.InnerException);
        Assert.Equal(known ? "AI reasoning response is invalid." : "AI reasoning body transfer failed.", error.Message);
        Assert.Equal(known ? 1 : 2, copies);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Every_actual_copy_write_overload_cannot_swallow_overflow(int overload)
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ScriptContent(async stream =>
            {
                var valid = Encoding.UTF8.GetBytes(Envelope());
                await stream.WriteAsync(valid);
                var excess = new byte[524289];
                try
                {
                    switch (overload)
                    {
                        case 0: stream.Write(excess, 0, excess.Length); break;
                        case 1: stream.Write(excess.AsSpan()); break;
                        case 2: for (var index = 0; index < excess.Length; index++) stream.WriteByte(32); break;
                        case 3: await stream.WriteAsync(excess, 0, excess.Length); break;
                        default: await stream.WriteAsync(excess.AsMemory()); break;
                    }
                }
                catch (IOException) { /* An untrusted content serializer may suppress the write exception. */ }
            })
        }));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<AiProviderExecutionException>(() => new OpenAiCompatibleResponsesAdapter(client, Options()).ExecuteAsync(Request()));
    }

    [Theory]
    [InlineData("short-length")]
    [InlineData("long-length")]
    [InlineData("truncated")]
    [InlineData("invalid-utf8")]
    [InlineData("deep-json")]
    [InlineData("encoding")]
    public async Task Actual_body_length_utf8_depth_and_encoding_refusals(string failure)
    {
        var bytes = Encoding.UTF8.GetBytes(Envelope());
        long? declared = null;
        if (failure == "short-length") declared = bytes.Length - 1;
        if (failure == "long-length") declared = bytes.Length + 1;
        if (failure == "truncated") bytes = bytes[..^1];
        if (failure == "invalid-utf8") bytes = bytes.Concat(new byte[] { 0xC3, 0x28 }).ToArray();
        if (failure == "deep-json") bytes = Encoding.UTF8.GetBytes(new string('[', 33) + "0" + new string(']', 33));
        var handler = new Handler((_, _) =>
        {
            var content = new ScriptContent(stream => stream.WriteAsync(bytes).AsTask(), declared);
            if (failure == "encoding") content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<AiProviderExecutionException>(() => new OpenAiCompatibleResponsesAdapter(client, Options()).ExecuteAsync(Request()));
        Assert.Null(error.InnerException);
        Assert.False(error.IsTransient);
    }

    [Theory]
    [InlineData("headers")]
    [InlineData("body")]
    [InlineData("eof")]
    [InlineData("dispose")]
    [InlineData("callback")]
    public async Task Total_deadline_retains_all_four_owned_slots_until_noncooperative_work_and_cleanup_finish(string stage)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0; var disposed = 0;
        void EnterStage()
        {
            if (Interlocked.Increment(ref entered) == 4) allEntered.TrySetResult();
        }
        var handler = new Handler(async (_, _) =>
        {
            if (stage == "headers") { EnterStage(); await gate.Task; }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ScriptContent(async stream =>
                {
                    if (stage == "body") { EnterStage(); await gate.Task; }
                    if (stage == "callback") await gate.Task;
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(Envelope()));
                    if (stage == "eof") { EnterStage(); await gate.Task; }
                }, dispose: () =>
                {
                    if (stage == "dispose") { EnterStage(); gate.Task.GetAwaiter().GetResult(); }
                    Interlocked.Increment(ref disposed);
                }, onCopy: token =>
                {
                    // HttpClient's SendAsync token can be unlinked once headers
                    // return. Exercise the actual bounded body's cancellation.
                    if (stage == "callback") token.Register(() => { EnterStage(); gate.Task.GetAwaiter().GetResult(); });
                })
            };
        });
        using var client = new HttpClient(handler);
        var adapter = new OpenAiCompatibleResponsesAdapter(client, Options(TimeSpan.FromMilliseconds(250)));
        var calls = Enumerable.Range(0, 4).Select(_ => adapter.ExecuteAsync(Request())).ToArray();
        try
        {
            // Bound the callers independently of waiting for all four Task.Run
            // delegates to reach their deliberately noncooperative stage. Pool
            // startup under parallel test load is not provider deadline latency.
            var failures = await Task.WhenAll(calls.Select(call =>
                Assert.ThrowsAsync<AiProviderExecutionException>(() => call))).WaitAsync(TimeSpan.FromSeconds(3));
            foreach (var failure in failures)
            {
                Assert.Contains("deadline", failure.Message, StringComparison.Ordinal);
                Assert.Null(failure.InnerException);
            }
            await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Equal(4, Volatile.Read(ref entered));
            Assert.Equal(0, Volatile.Read(ref disposed));
            Assert.False(gate.Task.IsCompleted);
            var occupied = await Assert.ThrowsAsync<AiProviderExecutionException>(() => adapter.ExecuteAsync(Request()));
            Assert.Contains("capacity", occupied.Message, StringComparison.Ordinal);
            Assert.Equal(4, handler.Calls);
        }
        finally
        {
            gate.TrySetResult();
            await Until(() => Volatile.Read(ref disposed) == 4);
        }
        await EventuallySuccess(adapter);
        Assert.Equal(5, handler.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_identity_returns_before_provider_finishes_then_recovers()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (_, _) => { started.TrySetResult(); await gate.Task; return Response(Envelope()); });
        using var client = new HttpClient(handler);
        using var caller = new CancellationTokenSource();
        var adapter = new OpenAiCompatibleResponsesAdapter(client, Options());
        var pending = adapter.ExecuteAsync(Request(), caller.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        caller.Cancel();
        try
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(caller.Token, error.CancellationToken);
        }
        finally { gate.TrySetResult(); }
        await EventuallySuccess(adapter);
    }

    [Fact]
    public async Task Synchronous_stalled_handler_is_inside_owned_deadline_and_late_private_fault_is_sanitized()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler((_, _) =>
        {
            entered.TrySetResult(); gate.Task.GetAwaiter().GetResult();
            throw new HttpRequestException("PRIVATE-LATE-NETWORK-CANARY");
        });
        using var client = new HttpClient(handler);
        var pending = new OpenAiCompatibleResponsesAdapter(client, Options(TimeSpan.FromMilliseconds(250))).ExecuteAsync(Request());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var failure = await Assert.ThrowsAsync<AiProviderExecutionException>(() => pending);
            Assert.Equal("AI reasoning deadline expired.", failure.Message);
            Assert.Null(failure.InnerException);
        }
        finally { gate.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Permanent_primary_denial_survives_disposal_failure_without_backup(bool malformed)
    {
        var count = 0;
        var handler = new Handler((_, _) =>
        {
            if (Interlocked.Increment(ref count) > 1) return Task.FromResult(Response(Envelope()));
            return Task.FromResult(new HttpResponseMessage(malformed ? HttpStatusCode.OK : HttpStatusCode.Unauthorized)
            {
                Content = new ScriptContent(stream => stream.WriteAsync(Encoding.UTF8.GetBytes("{malformed")).AsTask(),
                    dispose: () => throw new IOException("PRIVATE-DISPOSE-CANARY"))
            });
        });
        using var client = new HttpClient(handler);
        var gateway = new OrderedFailoverAiGateway(new OpenAiCompatibleResponsesAdapter(client, Options()),
            new OpenAiCompatibleResponsesAdapter(client, Options() with { ProviderId = "backup-provider" }));
        var error = await Assert.ThrowsAsync<AiProviderExecutionException>(() => gateway.ExecuteAsync(Request()));
        Assert.False(error.IsTransient);
        Assert.Equal(1, handler.Calls);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("PRIVATE-DISPOSE-CANARY", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Body_network_IO_can_use_backup_but_sticky_host_overflow_cannot(bool overflowFirst)
    {
        var count = 0;
        var handler = new Handler((_, _) =>
        {
            if (Interlocked.Increment(ref count) > 1) return Task.FromResult(Response(Envelope()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ScriptContent(async stream =>
                {
                    if (overflowFirst)
                    {
                        try { await stream.WriteAsync(new byte[524289]); }
                        catch (IOException) { }
                    }
                    throw new IOException("PRIVATE-BODY-NETWORK-CANARY");
                })
            });
        });
        using var client = new HttpClient(handler);
        var gateway = new OrderedFailoverAiGateway(new OpenAiCompatibleResponsesAdapter(client, Options()),
            new OpenAiCompatibleResponsesAdapter(client, Options() with { ProviderId = "backup-provider" }));
        if (overflowFirst)
        {
            var failure = await Assert.ThrowsAsync<AiProviderExecutionException>(() => gateway.ExecuteAsync(Request()));
            Assert.False(failure.IsTransient);
            Assert.Null(failure.InnerException);
            Assert.Equal(1, handler.Calls);
        }
        else
        {
            var result = await gateway.ExecuteAsync(Request());
            Assert.Equal("backup-provider", result.ProviderId);
            Assert.Equal(2, handler.Calls);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(10, limit.Token);
    }
    private static async Task EventuallySuccess(OpenAiCompatibleResponsesAdapter adapter)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (true)
        {
            try { await adapter.ExecuteAsync(Request(), limit.Token); return; }
            catch (AiProviderExecutionException error) when (error.Message.Contains("capacity", StringComparison.Ordinal))
            { await Task.Delay(10, limit.Token); }
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        { Interlocked.Increment(ref calls); return action(message, token); }
    }
    private sealed class ScriptContent(Func<Stream, Task> write, long? declared = null, Action? dispose = null,
        Action<CancellationToken>? onCopy = null) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => write(stream);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        { onCopy?.Invoke(cancellationToken); return write(stream); }
        protected override Task<Stream> CreateContentReadStreamAsync() => throw new InvalidOperationException("Unbounded stream factory must never be called.");
        protected override bool TryComputeLength(out long length) { length = declared ?? 0; return declared.HasValue; }
        protected override void Dispose(bool disposing) { if (disposing) dispose?.Invoke(); base.Dispose(disposing); }
    }
}

[CollectionDefinition(nameof(BoundedReasoningTimingCollection), DisableParallelization = true)]
public sealed class BoundedReasoningTimingCollection
{
}
