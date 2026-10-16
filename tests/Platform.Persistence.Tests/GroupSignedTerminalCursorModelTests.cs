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

public sealed class GroupSignedTerminalCursorModelTests
{
    private static PlatformDbContext Database() => new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(
        "Server=127.0.0.1,1;Database=owned_signed_cursor_schema;Integrated Security=true;TrustServerCertificate=true").Options);

    [Fact]
    public void CursorReferencesAllSevenFieldsOfTheScopedSignedAdvanceWithoutExtraPayload()
    {
        using var db = Database(); var model = db.GetService<IDesignTimeModel>().Model;
        var cursor = model.FindEntityType(typeof(GroupTerminalFrontierStateRecord))!;
        var advance = model.FindEntityType(typeof(GroupTerminalAdvanceRecord))!;
        var fk = Assert.Single(cursor.GetForeignKeys(), x => x.PrincipalEntityType == advance);
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "Version", "ThroughSequence", "LastTerminalBatchId", "UpdatedAtUtc" }, fk.Properties.Select(x => x.Name));
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "Version", "ThroughSequence", "BatchId", "AdvancedAtUtc" }, fk.PrincipalKey.Properties.Select(x => x.Name));
        Assert.Equal("FK_GroupTerminalFrontierStates_SignedAdvance", fk.GetConstraintName());
        Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
        Assert.Equal(7, cursor.GetProperties().Count()); Assert.Equal(26, advance.GetProperties().Count());
        Assert.All(cursor.GetProperties(), x => Assert.False(x.IsShadowProperty()));
        Assert.Equal(5, cursor.GetForeignKeys().Count());
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Fact]
    public void PersistedEmptyCursorAndPartialNullCannotBypassTheCompositeForeignKey()
    {
        using var db = Database(); var cursor = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GroupTerminalFrontierStateRecord))!;
        var check = Assert.Single(cursor.GetCheckConstraints(), x => x.Name == "CK_GroupTerminalFrontierStates_Signed").Sql;
        Assert.Contains("[ThroughSequence]>0", check); Assert.Contains("[Version]>0", check);
        Assert.Contains("[LastTerminalBatchId] IS NOT NULL", check); Assert.Contains("DATEPART(tz,[UpdatedAtUtc])=0", check);
        Assert.DoesNotContain(" OR ", check); Assert.DoesNotContain(" IS NULL", check);
        // The CLR/physical nullable column remains compatible; the CHECK closes
        // SQL's partial-null FK exception. Absence alone represents empty work.
        Assert.True(cursor.FindProperty("LastTerminalBatchId")!.IsNullable);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void PreflightRefusesLegacyBeforeAnyDdlAndDoesNotInventOrRewriteHistory()
    {
        var operations = new AddGroupSignedTerminalCursor().UpOperations;
        var first = Assert.IsType<SqlOperation>(operations[0]); Assert.False(first.SuppressTransaction);
        Assert.Contains("THROW 51048", first.Sql); Assert.Contains("WITH (UPDLOCK,HOLDLOCK)", first.Sql);
        foreach (var pair in new[] { "TenantId", "CompanyId", "BindingId", "Version", "ThroughSequence" })
            Assert.Contains($"a.[{pair}]=c.[{pair}]", first.Sql);
        Assert.Contains("a.[BatchId]=c.[LastTerminalBatchId]", first.Sql);
        Assert.Contains("a.[AdvancedAtUtc]=c.[UpdatedAtUtc]", first.Sql);
        Assert.Contains("c.[LastTerminalBatchId] IS NULL", first.Sql);
        Assert.Contains("c.[ThroughSequence]<=0", first.Sql);
        Assert.Contains("c.[Version]<=0", first.Sql);
        Assert.Contains("DATEPART(tz,c.[UpdatedAtUtc])<>0", first.Sql);
        Assert.Contains("NOT EXISTS", first.Sql);
        Assert.Equal(5, operations.Count);
        Assert.DoesNotContain(operations, x => x is CreateTableOperation or AddColumnOperation or AlterColumnOperation
            or DropColumnOperation or DropTableOperation or InsertDataOperation or UpdateDataOperation or DeleteDataOperation);
        Assert.Single(operations.OfType<SqlOperation>());
        foreach (var forbidden in new[] { "INSERT INTO", "UPDATE ", "DELETE ", "MERGE ", "GRANT ", "REVOKE ", "DROP " }) Assert.DoesNotContain(forbidden, first.Sql);
        Assert.Throws<NotSupportedException>(() => new AddGroupSignedTerminalCursor().DownOperations);
    }

    [Fact]
    public void ActualMigrationAndModelAgreeOnCompleteKeysAndPreserveExistingChecks()
    {
        using var db = Database(); var model = db.GetService<IDesignTimeModel>().Model;
        var operations = new AddGroupSignedTerminalCursor().UpOperations;
        var foreign = Assert.Single(operations.OfType<AddForeignKeyOperation>());
        Assert.Equal("aioffice", foreign.Schema); Assert.Equal("aioffice", foreign.PrincipalSchema);
        Assert.Equal("GroupTerminalFrontierStates", foreign.Table); Assert.Equal("GroupTerminalAdvances", foreign.PrincipalTable);
        Assert.Equal(ReferentialAction.Restrict, foreign.OnDelete);
        var cursor = model.FindEntityType(typeof(GroupTerminalFrontierStateRecord))!;
        var fk = Assert.Single(cursor.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(GroupTerminalAdvanceRecord));
        Assert.Equal(fk.Properties.Select(x => x.Name), foreign.Columns);
        Assert.Equal(fk.PrincipalKey.Properties.Select(x => x.Name), foreign.PrincipalColumns);
        var unique = Assert.Single(operations.OfType<AddUniqueConstraintOperation>());
        Assert.Equal("GroupTerminalAdvances", unique.Table); Assert.Equal(foreign.PrincipalColumns, unique.Columns);
        var index = Assert.Single(operations.OfType<CreateIndexOperation>());
        Assert.Equal("GroupTerminalFrontierStates", index.Table); Assert.Equal(foreign.Columns, index.Columns);
        var check = Assert.Single(operations.OfType<AddCheckConstraintOperation>());
        Assert.Equal(cursor.GetCheckConstraints().Single(x => x.Name == check.Name).Sql, check.Sql);
        Assert.Contains(cursor.GetCheckConstraints(), x => x.Name == "CK_GroupTerminalFrontierStates_Values");
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State); Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(MigrationsSqlGenerationOptions.Default)]
    [InlineData(MigrationsSqlGenerationOptions.Idempotent)]
    public void GeneratedForwardSqlIsLegalBoundedDdlWithLegacyRefusalFirst(MigrationsSqlGenerationOptions mode)
    {
        using var db = Database(); var sql = db.GetService<IMigrator>().GenerateScript(
            "20261011061648_AddGroupTerminalKeyEnrollment", "20261011075456_AddGroupSignedTerminalCursor", mode);
        new TSql160Parser(true).Parse(new StringReader(sql), out var errors); Assert.Empty(errors);
        var refusal = sql.IndexOf("THROW 51048", StringComparison.Ordinal);
        var firstDdl = sql.IndexOf("ALTER TABLE", StringComparison.Ordinal);
        Assert.InRange(refusal, 0, firstDdl - 1);
        foreach (var forbidden in new[] { "UPDATE [aioffice]", "INSERT INTO [aioffice]", "DELETE FROM [aioffice]", "DROP TABLE", "DROP CONSTRAINT", "NOCHECK", "GRANT CONTROL", "secretref://" }) Assert.DoesNotContain(forbidden, sql);
        Assert.Contains("FK_GroupTerminalFrontierStates_SignedAdvance", sql);
        Assert.Contains("CK_GroupTerminalFrontierStates_Signed", sql);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State); Assert.Empty(db.ChangeTracker.Entries());
    }
}
