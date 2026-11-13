extern alias RuntimeWorker;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;
using MinhHuyAiOffice.Shared.Contracts;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class ConversationTests
{
    [Fact]
    public async Task FirstQuestion_StartsItsOwnConversation_AndAFollowUpJoinsIt()
    {
        var f = await Fixture.CreateAsync();

        var first = await f.SubmitAsync("c-1");
        var second = await f.SubmitAsync("c-2", first.TaskId);

        Assert.Equal(first.TaskId, (await f.TaskAsync(first.TaskId)).ConversationId);
        Assert.Equal(first.TaskId, (await f.TaskAsync(second.TaskId)).ConversationId);
        var history = await new TaskHistoryService(f.Db, new EfAuthorizationDirectory(f.Db)).ListAsync(f.Authority, 0, 25);
        Assert.All(history.Items, item => Assert.Equal(first.TaskId, item.ConversationId));
    }

    [Fact]
    public async Task SomeoneElsesOrUnknownConversation_StartsANewOne()
    {
        var f = await Fixture.CreateAsync();
        var other = await f.AddMemberAsync();
        var theirs = await f.SubmitAsync("theirs", authority: other);

        var stolen = await f.SubmitAsync("mine-1", theirs.TaskId);
        var unknown = await f.SubmitAsync("mine-2", Guid.NewGuid());

        Assert.Equal(stolen.TaskId, (await f.TaskAsync(stolen.TaskId)).ConversationId);
        Assert.Equal(unknown.TaskId, (await f.TaskAsync(unknown.TaskId)).ConversationId);
    }

    [Fact]
    public async Task IntentSubmission_CarriesTheConversation()
    {
        var f = await Fixture.CreateAsync();
        var first = await f.SubmitAsync("c-root");
        var service = new TaskSubmissionIntentService(f.Db, new EfAuthorizationDirectory(f.Db));
        var operation = Guid.NewGuid();
        var prepared = await service.PrepareAsync(f.Authority, new TaskSubmissionPrepareRequest(operation, f.SourceId, "Còn tháng trước?"));

        var accepted = await service.ExecuteAsync(f.Authority, operation, new TaskSubmissionExecuteRequest(prepared.InputFingerprint!, first.TaskId));

        Assert.Equal(first.TaskId, (await f.TaskAsync(accepted.TaskId)).ConversationId);
        Assert.Throws<ArgumentException>(() => new TaskSubmissionExecuteRequest(prepared.InputFingerprint!, Guid.Empty).Validate());
    }

    [Fact]
    public async Task Context_HasTheAskersEarlierCompletedTurns_OldestFirst_WithWorkingSql()
    {
        var f = await Fixture.CreateAsync();
        var first = await f.SubmitAsync("t-1", question: "Doanh thu tháng 11 của Mak?");
        await f.CompleteAsync(first.TaskId, "Doanh thu tháng 11 là 120 triệu.",
            ("SELECT SUM(Amount) FROM dbo.Invoice WHERE Month = 11", null), ("SELECT broken", "Invalid column"));
        var failed = await f.SubmitAsync("t-2", first.TaskId, "Câu bị lỗi");
        await f.SetStatusAsync(failed.TaskId, TaskExecutionStatus.Failed);
        var hidden = await f.SubmitAsync("t-3", first.TaskId, "Câu đã xoá");
        await f.CompleteAsync(hidden.TaskId, "Bí mật.");
        f.Db.HiddenTasks.Add(new HiddenTaskRecord { TenantId = f.Authority.TenantId, CompanyId = f.Authority.CompanyId, TaskId = hidden.TaskId, UserId = f.Authority.UserId, HiddenAtUtc = DateTimeOffset.UtcNow });
        await f.Db.SaveChangesAsync();
        var current = await f.SubmitAsync("t-4", first.TaskId, "Còn tháng trước thì sao?");

        var text = await ConversationContext.ReadAsync(f.Db, f.Envelope(current.TaskId), default);

        Assert.NotNull(text);
        Assert.Contains("[Q1] Doanh thu tháng 11 của Mak?", text);
        Assert.Contains("[A1] Doanh thu tháng 11 là 120 triệu.", text);
        Assert.Contains("SQL used: SELECT SUM(Amount) FROM dbo.Invoice WHERE Month = 11", text);
        Assert.DoesNotContain("SELECT broken", text);
        Assert.DoesNotContain("Câu bị lỗi", text);
        Assert.DoesNotContain("Bí mật", text);
        Assert.DoesNotContain("Còn tháng trước", text);
        Assert.DoesNotContain("[Q2]", text);
    }

    [Fact]
    public async Task Context_IsEmptyForANewConversation_AndBoundsLongTurns()
    {
        var f = await Fixture.CreateAsync();
        var only = await f.SubmitAsync("solo");
        Assert.Null(await ConversationContext.ReadAsync(f.Db, f.Envelope(only.TaskId), default));

        var text = ConversationContext.Format([new ConversationContext.Turn(new string('q', 5000),
            JsonSerializer.Serialize(new { answer = new string('a', 5000) }))]);
        Assert.True(text.Length < ConversationContext.MaximumQuestion + ConversationContext.MaximumAnswer + 40);
    }

    private sealed class Fixture
    {
        public required PlatformDbContext Db { get; init; }
        public required AuthorizationContext Authority { get; init; }
        public required Guid SourceId { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase($"conversation-{Guid.NewGuid():N}").Options);
            var (tenant, company, user, source) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            db.Users.Add(new PlatformUserRecord { TenantId = tenant, Id = user, IdentityProvider = "test", Subject = $"u-{user:N}", DisplayName = "Tuyên", IsActive = true });
            db.Companies.Add(new CompanyRecord { TenantId = tenant, Id = company, Code = $"C-{company:N}", Name = "Minh Huy", IsActive = true });
            db.CompanyMemberships.Add(new CompanyMembershipRecord { TenantId = tenant, CompanyId = company, UserId = user, IsActive = true });
            db.DataSources.Add(new DataSourceRecord
            {
                TenantId = tenant,
                CompanyId = company,
                Id = source,
                LogicalName = "Mak ERP",
                Kind = "sql-server",
                Environment = "Production",
                Purpose = "ERP khách hàng",
                ConnectionSecretReference = "secretref://env/PILOT_ERP_CONNECTION",
                AllowRead = true,
                AllowWrite = false,
                MaxConcurrency = 2,
                IsEnabled = true,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            BindingFixture.Grant(db, tenant, company, "secretref://env/PILOT_ERP_CONNECTION");
            await db.SaveChangesAsync();
            return new() { Db = db, Authority = AuthorizationContext.Create(tenant, company, user), SourceId = source };
        }

        public async Task<AuthorizationContext> AddMemberAsync()
        {
            var user = Guid.NewGuid();
            Db.Users.Add(new PlatformUserRecord { TenantId = Authority.TenantId, Id = user, IdentityProvider = "test", Subject = $"u-{user:N}", DisplayName = "Khác", IsActive = true });
            Db.CompanyMemberships.Add(new CompanyMembershipRecord { TenantId = Authority.TenantId, CompanyId = Authority.CompanyId, UserId = user, IsActive = true });
            await Db.SaveChangesAsync();
            return AuthorizationContext.Create(Authority.TenantId, Authority.CompanyId, user);
        }

        public async Task<PilotTaskSubmissionAccepted> SubmitAsync(string key, Guid? conversation = null, string question = "Doanh thu?",
            AuthorizationContext? authority = null)
        {
            var accepted = await new PilotTaskSubmissionService(Db, new EfAuthorizationDirectory(Db)).SubmitAsync(authority ?? Authority,
                new PilotTaskSubmissionRequest(key, SourceId, question, 3) { ConversationId = conversation });
            await Task.Delay(2);   // distinct creation times keep the turn order deterministic
            return accepted;
        }

        public Task<TaskRecord> TaskAsync(Guid id) => Db.Tasks.AsNoTracking().SingleAsync(task => task.Id == id);

        public async Task SetStatusAsync(Guid taskId, TaskExecutionStatus status)
        {
            var task = await Db.Tasks.SingleAsync(row => row.Id == taskId);
            task.Status = status;
            await Db.SaveChangesAsync();
        }

        public async Task CompleteAsync(Guid taskId, string answer, params (string Sql, string? Error)[] queries)
        {
            await SetStatusAsync(taskId, TaskExecutionStatus.Completed);
            var step = await Db.TaskSteps.SingleAsync(row => row.TaskId == taskId);
            Db.TaskCheckpoints.Add(new TaskCheckpointRecord
            {
                TenantId = Authority.TenantId,
                CompanyId = Authority.CompanyId,
                TaskId = taskId,
                StepId = step.Id,
                Version = 1,
                PayloadJson = JsonSerializer.Serialize(new { status = "completed", answer, erpQueries = queries.Select(query => new { sql = query.Sql, error = query.Error }) }),
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await Db.SaveChangesAsync();
        }

        public WorkDispatchEnvelope Envelope(Guid taskId)
        {
            var step = PilotTaskIdentity.ForStep(taskId);
            return WorkDispatchEnvelope.Create(PilotTaskIdentity.ForMessage(taskId, step), Authority.TenantId, Authority.CompanyId, taskId, step, 1, 0,
                DateTimeOffset.UtcNow);
        }
    }
}
