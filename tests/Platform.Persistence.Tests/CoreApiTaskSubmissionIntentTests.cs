using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class CoreApiDataSourceAuthorizationIntegrationTests
{
    [Fact]
    public async Task IntentApiPreparesWithoutTaskThenExplicitSubmitReturnsBoundTyped202AndReadOnlyRecovery()
    {
        var owner = MemberDirectoryFixture.Authority(); await using var fixture = new MemberApiFixture(owner);
        var source = await SeedIntentApiAsync(fixture, owner); using var client = IntentClient(fixture, owner);
        // Caller authority headers cannot replace the authenticated directory owner.
        client.DefaultRequestHeaders.Add("X-AIOffice-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-AIOffice-Tenant-Id", Guid.NewGuid().ToString());
        var input = SubmissionIntentFixture.Request(source);
        var response = await client.PostAsJsonAsync("/api/tasks/intents", input);
        await AssertIntentResponseAsync(response, HttpStatusCode.OK);
        var prepared = (await response.Content.ReadFromJsonAsync<TaskSubmissionIntentDetail>())!;
        Assert.Equal(owner.CompanyId, prepared.CompanyId); Assert.Equal(input.OperationId, prepared.OperationId);
        Assert.Equal(TaskSubmissionIntentState.Prepared, prepared.State); Assert.Equal(input.Question, prepared.Question);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>(); Assert.Single(db.TaskSubmissionIntents);
            Assert.Equal(owner.UserId, db.TaskSubmissionIntents.Single().UserId); Assert.Empty(db.Tasks); Assert.Empty(db.TaskDispatches);
        }
        var path = $"/api/tasks/intents/{input.OperationId:D}";
        var execute = new TaskSubmissionExecuteRequest(prepared.InputFingerprint!);
        response = await client.PostAsJsonAsync(path + "/submit", execute); await AssertIntentResponseAsync(response, HttpStatusCode.Accepted);
        var accepted = (await response.Content.ReadFromJsonAsync<TaskSubmissionAcceptedReceipt>())!;
        Assert.Equal(prepared.OperationId, accepted.OperationId); Assert.Equal(prepared.CompanyId, accepted.CompanyId);
        Assert.Equal(prepared.InputFingerprint, accepted.InputFingerprint); Assert.Equal(source, accepted.DataSourceId);
        Assert.Equal(PilotTaskIdentity.ForTask(owner, TaskSubmissionIntentIdentity.IdempotencyKey(input.OperationId)), accepted.TaskId);
        Assert.Equal($"/api/tasks/{accepted.TaskId:D}/history", response.Headers.Location!.ToString());
        response = await client.PostAsJsonAsync(path + "/submit", execute); await AssertIntentResponseAsync(response, HttpStatusCode.Accepted);
        Assert.Equal(accepted, await response.Content.ReadFromJsonAsync<TaskSubmissionAcceptedReceipt>());
        var detail = (await client.GetFromJsonAsync<TaskSubmissionIntentDetail>(path))!;
        Assert.Equal(TaskSubmissionIntentState.Accepted, detail.State); Assert.Equal(accepted, detail.Accepted);
        var page = (await client.GetFromJsonAsync<TaskSubmissionIntentPage>("/api/tasks/intents?offset=0&limit=25"))!;
        Assert.Equal(detail, Assert.Single(page.Items));
        await using var check = fixture.Factory.Services.CreateAsyncScope(); var database = check.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.Single(database.Tasks); Assert.Single(database.TaskSteps); Assert.Single(database.TaskDispatches); Assert.Single(database.TaskStepExecutions);
        Assert.Single(database.TaskEvents); Assert.Single(database.TaskSubmissionIntents);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"operationId\":\"OPERATION\",\"dataSourceId\":\"SOURCE\",\"question\":\"test\",\"ownerId\":\"OPERATION\"}")]
    [InlineData("{\"operationId\":\"OPERATION\",\"dataSourceId\":\"SOURCE\",\"question\":\"test\",\"tenantId\":\"OPERATION\"}")]
    [InlineData("{\"operationId\":\"OPERATION\",\"dataSourceId\":\"SOURCE\",\"question\":\"test\",\"maxAttempts\":3}")]
    [InlineData("{\"operationId\":\"OPERATION\",\"dataSourceId\":\"SOURCE\",\"question\":\"test\",\"question\":\"PRIVATE_BODY\"}")]
    [InlineData("{\"operationId\":\"OPERATION\",\"\\u006fperationId\":\"OPERATION\",\"dataSourceId\":\"SOURCE\",\"question\":\"test\"}")]
    [InlineData("{\"OperationId\":\"OPERATION\",\"dataSourceId\":\"SOURCE\",\"question\":\"test\"}")]
    [InlineData("{\"operationId\":\"00000000-0000-0000-0000-000000000000\",\"dataSourceId\":\"SOURCE\",\"question\":\"test\"}")]
    [InlineData("{\"operationId\":\"OPERATION\",\"dataSourceId\":\"00000000-0000-0000-0000-000000000000\",\"question\":\"test\"}")]
    [InlineData("{\"operationId\":\"OPERATION\",\"dataSourceId\":\"SOURCE\",\"question\":\"\\ud800\"}")]
    [InlineData("{\"operationId\":\"OPERATION\",\"dataSourceId\":\"SOURCE\",\"question\":\"\\udfff\"}")]
    [InlineData("{\"operationId\":\"OPERATION\",\"dataSourceId\":\"SOURCE\",\"question\":\" test\"}")]
    [InlineData("{\"operationId\":\"OPERATION\",\"dataSourceId\":\"SOURCE\",\"question\":\"test\\n\"}")]
    [InlineData("{\"operationId\":\"OPERATION\",\"dataSourceId\":\"SOURCE\",\"question\":null}")]
    public async Task IntentApiRejectsUnknownDecodedDuplicateMissingAndMalformedScalarFieldsWithoutEffects(string body)
    {
        var owner = MemberDirectoryFixture.Authority(); await using var fixture = new MemberApiFixture(owner);
        var source = await SeedIntentApiAsync(fixture, owner); using var client = IntentClient(fixture, owner);
        body = body.Replace("OPERATION", Guid.NewGuid().ToString("D"), StringComparison.Ordinal).Replace("SOURCE", source.ToString("D"), StringComparison.Ordinal);
        await AssertIntentResponseAsync(await client.PostAsync("/api/tasks/intents", new StringContent(body, Encoding.UTF8, "application/json")), HttpStatusCode.BadRequest);
        await using var check = fixture.Factory.Services.CreateAsyncScope(); var db = check.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.Empty(db.TaskSubmissionIntents); Assert.Empty(db.Tasks); Assert.Empty(db.TaskDispatches);
    }

    [Theory]
    [InlineData("offset=-1")]
    [InlineData("offset=10001")]
    [InlineData("offset=01")]
    [InlineData("offset=+1")]
    [InlineData("offset=2147483648")]
    [InlineData("limit=0")]
    [InlineData("limit=26")]
    [InlineData("limit=garbage")]
    [InlineData("limit=1&limit=2")]
    [InlineData("%6cimit=1&limit=2")]
    [InlineData("ownerId=00000000-0000-0000-0000-000000000001")]
    public async Task IntentApiRejectsAmbiguousOrUnboundedPageSelectors(string query)
    {
        var owner = MemberDirectoryFixture.Authority(); await using var fixture = new MemberApiFixture(owner);
        await SeedIntentApiAsync(fixture, owner); using var client = IntentClient(fixture, owner);
        await AssertIntentResponseAsync(await client.GetAsync("/api/tasks/intents?" + query), HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("11111111111141118111111111111111")]
    [InlineData("not-a-guid")]
    public async Task IntentApiRequiresNonzeroCanonicalDOperationSelector(string operation)
    {
        var owner = MemberDirectoryFixture.Authority(); await using var fixture = new MemberApiFixture(owner);
        await SeedIntentApiAsync(fixture, owner); using var client = IntentClient(fixture, owner);
        var path = "/api/tasks/intents/" + operation;
        await AssertIntentResponseAsync(await client.GetAsync(path), HttpStatusCode.BadRequest);
        await AssertIntentResponseAsync(await client.PostAsJsonAsync(path + "/submit", new TaskSubmissionExecuteRequest(new string('A', 64))), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task IntentApiRejectsStrictUtf8AndActualBodyOverflowAndAcceptsFullyEscapedMaxScalarQuestion()
    {
        var owner = MemberDirectoryFixture.Authority(); await using var fixture = new MemberApiFixture(owner);
        var source = await SeedIntentApiAsync(fixture, owner); using var client = IntentClient(fixture, owner);
        using var invalid = new ByteArrayContent([(byte)'{', (byte)'"', 0xC0, 0xAF, (byte)'"', (byte)':', (byte)'0', (byte)'}']);
        invalid.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        await AssertIntentResponseAsync(await client.PostAsync("/api/tasks/intents", invalid), HttpStatusCode.BadRequest);
        using var tooLarge = new UnknownLengthContent(Encoding.UTF8.GetBytes(new string(' ', 32769)));
        await AssertIntentResponseAsync(await client.PostAsync("/api/tasks/intents", tooLarge), HttpStatusCode.RequestEntityTooLarge);
        await AssertIntentResponseAsync(await client.PostAsync("/api/tasks/intents", new StringContent(new string(' ', 32769), Encoding.UTF8, "application/json")), HttpStatusCode.RequestEntityTooLarge);
        var input = SubmissionIntentFixture.Request(source) with { Question = string.Concat(Enumerable.Repeat("😀", 2000)) };
        var json = JsonSerializer.Serialize(input, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.InRange(Encoding.UTF8.GetByteCount(json), 24000, 32768);
        var response = await client.PostAsync("/api/tasks/intents", new StringContent(json, Encoding.UTF8, "application/json"));
        await AssertIntentResponseAsync(response, HttpStatusCode.OK); Assert.Equal(input.Question, (await response.Content.ReadFromJsonAsync<TaskSubmissionIntentDetail>())!.Question);
        await using var check = fixture.Factory.Services.CreateAsyncScope(); var db = check.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.Single(db.TaskSubmissionIntents); Assert.Empty(db.Tasks);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"inputFingerprint\":null}")]
    [InlineData("{\"inputFingerprint\":\"lowercase\"}")]
    [InlineData("{\"inputFingerprint\":\"FINGERPRINT\",\"question\":\"PRIVATE_CHANGED\"}")]
    [InlineData("{\"inputFingerprint\":\"FINGERPRINT\",\"inputFingerprint\":\"FINGERPRINT\"}")]
    public async Task IntentApiExecuteAcceptsOnlyExactFingerprintAndNoReplacementBody(string body)
    {
        var owner = MemberDirectoryFixture.Authority(); await using var fixture = new MemberApiFixture(owner);
        var source = await SeedIntentApiAsync(fixture, owner); using var client = IntentClient(fixture, owner); var input = SubmissionIntentFixture.Request(source);
        var response = await client.PostAsJsonAsync("/api/tasks/intents", input); var prepared = (await response.Content.ReadFromJsonAsync<TaskSubmissionIntentDetail>())!;
        body = body.Replace("FINGERPRINT", prepared.InputFingerprint, StringComparison.Ordinal);
        await AssertIntentResponseAsync(await client.PostAsync($"/api/tasks/intents/{input.OperationId:D}/submit", new StringContent(body, Encoding.UTF8, "application/json")), HttpStatusCode.BadRequest);
        await using var check = fixture.Factory.Services.CreateAsyncScope(); Assert.Empty(check.ServiceProvider.GetRequiredService<PlatformDbContext>().Tasks);
    }

    [Fact]
    public async Task IntentApiKnownConflictForeignLookupAndSourceRevocationAreBoundedAndRestorationWorks()
    {
        var owner = MemberDirectoryFixture.Authority(); await using var fixture = new MemberApiFixture(owner);
        var source = await SeedIntentApiAsync(fixture, owner); using var client = IntentClient(fixture, owner); var input = SubmissionIntentFixture.Request(source);
        var response = await client.PostAsJsonAsync("/api/tasks/intents", input); var prepared = (await response.Content.ReadFromJsonAsync<TaskSubmissionIntentDetail>())!;
        var path = $"/api/tasks/intents/{input.OperationId:D}";
        response = await client.PostAsJsonAsync("/api/tasks/intents", input with { Question = "Other question" });
        await AssertIntentResponseAsync(response, HttpStatusCode.Conflict); Assert.Contains("operation-conflict", await response.Content.ReadAsStringAsync());
        await AssertIntentResponseAsync(await client.PostAsJsonAsync(path + "/submit", new TaskSubmissionExecuteRequest(new string('A', 64))), HttpStatusCode.Conflict);
        var execute = new TaskSubmissionExecuteRequest(prepared.InputFingerprint!);
        response = await client.PostAsJsonAsync(path + "/submit", execute); var accepted = (await response.Content.ReadFromJsonAsync<TaskSubmissionAcceptedReceipt>())!;
        Guid foreignOperation;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var other = AuthorizationContext.Create(owner.TenantId, owner.CompanyId, Guid.NewGuid()); MemberDirectoryFixture.AddMember(db, owner, other.UserId, "Other", roles: ["admin"]);
            var foreign = SubmissionIntentFixture.Request(source); foreignOperation = foreign.OperationId; db.TaskSubmissionIntents.Add(SubmissionIntentFixture.Row(other, foreign));
            (await db.DataSourceSecretBindings.SingleAsync()).IsEnabled = false; await db.SaveChangesAsync();
        }
        await AssertIntentResponseAsync(await client.PostAsJsonAsync(path + "/submit", execute), HttpStatusCode.Forbidden);
        await AssertIntentResponseAsync(await client.GetAsync($"/api/tasks/intents/{foreignOperation:D}"), HttpStatusCode.NotFound);
        await AssertIntentResponseAsync(await client.PostAsJsonAsync($"/api/tasks/intents/{foreignOperation:D}/submit", execute), HttpStatusCode.NotFound);
        var restored = (await client.GetFromJsonAsync<TaskSubmissionIntentDetail>(path))!; Assert.Equal(accepted, restored.Accepted);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        { var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>(); (await db.DataSourceSecretBindings.SingleAsync()).IsEnabled = true; await db.SaveChangesAsync(); }
        response = await client.PostAsJsonAsync(path + "/submit", execute); Assert.Equal(accepted, await response.Content.ReadFromJsonAsync<TaskSubmissionAcceptedReceipt>());
        await AssertIntentResponseAsync(await client.GetAsync(path + "?companyId=" + owner.CompanyId), HttpStatusCode.BadRequest);
        await AssertIntentResponseAsync(await client.PostAsJsonAsync(path + "/submit?ownerId=" + owner.UserId, execute), HttpStatusCode.BadRequest);
        await using var check = fixture.Factory.Services.CreateAsyncScope(); var database = check.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.Single(database.Tasks); Assert.Single(database.TaskDispatches);
    }

    [Fact]
    public async Task IntentApiFreshMembershipLossDeniesPreparationExecutionAndRecoveryUntilRestored()
    {
        var owner = MemberDirectoryFixture.Authority(); await using var fixture = new MemberApiFixture(owner);
        var source = await SeedIntentApiAsync(fixture, owner); using var client = IntentClient(fixture, owner); var input = SubmissionIntentFixture.Request(source);
        var response = await client.PostAsJsonAsync("/api/tasks/intents", input); var prepared = (await response.Content.ReadFromJsonAsync<TaskSubmissionIntentDetail>())!;
        var path = $"/api/tasks/intents/{input.OperationId:D}";
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        { var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>(); (await db.CompanyMemberships.SingleAsync()).IsActive = false; await db.SaveChangesAsync(); }
        foreach (var route in new[] { "/api/tasks/intents", path }) await AssertIntentResponseAsync(await client.GetAsync(route), HttpStatusCode.Forbidden);
        await AssertIntentResponseAsync(await client.PostAsJsonAsync("/api/tasks/intents", input), HttpStatusCode.Forbidden);
        await AssertIntentResponseAsync(await client.PostAsJsonAsync(path + "/submit", new TaskSubmissionExecuteRequest(prepared.InputFingerprint!)), HttpStatusCode.Forbidden);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        { var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>(); Assert.Empty(db.Tasks); (await db.CompanyMemberships.SingleAsync()).IsActive = true; await db.SaveChangesAsync(); }
        Assert.Equal(input.Question, (await client.GetFromJsonAsync<TaskSubmissionIntentDetail>(path))!.Question);
    }

    [Theory]
    [InlineData("/api/tasks/intents", "GET")]
    [InlineData("/api/tasks/intents", "POST")]
    [InlineData("/api/tasks/intents/11111111-1111-4111-8111-111111111111", "GET")]
    [InlineData("/api/tasks/intents/11111111-1111-4111-8111-111111111111/submit", "POST")]
    public async Task UnconfiguredIntentApiIsNoStoreUnavailable(string path, string method)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        { builder.UseSetting("AIOffice:Authentication:Authority", ""); builder.UseSetting("AIOffice:Authentication:Audience", ""); });
        using var client = factory.CreateClient(); using var request = new HttpRequestMessage(new HttpMethod(method), path);
        await AssertIntentResponseAsync(await client.SendAsync(request), HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task LegacyTaskHeaderRetainsTypedAcceptanceAndRejectsReplacingAnImmutableBrowserOperation()
    {
        var owner = MemberDirectoryFixture.Authority(); await using var fixture = new MemberApiFixture(owner);
        var source = await SeedIntentApiAsync(fixture, owner); using var client = IntentClient(fixture, owner); var input = SubmissionIntentFixture.Request(source);
        await AssertIntentResponseAsync(await client.PostAsJsonAsync("/api/tasks/intents", input), HttpStatusCode.OK);
        client.DefaultRequestHeaders.Add("Idempotency-Key", TaskSubmissionIntentIdentity.IdempotencyKey(input.OperationId));
        await AssertIntentResponseAsync(await client.PostAsJsonAsync("/api/tasks", new CustomerPilotTaskRequest(source, "Other input")), HttpStatusCode.Conflict);
        var response = await client.PostAsJsonAsync("/api/tasks", new CustomerPilotTaskRequest(source, input.Question));
        await AssertIntentResponseAsync(response, HttpStatusCode.Accepted);
        var legacy = (await response.Content.ReadFromJsonAsync<CustomerPilotTaskSubmissionResult>())!;
        var restored = (await client.GetFromJsonAsync<TaskSubmissionIntentDetail>($"/api/tasks/intents/{input.OperationId:D}"))!;
        Assert.Equal(legacy.TaskId, restored.Accepted!.TaskId); Assert.Equal(legacy.StepId, restored.Accepted.StepId);
        Assert.Equal(legacy.MessageId, restored.Accepted.MessageId); Assert.Equal(legacy.IdempotencyKey, TaskSubmissionIntentIdentity.IdempotencyKey(input.OperationId));
        await using var check = fixture.Factory.Services.CreateAsyncScope(); Assert.Single(check.ServiceProvider.GetRequiredService<PlatformDbContext>().Tasks);
    }

    private static HttpClient IntentClient(MemberApiFixture fixture, AuthorizationContext owner)
    {
        var client = fixture.Factory.CreateClient(); client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, owner.CompanyId.ToString()); return client;
    }
    private static async Task<Guid> SeedIntentApiAsync(MemberApiFixture fixture, AuthorizationContext owner)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var source = SubmissionIntentFixture.Seed(db, owner); await db.SaveChangesAsync(); return source;
    }
    private static async Task AssertIntentResponseAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("PRIVATE_", body); Assert.DoesNotContain("Exception", body);
        Assert.DoesNotContain("secretref", body); Assert.InRange(Encoding.UTF8.GetByteCount(body), 0, 262144);
    }
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; Headers.ContentType = new MediaTypeHeaderValue("application/json"); return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
}
