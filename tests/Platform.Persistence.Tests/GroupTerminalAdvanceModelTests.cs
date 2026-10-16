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

public sealed class GroupTerminalAdvanceModelTests
{
    private static PlatformDbContext Database() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer("Server=127.0.0.1,1;Database=owned_chain_schema;Integrated Security=true;TrustServerCertificate=true").Options);

    [Fact]
    public void ActualModelUsesBoundedMetadataAndExplicitNullablePredecessorOnly()
    {
        using var db = Database(); var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GroupTerminalAdvanceRecord))!;
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "Version" }, entity.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal(26, entity.GetProperties().Count()); Assert.DoesNotContain(entity.GetProperties(), p => p.IsShadowProperty());
        Assert.Equal(new[] { "PreviousAdvancedAtUtc", "PreviousFingerprint", "PreviousThroughSequence", "PreviousVersion" },
            entity.GetProperties().Where(p => p.IsNullable).Select(p => p.Name).Order());
        Assert.Equal("varbinary(385)", entity.FindProperty("Envelope")!.GetColumnType()); Assert.Equal(385, entity.FindProperty("Envelope")!.GetMaxLength());
        foreach (var p in new[] { "Fingerprint", "PreviousFingerprint", "AuthoritySha256", "TerminalManifestSha256" })
        { Assert.Equal("varbinary(32)", entity.FindProperty(p)!.GetColumnType()); Assert.Equal(32, entity.FindProperty(p)!.GetMaxLength()); }
        Assert.Equal(64, entity.FindProperty("KeyId")!.GetMaxLength()); Assert.False(entity.FindProperty("KeyId")!.IsUnicode());
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State); Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void SelfAndTerminalForeignKeysBindCompleteExactScopedIdentity()
    {
        using var db = Database(); var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GroupTerminalAdvanceRecord))!;
        Assert.Equal(6, entity.GetForeignKeys().Count()); Assert.All(entity.GetForeignKeys(), fk => Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior));
        var previous = Assert.Single(entity.GetForeignKeys(), fk => fk.PrincipalEntityType == entity);
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "PreviousVersion", "PreviousThroughSequence", "PreviousFingerprint", "PreviousAdvancedAtUtc" },
            previous.Properties.Select(p => p.Name));
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "Version", "ThroughSequence", "Fingerprint", "AdvancedAtUtc" },
            previous.PrincipalKey.Properties.Select(p => p.Name));
        var terminal = Assert.Single(entity.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(GroupBatchTerminalReceiptRecord));
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "TerminalOperationId", "AfterSequence", "ThroughSequence", "TerminalManifestSha256", "TerminalCommittedAtUtc" },
            terminal.Properties.Select(p => p.Name));
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "OperationId", "AfterSequence", "ThroughSequence", "ManifestSha256", "CommittedAtUtc" },
            terminal.PrincipalKey.Properties.Select(p => p.Name));
        foreach (var field in new[] { "AfterSequence", "BatchId" }) Assert.Contains(entity.GetIndexes(), i => i.IsUnique && i.Properties.Select(p => p.Name)
            .SequenceEqual(new[] { "TenantId", "CompanyId", "BindingId", field }));
    }

    [Fact]
    public void MigrationExpandsWithoutMutatingExistingCursorOrCreatingTrustedHistory()
    {
        var operations = new AddGroupSignedTerminalAdvances().UpOperations;
        Assert.Equal("GroupTerminalAdvances", Assert.Single(operations.OfType<CreateTableOperation>()).Name);
        Assert.Equal("GroupBatchTerminalReceipts", Assert.Single(operations.OfType<AddUniqueConstraintOperation>()).Table);
        Assert.DoesNotContain(operations, op => op is AddColumnOperation or AlterColumnOperation or DropColumnOperation or DropTableOperation
            or InsertDataOperation or UpdateDataOperation or DeleteDataOperation);
        Assert.DoesNotContain(operations.OfType<CreateIndexOperation>(), op => op.Table == "GroupTerminalFrontierStates");
        Assert.Throws<NotSupportedException>(() => new AddGroupSignedTerminalAdvances().DownOperations);
    }

    [Fact]
    public void PredecessorCheckExplicitlyClosesEveryPartialNullAndBackwardClockAlternative()
    {
        var table = new AddGroupSignedTerminalAdvances().UpOperations.OfType<CreateTableOperation>().Single();
        var check = table.CheckConstraints.Single(x => x.Name == "CK_GroupTerminalAdvances_Predecessor").Sql;
        foreach (var field in new[] { "PreviousVersion", "PreviousThroughSequence", "PreviousFingerprint", "PreviousAdvancedAtUtc" })
        { Assert.Contains("[" + field + "] IS NULL", check); Assert.Contains("[" + field + "] IS NOT NULL", check); }
        foreach (var fragment in new[] { "[Version]=1 AND [AfterSequence]=0", "[Version]>1 AND [AfterSequence]>0",
            "[PreviousVersion]=[Version]-1", "[PreviousThroughSequence]=[AfterSequence]", "DATALENGTH([PreviousFingerprint])=32",
            "DATEPART(tz,[PreviousAdvancedAtUtc])=0", "[PreviousAdvancedAtUtc]<=[AdvancedAtUtc]" }) Assert.Contains(fragment, check);
    }

    [Fact]
    public void GeneratedExpansionAndEffectiveColumnRightsProofParseWithoutOpeningSql()
    {
        using var db = Database(); var sql = db.GetService<IMigrator>().GenerateScript("20261011025104_AddGroupBatchTerminal", "20261011054423_AddGroupSignedTerminalAdvances");
        new TSql160Parser(true).Parse(new StringReader(sql), out var errors); Assert.Empty(errors);
        new TSql160Parser(true).Parse(new StringReader(GroupTerminalAdvancePermissionVerifier.VerificationSql), out errors); Assert.Empty(errors);
        Assert.DoesNotContain("UPDATE [aioffice].[GroupTerminalFrontierStates]", sql);
        var grants = Assert.Single(new AddGroupSignedTerminalAdvances().UpOperations.OfType<SqlOperation>()).Sql;
        foreach (var property in db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GroupTerminalAdvanceRecord))!.GetProperties())
            Assert.Contains("[" + property.Name + "]", grants);
        Assert.Contains("GRANT SELECT, INSERT", grants); Assert.Contains("DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP", grants);
        Assert.Contains("sys.columns", GroupTerminalAdvancePermissionVerifier.VerificationSql);
        Assert.Contains("ISNULL(HAS_PERMS_BY_NAME", GroupTerminalAdvancePermissionVerifier.VerificationSql);
        foreach (var permission in new[] { "UPDATE", "DELETE", "ALTER", "CONTROL", "TAKE OWNERSHIP" })
            Assert.Contains("N'" + permission + "')=0", GroupTerminalAdvancePermissionVerifier.VerificationSql);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State); Assert.Empty(db.ChangeTracker.Entries());
    }
}
