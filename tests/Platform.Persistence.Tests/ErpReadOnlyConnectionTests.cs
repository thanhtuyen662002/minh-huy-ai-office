extern alias RuntimeWorker;

using System.Data;
using System.Data.Common;
using MinhHuy.AiOffice.Shared.Contracts.Erp;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class ErpReadOnlyConnectionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    [InlineData("1")]
    [InlineData(true)]
    public async Task Ambiguous_or_denied_proof_never_reads_or_exposes_private_diagnostics(object? result)
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(result);
        await connection.OpenAsync(CancellationToken.None);
        var read = false;
        var error = await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => ErpReadOnlyConnectionVerifier.ReadAsync(connection,
            () => { read = true; return Task.FromResult("private row"); }));
        Assert.False(read);
        Assert.Single(connection.Commands);
        Assert.Equal("ERP read credentials are not qualified for read-only use.", error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task Mid_read_elevation_discards_evidence_and_later_calls_do_not_cache_qualification()
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(1);
        await connection.OpenAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => ErpReadOnlyConnectionVerifier.ReadAsync(connection,
            () => { connection.Result = 0; return Task.FromResult("private materialized rows"); }));
        Assert.Equal(2, connection.Calls);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => ErpReadOnlyConnectionVerifier.RequireAsync(connection));
        Assert.Equal(3, connection.Calls);
        connection.Result = 1;
        Assert.Equal("safe", await ErpReadOnlyConnectionVerifier.ReadAsync(connection, () => Task.FromResult("safe")));
        Assert.Equal(5, connection.Calls);
        Assert.All(connection.Commands, sql => Assert.Equal(ErpReadOnlyConnectionVerifier.VerificationSql, sql));
    }

    [Fact]
    public async Task Closed_connection_is_rejected_without_opening_or_replacing_the_session()
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(1);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => ErpReadOnlyConnectionVerifier.RequireAsync(connection));
        Assert.Empty(connection.Commands);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task Permission_query_fault_is_sanitized_and_pre_read_cancellation_propagates()
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(1) { Fault = new PrivateFault("private endpoint/password") };
        await connection.OpenAsync(CancellationToken.None);
        var error = await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => ErpReadOnlyConnectionVerifier.RequireAsync(connection));
        Assert.DoesNotContain("private", error.Message);
        Assert.Null(error.InnerException);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ErpReadOnlyConnectionVerifier.RequireAsync(connection, cancellation.Token));
        Assert.Equal(1, connection.Calls);
    }

    [Fact]
    public async Task Mid_read_cancellation_discards_evidence_before_final_query()
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(1);
        await connection.OpenAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ErpReadOnlyConnectionVerifier.ReadAsync(connection,
            () => { cancellation.Cancel(); return Task.FromResult("private row"); }, cancellation.Token));
        Assert.Equal(1, connection.Calls);
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("capability")]
    [InlineData("schema")]
    [InlineData("probe")]
    public async Task Every_independent_shipping_read_connection_requires_proof_before_its_query(string path)
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(0);
        var factory = new FixedFactory(connection);
        Task Read() => path switch
        {
            "catalog" => new SqlServerPilotErpEvidenceReader(factory).ReadAsync("synthetic").AsTask(),
            "schema" => new SqlServerSchemaDiscovery(factory).DiscoverAsync("tenant", "company", "source", 1, "synthetic").AsTask(),
            "probe" => new SqlDataSourceConnectionProbe(factory).ProbeAsync("synthetic", requireReadOnly: true).AsTask(),
            _ => new SqlServerErpReadCapabilityExecutor(factory, new ErpReadCapabilityRegistry([
                new("sample.read", "1", "SELECT 1", [], new())]))
                .ExecuteAsync("synthetic", new ErpReadCapabilityRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                    "sample.read", "1", new Dictionary<string, ErpReadParameterValue>())).AsTask()
        };
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(Read);
        Assert.Equal(1, factory.Calls);
        Assert.Single(connection.Commands); // Synthetic reader throws if a query is attempted.
    }

    [Fact]
    public async Task Connectivity_only_probe_preserves_write_source_semantics_but_cannot_claim_read_qualification()
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(0);
        await new SqlDataSourceConnectionProbe(new FixedFactory(connection)).ProbeAsync("synthetic");
        Assert.Empty(connection.Commands);
        IDataSourceConnectionProbe legacy = new LegacyProbe();
        await legacy.ProbeAsync("synthetic", requireReadOnly: false);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(async () => await legacy.ProbeAsync("synthetic", requireReadOnly: true));
    }

    private sealed class FixedFactory(DbConnection connection) : ISqlConnectionFactory
    {
        public int Calls { get; private set; }
        public DbConnection Create(string connectionString) { Calls++; return connection; }
    }
    private sealed class PrivateFault(string message) : DbException(message);
    private sealed class LegacyProbe : IDataSourceConnectionProbe
    {
        public ValueTask ProbeAsync(string connectionString, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
