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

    [Theory]
    [InlineData("created")]
    [InlineData("not_permitted")]
    public async Task ReaderCreationIsAdminOnlyAndAuditsOnlyAFixedCode(string code)
    {
        await using var fixture = await Fixture.Create();
        var provisioner = new FakeProvisioner { Code = code };
        var result = await fixture.Service.CreateReaderAsync(fixture.Authority, Request(password: "synthetic-admin-password"), provisioner);
        Assert.Equal(code, result.Code);
        Assert.Equal("synthetic-admin-password", provisioner.Received!.Password);
        var audit = Assert.Single(fixture.Db.CustomerErpProbeAudits);
        Assert.Equal("reader_" + code, audit.Code);
        var stored = JsonSerializer.Serialize(audit);
        Assert.DoesNotContain("synthetic-admin-password", stored); Assert.DoesNotContain("synthetic-reader-password", stored);
        Assert.Empty(fixture.Db.ProtectedDataSourceSecrets); Assert.Empty(fixture.Db.DataSources);
        Assert.DoesNotContain("synthetic", result.ToString());

        fixture.Db.RoleAssignments.RemoveRange(fixture.Db.RoleAssignments);
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.CreateReaderAsync(fixture.Authority, Request(), provisioner));
        Assert.Equal(1, provisioner.Calls);
    }

    [Fact]
    public void ReaderProvisioningMapsPermissionErrors()
    {
        Assert.Equal("not_permitted", SqlCustomerErpReaderProvisioner.MapSqlError(15247));
        Assert.Equal("login_failed", SqlCustomerErpReaderProvisioner.MapSqlError(18456));
        Assert.Equal("database_not_found", SqlCustomerErpReaderProvisioner.MapSqlError(4060));
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

    private sealed class FakeProvisioner : ICustomerErpReaderProvisioner
    {
        public string Code { get; init; } = "created";
        public int Calls { get; private set; }
        public CustomerErpConnectionInput? Received { get; private set; }
        public Task<CustomerErpReaderResult> CreateAsync(CustomerErpConnectionInput admin, CancellationToken cancellationToken)
        {
            Calls++; Received = admin;
            return Task.FromResult(Code == "created" ? new CustomerErpReaderResult(Code, "aioffice_reader_0a1b2c3d", "synthetic-reader-password") : new(Code));
        }
    }

    private sealed class FakeVerifier : ICustomerErpConnectionVerifier
    {
        private int calls;
        public string Code { get; set; } = "ok";
        /// <summary>Overrides <see cref="Code"/> per database, for shared server checks that run in parallel.</summary>
        public Func<string, string?>? CodeForDatabase { get; set; }
        public int Calls => Volatile.Read(ref calls);
        public Func<Task>? DuringVerification { get; set; }
        public async Task<CustomerErpConnectionResult> VerifyAsync(string connectionString, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            if (DuringVerification is not null) await DuringVerification();
            return new(CodeForDatabase?.Invoke(new SqlConnectionStringBuilder(connectionString).InitialCatalog) ?? Code, "16.0", 3);
        }
    }

    private static ErpServerSaveRequest ServerRequest(string? version = null, string host = "sql.example.test", string userName = "reader",
        string password = "synthetic-server-password", Guid? operation = null) => new()
        {
            OperationId = operation ?? Guid.NewGuid(),
            Name = "Máy chủ chính",
            Host = host,
            Port = 1433,
            UserName = userName,
            Password = password,
            Version = version
        };
    private static ErpServerSourceRequest OnServer(Guid server, string database, string? name = null, string? version = null) => new()
    {
        OperationId = Guid.NewGuid(),
        ServerId = server,
        Database = database,
        LogicalName = name,
        BindingVersion = version
    };
    private static async Task<SqlConnectionStringBuilder> ConnectionAsync(Fixture fixture, Guid source)
    {
        var secrets = new CompositeSecretResolver([new ProtectedSecretResolver(fixture.Db, fixture.Encryption)]);
        var scoped = new ScopedDataSourceSecretResolver(new(fixture.Db, new EfAuthorizationDirectory(fixture.Db)), secrets);
        return await scoped.UseAsync(fixture.Authority, source, true, (connection, _) => Task.FromResult(new SqlConnectionStringBuilder(connection)));
    }

    [Fact]
    public async Task ChangingASharedServerRechecksAndRewritesEveryDatabaseOnItTogether()
    {
        await using var fixture = await Fixture.Create();
        var server = await fixture.Service.SaveServerAsync(fixture.Authority, ServerRequest());
        Assert.Equal(0, fixture.Verifier.Calls);
        var a = await fixture.Service.SaveSourceOnServerAsync(fixture.Authority, OnServer(server.Id, "DemoA", "Customer A"));
        var b = await fixture.Service.SaveSourceOnServerAsync(fixture.Authority, OnServer(server.Id, "DemoB", "Customer B"));
        Assert.Equal((server.Id, "DemoA"), (a.ServerId!.Value, a.Database));
        Assert.Equal(2, Assert.Single(await fixture.Service.ListServersAsync(fixture.Authority)).SourceCount);
        Assert.Equal(new[] { "DemoA", "DemoB" }, (await fixture.Service.ListAsync(fixture.Authority)).Select(row => row.Database!));
        Assert.Equal("synthetic-server-password", (await ConnectionAsync(fixture, a.Id)).Password);

        // New address, login kept: the blank password keeps the saved one and every database is checked again.
        var change = ServerRequest(version: "1", host: "sql2.example.test", userName: "", password: "");
        var changed = await fixture.Service.SaveServerAsync(fixture.Authority, change, server.Id);
        Assert.Equal(("2", "sql2.example.test", "reader"), (changed.Version, changed.Host, changed.UserName));
        Assert.Equal(4, fixture.Verifier.Calls);
        foreach (var (source, database) in new[] { (a.Id, "DemoA"), (b.Id, "DemoB") })
        {
            var connection = await ConnectionAsync(fixture, source);
            Assert.Equal(("tcp:sql2.example.test,1433", database, "reader", "synthetic-server-password"),
                (connection.DataSource, connection.InitialCatalog, connection.UserID, connection.Password));
        }
        Assert.All(fixture.Db.ProtectedDataSourceSecrets, row => Assert.Equal(2, row.Version));
        // A lost reply can be retried without checking again; a stale version is refused.
        Assert.Equal(changed, await fixture.Service.SaveServerAsync(fixture.Authority, change, server.Id));
        Assert.Equal(4, fixture.Verifier.Calls);
        await Assert.ThrowsAsync<DataSourceRegistrationConflictException>(() =>
            fixture.Service.SaveServerAsync(fixture.Authority, ServerRequest(version: "1"), server.Id));
        Assert.Single(fixture.Db.ErpServerAudits.Where(row => row.ServerVersion == 2));
        Assert.DoesNotContain("synthetic-server-password", JsonSerializer.Serialize(await fixture.Service.ListServersAsync(fixture.Authority)));
        Assert.DoesNotContain("synthetic-server-password", Encoding.UTF8.GetString(Assert.Single(fixture.Db.ErpServers).Ciphertext));
    }

    [Fact]
    public async Task OneFailingDatabaseLeavesTheServerAndEveryDatabaseUnchanged()
    {
        await using var fixture = await Fixture.Create();
        var server = await fixture.Service.SaveServerAsync(fixture.Authority, ServerRequest());
        var a = await fixture.Service.SaveSourceOnServerAsync(fixture.Authority, OnServer(server.Id, "DemoA", "Customer A"));
        await fixture.Service.SaveSourceOnServerAsync(fixture.Authority, OnServer(server.Id, "DemoB", "Customer B"));
        fixture.Verifier.CodeForDatabase = database => database == "DemoB" ? "login_failed" : null;
        var failure = await Assert.ThrowsAsync<ErpServerVerificationException>(() =>
            fixture.Service.SaveServerAsync(fixture.Authority, ServerRequest(version: "1", password: "rotated"), server.Id));
        Assert.Equal(("login_failed", "Customer B"), (failure.Result.Code, failure.Source));
        Assert.Equal("1", Assert.Single(await fixture.Service.ListServersAsync(fixture.Authority)).Version);
        Assert.All(fixture.Db.ProtectedDataSourceSecrets, row => Assert.Equal(1, row.Version));
        Assert.Equal("synthetic-server-password", (await ConnectionAsync(fixture, a.Id)).Password);
        Assert.Empty(fixture.Db.ErpServerAudits.Where(row => row.ServerVersion > 1));
    }

    [Fact]
    public async Task ATypedSourceMovesOntoAServerKeepingItsDatabaseAndThenChangesOnlyThroughIt()
    {
        await using var fixture = await Fixture.Create();
        var typed = await fixture.Service.SaveAsync(fixture.Authority, Request());
        var server = await fixture.Service.SaveServerAsync(fixture.Authority, ServerRequest(host: "shared.example.test"));
        var moved = await fixture.Service.SaveSourceOnServerAsync(fixture.Authority, OnServer(server.Id, "", version: "1"), typed.Id);
        Assert.Equal(("2", "Demo", server.Id), (moved.BindingVersion, moved.Database, moved.ServerId!.Value));
        var connection = await ConnectionAsync(fixture, typed.Id);
        Assert.Equal(("tcp:shared.example.test,1433", "Demo", "synthetic-server-password"), (connection.DataSource, connection.InitialCatalog, connection.Password));
        // Typing a connection for a source on a shared server would make it drift from the server.
        await Assert.ThrowsAsync<DataSourceRegistrationConflictException>(() =>
            fixture.Service.SaveAsync(fixture.Authority, Request(version: "2", password: "typed"), typed.Id));
        var other = await fixture.Service.SaveSourceOnServerAsync(fixture.Authority, OnServer(server.Id, "Demo2", version: "2"), typed.Id);
        Assert.Equal(("3", "Demo2"), (other.BindingVersion, other.Database));
        Assert.Single(fixture.Db.ErpServerSources);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.SaveSourceOnServerAsync(fixture.Authority, OnServer(server.Id, "", "New customer")));
    }

    [Fact]
    public void ServerCiphertextNeverOpensAsASourceSecret()
    {
        var encryption = new ProtectedSecretEncryption(() => Convert.ToBase64String(new byte[32]));
        var (tenant, company, id) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var payload = encryption.Encrypt(tenant, company, id, "{}", ProtectedSecretEncryption.ErpServerPurpose);
        Assert.Equal("{}", encryption.Decrypt(tenant, company, id, payload, ProtectedSecretEncryption.ErpServerPurpose));
        Assert.Throws<InvalidOperationException>(() => encryption.Decrypt(tenant, company, id, payload));
        Assert.Throws<InvalidOperationException>(() => encryption.Encrypt(tenant, company, id, "{}", "other"));
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

    [Fact]
    public void Unreachable_connection_explains_the_kind_without_credentials()
    {
        var tls = SqlCustomerErpConnectionVerifier.Unreachable(new InvalidOperationException("A connection was established but the login failed",
            new System.Security.Authentication.AuthenticationException("Authentication failed, see inner exception.",
                new Exception("error:0A000102:SSL routines::unsupported protocol"))));
        Assert.Equal("unreachable", tls.Code);
        var finding = Assert.Single(tls.Findings!);
        Assert.Equal("connection_tls", finding.Code);
        Assert.Contains("unsupported protocol", finding.Detail);
        Assert.Equal("connection_dns", Assert.Single(SqlCustomerErpConnectionVerifier.Unreachable(
            new InvalidOperationException("x", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound))).Findings!).Code);
        Assert.Equal("connection_refused", Assert.Single(SqlCustomerErpConnectionVerifier.Unreachable(
            new InvalidOperationException("x", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused))).Findings!).Code);
        Assert.True(SqlCustomerErpConnectionVerifier.Unreachable(new InvalidOperationException(new string('x', 2000))).Findings![0].Detail!.Length <= 300);
    }
}
