using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupWholeBatchEffectReaderTests
{
    [Fact]
    public void OriginalEnvelopeMetadataKeepsInclusive256kAndFortyNoteLimitWithoutLoadingPayloads()
    {
        GroupWholeBatchEffectReader.RequireEnvelopeMetadata([], []);
        var ids = Enumerable.Range(0, 40).Select(_ => Guid.NewGuid()).ToArray();
        var metadata = ids.Select(id => new GroupWholeBatchEffectReader.EnvelopeMetadata(id, 6400, "owned-key", new('A', 64))).ToArray();
        GroupWholeBatchEffectReader.RequireEnvelopeMetadata(ids, metadata);
        metadata[0] = metadata[0] with { Length = 6401 };
        Assert.Throws<InvalidOperationException>(() => GroupWholeBatchEffectReader.RequireEnvelopeMetadata(ids, metadata));
        var next = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => GroupWholeBatchEffectReader.RequireEnvelopeMetadata(ids.Append(next).ToArray(),
            metadata.Append(new(next, 30, "owned-key", new('A', 64))).ToArray()));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("foreign")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-row")]
    [InlineData("empty-id")]
    [InlineData("null-row")]
    [InlineData("null-length")]
    [InlineData("short")]
    [InlineData("large")]
    public void MetadataRefusesMissingForeignDuplicateAndOversizedGraphsWithFixedInnerFreeError(string fault)
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var rows = ids.Select(id => new GroupWholeBatchEffectReader.EnvelopeMetadata(id, 30, "owned-key", new('A', 64))).ToArray();
        switch (fault)
        {
            case "missing": rows = rows[..1]; break;
            case "extra": rows = rows.Append(rows[0]).ToArray(); break;
            case "foreign": rows[0] = rows[0] with { RequestId = Guid.NewGuid() }; break;
            case "duplicate-id": ids[1] = ids[0]; break;
            case "duplicate-row": rows[1] = rows[0]; break;
            case "empty-id": ids[0] = Guid.Empty; rows[0] = rows[0] with { RequestId = Guid.Empty }; break;
            case "null-row": rows[0] = null!; break;
            case "null-length": rows[0] = rows[0] with { Length = null }; break;
            case "short": rows[0] = rows[0] with { Length = 29 }; break;
            case "large": rows[0] = rows[0] with { Length = GroupBrainContentProtector.MaximumEnvelopeLength + 1 }; break;
        }
        var error = Assert.Throws<InvalidOperationException>(() => GroupWholeBatchEffectReader.RequireEnvelopeMetadata(ids, rows));
        Assert.Equal("Whole group batch original effects are not available.", error.Message); Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoOwnedSqlTransactionCannotReadEffectsEvenWithWellShapedExpectation(bool sql)
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>();
        if (sql) options.UseSqlServer("Server=127.0.0.1,1;Database=never_connect;Integrated Security=true;Connect Timeout=1");
        else options.UseInMemoryDatabase(Guid.NewGuid().ToString());
        using var db = new PlatformDbContext(options.Options);
        var receipt = new GroupWorkCommitReceiptRecord { EffectLedgerVersion = 1, DependencyManifestVersion = 1, ExpectedEffectSha256 = new byte[32] };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new GroupWholeBatchEffectReader(db)
            .RequireLockedAsync(receipt, new(), [], default));
        Assert.Equal("Whole group batch original effects are not available.", error.Message); Assert.Null(error.InnerException);
        Assert.Null(db.Database.CurrentTransaction); Assert.Empty(db.ChangeTracker.Entries());
        if (sql) Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task CancellationAndMissingLegacyExpectationRefuseBeforeLookupAndDoNotBackfill()
    {
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=never_connect;Integrated Security=true;Connect Timeout=1").Options);
        var receipt = new GroupWorkCommitReceiptRecord(); var reader = new GroupWholeBatchEffectReader(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.RequireLockedAsync(receipt, new(), [], default));
        await Assert.ThrowsAsync<OperationCanceledException>(() => reader.RequireLockedAsync(receipt, new(), [], new CancellationToken(true)));
        Assert.Equal(0, receipt.EffectLedgerVersion); Assert.Null(receipt.ExpectedEffectSha256);
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData("RequestRows", 41)]
    [InlineData("RevisionRows", 41)]
    [InlineData("MetadataRows", 41)]
    [InlineData("EvidenceRows", 301)]
    [InlineData("OutboxRows", 2)]
    [InlineData("ItemRows", 41)]
    public void ActualEfQueriesKeepEveryScopeAndOriginalIdentityBoundWithoutConnection(string methodName, int maximum)
    {
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=never_connect;Integrated Security=true;Connect Timeout=1").Options);
        var receipt = new GroupWorkCommitReceiptRecord
        {
            TenantId = Guid.NewGuid(),
            CompanyId = Guid.NewGuid(),
            BindingId = Guid.NewGuid(),
            BatchId = Guid.NewGuid(),
            OperationId = Guid.NewGuid()
        };
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var method = typeof(GroupWholeBatchEffectReader).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        var args = method.GetParameters().Length == 1 ? new object[] { receipt } : new object[] { receipt, ids };
        var query = (IQueryable)method.Invoke(new GroupWholeBatchEffectReader(db), args)!;
        var sql = query.ToQueryString(); Assert.Contains("SELECT TOP(@", sql); Assert.Contains($" int = {maximum};", sql);
        foreach (var name in new[] { "TenantId", "CompanyId", "BindingId" }) Assert.Matches("\\[[^\\]]+\\]\\.\\[" + name + "\\] = @", sql);
        if (methodName is "RequestRows" or "OutboxRows")
        {
            Assert.Contains(receipt.BatchId.ToString("D"), sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(receipt.OperationId.ToString("D"), sql, StringComparison.OrdinalIgnoreCase);
        }
        else foreach (var id in ids) Assert.Contains(id.ToString("D"), sql, StringComparison.OrdinalIgnoreCase);
        if (methodName is "RevisionRows" or "MetadataRows") Assert.Contains("[Revision] = CAST(1 AS bigint)", sql);
        if (methodName == "EvidenceRows") Assert.Contains("[RequestRevision] = CAST(1 AS bigint)", sql);
        if (methodName == "MetadataRows")
        {
            Assert.Contains("DATALENGTH(", sql);
            var projection = sql[sql.IndexOf("SELECT TOP", StringComparison.Ordinal)..sql.IndexOf("FROM [aioffice]", StringComparison.Ordinal)];
            Assert.DoesNotMatch(@"(?:^|,)\s*\[[^\]]+\]\.\[ProtectedContent\](?:\s|,|$)", projection);
        }
        new TSql160Parser(true).Parse(new StringReader(sql), out var errors); Assert.Empty(errors);
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State); Assert.Empty(db.ChangeTracker.Entries());
    }
}
