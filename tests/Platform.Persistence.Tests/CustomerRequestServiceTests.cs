using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

internal sealed class CustomerRequestFixture : IDisposable
{
    internal readonly AuthorizationContext Authority = MemberDirectoryFixture.Authority();
    internal readonly Guid ServiceId = Guid.NewGuid();
    internal readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    internal readonly string Variable = "AIOFFICE_CUSTOMER_REQUEST_TEST_" + Guid.NewGuid().ToString("N");
    internal readonly DbContextOptions<PlatformDbContext> Options = MemberDirectoryFixture.Options();
    internal readonly CompositeSecretResolver Secrets = new([new EnvironmentVariableSecretResolver()]);
    internal CustomerRequestServiceDirectory Directory { get; }
    internal PlatformDbContext Database() => new(Options);
    internal CustomerRequestFixture(bool grantBotChannelGroups = false)
    {
        Environment.SetEnvironmentVariable(Variable, Convert.ToBase64String(Key));
        Directory = new([new(ServiceId, Authority.TenantId, Authority.CompanyId, "secretref://env/" + Variable, ["customer-group-1"], grantBotChannelGroups)]);
        using var db = Database(); MemberDirectoryFixture.Seed(db, Authority, role: null); db.SaveChanges();
    }
    internal CustomerRequestAuthenticator Authenticator() => new(Directory, Secrets, TimeProvider.System);
    internal CustomerRequestIngress Ingress(PlatformDbContext db) => new(db, Authenticator(), TimeProvider.System);
    internal CustomerRequestPortal Portal(PlatformDbContext db) => new(db, new EfAuthorizationDirectory(db));
    // Independent bot-side canonicalization, rather than using production SigningBytes.
    internal CustomerRequestSignature Sign(byte[] body, string domain = "aioffice-customer-request-v1", string method = "POST",
        string path = "/internal/customer-requests", long? timestamp = null, Guid? nonce = null, Guid? service = null)
    {
        var id = service ?? ServiceId;
        var time = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var value = nonce ?? Guid.NewGuid();
        var text = string.Join("\n", domain, id.ToString("D"), method, path, time.ToString(CultureInfo.InvariantCulture), value.ToString("D"), Convert.ToHexString(SHA256.HashData(body)));
        return new(id, time, value, Convert.ToHexString(HMACSHA256.HashData(Key, Encoding.ASCII.GetBytes(text))));
    }
    internal async Task<CustomerRequestReceipt> Accept(CustomerRequestInput? input = null)
    {
        await using var db = Database(); var body = CustomerRequestContractTests.Bytes(input);
        return await Ingress(db).AcceptAsync(Sign(body), body);
    }
    public void Dispose() { Environment.SetEnvironmentVariable(Variable, null); CryptographicOperations.ZeroMemory(Key); }
}

public sealed class CustomerRequestServiceTests
{
    [Fact]
    public async Task CreatesAtomicTicketGraphAndIdempotentReplayWithFreshNonceReturnsOriginal()
    {
        using var fixture = new CustomerRequestFixture();
        var first = await fixture.Accept(); var replay = await fixture.Accept(CustomerRequestContractTests.Sample() with { Summary = "Replayed text must not overwrite source" });
        Assert.Equal("YC-0001", first.Code); Assert.Equal("new", first.Status); Assert.Equal(first, replay);
        var next = await fixture.Accept(CustomerRequestContractTests.Sample() with { IdempotencyKey = "summary-002" }); Assert.Equal("YC-0002", next.Code);
        await using var db = fixture.Database();
        Assert.Equal(2, await db.CustomerRequests.CountAsync()); Assert.Equal(2, await db.CustomerRequestItems.CountAsync());
        Assert.Equal(2, await db.CustomerRequestEvidence.CountAsync()); Assert.Equal(2, await db.CustomerRequestEvents.CountAsync());
        Assert.Empty(db.CustomerRequestOpenQuestions); Assert.Empty(db.Tasks); Assert.Empty(db.GroupIngressOutbox);
        var stored = await db.CustomerRequests.SingleAsync(x => x.Id == first.Id);
        Assert.Equal("ai_extracted", stored.Origin); Assert.Null(stored.AssigneeUserId); Assert.Equal(CustomerRequestContractTests.Sample().Summary, stored.Summary);
        Assert.Equal(CustomerRequestContractTests.Sample().CustomerDesiredDueText, stored.CustomerDesiredDueText);
        var created = await db.CustomerRequestEvents.SingleAsync(x => x.RequestId == first.Id);
        Assert.Equal("service", created.ActorType); Assert.Equal(fixture.ServiceId, created.ActorId); Assert.Null(created.OldValue); Assert.Equal("new", created.NewValue);
    }

    [Fact]
    public async Task MissingInformationIsAutomaticallyRecordedWithEvidenceAndNeedsClarification()
    {
        using var fixture = new CustomerRequestFixture();
        var receipt = await fixture.Accept(CustomerRequestContractTests.Sample() with { OpenQuestions = ["Phiên bản ERP nào?", "Tái hiện như thế nào?"] });
        Assert.Equal("needs_clarification", receipt.Status);
        await using var db = fixture.Database(); var detail = await fixture.Portal(db).GetAsync(fixture.Authority, fixture.Authority.CompanyId, receipt.Code);
        Assert.Equal(new[] { "Phiên bản ERP nào?", "Tái hiện như thế nào?" }, detail.OpenQuestions); Assert.Single(detail.Evidence); Assert.Single(detail.Items);
        Assert.Equal("needs_clarification", Assert.Single(detail.Events).NewValue);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("domain")]
    [InlineData("method")]
    [InlineData("path")]
    [InlineData("body")]
    [InlineData("service")]
    [InlineData("group")]
    [InlineData("company")]
    [InlineData("inactive-company")]
    [InlineData("missing-secret")]
    [InlineData("bad-secret")]
    public async Task AuthenticationAndScopeFailuresStoreNothing(string change)
    {
        using var fixture = new CustomerRequestFixture();
        await using var db = fixture.Database();
        var input = CustomerRequestContractTests.Sample();
        if (change == "group") input = input with { SourceGroupExternalId = "unconfigured" };
        if (change == "company") { db.Companies.RemoveRange(db.Companies); await db.SaveChangesAsync(); }
        if (change == "inactive-company") { db.Companies.Single().IsActive = false; await db.SaveChangesAsync(); }
        if (change == "missing-secret") Environment.SetEnvironmentVariable(fixture.Variable, null);
        if (change == "bad-secret") Environment.SetEnvironmentVariable(fixture.Variable, "not-a-credential");
        var body = CustomerRequestContractTests.Bytes(input);
        var signature = fixture.Sign(body, domain: change == "domain" ? "aioffice-group-ingest-v1" : CustomerRequestAuthenticator.Domain,
            method: change == "method" ? "GET" : "POST", path: change == "path" ? "/internal/group-ingress/events" : CustomerRequestAuthenticator.Path,
            timestamp: DateTimeOffset.UtcNow.AddMinutes(change == "stale" ? -6 : change == "future" ? 6 : 0).ToUnixTimeSeconds(),
            service: change == "service" ? Guid.NewGuid() : null);
        if (change == "signature") signature = signature with { SignatureHex = new string('0', 64) };
        if (change == "body") body = CustomerRequestContractTests.Bytes(input with { Summary = "tampered" });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Ingress(db).AcceptAsync(signature, body));
        await using var check = fixture.Database(); Assert.Empty(check.CustomerRequests); Assert.Empty(check.CustomerRequestEvents);
        Assert.Empty(check.CustomerRequestNonces); Assert.Empty(check.CustomerRequestCounters);
    }

    [Fact]
    public async Task NonceCannotBeReplayedAcrossRequestsEvenForDifferentIdempotencyKey()
    {
        using var fixture = new CustomerRequestFixture(); var body = CustomerRequestContractTests.Bytes(); var signature = fixture.Sign(body);
        await using (var db = fixture.Database()) await fixture.Ingress(db).AcceptAsync(signature, body);
        await using (var db = fixture.Database()) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Ingress(db).AcceptAsync(signature, body));
        var other = CustomerRequestContractTests.Bytes(CustomerRequestContractTests.Sample() with { IdempotencyKey = "other" });
        await using (var db = fixture.Database()) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Ingress(db).AcceptAsync(fixture.Sign(other, nonce: signature.Nonce), other));
        await using var check = fixture.Database(); Assert.Single(check.CustomerRequests); Assert.Single(check.CustomerRequestNonces);
    }

    [Fact]
    public async Task AnyActiveMemberMayUpdateAndAuditBothChangesWithConcurrencyAndExplicitUnassign()
    {
        using var fixture = new CustomerRequestFixture(); var receipt = await fixture.Accept();
        var member = Guid.NewGuid();
        await using (var seed = fixture.Database()) { MemberDirectoryFixture.AddMember(seed, fixture.Authority, member, "Technician"); await seed.SaveChangesAsync(); }
        var actor = AuthorizationContext.Create(fixture.Authority.TenantId, fixture.Authority.CompanyId, member);
        await using var db = fixture.Database(); var portal = fixture.Portal(db);
        var detail = await portal.GetAsync(actor, actor.CompanyId, receipt.Code);
        var result = await portal.PatchAsync(actor, actor.CompanyId, receipt.Code, new(detail.Request.RowVersion, "in_progress", member, true));
        Assert.NotEqual(detail.Request.RowVersion, result.RowVersion); Assert.Equal(member, result.AssigneeUserId);
        await using (var stale = fixture.Database()) await Assert.ThrowsAsync<CustomerRequestConflictException>(() => fixture.Portal(stale).PatchAsync(actor, actor.CompanyId, receipt.Code, new(detail.Request.RowVersion, "done", null, false)));
        var updated = await portal.GetAsync(actor, actor.CompanyId, receipt.Code);
        Assert.Equal(3, updated.Events.Count); Assert.All(updated.Events.Where(x => x.ActorType == "user"), x => Assert.Equal(member, x.ActorId));
        var status = Assert.Single(updated.Events, x => x.EventType == "status_changed"); Assert.Equal("new", status.OldValue); Assert.Equal("in_progress", status.NewValue);
        var assignment = Assert.Single(updated.Events, x => x.EventType == "assignee_changed"); Assert.Null(assignment.OldValue); Assert.Equal(member.ToString("D"), assignment.NewValue);
        var unassigned = await portal.PatchAsync(actor, actor.CompanyId, receipt.Code, new(result.RowVersion, null, null, true)); Assert.Null(unassigned.AssigneeUserId);
        var noOp = await portal.PatchAsync(actor, actor.CompanyId, receipt.Code, new(unassigned.RowVersion, "in_progress", null, false));
        Assert.Equal(unassigned.RowVersion, noOp.RowVersion); Assert.Equal(4, await db.CustomerRequestEvents.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllPortalOperationsEnforceCompanyAndTenantScope(bool sameCompanyOtherTenant)
    {
        using var fixture = new CustomerRequestFixture(); var ticket = await fixture.Accept();
        var foreign = AuthorizationContext.Create(Guid.NewGuid(), sameCompanyOtherTenant ? fixture.Authority.CompanyId : Guid.NewGuid(), Guid.NewGuid());
        await using var db = fixture.Database(); MemberDirectoryFixture.Seed(db, foreign, role: null); await db.SaveChangesAsync(); var portal = fixture.Portal(db);
        if (sameCompanyOtherTenant)
        {
            Assert.Empty((await portal.ListAsync(foreign, foreign.CompanyId)).Items);
            await Assert.ThrowsAsync<CustomerRequestNotFoundException>(() => portal.GetAsync(foreign, foreign.CompanyId, ticket.Code));
            await Assert.ThrowsAsync<CustomerRequestNotFoundException>(() => portal.PatchAsync(foreign, foreign.CompanyId, ticket.Code, new("AAAAAAAAAAA=", "done", null, false)));
        }
        else
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => portal.ListAsync(foreign, fixture.Authority.CompanyId));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => portal.GetAsync(foreign, fixture.Authority.CompanyId, ticket.Code));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => portal.PatchAsync(foreign, fixture.Authority.CompanyId, ticket.Code, new("AAAAAAAAAAA=", "done", null, false)));
        }
        Assert.Equal("new", (await db.CustomerRequests.AsNoTracking().SingleAsync()).Status); Assert.Single(db.CustomerRequestEvents);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("membership")]
    [InlineData("company")]
    public async Task RevocationImmediatelyDeniesAllPortalOperations(string revoke)
    {
        using var fixture = new CustomerRequestFixture(); var receipt = await fixture.Accept();
        await using var db = fixture.Database(); var portal = fixture.Portal(db);
        var detail = await portal.GetAsync(fixture.Authority, fixture.Authority.CompanyId, receipt.Code);
        if (revoke == "user") db.Users.Single().IsActive = false;
        if (revoke == "membership") db.CompanyMemberships.Single().IsActive = false;
        if (revoke == "company") db.Companies.Single().IsActive = false;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => portal.ListAsync(fixture.Authority, fixture.Authority.CompanyId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => portal.GetAsync(fixture.Authority, fixture.Authority.CompanyId, receipt.Code));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => portal.PatchAsync(fixture.Authority, fixture.Authority.CompanyId, receipt.Code, new(detail.Request.RowVersion, "done", null, false)));
    }

    [Fact]
    public async Task FiltersAndStablePagingAreAppliedWithinCompany()
    {
        using var fixture = new CustomerRequestFixture(); await fixture.Accept();
        await fixture.Accept(CustomerRequestContractTests.Sample() with { IdempotencyKey = "two", CustomerName = "Other", OpenQuestions = ["Details?"] });
        await using var db = fixture.Database(); var portal = fixture.Portal(db); var scope = fixture.Authority;
        Assert.Equal("YC-0001", Assert.Single((await portal.ListAsync(scope, scope.CompanyId, customer: "Customer")).Items).Code);
        Assert.Equal("YC-0002", Assert.Single((await portal.ListAsync(scope, scope.CompanyId, status: "needs_clarification")).Items).Code);
        var first = await portal.ListAsync(scope, scope.CompanyId, limit: 1); var next = await portal.ListAsync(scope, scope.CompanyId, offset: 1, limit: 1);
        Assert.True(first.HasMore); Assert.False(next.HasMore); Assert.NotEqual(first.Items.Single().Id, next.Items.Single().Id);
    }

    [Fact]
    public void ConfigCannotMapOneServiceToMultipleCompaniesOrUsePlaintextSecret()
    {
        using var fixture = new CustomerRequestFixture(); var binding = new CustomerRequestServiceBinding(fixture.ServiceId, fixture.Authority.TenantId,
            fixture.Authority.CompanyId, "secretref://env/" + fixture.Variable, ["customer-group-1"]);
        Assert.Throws<InvalidOperationException>(() => new CustomerRequestServiceDirectory([binding, binding with { CompanyId = Guid.NewGuid() }]));
        Assert.Throws<InvalidOperationException>(() => new CustomerRequestServiceDirectory([binding with { SecretRef = "plaintext" }]));
    }
}
