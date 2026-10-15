extern alias GroupReferenceProof;

using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;
using Sql = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupShadowCursorSql;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedCursorFrozenTargetModelTests
{
    [Fact]
    public void CurrentSourceIncludesEveryJournalTableWhileShadowUsesTheActualFrozenMigrationModel()
    {
        using var database = ReviewerShadowCursorSqlPrototypeTests.ColdDatabase();
        var assembly = database.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations[Sql.CursorMigration], database.Database.ProviderName!);
        var frozen = database.GetService<IModelRuntimeInitializer>().Initialize(migration.TargetModel, designTime: true);
        var current = database.GetService<IDesignTimeModel>().Model.GetRelationalModel().Tables.Select(x => x.Name).Order().ToArray();
        var historical = frozen.GetRelationalModel().Tables.Select(x => x.Name).Order().ToArray();
        Assert.Equal(60, current.Length);
        Assert.Equal(54, historical.Length);
        var source = Sql.SourceSnapshots(database);
        var shadow = Sql.ShadowSnapshots(database, Sql.NewShadow());
        try
        {
            Assert.Equal(78, source.Count); Assert.Equal(72, shadow.Count);
            Assert.Equal(current.Concat(new[] { "ToolExecutionAudit", "CustomerAiCreditSettlements", "CustomerSlaPriorityAdmissions" }).Order(), source.Where(x => x.Tag.StartsWith("rows:", StringComparison.Ordinal)
                && x.Tag != "rows:__EFMigrationsHistory").Select(x => x.Tag[5..]).Order());
            Assert.Equal(historical.Concat(new[] { "ToolExecutionAudit", "CustomerAiCreditSettlements", "CustomerSlaPriorityAdmissions" }).Order(), shadow.Where(x => x.Tag.StartsWith("rows:", StringComparison.Ordinal)
                && x.Tag != "rows:__EFMigrationsHistory").Select(x => x.Tag[5..]).Order());
            Assert.Equal(new[] { "GroupAutomaticInputPlans", "GroupModelAttemptAdmissions", "GroupModelAttemptFailures", "GroupModelAttemptResults", "GroupModelAttemptStarts", "GroupOriginalAdmissionSidecars" }, current.Except(historical).Order());
            foreach (var name in new[] { "GroupAutomaticInputPlans", "GroupModelAttemptAdmissions", "GroupModelAttemptFailures", "GroupModelAttemptResults", "GroupModelAttemptStarts", "GroupOriginalAdmissionSidecars" })
            {
                Assert.Contains("(N'" + name + "')", source[0].Command.CommandText);
                Assert.DoesNotContain("(N'" + name + "')", shadow[0].Command.CommandText);
            }
            // Both inventories still refuse every unexpected or missing table in both directions.
            Assert.All(new[] { source[0], shadow[0] }, item =>
            {
                Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(item.Command.CommandText, @"\bEXCEPT\b").Count);
                Assert.Contains("Owned shadow inventory refused", item.Command.CommandText);
            });
            foreach (var item in source.Concat(shadow))
            {
                new TSql160Parser(true).Parse(new StringReader(item.Command.CommandText), out var errors);
                Assert.Empty(errors);
            }
        }
        finally { foreach (var item in source.Concat(shadow)) item.Command.Dispose(); }
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        Assert.Empty(database.ChangeTracker.Entries());
    }
}
