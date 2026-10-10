extern alias RuntimeWorker;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MinhHuyAiOffice.Shared.Contracts;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class StructuredResponsesAdapterTests
{
    private const string Schema = """{"type":"object","properties":{"note":{"type":"string"}},"required":["note"],"additionalProperties":false}""";
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static AiGatewayRequest Request(string input = "owned synthetic input", string schema = Schema) =>
        new("private-tenant-id", "private-company-id", "private-task-id", "private-request-id", AiCapability.StructuredGeneration, input, schema);
    private static OpenAiCompatibleResponsesOptions Options(TimeSpan? timeout = null) => new(
        new("https://provider.example/v1/responses"), "owned-model", "Bearer synthetic-test-secret", "owned-provider")
    { StructuredTimeout = timeout ?? TimeSpan.FromSeconds(2) };
    private static string Envelope(string text = "{\"note\":\"ghi nhận 😀\"}") => JsonSerializer.Serialize(new
    {
        status = "completed",
        model = "owned-model",
        output = new[] { new { type = "message", status = "completed", role = "assistant", content = new[] { new { type = "output_text", text } } } },
        usage = new { input_tokens = 3, output_tokens = 2, total_tokens = 5 }
    }, Relaxed);
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Actual_adapter_sends_schema_object_without_scope_secrets_and_accepts_completed_assistant()
    {
        string? body = null;
        var handler = new Handler(async (message, token) =>
        {
            body = await message.Content!.ReadAsStringAsync(token);
            Assert.Equal("Bearer synthetic-test-secret", message.Headers.GetValues("Authorization").Single());
            return Response(Envelope());
        });
        using var client = new HttpClient(handler);
        var adapter = new OpenAiCompatibleResponsesAdapter(client, Options());
        var result = await adapter.ExecuteAsync(Request());
        result.ValidateFor(Request());
        Assert.Equal("owned-provider", result.ProviderId);
        Assert.Equal("{\"note\":\"ghi nhận 😀\"}", result.Output);
        using var document = JsonDocument.Parse(body!);
        var root = document.RootElement;
        Assert.Equal(5, root.EnumerateObject().Count());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal("owned-model", root.GetProperty("model").GetString());
        var format = root.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.Equal(JsonValueKind.Object, format.GetProperty("schema").ValueKind);
        foreach (var secret in new[] { "private-tenant-id", "private-company-id", "private-task-id", "private-request-id", "synthetic-test-secret" })
            Assert.DoesNotContain(secret, body!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AiCapability.Embedding)]
    [InlineData(AiCapability.Vision)]
    [InlineData(AiCapability.Transcription)]
    public async Task Unsupported_capability_does_not_start_http(AiCapability capability)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(Envelope())));
        using var client = new HttpClient(handler);
        var adapter = new OpenAiCompatibleResponsesAdapter(client, Options());
        Assert.False(adapter.Supports(capability));
        await Assert.ThrowsAsync<AiProviderExecutionException>(() => adapter.ExecuteAsync(Request() with { Capability = capability }));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("{\"type\":\"array\",\"items\":{\"type\":\"string\"}}")]
    [InlineData("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":true}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"note\":{\"type\":\"string\"}},\"required\":[],\"additionalProperties\":false}")]
    [InlineData("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false,\"allOf\":[]}")]
    [InlineData("{\"type\":\"object\",\"typ\\u0065\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"note\":{\"type\":\"string\",\"enum\":[\"x\",\"\\u0078\"]}},\"required\":[\"note\"],\"additionalProperties\":false}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"note\":{\"type\":\"string\",\"enum\":[1]}},\"required\":[\"note\"],\"additionalProperties\":false}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"note\":{\"type\":[\"string\",\"string\"]}},\"required\":[\"note\"],\"additionalProperties\":false}")]
    [InlineData("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false,\"description\":\"\\uD800\"}")]
    public async Task Invalid_schema_fails_before_http(string schema)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(Envelope())));
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<AiProviderExecutionException>(() => new OpenAiCompatibleResponsesAdapter(client, Options()).ExecuteAsync(Request(schema: schema)));
        Assert.False(exception.IsTransient);
        Assert.Null(exception.InnerException);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_input_and_schema_byte_limits_are_distinct_and_plus_one_refuses(bool schemaBoundary)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(Envelope())));
        using var client = new HttpClient(handler);
        var adapter = new OpenAiCompatibleResponsesAdapter(client, Options());
        var exact = schemaBoundary ? Request(schema: Schema + new string(' ', 32000 - Encoding.UTF8.GetByteCount(Schema)))
            : Request(input: new string('中', 42666) + "ab"); //128000 actual UTF8 bytes.
        await adapter.ExecuteAsync(exact);
        var excess = schemaBoundary ? exact with { ResponseSchema = exact.ResponseSchema + " " } : exact with { Input = exact.Input + "a" };
        await Assert.ThrowsAsync<AiProviderExecutionException>(() => adapter.ExecuteAsync(excess));
        Assert.Equal(1, handler.Calls);
        await Assert.ThrowsAsync<AiProviderExecutionException>(() => adapter.ExecuteAsync(Request(input: "broken" + '\uD800')));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"note\":1}")]
    [InlineData("{\"note\":\"x\",\"extra\":true}")]
    [InlineData("{\"note\":\"x\",\"n\\u006fte\":\"y\"}")]
    [InlineData("{\"note\":\"\\uD800\"}")]
    [InlineData("{\"note\":\"\\uDC00\"}")]
    [InlineData("{\"note\":\"\\uD800x\"}")]
    [InlineData("{\"note\":\"x\"}")]
    public async Task Strict_output_schema_unicode_and_enum_are_checked(string output)
    {
        var schema = Schema.Replace("\"type\":\"string\"", "\"type\":\"string\",\"enum\":[\"ghi nhận 😀\"]");
        var handler = new Handler((_, _) => Task.FromResult(Response(Envelope(output))));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<AiProviderExecutionException>(() => new OpenAiCompatibleResponsesAdapter(client, Options()).ExecuteAsync(Request(schema: schema)));
        Assert.False(error.IsTransient);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task Nullable_array_boolean_number_and_escaped_unicode_profile_has_positive_control()
    {
        const string schema = """{"type":"object","properties":{"notes":{"type":"array","items":{"type":"string"}},"count":{"type":"integer"},"ok":{"type":"boolean"},"deadline":{"type":["string","null"]}},"required":["notes","count","ok","deadline"],"additionalProperties":false}""";
        var output = """{"notes":["\uD83D\uDE00","literal\\uD800"],"count":2,"ok":true,"deadline":null}""";
        var handler = new Handler((_, _) => Task.FromResult(Response(Envelope(output))));
        using var client = new HttpClient(handler);
        var result = await new OpenAiCompatibleResponsesAdapter(client, Options()).ExecuteAsync(Request(schema: schema));
        Assert.Equal(output, result.Output);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("refusal")]
    [InlineData("tool")]
    [InlineData("two-messages")]
    [InlineData("two-texts")]
    [InlineData("user")]
    [InlineData("pending-message")]
    [InlineData("error")]
    [InlineData("incomplete-details")]
    [InlineData("fallback-only")]
    [InlineData("duplicate-status")]
    [InlineData("negative-usage")]
    [InlineData("overflow-usage")]
    [InlineData("wrong-total")]
    [InlineData("model-control")]
    public async Task Envelope_failures_are_permanent_sanitized_and_cannot_use_fallback(string failure)
    {
        var text = Envelope();
        text = failure switch
        {
            "incomplete" => text.Replace("\"status\":\"completed\"", "\"status\":\"incomplete\""),
            "refusal" => text.Replace("output_text", "refusal"),
            "tool" => text.Replace("\"type\":\"message\"", "\"type\":\"function_call\""),
            "two-messages" => text.Replace("\"output\":[", "\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"status\":\"completed\",\"content\":[{\"type\":\"output_text\",\"text\":\"{}\"}]},"),
            "two-texts" => text.Replace("\"content\":[", "\"content\":[{\"type\":\"output_text\",\"text\":\"{}\"},"),
            "user" => text.Replace("assistant", "user"),
            "pending-message" => text.Replace("\"role\":\"assistant\"", "\"role\":\"assistant\",\"status\":\"pending\""),
            "error" => text.Insert(1, "\"error\":{\"message\":\"PRIVATE-PROVIDER-CANARY\"},"),
            "incomplete-details" => text.Insert(1, "\"incomplete_details\":{},"),
            "fallback-only" => "{\"status\":\"completed\",\"model\":\"owned-model\",\"output_text\":\"{}\",\"output\":[],\"usage\":{\"input_tokens\":3,\"output_tokens\":2,\"total_tokens\":5}}",
            "duplicate-status" => text.Insert(1, "\"stat\\u0075s\":\"completed\","),
            "negative-usage" => text.Replace("\"input_tokens\":3", "\"input_tokens\":-3"),
            "overflow-usage" => text.Replace("\"input_tokens\":3", "\"input_tokens\":9223372036854775807"),
            "wrong-total" => text.Replace("\"total_tokens\":5", "\"total_tokens\":6"),
            "model-control" => text.Replace("\"model\":\"owned-model\"", "\"model\":\"private\\nmodel\""),
            _ => throw new InvalidOperationException()
        };
        var handler = new Handler((_, _) => Task.FromResult(Response(text)));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<AiProviderExecutionException>(() => new OpenAiCompatibleResponsesAdapter(client, Options()).ExecuteAsync(Request()));
        Assert.False(error.IsTransient);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("PRIVATE-PROVIDER-CANARY", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_entity_exact_limit_and_plus_one_with_known_or_unknown_length(bool known)
    {
        var envelope = Encoding.UTF8.GetBytes(Envelope());
        var exact = new byte[262144]; Array.Fill(exact, (byte)' '); envelope.CopyTo(exact, 0);
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
        Assert.Equal(known ? "AI structured response is invalid." : "AI structured body transfer failed.", error.Message);
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
                var excess = new byte[262145];
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
    [InlineData('a')]
    [InlineData('中')]
    [InlineData('\\')]
    public async Task Output_utf8_limit_is_independent_of_entity_limit(char value)
    {
        var body = "{\"note\":\"";
        var size = Encoding.UTF8.GetByteCount(body + "\"}");
        var piece = value == '\\' ? "\\\\" : value.ToString();
        var count = (64000 - size) / Encoding.UTF8.GetByteCount(piece);
        var output = body + string.Concat(Enumerable.Repeat(piece, count)) + new string('a', (64000 - size) % Encoding.UTF8.GetByteCount(piece)) + "\"}";
        Assert.Equal(64000, Encoding.UTF8.GetByteCount(output));
        var handler = new Handler((_, _) => Task.FromResult(Response(Envelope(output))));
        using var client = new HttpClient(handler);
        var adapter = new OpenAiCompatibleResponsesAdapter(client, Options());
        await adapter.ExecuteAsync(Request());
        output = output.Insert(output.Length - 2, "a");
        await Assert.ThrowsAsync<AiProviderExecutionException>(() => adapter.ExecuteAsync(Request()));
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
        var started = 0; var disposed = 0;
        var handler = new Handler(async (_, token) =>
        {
            Interlocked.Increment(ref started);
            if (stage == "headers") await gate.Task;
            if (stage == "callback") token.Register(() => gate.Task.GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ScriptContent(async stream =>
                {
                    if (stage is "body" or "callback") await gate.Task;
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(Envelope()));
                    if (stage == "eof") await gate.Task;
                }, dispose: () =>
                {
                    if (stage == "dispose") gate.Task.GetAwaiter().GetResult();
                    Interlocked.Increment(ref disposed);
                })
            };
        });
        using var client = new HttpClient(handler);
        var adapter = new OpenAiCompatibleResponsesAdapter(client, Options(TimeSpan.FromMilliseconds(250)));
        var watch = Stopwatch.StartNew();
        var calls = Enumerable.Range(0, 4).Select(_ => adapter.ExecuteAsync(Request())).ToArray();
        try
        {
            await Until(() => Volatile.Read(ref started) == 4);
            foreach (var call in calls)
            {
                var failure = await Assert.ThrowsAsync<AiProviderExecutionException>(() => call);
                Assert.Contains("deadline", failure.Message, StringComparison.Ordinal);
                Assert.Null(failure.InnerException);
            }
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
            var occupied = await Assert.ThrowsAsync<AiProviderExecutionException>(() => adapter.ExecuteAsync(Request()));
            Assert.Contains("capacity", occupied.Message, StringComparison.Ordinal);
            Assert.Equal(4, handler.Calls);
        }
        finally { gate.TrySetResult(); }
        await Until(() => Volatile.Read(ref disposed) == 4);
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

    [Theory]
    [InlineData("http://provider.example/responses", false)]
    [InlineData("http://127.0.0.1:99/responses", false)]
    [InlineData("https://user:password@provider.example/responses", false)]
    [InlineData("https://provider.example/responses?secret=x", false)]
    [InlineData("https://provider.example/responses#secret", false)]
    public async Task Unsafe_endpoint_refuses_before_http(string uri, bool allow)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(Envelope())));
        using var client = new HttpClient(handler);
        var adapter = new OpenAiCompatibleResponsesAdapter(client, Options() with { Endpoint = new(uri), AllowInsecureLoopback = allow });
        await Assert.ThrowsAsync<AiProviderExecutionException>(() => adapter.ExecuteAsync(Request()));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Actual_production_factory_on_owned_loopback_does_not_follow_redirect()
    {
        using var source = new TcpListener(IPAddress.Loopback, 0);
        using var destination = new TcpListener(IPAddress.Loopback, 0);
        source.Start(); destination.Start();
        var sourcePort = ((IPEndPoint)source.LocalEndpoint).Port;
        var destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var connection = await source.AcceptTcpClientAsync();
            using var stream = connection.GetStream();
            var request = new byte[16384];
            var received = await stream.ReadAsync(request);
            Assert.True(received > 0);
            var redirect = Encoding.ASCII.GetBytes($"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{destinationPort}/stolen\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(redirect);
        });
        using var client = StructuredResponsesPolicy.CreateProductionClient();
        var options = Options() with { Endpoint = new($"http://127.0.0.1:{sourcePort}/responses"), AllowInsecureLoopback = true };
        var error = await Assert.ThrowsAsync<AiProviderExecutionException>(() => new OpenAiCompatibleResponsesAdapter(client, options).ExecuteAsync(Request()));
        Assert.False(error.IsTransient);
        await server.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(destination.Pending());
    }

    [Theory]
    [InlineData('中')]
    [InlineData('a')]
    public async Task Legacy_reasoning_64000_utf16_is_not_changed_by_structured_entity_budget(char value)
    {
        var text = new string(value, 64000);
        // Default JSON encoder intentionally escapes CJK: envelope exceeds the new structured entity bound.
        var legacy = JsonSerializer.Serialize(new { model = "owned-model", output_text = text, usage = new { input_tokens = 1, output_tokens = 1 } });
        if (value == '中') Assert.True(Encoding.UTF8.GetByteCount(legacy) > 262144);
        var handler = new Handler((_, _) => Task.FromResult(Response(legacy)));
        using var client = new HttpClient(handler);
        var result = await new OpenAiCompatibleResponsesAdapter(client, Options()).ExecuteAsync(Request() with { Capability = AiCapability.Reasoning, ResponseSchema = null });
        Assert.Equal(text, result.Output);
    }

    [Theory]
    [InlineData(9007199254740992L, true)]
    [InlineData(9007199254740993L, false)]
    public async Task Integer_enum_does_not_round_different_exact_json_numbers(long value, bool accepted)
    {
        var schema = Schema.Replace("\"type\":\"string\"", "\"type\":\"integer\",\"enum\":[9007199254740992]");
        var handler = new Handler((_, _) => Task.FromResult(Response(Envelope($"{{\"note\":{value}}}"))));
        using var client = new HttpClient(handler);
        var pending = new OpenAiCompatibleResponsesAdapter(client, Options()).ExecuteAsync(Request(schema: schema));
        if (accepted) await pending;
        else await Assert.ThrowsAsync<AiProviderExecutionException>(() => pending);
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public async Task Schema_nesting_limit_runs_on_actual_adapter(int depth, bool accepted)
    {
        var item = "{\"type\":\"string\"}";
        var value = "\"x\"";
        for (var index = 0; index < depth - 2; index++)
        {
            item = "{\"type\":\"array\",\"items\":" + item + "}";
            value = "[" + value + "]";
        }
        var schema = Schema.Replace("{\"type\":\"string\"}", item);
        var handler = new Handler((_, _) => Task.FromResult(Response(Envelope("{\"note\":" + value + "}"))));
        using var client = new HttpClient(handler);
        var pending = new OpenAiCompatibleResponsesAdapter(client, Options()).ExecuteAsync(Request(schema: schema));
        if (accepted) await pending;
        else { await Assert.ThrowsAsync<AiProviderExecutionException>(() => pending); Assert.Equal(0, handler.Calls); }
    }

    [Fact]
    public async Task Valid_extracted_output_can_still_exceed_encoded_entity_limit()
    {
        var output = "{\"note\":\"" + new string('<', 60000) + "\"}";
        Assert.True(Encoding.UTF8.GetByteCount(output) < 64000);
        // Default encoding escapes HTML characters; the actual serialized envelope is much larger.
        var escaped = JsonSerializer.Serialize(new
        {
            status = "completed",
            model = "owned-model",
            output = new[] { new { type = "message", role = "assistant", status = "completed", content = new[] { new { type = "output_text", text = output } } } },
            usage = new { input_tokens = 3, output_tokens = 2, total_tokens = 5 }
        });
        Assert.True(Encoding.UTF8.GetByteCount(escaped) > 262144);
        var handler = new Handler((_, _) => Task.FromResult(Response(escaped)));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<AiProviderExecutionException>(() => new OpenAiCompatibleResponsesAdapter(client, Options()).ExecuteAsync(Request()));
    }

    [Theory]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(401, false)]
    [InlineData(400, false)]
    [InlineData(307, false)]
    public async Task Ordered_structured_actual_adapters_use_backup_only_after_transient_http(int code, bool backupEligible)
    {
        var count = 0;
        var handler = new Handler((_, _) => Task.FromResult(Interlocked.Increment(ref count) == 1
            ? new HttpResponseMessage((HttpStatusCode)code) : Response(Envelope())));
        using var client = new HttpClient(handler);
        var gateway = new OrderedFailoverAiGateway(new OpenAiCompatibleResponsesAdapter(client, Options()),
            new OpenAiCompatibleResponsesAdapter(client, Options() with { ProviderId = "backup-provider" }));
        if (backupEligible)
        {
            var result = await gateway.ExecuteAsync(Request());
            Assert.Equal("backup-provider", result.ProviderId);
            Assert.Equal(2, handler.Calls);
        }
        else
        {
            await Assert.ThrowsAsync<AiProviderExecutionException>(() => gateway.ExecuteAsync(Request()));
            Assert.Equal(1, handler.Calls);
        }
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
            Assert.Equal("AI structured deadline expired.", failure.Message);
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
                        try { await stream.WriteAsync(new byte[262145]); }
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

    [Theory]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    public async Task Aggregate_enum_limit_applies_across_different_nested_properties(int count, bool accepted)
    {
        var first = Enumerable.Range(0, 500).ToArray();
        var second = Enumerable.Range(0, count - 500).ToArray();
        var schema = JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                a = new { type = "integer", @enum = first },
                nested = new
                {
                    type = "object",
                    properties = new { b = new { type = "integer", @enum = second } },
                    required = new[] { "b" },
                    additionalProperties = false
                }
            },
            required = new[] { "a", "nested" },
            additionalProperties = false
        });
        Assert.True(Encoding.UTF8.GetByteCount(schema) < 32000);
        var handler = new Handler((_, _) => Task.FromResult(Response(Envelope("{\"a\":0,\"nested\":{\"b\":0}}"))));
        using var client = new HttpClient(handler);
        var pending = new OpenAiCompatibleResponsesAdapter(client, Options()).ExecuteAsync(Request(schema: schema));
        if (accepted) { await pending; Assert.Equal(1, handler.Calls); }
        else { await Assert.ThrowsAsync<AiProviderExecutionException>(() => pending); Assert.Equal(0, handler.Calls); }
    }

    [Theory]
    [InlineData(15000, true)]
    [InlineData(15001, false)]
    public async Task More_than_250_string_enums_have_a_separate_aggregate_character_gate(int characters, bool accepted)
    {
        var choices = Enumerable.Range(0, 251).Select(index => index.ToString("D4")).ToArray();
        choices[0] += new string('a', characters - choices.Sum(value => value.Length));
        Assert.Equal(characters, choices.Sum(value => value.Length));
        var schema = JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new { note = new { type = "string", @enum = choices } },
            required = new[] { "note" },
            additionalProperties = false
        });
        Assert.True(Encoding.UTF8.GetByteCount(schema) < 32000);
        var handler = new Handler((_, _) => Task.FromResult(Response(Envelope(JsonSerializer.Serialize(new { note = choices[1] })))));
        using var client = new HttpClient(handler);
        var pending = new OpenAiCompatibleResponsesAdapter(client, Options()).ExecuteAsync(Request(schema: schema));
        if (accepted) { await pending; Assert.Equal(1, handler.Calls); }
        else { await Assert.ThrowsAsync<AiProviderExecutionException>(() => pending); Assert.Equal(0, handler.Calls); }
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
    private sealed class ScriptContent(Func<Stream, Task> write, long? declared = null, Action? dispose = null) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => write(stream);
        protected override Task<Stream> CreateContentReadStreamAsync() => throw new InvalidOperationException("Unbounded stream factory must never be called.");
        protected override bool TryComputeLength(out long length) { length = declared ?? 0; return declared.HasValue; }
        protected override void Dispose(bool disposing) { if (disposing) dispose?.Invoke(); base.Dispose(disposing); }
    }
}
