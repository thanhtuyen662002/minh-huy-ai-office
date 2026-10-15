using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalAdmissionPreparerDerivesItsOwnRequestsAndIssuesOnlyItsOwnPersistenceCarrier(bool hostOnly)
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f, hostOnly ? "password=OWNED_PREP_CANARY" : "Tra cứu tồn kho 😀");
        var original = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, hostOnly ? null : "model", hostOnly ? 0 : 64000, []);
        var gate = new SyntheticAdmission(); var service = new GroupOriginalAdmissionPreparer(gate, f.Auth.Clock);
        var before = await f.CountsAsync(); var keys = f.Keys.Reads;
        var issued = await service.PrepareAsync(original, preparation, brain, []);
        service.RequireIssued(issued, preparation, brain);
        issued.Sidecar.RequirePlan(original);
        Assert.Same(original, issued.Original); Assert.Equal(hostOnly ? 0 : 1, gate.Requests.Count);
        if (!hostOnly)
        {
            var request = Assert.Single(gate.Requests);
            Assert.Equal(original.Chunks[0].OperationId.ToString("D"), request.RequestId);
            Assert.Equal(preparation.Scope.TenantId.ToString("D"), request.TenantId);
            Assert.Equal(preparation.BatchId.ToString("D"), request.TaskId);
        }
        var foreign = new GroupOriginalAdmissionPreparer(gate, f.Auth.Clock);
        Assert.Throws<InvalidOperationException>(() => foreign.RequireIssued(issued, preparation, brain));
        var forged = new GroupOriginalAdmissionPreparer.PreparedOriginal(new object(), original, preparation, brain,
            GroupOriginalAdmissionSidecar.Read(issued.Sidecar.Encode()), issued.Started, issued.StartedAtUtc, issued.ExpiresAtUtc);
        Assert.Throws<InvalidOperationException>(() => service.RequireIssued(forged, preparation, brain));
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(keys, f.Keys.Reads); Assert.Equal(0, gate.Executions);
    }

    [Theory]
    [InlineData("decoded")]
    [InlineData("legacy")]
    [InlineData("foreign-brain")]
    public async Task OriginalAdmissionPreparerCannotUpgradeUnissuedOrLegacySelectionBeforeGate(string change)
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f);
        var original = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []);
        if (change == "decoded") original = GroupAutomaticInputPlan.Read(original.Encode());
        if (change == "legacy") original = GroupAutomaticInputPlan.Create(preparation, brain, PlanProfile, "model", 64000, []);
        var gate = new SyntheticAdmission(); var service = new GroupOriginalAdmissionPreparer(gate, f.Auth.Clock);
        if (change == "foreign-brain")
        {
            var claim = preparation.Context.Handle;
            brain = new(new(claim.Receipt, claim.Authority), brain.Snapshots.ToArray(), brain.Items.ToArray());
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(original, preparation, brain, []));
        Assert.Empty(gate.Requests); Assert.Equal(0, gate.Executions);
    }

    [Theory]
    [InlineData("wrong-input")]
    [InlineData("wrong-schema")]
    [InlineData("caller-cancel")]
    [InlineData("expired")]
    [InlineData("rollback")]
    public async Task OriginalAdmissionPreparerRefusesMismatchedMeasurementsAndFinalCancellationOrClock(string change)
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f);
        var original = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []);
        using var caller = new CancellationTokenSource();
        var gate = new SyntheticAdmission(); var service = new GroupOriginalAdmissionPreparer(gate, f.Auth.Clock);
        gate.Describe = request =>
        {
            if (change == "caller-cancel") caller.Cancel();
            if (change == "expired") f.Auth.Clock.Current += TimeSpan.FromMinutes(3);
            if (change == "rollback") f.Auth.Clock.Current -= TimeSpan.FromSeconds(1);
            return SidecarSnapshot(change == "wrong-input" ? request with { Input = "different" }
                : change == "wrong-schema" ? request with { ResponseSchema = "different" } : request);
        };
        if (change == "caller-cancel")
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareAsync(original, preparation, brain, [], caller.Token));
            Assert.Equal(caller.Token, error.CancellationToken);
        }
        else await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(original, preparation, brain, [], caller.Token));
        Assert.Equal(0, gate.Executions);
    }

    [Fact]
    public async Task OriginalAdmissionRetainsAuthenticatedExpiryObservationEvenWhenClockImmediatelyRewinds()
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f);
        var original = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []);
        var clock = new AdmissionRewindClock(f.Auth.Clock);
        var gate = new SyntheticAdmission(); var service = new GroupOriginalAdmissionPreparer(gate, clock);
        var issued = await service.PrepareAsync(original, preparation, brain, []);
        var before = await f.CountsAsync(); var keys = f.Keys.Reads;
        clock.Observation = preparation.Context.Handle.Receipt.ExpiresAtUtc; clock.Armed = true;
        var observed = service.InspectIssuedLocked(issued, preparation, brain);
        Assert.NotNull(observed); Assert.Same(preparation.Context.Handle, observed.Handle);
        Assert.Equal(preparation.Context.Handle.Receipt.ExpiresAtUtc, observed.ObservedAtUtc);
        Assert.True(clock.GetUtcNow() < observed.ObservedAtUtc);
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(keys, f.Keys.Reads); Assert.Equal(0, gate.Executions);
    }

    private sealed class AdmissionRewindClock(TimeProvider shared) : TimeProvider
    {
        internal bool Armed;
        internal DateTimeOffset Observation;
        public override DateTimeOffset GetUtcNow()
        {
            if (!Armed) return shared.GetUtcNow();
            Armed = false; return Observation;
        }
    }

    private sealed class SyntheticAdmission : IAiStructuredRequestAdmission
    {
        internal readonly List<AiGatewayRequest> Requests = [];
        internal Func<AiGatewayRequest, AiStructuredAdmissionSnapshot>? Describe;
        internal int Executions;
        public Task<IAiPreparedStructuredRequest> PrepareAsync(AiGatewayRequest request, CancellationToken cancellationToken = default)
        { Requests.Add(request); return Task.FromResult<IAiPreparedStructuredRequest>(new SyntheticHandle(request)); }
        public AiStructuredAdmissionSnapshot DescribePrepared(IAiPreparedStructuredRequest handle, CancellationToken cancellationToken = default)
        { var request = ((SyntheticHandle)handle).Request; return Describe?.Invoke(request) ?? SidecarSnapshot(request); }
        public Task<AiGatewayResponse> ExecutePreparedAsync(IAiPreparedStructuredRequest handle, CancellationToken cancellationToken = default)
        { Executions++; throw new InvalidOperationException("Preparation must never execute a provider."); }
        private sealed record SyntheticHandle(AiGatewayRequest Request) : IAiPreparedStructuredRequest;
    }
}
