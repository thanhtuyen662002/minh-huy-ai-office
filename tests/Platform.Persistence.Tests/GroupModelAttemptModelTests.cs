using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MinhHuy.AIOffice.Platform.Persistence.Migrations;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupModelAttemptModelTests
{
    private static PlatformDbContext Database() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer("Server=127.0.0.1,1;Database=owned_attempt_schema;Integrated Security=true;TrustServerCertificate=true").Options);

    [Fact]
    public void JournalStoresOnlyBoundedMetadataWithExplicitNullableFirstPredecessor()
    {
        using var db = Database(); var model = db.GetService<IDesignTimeModel>().Model;
        var start = model.FindEntityType(typeof(GroupModelAttemptStartRecord))!;
        var failure = model.FindEntityType(typeof(GroupModelAttemptFailureRecord))!;
        Assert.Equal(13, start.GetProperties().Count()); Assert.Equal(8, failure.GetProperties().Count());
        Assert.All(new[] { start, failure }, entity =>
        {
            Assert.DoesNotContain(entity.GetProperties(), property => property.IsShadowProperty());
            Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "RequestId" }, entity.FindPrimaryKey()!.Properties.Select(p => p.Name));
        });
        Assert.Equal(new[] { "PredecessorOrdinal", "PredecessorRequestId" }, start.GetProperties().Where(p => p.IsNullable).Select(p => p.Name));
        Assert.DoesNotContain(failure.GetProperties(), p => p.IsNullable);
        var hash = start.FindProperty("InputSha256")!;
        Assert.Equal("char(64)", hash.GetColumnType()); Assert.Equal(64, hash.GetMaxLength());
        Assert.False(hash.IsUnicode()); Assert.True(hash.IsFixedLength()); Assert.Equal("Latin1_General_100_BIN2", hash.GetCollation());
        Assert.Equal("Group model attempt start metadata.", new GroupModelAttemptStartRecord { InputSha256 = "PRIVATE" }.ToString());
        Assert.Equal("Group model attempt failure metadata.", new GroupModelAttemptFailureRecord().ToString());
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State); Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void SchemaBindsHistoricalClaimBatchEpochAndExactDensePredecessor()
    {
        using var db = Database(); var model = db.GetService<IDesignTimeModel>().Model;
        var start = model.FindEntityType(typeof(GroupModelAttemptStartRecord))!;
        Assert.Equal(3, start.GetForeignKeys().Count());
        Assert.All(start.GetForeignKeys(), key => Assert.Equal(DeleteBehavior.Restrict, key.DeleteBehavior));
        var claim = Assert.Single(start.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(GroupBatchClaimReceiptRecord));
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "ClaimOperationId", "BatchId", "ClaimEpoch" }, claim.Properties.Select(p => p.Name));
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "OperationId", "BatchId", "Epoch" }, claim.PrincipalKey.Properties.Select(p => p.Name));
        var previous = Assert.Single(start.GetForeignKeys(), key => key.PrincipalEntityType == start);
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "InputSha256", "PredecessorOrdinal", "PredecessorRequestId" }, previous.Properties.Select(p => p.Name));
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "InputSha256", "Ordinal", "RequestId" }, previous.PrincipalKey.Properties.Select(p => p.Name));
        Assert.Contains(start.GetIndexes(), index => index.IsUnique && index.Properties.Select(p => p.Name).SequenceEqual(
            new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "InputSha256", "Ordinal" }));
        var check = start.GetCheckConstraints().Single(c => c.Name == "CK_GroupModelAttemptStarts_Chain").Sql;
        foreach (var fragment in new[] { "[Ordinal] BETWEEN 1 AND 3", "[PredecessorOrdinal] IS NULL", "[PredecessorRequestId] IS NULL",
            "[PredecessorOrdinal] IS NOT NULL", "[PredecessorOrdinal]=[Ordinal]-1", "[PredecessorRequestId] IS NOT NULL" }) Assert.Contains(fragment, check);
    }

    [Fact]
    public void FailureCannotRebindOriginalStartDeadlineOrInventAnUnknownCategory()
    {
        using var db = Database(); var model = db.GetService<IDesignTimeModel>().Model;
        var failure = model.FindEntityType(typeof(GroupModelAttemptFailureRecord))!;
        var key = Assert.Single(failure.GetForeignKeys()); Assert.Equal(DeleteBehavior.Restrict, key.DeleteBehavior);
        var tuple = new[] { "TenantId", "CompanyId", "BindingId", "RequestId", "StartedAtUtc", "DeadlineUtc" };
        Assert.Equal(tuple, key.Properties.Select(p => p.Name)); Assert.Equal(tuple, key.PrincipalKey.Properties.Select(p => p.Name));
        Assert.Equal(typeof(GroupModelAttemptStartRecord), key.PrincipalEntityType.ClrType);
        var checks = failure.GetCheckConstraints().Select(c => c.Sql).ToArray();
        Assert.Contains("[Failure] BETWEEN 1 AND 4", checks);
        Assert.Contains(checks, c => c.Contains("DATEPART(tz,[ObservedAtUtc])=0") && c.Contains("[ObservedAtUtc]>=[StartedAtUtc]") && c.Contains("[ObservedAtUtc]<=[DeadlineUtc]"));
        Assert.Equal(new[] { 1, 2, 3, 4 }, Enum.GetValues<GroupModelAttemptFailure>().Select(x => (int)x));
    }

    [Fact]
    public void ExpansionDoesNotRewriteAnyExistingRowOrDestroyHistoryOnRollback()
    {
        var operations = new AddGroupModelAttemptJournal().UpOperations;
        Assert.Equal(new[] { "GroupModelAttemptFailures", "GroupModelAttemptStarts" }, operations.OfType<CreateTableOperation>().Select(t => t.Name).Order());
        var addedKey = Assert.Single(operations.OfType<AddUniqueConstraintOperation>());
        Assert.Equal("GroupBatchClaimReceipts", addedKey.Table);
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "OperationId", "BatchId", "Epoch" }, addedKey.Columns);
        Assert.DoesNotContain(operations, op => op is AddColumnOperation or AlterColumnOperation or DropColumnOperation or DropTableOperation
            or InsertDataOperation or UpdateDataOperation or DeleteDataOperation or DropForeignKeyOperation);
        Assert.Throws<NotSupportedException>(() => new AddGroupModelAttemptJournal().DownOperations);
    }

    [Fact]
    public void GeneratedExpansionAndEffectiveColumnRightsParseWithoutConnecting()
    {
        using var db = Database();
        var sql = db.GetService<IMigrator>().GenerateScript("20261011075456_AddGroupSignedTerminalCursor", "20261011121442_AddGroupModelAttemptJournal");
        new TSql160Parser(true).Parse(new StringReader(sql), out var errors); Assert.Empty(errors);
        new TSql160Parser(true).Parse(new StringReader(GroupModelAttemptPermissionVerifier.VerificationSql), out errors); Assert.Empty(errors);
        var grants = Assert.Single(new AddGroupModelAttemptJournal().UpOperations.OfType<SqlOperation>()).Sql;
        foreach (var type in new[] { typeof(GroupModelAttemptStartRecord), typeof(GroupModelAttemptFailureRecord) })
        {
            var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(type)!;
            Assert.Contains("ALTER AUTHORIZATION ON OBJECT::[aioffice].[" + entity.GetTableName() + "] TO [aioffice_binding_operator_owner]", grants);
            foreach (var property in entity.GetProperties()) Assert.Contains("[" + property.Name + "]", grants);
        }
        Assert.Equal(2, grants.Split("GRANT SELECT, INSERT").Length - 1);
        Assert.Equal(2, grants.Split("DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP").Length - 1);
        Assert.Contains("sys.columns", GroupModelAttemptPermissionVerifier.VerificationSql);
        Assert.Contains("ISNULL(HAS_PERMS_BY_NAME", GroupModelAttemptPermissionVerifier.VerificationSql);
        foreach (var permission in new[] { "UPDATE", "DELETE", "ALTER", "CONTROL", "TAKE OWNERSHIP" })
            Assert.Contains("N'" + permission + "')=0", GroupModelAttemptPermissionVerifier.VerificationSql);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State); Assert.Empty(db.ChangeTracker.Entries());
    }
}
