using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupBatchHistoricalSqlReaderTests
{
    [Fact]
    public void AllActualQueriesAreScopedBoundedAndOriginalMetadataWithoutOpeningSql()
    {
        using var db = Database(); var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()); var batch = Guid.NewGuid();
        var reader = new GroupBatchHistoricalSqlReader(db, new(scope.TenantId, scope.CompanyId, Guid.NewGuid(), 1), TimeProvider.System);
        var queries = new[] {
            (reader.TerminalRows(scope, batch).ToQueryString(), 2), (reader.WorkRows(scope, batch).ToQueryString(), 101),
            (reader.SelectedRows(scope, batch).ToQueryString(), 101), (reader.RawRows(scope, batch).ToQueryString(), 501),
            (reader.OriginalRows(scope, batch, [1, 2]).ToQueryString(), 102), (reader.CutoffRows(scope, Guid.NewGuid(), 500).ToQueryString(), 1) };
        foreach (var (sql, maximum) in queries)
        {
            foreach (var field in new[] { "[TenantId] =", "[CompanyId] =", "[BindingId] =" }) Assert.Contains(field, sql);
            Assert.Contains("TOP(@", sql); Assert.Contains("= " + maximum + ";", sql);
            new TSql160Parser(true).Parse(new StringReader(sql), out var errors); Assert.Empty(errors);
            Assert.DoesNotContain("GroupMessageHeads", sql);
        }
        var cutoff = queries[5].Item1; Assert.Contains("[CommittedSequence] <=", cutoff);
        Assert.Contains("[Kind]", cutoff); Assert.Contains("[Revision] DESC", cutoff);
        Assert.DoesNotContain("ProtectedContent", cutoff); Assert.DoesNotContain("ContentKeyId", cutoff);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State); Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void ActualBlobPreflightSelectsLengthsAndOperationWithoutReturningPayloads()
    {
        using var db = Database(); var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var reader = new GroupBatchHistoricalSqlReader(db, new(scope.TenantId, scope.CompanyId, Guid.NewGuid(), 1), TimeProvider.System);
        var sql = reader.WorkRows(scope, Guid.NewGuid()).Select(x => new GroupBatchHistoricalSqlReader.BlobMetadata(x.OperationId,
            EF.Functions.DataLength(x.DependencyManifest), EF.Functions.DataLength(x.ExpectedEffectSha256))).ToQueryString();
        Assert.Contains("DATALENGTH([", sql); Assert.DoesNotContain("SELECT [g].[DependencyManifest]", sql);
        new TSql160Parser(true).Parse(new StringReader(sql), out var errors); Assert.Empty(errors);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("extra")]
    [InlineData("null-row")]
    [InlineData("empty-operation")]
    [InlineData("null-length")]
    [InlineData("underflow")]
    [InlineData("overflow")]
    [InlineData("null-digest")]
    [InlineData("short-digest")]
    [InlineData("long-digest")]
    [InlineData("duplicate")]
    public void MalformedOrUnboundedMetadataRefusesBeforeEncryptedPayloadSelection(string fault)
    {
        GroupBatchHistoricalSqlReader.BlobMetadata[] rows = [new(Guid.NewGuid(), 257, 32)]; var maximum = 1;
        switch (fault)
        {
            case "null": rows = null!; break;
            case "empty": rows = []; break;
            case "extra": rows = [rows[0], new(Guid.NewGuid(), 257, 32)]; break;
            case "null-row": rows = [null!]; break;
            case "empty-operation": rows = [rows[0] with { OperationId = Guid.Empty }]; break;
            case "null-length": rows = [rows[0] with { Length = null }]; break;
            case "underflow": rows = [rows[0] with { Length = 256 }]; break;
            case "overflow": rows = [rows[0] with { Length = 8178 }]; break;
            case "null-digest": rows = [rows[0] with { DigestLength = null }]; break;
            case "short-digest": rows = [rows[0] with { DigestLength = 31 }]; break;
            case "long-digest": rows = [rows[0] with { DigestLength = 33 }]; break;
            case "duplicate": rows = [rows[0], rows[0]]; maximum = 2; break;
        }
        var error = Assert.Throws<InvalidOperationException>(() => GroupBatchHistoricalSqlReader.RequireBlobMetadata(rows, maximum, 257, 8177));
        Assert.Equal("Group historical SQL evidence is not available.", error.Message); Assert.Null(error.InnerException);
    }

    [Fact]
    public void ExactMaximumContributorMetadataAndBothPayloadLengthBoundariesRemainValid()
    {
        GroupBatchHistoricalSqlReader.RequireBlobMetadata([new(Guid.NewGuid(), 257, 32)], 1, 257, 8177);
        GroupBatchHistoricalSqlReader.RequireBlobMetadata([new(Guid.NewGuid(), 8177, 32)], 1, 257, 8177);
        var rows = Enumerable.Range(0, 100).Select(_ => new GroupBatchHistoricalSqlReader.BlobMetadata(Guid.NewGuid(),
            GroupWorkDependencyManifest.MaximumBytes, 32)).ToArray();
        GroupBatchHistoricalSqlReader.RequireBlobMetadata(rows, 100, GroupWorkDependencyManifest.MinimumBytes, GroupWorkDependencyManifest.MaximumBytes);
        Assert.Throws<InvalidOperationException>(() => GroupBatchHistoricalSqlReader.RequireBlobMetadata(rows.Append(new(Guid.NewGuid(), 218, 32)).ToArray(),
            100, GroupWorkDependencyManifest.MinimumBytes, GroupWorkDependencyManifest.MaximumBytes));
    }

    [Fact]
    public async Task MissingOwnedUnitForeignWorkerEmptyBatchAndCancellationAreColdRefusals()
    {
        using var db = Database(); var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()); var service = Guid.NewGuid();
        var authority = Authority(scope, service); var reader = new GroupBatchHistoricalSqlReader(db, new(scope.TenantId, scope.CompanyId, service, 1), TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadLockedAsync(authority, Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadLockedAsync(authority, Guid.Empty));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ReadLockedAsync(Authority(scope, Guid.NewGuid()), Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => reader.ReadLockedAsync(null!, Guid.NewGuid()));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadLockedAsync(authority, Guid.NewGuid(), cts.Token));
        Assert.Equal(cts.Token, canceled.CancellationToken);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State); Assert.Empty(db.ChangeTracker.Entries());
    }

    private static PlatformDbContext Database() => new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(
        "Server=127.0.0.1,1;Database=owned_historical_design;Integrated Security=true;TrustServerCertificate=true").Options);
    private static GroupExtractionAuthority Authority(GroupScope scope, Guid service) => new(
        new(scope.TenantId, scope.CompanyId, service, 1, true),
        new(scope.TenantId, scope.CompanyId, service, scope.SourceBindingId, GroupServiceCapability.Extract, 1, true),
        new(scope, Guid.NewGuid(), new("owned", "account", "source"), "Owned source", 1, 0, true), 1, "env:OWNED_HISTORICAL_COLD");

    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("epoch")]
    public async Task ChangedCurrentWorkerBindingRefusesBeforeSqlEvenWithoutAnOwnedTransaction(string field)
    {
        using var db = Database(); var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()); var service = Guid.NewGuid();
        var worker = new GroupExtractionWorkerBinding(scope.TenantId, scope.CompanyId, service, 1);
        worker = field switch
        {
            "tenant" => worker with { TenantId = Guid.NewGuid() },
            "company" => worker with { CompanyId = Guid.NewGuid() },
            _ => worker with { CredentialEpoch = 2 }
        };
        var reader = new GroupBatchHistoricalSqlReader(db, worker, TimeProvider.System);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ReadLockedAsync(Authority(scope, service), Guid.NewGuid()));
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State); Assert.Empty(db.ChangeTracker.Entries());
    }
}
