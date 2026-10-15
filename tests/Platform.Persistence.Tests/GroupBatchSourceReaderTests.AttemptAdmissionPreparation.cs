extern alias RuntimeWorker;
using System.Security.Cryptography;
using System.Text;
using System.Transactions;
using MinhHuyAiOffice.Shared.Contracts;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttemptAdmissionUsesActualExistingGatewayGateAndCannotBypassEmptyProductionCatalog(bool syntheticCatalog)
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        var adapter = new AttemptAdmissionAdapter(); var gateway = new OrderedFailoverAiGateway(adapter);
        var counted = new List<string>();
        var counter = new BoundedStructuredTextTokenCounter((text, _) => { counted.Add(text); return 1; }, TimeSpan.FromSeconds(1));
        var metadata = gateway.DescribeStructuredGateway(); var route = metadata.Routes[0];
        var candidate = new StructuredModelProfileCandidate(new AiModelProfile(route.ProviderId, route.ConfiguredModel,
            new HashSet<AiCapability> { AiCapability.StructuredGeneration }, 1, 0, 1), metadata.MetadataSha256, 0,
            route.EndpointIdentitySha256, sidecar.Entries[0].Snapshot!.SchemaSha256, StructuredTextTokenCounter.Version,
            10000, 5, 8, ["current-echo"], new('D', 64));
        IAiStructuredRequestAdmission gate = syntheticCatalog
            ? new StructuredModelAdmission(gateway, StructuredModelProfileCatalog.ForSyntheticControls(candidate), counter, f.Auth.Clock)
            : new StructuredModelAdmission(gateway);
        var service = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, gate, f.Auth.Clock);
        if (syntheticCatalog)
        {
            var issued = await service.PrepareAsync(plan, restored, sidecar);
            var request = restored.Current.ModelContext!.CreateGatewayRequest(issued.RequestId);
            Assert.Equal(new[] { request.Input, request.ResponseSchema }, counted);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Input))), issued.Snapshot.InputSha256);
            Assert.Equal("current-model", issued.Snapshot.Routes[0].ConfiguredModel);
            Assert.Null(service.InspectIssuedLocked(issued, issued.Original.Context));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(plan, restored, sidecar));
            Assert.Empty(counted);
        }
        Assert.Equal(0, adapter.Executions);
    }

    [Fact]
    public async Task AttemptAdmissionDerivesFreshNonceAndPreservesOriginalInputAcrossCurrentRouteChange()
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        var gate = new AttemptAdmissionGate { Describe = request => ChangedAttemptSnapshot(request) };
        var service = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, gate, f.Auth.Clock);
        var counts = await f.CountsAsync(); var keys = f.Keys.Reads;
        var first = await service.PrepareAsync(GroupAutomaticInputPlan.Read(plan.Encode()), restored,
            GroupOriginalAdmissionSidecar.Read(sidecar.Encode()));
        var second = await service.PrepareAsync(plan, restored, sidecar);
        Assert.NotEqual(first.RequestId, second.RequestId); Assert.NotEqual(plan.Chunks[0].OperationId, first.RequestId);
        Assert.Equal(2, gate.Requests.Count); Assert.Equal(0, gate.Executions);
        var request = gate.Requests[0];
        Assert.Equal(first.RequestId.ToString("D"), request.RequestId);
        Assert.Equal(restored.Current.ModelContext!.Scope.TenantId.ToString("D"), request.TenantId);
        Assert.Equal(restored.Current.ModelContext.Scope.CompanyId.ToString("D"), request.CompanyId);
        Assert.Equal(plan.Dependencies.BatchId.ToString("D"), request.TaskId);
        var expected = restored.Current.ModelContext.CreateGatewayRequest(first.RequestId);
        Assert.Equal(expected, request); Assert.Equal(sidecar.Sha256, first.Sidecar.Sha256);
        Assert.Equal(plan.Sha256, first.Original.Plan.Sha256); Assert.Equal(restored.Original.Ordinal, first.Ordinal);
        Assert.Equal("current-model", first.Snapshot.Routes[0].ConfiguredModel);
        Assert.NotEqual(sidecar.Entries[0].Snapshot!.GatewaySha256, first.Snapshot.GatewaySha256);
        Assert.Equal(sidecar.Entries[0].Snapshot!.InputSha256, first.Snapshot.InputSha256);
        Assert.Equal(sidecar.Entries[0].Snapshot!.SchemaSha256, first.Snapshot.SchemaSha256);
        Assert.Null(service.InspectIssuedLocked(first, restored.Current.ModelContext));
        Assert.Equal(2, gate.Descriptions); Assert.Equal(counts, await f.CountsAsync()); Assert.Equal(keys, f.Keys.Reads);
        Assert.DoesNotContain("original private", first.ToString(), StringComparison.Ordinal);
        Assert.True(service.UsesContext(f.Auth.Db, f.Auth.Clock));
        Assert.False(service.UsesContext(f.Auth.Db, TimeProvider.System));
    }

    [Theory]
    [InlineData("old-input")]
    [InlineData("old-schema")]
    [InlineData("other-plan")]
    [InlineData("legacy")]
    [InlineData("host-only")]
    [InlineData("dirty-context")]
    [InlineData("ambient")]
    public async Task AttemptAdmissionRefusesIncompatibleHistoryAndSqlUnitBeforeCallingGate(string change)
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        using var secondary = change is "other-plan" or "host-only" ? new Fixture() : null;
        var context = restored.Current.ModelContext!;
        if (change is "old-input" or "old-schema")
        {
            var request = context.CreateGatewayRequest(Guid.NewGuid());
            sidecar = GroupOriginalAdmissionSidecar.Create(plan, context.Preparation.Context.Handle,
                [SidecarSnapshot(change == "old-input" ? request with { Input = "different" } : request with { ResponseSchema = "different" })]);
        }
        if (change == "other-plan")
        {
            var (preparation, brain, selection) = await IssuedDisclosure(secondary!, "different source");
            var other = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []);
            var request = Assert.Single(other.RestoreChunks(preparation, brain, PlanProfile, [])).Current.ModelContext!.CreateGatewayRequest(Guid.NewGuid());
            sidecar = GroupOriginalAdmissionSidecar.Create(other, preparation.Context.Handle, [SidecarSnapshot(request)]);
        }
        if (change == "legacy") plan = GroupAutomaticInputPlan.Create(context.Preparation, context.Brain, PlanProfile, "model", 64000, []);
        if (change == "host-only")
        {
            var (preparation, brain, selection) = await IssuedDisclosure(secondary!, "password=OWNED_ATTEMPT_CANARY");
            plan = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, null, 0, []);
            restored = Assert.Single(plan.RestoreChunks(preparation, brain, PlanProfile, []));
            sidecar = GroupOriginalAdmissionSidecar.Create(plan, preparation.Context.Handle, [null]);
        }
        if (change == "dirty-context") f.Auth.Binding.Version++;
        using var ambient = change == "ambient" ? new TransactionScope(TransactionScopeAsyncFlowOption.Enabled) : null;
        var gate = new AttemptAdmissionGate();
        var service = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, gate, f.Auth.Clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(plan, restored, sidecar));
        Assert.Empty(gate.Requests); Assert.Equal(0, gate.Descriptions); Assert.Equal(0, gate.Executions);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("schema")]
    [InlineData("null-snapshot")]
    [InlineData("dirty-after-await")]
    [InlineData("dirty-after-describe")]
    [InlineData("utc-rewind")]
    [InlineData("preparation-deadline")]
    [InlineData("claim-deadline")]
    [InlineData("caller-cancel")]
    [InlineData("private-error")]
    [InlineData("unowned-cancel")]
    public async Task AttemptAdmissionRefusesFinalDriftCancellationAndPrivateFailuresWithoutExecution(string change)
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        using var caller = new CancellationTokenSource();
        var gate = new AttemptAdmissionGate();
        gate.Prepare = _ =>
        {
            if (change == "dirty-after-await") f.Auth.Binding.Version++;
            return Task.CompletedTask;
        };
        gate.Describe = request =>
        {
            if (change == "private-error") throw new Exception("OWNED_PRIVATE_PROVIDER_CANARY");
            if (change == "unowned-cancel") throw new OperationCanceledException("OWNED_PRIVATE_PROVIDER_CANARY");
            if (change == "caller-cancel") caller.Cancel();
            if (change == "dirty-after-describe") f.Auth.Binding.Version++;
            if (change == "utc-rewind") f.Auth.Clock.Current -= TimeSpan.FromTicks(1);
            if (change == "preparation-deadline") f.Auth.Clock.Current += GroupModelAttemptAdmissionPreparer.PreparationBudget;
            if (change == "claim-deadline") f.Auth.Clock.Current = restored.Current.ModelContext!.Preparation.Context.Handle.Receipt.ExpiresAtUtc;
            return change == "null-snapshot" ? null! : SidecarSnapshot(change == "input" ? request with { Input = "different" }
                : change == "schema" ? request with { ResponseSchema = "different" } : request);
        };
        var service = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, gate, f.Auth.Clock);
        if (change == "caller-cancel")
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareAsync(plan, restored, sidecar, caller.Token));
            Assert.Equal(caller.Token, error.CancellationToken);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(plan, restored, sidecar, caller.Token));
            Assert.Equal("Group model attempt admission is not available.", error.Message); Assert.Null(error.InnerException);
        }
        Assert.Single(gate.Requests); Assert.Equal(0, gate.Executions);
    }

    [Theory]
    [InlineData("foreign-issuer")]
    [InlineData("forged-carrier")]
    [InlineData("other-context")]
    [InlineData("utc-rewind")]
    [InlineData("lifetime")]
    [InlineData("caller-cancel")]
    public async Task AttemptAdmissionSqlFenceRejectsForeignOrExpiredIssuanceWithoutGateOrDatabaseWork(string change)
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        var gate = new AttemptAdmissionGate(); var service = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, gate, f.Auth.Clock);
        var issued = await service.PrepareAsync(plan, restored, sidecar); var context = issued.Original.Context;
        if (change == "foreign-issuer") service = new(f.Auth.Db, gate, f.Auth.Clock);
        if (change == "forged-carrier") issued = new(new object(), issued.Original, issued.Sidecar, issued.Ordinal, issued.RequestId,
            issued.Snapshot, new AttemptAdmissionHandle(), issued.Started, issued.StartedAtUtc, issued.ExpiresAtUtc);
        if (change == "other-context") context = GroupAutomaticModelContext.Create(context.Preparation, context.Brain, "model", 64000);
        if (change == "utc-rewind") f.Auth.Clock.Current -= TimeSpan.FromTicks(1);
        if (change == "lifetime") f.Auth.Clock.Current = issued.ExpiresAtUtc;
        using var caller = new CancellationTokenSource(); if (change == "caller-cancel") caller.Cancel();
        if (change == "caller-cancel")
            Assert.Throws<OperationCanceledException>(() => service.InspectIssuedLocked(issued, context, caller.Token));
        else Assert.Throws<InvalidOperationException>(() => service.InspectIssuedLocked(issued, context, caller.Token));
        Assert.Single(gate.Requests); Assert.Equal(1, gate.Descriptions); Assert.Equal(0, gate.Executions);
    }

    [Fact]
    public async Task AttemptAdmissionPreservesExactClaimExpiryObservationWhenNextClockReadRewinds()
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        var clock = new AdmissionRewindClock(f.Auth.Clock); var gate = new AttemptAdmissionGate();
        var service = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, gate, clock);
        var issued = await service.PrepareAsync(plan, restored, sidecar);
        clock.Observation = restored.Current.ModelContext!.Preparation.Context.Handle.Receipt.ExpiresAtUtc; clock.Armed = true;
        var observation = service.InspectIssuedLocked(issued, issued.Original.Context);
        Assert.NotNull(observation); Assert.Same(issued.Original.Context.Preparation.Context.Handle, observation.Handle);
        Assert.Equal(clock.Observation, observation.ObservedAtUtc); Assert.True(clock.GetUtcNow() < observation.ObservedAtUtc);
        Assert.Single(gate.Requests); Assert.Equal(1, gate.Descriptions); Assert.Equal(0, gate.Executions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttemptAdmissionMonotonicClockAlsoBoundsPreparationAndSqlFence(bool afterPreparation)
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        var clock = new AttemptTimestampClock(f.Auth.Clock); var gate = new AttemptAdmissionGate();
        var service = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, gate, clock);
        if (afterPreparation)
        {
            var issued = await service.PrepareAsync(plan, restored, sidecar);
            clock.Timestamp += TimeSpan.FromMinutes(2).Ticks;
            Assert.Throws<InvalidOperationException>(() => service.InspectIssuedLocked(issued, issued.Original.Context));
        }
        else
        {
            gate.Describe = request => { clock.Timestamp += TimeSpan.FromSeconds(30).Ticks; return SidecarSnapshot(request); };
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(plan, restored, sidecar));
        }
        Assert.Equal(0, gate.Executions);
    }

    [Fact]
    public async Task AttemptAdmissionHonorsOriginalCallerCancellationBeforeContextAccess()
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        var gate = new AttemptAdmissionGate(); var service = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, gate, f.Auth.Clock);
        f.Auth.Db.Dispose(); using var caller = new CancellationTokenSource(); caller.Cancel();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => service.PrepareAsync(plan, restored, sidecar, caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Empty(gate.Requests);
    }

    private static async Task<(GroupAutomaticInputPlan Plan, GroupAutomaticInputPlan.RestoredChunk Restored,
        GroupOriginalAdmissionSidecar Sidecar)> AttemptAdmissionInput(Fixture f)
    {
        var (preparation, brain, selection) = await IssuedDisclosure(f);
        var plan = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []);
        var restored = Assert.Single(plan.RestoreChunks(preparation, brain, PlanProfile, []));
        var sidecar = GroupOriginalAdmissionSidecar.Create(plan, preparation.Context.Handle,
            [SidecarSnapshot(restored.Current.ModelContext!.CreateGatewayRequest(Guid.NewGuid()))]);
        return (plan, restored, sidecar);
    }

    private static AiStructuredAdmissionSnapshot ChangedAttemptSnapshot(AiGatewayRequest request)
    {
        var old = SidecarSnapshot(request);
        var route = new AiStructuredAdmissionRoute(0, "current-provider", "current-model", new('E', 64), new('F', 64), new('1', 64),
            20000, 3, 12, 8, ["current-echo"]);
        return new(new('2', 64), TimeSpan.FromSeconds(45).Ticks, old.InputSha256, old.InputBytes, old.SchemaSha256,
            old.SchemaBytes, "current-tokenizer", 2, 3, [route]);
    }

    private sealed class AttemptTimestampClock(TimeProvider shared) : TimeProvider
    {
        internal long Timestamp = TimeSpan.FromHours(1).Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => shared.GetUtcNow();
    }

    private sealed class AttemptAdmissionHandle(AiGatewayRequest? request = null) : IAiPreparedStructuredRequest
    { internal AiGatewayRequest? Request { get; } = request; }
    private sealed class AttemptAdmissionAdapter : IAiProviderAdapter, IAiStructuredProviderMetadataSource
    {
        internal int Executions;
        internal bool MetadataChanged;
        internal Func<AiGatewayRequest, AiGatewayResponse>? Response;
        public string ProviderId => "current-provider";
        public bool Supports(AiCapability capability) => capability == AiCapability.StructuredGeneration;
        public AiStructuredRouteMetadata DescribeStructuredRoute() => new(ProviderId, MetadataChanged ? "changed-model" : "current-model", new('B', 64), 8,
            TimeSpan.FromSeconds(60), new("synthetic-attempt-v1", 128000, 32000, 64000, 262144, 32, 10, 4));
        public Task<AiGatewayResponse> ExecuteAsync(AiGatewayRequest request, CancellationToken cancellationToken = default)
        {
            Executions++; if (Response is not null) return Task.FromResult(Response(request));
            throw new InvalidOperationException("Preparation cannot execute a provider.");
        }
    }
    private sealed class AttemptAdmissionGate : IAiStructuredRequestAdmission
    {
        internal readonly List<AiGatewayRequest> Requests = [];
        internal Func<AiGatewayRequest, Task>? Prepare;
        internal Func<AiGatewayRequest, AiStructuredAdmissionSnapshot>? Describe;
        internal Func<AiGatewayRequest, CancellationToken, Task<AiGatewayResponse>>? Execute;
        internal int Descriptions;
        internal int Executions;
        public async Task<IAiPreparedStructuredRequest> PrepareAsync(AiGatewayRequest request, CancellationToken cancellationToken = default)
        { Requests.Add(request); if (Prepare is not null) await Prepare(request); return new AttemptAdmissionHandle(request); }
        public AiStructuredAdmissionSnapshot DescribePrepared(IAiPreparedStructuredRequest handle, CancellationToken cancellationToken = default)
        {
            Descriptions++; var request = ((AttemptAdmissionHandle)handle).Request ?? throw new InvalidOperationException();
            return Describe is null ? SidecarSnapshot(request) : Describe(request);
        }
        public Task<AiGatewayResponse> ExecutePreparedAsync(IAiPreparedStructuredRequest handle, CancellationToken cancellationToken = default)
        {
            Executions++;
            if (Execute is not null) return Execute(((AttemptAdmissionHandle)handle).Request!, cancellationToken);
            throw new InvalidOperationException("Preparation cannot execute a provider.");
        }
    }
}
