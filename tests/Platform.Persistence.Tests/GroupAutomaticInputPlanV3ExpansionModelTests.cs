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

public sealed class GroupAutomaticInputPlanV3ExpansionModelTests
{
    private static PlatformDbContext Database() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer("Server=127.0.0.1,1;Database=offline_plan_v3_schema;Integrated Security=true;TrustServerCertificate=true").Options);

    [Fact]
    public void InputPlanV3ExpansionChangesOnlyTheFormatConstraintAndRefusesContractRollback()
    {
        var migration = new ExpandOriginalInputPlanV3();
        Assert.Equal(2, migration.UpOperations.Count);
        var drop = Assert.IsType<DropCheckConstraintOperation>(migration.UpOperations[0]);
        var add = Assert.IsType<AddCheckConstraintOperation>(migration.UpOperations[1]);
        Assert.Equal("aioffice", drop.Schema); Assert.Equal("GroupAutomaticInputPlans", drop.Table);
        Assert.Equal("CK_GroupAutomaticInputPlans_Format", drop.Name);
        Assert.Equal(drop.Schema, add.Schema); Assert.Equal(drop.Table, add.Table); Assert.Equal(drop.Name, add.Name);
        Assert.Equal("[FormatVersion] IN (2,3) AND DATALENGTH([Plan]) BETWEEN 256 AND 32000", add.Sql);
        Assert.Throws<NotSupportedException>(() => migration.DownOperations);
    }

    [Fact]
    public void InputPlanV3ExpansionGeneratedSqlParsesWithoutDataRewriteOrRightsChange()
    {
        using var db = Database();
        var sql = db.GetService<IMigrator>().GenerateScript("20261012024331_AddGroupModelAttemptResults", "20261014050545_ExpandOriginalInputPlanV3");
        new TSql160Parser(true).Parse(new StringReader(sql), out var errors); Assert.Empty(errors);
        Assert.Contains("DROP CONSTRAINT [CK_GroupAutomaticInputPlans_Format]", sql);
        Assert.Contains("CHECK ([FormatVersion] IN (2,3) AND DATALENGTH([Plan]) BETWEEN 256 AND 32000)", sql);
        foreach (var text in new[] { "UPDATE [aioffice]", "DELETE FROM [aioffice]", "INSERT INTO [aioffice]", "ALTER COLUMN", "DROP TABLE", "GRANT ", "DENY ", "ALTER AUTHORIZATION" })
            Assert.DoesNotContain(text, sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State); Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void InputPlanV3ExpansionKeepsScopedIdentityHistoricalReceiptSizeAndImmutableRowShape()
    {
        using var db = Database(); var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GroupAutomaticInputPlanRecord))!;
        Assert.Equal(10, entity.GetProperties().Count()); Assert.Equal(3, entity.GetCheckConstraints().Count());
        Assert.Contains(entity.GetCheckConstraints(), x => x.Name == "CK_GroupAutomaticInputPlans_Format"
            && x.Sql == "[FormatVersion] IN (2,3) AND DATALENGTH([Plan]) BETWEEN 256 AND 32000");
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "BatchId" }, entity.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.Equal(32000, entity.FindProperty("Plan")!.GetMaxLength());
        Assert.All(entity.GetForeignKeys(), x => Assert.Equal(DeleteBehavior.Restrict, x.DeleteBehavior));
        var receipt = Assert.Single(entity.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(GroupBatchClaimReceiptRecord));
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "OriginalClaimOperationId", "BatchId", "OriginalClaimEpoch" }, receipt.Properties.Select(x => x.Name));
        Assert.DoesNotContain(entity.GetProperties(), x => x.IsNullable || x.IsShadowProperty());
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State); Assert.Empty(db.ChangeTracker.Entries());
    }
}
