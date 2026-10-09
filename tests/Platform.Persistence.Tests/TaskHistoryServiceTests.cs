using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

internal static class TaskHistoryFixture
{
    internal static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-09T00:00:00Z");
    internal static readonly Guid SourceId = Guid.Parse("12345678-1234-1234-1234-123456789abc");
    internal static string Request(string question = "Tra cứu tồn kho 😀 �") => JsonSerializer.Serialize(
        new PilotTaskRequestEvent("history-request", SourceId, question, 3), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    internal static string Checkpoint(string question = "Tra cứu tồn kho 😀 �") => JsonSerializer.Serialize(new
    {
        status = "completed",
        dataSourceId = SourceId,
        logicalName = "Owned fixture",
        questionLength = question.Length,
        answer = "Kết quả đã lưu 😀 �\nDòng tiếp theo.",
        provider = "fixture",
        model = "fixture-v1",
        usage = new { inputTokens = 2, outputTokens = 3, totalTokens = 5 },
        billingStatus = "usage-observed-not-settled",
        erpEvidence = new { databaseName = "OwnedFixture", tableCount = 0, sampledTableCount = 0, topTables = Array.Empty<object>() },
        evidence = "ai-provider-reasoning-after-bounded-read-only-erp-catalog"
    });
    internal static Guid Add(PlatformDbContext db, AuthorizationContext owner, TaskExecutionStatus status = TaskExecutionStatus.Pending,
        string? request = null, string? checkpoint = null, Guid? id = null)
    {
        var taskId = id ?? Guid.NewGuid(); var stepId = PilotTaskIdentity.ForStep(taskId);
        db.Tasks.Add(new()
        {
            TenantId = owner.TenantId,
            CompanyId = owner.CompanyId,
            Id = taskId,
            CreatedByUserId = owner.UserId,
            CreatedAtUtc = Time,
            UpdatedAtUtc = Time,
            Status = status,
            WaitReason = "PRIVATE_DIAGNOSTIC"
        });
        db.TaskEvents.Add(new()
        {
            TenantId = owner.TenantId,
            CompanyId = owner.CompanyId,
            TaskId = taskId,
            Sequence = 1,
            EventType = PilotTaskRequestEvent.EventType,
            PayloadJson = request ?? Request(),
            OccurredAtUtc = Time
        });
        if (checkpoint is not null) db.TaskCheckpoints.Add(new()
        {
            TenantId = owner.TenantId,
            CompanyId = owner.CompanyId,
            TaskId = taskId,
            StepId = stepId,
            Version = 1,
            PayloadJson = checkpoint,
            CreatedAtUtc = Time
        });
        return taskId;
    }
}

public sealed class TaskHistoryServiceTests
{
    [Fact]
    public async Task OwnerScopePrecedesEqualTimePaginationAndArchiveReadsHaveNoEffects()
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var owner = MemberDirectoryFixture.Authority();
        MemberDirectoryFixture.Seed(db, owner);
        var other = AuthorizationContext.Create(owner.TenantId, owner.CompanyId, Guid.NewGuid());
        MemberDirectoryFixture.AddMember(db, owner, other.UserId, "Other", roles: ["admin"]);
        var foreign = MemberDirectoryFixture.Authority(); MemberDirectoryFixture.Seed(db, foreign);
        var secondCompany = AuthorizationContext.Create(owner.TenantId, Guid.NewGuid(), owner.UserId);
        db.Companies.Add(new() { TenantId = owner.TenantId, Id = secondCompany.CompanyId, Code = "SECOND", Name = "Second" });
        db.CompanyMemberships.Add(new() { TenantId = owner.TenantId, CompanyId = secondCompany.CompanyId, UserId = owner.UserId });
        var ids = Enumerable.Range(1, 5).Select(index => TaskHistoryFixture.Add(db, owner,
            (TaskExecutionStatus)(index % 7), id: Guid.Parse($"00000000-0000-0000-0000-{index:D12}"))).ToArray();
        var otherTask = TaskHistoryFixture.Add(db, other); TaskHistoryFixture.Add(db, foreign); TaskHistoryFixture.Add(db, secondCompany);
        await db.SaveChangesAsync(); var before = Fingerprint(db);
        var service = new TaskHistoryService(db, new EfAuthorizationDirectory(db));
        var first = await service.ListAsync(owner, 0, 2); var second = await service.ListAsync(owner, 2, 2); var last = await service.ListAsync(owner, 4, 2);
        Assert.Equal(ids.Reverse(), first.Items.Concat(second.Items).Concat(last.Items).Select(item => item.TaskId));
        Assert.True(first.HasMore); Assert.True(second.HasMore); Assert.False(last.HasMore);
        Assert.Null(await service.GetAsync(owner, otherTask));
        Assert.Equal(otherTask, Assert.Single((await service.ListAsync(other)).Items).TaskId);
        Assert.Null(await service.GetAsync(other, ids[0])); // admin is not an archive-wide role
        Assert.Equal(before, Fingerprint(db)); Assert.Empty(db.TaskDispatches);
    }

    [Fact]
    public async Task KnownCompletedCheckpointIsAllowlistedAndMissingCheckpointRemainsCompleted()
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var owner = MemberDirectoryFixture.Authority();
        MemberDirectoryFixture.Seed(db, owner, role: null);
        var completed = TaskHistoryFixture.Add(db, owner, TaskExecutionStatus.Completed, checkpoint: TaskHistoryFixture.Checkpoint());
        var missing = TaskHistoryFixture.Add(db, owner, TaskExecutionStatus.Completed);
        var pending = TaskHistoryFixture.Add(db, owner, checkpoint: TaskHistoryFixture.Checkpoint());
        await db.SaveChangesAsync(); var before = Fingerprint(db);
        var service = new TaskHistoryService(db, new EfAuthorizationDirectory(db));
        var result = Assert.IsType<TaskHistoryDetail>(await service.GetAsync(owner, completed));
        Assert.Equal("answer", result.Result!.Kind); Assert.Contains("😀 �", result.Result.Answer); Assert.Equal(5, result.Result.TotalTokens);
        Assert.False(result.ResultUnavailable); Assert.False(result.Task.MetadataUnavailable);
        var unavailable = (await service.GetAsync(owner, missing))!;
        Assert.Equal(TaskExecutionStatus.Completed, unavailable.Task.Status); Assert.True(unavailable.ResultUnavailable); Assert.Null(unavailable.Result);
        Assert.Null((await service.GetAsync(owner, pending))!.Result);
        Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(result)); Assert.DoesNotContain("databaseName", JsonSerializer.Serialize(result));
        Assert.Equal(before, Fingerprint(db));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"idempotencyKey\":\"a\",\"dataSourceId\":\"12345678-1234-1234-1234-123456789abc\",\"question\":\"x\",\"maxAttempts\":3,\"secretRef\":\"PRIVATE_SECRET\"}")]
    [InlineData("{\"idempotencyKey\":\"a\",\"dataSourceId\":\"12345678-1234-1234-1234-123456789abc\",\"question\":\"x\",\"question\":\"PRIVATE\",\"maxAttempts\":3}")]
    [InlineData("{\"idempotencyKey\":\"a\",\"dataSourceId\":\"12345678-1234-1234-1234-123456789abc\",\"question\":\"\\ud800\",\"maxAttempts\":3}")]
    public async Task MalformedRequestIsUnavailableWithoutExposingRawEvidence(string request)
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var owner = MemberDirectoryFixture.Authority();
        MemberDirectoryFixture.Seed(db, owner); var id = TaskHistoryFixture.Add(db, owner, TaskExecutionStatus.Completed, request, TaskHistoryFixture.Checkpoint());
        await db.SaveChangesAsync(); var service = new TaskHistoryService(db, new EfAuthorizationDirectory(db));
        var item = Assert.Single((await service.ListAsync(owner)).Items); Assert.True(item.MetadataUnavailable); Assert.Null(item.Summary);
        var detail = (await service.GetAsync(owner, id))!; Assert.True(detail.ResultUnavailable); Assert.Equal(TaskExecutionStatus.Completed, detail.Task.Status);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(detail));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("oversize")]
    [InlineData("bad-usage")]
    [InlineData("bad-unicode")]
    [InlineData("wrong-source")]
    public async Task InvalidLatestCheckpointNeverFallsBackToOldResultOrChangesDurableCompletion(string corrupt)
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var owner = MemberDirectoryFixture.Authority();
        MemberDirectoryFixture.Seed(db, owner); var valid = TaskHistoryFixture.Checkpoint();
        var id = TaskHistoryFixture.Add(db, owner, TaskExecutionStatus.Completed, checkpoint: valid);
        var bad = corrupt switch
        {
            "unknown" => "{\"answer\":\"PRIVATE_UNTRUSTED\"}",
            "duplicate" => valid.Insert(1, "\"answer\":\"PRIVATE_UNTRUSTED\","),
            "extra" => valid.Insert(1, "\"secretRef\":\"PRIVATE_SECRET\","),
            "oversize" => new string('x', 140000),
            "bad-usage" => valid.Replace("\"totalTokens\":5", "\"totalTokens\":4"),
            "bad-unicode" => valid.Replace("\"answer\":\"", "\"answer\":\"PRIVATE\ud800"),
            _ => valid.Replace(TaskHistoryFixture.SourceId.ToString(), Guid.NewGuid().ToString())
        };
        Assert.NotEqual(valid, bad);
        db.TaskCheckpoints.Add(new()
        {
            TenantId = owner.TenantId,
            CompanyId = owner.CompanyId,
            TaskId = id,
            StepId = PilotTaskIdentity.ForStep(id),
            Version = 2,
            PayloadJson = bad,
            CreatedAtUtc = TaskHistoryFixture.Time
        });
        await db.SaveChangesAsync(); var before = Fingerprint(db);
        var detail = (await new TaskHistoryService(db, new EfAuthorizationDirectory(db)).GetAsync(owner, id))!;
        Assert.Null(detail.Result); Assert.True(detail.ResultUnavailable); Assert.Equal(TaskExecutionStatus.Completed, detail.Task.Status);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(detail)); Assert.Equal(before, Fingerprint(db));
    }

    [Theory]
    [InlineData("list", "membership")]
    [InlineData("detail", "membership")]
    [InlineData("list", "user")]
    [InlineData("detail", "company")]
    [InlineData("list", "scope")]
    [InlineData("detail", "scope")]
    public async Task FinalFreshAuthorityDeniesRevocationDuringReadAndRestoredAuthorityRecovers(string operation, string change)
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var owner = MemberDirectoryFixture.Authority();
        MemberDirectoryFixture.Seed(db, owner); var id = TaskHistoryFixture.Add(db, owner, TaskExecutionStatus.Completed, checkpoint: TaskHistoryFixture.Checkpoint());
        await db.SaveChangesAsync(); var before = Fingerprint(db);
        var directory = new ChangingDirectory(db, owner, change); var service = new TaskHistoryService(db, directory);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            if (operation == "list") await service.ListAsync(owner); else await service.GetAsync(owner, id);
        });
        Assert.Equal(2, directory.Calls);
        db.CompanyMemberships.Local.Single().IsActive = true; db.Users.Local.Single().IsActive = true; db.Companies.Local.Single().IsActive = true;
        await db.SaveChangesAsync(); Assert.Single((await new TaskHistoryService(db, new EfAuthorizationDirectory(db)).ListAsync(owner)).Items);
        Assert.Equal(before, Fingerprint(db));
    }

    [Theory]
    [InlineData(-1, 25)]
    [InlineData(10001, 25)]
    [InlineData(0, 0)]
    [InlineData(0, 101)]
    public async Task PaginationBoundsRejectBeforeDirectoryOrQueries(int offset, int limit)
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var owner = MemberDirectoryFixture.Authority();
        var directory = new ChangingDirectory(db, owner, "scope");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new TaskHistoryService(db, directory).ListAsync(owner, offset, limit));
        Assert.Equal(0, directory.Calls);
    }

    private static string Fingerprint(PlatformDbContext db) => JsonSerializer.Serialize(new
    {
        tasks = db.Tasks.AsNoTracking().ToArray(),
        events = db.TaskEvents.AsNoTracking().ToArray(),
        checkpoints = db.TaskCheckpoints.AsNoTracking().ToArray(),
        steps = db.TaskSteps.AsNoTracking().ToArray(),
        executions = db.TaskStepExecutions.AsNoTracking().ToArray(),
        dispatches = db.TaskDispatches.AsNoTracking().ToArray()
    });

    private sealed class ChangingDirectory(PlatformDbContext db, AuthorizationContext owner, string change) : IAuthorizationDirectory
    {
        public int Calls { get; private set; }
        public async Task<AuthorizationDirectoryEntry?> ResolveAsync(AuthorizationContext context, CancellationToken cancellationToken = default)
        {
            if (++Calls == 2)
            {
                if (change == "scope") return new(AuthorizationContext.Create(owner.TenantId, Guid.NewGuid(), owner.UserId), []);
                if (change == "membership") db.CompanyMemberships.Local.Single().IsActive = false;
                if (change == "user") db.Users.Local.Single().IsActive = false;
                if (change == "company") db.Companies.Local.Single().IsActive = false;
                await db.SaveChangesAsync(cancellationToken);
            }
            return await new EfAuthorizationDirectory(db).ResolveAsync(context, cancellationToken);
        }
    }
}
