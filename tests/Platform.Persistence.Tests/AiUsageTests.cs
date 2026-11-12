extern alias RuntimeWorker;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using MinhHuy.AIOffice.Shared.Contracts;
using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class AiUsageTests
{
    [Fact]
    public async Task Submission_RecordsAdmissionAgainstTheSourcesCustomer()
    {
        var f = await Fixture.CreateAsync();
        var account = await f.Service.SaveAccountAsync(f.Authority, new("Mak", 500_000m, null, [f.SourceId], null));

        var accepted = await f.SubmitAsync("q-1");

        var admission = await f.Db.AiQuestionAdmissions.SingleAsync();
        Assert.Equal(accepted.TaskId, admission.TaskId);
        Assert.Equal(f.Authority.UserId, admission.UserId);
        Assert.Equal(f.SourceId, admission.DataSourceId);
        Assert.Equal(account.Id, admission.BillingAccountId);
    }

    [Fact]
    public async Task Submission_RefusesOverThePerPersonRate_AndLeavesNoTask()
    {
        var f = await Fixture.CreateAsync();
        for (var i = 0; i < AiUsageAdmission.UserQuestionsPerMinute; i++)
            f.Db.AiQuestionAdmissions.Add(f.Admission(DateTimeOffset.UtcNow.AddSeconds(-10)));
        await f.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<AiUsageLimitException>(() => f.SubmitAsync("q-over"));

        Assert.Equal(AiUsageLimitException.UserRate, error.Code);
        Assert.Empty(await f.Db.Tasks.ToListAsync());
        Assert.Equal(AiUsageAdmission.UserQuestionsPerMinute, await f.Db.AiQuestionAdmissions.CountAsync());
    }

    [Fact]
    public async Task Submission_AllowsAgainOnceTheMinuteHasPassed()
    {
        var f = await Fixture.CreateAsync();
        for (var i = 0; i < AiUsageAdmission.UserQuestionsPerMinute; i++)
            f.Db.AiQuestionAdmissions.Add(f.Admission(DateTimeOffset.UtcNow.AddMinutes(-2)));
        await f.Db.SaveChangesAsync();

        await f.SubmitAsync("q-later");

        Assert.Single(await f.Db.Tasks.ToListAsync());
    }

    [Fact]
    public async Task Submission_RefusesOverTheCustomersRate()
    {
        var f = await Fixture.CreateAsync();
        var account = await f.Service.SaveAccountAsync(f.Authority, new("Mak", null, 2, [f.SourceId], null));
        var other = Guid.NewGuid();
        f.Db.AiQuestionAdmissions.AddRange(f.Admission(DateTimeOffset.UtcNow, account.Id, other), f.Admission(DateTimeOffset.UtcNow, account.Id, other));
        await f.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<AiUsageLimitException>(() => f.SubmitAsync("q-busy"));

        Assert.Equal(AiUsageLimitException.AccountRate, error.Code);
        Assert.Contains("Mak", error.Message);
    }

    [Fact]
    public async Task Submission_RefusesWhenTheMonthlyBudgetIsSpent()
    {
        var f = await Fixture.CreateAsync();
        var account = await f.Service.SaveAccountAsync(f.Authority, new("Mak", 1_000m, null, [f.SourceId], null));
        f.Db.AiUsageRecords.Add(f.Usage(account.Id, 1_000m, DateTimeOffset.UtcNow));
        // Spend from last month does not count against this month.
        f.Db.AiUsageRecords.Add(f.Usage(account.Id, 50_000m, AiBillingCalendar.MonthStart(DateTimeOffset.UtcNow).AddDays(-1)));
        await f.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<AiUsageLimitException>(() => f.SubmitAsync("q-broke"));

        Assert.Equal(AiUsageLimitException.Budget, error.Code);
        Assert.Empty(await f.Db.Tasks.ToListAsync());
    }

    [Fact]
    public async Task Submission_IgnoresLastMonthsSpend()
    {
        var f = await Fixture.CreateAsync();
        var account = await f.Service.SaveAccountAsync(f.Authority, new("Mak", 1_000m, null, [f.SourceId], null));
        f.Db.AiUsageRecords.Add(f.Usage(account.Id, 50_000m, AiBillingCalendar.MonthStart(DateTimeOffset.UtcNow).AddMinutes(-1)));
        await f.Db.SaveChangesAsync();

        await f.SubmitAsync("q-new-month");

        Assert.Single(await f.Db.Tasks.ToListAsync());
    }

    [Fact]
    public async Task Recorder_PricesByLongestModelPrefix_WithCachedInputAtItsOwnRate()
    {
        var f = await Fixture.CreateAsync();
        var account = await f.Service.SaveAccountAsync(f.Authority, new("Mak", null, null, [f.SourceId], null));
        await f.Service.SavePriceAsync(f.Authority, new("claude", 1m, 1m, 1m));
        await f.Service.SavePriceAsync(f.Authority, new("claude-sonnet-5-5", 75_000m, 7_500m, 375_000m));

        var row = await f.Recorder.RecordAsync(f.Context(), "company-shared", "claude-sonnet-5-5-20261001", 2_000_000, 1_000_000, 100_000, default);

        // 1M uncached × 75 000 + 1M cached × 7 500 + 0.1M output × 375 000
        Assert.Equal(75_000m + 7_500m + 37_500m, row.Cost);
        Assert.Equal(account.Id, row.BillingAccountId);
        Assert.Equal(f.Authority.UserId, row.UserId);
        Assert.Equal(1_000_000, row.CachedInputTokens);
    }

    [Fact]
    public async Task Recorder_KeepsTokensWithoutCost_WhenTheModelHasNoPrice()
    {
        var f = await Fixture.CreateAsync();

        var row = await f.Recorder.RecordAsync(f.Context(), "openai-direct", "gpt-unpriced", 1200, 0, 300, default);

        Assert.Null(row.Cost);
        Assert.Null(row.PriceId);
        Assert.Equal(1200, (await f.Db.AiUsageRecords.SingleAsync()).InputTokens);
    }

    [Fact]
    public async Task Price_ChangeAppliesFromNowOn_AndOldUsageKeepsItsPrice()
    {
        var f = await Fixture.CreateAsync();
        await f.Service.SavePriceAsync(f.Authority, new("m", 1_000_000m, 0, 0));
        var first = await f.Recorder.RecordAsync(f.Context(), "p", "m", 1_000, 0, 0, default);
        await Task.Delay(5);
        await f.Service.SavePriceAsync(f.Authority, new("m", 2_000_000m, 0, 0));
        var second = await f.Recorder.RecordAsync(f.Context(), "p", "m", 1_000, 0, 0, default);

        Assert.Equal(1_000m, first.Cost);
        Assert.Equal(2_000m, second.Cost);
        Assert.Equal(2, await f.Db.AiPrices.CountAsync());
        Assert.Equal(2_000_000m, (await f.Service.GetSettingsAsync(f.Authority)).Prices.Single().InputPerMillion);
    }

    [Fact]
    public async Task MeteredGateway_RecordsEveryCall_AndStopsOnceTheBudgetIsSpent()
    {
        var f = await Fixture.CreateAsync();
        await f.Service.SaveAccountAsync(f.Authority, new("Mak", 100m, null, [f.SourceId], null));
        await f.Service.SavePriceAsync(f.Authority, new("model-x", 60_000_000m, 0, 0));
        var inner = new FakeGateway(1_000, 10);
        var gateway = new MeteredAiGateway(inner, new AiQuestionUsageScope { Current = f.Context() }, f.Recorder,
            NullLogger<MeteredAiGateway>.Instance);

        await gateway.ExecuteAsync(f.Request("r1"));   // 60 000 000 × 1 000 / 1M = 60 000 đ, over the 100 đ budget
        var refused = await Assert.ThrowsAsync<AiProviderExecutionException>(() => gateway.ExecuteAsync(f.Request("r2")));

        Assert.False(refused.IsTransient);
        Assert.Contains("hạn mức", refused.Message);
        Assert.Equal(1, inner.Calls);
        var row = await f.Db.AiUsageRecords.SingleAsync();
        Assert.Equal("assistant", row.Purpose);
        Assert.Equal(60_000m, row.Cost);
    }

    [Fact]
    public async Task Accounts_MoveASourceBetweenCustomers_AndRejectAStaleVersion()
    {
        var f = await Fixture.CreateAsync();
        var mak = await f.Service.SaveAccountAsync(f.Authority, new("Mak", null, null, [f.SourceId], null));
        var atc = await f.Service.SaveAccountAsync(f.Authority, new("ATC", null, null, [f.SourceId], null));

        var settings = await f.Service.GetSettingsAsync(f.Authority);
        Assert.Empty(settings.Accounts.Single(row => row.Id == mak.Id).SourceIds);
        Assert.Equal([f.SourceId], settings.Accounts.Single(row => row.Id == atc.Id).SourceIds);

        var renamed = await f.Service.SaveAccountAsync(f.Authority, new("ATC Group", 5m, 3, [], atc.Version), atc.Id);
        Assert.Equal("2", renamed.Version);
        await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
            f.Service.SaveAccountAsync(f.Authority, new("ATC again", null, null, [], atc.Version), atc.Id));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            f.Service.SaveAccountAsync(f.Authority, new("Ghost", null, null, [Guid.NewGuid()], null)));
    }

    [Fact]
    public async Task Admin_RoutesRefuseNonAdministrators()
    {
        var f = await Fixture.CreateAsync(admin: false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.GetSettingsAsync(f.Authority));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReportAsync(f.Authority, 2026, 11));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SavePriceAsync(f.Authority, new("m", 1, 1, 1)));
    }

    [Fact]
    public async Task Report_SumsTheMonthByCustomerPersonModelAndVietnamDay()
    {
        var f = await Fixture.CreateAsync();
        var mak = await f.Service.SaveAccountAsync(f.Authority, new("Mak", 300_000m, null, [f.SourceId], null));
        // 2026-11-01 00:30 in Vietnam is still 31 October in UTC: it belongs to November, day 01.
        var earlyNovember = new DateTimeOffset(2026, 10, 31, 17, 30, 0, TimeSpan.Zero);
        f.Db.AiUsageRecords.AddRange(
            f.Usage(mak.Id, 100m, earlyNovember, input: 1_000, output: 100),
            f.Usage(mak.Id, 50m, earlyNovember.AddDays(2), input: 500, output: 50),
            f.Usage(null, null, earlyNovember.AddDays(2), input: 7, output: 3),
            f.Usage(mak.Id, 999m, earlyNovember.AddMinutes(-60), input: 1, output: 1));   // October
        f.Db.AiQuestionAdmissions.AddRange(f.Admission(earlyNovember, mak.Id), f.Admission(earlyNovember.AddDays(2), mak.Id));
        await f.Db.SaveChangesAsync();

        var report = await f.Service.ReportAsync(f.Authority, 2026, 11);

        Assert.Equal("2026-11", report.Month);
        Assert.Equal(2, report.Totals.Questions);
        Assert.Equal(3, report.Totals.Calls);
        Assert.Equal(150m, report.Totals.Cost);
        Assert.Equal(1, report.Totals.UnpricedCalls);
        var makRow = report.Accounts.Single(row => row.AccountId == mak.Id);
        Assert.Equal(300_000m, makRow.MonthlyBudget);
        Assert.Equal(1_500, makRow.Totals.InputTokens);
        Assert.Equal(2, makRow.Totals.Questions);
        Assert.Contains(report.Accounts, row => row.AccountId is null && row.Totals.Calls == 1);
        Assert.Equal(["2026-11-01", "2026-11-03"], report.Days.Select(row => row.Date));
        Assert.Equal("Pilot User", report.Users.Single(row => row.UserId == f.Authority.UserId).Name);
    }

    [Fact]
    public async Task Limiter_HoldsCapacity_AndFreesTheSlotOnDispose()
    {
        var limiter = new DataSourceConcurrencyLimiter();
        var (tenant, company, source) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var first = await limiter.TryEnterAsync(tenant, company, source, 1, TimeSpan.Zero, default);
        var second = await limiter.TryEnterAsync(tenant, company, source, 1, TimeSpan.FromMilliseconds(20), default);
        var otherSource = await limiter.TryEnterAsync(tenant, company, Guid.NewGuid(), 1, TimeSpan.Zero, default);
        first!.Dispose();
        first.Dispose();
        var third = await limiter.TryEnterAsync(tenant, company, source, 1, TimeSpan.Zero, default);

        Assert.Null(second);
        Assert.NotNull(otherSource);
        Assert.NotNull(third);
        Assert.Null(await limiter.TryEnterAsync(tenant, company, source, 1, TimeSpan.Zero, default));
    }

    [Fact]
    public void Calendar_UsesVietnamMonths()
    {
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(7)),
            AiBillingCalendar.MonthStart(new DateTimeOffset(2026, 10, 31, 17, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(7)),
            AiBillingCalendar.MonthStart(new DateTimeOffset(2026, 10, 31, 16, 59, 0, TimeSpan.Zero)));
    }

    private sealed class FakeGateway(long input, long output) : IAiGateway
    {
        public int Calls { get; private set; }

        public Task<AiGatewayResponse> ExecuteAsync(AiGatewayRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new AiGatewayResponse(request.RequestId, request.Capability, "ok", "model-x", input, output)
            { ProviderId = "fake" });
        }
    }

    private sealed class Fixture
    {
        public required PlatformDbContext Db { get; init; }
        public required DbContextOptions<PlatformDbContext> Options { get; init; }
        public required AuthorizationContext Authority { get; init; }
        public required Guid SourceId { get; init; }
        public AiUsageService Service => new(Db, new EfAuthorizationDirectory(Db));
        public AiUsageRecorder Recorder => new(Options);

        public static async Task<Fixture> CreateAsync(bool admin = true)
        {
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase($"ai-usage-{Guid.NewGuid():N}").Options;
            var db = new PlatformDbContext(options);
            var (tenant, company, user, source) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            db.Users.Add(new PlatformUserRecord { TenantId = tenant, Id = user, IdentityProvider = "test", Subject = $"u-{user:N}", DisplayName = "Pilot User", IsActive = true });
            db.Companies.Add(new CompanyRecord { TenantId = tenant, Id = company, Code = $"C-{company:N}", Name = "Minh Huy", IsActive = true });
            db.CompanyMemberships.Add(new CompanyMembershipRecord { TenantId = tenant, CompanyId = company, UserId = user, IsActive = true });
            if (admin) db.RoleAssignments.Add(new() { TenantId = tenant, CompanyId = company, UserId = user, RoleKey = "admin" });
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
            return new() { Db = db, Options = options, Authority = AuthorizationContext.Create(tenant, company, user), SourceId = source };
        }

        public Task<PilotTaskSubmissionAccepted> SubmitAsync(string key) =>
            new PilotTaskSubmissionService(Db, new EfAuthorizationDirectory(Db)).SubmitAsync(Authority,
                new PilotTaskSubmissionRequest(key, SourceId, "Doanh thu tháng này?", 3));

        public AiUsageContext Context() => new(Authority.TenantId, Authority.CompanyId, Guid.NewGuid(), Authority.UserId, SourceId, "assistant");

        public AiGatewayRequest Request(string id) => new(Authority.TenantId.ToString("N"), Authority.CompanyId.ToString("N"),
            Guid.NewGuid().ToString("N"), id, AiCapability.Reasoning, "question");

        public AiQuestionAdmissionRecord Admission(DateTimeOffset at, Guid? account = null, Guid? user = null) => new()
        {
            TenantId = Authority.TenantId,
            CompanyId = Authority.CompanyId,
            TaskId = Guid.NewGuid(),
            UserId = user ?? Authority.UserId,
            DataSourceId = SourceId,
            BillingAccountId = account,
            AdmittedAtUtc = at
        };

        public AiUsageRecord Usage(Guid? account, decimal? cost, DateTimeOffset at, long input = 10, long output = 5) => new()
        {
            TenantId = Authority.TenantId,
            CompanyId = Authority.CompanyId,
            Id = Guid.NewGuid(),
            OccurredAtUtc = at,
            UserId = Authority.UserId,
            DataSourceId = SourceId,
            BillingAccountId = account,
            Purpose = "assistant",
            ProviderId = "p",
            Model = "m",
            InputTokens = input,
            OutputTokens = output,
            Cost = cost
        };
    }
}
