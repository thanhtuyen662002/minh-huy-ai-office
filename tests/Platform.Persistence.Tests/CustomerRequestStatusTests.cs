using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class CustomerRequestStatusTests
{
    internal static byte[] Bytes(string code = "YC-0001", string status = "done", string sourceMessageId = "tech-msg-1") =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { code, status, actorName = "Minh Kỹ thuật", actorExternalId = "zalo-user-7", sourceMessageId }));

    private static async Task<CustomerRequestStatusReceipt> Update(CustomerRequestFixture fixture, byte[] body)
    {
        await using var db = fixture.Database();
        return await new CustomerRequestStatusUpdater(db, fixture.Authenticator(), TimeProvider.System)
            .UpdateAsync(fixture.Sign(body, path: CustomerRequestStatusUpdater.Path), body);
    }

    [Fact]
    public async Task ZaloCommandChangesStatusRecordsActorAndPortalShowsName()
    {
        using var fixture = new CustomerRequestFixture(); var receipt = await fixture.Accept();
        DateTimeOffset before; byte[] version;
        await using (var db = fixture.Database()) { var stored = await db.CustomerRequests.SingleAsync(); before = stored.UpdatedAt; version = stored.RowVersion; }
        var result = await Update(fixture, Bytes(receipt.Code));
        Assert.Equal(new CustomerRequestStatusReceipt(receipt.Code, "done", "new", true), result);
        await using var check = fixture.Database();
        var ticket = await check.CustomerRequests.SingleAsync();
        Assert.Equal("done", ticket.Status); Assert.True(ticket.UpdatedAt >= before); Assert.NotEqual(version, ticket.RowVersion);
        var change = await check.CustomerRequestEvents.SingleAsync(x => x.EventType == "status_changed");
        Assert.Equal(CustomerRequestStatusUpdater.ActorType, change.ActorType); Assert.Equal(fixture.ServiceId, change.ActorId);
        Assert.Equal("Minh Kỹ thuật", change.ActorName); Assert.Equal("zalo-user-7", change.ActorExternalId); Assert.Equal("tech-msg-1", change.SourceMessageId);
        Assert.Equal("new", change.OldValue); Assert.Equal("done", change.NewValue);
        var detail = await fixture.Portal(check).GetAsync(fixture.Authority, fixture.Authority.CompanyId, receipt.Code);
        var shown = Assert.Single(detail.Events, x => x.EventType == "status_changed");
        Assert.Equal("Minh Kỹ thuật", shown.ActorName); Assert.Equal("zalo_member", shown.ActorType);
        Assert.Null(Assert.Single(detail.Events, x => x.EventType == "created").ActorName);
    }

    [Fact]
    public async Task ReplayedMessageAndSameStatusAreNoOpsWithoutEvents()
    {
        using var fixture = new CustomerRequestFixture(); var receipt = await fixture.Accept();
        await Update(fixture, Bytes(receipt.Code, "in_progress", "tech-msg-1"));
        await Update(fixture, Bytes(receipt.Code, "done", "tech-msg-2"));
        // The first message replayed with a fresh nonce returns the current state and writes nothing.
        Assert.Equal(new CustomerRequestStatusReceipt(receipt.Code, "done", "done", false), await Update(fixture, Bytes(receipt.Code, "in_progress", "tech-msg-1")));
        Assert.Equal(new CustomerRequestStatusReceipt(receipt.Code, "done", "done", false), await Update(fixture, Bytes(receipt.Code, "done", "tech-msg-3")));
        await using var check = fixture.Database();
        Assert.Equal("done", (await check.CustomerRequests.SingleAsync()).Status);
        Assert.Equal(2, await check.CustomerRequestEvents.CountAsync(x => x.EventType == "status_changed"));
        Assert.Equal(5, await check.CustomerRequestNonces.CountAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("other-group")]
    [InlineData("other-company")]
    public async Task OutOfScopeTicketIsNotFoundAndUnchanged(string scope)
    {
        using var fixture = new CustomerRequestFixture(); var receipt = await fixture.Accept();
        await using (var db = fixture.Database())
        {
            var ticket = await db.CustomerRequests.SingleAsync();
            if (scope == "other-group") ticket.SourceGroupExternalId = "removed-group";
            await db.SaveChangesAsync();
            if (scope == "other-company")
            {
                db.CustomerRequests.Add(new()
                {
                    TenantId = ticket.TenantId,
                    CompanyId = Guid.NewGuid(),
                    Id = Guid.NewGuid(),
                    Code = "YC-0900",
                    SourceGroupExternalId = "customer-group-1",
                    SourceGroupName = "Foreign",
                    CustomerName = "Foreign",
                    ReportedBy = "Lan",
                    Summary = "Foreign",
                    Status = "new",
                    IdempotencyKey = "foreign-1",
                    CreatedAt = ticket.CreatedAt,
                    UpdatedAt = ticket.UpdatedAt,
                    RowVersion = [1]
                });
                await db.SaveChangesAsync();
            }
        }
        var code = scope switch { "missing" => "YC-9999", "other-company" => "YC-0900", _ => receipt.Code };
        await Assert.ThrowsAsync<CustomerRequestNotFoundException>(() => Update(fixture, Bytes(code)));
        await using var check = fixture.Database();
        Assert.All(check.CustomerRequests, x => Assert.Equal("new", x.Status)); Assert.Single(check.CustomerRequestEvents); Assert.Single(check.CustomerRequestNonces);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("path")]
    [InlineData("body")]
    [InlineData("service")]
    [InlineData("stale")]
    [InlineData("inactive-company")]
    public async Task AuthenticationFailuresChangeNothing(string change)
    {
        using var fixture = new CustomerRequestFixture(); var receipt = await fixture.Accept();
        await using var db = fixture.Database();
        if (change == "inactive-company") { db.Companies.Single().IsActive = false; await db.SaveChangesAsync(); }
        var body = Bytes(receipt.Code);
        var signature = fixture.Sign(body, path: change == "path" ? CustomerRequestAuthenticator.Path : CustomerRequestStatusUpdater.Path,
            timestamp: DateTimeOffset.UtcNow.AddMinutes(change == "stale" ? -6 : 0).ToUnixTimeSeconds(), service: change == "service" ? Guid.NewGuid() : null);
        if (change == "signature") signature = signature with { SignatureHex = new string('0', 64) };
        if (change == "body") body = Bytes(receipt.Code, "rejected");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new CustomerRequestStatusUpdater(db, fixture.Authenticator(), TimeProvider.System).UpdateAsync(signature, body));
        await using var check = fixture.Database();
        Assert.Equal("new", check.CustomerRequests.Single().Status); Assert.Single(check.CustomerRequestEvents); Assert.Single(check.CustomerRequestNonces);
    }

    [Theory]
    [InlineData("{\"code\":\"YC-0001\",\"status\":\"approved\",\"actorName\":\"A\",\"actorExternalId\":\"u\",\"sourceMessageId\":\"m\"}")]
    [InlineData("{\"code\":\"YC-0001\",\"status\":\"done\",\"actorName\":\"A\",\"actorExternalId\":\"u\"}")]
    [InlineData("{\"code\":\"YC-0001\",\"status\":\"done\",\"actorName\":\"A\",\"actorExternalId\":\"u\",\"sourceMessageId\":\"m\",\"assigneeUserId\":\"x\"}")]
    [InlineData("{\"code\":\"YC-00000000000000000000001\",\"status\":\"done\",\"actorName\":\"A\",\"actorExternalId\":\"u\",\"sourceMessageId\":\"m\"}")]
    [InlineData("{\"code\":\"YC-0001\",\"status\":\"done\",\"actorName\":\" \",\"actorExternalId\":\"u\",\"sourceMessageId\":\"m\"}")]
    [InlineData("{\"code\":\"YC-0001\",\"status\":\"done\",\"actorName\":\"A\",\"actorExternalId\":\" u\",\"sourceMessageId\":\"m\"}")]
    public void InvalidStatusBodiesAreRejected(string json)
    {
        var error = Record.Exception(() => CustomerRequestJson.ReadStatus(Encoding.UTF8.GetBytes(json)));
        Assert.True(error is ArgumentException or JsonException);
    }
}
