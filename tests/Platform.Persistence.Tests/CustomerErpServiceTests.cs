using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class CustomerErpServiceTests
{
    [Theory]
    [InlineData("write")]
    [InlineData("kind")]
    [InlineData("rebind")]
    [InlineData("clone")]
    public async Task LegacyRegistryCannotWeakenOrReplaceProtectedCustomerConnections(string mode)
    {
        await using var fixture = await Fixture.Create();
        var source = await fixture.Service.SaveAsync(fixture.Authority, Request());
        BindingFixture.Grant(fixture.Db, fixture.Authority, "secretref://env/OTHER_APPROVED_FIXTURE");
        await fixture.Db.SaveChangesAsync();
        var reference = Assert.Single(fixture.Db.ProtectedDataSourceSecrets).Reference;
        var registry = new DataSourceRegistryService(fixture.Db, new EfAuthorizationDirectory(fixture.Db));
        var request = new DataSourceRegistryWriteRequest(mode == "clone" ? "Clone" : source.LogicalName,
            mode == "kind" ? "other" : "sql-server", "Production", "ERP", mode == "rebind"
                ? "secretref://env/OTHER_APPROVED_FIXTURE" : mode == "clone" ? reference : null,
            true, mode == "write", 2);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            if (mode == "clone") await registry.CreateAsync(fixture.Authority, request);
            else await registry.UpdateAsync(fixture.Authority, source.Id, request);
        });
        var retained = Assert.Single(fixture.Db.DataSources);
        Assert.True(retained.AllowRead); Assert.False(retained.AllowWrite); Assert.Equal(reference, retained.ConnectionSecretReference);
    }

    [Fact]
    public async Task CreateIsEncryptedScopedReadOnlyAuditedAndRetrySafe()
    {
        await using var fixture = await Fixture.Create();
        var request = Request();
        var saved = await fixture.Service.SaveAsync(fixture.Authority, request);
        Assert.Equal(saved, await fixture.Service.SaveAsync(fixture.Authority, request));
        Assert.Equal(1, fixture.Verifier.Calls);
        var row = Assert.Single(fixture.Db.ProtectedDataSourceSecrets);
        var source = Assert.Single(fixture.Db.DataSources);
        Assert.True(source.AllowRead); Assert.False(source.AllowWrite);
        Assert.Empty(fixture.Db.DataSourceSecretBindings);
        Assert.Single(fixture.Db.DataSourceRegistrationAudits);
        Assert.DoesNotContain("synthetic-password", Encoding.UTF8.GetString(row.Ciphertext));
        Assert.DoesNotContain("password", JsonSerializer.Serialize(saved), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("synthetic-password", JsonSerializer.Serialize(fixture.Db.DataSourceRegistrationAudits.ToArray()));
        var secrets = new CompositeSecretResolver([new ProtectedSecretResolver(fixture.Db, fixture.Encryption)]);
        var scoped = new ScopedDataSourceSecretResolver(new(fixture.Db, new EfAuthorizationDirectory(fixture.Db)), secrets);
        var password = await scoped.UseAsync(fixture.Authority, saved.Id, true,
            (connection, _) => Task.FromResult(new SqlConnectionStringBuilder(connection).Password));
        Assert.Equal(request.Password, password);
        await Assert.ThrowsAsync<DataSourceRegistrationConflictException>(() => fixture.Service.SaveAsync(fixture.Authority, Request(request.OperationId, password: "changed")));
        await Assert.ThrowsAsync<DataSourceRegistrationConflictException>(() => fixture.Service.SaveAsync(fixture.Authority, Request()));
    }

    [Fact]
    public async Task RotationReverifiesBumpsVersionAndRetainsPreviousBindingSnapshotDenial()
    {
        await using var fixture = await Fixture.Create();
        var first = await fixture.Service.SaveAsync(fixture.Authority, Request());
        var bindings = new DataSourceSecretBindingService(fixture.Db, new EfAuthorizationDirectory(fixture.Db));
        var prior = await bindings.RequireSourceAsync(fixture.Authority, first.Id, true);
        var request = Request(version: "1", password: "synthetic-rotated");
        var rotated = await fixture.Service.SaveAsync(fixture.Authority, request, first.Id);
        Assert.Equal("2", rotated.BindingVersion);
        Assert.Equal(2, fixture.Verifier.Calls);
        Assert.Equal(rotated, await fixture.Service.SaveAsync(fixture.Authority, request, first.Id));
        Assert.Equal(2, fixture.Verifier.Calls);
        Assert.Equal(2, fixture.Db.DataSourceRegistrationAudits.Count());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bindings.RevalidateAsync(prior, true));
        await Assert.ThrowsAsync<DataSourceRegistrationConflictException>(() => fixture.Service.SaveAsync(fixture.Authority, Request(version: "1"), first.Id));
        var row = Assert.Single(fixture.Db.ProtectedDataSourceSecrets);
        var value = await new ProtectedSecretResolver(fixture.Db, fixture.Encryption).ResolveAsync(SecretReference.Parse(row.Reference));
        Assert.Equal("synthetic-rotated", new SqlConnectionStringBuilder(value).Password);
    }

    [Theory]
    [InlineData("not_read_only")]
    [InlineData("login_failed")]
    [InlineData("database_not_found")]
    [InlineData("missing_view_definition")]
    [InlineData("unreachable")]
    public async Task FailedVerificationNeverCreatesAndFailedRotationPreservesCiphertext(string code)
    {
        await using var fixture = await Fixture.Create();
        fixture.Verifier.Code = code;
        var error = await Assert.ThrowsAsync<CustomerErpVerificationException>(() => fixture.Service.SaveAsync(fixture.Authority, Request()));
        Assert.Equal(code, error.Result.Code); Assert.Empty(fixture.Db.DataSources); Assert.Empty(fixture.Db.ProtectedDataSourceSecrets);
        Assert.Empty(fixture.Db.DataSourceRegistrationAudits);
        fixture.Verifier.Code = "ok";
        var first = await fixture.Service.SaveAsync(fixture.Authority, Request());
        var encrypted = Assert.Single(fixture.Db.ProtectedDataSourceSecrets).Ciphertext.ToArray();
        fixture.Verifier.Code = code;
        await Assert.ThrowsAsync<CustomerErpVerificationException>(() => fixture.Service.SaveAsync(fixture.Authority, Request(version: "1"), first.Id));
        Assert.Equal(encrypted, Assert.Single(fixture.Db.ProtectedDataSourceSecrets).Ciphertext);
        Assert.Equal(1, Assert.Single(fixture.Db.ProtectedDataSourceSecrets).Version);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("ADMIN")]
    [InlineData("revoked")]
    [InlineData("foreign-company")]
    [InlineData("foreign-tenant")]
    public async Task NonAdministratorsAndForeignScopesCannotProbeCreateRotateOrList(string mode)
    {
        await using var fixture = await Fixture.Create();
        var first = await fixture.Service.SaveAsync(fixture.Authority, Request());
        var authority = fixture.Authority;
        if (mode == "foreign-company") authority = AuthorizationContext.Create(authority.TenantId, Guid.NewGuid(), authority.UserId);
        else if (mode == "foreign-tenant") authority = AuthorizationContext.Create(Guid.NewGuid(), authority.CompanyId, authority.UserId);
        else
        {
            fixture.Db.RoleAssignments.RemoveRange(fixture.Db.RoleAssignments);
            if (mode != "revoked") fixture.Db.RoleAssignments.Add(new() { TenantId = authority.TenantId, CompanyId = authority.CompanyId, UserId = authority.UserId, RoleKey = mode });
            await fixture.Db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.TestAsync(authority, Request()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.SaveAsync(authority, Request()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.SaveAsync(authority, Request(version: "1"), first.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ListAsync(authority));
        Assert.Equal(1, fixture.Verifier.Calls);
    }

    [Fact]
    public async Task RevocationWhileVerificationAwaitsDiscardsMutationAndResult()
    {
        await using var fixture = await Fixture.Create();
        fixture.Verifier.DuringVerification = async () =>
        {
            fixture.Db.RoleAssignments.RemoveRange(fixture.Db.RoleAssignments);
            await fixture.Db.SaveChangesAsync();
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.TestAsync(fixture.Authority, Request()));
        Assert.Empty(fixture.Db.CustomerErpProbeAudits);
    }

    [Fact]
    public async Task TestIsAuditedWithoutCredentialsAndListsOnlyCompanySources()
    {
        await using var fixture = await Fixture.Create();
        Assert.Equal("ok", (await fixture.Service.TestAsync(fixture.Authority, Request())).Code);
        var audit = Assert.Single(fixture.Db.CustomerErpProbeAudits);
        Assert.Equal(fixture.Authority.UserId, audit.ActorUserId);
        Assert.DoesNotContain("synthetic-password", JsonSerializer.Serialize(audit));
        await fixture.Service.SaveAsync(fixture.Authority, Request());
        Assert.Single(await fixture.Service.ListAsync(fixture.Authority));
    }

    [Theory]
    [InlineData(18456, "login_failed")]
    [InlineData(4060, "database_not_found")]
    [InlineData(911, "database_not_found")]
    [InlineData(-2, "unreachable")]
    [InlineData(53, "unreachable")]
    public void SqlErrorsAreStableAndSanitized(int number, string code) => Assert.Equal(code, SqlCustomerErpConnectionVerifier.MapSqlError(number));

    [Fact]
    public void ConnectionBuilderQuotesPasswordAndPinsProtocolTimeoutAndAuthentication()
    {
        var input = Request(password: "x;User ID=sa;Database=master");
        var builder = new SqlConnectionStringBuilder(input.BuildConnectionString());
        Assert.Equal(input.Password, builder.Password); Assert.Equal("reader", builder.UserID);
        Assert.Equal("tcp:sql.example.test,1433", builder.DataSource); Assert.False(builder.IntegratedSecurity);
        Assert.False(builder.PersistSecurityInfo); Assert.Equal(5, builder.ConnectTimeout); Assert.False(builder.Pooling);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
        Assert.DoesNotContain(input.Password, input.ToString());
    }

    [Theory]
    [InlineData("tcp:host,1433")]
    [InlineData("host;User ID=sa")]
    [InlineData("host\\instance")]
    [InlineData("/etc/passwd")]
    [InlineData("host,1433")]
    public void InvalidHostCannotReachTheSqlClient(string host) => Assert.Throws<ArgumentException>(() => new CustomerErpConnectionInput
    { Host = host, Database = "Demo", UserName = "reader", Password = "synthetic" }.BuildConnectionString());

    private static CustomerErpSaveRequest Request(Guid? operation = null, string? version = null, string password = "synthetic-password") => new()
    {
        Host = "sql.example.test",
        Port = 1433,
        Database = "Demo",
        UserName = "reader",
        Password = password,
        OperationId = operation ?? Guid.NewGuid(),
        LogicalName = "Customer A",
        BindingVersion = version
    };

    private sealed class FakeVerifier : ICustomerErpConnectionVerifier
    {
        public string Code { get; set; } = "ok";
        public int Calls { get; private set; }
        public Func<Task>? DuringVerification { get; set; }
        public async Task<CustomerErpConnectionResult> VerifyAsync(string connectionString, CancellationToken cancellationToken)
        { Calls++; if (DuringVerification is not null) await DuringVerification(); return new(Code, "16.0", 3); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public required PlatformDbContext Db { get; init; }
        public required AuthorizationContext Authority { get; init; }
        public FakeVerifier Verifier { get; } = new();
        public ProtectedSecretEncryption Encryption { get; } = new(() => Convert.ToBase64String(new byte[32]));
        public CustomerErpService Service => new(Db, new EfAuthorizationDirectory(Db), Encryption, Verifier);
        public static async Task<Fixture> Create()
        {
            var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase("customer-erp-" + Guid.NewGuid()).Options);
            var authority = AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            db.Users.Add(new() { TenantId = authority.TenantId, Id = authority.UserId, IdentityProvider = "fixture", Subject = "owner", DisplayName = "Owner", IsActive = true });
            db.Companies.Add(new() { TenantId = authority.TenantId, Id = authority.CompanyId, Code = "FIXTURE", Name = "Company", IsActive = true });
            db.CompanyMemberships.Add(new() { TenantId = authority.TenantId, CompanyId = authority.CompanyId, UserId = authority.UserId, IsActive = true });
            db.RoleAssignments.Add(new() { TenantId = authority.TenantId, CompanyId = authority.CompanyId, UserId = authority.UserId, RoleKey = "admin" });
            await db.SaveChangesAsync(); return new() { Db = db, Authority = authority };
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
