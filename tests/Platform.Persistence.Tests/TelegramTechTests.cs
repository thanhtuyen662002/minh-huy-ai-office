using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class TelegramTechTests
{
    private const long Telegram = 777001;

    private static byte[] Json(object value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    private static TelegramLinkPortal Portal(CustomerRequestFixture fixture, PlatformDbContext db, string? bot = "Minh_Huy_Ky_Thuat_Bot") =>
        new(db, new EfAuthorizationDirectory(db), TelegramOptions.Create(bot), TimeProvider.System);
    private static TelegramTechBridge Bridge(CustomerRequestFixture fixture, PlatformDbContext db) =>
        new(db, fixture.Authenticator(), new EfAuthorizationDirectory(db), TimeProvider.System);

    private static async Task<T> Call<T>(CustomerRequestFixture fixture, string path, object body,
        Func<TelegramTechBridge, CustomerRequestSignature, byte[], Task<T>> call)
    {
        await using var db = fixture.Database(); var bytes = Json(body);
        return await call(Bridge(fixture, db), fixture.Sign(bytes, path: path), bytes);
    }
    private static Task<TelegramLinkReceipt> Link(CustomerRequestFixture fixture, string code, long telegram = Telegram) =>
        Call(fixture, TelegramTechBridge.LinkPath, new { code, telegramUserId = telegram, telegramName = "Minh Tg" }, (b, s, x) => b.LinkAsync(s, x));
    private static Task<TelegramActionReceipt> Act(CustomerRequestFixture fixture, string code, string action, string message, long telegram = Telegram) =>
        Call(fixture, TelegramTechBridge.ActionPath, new { code, action, telegramUserId = telegram, telegramName = "Minh Tg", sourceMessageId = message },
            (b, s, x) => b.ActAsync(s, x));
    private static Task<TelegramTicketList> List(CustomerRequestFixture fixture, string scope, long telegram = Telegram) =>
        Call(fixture, TelegramTechBridge.TicketsPath, new { telegramUserId = telegram, scope }, (b, s, x) => b.ListAsync(s, x));

    private static async Task<string> NewCode(CustomerRequestFixture fixture, AuthorizationContext? who = null)
    {
        await using var db = fixture.Database();
        var authority = who ?? fixture.Authority;
        var code = await Portal(fixture, db).CreateCodeAsync(authority, authority.CompanyId);
        Assert.True(TelegramTechJson.IsCode(code.Code));
        Assert.Equal($"https://t.me/Minh_Huy_Ky_Thuat_Bot?start={code.Code}", code.Url);
        return code.Code;
    }

    private static async Task<AuthorizationContext> AddMember(CustomerRequestFixture fixture, string name)
    {
        var id = Guid.NewGuid();
        await using var db = fixture.Database(); MemberDirectoryFixture.AddMember(db, fixture.Authority, id, name); await db.SaveChangesAsync();
        return AuthorizationContext.Create(fixture.Authority.TenantId, fixture.Authority.CompanyId, id);
    }

    [Fact]
    public async Task MemberLinksWithOneTimeCodeAndPortalShowsIt()
    {
        using var fixture = new CustomerRequestFixture();
        var code = await NewCode(fixture);
        Assert.Equal("Owner", (await Link(fixture, code)).DisplayName);
        await Assert.ThrowsAsync<CustomerRequestNotFoundException>(() => Link(fixture, code, 99));
        await using var db = fixture.Database();
        var view = await Portal(fixture, db).GetAsync(fixture.Authority, fixture.Authority.CompanyId);
        Assert.True(view.Linked); Assert.Equal("Minh Tg", view.TelegramName); Assert.Equal("Minh_Huy_Ky_Thuat_Bot", view.BotUsername);
        Assert.Empty(db.TelegramLinkCodes);
        await Portal(fixture, db).UnlinkAsync(fixture.Authority, fixture.Authority.CompanyId);
        Assert.False((await Portal(fixture, db).GetAsync(fixture.Authority, fixture.Authority.CompanyId)).Linked);
    }

    [Fact]
    public async Task RelinkingMovesTheTelegramAccountAndNewCodeReplacesOld()
    {
        using var fixture = new CustomerRequestFixture();
        var first = await NewCode(fixture); var second = await NewCode(fixture);
        await Assert.ThrowsAsync<CustomerRequestNotFoundException>(() => Link(fixture, first));
        await Link(fixture, second);
        var other = await AddMember(fixture, "Lan");
        await Link(fixture, await NewCode(fixture, other));
        await using var db = fixture.Database();
        var link = await db.TelegramAccountLinks.SingleAsync();
        Assert.Equal(other.UserId, link.UserId); Assert.Equal(Telegram, link.TelegramUserId);
    }

    [Fact]
    public async Task ExpiredOrForeignCodesDoNotLink()
    {
        using var fixture = new CustomerRequestFixture();
        var code = await NewCode(fixture);
        await using (var db = fixture.Database()) { (await db.TelegramLinkCodes.SingleAsync()).ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); await db.SaveChangesAsync(); }
        await Assert.ThrowsAsync<CustomerRequestNotFoundException>(() => Link(fixture, code));
        var inactive = await AddMember(fixture, "Gone");
        var gone = await NewCode(fixture, inactive);
        await using (var db = fixture.Database()) { (await db.CompanyMemberships.SingleAsync(x => x.UserId == inactive.UserId)).IsActive = false; await db.SaveChangesAsync(); }
        await Assert.ThrowsAsync<CustomerRequestNotFoundException>(() => Link(fixture, gone));
        await using var check = fixture.Database(); Assert.Empty(check.TelegramAccountLinks);
    }

    [Fact]
    public async Task ClaimAssignsAndStartsThenOthersSeeItTaken()
    {
        using var fixture = new CustomerRequestFixture(); var ticket = await fixture.Accept();
        Assert.Equal("not_linked", (await Act(fixture, ticket.Code, "claim", "cb-0")).Outcome);
        await Link(fixture, await NewCode(fixture));
        Assert.Equal(1, (await List(fixture, "open")).Total);
        var claimed = await Act(fixture, ticket.Code, "claim", "cb-1");
        Assert.Equal(new TelegramActionReceipt("ok", ticket.Code, "in_progress", "Owner"), claimed);
        Assert.Equal(0, (await List(fixture, "open")).Total);
        var mine = await List(fixture, "mine");
        Assert.True(mine.Linked); Assert.Equal("Owner", mine.DisplayName); Assert.Equal(ticket.Code, Assert.Single(mine.Items).Code);

        var other = await AddMember(fixture, "Lan");
        await Link(fixture, await NewCode(fixture, other), 888);
        Assert.Equal(new TelegramActionReceipt("taken", ticket.Code, "in_progress", "Owner"), await Act(fixture, ticket.Code, "claim", "cb-2", 888));
        Assert.Equal("taken", (await Act(fixture, ticket.Code, "done", "cb-3", 888)).Outcome);

        Assert.Equal(new TelegramActionReceipt("ok", ticket.Code, "done", "Owner"), await Act(fixture, ticket.Code, "done", "cb-4"));
        Assert.Equal("unchanged", (await Act(fixture, ticket.Code, "done", "cb-4")).Outcome);
        Assert.Empty((await List(fixture, "mine")).Items);

        await using var db = fixture.Database();
        var events = await db.CustomerRequestEvents.Where(x => x.ActorType == "user").OrderBy(x => x.Timestamp).ToArrayAsync();
        Assert.Equal(3, events.Length);
        Assert.All(events, x => { Assert.Equal(fixture.Authority.UserId, x.ActorId); Assert.Equal("Owner", x.ActorName); Assert.Equal("telegram:777001", x.ActorExternalId); });
        Assert.Contains(events, x => x.EventType == "assignee_changed" && x.SourceMessageId == "cb-1");
        Assert.Contains(events, x => x.EventType == "status_changed" && x.SourceMessageId == "cb-1:s" && x.NewValue == "in_progress");
        Assert.Contains(events, x => x.EventType == "status_changed" && x.SourceMessageId == "cb-4" && x.NewValue == "done");
    }

    [Fact]
    public async Task StatusButtonOnUnclaimedTicketClaimsItAndReleaseReturnsIt()
    {
        using var fixture = new CustomerRequestFixture(); var ticket = await fixture.Accept();
        await Link(fixture, await NewCode(fixture));
        Assert.Equal(new TelegramActionReceipt("ok", ticket.Code, "needs_clarification", "Owner"), await Act(fixture, ticket.Code, "needs_clarification", "cb-1"));
        Assert.Equal(new TelegramActionReceipt("ok", ticket.Code, "in_progress", "Owner"), await Act(fixture, ticket.Code, "in_progress", "cb-2"));
        Assert.Equal(new TelegramActionReceipt("ok", ticket.Code, "new", null), await Act(fixture, ticket.Code, "release", "cb-3"));
        Assert.Equal("unchanged", (await Act(fixture, ticket.Code, "release", "cb-4")).Outcome);
        await using var db = fixture.Database();
        var stored = await db.CustomerRequests.SingleAsync(); Assert.Null(stored.AssigneeUserId); Assert.Equal("new", stored.Status);
    }

    [Fact]
    public async Task TicketDetailNeedsLinkAndStaysInServiceScope()
    {
        using var fixture = new CustomerRequestFixture(); var ticket = await fixture.Accept();
        Task<TelegramTicketView> Get(string code) =>
            Call(fixture, TelegramTechBridge.TicketPath, new { telegramUserId = Telegram, code }, (b, s, x) => b.GetAsync(s, x));
        Assert.False((await Get(ticket.Code)).Linked);
        await Link(fixture, await NewCode(fixture));
        var view = await Get(ticket.Code);
        Assert.True(view.Linked); Assert.Equal(ticket.Code, view.Ticket!.Code); Assert.NotEmpty(view.Ticket.Items);
        await using (var db = fixture.Database()) { (await db.CustomerRequests.SingleAsync()).SourceGroupExternalId = "removed-group"; await db.SaveChangesAsync(); }
        await Assert.ThrowsAsync<CustomerRequestNotFoundException>(() => Get(ticket.Code));
        await Assert.ThrowsAsync<CustomerRequestNotFoundException>(() => Act(fixture, ticket.Code, "claim", "cb-9"));
    }

    [Fact]
    public async Task WrongPathOrSignatureIsDenied()
    {
        using var fixture = new CustomerRequestFixture(); var ticket = await fixture.Accept();
        await using var db = fixture.Database();
        var bytes = Json(new { telegramUserId = Telegram, scope = "open" });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Bridge(fixture, db).ListAsync(fixture.Sign(bytes, path: TelegramTechBridge.ActionPath), bytes));
        var signature = fixture.Sign(bytes, path: TelegramTechBridge.TicketsPath) with { SignatureHex = new string('0', 64) };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Bridge(fixture, db).ListAsync(signature, bytes));
    }

    [Fact]
    public async Task PortalRequiresActiveMembership()
    {
        using var fixture = new CustomerRequestFixture();
        await using var db = fixture.Database();
        var stranger = AuthorizationContext.Create(fixture.Authority.TenantId, fixture.Authority.CompanyId, Guid.NewGuid());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Portal(fixture, db).CreateCodeAsync(stranger, stranger.CompanyId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Portal(fixture, db).GetAsync(fixture.Authority, Guid.NewGuid()));
    }

    [Theory]
    [InlineData("{\"code\":\"YC-0001\",\"action\":\"approve\",\"telegramUserId\":1,\"telegramName\":\"A\",\"sourceMessageId\":\"m\"}")]
    [InlineData("{\"code\":\"YC-0001\",\"action\":\"done\",\"telegramUserId\":0,\"telegramName\":\"A\",\"sourceMessageId\":\"m\"}")]
    [InlineData("{\"code\":\"YC-0001\",\"action\":\"done\",\"telegramUserId\":1,\"telegramName\":\"A\"}")]
    [InlineData("{\"code\":\"YC-0001\",\"action\":\"done\",\"telegramUserId\":1,\"telegramName\":\"A\",\"sourceMessageId\":\"m\",\"assignee\":\"x\"}")]
    [InlineData("{\"code\":\"YC-0001\",\"code\":\"YC-0002\",\"action\":\"done\",\"telegramUserId\":1,\"telegramName\":\"A\",\"sourceMessageId\":\"m\"}")]
    public void InvalidActionBodiesAreRejected(string json)
    {
        var error = Record.Exception(() => TelegramTechJson.ReadAction(Encoding.UTF8.GetBytes(json)));
        Assert.True(error is ArgumentException or JsonException);
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("NotABotName")]
    public void BotUsernameIsValidated(string name) => Assert.Throws<InvalidOperationException>(() => TelegramOptions.Create(name));
}
