using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

internal sealed class BotChannelFixture : IDisposable
{
    internal readonly CustomerRequestFixture State = new();
    internal readonly BotClock Clock = new();
    internal AuthorizationContext Authority => State.Authority;
    internal BotSecretProtector Protector => new(State.Secrets, "secretref://env/" + State.Variable);
    internal PlatformDbContext Database() => State.Database();
    internal BotChannelService Service(PlatformDbContext db) => new(db, new EfAuthorizationDirectory(db), Protector, Clock);
    internal BotChannelIngress Ingress(PlatformDbContext db) => new(db, new(State.Directory, State.Secrets, Clock), Service(db), Clock);
    internal BotChannelFixture()
    {
        using var db = Database(); db.RoleAssignments.Add(new() { TenantId = Authority.TenantId, CompanyId = Authority.CompanyId, UserId = Authority.UserId, RoleKey = "admin" }); db.SaveChanges();
    }
    internal static byte[] Bytes<T>(T input) => JsonSerializer.SerializeToUtf8Bytes(input, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    internal CustomerRequestSignature Sign(byte[] body, string method, string path, Guid? nonce = null) => State.Sign(body, method: method, path: path, timestamp: Clock.Now.ToUnixTimeSeconds(), nonce: nonce);
    internal async Task Heartbeat(BotHeartbeat? input = null)
    {
        input ??= new("zalo", "logged_in", [new("tech", "Kỹ thuật"), new("customer-group-1", "Khách hàng A")]);
        var bytes = Bytes(input); await using var db = Database(); await Ingress(db).HeartbeatAsync(Sign(bytes, "POST", "/internal/bot-status"), bytes);
    }
    internal async Task<BotConfigView> Pull(string channel = "zalo")
    { await using var db = Database(); return await Ingress(db).PullAsync(Sign([], "GET", "/internal/bot-config?channel=" + channel), channel); }
    internal BotChannelUpdate Update(string version = "1") => new(version, true, true, ["17:30", "08:00", "08:00"], "tech", [new("customer-group-1", "Khách hàng A", "Khách A")]);
    public void Dispose() => State.Dispose();
}
internal sealed class BotClock : TimeProvider
{
    internal DateTimeOffset Now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class BotChannelServiceTests
{
    [Fact]
    public async Task EffectiveChangesBumpVersionNoOpDoesNotAndStaleWriteIsRejected()
    {
        using var f = new BotChannelFixture(); await f.Heartbeat(); await using var db = f.Database(); var service = f.Service(db);
        var initial = await service.GetAsync(f.Authority); Assert.False(initial.Channels[0].Enabled); Assert.True(initial.Channels[0].DryRun);
        var result = await service.UpdateAsync(f.Authority, "zalo", f.Update()); Assert.Equal("2", result.ConfigVersion); Assert.Equal(new[] { "08:00", "17:30" }, result.SummarySchedule);
        var count = db.BotChannelAudits.Count(); var noOp = await service.UpdateAsync(f.Authority, "zalo", f.Update("2"));
        Assert.Equal("2", noOp.ConfigVersion); Assert.Equal(count, db.BotChannelAudits.Count());
        await Assert.ThrowsAsync<BotChannelConflictException>(() => service.UpdateAsync(f.Authority, "zalo", f.Update()));
        Assert.Equal("2", (await f.Pull()).Config.ConfigVersion);
        Assert.All(await service.AuditAsync(f.Authority), row => Assert.Equal(f.Authority.UserId, row.ActorUserId));
    }

    [Fact]
    public async Task SharedAiChangesBumpBothChannelsKeyIsEncryptedWriteOnlyAndOmittedKeyKeepsIt()
    {
        using var f = new BotChannelFixture(); await f.Heartbeat(); await using var db = f.Database(); var service = f.Service(db);
        const string key = "PRIVATE-TEST-KEY";
        var ai = await service.UpdateAiAsync(f.Authority, new("0", "https://ai.example.test/v1/", "model-a", key));
        Assert.True(ai.ApiKeyConfigured); Assert.Equal("https://ai.example.test/v1", ai.BaseUrl);
        var stored = db.BotAiSettings.Single(); Assert.DoesNotContain(key, Encoding.UTF8.GetString(stored.ProtectedApiKey!));
        Assert.Equal(key, await f.Protector.UnprotectAsync(f.Authority.TenantId, f.Authority.CompanyId, stored.ProtectedApiKey!));
        var one = await f.Pull(); var other = await f.Pull("whatsapp"); Assert.Equal(key, one.Ai.ApiKey); Assert.Equal(key, other.Ai.ApiKey);
        var publicJson = JsonSerializer.Serialize(await service.GetAsync(f.Authority)); var auditJson = JsonSerializer.Serialize(await service.AuditAsync(f.Authority));
        Assert.DoesNotContain(key, publicJson); Assert.DoesNotContain(key, auditJson); Assert.Contains("[redacted]", auditJson);
        ai = await service.UpdateAiAsync(f.Authority, new(ai.Version, ai.BaseUrl, "model-b"));
        Assert.True(ai.ApiKeyConfigured); Assert.Equal(key, (await f.Pull()).Ai.ApiKey);
        Assert.True(long.Parse((await f.Pull()).Config.ConfigVersion) > long.Parse(one.Config.ConfigVersion));
        Assert.True(long.Parse((await f.Pull("whatsapp")).Config.ConfigVersion) > long.Parse(other.Config.ConfigVersion));
        var current = (await f.Pull()).Config.ConfigVersion;
        var noOp = await service.UpdateAiAsync(f.Authority, new(ai.Version, ai.BaseUrl, "model-b", key)); Assert.Equal(ai, noOp); Assert.Equal(current, (await f.Pull()).Config.ConfigVersion);
        ai = await service.UpdateAiAsync(f.Authority, new(ai.Version, ai.BaseUrl, ai.Model, ClearApiKey: true));
        Assert.False(ai.ApiKeyConfigured); Assert.Null((await f.Pull()).Ai.ApiKey);
        await Assert.ThrowsAsync<BotChannelConflictException>(() => service.UpdateAiAsync(f.Authority, new("1", ai.BaseUrl, "model-c")));
    }

    [Fact]
    public async Task EncryptionIsRandomizedAuthenticatedAndBoundToTenantCompanyAndKey()
    {
        using var f = new BotChannelFixture(); var a = await f.Protector.ProtectAsync(f.Authority.TenantId, f.Authority.CompanyId, "PRIVATE");
        var b = await f.Protector.ProtectAsync(f.Authority.TenantId, f.Authority.CompanyId, "PRIVATE"); Assert.NotEqual(a, b);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Protector.UnprotectAsync(Guid.NewGuid(), f.Authority.CompanyId, a));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Protector.UnprotectAsync(f.Authority.TenantId, Guid.NewGuid(), a));
        a[^1] ^= 1; await Assert.ThrowsAsync<InvalidOperationException>(() => f.Protector.UnprotectAsync(f.Authority.TenantId, f.Authority.CompanyId, a));
        Environment.SetEnvironmentVariable(f.State.Variable, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Protector.UnprotectAsync(f.Authority.TenantId, f.Authority.CompanyId, b));
    }

    [Theory]
    [InlineData("tech")]
    [InlineData("customer")]
    [InlineData("name")]
    [InlineData("same-group")]
    [InlineData("duplicate")]
    [InlineData("schedule")]
    [InlineData("enabled-empty")]
    public async Task GroupSelectionsMustBeReportedDistinctAndSeparatedFromTechGroup(string change)
    {
        using var f = new BotChannelFixture(); await f.Heartbeat(); await using var db = f.Database(); var update = f.Update();
        update = change switch
        {
            "tech" => update with { TechGroupExternalId = "unreported" },
            "customer" => update with { CustomerGroups = [new("unreported", "Unknown", "Customer")] },
            "name" => update with { CustomerGroups = [new("customer-group-1", "Forged name", "Customer")] },
            "same-group" => update with { TechGroupExternalId = "customer-group-1" },
            "duplicate" => update with { CustomerGroups = [update.CustomerGroups[0], update.CustomerGroups[0]] },
            "schedule" => update with { SummarySchedule = ["25:00"] },
            _ => update with { TechGroupExternalId = null, CustomerGroups = [] }
        };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => f.Service(db).UpdateAsync(f.Authority, "zalo", update));
        Assert.Empty(db.BotChannelAudits); Assert.Equal(1, db.BotChannels.Single().ConfigVersion);
    }

    [Fact]
    public async Task HeartbeatSanitizesUntrustedFieldsAndQrExpiresAndClearsOnNextStatus()
    {
        using var f = new BotChannelFixture();
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGD4DwABBAEAX+XDSwAAAABJRU5ErkJggg==";
        await f.Heartbeat(new("zalo", "awaiting_qr", [new("g", "Khách\0\r\n\u202e A")], LastError: "Bearer PRIVATE-KEY https://bad/?api_key=SECRET", LoginQrContentType: "image/png", LoginQrBase64: png));
        await using var db = f.Database(); var service = f.Service(db); var status = (await service.GetAsync(f.Authority)).Channels[0].Status!;
        Assert.Equal("Khách A", status.Groups.Single().DisplayName); Assert.DoesNotContain("PRIVATE", status.LastError); Assert.DoesNotContain("SECRET", status.LastError);
        Assert.NotNull(await service.QrAsync(f.Authority, "zalo")); Assert.DoesNotContain(png, JsonSerializer.Serialize(await service.GetAsync(f.Authority)));
        f.Clock.Now = f.Clock.Now.AddMinutes(2); Assert.Null(await service.QrAsync(f.Authority, "zalo"));
        await f.Heartbeat(new("zalo", "logged_in", [])); Assert.Null(await service.QrAsync(f.Authority, "zalo"));
    }

    [Theory]
    [InlineData("groups")]
    [InlineData("duplicates")]
    [InlineData("id")]
    [InlineData("state")]
    [InlineData("qr-type")]
    [InlineData("qr-base64")]
    [InlineData("qr-content")]
    [InlineData("qr-size")]
    [InlineData("qr-crc")]
    [InlineData("future-summary")]
    public async Task InvalidHeartbeatsHaveNoEffects(string change)
    {
        using var f = new BotChannelFixture(); var input = new BotHeartbeat("zalo", "logged_in", []);
        input = change switch
        {
            "groups" => input with { Groups = Enumerable.Range(0, 501).Select(i => new BotGroup(i.ToString(), "Group")).ToArray() },
            "duplicates" => input with { Groups = [new("g", "A"), new("g", "B")] },
            "id" => input with { Groups = [new("g\0", "A")] },
            "state" => input with { LoginState = "<script>" },
            "qr-type" => input with { LoginState = "awaiting_qr", LoginQrContentType = "image/svg+xml", LoginQrBase64 = Convert.ToBase64String("<svg/>"u8) },
            "qr-base64" => input with { LoginState = "awaiting_qr", LoginQrContentType = "image/png", LoginQrBase64 = "bad" },
            "qr-content" => input with { LoginState = "awaiting_qr", LoginQrContentType = "image/png", LoginQrBase64 = Convert.ToBase64String("<script>bad</script>"u8) },
            "qr-size" => input with { LoginState = "awaiting_qr", LoginQrContentType = "image/png", LoginQrBase64 = new string('A', 87385) },
            "qr-crc" => input with { LoginState = "awaiting_qr", LoginQrContentType = "image/png", LoginQrBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAAAAAAAAAABJRU5ErkJggg==" },
            _ => input with { LastSummaryAt = f.Clock.Now.AddHours(1) }
        };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => f.Heartbeat(input)); await using var db = f.Database(); Assert.Empty(db.BotStatuses); Assert.Empty(db.CustomerRequestNonces);
    }

    [Fact]
    public async Task CommandsAreDeduplicatedExpireAndAcknowledgementIsIdempotent()
    {
        using var f = new BotChannelFixture(); await using var db = f.Database(); var service = f.Service(db);
        var command = await service.EnqueueAsync(f.Authority, "zalo", "summarize-now");
        Assert.Equal(command, await service.EnqueueAsync(f.Authority, "zalo", "summarize-now")); Assert.Single((await f.Pull()).Commands);
        var body = BotChannelFixture.Bytes(new BotCommandAck("zalo"));
        for (var i = 0; i < 2; i++) { await using var ack = f.Database(); await f.Ingress(ack).AckAsync(f.Sign(body, "POST", $"/internal/bot-commands/{command.Id:D}/ack"), command.Id, body); }
        Assert.Empty((await f.Pull()).Commands); Assert.NotNull((await db.BotCommands.AsNoTracking().SingleAsync()).AcknowledgedAt);
        var relogin = await service.EnqueueAsync(f.Authority, "zalo", "relogin"); f.Clock.Now = f.Clock.Now.AddMinutes(10);
        Assert.Empty((await f.Pull()).Commands); Assert.NotEqual(relogin.Id, (await service.EnqueueAsync(f.Authority, "zalo", "relogin")).Id);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("member")]
    [InlineData("company")]
    [InlineData("user")]
    public async Task EveryAdminOperationRechecksFreshAuthority(string revoke)
    {
        using var f = new BotChannelFixture(); await f.Heartbeat(); await using var db = f.Database(); var service = f.Service(db);
        if (revoke == "role") db.RoleAssignments.RemoveRange(db.RoleAssignments);
        if (revoke == "member") db.CompanyMemberships.Single().IsActive = false;
        if (revoke == "company") db.Companies.Single().IsActive = false;
        if (revoke == "user") db.Users.Single().IsActive = false;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(f.Authority));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateAsync(f.Authority, "zalo", f.Update()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateAiAsync(f.Authority, new("0", "https://ai.test", "model", "SECRET")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.EnqueueAsync(f.Authority, "zalo", "relogin"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.QrAsync(f.Authority, "zalo"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AuditAsync(f.Authority));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ProbeSettingsAsync(f.Authority));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdminsInOtherScopesCannotReadOrChangeThisCompany(bool sameCompanyOtherTenant)
    {
        using var f = new BotChannelFixture(); await f.Heartbeat(); await using var db = f.Database(); var service = f.Service(db);
        var foreign = AuthorizationContext.Create(sameCompanyOtherTenant ? Guid.NewGuid() : f.Authority.TenantId, sameCompanyOtherTenant ? f.Authority.CompanyId : Guid.NewGuid(), Guid.NewGuid());
        MemberDirectoryFixture.Seed(db, foreign); await db.SaveChangesAsync();
        Assert.All((await service.GetAsync(foreign)).Channels, c => { Assert.Null(c.Status); Assert.Empty(c.CustomerGroups); });
        Assert.Empty(await service.AuditAsync(foreign)); Assert.Null(await service.QrAsync(foreign, "zalo"));
        await Assert.ThrowsAsync<BotChannelConflictException>(() => service.UpdateAsync(foreign, "zalo", f.Update()));
        var original = await f.Pull(); Assert.Equal("1", original.Config.ConfigVersion);
    }

    [Theory]
    [InlineData("file:///secret")]
    [InlineData("ftp://ai.test")]
    [InlineData("https://user:password@ai.test")]
    [InlineData("https://ai.test?api_key=PRIVATE")]
    [InlineData("https://ai.test#PRIVATE")]
    public void AiUrlRejectsNonHttpAndEmbeddedCredentials(string url) => Assert.Throws<ArgumentException>(() => BotChannelJson.BaseUrl(url));

    [Fact]
    public void MigrationAndModelHaveTenantForeignKeysConcurrencyTokensAndAppendOnlyAudit()
    {
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer("Server=127.0.0.1,1;Database=DesignOnly;User Id=unused;Password=unused").Options);
        foreach (var type in new[] { typeof(BotChannelRecord), typeof(BotAiSettingsRecord), typeof(BotStatusRecord) })
        { var entity = db.Model.FindEntityType(type)!; Assert.Contains(entity.GetForeignKeys(), fk => fk.Properties.Any(p => p.Name == "TenantId")); Assert.True(entity.FindProperty("RowVersion")!.IsConcurrencyToken); }
        Assert.False(db.Database.HasPendingModelChanges());
        var sql = db.GetService<IMigrator>().GenerateScript("20261026090633_AddCustomerRequestBridge", "20261026111913_AddBotChannels");
        Assert.Contains("CREATE TABLE [aioffice].[BotChannels]", sql); Assert.Contains("DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[BotChannelAudits]", sql);
    }
}
