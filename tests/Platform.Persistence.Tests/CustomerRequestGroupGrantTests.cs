using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class CustomerRequestGroupGrantTests
{
    private const string WebGroup = "wa:120363000000000001@g.us";

    private static async Task Select(CustomerRequestFixture fixture, string group, Guid? company = null)
    {
        await using var db = fixture.Database();
        db.BotCustomerGroups.Add(new() { TenantId = fixture.Authority.TenantId, CompanyId = company ?? fixture.Authority.CompanyId, Channel = "whatsapp",
            ExternalId = group, DisplayName = "Khách WhatsApp", CustomerName = "Công ty WA" });
        await db.SaveChangesAsync();
    }

    private static Task<CustomerRequestReceipt> AcceptFrom(CustomerRequestFixture fixture, string group) =>
        fixture.Accept(CustomerRequestContractTests.Sample() with { SourceGroupExternalId = group, IdempotencyKey = "grant-" + group });

    [Fact]
    public async Task GroupSelectedInBotChannelsFilesTicketsWhenServiceOptsIn()
    {
        using var fixture = new CustomerRequestFixture(grantBotChannelGroups: true); await Select(fixture, WebGroup);
        var receipt = await AcceptFrom(fixture, WebGroup);
        Assert.Equal("YC-0001", receipt.Code);
        Assert.Equal("YC-0002", (await fixture.Accept()).Code);
        await using var db = fixture.Database(); Assert.Equal(WebGroup, (await db.CustomerRequests.SingleAsync(x => x.Id == receipt.Id)).SourceGroupExternalId);
    }

    [Theory]
    [InlineData("not-opted-in")]
    [InlineData("not-selected")]
    [InlineData("other-company")]
    [InlineData("case-differs")]
    public async Task UnselectedOrForeignGroupsStayDenied(string scope)
    {
        using var fixture = new CustomerRequestFixture(grantBotChannelGroups: scope != "not-opted-in");
        if (scope is "not-opted-in") await Select(fixture, WebGroup);
        if (scope is "other-company") await Select(fixture, WebGroup, Guid.NewGuid());
        if (scope is "case-differs") await Select(fixture, WebGroup.ToUpperInvariant());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AcceptFrom(fixture, WebGroup));
        await using var check = fixture.Database(); Assert.Empty(check.CustomerRequests); Assert.Empty(check.CustomerRequestNonces);
    }

    [Fact]
    public async Task DigestAndStatusCommandsCoverSelectedGroupsUntilTheyAreRemoved()
    {
        using var fixture = new CustomerRequestFixture(grantBotChannelGroups: true); await Select(fixture, WebGroup);
        var receipt = await AcceptFrom(fixture, WebGroup);
        await using (var db = fixture.Database())
        {
            var digest = await new CustomerRequestDigestReader(db, fixture.Authenticator(), TimeProvider.System)
                .ReadAsync(fixture.Sign([], method: "GET", path: CustomerRequestDigestReader.Path), null);
            Assert.Equal(receipt.Code, Assert.Single(digest.Open).Code);
        }
        var body = CustomerRequestStatusTests.Bytes(receipt.Code);
        await using (var db = fixture.Database())
            Assert.Equal("done", (await new CustomerRequestStatusUpdater(db, fixture.Authenticator(), TimeProvider.System)
                .UpdateAsync(fixture.Sign(body, path: CustomerRequestStatusUpdater.Path), body)).Status);
        await using (var db = fixture.Database()) { db.BotCustomerGroups.RemoveRange(db.BotCustomerGroups); await db.SaveChangesAsync(); }
        var again = CustomerRequestStatusTests.Bytes(receipt.Code, "in_progress", "tech-msg-2");
        await using (var db = fixture.Database())
            await Assert.ThrowsAsync<CustomerRequestNotFoundException>(() => new CustomerRequestStatusUpdater(db, fixture.Authenticator(), TimeProvider.System)
                .UpdateAsync(fixture.Sign(again, path: CustomerRequestStatusUpdater.Path), again));
    }

    [Fact]
    public void OptedInServiceMayRelyOnWebSelectionAlone()
    {
        using var fixture = new CustomerRequestFixture();
        var binding = new CustomerRequestServiceBinding(fixture.ServiceId, fixture.Authority.TenantId, fixture.Authority.CompanyId, "secretref://env/" + fixture.Variable, []);
        Assert.Throws<InvalidOperationException>(() => new CustomerRequestServiceDirectory([binding]));
        _ = new CustomerRequestServiceDirectory([binding with { GrantBotChannelGroups = true }]);
    }
}
