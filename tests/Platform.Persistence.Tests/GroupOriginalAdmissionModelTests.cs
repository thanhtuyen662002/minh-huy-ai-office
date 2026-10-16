using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MinhHuy.AIOffice.Platform.Persistence.Migrations;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupOriginalAdmissionModelTests
{
    private static PlatformDbContext Database() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer("Server=127.0.0.1,1;Database=offline_admission_schema;Integrated Security=true;Pooling=false").Options);

    [Fact]
    public void Admission_metadata_has_full_scope_plan_hash_original_claim_and_restrictive_foreign_keys()
    {
        using var database = Database();
        var entity = database.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GroupOriginalAdmissionSidecarRecord))!;
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "BatchId" }, entity.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.DoesNotContain(entity.GetProperties(), p => p.IsNullable || p.IsShadowProperty());
        Assert.Equal(GroupOriginalAdmissionSidecar.MaximumBytes, entity.FindProperty("Sidecar")!.GetMaxLength());
        Assert.Equal("varbinary(max)", entity.FindProperty("Sidecar")!.GetColumnType());
        var plan = Assert.Single(entity.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(GroupAutomaticInputPlanRecord));
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "PlanSha256" }, plan.Properties.Select(p => p.Name));
        var claim = Assert.Single(entity.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(GroupBatchClaimReceiptRecord));
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "OriginalClaimOperationId", "BatchId", "OriginalClaimEpoch" }, claim.Properties.Select(p => p.Name));
        Assert.All(entity.GetForeignKeys(), key => Assert.Equal(DeleteBehavior.Restrict, key.DeleteBehavior));
        Assert.Equal(3, entity.GetCheckConstraints().Count());
        Assert.DoesNotContain(entity.GetProperties(), p => p.Name is "Input" or "Schema" or "Authorization" or "Endpoint");
    }

    [Fact]
    public void Admission_migration_only_adds_append_only_provenance_and_requires_forward_repair()
    {
        var migration = new AddGroupOriginalAdmissionSidecars(); var operations = migration.UpOperations;
        var table = Assert.Single(operations.OfType<CreateTableOperation>());
        Assert.Equal("GroupOriginalAdmissionSidecars", table.Name); Assert.Equal("aioffice", table.Schema);
        Assert.Equal(11, table.Columns.Count);
        Assert.DoesNotContain(operations, op => op is AlterColumnOperation or DropTableOperation or DropColumnOperation or UpdateDataOperation);
        var sql = string.Join("\n", operations.OfType<SqlOperation>().Select(op => op.Sql));
        Assert.Contains("ALTER AUTHORIZATION", sql, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT, INSERT", sql, StringComparison.Ordinal);
        Assert.Contains("DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP", sql, StringComparison.Ordinal);
        foreach (var column in table.Columns) Assert.Contains("[" + column.Name + "]", sql, StringComparison.Ordinal);
        using var text = new StringReader(sql); new TSql160Parser(false).Parse(text, out var errors); Assert.Empty(errors);
        Assert.Throws<NotSupportedException>(() => migration.DownOperations);
    }

    [Fact]
    public void New_admission_permission_proof_is_not_added_to_legacy_original_or_result_recovery()
    {
        var own = GroupOriginalAdmissionPermissionVerifier.VerificationSql;
        Assert.Contains("GroupOriginalAdmissionSidecars", own, StringComparison.Ordinal);
        foreach (var permission in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "ALTER", "CONTROL", "TAKE OWNERSHIP", "COLUMN" })
            Assert.Contains(permission, own, StringComparison.Ordinal);
        Assert.DoesNotContain("GroupOriginalAdmissionSidecars", GroupAutomaticInputPlanPermissionVerifier.EffectiveVerificationSql, StringComparison.Ordinal);
        Assert.DoesNotContain("GroupOriginalAdmissionSidecars", GroupModelAttemptResultPermissionVerifier.EffectiveVerificationSql, StringComparison.Ordinal);
        using var text = new StringReader(GroupOriginalAdmissionPermissionVerifier.EffectiveVerificationSql);
        new TSql160Parser(false).Parse(text, out var errors); Assert.Empty(errors);
    }
}
