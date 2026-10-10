using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedEffectSnapshotSchemaTests
{
    private static PlatformDbContext Context() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer("Server=127.0.0.1,1;Database=owned_schema_inert;User ID=inert;Password=inert;TrustServerCertificate=true").Options);

    private static string Script() => File.ReadAllText(Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")), "scripts/smoke-group-reference.py"));

    private static string QuotedValueAfter(string script, string marker)
    {
        var begin = script.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(begin >= 0);
        begin += marker.Length;
        var end = script.IndexOf('"', begin);
        Assert.True(end > begin);
        return script[begin..end];
    }

    [Theory]
    [InlineData(typeof(GroupAccountCoverageGapRecord), "GroupAccountCoverageGaps")]
    [InlineData(typeof(GroupListenerLeaseRecord), "GroupListenerLeases")]
    [InlineData(typeof(GroupListenerCommandReceiptRecord), "GroupListenerCommandReceipts")]
    public void RetainedAccountSnapshotOrderUsesActualColumnsAndScopedUniqueKey(Type record, string table)
    {
        using var db = Context();
        var entity = db.Model.FindEntityType(record)!;
        var store = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        var columns = entity.GetProperties().Select(x => x.GetColumnName(store)!).ToHashSet(StringComparer.Ordinal);
        var order = QuotedValueAfter(Script(), "(\"" + table + "\", \"").Split(',').Select(x => x.Trim()).ToHashSet(StringComparer.Ordinal);
        Assert.All(order, column => Assert.Contains(column, columns));
        var scopedColumns = new[] { "TenantId", "CompanyId", "ConnectorAccountId" }.ToHashSet(StringComparer.Ordinal);
        Assert.All(entity.FindPrimaryKey()!.Properties.Select(x => x.GetColumnName(store)!).Where(x => !scopedColumns.Contains(x)),
            column => Assert.Contains(column, order));
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Fact]
    public void CleanSourceStateSnapshotOmitsOnlyDocumentedPreparationFields()
    {
        using var db = Context();
        var entity = db.Model.FindEntityType(typeof(GroupSourceStateRecord))!;
        var store = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        var all = entity.GetProperties().Select(x => x.GetColumnName(store)!).ToHashSet(StringComparer.Ordinal);
        var retained = QuotedValueAfter(Script(), "digest(\"GroupSourceStates\", \"BindingId\", \"").Split(',').ToHashSet(StringComparer.Ordinal);
        Assert.All(retained, column => Assert.Contains(column, all));
        Assert.Equal(new[] { "FirstPendingAtUtc", "LastPendingAtUtc", "ScheduledThroughSequence" }, all.Except(retained).Order().ToArray());
    }

    [Fact]
    public void PreparedSourceStateOracleIncludesEveryActualStateColumn()
    {
        using var db = Context();
        var entity = db.Model.FindEntityType(typeof(GroupSourceStateRecord))!;
        var store = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        var retained = QuotedValueAfter(Script(), "source_state_columns = \"").Split(',');
        Assert.Equal(entity.GetProperties().Select(x => x.GetColumnName(store)!).Order(), retained.Order());
    }

    [Fact]
    public void AllocatedRevisionOracleComparesEveryActualCopiedColumn()
    {
        using var db = Context();
        var entity = db.Model.FindEntityType(typeof(GroupBatchAllocatedRevisionRecord))!;
        var store = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        var copied = QuotedValueAfter(Script(), "revision_columns = \"").Split(',').ToHashSet(StringComparer.Ordinal);
        Assert.Equal(entity.GetProperties().Select(x => x.GetColumnName(store)!).Where(x => x != "BatchId").Order(), copied.Order());
        var source = db.Model.FindEntityType(typeof(GroupMessageRevisionRecord))!;
        var sourceStore = StoreObjectIdentifier.Table(source.GetTableName()!, source.GetSchema());
        var sourceColumns = source.GetProperties().Select(x => x.GetColumnName(sourceStore)!).ToHashSet(StringComparer.Ordinal);
        Assert.All(copied, column => Assert.Contains(column, sourceColumns));
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }
}
