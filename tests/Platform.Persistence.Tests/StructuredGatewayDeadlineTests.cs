extern alias RuntimeWorker;

using System.Net;
using System.Text;
using MinhHuyAiOffice.Shared.Contracts;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

// Real timeout/cleanup transport controls using the same adapters/client. No model evaluation.
[Collection(nameof(StructuredGatewayTimingCollection))]
public sealed class StructuredGatewayDeadlineTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(4);
    private static AiGatewayRequest Request(AiCapability capability = AiCapability.StructuredGeneration) =>
        new("tenant", "company", "task", "request", capability, "synthetic input",
            capability == AiCapability.StructuredGeneration ? """{"type":"object","properties":{"note":{"type":"string"}},"required":["note"],"additionalProperties":false}""" : null);
    private static OpenAiCompatibleResponsesOptions Options(string provider = "primary") =>
        new(new("https://provider.example/v1/responses"), "synthetic-model", "Bearer synthetic-key", provider)
        { StructuredTimeout = TimeSpan.FromSeconds(3) };
    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"status":"completed","model":"synthetic-model","output":[{"type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"{\"note\":\"synthetic\"}"}]}],"usage":{"input_tokens":3,"output_tokens":2,"total_tokens":5}}""", Encoding.UTF8, "application/json")
    };
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static OrderedFailoverAiGateway Gateway(HttpClient client, TimeSpan timeout, bool backup = true) =>
        new(new OpenAiCompatibleResponsesAdapter(client, Options()),
            backup ? new OpenAiCompatibleResponsesAdapter(client, Options("backup")) : null, timeout);

    [Fact]
    public async Task Backup_receives_only_original_remaining_budget_and_late_success_is_discarded()
    {
        var primaryEntered = Signal(); var backupEntered = Signal(); var failPrimary = Signal(); var lateBackup = Signal();
        using var client = new HttpClient(new Handler(async (_, _) =>
        {
            if (!primaryEntered.Task.IsCompleted)
            { primaryEntered.SetResult(); await failPrimary.Task; return new(HttpStatusCode.ServiceUnavailable); }
            backupEntered.SetResult(); await lateBackup.Task; return Success();
        }));
        var gateway = Gateway(client, TimeSpan.FromMilliseconds(600));
        var pending = gateway.ExecuteAsync(Request());
        try
        {
            await primaryEntered.Task.WaitAsync(Guard);
            await Task.Delay(400);
            failPrimary.SetResult(); await backupEntered.Task.WaitAsync(Guard);
            // A reset600ms backup budget would still be waiting here.
            var error = await Assert.ThrowsAsync<AiProviderExecutionException>(() => pending.WaitAsync(TimeSpan.FromMilliseconds(350)));
            Assert.Equal("AI structured Gateway deadline expired.", error.Message);
            Assert.Null(error.InnerException);
            lateBackup.SetResult();
            Assert.True(pending.IsFaulted);
        }
        finally { failPrimary.TrySetResult(); lateBackup.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Primary_that_ignores_deadline_cannot_dispatch_backup_when_later_released(bool lateFault)
    {
        var entered = Signal(); var release = Signal(); var finished = Signal(); var calls = 0;
        using var client = new HttpClient(new Handler(async (_, _) =>
        {
            Interlocked.Increment(ref calls); entered.TrySetResult();
            try
            {
                await release.Task;
                if (lateFault) throw new HttpRequestException("PRIVATE-LATE-CANARY");
                return new(HttpStatusCode.ServiceUnavailable);
            }
            finally { finished.TrySetResult(); }
        }));
        var pending = Gateway(client, TimeSpan.FromMilliseconds(200)).ExecuteAsync(Request());
        try
        {
            await entered.Task.WaitAsync(Guard);
            var failure = await Assert.ThrowsAsync<AiProviderExecutionException>(() => pending.WaitAsync(Guard));
            Assert.Equal("AI structured Gateway deadline expired.", failure.Message);
            Assert.DoesNotContain("PRIVATE-LATE-CANARY", failure.ToString(), StringComparison.Ordinal);
            release.SetResult(); await finished.Task.WaitAsync(Guard);
            await Task.Delay(75);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Cancellation_has_original_identity_and_does_not_dispatch_a_backup()
    {
        var entered = Signal(); var release = Signal(); var calls = 0;
        using var caller = new CancellationTokenSource();
        using var client = new HttpClient(new Handler(async (_, _) =>
        { Interlocked.Increment(ref calls); entered.SetResult(); await release.Task; return new(HttpStatusCode.ServiceUnavailable); }));
        var pending = Gateway(client, TimeSpan.FromSeconds(2)).ExecuteAsync(Request(), caller.Token);
        try
        {
            await entered.Task.WaitAsync(Guard); caller.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Guard));
            Assert.Equal(caller.Token, error.CancellationToken);
            release.SetResult(); await Task.Delay(75); Assert.Equal(1, calls);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Already_cancelled_call_does_not_start_HTTP()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Success()); }));
        using var caller = new CancellationTokenSource(); caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Gateway(client, TimeSpan.FromSeconds(1)).ExecuteAsync(Request(), caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("body")]
    [InlineData("dispose")]
    [InlineData("cancel")]
    public async Task Deadline_returns_but_four_original_route_slots_remain_owned_through_cleanup(string stage)
    {
        var entered = new TaskCompletionSource[4]; var bodyExited = new TaskCompletionSource[4];
        var callbackEntered = new TaskCompletionSource[4];
        for (var i = 0; i < 4; i++) { entered[i] = Signal(); bodyExited[i] = Signal(); callbackEntered[i] = Signal(); }
        var release = Signal(); var releaseCallback = Signal(); var calls = 0;
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            var index = Interlocked.Increment(ref calls) - 1;
            if (index >= 4) return Success();
            if (stage == "header")
            { entered[index].TrySetResult(); release.Task.GetAwaiter().GetResult(); return Success(); }
            await Task.CompletedTask;
            return new(HttpStatusCode.OK)
            {
                Content = new ScriptContent(async (stream, copyToken) =>
                {
                    // ResponseHeadersRead releases HttpClient's header token;
                    // the physical body/cleanup operation owns this copy token.
                    using var registration = stage == "cancel" ? copyToken.Register(() =>
                    { callbackEntered[index].TrySetResult(); releaseCallback.Task.GetAwaiter().GetResult(); }) : default;
                    try
                    {
                        if (stage != "dispose") { entered[index].TrySetResult(); await release.Task; }
                        using var response = Success(); var bytes = await response.Content.ReadAsByteArrayAsync(); await stream.WriteAsync(bytes);
                    }
                    finally { bodyExited[index].TrySetResult(); }
                }, () =>
                { if (stage == "dispose") { entered[index].TrySetResult(); release.Task.GetAwaiter().GetResult(); } })
            };
        }));
        var adapter = new OpenAiCompatibleResponsesAdapter(client, Options());
        var gateway = new OrderedFailoverAiGateway(adapter, wholeStructuredTimeout: TimeSpan.FromMilliseconds(250));
        var held = Enumerable.Range(0, 4).Select(_ => gateway.ExecuteAsync(Request())).ToArray();
        try
        {
            await Task.WhenAll(entered.Select(x => x.Task)).WaitAsync(Guard);
            foreach (var call in held)
            {
                var error = await Assert.ThrowsAsync<AiProviderExecutionException>(() => call.WaitAsync(Guard));
                Assert.Equal("AI structured Gateway deadline expired.", error.Message);
            }
            if (stage == "cancel") await Task.WhenAll(callbackEntered.Select(x => x.Task)).WaitAsync(Guard);
            // Probe the same physical adapter directly: a newly scheduled
            // Gateway's250ms caller deadline must not mask its capacity result.
            var occupied = await Assert.ThrowsAsync<AiProviderExecutionException>(() => adapter.ExecuteAsync(Request()).WaitAsync(Guard));
            Assert.Equal("AI structured capacity is occupied.", occupied.Message); Assert.Equal(4, calls);
            if (stage == "cancel")
            {
                release.SetResult();
                // Actual copy-body exit precedes its registration/callback
                // cleanup. Observe all four bodies, not a passive75ms delay.
                await Task.WhenAll(bodyExited.Select(x => x.Task)).WaitAsync(Guard);
                occupied = await Assert.ThrowsAsync<AiProviderExecutionException>(() => adapter.ExecuteAsync(Request()).WaitAsync(Guard));
                Assert.Equal("AI structured capacity is occupied.", occupied.Message); Assert.Equal(4, calls);
            }
        }
        finally { release.TrySetResult(); releaseCallback.TrySetResult(); }
        // Retry only the explicit occupied-capacity result while asynchronous owned cleanup ends.
        using var guard = new CancellationTokenSource(Guard);
        while (true)
        {
            try { Assert.Equal("primary", (await adapter.ExecuteAsync(Request(), guard.Token)).ProviderId); break; }
            catch (AiProviderExecutionException error) when (error.Message == "AI structured capacity is occupied.")
            { await Task.Delay(10, guard.Token); }
        }
    }

    [Theory]
    [InlineData(401)]
    [InlineData(400)]
    [InlineData(307)]
    public async Task Permanent_HTTP_denial_retains_nonfallback_policy(int code)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)code)); }));
        var error = await Assert.ThrowsAsync<AiProviderExecutionException>(() => Gateway(client, TimeSpan.FromSeconds(1)).ExecuteAsync(Request()));
        Assert.False(error.IsTransient); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Fast_transient_primary_can_use_backup_and_primary_only_can_succeed()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
            ? new(HttpStatusCode.ServiceUnavailable) : Success())));
        Assert.Equal("backup", (await Gateway(client, TimeSpan.FromSeconds(1)).ExecuteAsync(Request())).ProviderId);
        Assert.Equal(2, calls);
        Assert.Equal("primary", (await Gateway(client, TimeSpan.FromSeconds(1), false).ExecuteAsync(Request())).ProviderId);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Reasoning_retains_legacy_route_behavior_without_the_structured_chain_deadline()
    {
        using var client = new HttpClient(new Handler(async (_, _) => { await Task.Delay(80); return Success(); }));
        var response = await Gateway(client, TimeSpan.FromMilliseconds(10), false).ExecuteAsync(Request(AiCapability.Reasoning));
        Assert.Equal(AiCapability.Reasoning, response.Capability); Assert.Equal("primary", response.ProviderId);
    }

    [Fact]
    public void Metadata_versions_deadline_drift_and_preserves_legacy_unqualified_descriptor()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("No HTTP")));
        var first = Gateway(client, TimeSpan.FromSeconds(1)).DescribeStructuredGateway();
        var second = Gateway(client, TimeSpan.FromSeconds(2)).DescribeStructuredGateway();
        Assert.True(first.ProvidesWholeOperationDeadline); Assert.Equal(TimeSpan.FromSeconds(1).Ticks, first.WholeOperationTimeoutTicks);
        Assert.NotEqual(first.MetadataSha256, second.MetadataSha256);
        var legacy = AiStructuredGatewayMetadata.Create(first.Routes[0], first.Routes[1]);
        Assert.False(legacy.ProvidesWholeOperationDeadline); Assert.Null(legacy.WholeOperationTimeoutTicks);
        Assert.NotEqual(first.MetadataSha256, legacy.MetadataSha256); Assert.True(first.ModelAccountingUnavailable);
        Assert.Equal(2, first.MaximumPhysicalInvocations);
        foreach (var timeout in new[] { TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.FromSeconds(61) })
            Assert.Throws<ArgumentException>(() => Gateway(client, timeout));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Direct_noncooperative_primary_completion_cannot_revive_caller_or_start_backup(bool cancel, bool lateSuccess)
    {
        var entered = Signal(); var callbacks = 0; var backupCalls = 0;
        var primaryCompletion = new TaskCompletionSource<AiGatewayResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = new CancellationTokenSource();
        CancellationToken observedToken = default;
        var primary = new DirectAdapter("primary", (request, token) =>
        {
            observedToken = token; token.Register(() => Interlocked.Increment(ref callbacks));
            entered.TrySetResult(); return primaryCompletion.Task;
        });
        var backup = new DirectAdapter("backup", (request, _) =>
        { Interlocked.Increment(ref backupCalls); return Task.FromResult(DirectResponse(request, "backup")); });
        var gateway = new OrderedFailoverAiGateway(primary, backup,
            cancel ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(200));
        var owned = gateway.BeginStructuredOperation(Request(), caller.Token); var pending = owned.Caller;
        await entered.Task.WaitAsync(Guard);
        if (cancel)
        {
            caller.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Guard));
            Assert.Equal(caller.Token, error.CancellationToken);
        }
        else
        {
            var error = await Assert.ThrowsAsync<AiProviderExecutionException>(() => pending.WaitAsync(Guard));
            Assert.Equal("AI structured Gateway deadline expired.", error.Message);
        }
        Assert.False(primaryCompletion.Task.IsCompleted);
        Assert.True(observedToken.IsCancellationRequested);
        Assert.False(owned.ChainCompletion.IsCompleted);
        if (lateSuccess) primaryCompletion.SetResult(DirectResponse(Request(), "primary"));
        else primaryCompletion.SetException(new AiProviderExecutionException("PRIVATE-DIRECT-LATE-CANARY", true));
        await ObserveChainCompletion(owned);
        Assert.True(primaryCompletion.Task.IsCompleted); Assert.Equal(0, backupCalls);
        Assert.Equal(1, callbacks);
        Assert.Equal(cancel, pending.IsCanceled); Assert.Equal(!cancel, pending.IsFaulted);
        Assert.DoesNotContain("PRIVATE-DIRECT-LATE-CANARY", pending.Exception?.ToString() ?? "", StringComparison.Ordinal);
        // Cached token reads remain legal after the Gateway disposes its source.
        Assert.True(observedToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Direct_noncooperative_backup_shares_the_original_clock_and_late_completion_is_discarded()
    {
        var primaryEntered = Signal(); var backupEntered = Signal();
        var primaryCompletion = new TaskCompletionSource<AiGatewayResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backupCompletion = new TaskCompletionSource<AiGatewayResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new DirectAdapter("primary", (_, _) => { primaryEntered.SetResult(); return primaryCompletion.Task; });
        var backup = new DirectAdapter("backup", (_, _) => { backupEntered.SetResult(); return backupCompletion.Task; });
        var owned = new OrderedFailoverAiGateway(primary, backup, TimeSpan.FromMilliseconds(600)).BeginStructuredOperation(Request(), default);
        var pending = owned.Caller;
        await primaryEntered.Task.WaitAsync(Guard); await Task.Delay(400);
        primaryCompletion.SetException(new AiProviderExecutionException("synthetic transient", true));
        await backupEntered.Task.WaitAsync(Guard);
        var error = await Assert.ThrowsAsync<AiProviderExecutionException>(() => pending.WaitAsync(TimeSpan.FromMilliseconds(350)));
        Assert.Equal("AI structured Gateway deadline expired.", error.Message);
        Assert.False(backupCompletion.Task.IsCompleted);
        Assert.False(owned.ChainCompletion.IsCompleted);
        backupCompletion.SetResult(DirectResponse(Request(), "backup"));
        await ObserveChainCompletion(owned);
        Assert.True(backupCompletion.Task.IsCompleted); Assert.True(pending.IsFaulted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Synchronous_supplier_work_is_bounded_and_cannot_start_an_invocation_after_expiry(bool blockSupports)
    {
        var entered = Signal(); using var release = new ManualResetEventSlim(); var executeCalls = 0; var backupCalls = 0;
        var primary = new DirectAdapter("primary", (request, _) =>
        {
            Interlocked.Increment(ref executeCalls);
            if (!blockSupports) { entered.SetResult(); release.Wait(); }
            throw new AiProviderExecutionException("synthetic transient", true);
        }, capability =>
        {
            if (blockSupports) { entered.SetResult(); release.Wait(); }
            return capability == AiCapability.StructuredGeneration;
        });
        var backup = new DirectAdapter("backup", (request, _) =>
        { Interlocked.Increment(ref backupCalls); return Task.FromResult(DirectResponse(request, "backup")); });
        var owned = new OrderedFailoverAiGateway(primary, backup, TimeSpan.FromMilliseconds(200)).BeginStructuredOperation(Request(), default);
        var pending = owned.Caller;
        try
        {
            await entered.Task.WaitAsync(Guard);
            var error = await Assert.ThrowsAsync<AiProviderExecutionException>(() => pending.WaitAsync(Guard));
            Assert.Equal("AI structured Gateway deadline expired.", error.Message);
            Assert.False(owned.ChainCompletion.IsCompleted);
            release.Set();
            await ObserveChainCompletion(owned);
            Assert.Equal(blockSupports ? 0 : 1, Volatile.Read(ref executeCalls));
            Assert.Equal(0, Volatile.Read(ref backupCalls)); Assert.True(pending.IsFaulted);
        }
        finally { release.Set(); }
    }

    private static async Task ObserveChainCompletion(OrderedFailoverAiGateway.StructuredOperation owned)
    {
        try { await owned.ChainCompletion.WaitAsync(Guard); }
        catch (AiProviderExecutionException) { }
        catch (OperationCanceledException) { }
        // TimeoutException stays a failure. Await the actual Gateway chain,
        // not an HTTP gate, scheduler inference or passive millisecond wait.
        Assert.True(owned.ChainCompletion.IsCompleted);
    }

    private static AiGatewayResponse DirectResponse(AiGatewayRequest request, string provider) =>
        new(request.RequestId, request.Capability, "{\"note\":\"synthetic\"}", "synthetic-model", 3, 2) { ProviderId = provider };
    private sealed class DirectAdapter(string provider,
        Func<AiGatewayRequest, CancellationToken, Task<AiGatewayResponse>> execute,
        Func<AiCapability, bool>? supports = null) : IAiProviderAdapter
    {
        public string ProviderId => provider;
        public bool Supports(AiCapability capability) => supports?.Invoke(capability) ?? capability == AiCapability.StructuredGeneration;
        public Task<AiGatewayResponse> ExecuteAsync(AiGatewayRequest request, CancellationToken token = default) => execute(request, token);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> execute) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => execute(request, token);
    }
    private sealed class ScriptContent(Func<Stream, CancellationToken, Task> write, Action dispose) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => write(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => write(stream, token);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { if (disposing) dispose(); base.Dispose(disposing); }
    }
}

[CollectionDefinition(nameof(StructuredGatewayTimingCollection), DisableParallelization = true)]
public sealed class StructuredGatewayTimingCollection
{
}
