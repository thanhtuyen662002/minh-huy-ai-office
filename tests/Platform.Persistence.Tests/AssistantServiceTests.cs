using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class AssistantServiceTests
{
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82];

    [Fact]
    public async Task PicturesAreStoredCheckedAndReadableOnlyByTheirUploader()
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var owner = MemberDirectoryFixture.Authority();
        MemberDirectoryFixture.Seed(db, owner);
        var other = AuthorizationContext.Create(owner.TenantId, owner.CompanyId, Guid.NewGuid());
        MemberDirectoryFixture.AddMember(db, owner, other.UserId, "Other", roles: ["admin"]);
        await db.SaveChangesAsync();
        var service = new AssistantService(db, new EfAuthorizationDirectory(db));

        var receipt = await service.UploadAsync(owner, "image/png", Png);
        Assert.Equal("image/png", receipt.ContentType);
        Assert.Equal(Png, (await service.ImageAsync(owner, receipt.Id)).Content);
        await Assert.ThrowsAsync<AssistantImageNotFoundException>(() => service.ImageAsync(other, receipt.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UploadAsync(owner, "image/jpeg", Png));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UploadAsync(owner, "image/png", "not a picture at all"u8.ToArray()));

        var forTask = await AssistantService.ForTaskAsync(db, owner.TenantId, owner.CompanyId, owner.UserId, [Guid.NewGuid(), receipt.Id]);
        Assert.Equal(Png, Assert.Single(forTask).Content);
        Assert.Empty(await AssistantService.ForTaskAsync(db, owner.TenantId, owner.CompanyId, other.UserId, [receipt.Id]));
    }

    [Fact]
    public async Task DeletedQuestionsLeaveTheAskersHistoryAndTakeTheirPicturesAlong()
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var owner = MemberDirectoryFixture.Authority();
        MemberDirectoryFixture.Seed(db, owner);
        var other = AuthorizationContext.Create(owner.TenantId, owner.CompanyId, Guid.NewGuid());
        MemberDirectoryFixture.AddMember(db, owner, other.UserId, "Other");
        await db.SaveChangesAsync();
        var service = new AssistantService(db, new EfAuthorizationDirectory(db));
        var picture = await service.UploadAsync(owner, "image/png", Png);
        var kept = await service.UploadAsync(owner, "image/png", Png);
        var withPicture = TaskHistoryFixture.Add(db, owner, request: TaskHistoryFixture.Request($"Mã lỗi trong ảnh? {AssistantImageTokens.Format(picture.Id)}"));
        var plain = TaskHistoryFixture.Add(db, owner, request: TaskHistoryFixture.Request());
        var foreign = TaskHistoryFixture.Add(db, other, request: TaskHistoryFixture.Request());
        await db.SaveChangesAsync();

        Assert.Equal(1, await service.HideTasksAsync(owner, [withPicture, foreign]));
        var history = new TaskHistoryService(db, new EfAuthorizationDirectory(db));
        Assert.Equal(plain, Assert.Single((await history.ListAsync(owner)).Items).TaskId);
        Assert.Null(await history.GetAsync(owner, withPicture));
        Assert.Single((await history.ListAsync(other)).Items);
        Assert.Equal(kept.Id, Assert.Single(db.AssistantImages).Id);
        Assert.Single(db.Tasks.Where(task => task.Id == withPicture)); // kept for audit
        Assert.Equal(1, await service.HideTasksAsync(owner, [withPicture])); // repeating is harmless
        Assert.Single(db.HiddenTasks);
        await Assert.ThrowsAsync<ArgumentException>(() => service.HideTasksAsync(owner, []));
    }

    [Theory]
    [InlineData("Lỗi gì? [img:6f1c0c8e-1d2a-4b3c-8d4e-5f6a7b8c9d0e] [img:6F1C0C8E-1D2A-4B3C-8D4E-5F6A7B8C9D0E]", "Lỗi gì?", 1)]
    [InlineData("[img:6f1c0c8e-1d2a-4b3c-8d4e-5f6a7b8c9d0e]", "", 1)]
    [InlineData("Không có ảnh [img:nope]", "Không có ảnh [img:nope]", 0)]
    public void TokensAreParsedFromTheQuestion(string question, string text, int count)
    {
        var parsed = AssistantImageTokens.Parse(question);
        Assert.Equal(text, parsed.Text);
        Assert.Equal(count, parsed.ImageIds.Count);
    }
}
