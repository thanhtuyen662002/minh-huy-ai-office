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

public sealed class GroupBatchTerminalModelTests
{
    private static PlatformDbContext Database() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer("Server=127.0.0.1,1;Database=owned_terminal_schema;Integrated Security=true;TrustServerCertificate=true").Options);

    [Fact]
    public void ReceiptStoresBoundedCanonicalManifestWithOneCompletionPerScopedBatchAndInterval()
    {
        using var db = Database(); var model = db.GetService<IDesignTimeModel>().Model;
        var entity = model.FindEntityType(typeof(GroupBatchTerminalReceiptRecord))!;
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "BatchId" }, entity.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.Equal(GroupBatchTerminalManifest.MaximumBytes, entity.FindProperty("Manifest")!.GetMaxLength());
        Assert.Equal("varbinary(8177)", entity.FindProperty("Manifest")!.GetColumnType());
        Assert.Equal(32, entity.FindProperty("ManifestSha256")!.GetMaxLength());
        Assert.Equal("varbinary(32)", entity.FindProperty("ManifestSha256")!.GetColumnType());
        Assert.All(entity.GetProperties(), p => Assert.False(p.IsNullable));
        Assert.DoesNotContain(entity.GetProperties(), p => p.ClrType == typeof(string) || p.IsShadowProperty());
        Assert.Equal(24, entity.GetProperties().Count());
        foreach (var finalColumn in new[] { "OperationId", "AfterSequence" })
            Assert.Contains(entity.GetIndexes(), i => i.IsUnique && i.Properties.Select(p => p.Name)
                .SequenceEqual(new[] { "TenantId", "CompanyId", "BindingId", finalColumn }));
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Fact]
    public void OriginalClaimForeignKeyBindsSameScopeAndBatchWithoutOverstatingEpochAuthority()
    {
        using var db = Database(); var model = db.GetService<IDesignTimeModel>().Model;
        var receipt = model.FindEntityType(typeof(GroupBatchTerminalReceiptRecord))!;
        Assert.Equal(5, receipt.GetForeignKeys().Count());
        Assert.All(receipt.GetForeignKeys(), fk =>
        {
            Assert.Equal(new[] { "TenantId", "CompanyId" }, fk.Properties.Take(2).Select(p => p.Name));
            Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
        });
        var claim = Assert.Single(receipt.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(GroupBatchClaimReceiptRecord));
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "ClaimOperationId" }, claim.Properties.Select(p => p.Name));
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "OperationId" }, claim.PrincipalKey.Properties.Select(p => p.Name));
        Assert.DoesNotContain(claim.Properties, p => p.Name == "ClaimEpoch" || p.Name == "ClaimOwnerId");
        // The owned transaction still has to authenticate all authority fields;
        // this restrictive FK only prevents cross-batch/scope claim substitution.
        var allocation = Assert.Single(receipt.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(GroupBatchAllocationRecord));
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "BatchId" }, allocation.Properties.Select(p => p.Name));
    }

    [Fact]
    public void FrontierIsSeparateScopedCursorWithScopedTerminalAndIngestReferences()
    {
        using var db = Database(); var model = db.GetService<IDesignTimeModel>().Model;
        var frontier = model.FindEntityType(typeof(GroupTerminalFrontierStateRecord))!;
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId" }, frontier.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal(7, frontier.GetProperties().Count());
        Assert.Equal("LastTerminalBatchId", Assert.Single(frontier.GetProperties(), p => p.IsNullable).Name);
        Assert.Equal(4, frontier.GetForeignKeys().Count());
        Assert.All(frontier.GetForeignKeys(), fk => Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior));
        var terminal = Assert.Single(frontier.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(GroupBatchTerminalReceiptRecord));
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "LastTerminalBatchId" }, terminal.Properties.Select(p => p.Name));
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "BatchId" }, terminal.PrincipalKey.Properties.Select(p => p.Name));
        Assert.Contains(frontier.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(GroupSourceStateRecord));
        Assert.DoesNotContain(model.FindEntityType(typeof(GroupSourceStateRecord))!.GetProperties(), p => p.Name.Contains("Terminal", StringComparison.Ordinal));
        Assert.Contains("([ThroughSequence]=0 AND [LastTerminalBatchId] IS NULL)", Assert.Single(frontier.GetCheckConstraints()).Sql);
        Assert.Contains("([ThroughSequence]>0 AND [LastTerminalBatchId] IS NOT NULL)", Assert.Single(frontier.GetCheckConstraints()).Sql);
    }

    [Fact]
    public void ChecksKeepManifestRangeAndAuthorityFiniteAndRequireUtc()
    {
        var receipt = new AddGroupBatchTerminal().UpOperations.OfType<CreateTableOperation>().Single(t => t.Name == "GroupBatchTerminalReceipts");
        var checks = receipt.CheckConstraints.ToDictionary(c => c.Name, c => c.Sql);
        Assert.Equal(3, checks.Count);
        var manifest = checks["CK_GroupBatchTerminalReceipts_Manifest"];
        foreach (var clause in new[] { "[ManifestVersion]=1", "DATALENGTH([Manifest]) BETWEEN 257 AND 8177",
            "SUBSTRING([Manifest],1,8)=0x41494F4754524D31", "DATALENGTH([ManifestSha256])=32", "HASHBYTES('SHA2_256',[Manifest])=[ManifestSha256]" })
            Assert.Contains(clause, manifest);
        var range = checks["CK_GroupBatchTerminalReceipts_Range"];
        foreach (var clause in new[] { "[AfterSequence]>=0", "[ThroughSequence]>[AfterSequence]", "[ThroughSequence]-[AfterSequence]=[RawRevisionCount]",
            "[RawRevisionCount] BETWEEN 1 AND 500", "[SelectedMessageCount] BETWEEN 1 AND 100", "[SelectedMessageCount]<=[RawRevisionCount]",
            "[ContributorCount] BETWEEN 1 AND [SelectedMessageCount]", "[NoteCount] BETWEEN 0 AND [ContributorCount]*40" })
            Assert.Contains(clause, range);
        var authority = checks["CK_GroupBatchTerminalReceipts_Authority"];
        foreach (var field in new[] { "ClaimEpoch", "CredentialEpoch", "GrantVersion", "SourceVersion", "AccountVersion" }) Assert.Contains($"[{field}]>0", authority);
        Assert.Contains("[DeletionGeneration]>=0", authority); Assert.Contains("DATEPART(tz,[CommittedAtUtc])=0", authority);
    }

    [Fact]
    public void MigrationOnlyExpandsTwoTablesAndOneScopedAlternateClaimKeyAndRefusesDestructiveRollback()
    {
        var migration = new AddGroupBatchTerminal(); var operations = migration.UpOperations;
        Assert.Equal(9, operations.Count);
        Assert.Equal(new[] { "GroupBatchTerminalReceipts", "GroupTerminalFrontierStates" }, operations.OfType<CreateTableOperation>().Select(t => t.Name).Order());
        Assert.All(operations.OfType<CreateTableOperation>(), t => Assert.Equal("aioffice", t.Schema));
        var key = Assert.Single(operations.OfType<AddUniqueConstraintOperation>());
        Assert.Equal("aioffice", key.Schema); Assert.Equal("GroupBatchClaimReceipts", key.Table);
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "OperationId" }, key.Columns);
        Assert.Equal(5, operations.OfType<CreateIndexOperation>().Count());
        Assert.Single(operations.OfType<SqlOperation>());
        Assert.DoesNotContain(operations, op => op is AddColumnOperation or AlterColumnOperation or DropColumnOperation or DropTableOperation
            or InsertDataOperation or UpdateDataOperation or DeleteDataOperation);
        Assert.Throws<NotSupportedException>(() => migration.DownOperations);
    }

    [Fact]
    public void FrozenRuntimeRightsDenyAllReceiptColumnsAndOnlyPermitFourCursorColumns()
    {
        var migration = new AddGroupBatchTerminal(); var sql = Assert.Single(migration.UpOperations.OfType<SqlOperation>()).Sql;
        foreach (var table in migration.UpOperations.OfType<CreateTableOperation>())
        {
            Assert.Contains($"ALTER AUTHORIZATION ON OBJECT::[aioffice].[{table.Name}] TO [aioffice_binding_operator_owner];", sql);
            Assert.Contains($"DENY DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::[aioffice].[{table.Name}]", sql.Replace("DENY UPDATE, DELETE", "DENY DELETE", StringComparison.Ordinal));
        }
        Assert.Contains("GRANT SELECT, INSERT ON OBJECT::[aioffice].[GroupBatchTerminalReceipts]", sql);
        Assert.Contains("GRANT SELECT, INSERT, UPDATE ON OBJECT::[aioffice].[GroupTerminalFrontierStates]", sql);
        var receipt = migration.UpOperations.OfType<CreateTableOperation>().Single(t => t.Name == "GroupBatchTerminalReceipts");
        Assert.Contains($"DENY UPDATE ON OBJECT::[aioffice].[GroupBatchTerminalReceipts] ({string.Join(", ", receipt.Columns.Select(c => $"[{c.Name}]"))}) TO [aioffice_binding_runtime];", sql);
        Assert.Contains("DENY UPDATE ON OBJECT::[aioffice].[GroupTerminalFrontierStates] ([TenantId], [CompanyId], [BindingId]) TO [aioffice_binding_runtime];", sql);
        foreach (var forbidden in new[] { "GRANT CONTROL", "GRANT ALTER", "REVOKE", "GroupSourceStates", "GroupBatchClaimReceipts" }) Assert.DoesNotContain(forbidden, sql);
    }

    [Fact]
    public void EffectivePermissionProofRequiresOwnershipAndFailsClosedForAnyExtraImmutableColumn()
    {
        var sql = GroupBatchTerminalPermissionVerifier.VerificationSql;
        foreach (var table in new[] { "GroupBatchTerminalReceipts", "GroupTerminalFrontierStates" })
        {
            Assert.Contains($"OBJECT_ID(N'aioffice.{table}',N'U') IS NOT NULL", sql);
            Assert.Contains($"o.object_id=OBJECT_ID(N'aioffice.{table}') AND o.principal_id=DATABASE_PRINCIPAL_ID(N'aioffice_binding_operator_owner')", sql);
            foreach (var right in new[] { "SELECT", "INSERT" }) Assert.Contains($"HAS_PERMS_BY_NAME(N'aioffice.{table}',N'OBJECT',N'{right}')=1", sql);
            foreach (var right in new[] { "DELETE", "ALTER", "CONTROL", "TAKE OWNERSHIP" }) Assert.Contains($"HAS_PERMS_BY_NAME(N'aioffice.{table}',N'OBJECT',N'{right}')=0", sql);
            Assert.Contains($"HAS_PERMS_BY_NAME(N'aioffice.{table}',N'OBJECT',N'UPDATE',c.name,N'COLUMN'),1)<>0", sql);
        }
        Assert.Contains("HAS_PERMS_BY_NAME(N'aioffice.GroupBatchTerminalReceipts',N'OBJECT',N'UPDATE')=0", sql);
        Assert.Contains("c.object_id=OBJECT_ID(N'aioffice.GroupBatchTerminalReceipts') AND ISNULL", sql);
        Assert.Contains("c.object_id=OBJECT_ID(N'aioffice.GroupTerminalFrontierStates') AND c.name NOT IN (N'ThroughSequence',N'LastTerminalBatchId',N'Version',N'UpdatedAtUtc') AND ISNULL", sql);
        foreach (var column in new[] { "ThroughSequence", "LastTerminalBatchId", "Version", "UpdatedAtUtc" })
            Assert.Contains($"HAS_PERMS_BY_NAME(N'aioffice.GroupTerminalFrontierStates',N'OBJECT',N'UPDATE',N'{column}',N'COLUMN')=1", sql);
    }

    [Fact]
    public void GeneratedForwardSqlAndPermissionProofParseAndSnapshotHasNoUnversionedChanges()
    {
        using var db = Database(); var model = db.GetService<IDesignTimeModel>().Model;
        Assert.False(db.Database.HasPendingModelChanges());
        var sql = string.Join('\n', db.GetService<IMigrationsSqlGenerator>().Generate(new AddGroupBatchTerminal().UpOperations, model).Select(c => c.CommandText));
        foreach (var text in new[] { sql, GroupBatchTerminalPermissionVerifier.VerificationSql })
        {
            new TSql160Parser(true).Parse(new StringReader(text), out var errors); Assert.Empty(errors);
        }
        foreach (var forbidden in new[] { "DROP TABLE", "DROP COLUMN", "ALTER COLUMN", "INSERT INTO", "DELETE FROM", "UPDATE [" }) Assert.DoesNotContain(forbidden, sql, StringComparison.OrdinalIgnoreCase);
        var tables = new AddGroupBatchTerminal().UpOperations.OfType<CreateTableOperation>();
        foreach (var table in tables)
        {
            var entity = model.GetEntityTypes().Single(e => e.GetTableName() == table.Name);
            Assert.Equal(table.CheckConstraints.Count, entity.GetCheckConstraints().Count());
            foreach (var check in table.CheckConstraints) Assert.Equal(check.Sql, entity.GetCheckConstraints().Single(c => c.Name == check.Name).Sql);
        }
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }
}
