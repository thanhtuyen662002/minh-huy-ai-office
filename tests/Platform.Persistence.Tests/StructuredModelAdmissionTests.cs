extern alias RuntimeWorker;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinhHuyAiOffice.Shared.Contracts;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

// Synthetic catalog/framing/window/HTTP fixtures ONLY. No real model release.
[Collection(nameof(BoundedTokenizationCollection))]
public sealed class StructuredModelAdmissionTests
{
    private const string Schema = "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}";
    private static AiGatewayRequest Request(string input = "Tra cứu tồn kho, nhập xuất") =>
        new("tenant", "company", "task", "request", AiCapability.StructuredGeneration, input, Schema);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Fingerprint => new('A', 64);

    [Fact]
    public async Task Production_catalog_is_empty_and_never_releases_HTTP()
    {
        using var fixture = new Fixture();
        var gate = new StructuredModelAdmission(fixture.Gateway);
        await Denied(() => gate.PrepareAsync(Request()));
        Assert.Equal(0, fixture.Calls);
        Assert.Empty(typeof(IAiPreparedStructuredRequest).GetProperties());
    }

    [Fact]
    public async Task Exact_complete_strings_and_every_route_reserve_are_measured_before_release()
    {
        using var fixture = new Fixture(true);
        var seen = new List<string>();
        var counter = new BoundedStructuredTextTokenCounter((text, _) => { seen.Add(text); return text == Schema ? 3 : 5; }, TimeSpan.FromSeconds(1));
        var gate = fixture.Gate(fixture.Catalog(windows: [21, 29]), counter);
        var prepared = await gate.PrepareAsync(Request());
        Assert.Equal(new[] { Request().Input, Schema }, seen);
        Assert.Equal(0, fixture.Calls);
        var actual = Assert.IsType<StructuredModelAdmission.Prepared>(prepared);
        Assert.All(actual.Budgets, budget => Assert.Equal(13, budget.MaximumInputTokens));
        Assert.Equal(2, actual.Budgets.Count);
        Assert.Equal("primary-observed", (await gate.ExecutePreparedAsync(prepared)).Model);
        Assert.Equal(1, fixture.Calls);
    }

    [Fact]
    public async Task Actual_bounded_scalar_counter_executes_only_synthetic_qualified_route()
    {
        using var fixture = new Fixture();
        var gate = fixture.Gate(fixture.Catalog());
        var handle = await gate.PrepareAsync(Request());
        var result = await gate.ExecutePreparedAsync(handle);
        Assert.Equal("primary", result.ProviderId);
        Assert.Equal("request", result.RequestId);
        Assert.Equal("{}", result.Output);
        Assert.Contains("Tra", fixture.LastBody);
        Assert.Contains("group_work_notes_v1", fixture.LastBody);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_window_boundary_passes_and_one_less_fails_for_any_fallback_route(bool backup)
    {
        using var fixture = new Fixture(backup);
        var windows = backup ? new long[] { 21, 29 } : [21];
        await fixture.Gate(fixture.Catalog(windows), ConstantCounter()).PrepareAsync(Request());
        windows[^1]--;
        await Denied(() => fixture.Gate(fixture.Catalog(windows), ConstantCounter()).PrepareAsync(Request()));
        Assert.Equal(0, fixture.Calls);
    }

    [Fact]
    public async Task Unknown_backup_and_schema_profile_refuse_before_count_or_HTTP()
    {
        using var fixture = new Fixture(true);
        var calls = 0;
        var counter = new BoundedStructuredTextTokenCounter((_, _) => { calls++; return 1; }, TimeSpan.FromSeconds(1));
        var primaryOnly = StructuredModelProfileCatalog.ForSyntheticControls(fixture.Profile(0));
        await Denied(() => fixture.Gate(primaryOnly, counter).PrepareAsync(Request()));
        var wrongSchema = fixture.Catalog(schema: Hash("different"));
        await Denied(() => fixture.Gate(wrongSchema, counter).PrepareAsync(Request()));
        Assert.Equal(0, calls); Assert.Equal(0, fixture.Calls);
    }

    [Theory]
    [InlineData("gateway")]
    [InlineData("endpoint")]
    [InlineData("model")]
    [InlineData("output")]
    [InlineData("order")]
    public async Task Route_specific_descriptor_drift_refuses_without_reselecting(string change)
    {
        using var fixture = new Fixture(true);
        var original = fixture.Profile(0);
        var mismatched = fixture.Profile(0, gatewayHash: change == "gateway" ? new('B', 64) : null,
            endpointHash: change == "endpoint" ? new('B', 64) : null, model: change == "model" ? "other" : null,
            output: change == "output" ? 9 : null, ordinal: change == "order" ? 1 : null);
        Assert.NotEqual(original.CandidateSha256, mismatched.CandidateSha256);
        if (change == "order")
            Assert.Throws<InvalidOperationException>(() => StructuredModelProfileCatalog.ForSyntheticControls(mismatched, fixture.Profile(1)));
        else
            await Denied(() => fixture.Gate(StructuredModelProfileCatalog.ForSyntheticControls(mismatched, fixture.Profile(1))).PrepareAsync(Request()));
        Assert.Equal(0, fixture.Calls);
    }

    [Fact]
    public async Task Handle_is_concrete_issuer_bound_immutable_and_single_use()
    {
        using var fixture = new Fixture();
        var catalog = fixture.Catalog(); var first = fixture.Gate(catalog); var second = fixture.Gate(catalog);
        var handle = await first.PrepareAsync(Request());
        await Denied(() => first.ExecutePreparedAsync(new Forged()));
        await Denied(() => second.ExecutePreparedAsync(handle));
        Assert.Equal(0, fixture.Calls);
        await first.ExecutePreparedAsync(handle);
        await Denied(() => first.ExecutePreparedAsync(handle));
        Assert.Equal(1, fixture.Calls);
        foreach (var diagnostic in new[] { handle.ToString(), first.ToString(), catalog.ToString() })
            foreach (var secret in new[] { Request().Input, Schema, "synthetic-secret", "provider.example", "primary-observed" })
                Assert.DoesNotContain(secret, diagnostic!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revocation_after_preparation_refuses_without_HTTP()
    {
        using var fixture = new Fixture(); var catalog = fixture.Catalog(); var gate = fixture.Gate(catalog);
        var handle = await gate.PrepareAsync(Request());
        catalog.RevokeForSyntheticControls(fixture.Profile(0).CandidateSha256);
        await Denied(() => gate.ExecutePreparedAsync(handle)); Assert.Equal(0, fixture.Calls);
    }

    [Fact]
    public async Task Revocation_while_provider_waits_never_returns_success()
    {
        using var fixture = new Fixture(); var catalog = fixture.Catalog(); var gate = fixture.Gate(catalog);
        fixture.BeforeReply = () => catalog.RevokeForSyntheticControls(fixture.Profile(0).CandidateSha256);
        var handle = await gate.PrepareAsync(Request());
        await Denied(() => gate.ExecutePreparedAsync(handle)); Assert.Equal(1, fixture.Calls);
    }

    [Theory]
    [InlineData("cross-route-alias")]
    [InlineData("output-reserve")]
    [InlineData("input-framing")]
    public async Task Response_must_match_actual_route_alias_and_its_reserves(string change)
    {
        using var fixture = new Fixture(true);
        fixture.Model = change == "cross-route-alias" ? "backup-observed" : null;
        fixture.OutputTokens = change == "output-reserve" ? 9 : 0;
        fixture.InputTokens = change == "input-framing" ? 14 : 1;
        var gate = fixture.Gate(fixture.Catalog(), ConstantCounter());
        var handle = await gate.PrepareAsync(Request());
        if (change == "output-reserve")
        {
            var error = await Assert.ThrowsAsync<AiProviderExecutionException>(() => gate.ExecutePreparedAsync(handle));
            Assert.False(error.IsTransient); Assert.Null(error.InnerException);
        }
        else await Denied(() => gate.ExecutePreparedAsync(handle));
        Assert.Equal(1, fixture.Calls);
    }

    [Fact]
    public async Task Existing_gateway_alone_selects_transient_fallback_and_backup_alias()
    {
        using var fixture = new Fixture(true) { FailPrimary = true };
        var gate = fixture.Gate(fixture.Catalog(), ConstantCounter());
        var handle = await gate.PrepareAsync(Request());
        Assert.Equal("backup-observed", (await gate.ExecutePreparedAsync(handle)).Model);
        Assert.Equal(2, fixture.Calls);
    }

    [Fact]
    public async Task Provider_failure_preserves_transient_class_without_inner_private_error_and_handle_stays_consumed()
    {
        using var fixture = new Fixture() { FailPrimary = true };
        var gate = fixture.Gate(fixture.Catalog()); var handle = await gate.PrepareAsync(Request());
        var failure = await Assert.ThrowsAsync<AiProviderExecutionException>(() => gate.ExecutePreparedAsync(handle));
        Assert.True(failure.IsTransient); Assert.Null(failure.InnerException);
        await Denied(() => gate.ExecutePreparedAsync(handle)); Assert.Equal(1, fixture.Calls);
    }

    [Fact]
    public async Task Original_cancel_identity_and_precancellation_do_not_dispatch()
    {
        using var fixture = new Fixture(); var gate = fixture.Gate(fixture.Catalog());
        using var source = new CancellationTokenSource(); source.Cancel();
        var preparing = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.PrepareAsync(Request(), source.Token));
        Assert.Equal(source.Token, preparing.CancellationToken);
        var handle = await gate.PrepareAsync(Request());
        var executing = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.ExecutePreparedAsync(handle, source.Token));
        Assert.Equal(source.Token, executing.CancellationToken); Assert.Equal(0, fixture.Calls);
        await gate.ExecutePreparedAsync(handle); Assert.Equal(1, fixture.Calls);
    }

    [Fact]
    public async Task One_preparation_clock_covers_both_measurements_and_handle_expires()
    {
        using var fixture = new Fixture(); var clock = new ManualTimestamp(); var count = 0;
        var counter = new BoundedStructuredTextTokenCounter((_, _) => { clock.Advance(TimeSpan.FromSeconds(++count == 1 ? 20 : 15)); return 5; }, TimeSpan.FromSeconds(1));
        await Denied(() => fixture.Gate(fixture.Catalog(), counter, clock).PrepareAsync(Request()));
        Assert.Equal(2, count); Assert.Equal(0, fixture.Calls);
        clock = new ManualTimestamp(); var gate = fixture.Gate(fixture.Catalog(), ConstantCounter(), clock);
        var handle = await gate.PrepareAsync(Request()); clock.Advance(StructuredModelAdmission.MaximumPreparationLifetime);
        await Denied(() => gate.ExecutePreparedAsync(handle)); Assert.Equal(0, fixture.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_preparation_deadline_or_caller_cancellation_ends_wait_while_physical_count_drains(bool cancelCaller)
    {
        using var fixture = new Fixture(); var clock = new ManualTimestamp();
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var counter = new BoundedStructuredTextTokenCounter((_, _) =>
        {
            entered.TrySetResult();
            try { release.Task.GetAwaiter().GetResult(); return 5; }
            finally { exited.TrySetResult(); }
        }, TimeSpan.FromSeconds(4));
        var pending = fixture.Gate(fixture.Catalog(), counter, clock).PrepareAsync(Request(), caller.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (cancelCaller) caller.Cancel(); else clock.Advance(StructuredModelAdmission.PreparationBudget);
            if (cancelCaller)
            {
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.Equal(caller.Token, error.CancellationToken);
            }
            else await Denied(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(exited.Task.IsCompleted); Assert.Equal(0, fixture.Calls);
        }
        finally
        {
            release.TrySetResult(); await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));
            _ = await Record.ExceptionAsync(() => pending);
            // Count-function exit precedes owned callback cleanup/slot release.
            // This collection is exclusive; wait for actual physical recovery
            // before another four-slot ownership test starts.
            var capacity = (SemaphoreSlim)typeof(BoundedStructuredTextTokenCounter)
                .GetField("Capacity", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            Assert.True(SpinWait.SpinUntil(() => capacity.CurrentCount == BoundedStructuredTextTokenCounter.MaximumPhysicalOperations,
                TimeSpan.FromSeconds(3)));
        }
        Assert.False(pending.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Concurrent_reuse_cannot_start_a_second_provider_call()
    {
        using var fixture = new Fixture(); var gate = fixture.Gate(fixture.Catalog());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeReply = () => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); };
        var handle = await gate.PrepareAsync(Request()); var pending = gate.ExecutePreparedAsync(handle);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Denied(() => gate.ExecutePreparedAsync(handle)); Assert.Equal(1, fixture.Calls);
        }
        finally { release.TrySetResult(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(1, fixture.Calls);
    }

    [Fact]
    public async Task Handle_expiration_during_provider_execution_cannot_return_late_success()
    {
        using var fixture = new Fixture(); var clock = new ManualTimestamp();
        var gate = fixture.Gate(fixture.Catalog(), ConstantCounter(), clock);
        fixture.BeforeReply = () => clock.Advance(StructuredModelAdmission.MaximumPreparationLifetime);
        var handle = await gate.PrepareAsync(Request());
        await Denied(() => gate.ExecutePreparedAsync(handle)); Assert.Equal(1, fixture.Calls);
        await Denied(() => gate.ExecutePreparedAsync(handle)); Assert.Equal(1, fixture.Calls);
    }

    [Theory]
    [InlineData("tokenizer")]
    [InlineData("alias-duplicate")]
    [InlineData("empty-alias")]
    [InlineData("window")]
    [InlineData("framing")]
    [InlineData("overflow")]
    public void Unavailable_or_malformed_profile_candidate_cannot_enter_even_synthetic_catalog(string change)
    {
        using var fixture = new Fixture();
        Assert.Throws<ArgumentException>(() => fixture.Profile(0, tokenizer: change == "tokenizer" ? "unknown" : null,
            aliases: change == "alias-duplicate" ? ["same", "same"] : change == "empty-alias" ? [] : null,
            window: change == "window" ? 1 : change == "overflow" ? long.MinValue : 10000,
            framing: change == "framing" ? -1 : 5));
    }

    [Theory]
    [InlineData("deadline-timer")]
    [InlineData("deadline-monotonic-only")]
    [InlineData("negative-elapsed")]
    [InlineData("original-caller")]
    public async Task Final_preparation_metadata_cannot_release_after_deadline_or_original_cancellation(string mode)
    {
        using var caller = new CancellationTokenSource();
        var clock = new ManualTimestamp(); var adapter = new MetadataBoundaryAdapter();
        var gateway = new OrderedFailoverAiGateway(adapter);
        var gate = BoundaryGate(gateway, adapter, clock);
        adapter.OnDescribe = () =>
        {
            if (adapter.Descriptions != 2) return;
            if (mode == "original-caller") caller.Cancel();
            else clock.Advance(mode == "negative-elapsed" ? TimeSpan.FromSeconds(-1) : TimeSpan.FromSeconds(31),
                fireTimers: mode == "deadline-timer");
        };
        if (mode == "original-caller")
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.PrepareAsync(Request(), caller.Token));
            Assert.Equal(caller.Token, error.CancellationToken);
        }
        else await Denied(() => gate.PrepareAsync(Request(), caller.Token));
        Assert.Equal(2, adapter.Descriptions); Assert.Equal(0, adapter.Executions);
    }

    [Theory]
    [InlineData("original-caller")]
    [InlineData("lifetime-timer")]
    [InlineData("lifetime-monotonic-only")]
    public async Task Final_execution_metadata_cannot_release_cancelled_or_expired_success(string mode)
    {
        using var caller = new CancellationTokenSource();
        var clock = new ManualTimestamp(); var adapter = new MetadataBoundaryAdapter();
        var gateway = new OrderedFailoverAiGateway(adapter);
        var gate = BoundaryGate(gateway, adapter, clock);
        var handle = await gate.PrepareAsync(Request());
        adapter.OnDescribe = () =>
        {
            if (adapter.Descriptions != 4) return;
            if (mode == "original-caller") caller.Cancel();
            else clock.Advance(StructuredModelAdmission.MaximumPreparationLifetime, fireTimers: mode == "lifetime-timer");
        };
        if (mode == "original-caller")
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.ExecutePreparedAsync(handle, caller.Token));
            Assert.Equal(caller.Token, error.CancellationToken);
        }
        else await Denied(() => gate.ExecutePreparedAsync(handle, caller.Token));
        await Denied(() => gate.ExecutePreparedAsync(handle));
        Assert.Equal(4, adapter.Descriptions); Assert.Equal(1, adapter.Executions);
    }

    [Fact]
    public async Task Metadata_boundary_positive_control_keeps_same_gateway_and_single_execution()
    {
        var clock = new ManualTimestamp(); var adapter = new MetadataBoundaryAdapter();
        var gateway = new OrderedFailoverAiGateway(adapter); var gate = BoundaryGate(gateway, adapter, clock);
        var handle = await gate.PrepareAsync(Request());
        Assert.Equal("echo-model", (await gate.ExecutePreparedAsync(handle)).Model);
        Assert.Equal(4, adapter.Descriptions); Assert.Equal(1, adapter.Executions);
        await Denied(() => gate.ExecutePreparedAsync(handle)); Assert.Equal(1, adapter.Executions);
    }

    private static StructuredModelAdmission BoundaryGate(OrderedFailoverAiGateway gateway, MetadataBoundaryAdapter adapter, TimeProvider clock)
    {
        var metadata = gateway.DescribeStructuredGateway(); var route = metadata.Routes[0];
        var profile = new StructuredModelProfileCandidate(new AiModelProfile(route.ProviderId, route.ConfiguredModel,
            new HashSet<AiCapability> { AiCapability.StructuredGeneration }, 1, 0, 1), metadata.MetadataSha256,
            0, route.EndpointIdentitySha256, Hash(Schema), StructuredTextTokenCounter.Version, 1000, 5, 8, ["echo-model"], Fingerprint);
        adapter.Descriptions = 0;
        return new(gateway, StructuredModelProfileCatalog.ForSyntheticControls(profile), ConstantCounter(), clock);
    }

    private sealed class MetadataBoundaryAdapter : IAiProviderAdapter, IAiStructuredProviderMetadataSource
    {
        public string ProviderId => "boundary";
        internal int Descriptions, Executions;
        internal Action? OnDescribe;
        public bool Supports(AiCapability capability) => capability == AiCapability.StructuredGeneration;
        public AiStructuredRouteMetadata DescribeStructuredRoute()
        {
            Descriptions++; OnDescribe?.Invoke();
            return new(ProviderId, "configured", new string('B', 64), 8, TimeSpan.FromSeconds(60),
                new("synthetic-boundary-v1", 128000, 32000, 64000, 262144, 32, 10, 4));
        }
        public Task<AiGatewayResponse> ExecuteAsync(AiGatewayRequest request, CancellationToken cancellationToken = default)
        {
            Executions++;
            return Task.FromResult(new AiGatewayResponse(request.RequestId, request.Capability, "{}", "echo-model", 1, 0) { ProviderId = ProviderId });
        }
    }

    private static BoundedStructuredTextTokenCounter ConstantCounter() => new((text, _) => text == Schema ? 3 : 5, TimeSpan.FromSeconds(1));
    private static async Task Denied(Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(action);
        Assert.Equal(StructuredModelAdmission.UnavailableMessage, error.Message); Assert.Null(error.InnerException);
    }
    private sealed class Forged : IAiPreparedStructuredRequest { }
    private sealed class ManualTimestamp : TimeProvider
    {
        private long ticks;
        private readonly List<ManualTimer> timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime);
            lock (timers) timers.Add(timer);
            return timer;
        }
        internal void Advance(TimeSpan amount, bool fireTimers = true)
        {
            var now = Interlocked.Add(ref ticks, amount.Ticks);
            if (!fireTimers) return;
            ManualTimer[] due;
            lock (timers) due = timers.Where(timer => timer.TakeDue(now)).ToArray();
            foreach (var timer in due) timer.Fire();
        }
        private sealed class ManualTimer(ManualTimestamp clock, TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            private long at = clock.GetTimestamp() + due.Ticks;
            internal bool TakeDue(long now)
            {
                var next = Interlocked.Read(ref at);
                return next != long.MaxValue && now >= next && Interlocked.CompareExchange(ref at, long.MaxValue, next) == next;
            }
            internal void Fire() => callback(state);
            public bool Change(TimeSpan next, TimeSpan period) { Interlocked.Exchange(ref at, clock.GetTimestamp() + next.Ticks); return true; }
            public void Dispose() => Interlocked.Exchange(ref at, long.MaxValue);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient client;
        internal readonly OrderedFailoverAiGateway Gateway;
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        internal string LastBody = "";
        internal string? Model;
        internal long InputTokens = 1;
        internal long OutputTokens;
        internal bool FailPrimary;
        internal Action? BeforeReply;
        internal Fixture(bool backup = false)
        {
            client = new HttpClient(new Handler(async request =>
            {
                Interlocked.Increment(ref calls);
                LastBody = await request.Content!.ReadAsStringAsync();
                var primary = request.RequestUri!.AbsolutePath.Contains("primary", StringComparison.Ordinal);
                if (primary && FailPrimary) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                BeforeReply?.Invoke();
                var body = JsonSerializer.Serialize(new
                {
                    status = "completed",
                    model = Model ?? (primary ? "primary-observed" : "backup-observed"),
                    output = new[] { new { type = "message", status = "completed", role = "assistant", content = new[] { new { type = "output_text", text = "{}" } } } },
                    usage = new { input_tokens = InputTokens, output_tokens = OutputTokens, total_tokens = InputTokens + OutputTokens }
                });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }));
            Gateway = new(new OpenAiCompatibleResponsesAdapter(client, Options("primary", 8)),
                backup ? new OpenAiCompatibleResponsesAdapter(client, Options("backup", 16)) : null);
        }
        private static OpenAiCompatibleResponsesOptions Options(string provider, int output) =>
            new(new("https://provider.example/" + provider + "/responses"), provider + "-configured", "Bearer synthetic-secret", provider, output);
        internal StructuredModelProfileCandidate Profile(int index, long window = 10000, string? gatewayHash = null, string? endpointHash = null,
            string? schemaHash = null, string? model = null, int? output = null, int? ordinal = null,
            string? tokenizer = null, IReadOnlyList<string>? aliases = null, int framing = 5)
        {
            var metadata = Gateway.DescribeStructuredGateway(); var route = metadata.Routes[index];
            return new(new AiModelProfile(route.ProviderId, model ?? route.ConfiguredModel, new HashSet<AiCapability> { AiCapability.StructuredGeneration }, 1, 0, 1),
                gatewayHash ?? metadata.MetadataSha256, ordinal ?? index, endpointHash ?? route.EndpointIdentitySha256,
                schemaHash ?? Hash(Schema), tokenizer ?? StructuredTextTokenCounter.Version, window, framing,
                output ?? route.MaxOutputTokens, aliases ?? [route.ProviderId + "-observed"], Fingerprint);
        }
        internal StructuredModelProfileCatalog Catalog(long[]? windows = null, string? schema = null) =>
            StructuredModelProfileCatalog.ForSyntheticControls(Gateway.DescribeStructuredGateway().Routes.Select((_, i) => Profile(i, windows?[i] ?? 10000, schemaHash: schema)).ToArray());
        internal StructuredModelAdmission Gate(StructuredModelProfileCatalog catalog, BoundedStructuredTextTokenCounter? counter = null, TimeProvider? clock = null) =>
            new(Gateway, catalog, counter ?? new BoundedStructuredTextTokenCounter(), clock);
        public void Dispose() => client.Dispose();
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request);
    }
}
