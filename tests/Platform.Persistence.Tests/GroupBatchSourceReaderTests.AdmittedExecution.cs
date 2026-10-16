extern alias RuntimeWorker;

using MinhHuyAiOffice.Shared.Contracts;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostJournalWrapperReusesActualGatewayGateAndRechecksItsCurrentMetadata(bool drift)
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        var adapter = new AttemptAdmissionAdapter
        {
            Response = request => new(request.RequestId, request.Capability, "{}", "current-echo", 1, 0) { ProviderId = "current-provider" }
        };
        var gateway = new OrderedFailoverAiGateway(adapter); var metadata = gateway.DescribeStructuredGateway(); var route = metadata.Routes[0];
        var candidate = new StructuredModelProfileCandidate(new AiModelProfile(route.ProviderId, route.ConfiguredModel,
            new HashSet<AiCapability> { AiCapability.StructuredGeneration }, 1, 0, 1), metadata.MetadataSha256, 0, route.EndpointIdentitySha256,
            sidecar.Entries[0].Snapshot!.SchemaSha256, StructuredTextTokenCounter.Version, 10000, 5, 8, ["current-echo"], new('D', 64));
        var gate = new StructuredModelAdmission(gateway, StructuredModelProfileCatalog.ForSyntheticControls(candidate),
            new BoundedStructuredTextTokenCounter((_, _) => 1, TimeSpan.FromSeconds(1)), f.Auth.Clock);
        var issuer = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, gate, f.Auth.Clock);
        var issued = await issuer.PrepareAsync(plan, restored, sidecar); var permit = ExecutionGuardPermit(issued);
        adapter.MetadataChanged = drift;
        if (drift) await Assert.ThrowsAsync<InvalidOperationException>(() => issuer.ExecuteAfterCurrentJournalAsync(permit, default));
        else Assert.Equal(issued.RequestId.ToString("D"), (await issuer.ExecuteAfterCurrentJournalAsync(permit, default)).RequestId);
        Assert.Equal(drift ? 0 : 1, adapter.Executions);
        adapter.MetadataChanged = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => issuer.ExecuteAfterCurrentJournalAsync(permit, default));
        Assert.Equal(drift ? 0 : 1, adapter.Executions);
        // Real classes, synthetic catalog/adapter and a structural permit:
        // this is neither hosted SQL nor authentic provider/model evidence.
    }

    // These synthetic permits exercise the private post-journal gate wrapper
    // only. They are not committed SQL receipts or current release evidence.
    private static GroupModelAttemptPermit ExecutionGuardPermit(GroupModelAttemptAdmissionPreparer.PreparedAttempt issued)
    {
        var context = issued.Original.Context; var claim = context.Preparation.Context.Handle.Receipt;
        return new(context, new(context.Scope, context.BatchId, GroupModelAttemptBudget.InputIdentity(context), 1,
            issued.RequestId, null, claim.OperationId, claim.Epoch, issued.StartedAtUtc, issued.ExpiresAtUtc), issued.Original, issued);
    }

    [Fact]
    public async Task AdmittedExecutionPublicHostPathRequiresActualSqlBeforeAnyGateRelease()
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        var trap = new ModelFenceConnectionTrap(); using var database = V3StoreDatabase(trap);
        var gate = new AttemptAdmissionGate(); var issuer = new GroupModelAttemptAdmissionPreparer(database, gate, f.Auth.Clock);
        var issued = await issuer.PrepareAsync(plan, restored, sidecar); var permit = ExecutionGuardPermit(issued);
        var journal = new GroupModelAttemptJournalStore(database, f.Worker, f.Auth.Clock,
            new(database, f.Worker, f.Auth.Clock, f.Keys, new()), new(database, f.Worker, f.Auth.Clock, f.Keys, new()), issuer);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => journal.ExecuteAdmittedOriginalAsync(permit));
        Assert.Equal("Model dependency guard attempted SQL.", error.Message); Assert.Equal(1, trap.Attempts);
        Assert.Single(gate.Requests); Assert.Equal(1, gate.Descriptions); Assert.Equal(0, gate.Executions);
    }

    [Fact]
    public async Task PrivatePostJournalWrapperCallsSameGateExactRequestOnceOutsideSql()
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        var gate = new AttemptAdmissionGate(); var issuer = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, gate, f.Auth.Clock);
        var issued = await issuer.PrepareAsync(plan, restored, sidecar); var permit = ExecutionGuardPermit(issued);
        gate.Execute = (request, token) =>
        {
            Assert.Null(f.Auth.Db.Database.CurrentTransaction); Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
            Assert.False(token.IsCancellationRequested); Assert.Equal(permit.Context.CreateGatewayRequest(permit.Start.RequestId), request);
            return Task.FromResult(new AiGatewayResponse(request.RequestId, request.Capability, "{}", "echo", 1, 0) { ProviderId = "synthetic" });
        };
        var response = await issuer.ExecuteAfterCurrentJournalAsync(permit, default);
        Assert.Equal(issued.RequestId.ToString("D"), response.RequestId); Assert.Equal(1, gate.Executions); Assert.Equal(2, gate.Descriptions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => issuer.ExecuteAfterCurrentJournalAsync(permit, default));
        Assert.Equal(1, gate.Executions); Assert.Equal(2, gate.Descriptions);
    }

    [Fact]
    public async Task ConcurrentReleaseOfSameCarrierCallsGateAtMostOnce()
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        var gate = new AttemptAdmissionGate(); var issuer = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, gate, f.Auth.Clock);
        var issued = await issuer.PrepareAsync(plan, restored, sidecar); var permit = ExecutionGuardPermit(issued);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.Execute = async (request, _) =>
        {
            entered.SetResult(); await release.Task;
            return new(request.RequestId, request.Capability, "{}", "echo", 1, 0) { ProviderId = "synthetic" };
        };
        var first = issuer.ExecuteAfterCurrentJournalAsync(permit, default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => issuer.ExecuteAfterCurrentJournalAsync(permit, default)); }
        finally { release.SetResult(); }
        Assert.Equal(issued.RequestId.ToString("D"), (await first).RequestId); Assert.Equal(1, gate.Executions);
    }

    [Fact]
    public async Task MonotonicExpiryAfterProviderAwaitRejectsReplyWithUnchangedUtc()
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        var clock = new AttemptTimestampClock(f.Auth.Clock); var gate = new AttemptAdmissionGate();
        var issuer = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, gate, clock);
        var issued = await issuer.PrepareAsync(plan, restored, sidecar); var permit = ExecutionGuardPermit(issued);
        gate.Execute = (request, _) =>
        {
            clock.Timestamp += TimeSpan.FromMinutes(2).Ticks;
            return Task.FromResult(new AiGatewayResponse(request.RequestId, request.Capability, "{}", "echo", 1, 0) { ProviderId = "synthetic" });
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => issuer.ExecuteAfterCurrentJournalAsync(permit, default));
        Assert.Equal(issued.StartedAtUtc, f.Auth.Clock.Current); Assert.Equal(1, gate.Executions);
    }

    [Fact]
    public async Task ExecutionCancellationPreemptsDisposedDbAndForeignIssuerCannotConsumeCarrier()
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        var gate = new AttemptAdmissionGate(); var issuer = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, gate, f.Auth.Clock);
        var issued = await issuer.PrepareAsync(plan, restored, sidecar); var permit = ExecutionGuardPermit(issued);
        var foreign = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, gate, f.Auth.Clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => foreign.ExecuteAfterCurrentJournalAsync(permit, default));
        Assert.Equal(1, gate.Descriptions); Assert.Equal(0, gate.Executions);
        f.Auth.Db.Dispose(); using var caller = new CancellationTokenSource(); caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => issuer.ExecuteAfterCurrentJournalAsync(permit, caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Equal(0, gate.Executions);
    }

    [Fact]
    public async Task ProviderWaitExpiryCarriesExactIssuedObservationAcrossImmediateUtcRewind()
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        var clock = new AdmissionRewindClock(f.Auth.Clock); var gate = new AttemptAdmissionGate();
        var issuer = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, gate, clock);
        var issued = await issuer.PrepareAsync(plan, restored, sidecar); var permit = ExecutionGuardPermit(issued);
        gate.Execute = (request, _) =>
        {
            clock.Observation = permit.Context.Preparation.Context.Handle.Receipt.ExpiresAtUtc; clock.Armed = true;
            return Task.FromResult(new AiGatewayResponse(request.RequestId, request.Capability, "{}", "echo", 1, 0) { ProviderId = "synthetic" });
        };
        var error = await Assert.ThrowsAsync<GroupAdmittedExecutionExpiry>(() => issuer.ExecuteAfterCurrentJournalAsync(permit, default));
        Assert.Same(permit.Context.Preparation.Context.Handle, error.Observation.Handle);
        Assert.Equal(clock.Observation, error.Observation.ObservedAtUtc); Assert.True(clock.GetUtcNow() < error.Observation.ObservedAtUtc);
        issuer.RequireExecutionExpiry(error, permit, default);
        var foreign = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, gate, clock);
        Assert.Throws<InvalidOperationException>(() => foreign.RequireExecutionExpiry(error, permit, default));
        Assert.Throws<InvalidOperationException>(() => issuer.RequireExecutionExpiry(
            new GroupAdmittedExecutionExpiry(new object(), error.Observation), permit, default));
        Assert.Equal(1, gate.Executions); Assert.Equal("Group admitted model claim expired.", error.Message); Assert.Null(error.InnerException);
        // This proves private issuer/timestamp carriage only. Actual SQL
        // retirement and replacement-claim races require owned runtime proof.
    }

    [Theory]
    [InlineData("changed-current-snapshot")]
    [InlineData("expired-during-description")]
    [InlineData("cancel-during-description")]
    [InlineData("dirty-during-description")]
    [InlineData("private-description-failure")]
    public async Task CurrentGateRejectionConsumesWrapperWithoutProviderExecutionOrPrivateErrors(string change)
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        using var caller = new CancellationTokenSource(); var gate = new AttemptAdmissionGate();
        var issuer = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, gate, f.Auth.Clock);
        var issued = await issuer.PrepareAsync(plan, restored, sidecar); var permit = ExecutionGuardPermit(issued);
        gate.Describe = request =>
        {
            if (change == "expired-during-description") f.Auth.Clock.Current = permit.Start.DeadlineUtc;
            if (change == "cancel-during-description") caller.Cancel();
            if (change == "dirty-during-description") f.Auth.Binding.Version++;
            if (change == "private-description-failure") throw new Exception("OWNED_PRIVATE_RELEASE_CANARY");
            return change == "changed-current-snapshot" ? ChangedAttemptSnapshot(request) : SidecarSnapshot(request);
        };
        if (caller.IsCancellationRequested) throw new InvalidOperationException();
        if (change == "cancel-during-description")
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => issuer.ExecuteAfterCurrentJournalAsync(permit, caller.Token));
            Assert.Equal(caller.Token, error.CancellationToken);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => issuer.ExecuteAfterCurrentJournalAsync(permit, caller.Token));
            Assert.Equal("Group model attempt admission is not available.", error.Message); Assert.Null(error.InnerException);
        }
        Assert.Equal(0, gate.Executions); Assert.Equal(2, gate.Descriptions);
        // Rewind/release cleanup cannot resurrect a consumed wrapper.
        f.Auth.Clock.Current = issued.StartedAtUtc; f.Auth.Db.ChangeTracker.Clear(); gate.Describe = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => issuer.ExecuteAfterCurrentJournalAsync(permit, default));
        Assert.Equal(0, gate.Executions); Assert.Equal(2, gate.Descriptions);
    }

    [Theory]
    [InlineData("expired-reply")]
    [InlineData("utc-rewind-reply")]
    [InlineData("caller-cancel-reply")]
    [InlineData("dirty-reply")]
    [InlineData("wrong-request")]
    [InlineData("private-failure")]
    [InlineData("unowned-cancellation")]
    public async Task PostAwaitLifetimeCancellationIdentityAndPrivateFailureNeverReturnResponse(string change)
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        using var caller = new CancellationTokenSource(); var gate = new AttemptAdmissionGate();
        var issuer = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, gate, f.Auth.Clock);
        var issued = await issuer.PrepareAsync(plan, restored, sidecar); var permit = ExecutionGuardPermit(issued);
        gate.Execute = async (request, _) =>
        {
            await Task.Yield();
            if (change == "expired-reply") f.Auth.Clock.Current = permit.Start.DeadlineUtc;
            if (change == "utc-rewind-reply") f.Auth.Clock.Current = permit.Start.StartedAtUtc.AddTicks(-1);
            if (change == "caller-cancel-reply") caller.Cancel();
            if (change == "dirty-reply") f.Auth.Binding.Version++;
            if (change == "private-failure") throw new Exception("OWNED_PRIVATE_RESPONSE_CANARY");
            if (change == "unowned-cancellation") throw new OperationCanceledException("OWNED_PRIVATE_RESPONSE_CANARY");
            return new AiGatewayResponse(change == "wrong-request" ? "wrong" : request.RequestId, request.Capability, "{}", "echo", 1, 0) { ProviderId = "synthetic" };
        };
        if (change == "caller-cancel-reply")
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => issuer.ExecuteAfterCurrentJournalAsync(permit, caller.Token));
            Assert.Equal(caller.Token, error.CancellationToken);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => issuer.ExecuteAfterCurrentJournalAsync(permit, caller.Token));
            Assert.Equal("Group model attempt admission is not available.", error.Message); Assert.Null(error.InnerException);
        }
        Assert.Equal(1, gate.Executions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedProviderFailureRetainsOnlyExistingRetryBoolean(bool transient)
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        var gate = new AttemptAdmissionGate(); var issuer = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, gate, f.Auth.Clock);
        var issued = await issuer.PrepareAsync(plan, restored, sidecar); var permit = ExecutionGuardPermit(issued);
        var original = new ExecutionTypedFailure(() => transient);
        gate.Execute = (_, _) => throw original;
        var error = await Assert.ThrowsAsync<GroupAdmittedProviderFailure>(() => issuer.ExecuteAfterCurrentJournalAsync(permit, default));
        Assert.Equal(transient, error.IsTransient); Assert.Equal(1, original.Reads);
        Assert.Equal("AI group model execution was not confirmed.", error.Message); Assert.Null(error.InnerException);
        Assert.True(GroupModelAttemptProviderFailure.TryClassify(error, out var kind));
        Assert.Equal(transient ? GroupModelAttemptFailure.TransientProvider : GroupModelAttemptFailure.PermanentProvider, kind);
        Assert.Equal(1, gate.Executions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FaultyOrCancellingTypedFailureMetadataDoesNotEscapePrivateDetails(bool cancelling)
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        using var caller = new CancellationTokenSource(); var gate = new AttemptAdmissionGate();
        var issuer = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, gate, f.Auth.Clock);
        var issued = await issuer.PrepareAsync(plan, restored, sidecar); var permit = ExecutionGuardPermit(issued);
        var original = new ExecutionTypedFailure(() =>
        {
            if (cancelling) { caller.Cancel(); return true; }
            throw new Exception("OWNED_PRIVATE_METADATA_CANARY");
        });
        gate.Execute = (_, _) => throw original;
        if (cancelling)
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => issuer.ExecuteAfterCurrentJournalAsync(permit, caller.Token));
            Assert.Equal(caller.Token, error.CancellationToken);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => issuer.ExecuteAfterCurrentJournalAsync(permit, caller.Token));
            Assert.Equal("Group model attempt admission is not available.", error.Message); Assert.Null(error.InnerException);
        }
        Assert.Equal(1, original.Reads); Assert.Equal(1, gate.Executions);
    }

    private sealed class ExecutionTypedFailure(Func<bool> read) : Exception, IAiGatewayProviderFailure
    {
        internal int Reads;
        public bool IsTransient { get { Reads++; return read(); } }
        public override string Message => throw new Exception("PRIVATE_MESSAGE_GETTER");
        public override string ToString() => throw new Exception("PRIVATE_ERROR_FORMATTER");
    }
}
