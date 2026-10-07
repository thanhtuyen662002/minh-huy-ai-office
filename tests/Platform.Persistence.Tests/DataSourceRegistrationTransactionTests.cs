using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class DataSourceRegistrationTransactionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedConnectionRestoresBaselineAfterCommitOrRollbackBeforeClosing(bool commit)
    {
        await using var connection = new StickyConnection();
        await using var db = Database(connection);
        await using (var transaction = await DataSourceRegistrationTransaction.BeginAsync(db))
        {
            Assert.Equal(4, connection.Level);
            if (commit) await transaction.CommitAsync();
        }
        Assert.Equal(2, connection.Level);
        Assert.True(connection.RestoredAfterTransaction);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task CallerOwnedOpenConnectionPreservesItsOriginalIsolationAndLifetime(int initialLevel)
    {
        await using var connection = new StickyConnection { Level = initialLevel };
        connection.Open();
        await using var db = Database(connection);
        await using (var transaction = await DataSourceRegistrationTransaction.BeginAsync(db))
        {
            Assert.Equal(4, connection.Level);
            await transaction.CommitAsync();
        }
        Assert.Equal(initialLevel, connection.Level);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task FailedRestorationClosesAnUnsafeCallerOwnedSession()
    {
        await using var connection = new StickyConnection { FailReset = true };
        connection.Open();
        await using var db = Database(connection);
        await using (var transaction = await DataSourceRegistrationTransaction.BeginAsync(db)) { }
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task CancellationAfterBeginCannotSkipSessionRestoration()
    {
        await using var connection = new StickyConnection();
        await using var db = Database(connection);
        using var cancellation = new CancellationTokenSource();
        await using (var transaction = await DataSourceRegistrationTransaction.BeginAsync(db, cancellation.Token))
        {
            cancellation.Cancel();
        }
        Assert.Equal(2, connection.Level);
        Assert.False(connection.ResetTokenWasCanceled);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    private static PlatformDbContext Database(DbConnection connection) => new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(connection).Options);

    private sealed class StickyConnection : DbConnection
    {
        private ConnectionState state;
        public int Level { get; set; } = 2;
        public bool TransactionActive { get; set; }
        public bool RestoredAfterTransaction { get; set; }
        public bool FailReset { get; set; }
        public bool ResetTokenWasCanceled { get; set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = "";
        public override string Database => "SyntheticStickyIsolation";
        public override string DataSource => "NoNetwork";
        public override string ServerVersion => "16.0";
        public override ConnectionState State => state;
        public override void Open() => state = ConnectionState.Open;
        public override Task OpenAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Open(); return Task.CompletedTask; }
        public override void Close() => state = ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) { Level = 4; TransactionActive = true; return new StickyTransaction(this, isolationLevel); }
        protected override DbCommand CreateDbCommand() => new StickyCommand(this);
    }
    private sealed class StickyTransaction(StickyConnection connection, IsolationLevel level) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => level;
        protected override DbConnection DbConnection => connection;
        public override void Commit() { }
        public override void Rollback() { }
        protected override void Dispose(bool disposing) { connection.TransactionActive = false; base.Dispose(disposing); }
    }
    private sealed class StickyCommand(StickyConnection connection) : DbCommand
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
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
        public override object ExecuteScalar() => connection.Level;
        public override int ExecuteNonQuery()
        {
            if (connection.FailReset) throw new StickyException();
            Assert.False(connection.TransactionActive);
            connection.Level = CommandText switch
            {
                "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;" => 1,
                "SET TRANSACTION ISOLATION LEVEL READ COMMITTED;" => 2,
                "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;" => 3,
                "SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;" => 4,
                "SET TRANSACTION ISOLATION LEVEL SNAPSHOT;" => 5,
                _ => throw new InvalidOperationException("Unexpected synthetic isolation statement.")
            };
            connection.RestoredAfterTransaction = true;
            return 0;
        }
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            connection.ResetTokenWasCanceled = cancellationToken.IsCancellationRequested;
            return Task.FromResult(ExecuteNonQuery());
        }
    }
    private sealed class StickyException : DbException;
}
