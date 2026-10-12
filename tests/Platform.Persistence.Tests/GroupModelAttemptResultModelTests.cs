using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MinhHuy.AIOffice.Platform.Persistence.Migrations;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupModelAttemptResultModelTests
{
    private static PlatformDbContext Database() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer("Server=127.0.0.1,1;Database=offline_result_schema;Integrated Security=true;TrustServerCertificate=true").Options);

    [Fact]
    public void Encrypted_result_has_full_scoped_original_plan_and_start_foreign_keys()
    {
        using var db = Database();
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GroupModelAttemptResultRecord))!;
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "RequestId" }, entity.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.DoesNotContain(entity.GetProperties(), x => x.IsNullable || x.IsShadowProperty() || x.Name == "Result");
        Assert.Equal("varbinary(max)", entity.FindProperty("ProtectedResult")!.GetColumnType());
        Assert.Equal(66029, entity.FindProperty("ProtectedResult")!.GetMaxLength());
        var plan = Assert.Single(entity.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(GroupAutomaticInputPlanRecord));
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "PlanSha256" }, plan.Properties.Select(x => x.Name));
        var start = Assert.Single(entity.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(GroupModelAttemptStartRecord));
        Assert.Equal(new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "InputSha256", "RequestId", "StartedAtUtc", "DeadlineUtc" }, start.Properties.Select(x => x.Name));
        Assert.All(entity.GetForeignKeys(), x => Assert.Equal(DeleteBehavior.Restrict, x.DeleteBehavior));
        Assert.Equal("Group model attempt result (private SQL record).", new GroupModelAttemptResultRecord { ProtectedResult = [42] }.ToString());
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Fact]
    public void Additive_migration_and_effective_rights_parse_and_cover_every_column_without_connecting()
    {
        using var db = Database();
        var migration = new AddGroupModelAttemptResults();
        Assert.Single(migration.UpOperations.OfType<CreateTableOperation>());
        Assert.Equal(2, migration.UpOperations.OfType<AddUniqueConstraintOperation>().Count());
        Assert.DoesNotContain(migration.UpOperations, x => x is DropTableOperation or DropColumnOperation or AlterColumnOperation
            or InsertDataOperation or UpdateDataOperation or DeleteDataOperation);
        Assert.Throws<NotSupportedException>(() => migration.DownOperations);
        var script = db.GetService<IMigrator>().GenerateScript("20261011203117_AddGroupAutomaticInputPlanStore", "20261012024331_AddGroupModelAttemptResults");
        new TSql160Parser(true).Parse(new StringReader(script), out var errors); Assert.Empty(errors);
        new TSql160Parser(true).Parse(new StringReader(GroupModelAttemptResultPermissionVerifier.VerificationSql), out errors); Assert.Empty(errors);
        var rights = Assert.Single(migration.UpOperations.OfType<SqlOperation>()).Sql;
        Assert.Contains("ALTER AUTHORIZATION", rights); Assert.Contains("GRANT SELECT, INSERT", rights);
        Assert.Contains("DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP", rights);
        foreach (var property in db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GroupModelAttemptResultRecord))!.GetProperties())
            Assert.Contains("[" + property.Name + "]", rights);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Fact]
    public void Actual_bounded_payload_query_translates_to_server_case_and_all_scope_parameters()
    {
        using var db = Database();
        var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var sql = GroupModelAttemptResultStore.BoundedResultQuery(db, scope, Guid.NewGuid()).ToQueryString();
        Assert.Contains("CASE", sql); Assert.Contains("DATALENGTH", sql); Assert.Contains("229", sql); Assert.Contains("66029", sql);
        // SQL Server returns NULL for an omitted ELSE. EF may omit the explicit
        // clause; either form must still refuse an out-of-bound payload.
        var parsed = new TSql160Parser(true).Parse(new StringReader(sql), out var errors);
        Assert.Empty(errors);
        var cases = new CaseCollector(); parsed.Accept(cases);
        var bounded = Assert.Single(cases.Expressions);
        Assert.True(bounded.ElseExpression is null or NullLiteral);
        foreach (var column in new[] { "TenantId", "CompanyId", "BindingId", "RequestId" }) Assert.Contains("[" + column + "] = @", sql);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State); Assert.Empty(db.ChangeTracker.Entries());
    }

    private sealed class CaseCollector : TSqlFragmentVisitor
    {
        internal List<SearchedCaseExpression> Expressions { get; } = [];
        public override void ExplicitVisit(SearchedCaseExpression node)
        { Expressions.Add(node); base.ExplicitVisit(node); }
    }
}
