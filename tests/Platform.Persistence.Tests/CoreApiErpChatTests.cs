using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class CoreApiDataSourceAuthorizationIntegrationTests
{
    [Fact]
    public async Task ErpChatApi_AcceptsOnlySignedOnceRequests_AndShowsAMessageOnlyToItsErpUser()
    {
        var variable = "AIOFFICE_ERPCHAT_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "synthetic-design-value");
        try
        {
            var (tenant, company, admin, source, account) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            var authority = AuthorizationContext.Create(tenant, company, admin);
            await using var factory = AuthenticatedFactory(new(authority, ["admin"]), "secretref://env/" + variable, useRealDirectory: true,
                additionalServices: services => services.AddSingleton(new ProtectedSecretEncryption(() => Convert.ToBase64String(new byte[32]))));
            ErpChatClientSecret created;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                db.Users.Add(new PlatformUserRecord { TenantId = tenant, Id = admin, IdentityProvider = "test", Subject = "admin", DisplayName = "Admin", IsActive = true });
                db.Companies.Add(new CompanyRecord { TenantId = tenant, Id = company, Code = "MH", Name = "Minh Huy", IsActive = true });
                db.CompanyMemberships.Add(new CompanyMembershipRecord { TenantId = tenant, CompanyId = company, UserId = admin, IsActive = true });
                db.RoleAssignments.Add(new() { TenantId = tenant, CompanyId = company, UserId = admin, RoleKey = "admin" });
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
                db.AiBillingAccounts.Add(new AiBillingAccountRecord { TenantId = tenant, CompanyId = company, Id = account, Name = "Mak" });
                db.AiBillingAccountSources.Add(new AiBillingAccountSourceRecord { TenantId = tenant, CompanyId = company, SourceId = source, AccountId = account });
                BindingFixture.Grant(db, tenant, company, "secretref://env/PILOT_ERP_CONNECTION");
                await db.SaveChangesAsync();
                created = await scope.ServiceProvider.GetRequiredService<ErpChatService>().CreateClientAsync(authority, new("Web ERP Mak", account));
            }
            using var http = factory.CreateClient();
            // The test host signs every request in as the admin; real web ERP calls carry no bearer token and skip that check.
            http.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, company.ToString());

            var body = JsonSerializer.SerializeToUtf8Bytes(new { requestId = "r-1", externalUserId = "nv001", question = "Doanh thu tháng này?" });
            var ask = Signed(HttpMethod.Post, "/api/erp-chat/v1/messages", body, created);
            var accepted = await http.SendAsync(ask.Request);
            Assert.True(accepted.StatusCode == HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync());
            Assert.True(accepted.Headers.CacheControl!.NoStore);
            var message = await accepted.Content.ReadFromJsonAsync<ErpChatAccepted>();
            Assert.Equal("pending", message!.Status);

            var replayed = await http.SendAsync(Signed(HttpMethod.Post, "/api/erp-chat/v1/messages", body, created, ask.Nonce).Request);
            Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
            var tampered = Signed(HttpMethod.Post, "/api/erp-chat/v1/messages", body, created).Request;
            tampered.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"requestId":"r-2","externalUserId":"nv001","question":"Khác"}"""));
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(tampered)).StatusCode);
            using var unsigned = new HttpRequestMessage(HttpMethod.Post, "/api/erp-chat/v1/messages") { Content = new ByteArrayContent(body) };
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(unsigned)).StatusCode);

            var mine = await http.SendAsync(Signed(HttpMethod.Get, $"/api/erp-chat/v1/messages/{message.MessageId}?externalUserId=nv001", [], created).Request);
            Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
            Assert.Equal("pending", (await mine.Content.ReadFromJsonAsync<ErpChatMessage>())!.Status);
            var theirs = await http.SendAsync(Signed(HttpMethod.Get, $"/api/erp-chat/v1/messages/{message.MessageId}?externalUserId=nv002", [], created).Request);
            Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);

            var clients = await http.GetStringAsync("/api/erp-chat/clients");
            Assert.Contains("Web ERP Mak", clients);
            Assert.DoesNotContain(created.SigningKey, clients);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    private static (HttpRequestMessage Request, Guid Nonce) Signed(HttpMethod method, string pathAndQuery, byte[] body, ErpChatClientSecret client,
        Guid? nonce = null)
    {
        var signature = new ErpChatSignature(client.Client.Id, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), nonce ?? Guid.NewGuid(), "");
        var hex = Convert.ToHexString(HMACSHA256.HashData(Convert.FromBase64String(client.SigningKey),
            ErpChatService.SigningBytes(signature, method.Method, pathAndQuery, body)));
        var request = new HttpRequestMessage(method, pathAndQuery);
        if (method == HttpMethod.Post) request.Content = new ByteArrayContent(body);
        request.Headers.Add("X-AIOffice-Client", signature.ClientId.ToString("D"));
        request.Headers.Add("X-AIOffice-Timestamp", signature.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-AIOffice-Nonce", signature.Nonce.ToString("D"));
        request.Headers.Add("X-AIOffice-Signature", hex.ToLowerInvariant());
        return (request, signature.Nonce);
    }
}
