using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupTerminalFrontierStoreTests
{
    [Fact]
    public async Task IgnoredResolverCancellationEndsOwnedWaitAndDisposesLateMaterial()
    {
        using var canceled = new CancellationTokenSource(); var pending = new TaskCompletionSource<GroupSourceKeyMaterial>();
        var wait = GroupTerminalFrontierStore.AwaitMaterialAsync(() => new(pending.Task), canceled.Token);
        canceled.Cancel(); var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(canceled.Token, error.CancellationToken);
        var late = new GroupSourceKeyMaterial("late-key", new byte[32]); pending.SetResult(late);
        Assert.Throws<ObjectDisposedException>(() => late.Key.ToArray());
    }

    [Fact]
    public async Task CancelledBeforeResolverEntryMakesNoCallAndSuccessfulMaterialRemainsCallerOwned()
    {
        var calls = 0; using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GroupTerminalFrontierStore.AwaitMaterialAsync(
            () => { calls++; return new(new GroupSourceKeyMaterial("key", new byte[32])); }, canceled.Token));
        Assert.Equal(canceled.Token, failure.CancellationToken); Assert.Equal(0, calls);
        using var key = new GroupSourceKeyMaterial("key", new byte[32]);
        Assert.Same(key, await GroupTerminalFrontierStore.AwaitMaterialAsync(() => new(key), CancellationToken.None)); Assert.Equal(32, key.Key.Length);
    }

    [Theory]
    [InlineData("sync")]
    [InlineData("async")]
    [InlineData("null")]
    public async Task ProviderFailuresNeverExposePrivateExceptionOrReturnNullMaterial(string failure)
    {
        ValueTask<GroupSourceKeyMaterial> Resolve() => failure switch
        {
            "sync" => throw new InvalidOperationException("Private resolver diagnostic"),
            "async" => new(Task.FromException<GroupSourceKeyMaterial>(new ArgumentException("Private resolver diagnostic"))),
            _ => new((GroupSourceKeyMaterial)null!)
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GroupTerminalFrontierStore.AwaitMaterialAsync(Resolve, CancellationToken.None));
        Assert.Equal("Group terminal frontier advance is not available.", error.Message); Assert.Null(error.InnerException);
    }
    [Fact]
    public void OneClockPathRefusesRollbackInsideHistoricalReadsEvenWhenLaterTimeRecovers()
    {
        var start = new DateTimeOffset(2026, 10, 11, 6, 0, 0, TimeSpan.Zero); var clock = new SequenceClock(start);
        var guarded = new GroupTerminalFrontierStore.MonotoneClock(clock);
        Assert.Equal(start, guarded.GetUtcNow()); Assert.Equal(start, guarded.GetUtcNow());
        clock.Current = start.AddTicks(1); Assert.Equal(clock.Current, guarded.GetUtcNow());
        clock.Current = start; Private(() => guarded.GetUtcNow());
        clock.Current = start.AddTicks(2); Assert.Equal(clock.Current, guarded.GetUtcNow());
        clock.Current = clock.Current.ToOffset(TimeSpan.FromHours(7)); Private(() => guarded.GetUtcNow());
    }

    [Fact]
    public async Task NonSqlProviderCannotReachTheExternalKeyProviderOrStageEffects()
    {
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var scope = Scope(); var keys = new ColdKeys(); var store = new GroupTerminalFrontierStore(db, Worker(scope), TimeProvider.System, keys);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AdvanceAsync(scope, Guid.NewGuid()));
        Assert.Empty(db.ChangeTracker.Entries()); Assert.Equal(0, keys.Calls);
    }
    [Fact]
    public void AllActualMetadataQueriesAreExactlyScopedBoundedAndLatestByVersion()
    {
        using var db = Database(); var scope = Scope(); var provider = new ColdKeys();
        var store = new GroupTerminalFrontierStore(db, Worker(scope), TimeProvider.System, provider);
        var queries = new[] { (store.LatestRows(scope).ToQueryString(), 2), (store.TargetRows(scope, Guid.NewGuid()).ToQueryString(), 2),
            (store.VersionRows(scope, 42).ToQueryString(), 2), (store.CursorRows(scope).ToQueryString(), 2),
            (store.KeyRows(scope).ToQueryString(), 4097) };
        foreach (var (sql, maximum) in queries)
        {
            foreach (var column in new[] { "[TenantId] =", "[CompanyId] =", "[BindingId] =" }) Assert.Contains(column, sql);
            Assert.Contains("TOP(@", sql); Assert.Contains("= " + maximum + ";", sql);
            new TSql160Parser(true).Parse(new StringReader(sql), out var errors); Assert.Empty(errors);
            Assert.DoesNotContain("GroupMessage", sql); Assert.DoesNotContain("MAX(", sql);
        }
        Assert.Contains("[Version] DESC", queries[0].Item1); Assert.Contains("[BatchId] =", queries[1].Item1);
        Assert.Contains("[Version] =", queries[2].Item1); Assert.Contains("ORDER BY", queries[4].Item1);
        var blobSql = store.LatestRows(scope).Select(x => new GroupTerminalFrontierStore.AdvanceBlob(x.Version, x.BatchId,
            EF.Functions.DataLength(x.Envelope), EF.Functions.DataLength(x.Fingerprint), EF.Functions.DataLength(x.PreviousFingerprint),
            EF.Functions.DataLength(x.TerminalManifestSha256), EF.Functions.DataLength(x.AuthoritySha256))).ToQueryString();
        Assert.Contains("DATALENGTH(", blobSql); Assert.DoesNotContain("SELECT [g].[Envelope]", blobSql);
        new TSql160Parser(true).Parse(new StringReader(blobSql), out var blobErrors); Assert.Empty(blobErrors);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State); Assert.Empty(db.ChangeTracker.Entries()); Assert.Equal(0, provider.Calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("extra")]
    [InlineData("null-row")]
    [InlineData("version")]
    [InlineData("batch")]
    [InlineData("envelope-null")]
    [InlineData("envelope-short")]
    [InlineData("envelope-long")]
    [InlineData("fingerprint")]
    [InlineData("previous")]
    [InlineData("terminal")]
    [InlineData("authority")]
    [InlineData("duplicate")]
    public void AdvanceLengthPreflightRefusesBeforeSelectingAnyEnvelope(string fault)
    {
        var row = new GroupTerminalFrontierStore.AdvanceBlob(1, Guid.NewGuid(), 322, 32, null, 32, 32);
        GroupTerminalFrontierStore.AdvanceBlob[] values = [row];
        switch (fault)
        {
            case "null": values = null!; break;
            case "extra": values = [row, row with { Version = 2 }, row with { Version = 3 }]; break;
            case "null-row": values = [null!]; break;
            case "version": values = [row with { Version = 0 }]; break;
            case "batch": values = [row with { BatchId = Guid.Empty }]; break;
            case "envelope-null": values = [row with { Envelope = null }]; break;
            case "envelope-short": values = [row with { Envelope = 321 }]; break;
            case "envelope-long": values = [row with { Envelope = 386 }]; break;
            case "fingerprint": values = [row with { Fingerprint = 31 }]; break;
            case "previous": values = [row with { PreviousFingerprint = 33 }]; break;
            case "terminal": values = [row with { TerminalHash = null }]; break;
            case "authority": values = [row with { AuthorityHash = 1_000_000 }]; break;
            case "duplicate": values = [row, row]; break;
        }
        Private(() => GroupTerminalFrontierStore.RequireAdvanceBlobs(values));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("extra")]
    [InlineData("null-row")]
    [InlineData("empty-key")]
    [InlineData("long-key")]
    [InlineData("non-ascii")]
    [InlineData("space")]
    [InlineData("material")]
    [InlineData("authority")]
    [InlineData("duplicate")]
    public void EntireFiniteRegistryIsBoundedWithoutDroppingAnyOlderKey(string fault)
    {
        var row = new GroupTerminalFrontierStore.KeyBlob("old-key", 32, 32);
        GroupTerminalFrontierStore.KeyBlob[] values = [row];
        switch (fault)
        {
            case "null": values = null!; break;
            case "extra": values = Enumerable.Range(0, 4097).Select(i => row with { KeyId = "key-" + i }).ToArray(); break;
            case "null-row": values = [null!]; break;
            case "empty-key": values = [row with { KeyId = "" }]; break;
            case "long-key": values = [row with { KeyId = new('a', 65) }]; break;
            case "non-ascii": values = [row with { KeyId = "khóa" }]; break;
            case "space": values = [row with { KeyId = "old-key " }]; break;
            case "material": values = [row with { Material = null }]; break;
            case "authority": values = [row with { Authority = 33 }]; break;
            case "duplicate": values = [row, row]; break;
        }
        Private(() => GroupTerminalFrontierStore.RequireKeyBlobs(values));
    }

    [Fact]
    public void ExactMetadataBoundariesAndOrdinalKeyIdsRemainAvailable()
    {
        GroupTerminalFrontierStore.RequireAdvanceBlobs([]); GroupTerminalFrontierStore.RequireKeyBlobs([]);
        GroupTerminalFrontierStore.RequireAdvanceBlobs([new(1, Guid.NewGuid(), 322, 32, null, 32, 32), new(long.MaxValue, Guid.NewGuid(), 385, 32, 32, 32, 32)]);
        GroupTerminalFrontierStore.RequireKeyBlobs(Enumerable.Range(0, 4096).Select(i => new GroupTerminalFrontierStore.KeyBlob("key-" + i, 32, 32)).ToArray());
        GroupTerminalFrontierStore.RequireKeyBlobs([new("key", 32, 32), new("KEY", 32, 32), new(new('a', 64), 32, 32)]);
    }

    [Fact]
    public void ExactSnapshotComparisonBindsEveryAdvanceCursorAndEnrolledKeyField()
    {
        var original = Snapshot(); GroupTerminalFrontierStore.RequireSameSnapshot(original, Clone(original));
        foreach (var property in typeof(GroupTerminalAdvanceRecord).GetProperties())
        {
            var changed = Clone(original); Change(property, changed.Latest[0]);
            Private(() => GroupTerminalFrontierStore.RequireSameSnapshot(original, changed));
            changed = Clone(original); Change(property, changed.Target!);
            Private(() => GroupTerminalFrontierStore.RequireSameSnapshot(original, changed));
            changed = Clone(original); Change(property, changed.TargetPrevious!);
            Private(() => GroupTerminalFrontierStore.RequireSameSnapshot(original, changed));
        }
        foreach (var property in typeof(GroupTerminalFrontierStateRecord).GetProperties())
        {
            var changed = Clone(original); Change(property, changed.Cursor!);
            Private(() => GroupTerminalFrontierStore.RequireSameSnapshot(original, changed));
        }
        foreach (var property in typeof(GroupTerminalAdvanceKeyRecord).GetProperties())
        {
            var changed = Clone(original); Change(property, changed.Keys[0]);
            Private(() => GroupTerminalFrontierStore.RequireSameSnapshot(original, changed));
        }
        foreach (var changed in new[] { original with { Latest = [] }, original with { Cursor = null }, original with { Target = null },
            original with { TargetPrevious = null }, original with { Keys = [] } })
            Private(() => GroupTerminalFrontierStore.RequireSameSnapshot(original, changed));
        var offset = Clone(original); offset.Cursor!.UpdatedAtUtc = original.Cursor!.UpdatedAtUtc.ToOffset(TimeSpan.FromHours(7));
        Private(() => GroupTerminalFrontierStore.RequireSameSnapshot(original, offset));
    }

    [Theory]
    [InlineData("batch")]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("worker")]
    [InlineData("dirty")]
    [InlineData("ambient")]
    [InlineData("mars")]
    [InlineData("open")]
    [InlineData("keys")]
    [InlineData("clock")]
    [InlineData("cancelled")]
    public async Task UnsafeEntryRefusesBeforeSqlOrAnyKeyProviderAwait(string fault)
    {
        var scope = Scope(); var worker = Worker(scope); var keys = new ColdKeys();
        using var connection = fault == "open" ? new InertOpenConnection() : null;
        var options = connection is null ? new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(
            "Server=127.0.0.1,1;Database=never_frontier;Integrated Security=true;MultipleActiveResultSets=" + (fault == "mars" ? "true" : "false")).Options
            : new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(connection).Options;
        using var db = new PlatformDbContext(options);
        if (fault == "tenant") scope = scope with { TenantId = Guid.NewGuid() };
        if (fault == "company") scope = scope with { CompanyId = Guid.NewGuid() };
        if (fault == "worker") worker = worker with { CredentialEpoch = 0 };
        if (fault == "dirty") db.Add(new GroupTerminalAdvanceRecord());
        var initialEntries = db.ChangeTracker.Entries().Count();
        using var ambient = fault == "ambient" ? new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled) : null;
        using var canceled = new CancellationTokenSource(); if (fault == "cancelled") canceled.Cancel();
        var store = new GroupTerminalFrontierStore(db, worker, fault == "clock" ? null! : TimeProvider.System, fault == "keys" ? null! : keys);
        if (fault == "cancelled")
            Assert.Equal(canceled.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AdvanceAsync(scope, Guid.NewGuid(), canceled.Token))).CancellationToken);
        else if (fault is "tenant" or "company") await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.AdvanceAsync(scope, Guid.NewGuid()));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => store.AdvanceAsync(scope, fault == "batch" ? Guid.Empty : Guid.NewGuid()));
        Assert.Equal(initialEntries, db.ChangeTracker.Entries().Count()); Assert.Equal(0, keys.Calls); Assert.Null(db.Database.CurrentTransaction);
        Assert.Equal(connection is null ? ConnectionState.Closed : ConnectionState.Open, db.Database.GetDbConnection().State);
    }

    private static GroupTerminalFrontierStore.Snapshot Snapshot()
    {
        var scope = Scope(); var now = DateTimeOffset.UtcNow;
        GroupTerminalAdvanceRecord Advance() => new()
        {
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            BindingId = scope.SourceBindingId,
            Version = 2,
            BatchId = Guid.NewGuid(),
            TerminalOperationId = Guid.NewGuid(),
            AfterSequence = 2,
            ThroughSequence = 4,
            TerminalManifestSha256 = [1],
            TerminalCommittedAtUtc = now,
            PreviousVersion = 1,
            PreviousThroughSequence = 2,
            PreviousFingerprint = [2],
            PreviousAdvancedAtUtc = now,
            Envelope = [3],
            Fingerprint = [4],
            KeyId = "key",
            ServiceId = Guid.NewGuid(),
            CredentialEpoch = 1,
            GrantVersion = 1,
            SourceVersion = 1,
            AccountVersion = 1,
            AuthoritySha256 = [5],
            AdvancedAtUtc = now
        };
        return new([Advance(), Advance()], new()
        {
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            BindingId = scope.SourceBindingId,
            Version = 2,
            ThroughSequence = 4,
            LastTerminalBatchId = Guid.NewGuid(),
            UpdatedAtUtc = now
        }, Advance(), Advance(),
            [new() { TenantId = scope.TenantId, CompanyId = scope.CompanyId, BindingId = scope.SourceBindingId, KeyId = "key",
                MaterialFingerprint = [6], EnrolledAtUtc = now, ServiceId = Guid.NewGuid(), CredentialEpoch = 1, GrantVersion = 1,
                SourceVersion = 1, AccountVersion = 1, AuthoritySha256 = [7] }]);
    }
    private static GroupTerminalFrontierStore.Snapshot Clone(GroupTerminalFrontierStore.Snapshot value) =>
        JsonSerializer.Deserialize<GroupTerminalFrontierStore.Snapshot>(JsonSerializer.Serialize(value))!;
    private static void Change(System.Reflection.PropertyInfo property, object target)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var value = property.GetValue(target);
        object next = type == typeof(Guid) ? Guid.NewGuid() : type == typeof(long) ? (long)value! + 1 : type == typeof(int) ? (int)value! + 1
            : type == typeof(DateTimeOffset) ? ((DateTimeOffset)value!).AddTicks(1) : type == typeof(string) ? "changed" : new byte[] { 255 };
        property.SetValue(target, next);
    }
    private static void Private(Action action)
    { var error = Assert.Throws<InvalidOperationException>(action); Assert.Equal("Group terminal frontier advance is not available.", error.Message); Assert.Null(error.InnerException); }
    private static GroupScope Scope() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static GroupExtractionWorkerBinding Worker(GroupScope scope) => new(scope.TenantId, scope.CompanyId, Guid.NewGuid(), 1);
    private static PlatformDbContext Database() => new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(
        "Server=127.0.0.1,1;Database=owned_frontier_design;Integrated Security=true;TrustServerCertificate=true").Options);
    private sealed class ColdKeys : IGroupSourceKeyProvider
    {
        internal int Calls { get; private set; }
        public ValueTask<GroupSourceKeyMaterial> ResolveReadAsync(GroupScope scope, string id, CancellationToken token = default)
        { Calls++; throw new InvalidOperationException("A cold refusal must not reach a provider."); }
        public ValueTask<GroupSourceKeyMaterial> ResolveWriteAsync(GroupScope scope, CancellationToken token = default)
        { Calls++; throw new InvalidOperationException("A cold refusal must not reach a provider."); }
    }
    private sealed class SequenceClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Current = now;
        public override DateTimeOffset GetUtcNow() => Current;
    }
    private sealed class InertOpenConnection : DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = "Server=127.0.0.1,1;Database=inert_frontier";
        public override string Database => "inert_frontier";
        public override string DataSource => "inert_frontier";
        public override string ServerVersion => "16";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string name) => throw new InvalidOperationException();
        public override void Close() { }
        public override void Open() => throw new InvalidOperationException();
        protected override DbTransaction BeginDbTransaction(System.Data.IsolationLevel level) => throw new InvalidOperationException();
        protected override DbCommand CreateDbCommand() => throw new InvalidOperationException();
    }
}
