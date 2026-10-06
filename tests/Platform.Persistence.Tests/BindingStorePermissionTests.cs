using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class BindingStorePermissionTests
{
    [Fact]
    public async Task Permission_query_uses_the_existing_admission_transaction_without_closing_its_connection()
    {
        await using var connection = new SyntheticConnection(1);
        await using var db = Database(connection);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await new BindingStorePermissionVerifier(db).RequireReadOnlyAsync();
        Assert.Same(transaction.GetDbTransaction(), connection.LastTransaction);
        Assert.Equal(ConnectionState.Open, connection.State);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    [InlineData("1")]
    public async Task Only_an_explicit_integer_permission_proof_is_accepted(object? result)
    {
        await using var connection = new SyntheticConnection(result);
        await using var db = Database(connection);
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new BindingStorePermissionVerifier(db).RequireReadOnlyAsync());
        Assert.Equal("Data source is unavailable for authorized use.", error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task Permission_proof_is_repeated_and_storage_fault_is_sanitized()
    {
        await using var connection = new SyntheticConnection(1);
        await using var db = Database(connection);
        var verifier = new BindingStorePermissionVerifier(db);
        await verifier.RequireReadOnlyAsync();
        connection.Result = 0;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => verifier.RequireReadOnlyAsync());
        connection.Fault = new SyntheticDbException("synthetic diagnostic must remain private");
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => verifier.RequireReadOnlyAsync());
        Assert.DoesNotContain("diagnostic", error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(3, connection.Calls);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    private static PlatformDbContext Database(DbConnection connection) => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer(connection).Options);

    private sealed class SyntheticDbException(string message) : DbException(message);

    internal sealed class SyntheticConnection(object? result) : DbConnection
    {
        private ConnectionState state;
        public object? Result { get; set; } = result;
        public Exception? Fault { get; set; }
        public int Calls { get; set; }
        public DbTransaction? LastTransaction { get; set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = "";
        public override string Database => "SyntheticPermissionProof";
        public override string DataSource => "SyntheticNoNetwork";
        public override string ServerVersion => "16.0";
        public override ConnectionState State => state;
        public override void Open() => state = ConnectionState.Open;
        public override Task OpenAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Open(); return Task.CompletedTask; }
        public override void Close() => state = ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new SyntheticTransaction(this, isolationLevel);
        protected override DbCommand CreateDbCommand() => new SyntheticCommand(this);
    }

    private sealed class SyntheticCommand(SyntheticConnection connection) : DbCommand
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        public override void Cancel() { }
        public override void Prepare() { }
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() { connection.Calls++; connection.LastTransaction = DbTransaction; if (connection.Fault is not null) throw connection.Fault; return connection.Result; }
        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(ExecuteScalar()); }
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
    }

    private sealed class SyntheticTransaction(DbConnection connection, IsolationLevel isolationLevel) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => isolationLevel;
        protected override DbConnection DbConnection => connection;
        public override void Commit() { }
        public override void Rollback() { }
    }
}
