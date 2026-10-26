using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MinhHuy.AIOffice.Core.Api.Authorization;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class CoreApiDataSourceAuthorizationIntegrationTests
{
    [Theory]
    [InlineData("admin", HttpStatusCode.OK)]
    [InlineData("viewer", HttpStatusCode.Forbidden)]
    [InlineData("ADMIN", HttpStatusCode.Forbidden)]
    public async Task CustomerErpApiUsesCompanyAdministrationAndNeverReturnsCredentials(string role, HttpStatusCode expected)
    {
        var variable = "AIOFFICE296_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "synthetic-design-value");
        try
        {
            var authority = AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            var verifier = new CustomerApiVerifier();
            await using var factory = AuthenticatedFactory(new(authority, [role]), "secretref://env/" + variable,
                additionalServices: services =>
                {
                    services.AddSingleton(new ProtectedSecretEncryption(() => Convert.ToBase64String(new byte[32])));
                    services.AddSingleton<ICustomerErpConnectionVerifier>(verifier);
                });
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, authority.CompanyId.ToString());
            var input = new { host = "sql.example.test", database = "Demo", userName = "reader", password = "synthetic-api-password" };
            var response = await client.PostAsJsonAsync("/api/data-sources/connection-test", input);
            Assert.Equal(expected, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.DoesNotContain(input.password, await response.Content.ReadAsStringAsync());
            if (expected != HttpStatusCode.OK) { Assert.Equal(0, verifier.Calls); return; }
            var save = new { input.host, input.database, input.userName, input.password, operationId = Guid.NewGuid(), logicalName = "Customer A" };
            var created = await client.PostAsJsonAsync("/api/data-sources/customer-erp", save);
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var source = await created.Content.ReadFromJsonAsync<CustomerErpSource>();
            Assert.NotNull(source); Assert.Equal("1", source.BindingVersion);
            var retry = await client.PostAsJsonAsync("/api/data-sources/customer-erp", save);
            Assert.Equal(source, await retry.Content.ReadFromJsonAsync<CustomerErpSource>());
            var rotated = await client.PutAsJsonAsync($"/api/data-sources/{source.Id}/credentials", new
            { input.host, input.database, input.userName, password = "synthetic-api-rotated", operationId = Guid.NewGuid(), logicalName = "Customer A", bindingVersion = "1" });
            Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
            Assert.Equal("2", (await rotated.Content.ReadFromJsonAsync<CustomerErpSource>())!.BindingVersion);
            var list = await client.GetAsync("/api/data-sources/customer-erp");
            Assert.True(list.Headers.CacheControl!.NoStore);
            var body = await list.Content.ReadAsStringAsync();
            Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secretref", body);
            Assert.DoesNotContain(input.host, body);
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            Assert.Single(db.ProtectedDataSourceSecrets); Assert.Equal(2, db.DataSourceRegistrationAudits.Count());
            Assert.Equal(3, verifier.Calls);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public async Task CustomerErpApiBoundsAndStrictlyParsesBeforeOutboundConnectionsAndLimitsAttempts()
    {
        var variable = "AIOFFICE296_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "synthetic-design-value");
        try
        {
            var authority = AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            var verifier = new CustomerApiVerifier();
            await using var factory = AuthenticatedFactory(new(authority, ["admin"]), "secretref://env/" + variable,
                additionalServices: services => services.AddSingleton<ICustomerErpConnectionVerifier>(verifier));
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add(AuthorizationHeaders.CompanyId, authority.CompanyId.ToString());
            const string json = "{\"host\":\"sql.example.test\",\"database\":\"Demo\",\"userName\":\"reader\",\"password\":\"synthetic-api-password\"}";
            foreach (var invalid in new[] { json.Replace("}", ",\"host\":\"other\"}"), json.Replace("}", ",\"connectionString\":\"forbidden\"}"),
                json.Replace("sql.example.test", "host;User ID=sa"), "[]" })
            {
                var response = await client.PostAsync("/api/data-sources/connection-test", new StringContent(invalid, Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
                Assert.DoesNotContain("synthetic-api-password", await response.Content.ReadAsStringAsync());
            }
            var large = await client.PostAsync("/api/data-sources/connection-test", new StringContent(new string('x', 16385), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
            Assert.Equal(0, verifier.Calls);
            for (var i = 0; i < 5; i++)
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/data-sources/connection-test", new StringContent(json, Encoding.UTF8, "application/json"))).StatusCode);
            var limited = await client.PostAsync("/api/data-sources/connection-test", new StringContent(json, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode); Assert.True(limited.Headers.CacheControl!.NoStore);
            Assert.Equal(5, verifier.Calls);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    private sealed class CustomerApiVerifier : ICustomerErpConnectionVerifier
    {
        public int Calls { get; private set; }
        public Task<CustomerErpConnectionResult> VerifyAsync(string connectionString, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new CustomerErpConnectionResult("ok", "16.0", 3)); }
    }
}
