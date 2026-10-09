using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

internal static class SubmissionIntentFixture
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);
    internal static Guid Seed(PlatformDbContext db, AuthorizationContext owner)
    {
        MemberDirectoryFixture.Seed(db, owner, role: null);
        var sourceId = Guid.NewGuid();
        db.DataSources.Add(new()
        {
            TenantId = owner.TenantId,
            CompanyId = owner.CompanyId,
            Id = sourceId,
            LogicalName = "intent-source",
            Kind = "erp",
            Environment = "test",
            Purpose = "owned test",
            ConnectionSecretReference = "secretref://env/INTENT_TEST_ERP",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now
        });
        BindingFixture.Grant(db, owner.TenantId, owner.CompanyId, "secretref://env/INTENT_TEST_ERP");
        return sourceId;
    }
    internal static TaskSubmissionPrepareRequest Request(Guid sourceId) => new(Guid.NewGuid(), sourceId, "Tồn kho 😀 �");
    internal static TaskSubmissionIntentRecord Row(AuthorizationContext owner, TaskSubmissionPrepareRequest request) => new()
    {
        TenantId = owner.TenantId,
        CompanyId = owner.CompanyId,
        UserId = owner.UserId,
        OperationId = request.OperationId,
        DataSourceId = request.DataSourceId,
        Question = request.Question,
        InputVersion = 1,
        MaxAttempts = 3,
        InputFingerprint = TaskSubmissionIntentIdentity.Fingerprint(request),
        CreatedAtUtc = Now,
        ExpiresAtUtc = Now.AddHours(24)
    };
    internal sealed class Clock : TimeProvider
    {
        internal DateTimeOffset NowUtc { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => NowUtc;
    }
    internal static string Effects(PlatformDbContext db) => JsonSerializer.Serialize(new
    {
        Tasks = db.Tasks.AsNoTracking().OrderBy(row => row.Id).ToArray(),
        Steps = db.TaskSteps.AsNoTracking().OrderBy(row => row.Id).ToArray(),
        Executions = db.TaskStepExecutions.AsNoTracking().OrderBy(row => row.StepId).ToArray(),
        Dispatches = db.TaskDispatches.AsNoTracking().OrderBy(row => row.MessageId).ToArray(),
        Events = db.TaskEvents.AsNoTracking().OrderBy(row => row.TaskId).ThenBy(row => row.Sequence).ToArray()
    });
}

public sealed class TaskSubmissionIntentServiceTests
{
    [Fact]
    public async Task PreparationPersistsExactImmutableInputWithoutAnyTaskOrExecutionAndDoesNotRenew()
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var owner = MemberDirectoryFixture.Authority();
        var source = SubmissionIntentFixture.Seed(db, owner); await db.SaveChangesAsync();
        var clock = new SubmissionIntentFixture.Clock(); var service = Service(db, clock); var input = SubmissionIntentFixture.Request(source);
        var before = SubmissionIntentFixture.Effects(db);
        var first = await service.PrepareAsync(owner, input);
        clock.NowUtc = clock.NowUtc.AddHours(12);
        var replay = await service.PrepareAsync(owner, input);
        Assert.Equal(first, replay); Assert.Equal(TaskSubmissionIntentState.Prepared, first.State);
        Assert.Equal(input.Question, first.Question); Assert.Equal(input.OperationId, first.OperationId);
        Assert.Equal(TaskSubmissionIntentIdentity.Fingerprint(input), first.InputFingerprint);
        Assert.Equal(SubmissionIntentFixture.Now, first.CreatedAtUtc); Assert.Equal(SubmissionIntentFixture.Now.AddHours(24), first.ExpiresAtUtc);
        Assert.Null(first.Accepted); Assert.Single(db.TaskSubmissionIntents); Assert.Equal(before, SubmissionIntentFixture.Effects(db));
    }

    [Fact]
    public async Task DeliberateSubmitReplayAndReadOnlyReloadUseTheOneOriginalTaskGraph()
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var owner = MemberDirectoryFixture.Authority();
        var source = SubmissionIntentFixture.Seed(db, owner); await db.SaveChangesAsync(); var service = Service(db);
        var input = SubmissionIntentFixture.Request(source); var prepared = await service.PrepareAsync(owner, input);
        var execute = new TaskSubmissionExecuteRequest(prepared.InputFingerprint!);
        var first = await service.ExecuteAsync(owner, input.OperationId, execute);
        var replay = await service.ExecuteAsync(owner, input.OperationId, execute);
        Assert.Equal(first, replay); Assert.Equal(owner.CompanyId, first.CompanyId); Assert.Equal(input.OperationId, first.OperationId);
        var expected = PilotTaskIdentity.ForTask(owner, TaskSubmissionIntentIdentity.IdempotencyKey(input.OperationId));
        Assert.Equal(expected, first.TaskId); Assert.Equal(PilotTaskIdentity.ForStep(expected), first.StepId);
        Assert.Equal(PilotTaskIdentity.ForMessage(first.TaskId, first.StepId), first.MessageId);
        Assert.Single(db.Tasks); Assert.Single(db.TaskSteps); Assert.Single(db.TaskStepExecutions); Assert.Single(db.TaskDispatches); Assert.Single(db.TaskEvents);
        var before = SubmissionIntentFixture.Effects(db);
        var restored = (await Service(db).GetAsync(owner, input.OperationId))!;
        Assert.Equal(TaskSubmissionIntentState.Accepted, restored.State); Assert.Equal(first, restored.Accepted);
        Assert.Equal(input.Question, restored.Question); Assert.Equal(restored, Assert.Single((await Service(db).ListAsync(owner)).Items));
        Assert.Equal(before, SubmissionIntentFixture.Effects(db));
        db.Tasks.Local.Single().Status = TaskExecutionStatus.Completed;
        db.TaskDispatches.Local.Single().State = WorkDispatchState.Acknowledged;
        await db.SaveChangesAsync();
        var advanced = await service.ExecuteAsync(owner, input.OperationId, execute);
        Assert.Equal(first.TaskId, advanced.TaskId); Assert.Equal(first.StepId, advanced.StepId); Assert.Equal(first.MessageId, advanced.MessageId);
        Assert.Equal(TaskExecutionStatus.Completed, advanced.Status); Assert.Equal(WorkDispatchState.Acknowledged, advanced.DispatchState);
    }

    [Theory]
    [InlineData("question")]
    [InlineData("source")]
    public async Task SameOperationChangedInputConflictsWithoutEffects(string changed)
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var owner = MemberDirectoryFixture.Authority();
        var source = SubmissionIntentFixture.Seed(db, owner); await db.SaveChangesAsync(); var service = Service(db);
        var input = SubmissionIntentFixture.Request(source); var first = await service.PrepareAsync(owner, input);
        var before = JsonSerializer.Serialize(await db.TaskSubmissionIntents.AsNoTracking().ToArrayAsync());
        var conflicting = changed == "question" ? input with { Question = "Tồn kho khác" } : input with { DataSourceId = Guid.NewGuid() };
        if (changed == "source")
        {
            var original = await db.DataSources.AsNoTracking().SingleAsync();
            db.DataSources.Add(new()
            {
                TenantId = owner.TenantId,
                CompanyId = owner.CompanyId,
                Id = conflicting.DataSourceId,
                LogicalName = "second",
                Kind = original.Kind,
                Environment = original.Environment,
                Purpose = original.Purpose,
                ConnectionSecretReference = original.ConnectionSecretReference
            }); await db.SaveChangesAsync();
        }
        Assert.Equal("operation-conflict", (await Assert.ThrowsAsync<TaskSubmissionIntentConflictException>(() => service.PrepareAsync(owner, conflicting))).Code);
        Assert.Equal(before, JsonSerializer.Serialize(await db.TaskSubmissionIntents.AsNoTracking().ToArrayAsync()));
        Assert.Equal(first, await service.GetAsync(owner, input.OperationId)); Assert.Empty(db.Tasks);
        Assert.Equal("operation-conflict", (await Assert.ThrowsAsync<TaskSubmissionIntentConflictException>(() => service.ExecuteAsync(owner,
            input.OperationId, new(new string('A', 64))))).Code); Assert.Empty(db.TaskDispatches);
    }

    [Fact]
    public async Task FixedExpiryStopsOnlyUncommittedExecutionAndNeverHidesAcceptedTask()
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var owner = MemberDirectoryFixture.Authority();
        var source = SubmissionIntentFixture.Seed(db, owner); await db.SaveChangesAsync(); var clock = new SubmissionIntentFixture.Clock(); var service = Service(db, clock);
        var pending = SubmissionIntentFixture.Request(source); var committed = SubmissionIntentFixture.Request(source);
        var prepared = await service.PrepareAsync(owner, pending); var second = await service.PrepareAsync(owner, committed);
        var accepted = await service.ExecuteAsync(owner, committed.OperationId, new(second.InputFingerprint!));
        clock.NowUtc = clock.NowUtc.AddHours(24);
        Assert.Equal(TaskSubmissionIntentState.Expired, (await service.GetAsync(owner, pending.OperationId))!.State);
        Assert.Equal("intent-expired", (await Assert.ThrowsAsync<TaskSubmissionIntentConflictException>(() => service.ExecuteAsync(owner,
            pending.OperationId, new(prepared.InputFingerprint!)))).Code);
        var expiredReplay = await service.PrepareAsync(owner, pending);
        Assert.Equal(prepared.ExpiresAtUtc, expiredReplay.ExpiresAtUtc); Assert.Equal(TaskSubmissionIntentState.Expired, expiredReplay.State);
        var before = SubmissionIntentFixture.Effects(db);
        Assert.Equal(TaskSubmissionIntentState.Accepted, (await service.GetAsync(owner, committed.OperationId))!.State);
        Assert.Equal(accepted, await service.ExecuteAsync(owner, committed.OperationId, new(second.InputFingerprint!)));
        Assert.Equal(before, SubmissionIntentFixture.Effects(db)); Assert.Equal(2, (await service.ListAsync(owner)).Items.Count);
    }

    [Fact]
    public async Task OneHundredActiveLimitExcludesOnlyValidCommittedEvidenceAndRetainsEarlierOperation()
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var owner = MemberDirectoryFixture.Authority();
        var source = SubmissionIntentFixture.Seed(db, owner);
        var rows = Enumerable.Range(0, 99).Select(_ => SubmissionIntentFixture.Row(owner, SubmissionIntentFixture.Request(source))).ToArray();
        db.TaskSubmissionIntents.AddRange(rows); await db.SaveChangesAsync(); var service = Service(db);
        var hundred = await service.PrepareAsync(owner, SubmissionIntentFixture.Request(source));
        Assert.Equal(100, db.TaskSubmissionIntents.Count()); var overflow = SubmissionIntentFixture.Request(source);
        Assert.Equal("intent-limit", (await Assert.ThrowsAsync<TaskSubmissionIntentConflictException>(() => service.PrepareAsync(owner, overflow))).Code);
        var first = await service.ExecuteAsync(owner, rows[0].OperationId, new(rows[0].InputFingerprint));
        await service.PrepareAsync(owner, overflow); Assert.Equal(101, db.TaskSubmissionIntents.Count());
        Assert.Equal(first.TaskId, (await service.GetAsync(owner, rows[0].OperationId))!.Accepted!.TaskId);
        // A malformed committed event must start consuming that quota again.
        db.TaskEvents.Local.Single().PayloadJson = "{}"; await db.SaveChangesAsync();
        Assert.Equal("intent-limit", (await Assert.ThrowsAsync<TaskSubmissionIntentConflictException>(() =>
            service.PrepareAsync(owner, SubmissionIntentFixture.Request(source)))).Code);
        Assert.Equal(TaskSubmissionIntentState.Prepared, (await service.GetAsync(owner, hundred.OperationId))!.State);
    }

    [Theory]
    [InlineData("question")]
    [InlineData("surrogate")]
    [InlineData("fingerprint")]
    [InlineData("version")]
    [InlineData("attempts")]
    [InlineData("source")]
    [InlineData("created")]
    [InlineData("expires")]
    public async Task EveryReadAndExecuteRevalidatesImmutableStoredInputAndNeverCreatesReplacement(string corrupt)
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var owner = MemberDirectoryFixture.Authority();
        var source = SubmissionIntentFixture.Seed(db, owner); var input = SubmissionIntentFixture.Request(source); var row = SubmissionIntentFixture.Row(owner, input);
        switch (corrupt)
        {
            case "question": row.Question = "PRIVATE_CHANGED"; break;
            case "surrogate": row.Question = new string((char)0xD800, 1); break;
            case "fingerprint": row.InputFingerprint = row.InputFingerprint.ToLowerInvariant(); break;
            case "version": row.InputVersion = 2; break;
            case "attempts": row.MaxAttempts = 4; break;
            case "source": row.DataSourceId = Guid.Empty; break;
            case "created": row.CreatedAtUtc = default; break;
            case "expires": row.ExpiresAtUtc = row.ExpiresAtUtc.AddMinutes(1); break;
        }
        db.TaskSubmissionIntents.Add(row); await db.SaveChangesAsync(); var service = Service(db);
        var detail = (await service.GetAsync(owner, input.OperationId))!;
        Assert.Equal(TaskSubmissionIntentState.Unavailable, detail.State); Assert.Null(detail.Question); Assert.Null(detail.InputFingerprint);
        Assert.Null(detail.DataSourceId); Assert.Null(detail.Accepted); Assert.Null(detail.CreatedAtUtc); Assert.Null(detail.ExpiresAtUtc);
        Assert.Equal(detail, Assert.Single((await service.ListAsync(owner)).Items));
        Assert.Equal("intent-unavailable", (await Assert.ThrowsAsync<TaskSubmissionIntentConflictException>(() => service.ExecuteAsync(owner,
            input.OperationId, new(TaskSubmissionIntentIdentity.Fingerprint(input))))).Code);
        Assert.Empty(db.Tasks); Assert.Empty(db.TaskDispatches);
        Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(detail));
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("surrogate")]
    [InlineData("step")]
    [InlineData("execution")]
    [InlineData("dispatch")]
    public async Task MalformedOriginalTaskEvidenceIsUnavailableAndCannotAdmitAnotherGraph(string corrupt)
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var owner = MemberDirectoryFixture.Authority();
        var source = SubmissionIntentFixture.Seed(db, owner); await db.SaveChangesAsync(); var service = Service(db);
        var input = SubmissionIntentFixture.Request(source); var prepared = await service.PrepareAsync(owner, input);
        await service.ExecuteAsync(owner, input.OperationId, new(prepared.InputFingerprint!));
        var stored = db.TaskEvents.Local.Single();
        if (corrupt == "extra") stored.PayloadJson = stored.PayloadJson[..^1] + ",\"private\":\"PRIVATE_SECRET\"}";
        if (corrupt == "duplicate") stored.PayloadJson = stored.PayloadJson[..^1] + ",\"question\":\"PRIVATE_SECRET\"}";
        if (corrupt == "surrogate") stored.PayloadJson = stored.PayloadJson.Replace("T", "\\ud800", StringComparison.Ordinal);
        if (corrupt == "step") db.TaskSteps.Remove(db.TaskSteps.Local.Single());
        if (corrupt == "execution") db.TaskStepExecutions.Remove(db.TaskStepExecutions.Local.Single());
        if (corrupt == "dispatch") db.TaskDispatches.Remove(db.TaskDispatches.Local.Single());
        await db.SaveChangesAsync(); var before = SubmissionIntentFixture.Effects(db);
        Assert.Equal(TaskSubmissionIntentState.Unavailable, (await service.GetAsync(owner, input.OperationId))!.State);
        Assert.Equal("intent-unavailable", (await Assert.ThrowsAsync<TaskSubmissionIntentConflictException>(() => service.ExecuteAsync(owner,
            input.OperationId, new(prepared.InputFingerprint!)))).Code);
        Assert.Equal(before, SubmissionIntentFixture.Effects(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReservedKeyLegacyTaskRequiresExactOriginalInputBeforePreparation(bool conflicting)
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var owner = MemberDirectoryFixture.Authority();
        var source = SubmissionIntentFixture.Seed(db, owner); await db.SaveChangesAsync(); var input = SubmissionIntentFixture.Request(source);
        var accepted = await new PilotTaskSubmissionService(db, new EfAuthorizationDirectory(db), new SubmissionIntentFixture.Clock()).SubmitAsync(owner,
            new PilotTaskSubmissionRequest(TaskSubmissionIntentIdentity.IdempotencyKey(input.OperationId), source, conflicting ? "Other input" : input.Question));
        var before = SubmissionIntentFixture.Effects(db); var service = Service(db);
        if (conflicting)
        {
            Assert.Equal("operation-conflict", (await Assert.ThrowsAsync<TaskSubmissionIntentConflictException>(() => service.PrepareAsync(owner, input))).Code);
            Assert.Empty(db.TaskSubmissionIntents);
        }
        else
        {
            var prepared = await service.PrepareAsync(owner, input); Assert.Equal(TaskSubmissionIntentState.Accepted, prepared.State);
            Assert.Equal(accepted.TaskId, prepared.Accepted!.TaskId); Assert.Single(db.TaskSubmissionIntents);
        }
        Assert.Equal(before, SubmissionIntentFixture.Effects(db));
    }

    [Fact]
    public async Task OwnerScopePrecedesPagingAndAdminCannotReadAnotherUsersIntent()
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var owner = MemberDirectoryFixture.Authority();
        var source = SubmissionIntentFixture.Seed(db, owner); var other = AuthorizationContext.Create(owner.TenantId, owner.CompanyId, Guid.NewGuid());
        MemberDirectoryFixture.AddMember(db, owner, other.UserId, "Other", roles: ["admin"]);
        var foreign = MemberDirectoryFixture.Authority(); var foreignSource = SubmissionIntentFixture.Seed(db, foreign);
        var second = AuthorizationContext.Create(owner.TenantId, Guid.NewGuid(), owner.UserId);
        db.Companies.Add(new() { TenantId = owner.TenantId, Id = second.CompanyId, Code = "second", Name = "Second" });
        db.CompanyMemberships.Add(new() { TenantId = owner.TenantId, CompanyId = second.CompanyId, UserId = owner.UserId });
        var local = Enumerable.Range(1, 4).Select(index => SubmissionIntentFixture.Request(source) with
        { OperationId = Guid.Parse($"00000000-0000-0000-0000-{index:D12}") }).ToArray();
        foreach (var request in local) db.TaskSubmissionIntents.Add(SubmissionIntentFixture.Row(owner, request));
        var otherInput = SubmissionIntentFixture.Request(source); db.TaskSubmissionIntents.Add(SubmissionIntentFixture.Row(other, otherInput));
        db.TaskSubmissionIntents.Add(SubmissionIntentFixture.Row(foreign, SubmissionIntentFixture.Request(foreignSource)));
        db.TaskSubmissionIntents.Add(SubmissionIntentFixture.Row(second, SubmissionIntentFixture.Request(source)));
        await db.SaveChangesAsync(); var service = Service(db); var before = SubmissionIntentFixture.Effects(db);
        var first = await service.ListAsync(owner, 0, 2); var last = await service.ListAsync(owner, 2, 2);
        Assert.True(first.HasMore); Assert.False(last.HasMore);
        Assert.Equal(local.Reverse().Select(row => row.OperationId), first.Items.Concat(last.Items).Select(row => row.OperationId));
        Assert.Null(await service.GetAsync(other, local[0].OperationId)); Assert.Null(await service.GetAsync(owner, otherInput.OperationId));
        Assert.Equal(otherInput.OperationId, Assert.Single((await service.ListAsync(other)).Items).OperationId);
        await Assert.ThrowsAsync<TaskSubmissionIntentNotFoundException>(() => service.ExecuteAsync(other, local[0].OperationId,
            new(TaskSubmissionIntentIdentity.Fingerprint(local[0])))); Assert.Equal(before, SubmissionIntentFixture.Effects(db));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("grant")]
    public async Task FreshExecutionPermissionLossDeniesReplayWhileHistoricalOwnerGetRemainsReadOnly(string revoke)
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var owner = MemberDirectoryFixture.Authority();
        var source = SubmissionIntentFixture.Seed(db, owner); await db.SaveChangesAsync(); var service = Service(db);
        var input = SubmissionIntentFixture.Request(source); var prepared = await service.PrepareAsync(owner, input);
        var accepted = await service.ExecuteAsync(owner, input.OperationId, new(prepared.InputFingerprint!));
        if (revoke == "source") db.DataSources.Local.Single().IsEnabled = false;
        else db.DataSourceSecretBindings.Local.Single().IsEnabled = false;
        await db.SaveChangesAsync(); var before = SubmissionIntentFixture.Effects(db);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ExecuteAsync(owner, input.OperationId, new(prepared.InputFingerprint!)));
        Assert.Equal(accepted, (await service.GetAsync(owner, input.OperationId))!.Accepted);
        Assert.Equal(before, SubmissionIntentFixture.Effects(db));
        db.DataSources.Local.Single().IsEnabled = true; db.DataSourceSecretBindings.Local.Single().IsEnabled = true; await db.SaveChangesAsync();
        Assert.Equal(accepted, await service.ExecuteAsync(owner, input.OperationId, new(prepared.InputFingerprint!)));
    }

    [Fact]
    public async Task FinalReadMembershipLossDeniesAfterPrivateInputWasReadAndRestorationWorks()
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var owner = MemberDirectoryFixture.Authority();
        var source = SubmissionIntentFixture.Seed(db, owner); await db.SaveChangesAsync(); var input = SubmissionIntentFixture.Request(source);
        await Service(db).PrepareAsync(owner, input); var directory = new FinalReadDirectory(db);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new TaskSubmissionIntentService(db, directory).GetAsync(owner, input.OperationId));
        Assert.Equal(2, directory.Calls); Assert.Empty(db.Tasks);
        db.CompanyMemberships.Local.Single().IsActive = true; await db.SaveChangesAsync();
        Assert.Equal(input.Question, (await Service(db).GetAsync(owner, input.OperationId))!.Question);
    }

    [Fact]
    public async Task FullScalarQuestionAndExplicitNewOperationPreserveTheOriginalPendingInput()
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var owner = MemberDirectoryFixture.Authority();
        var source = SubmissionIntentFixture.Seed(db, owner); await db.SaveChangesAsync(); var service = Service(db);
        var input = SubmissionIntentFixture.Request(source) with { Question = string.Concat(Enumerable.Repeat("😀", 2000)) };
        var first = await service.PrepareAsync(owner, input); var another = await service.PrepareAsync(owner, input with { OperationId = Guid.NewGuid() });
        Assert.NotEqual(first.OperationId, another.OperationId); Assert.Equal(first.InputFingerprint, another.InputFingerprint);
        Assert.Equal(first, await service.GetAsync(owner, first.OperationId)); Assert.Equal(2, (await service.ListAsync(owner)).Items.Count);
        Assert.Empty(db.Tasks); await service.ExecuteAsync(owner, first.OperationId, new(first.InputFingerprint!));
        Assert.Equal(input.Question, (await service.GetAsync(owner, first.OperationId))!.Question);
    }

    [Fact]
    public async Task LegacyHeaderCannotReplacePreparedInputAttemptsOrExpiredUncommittedOperation()
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var owner = MemberDirectoryFixture.Authority();
        var source = SubmissionIntentFixture.Seed(db, owner); await db.SaveChangesAsync(); var clock = new SubmissionIntentFixture.Clock();
        var input = SubmissionIntentFixture.Request(source); var prepared = await Service(db, clock).PrepareAsync(owner, input);
        var service = new PilotTaskSubmissionService(db, new EfAuthorizationDirectory(db), clock);
        var request = new PilotTaskSubmissionRequest(TaskSubmissionIntentIdentity.IdempotencyKey(input.OperationId), source, input.Question);
        foreach (var conflicting in new[] { request with { Question = "different" }, request with { MaxAttempts = 4 } })
            Assert.Equal("operation-conflict", (await Assert.ThrowsAsync<TaskSubmissionIntentConflictException>(() => service.SubmitAsync(owner, conflicting))).Code);
        Assert.Empty(db.Tasks); Assert.Equal(prepared, await Service(db, clock).GetAsync(owner, input.OperationId));
        clock.NowUtc = clock.NowUtc.AddHours(24);
        Assert.Equal("intent-expired", (await Assert.ThrowsAsync<TaskSubmissionIntentConflictException>(() => service.SubmitAsync(owner, request))).Code);
        Assert.Empty(db.TaskDispatches);
    }

    private static TaskSubmissionIntentService Service(PlatformDbContext db, TimeProvider? clock = null) =>
        new(db, new EfAuthorizationDirectory(db), clock ?? new SubmissionIntentFixture.Clock());
    private sealed class FinalReadDirectory(PlatformDbContext db) : IAuthorizationDirectory
    {
        public int Calls { get; private set; }
        public async Task<AuthorizationDirectoryEntry?> ResolveAsync(AuthorizationContext authority, CancellationToken cancellationToken = default)
        {
            if (++Calls == 2) { db.CompanyMemberships.Local.Single().IsActive = false; await db.SaveChangesAsync(cancellationToken); }
            return await new EfAuthorizationDirectory(db).ResolveAsync(authority, cancellationToken);
        }
    }
}
