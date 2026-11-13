using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class ErpChatTests
{
    [Fact]
    public async Task CreatingAClient_AddsAServiceMemberWithoutRoles_AndReturnsTheKeyOnce()
    {
        var f = await Fixture.CreateAsync();

        var created = await f.CreateClientAsync();

        Assert.Equal(44, created.SigningKey.Length);
        var client = await f.Db.ErpChatClients.SingleAsync();
        Assert.DoesNotContain(created.SigningKey, Convert.ToBase64String(client.Ciphertext));
        var member = await f.Db.Users.SingleAsync(row => row.Id == client.ServiceUserId);
        Assert.Equal(ErpChatService.IdentityProvider, member.IdentityProvider);
        Assert.Equal("Mak (web ERP)", member.DisplayName);
        Assert.True(await f.Db.CompanyMemberships.AnyAsync(row => row.UserId == client.ServiceUserId && row.IsActive));
        Assert.False(await f.Db.RoleAssignments.AnyAsync(row => row.UserId == client.ServiceUserId));
        Assert.Single(await f.Service.ListClientsAsync(f.Authority));
    }

    [Fact]
    public async Task Signature_IsCheckedOverMethodPathAndBody_AndEachNonceWorksOnce()
    {
        var f = await Fixture.CreateAsync();
        var created = await f.CreateClientAsync();
        var body = Encoding.UTF8.GetBytes("""{"question":"x"}""");
        var good = Sign(created, "POST", "/api/erp-chat/v1/messages", body);

        Assert.Equal(created.Client.Id, (await f.Service.AuthenticateAsync(good, "POST", "/api/erp-chat/v1/messages", body, default)).Id);
        await Refused(f, good, "POST", "/api/erp-chat/v1/messages", body, "replayed");
        var other = Sign(created, "POST", "/api/erp-chat/v1/messages", body);
        await Refused(f, other, "POST", "/api/erp-chat/v1/messages", Encoding.UTF8.GetBytes("""{"question":"y"}"""), "unauthorized");
        await Refused(f, Sign(created, "POST", "/api/erp-chat/v1/messages?x=1", body), "POST", "/api/erp-chat/v1/messages", body, "unauthorized");
        await Refused(f, Sign(created, "POST", "/api/erp-chat/v1/messages", body, DateTimeOffset.UtcNow.AddMinutes(-6)),
            "POST", "/api/erp-chat/v1/messages", body, "unauthorized");
    }

    [Fact]
    public async Task ReplacedKeyOrDisabledClient_StopsWorkingAtOnce()
    {
        var f = await Fixture.CreateAsync();
        var created = await f.CreateClientAsync();
        var rotated = await f.Service.UpdateClientAsync(f.Authority, created.Client.Id, new(true, true, "1"));
        Assert.NotNull(rotated);

        await Refused(f, Sign(created, "GET", "/p", []), "GET", "/p", [], "unauthorized");
        Assert.NotNull(await f.Service.AuthenticateAsync(Sign(rotated!, "GET", "/p", []), "GET", "/p", Array.Empty<byte>(), default));

        Assert.Null(await f.Service.UpdateClientAsync(f.Authority, created.Client.Id, new(false, false, "2")));
        await Refused(f, Sign(rotated!, "GET", "/p", []), "GET", "/p", [], "unauthorized");
        Assert.False((await f.Db.CompanyMemberships.SingleAsync(row => row.UserId == f.Db.ErpChatClients.Single().ServiceUserId)).IsActive);
    }

    [Fact]
    public async Task Ask_UsesTheCustomersOnlySource_KeepsConversationsPerErpUser_AndIsIdempotent()
    {
        var f = await Fixture.CreateAsync();
        var client = await f.ClientAsync();

        var first = await f.AskAsync(client, new("r-1", "erp-user-a", "Doanh thu tháng 11?"));
        var followUp = await f.AskAsync(client, new("r-2", "erp-user-a", "Còn tháng trước?", ConversationId: first.ConversationId));
        var stranger = await f.AskAsync(client, new("r-3", "erp-user-b", "Xem giúp", ConversationId: first.ConversationId));
        var replay = await f.AskAsync(client, new("r-1", "erp-user-a", "Doanh thu tháng 11?"));

        Assert.Equal(first.MessageId, first.ConversationId);
        Assert.Equal(first.ConversationId, followUp.ConversationId);
        Assert.Equal(stranger.MessageId, stranger.ConversationId);
        Assert.Equal(first.MessageId, replay.MessageId);
        Assert.Equal("pending", first.Status);
        var task = await f.Db.Tasks.SingleAsync(row => row.Id == first.MessageId);
        Assert.Equal("erp-user-a", task.ExternalUserId);
        Assert.Equal(f.SourceId, (await f.Db.AiQuestionAdmissions.SingleAsync(row => row.TaskId == first.MessageId)).DataSourceId);
        Assert.Equal("erp-user-a", (await f.Db.AiQuestionAdmissions.SingleAsync(row => row.TaskId == first.MessageId)).ExternalUserId);
        var conflict = await Assert.ThrowsAsync<ErpChatException>(() => f.AskAsync(client, new("r-1", "erp-user-b", "Doanh thu tháng 11?")));
        Assert.Equal(409, conflict.Status);
    }

    [Fact]
    public async Task Ask_RefusesSourcesOutsideTheCustomer_AndNeedsOneWhenThereAreSeveral()
    {
        var f = await Fixture.CreateAsync();
        var client = await f.ClientAsync();
        var outside = await f.AddSourceAsync("Novo", account: null);

        Assert.Equal(403, (await Assert.ThrowsAsync<ErpChatException>(() =>
            f.AskAsync(client, new("r-x", "u", "?", DataSourceId: outside)))).Status);
        await f.AddSourceAsync("Mak Nông dân", account: client.BillingAccountId);
        Assert.Equal("data_source_required", (await Assert.ThrowsAsync<ErpChatException>(() =>
            f.AskAsync(client, new("r-y", "u", "?")))).Code);
        Assert.Equal("invalid_fields", (await Assert.ThrowsAsync<ErpChatException>(() =>
            f.AskAsync(client, new("r-z", " ", "?")))).Code);
    }

    [Fact]
    public async Task EachErpUser_HasTheirOwnRateLimit()
    {
        var f = await Fixture.CreateAsync();
        var client = await f.ClientAsync();
        for (var i = 0; i < AiUsageAdmission.UserQuestionsPerMinute; i++)
            await f.AskAsync(client, new($"a-{i}", "erp-user-a", $"Câu {i}"));

        await Assert.ThrowsAsync<AiUsageLimitException>(() => f.AskAsync(client, new("a-over", "erp-user-a", "Thêm")));
        Assert.Equal("pending", (await f.AskAsync(client, new("b-1", "erp-user-b", "Câu khác"))).Status);
    }

    [Fact]
    public async Task Get_ShowsOnlyTheAskersMessage_WithAnswerAndCost()
    {
        var f = await Fixture.CreateAsync();
        var client = await f.ClientAsync();
        var asked = await f.AskAsync(client, new("r-1", "erp-user-a", "Doanh thu?"));
        var task = await f.Db.Tasks.SingleAsync(row => row.Id == asked.MessageId);
        task.Status = TaskExecutionStatus.Completed;
        var step = await f.Db.TaskSteps.SingleAsync(row => row.TaskId == asked.MessageId);
        step.Status = TaskStepStatus.Completed;
        f.Db.TaskCheckpoints.Add(new TaskCheckpointRecord
        {
            TenantId = task.TenantId,
            CompanyId = task.CompanyId,
            TaskId = task.Id,
            StepId = step.Id,
            Version = 1,
            PayloadJson = JsonSerializer.Serialize(new { status = "completed", answer = "120 triệu." }),
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        f.Db.AiUsageRecords.Add(new AiUsageRecord
        {
            TenantId = task.TenantId,
            CompanyId = task.CompanyId,
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Purpose = "assistant",
            ProviderId = "p",
            Model = "m",
            InputTokens = 900,
            OutputTokens = 100,
            Cost = 42m
        });
        await f.Db.SaveChangesAsync();
        var results = new PilotTaskResultService(f.Db, new EfAuthorizationDirectory(f.Db));

        var message = await f.Service.GetAsync(client, asked.MessageId, "erp-user-a", results, default);

        Assert.Equal("answered", message.Status);
        Assert.Equal("120 triệu.", message.Answer);
        Assert.Equal(1000, message.Usage.InputTokens + message.Usage.OutputTokens);
        Assert.Equal(42m, message.Usage.Cost);
        Assert.Equal(404, (await Assert.ThrowsAsync<ErpChatException>(() =>
            f.Service.GetAsync(client, asked.MessageId, "erp-user-b", results, default))).Status);
    }

    [Fact]
    public async Task Report_ListsEachErpUserSeparately()
    {
        var f = await Fixture.CreateAsync();
        var client = await f.ClientAsync();
        await f.AskAsync(client, new("r-1", "erp-user-a", "?"));
        await f.AskAsync(client, new("r-2", "erp-user-b", "?"));
        var now = DateTimeOffset.UtcNow.ToOffset(AiBillingCalendar.Offset);

        var report = await new AiUsageService(f.Db, new EfAuthorizationDirectory(f.Db)).ReportAsync(f.Authority, now.Year, now.Month);

        Assert.Contains(report.Users, row => row.ExternalUserId == "erp-user-a" && row.Name == "erp-user-a · Mak (web ERP)" && row.Totals.Questions == 1);
        Assert.Contains(report.Users, row => row.ExternalUserId == "erp-user-b" && row.Totals.Questions == 1);
    }

    [Fact]
    public void Scopes_AreValidated_AndStoredInOneCanonicalOrder()
    {
        Assert.Null(ErpChatScopes.Normalize(null));
        Assert.Null(ErpChatScopes.Normalize(new Dictionary<string, string>()));
        var a = ErpChatScopes.Normalize(new Dictionary<string, string> { ["region"] = "Bắc", ["branch_id"] = "CN01" });
        var b = ErpChatScopes.Normalize(new Dictionary<string, string> { ["branch_id"] = "CN01", ["region"] = "Bắc" });
        Assert.Equal(a, b);
        Assert.Equal("CN01", ErpChatScopes.Parse(a)["branch_id"]);
        Assert.Equal("Bắc", ErpChatScopes.Parse(a)["region"]);

        foreach (var bad in new[]
        {
            new Dictionary<string, string> { ["Branch"] = "x" },
            new Dictionary<string, string> { ["1branch"] = "x" },
            new Dictionary<string, string> { ["branch-id"] = "x" },
            new Dictionary<string, string> { [ErpChatScopes.UserKey] = "someone-else" },
            new Dictionary<string, string> { ["branch_id"] = "" },
            new Dictionary<string, string> { ["branch_id"] = "a\nb" },
            new Dictionary<string, string> { ["branch_id"] = new string('x', ErpChatScopes.MaximumValueLength + 1) },
            Enumerable.Range(0, ErpChatScopes.MaximumScopes + 1).ToDictionary(i => $"k{i}", i => "v")
        })
            Assert.Equal("invalid_scopes", Assert.Throws<ErpChatException>(() => ErpChatScopes.Normalize(bad)).Code);
    }

    [Fact]
    public async Task Ask_StoresScopes_RefusesAReusedRequestWithOtherScopes_AndStartsANewConversationWhenScopesChange()
    {
        var f = await Fixture.CreateAsync();
        var client = await f.ClientAsync();
        var cn01 = new Dictionary<string, string> { ["branch_id"] = "CN01" };

        var first = await f.AskAsync(client, new("s-1", "nv001", "Doanh thu?", Scopes: cn01));
        var same = await f.AskAsync(client, new("s-2", "nv001", "Còn tháng trước?", ConversationId: first.ConversationId,
            Scopes: new Dictionary<string, string> { ["branch_id"] = "CN01" }));
        var moved = await f.AskAsync(client, new("s-3", "nv001", "Còn tuần này?", ConversationId: first.ConversationId,
            Scopes: new Dictionary<string, string> { ["branch_id"] = "CN02" }));

        Assert.Equal("""{"branch_id":"CN01"}""", (await f.Db.Tasks.SingleAsync(row => row.Id == first.MessageId)).ExternalScopesJson);
        Assert.Equal(first.ConversationId, same.ConversationId);
        Assert.Equal(moved.MessageId, moved.ConversationId);
        Assert.Equal(first.MessageId, (await f.AskAsync(client, new("s-1", "nv001", "Doanh thu?", Scopes: cn01))).MessageId);
        var conflict = await Assert.ThrowsAsync<ErpChatException>(() => f.AskAsync(client, new("s-1", "nv001", "Doanh thu?")));
        Assert.Equal("request_conflict", conflict.Code);
    }

    [Fact]
    public async Task ClientThatRequiresScopes_RefusesQuestionsWithoutThem()
    {
        var f = await Fixture.CreateAsync();
        var created = await f.Service.CreateClientAsync(f.Authority, new("Mak", f.AccountId, RequireScopes: true));
        Assert.True(created.Client.RequireScopes);
        var client = await f.Db.ErpChatClients.AsNoTracking().SingleAsync();

        Assert.Equal("scopes_required", (await Assert.ThrowsAsync<ErpChatException>(() =>
            f.AskAsync(client, new("q-1", "nv001", "Doanh thu?")))).Code);
        Assert.Equal("pending", (await f.AskAsync(client, new("q-2", "nv001", "Doanh thu?",
            Scopes: new Dictionary<string, string> { ["branch_id"] = "CN01" }))).Status);

        await f.Service.UpdateClientAsync(f.Authority, client.Id, new(true, false, "1", RequireScopes: false));
        var relaxed = await f.Db.ErpChatClients.AsNoTracking().SingleAsync();
        Assert.False(relaxed.RequireScopes);
        Assert.Equal("pending", (await f.AskAsync(relaxed, new("q-3", "nv001", "Doanh thu?"))).Status);
        await f.Service.UpdateClientAsync(f.Authority, client.Id, new(true, false, "2"));
        Assert.False((await f.Db.ErpChatClients.AsNoTracking().SingleAsync()).RequireScopes);
    }

    private static async Task Refused(Fixture f, ErpChatSignature signature, string method, string path, byte[] body, string code)
    {
        var error = await Assert.ThrowsAsync<ErpChatException>(() => f.Service.AuthenticateAsync(signature, method, path, body, default));
        Assert.Equal(code, error.Code);
    }

    private static ErpChatSignature Sign(ErpChatClientSecret client, string method, string path, byte[] body, DateTimeOffset? at = null)
    {
        var unsigned = new ErpChatSignature(client.Client.Id, (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds(), Guid.NewGuid(), "");
        var signature = HMACSHA256.HashData(Convert.FromBase64String(client.SigningKey), ErpChatService.SigningBytes(unsigned, method, path, body));
        return unsigned with { SignatureHex = Convert.ToHexString(signature) };
    }

    private sealed class Fixture
    {
        public required PlatformDbContext Db { get; init; }
        public required AuthorizationContext Authority { get; init; }
        public required Guid SourceId { get; init; }
        public required Guid AccountId { get; init; }
        public ErpChatService Service => new(Db, new EfAuthorizationDirectory(Db), new ProtectedSecretEncryption(() => Key));
        private static readonly string Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        public static async Task<Fixture> CreateAsync()
        {
            var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase($"erp-chat-{Guid.NewGuid():N}").Options);
            var (tenant, company, user, account) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            db.Users.Add(new PlatformUserRecord { TenantId = tenant, Id = user, IdentityProvider = "test", Subject = $"u-{user:N}", DisplayName = "Admin", IsActive = true });
            db.Companies.Add(new CompanyRecord { TenantId = tenant, Id = company, Code = $"C-{company:N}", Name = "Minh Huy", IsActive = true });
            db.CompanyMemberships.Add(new CompanyMembershipRecord { TenantId = tenant, CompanyId = company, UserId = user, IsActive = true });
            db.RoleAssignments.Add(new() { TenantId = tenant, CompanyId = company, UserId = user, RoleKey = "admin" });
            db.AiBillingAccounts.Add(new AiBillingAccountRecord { TenantId = tenant, CompanyId = company, Id = account, Name = "Mak" });
            await db.SaveChangesAsync();
            var fixture = new Fixture { Db = db, Authority = AuthorizationContext.Create(tenant, company, user), SourceId = Guid.Empty, AccountId = account };
            return new Fixture { Db = db, Authority = fixture.Authority, AccountId = account, SourceId = await fixture.AddSourceAsync("Mak ERP", account) };
        }

        public async Task<Guid> AddSourceAsync(string name, Guid? account)
        {
            var id = Guid.NewGuid();
            Db.DataSources.Add(new DataSourceRecord
            {
                TenantId = Authority.TenantId,
                CompanyId = Authority.CompanyId,
                Id = id,
                LogicalName = name,
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
            if (account is { } accountId)
                Db.AiBillingAccountSources.Add(new AiBillingAccountSourceRecord { TenantId = Authority.TenantId, CompanyId = Authority.CompanyId, SourceId = id, AccountId = accountId });
            if (!await Db.DataSourceSecretBindings.AnyAsync()) BindingFixture.Grant(Db, Authority.TenantId, Authority.CompanyId, "secretref://env/PILOT_ERP_CONNECTION");
            await Db.SaveChangesAsync();
            return id;
        }

        public Task<ErpChatClientSecret> CreateClientAsync() => Service.CreateClientAsync(Authority, new("Mak", AccountId));

        public async Task<ErpChatClientRecord> ClientAsync()
        {
            await CreateClientAsync();
            return await Db.ErpChatClients.AsNoTracking().SingleAsync();
        }

        public async Task<ErpChatAccepted> AskAsync(ErpChatClientRecord client, ErpChatAskRequest request)
        {
            var accepted = await Service.AskAsync(client, request, new PilotTaskSubmissionService(Db, new EfAuthorizationDirectory(Db)), default);
            await Task.Delay(2);
            return accepted;
        }
    }
}
