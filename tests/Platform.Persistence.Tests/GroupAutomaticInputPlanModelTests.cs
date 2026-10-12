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

public sealed class GroupAutomaticInputPlanModelTests
{
    private static PlatformDbContext Database() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer("Server=127.0.0.1,1;Database=offline_plan_schema;Integrated Security=true;TrustServerCertificate=true").Options);

    [Fact]
    public void OriginalPlanHasOneScopedBatchIdentityBoundedMetadataAndHistoricalClaimEpoch()
    {
        using var db = Database(); var model = db.GetService<IDesignTimeModel>().Model;
        var entity = model.FindEntityType(typeof(GroupAutomaticInputPlanRecord))!;
        Assert.Equal(10, entity.GetProperties().Count());
        Assert.DoesNotContain(entity.GetProperties(), x => x.IsNullable || x.IsShadowProperty());
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "BatchId" }, entity.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.Equal(GroupAutomaticInputPlan.MaximumBytes, entity.FindProperty("Plan")!.GetMaxLength());
        var hash = entity.FindProperty("PlanSha256")!;
        Assert.Equal("char(64)", hash.GetColumnType()); Assert.Equal(64, hash.GetMaxLength());
        Assert.False(hash.IsUnicode()); Assert.True(hash.IsFixedLength()); Assert.Equal("Latin1_General_100_BIN2", hash.GetCollation());
        Assert.Equal(2, entity.GetForeignKeys().Count()); Assert.All(entity.GetForeignKeys(), x => Assert.Equal(DeleteBehavior.Restrict, x.DeleteBehavior));
        var claim = Assert.Single(entity.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(GroupBatchClaimReceiptRecord));
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "OriginalClaimOperationId", "BatchId", "OriginalClaimEpoch" }, claim.Properties.Select(x => x.Name));
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "OperationId", "BatchId", "Epoch" }, claim.PrincipalKey.Properties.Select(x => x.Name));
        Assert.Equal("Group original input plan metadata.", new GroupAutomaticInputPlanRecord { PlanSha256 = "PRIVATE", Plan = [42] }.ToString());
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State); Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void ExpansionPreservesExistingRowsAndForbidsDestructiveOriginalPlanRollback()
    {
        var migration = new AddGroupAutomaticInputPlanStore();
        var table = Assert.Single(migration.UpOperations.OfType<CreateTableOperation>());
        Assert.Equal("GroupAutomaticInputPlans", table.Name); Assert.Equal("aioffice", table.Schema);
        Assert.Equal(3, table.CheckConstraints.Count);
        Assert.Contains(table.CheckConstraints, x => x.Sql.Contains("[FormatVersion]=2") && x.Sql.Contains("BETWEEN 256 AND 32000"));
        Assert.DoesNotContain(migration.UpOperations, x => x is AddColumnOperation or AlterColumnOperation or DropColumnOperation
            or DropTableOperation or InsertDataOperation or UpdateDataOperation or DeleteDataOperation or DropForeignKeyOperation);
        Assert.Throws<NotSupportedException>(() => migration.DownOperations);
    }

    [Fact]
    public void GeneratedMigrationAndEffectiveRightsParseWithCompleteColumnDenialsWithoutConnecting()
    {
        using var db = Database();
        var sql = db.GetService<IMigrator>().GenerateScript("20261011121442_AddGroupModelAttemptJournal", "20261011203117_AddGroupAutomaticInputPlanStore");
        new TSql160Parser(true).Parse(new StringReader(sql), out var errors); Assert.Empty(errors);
        new TSql160Parser(true).Parse(new StringReader(GroupAutomaticInputPlanPermissionVerifier.VerificationSql), out errors); Assert.Empty(errors);
        var grant = Assert.Single(new AddGroupAutomaticInputPlanStore().UpOperations.OfType<SqlOperation>()).Sql;
        Assert.Contains("ALTER AUTHORIZATION ON OBJECT::[aioffice].[GroupAutomaticInputPlans] TO [aioffice_binding_operator_owner]", grant);
        Assert.Contains("GRANT SELECT, INSERT", grant); Assert.Contains("DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP", grant);
        foreach (var property in db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GroupAutomaticInputPlanRecord))!.GetProperties())
            Assert.Contains("[" + property.Name + "]", grant);
        foreach (var permission in new[] { "UPDATE", "DELETE", "ALTER", "CONTROL", "TAKE OWNERSHIP" })
            Assert.Contains("N'" + permission + "')=0", GroupAutomaticInputPlanPermissionVerifier.VerificationSql);
        Assert.Contains("sys.columns", GroupAutomaticInputPlanPermissionVerifier.VerificationSql);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State); Assert.Empty(db.ChangeTracker.Entries());
    }
}
