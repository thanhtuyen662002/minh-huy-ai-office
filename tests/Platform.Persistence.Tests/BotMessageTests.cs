using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class BotMessageTests
{
    [Fact]
    public async Task OnlySelectedGroupsAreStoredDuplicatesAreIgnoredAndOldMessagesArePruned()
    {
        using var f = await Selected();
        var now = f.Clock.Now;
        var first = await Send(f, new("zalo", [
            new("customer-group-1", "m1", "Anh Minh", now.AddMinutes(-2), "Máy in lỗi rồi"),
            new("customer-group-1", "m2", "Anh Minh", now.AddMinutes(-1), null, true),
            new("other-group", "m3", "Người lạ", now.AddMinutes(-1), "Không được lưu"),
            new("customer-group-1", "m4", "Anh Minh", now.AddDays(-91), "Quá cũ")]));
        Assert.Equal((2, 2), (first.Accepted, first.Ignored));
        var again = await Send(f, new("zalo", [new("customer-group-1", "m1", "Anh Minh", now.AddMinutes(-2), "Máy in lỗi rồi")]));
        Assert.Equal((0, 1), (again.Accepted, again.Ignored));
        await using (var db = f.Database()) Assert.Equal(["m1", "m2"], db.BotMessages.Select(x => x.MessageId).OrderBy(x => x).ToArray());

        f.Clock.Now = now.AddDays(91);
        await Send(f, new("zalo", [new("customer-group-1", "m5", "Chị Lan", f.Clock.Now, "Tin mới")]));
        await using (var db = f.Database()) Assert.Equal("m5", Assert.Single(db.BotMessages).MessageId);
    }

    [Fact]
    public async Task MembersReadSelectedGroupsAndPagesNewestFirst()
    {
        using var f = await Selected();
        var start = f.Clock.Now.AddHours(-1);
        await Send(f, new("zalo", Enumerable.Range(1, 5).Select(i => new BotMessageInput("customer-group-1", $"m{i}", "Anh Minh", start.AddMinutes(i), $"Tin {i}")).ToArray()));
        await using var db = f.Database(); var service = f.Service(db);

        var group = Assert.Single(await service.MessageGroupsAsync(f.Authority));
        Assert.Equal(("customer-group-1", "Khách A", 5, start.AddMinutes(5)), (group.ExternalId, group.CustomerName, group.MessageCount, group.LastMessageAt!.Value));
        var page = await service.MessagesAsync(f.Authority, "zalo", "customer-group-1", null, 2);
        Assert.Equal(["m5", "m4"], page.Items.Select(x => x.MessageId).ToArray()); Assert.True(page.HasMore);
        var last = await service.MessagesAsync(f.Authority, "zalo", "customer-group-1", page.Items[^1].SentAt, 10);
        Assert.Equal(["m3", "m2", "m1"], last.Items.Select(x => x.MessageId).ToArray()); Assert.False(last.HasMore);

        await Assert.ThrowsAsync<BotChannelNotFoundException>(() => service.MessagesAsync(f.Authority, "zalo", "other-group", null, 10));
        await Assert.ThrowsAsync<ArgumentException>(() => service.MessagesAsync(f.Authority, "zalo", "customer-group-1", null, 201));
        foreach (var membership in db.CompanyMemberships) membership.IsActive = false;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.MessageGroupsAsync(f.Authority));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.MessagesAsync(f.Authority, "zalo", "customer-group-1", null, 10));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("offset")]
    [InlineData("future")]
    [InlineData("duplicate")]
    [InlineData("long")]
    [InlineData("channel")]
    [InlineData("none")]
    public void BatchesAreStrict(string attack)
    {
        var now = DateTimeOffset.UtcNow;
        BotMessageInput Message(string id = "m1") => new("customer-group-1", id, "Anh Minh", now, "Xin chào");
        var batch = attack switch
        {
            "empty" => new BotMessageBatch("zalo", [Message() with { Text = "" }]),
            "offset" => new("zalo", [Message() with { SentAt = now.ToOffset(TimeSpan.FromHours(7)) }]),
            "future" => new("zalo", [Message() with { SentAt = now.AddMinutes(6) }]),
            "duplicate" => new("zalo", [Message(), Message()]),
            "long" => new("zalo", [Message() with { Text = new string('a', BotChannelJson.MaximumMessageText + 1) }]),
            "channel" => new("telegram", [Message()]),
            _ => new("zalo", [])
        };
        Assert.Throws<ArgumentException>(() => BotChannelJson.Messages(batch, now));
        Assert.Equal("", BotChannelJson.Messages(new("zalo", [Message() with { Text = null, HasImage = true }]), now).Messages[0].Text);
    }

    private static async Task<BotChannelFixture> Selected()
    {
        var f = new BotChannelFixture(); await f.Heartbeat();
        await using var db = f.Database(); await f.Service(db).UpdateAsync(f.Authority, "zalo", f.Update());
        return f;
    }

    private static async Task<BotMessageReceipt> Send(BotChannelFixture f, BotMessageBatch batch)
    {
        var bytes = BotChannelFixture.Bytes(batch); await using var db = f.Database();
        return await f.Ingress(db).MessagesAsync(f.Sign(bytes, "POST", "/internal/bot-messages"), bytes);
    }
}
