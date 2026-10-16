using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MinhHuy.AIOffice.Platform.Persistence.Migrations;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupAutomaticNoteCapacityMigrationTests
{
    private static PlatformDbContext Database() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer("Server=localhost;Database=Group_Design_Only;Integrated Security=true;TrustServerCertificate=true").Options);

    [Fact]
    public void ExpansionChangesOnlyFourFiniteBoundsAndPreservesEveryOtherClause()
    {
        using var db = Database();
        var migration = new ExpandGroupAutomaticNoteCapacity();
        Assert.Equal(8, migration.UpOperations.Count);
        var drops = migration.UpOperations.OfType<DropCheckConstraintOperation>().ToArray();
        var adds = migration.UpOperations.OfType<AddCheckConstraintOperation>().ToArray();
        Assert.Equal(4, drops.Length); Assert.Equal(4, adds.Length);
        var original = new AddGroupWorkNotes().UpOperations.OfType<CreateTableOperation>().ToDictionary(x => x.Name);
        var model = db.GetService<IDesignTimeModel>().Model;
        foreach (var add in adds)
        {
            var drop = Assert.Single(drops, x => x.Name == add.Name);
            Assert.Equal("aioffice", drop.Schema); Assert.Equal(drop.Schema, add.Schema); Assert.Equal(drop.Table, add.Table);
            var old = original[add.Table].CheckConstraints.Single(x => x.Name == add.Name).Sql;
            Assert.Contains("BETWEEN 1 AND 20", old);
            Assert.Equal(old.Replace("BETWEEN 1 AND 20", "BETWEEN 1 AND 40", StringComparison.Ordinal), add.Sql);
            Assert.Equal(add.Sql, model.GetEntityTypes().Single(x => x.GetTableName() == add.Table)
                .GetCheckConstraints().Single(x => x.Name == add.Name).Sql);
        }
        Assert.Equal(new[] { "GroupCustomerRequests", "GroupNotesCommittedItems", "GroupNotesCommittedOutbox", "GroupWorkCommitReceipts" },
            adds.Select(x => x.Table).Order(StringComparer.Ordinal));
        Assert.Equal(40, GroupAutomaticNotePlan.MaximumNotes);
        Assert.Throws<NotSupportedException>(() => migration.DownOperations);
    }

    [Fact]
    public void GeneratedForwardSqlAndModelHaveNoUnversionedColumnsRightsOrDataMutation()
    {
        using var db = Database();
        Assert.False(db.Database.HasPendingModelChanges());
        var sql = string.Join("\n", db.GetService<IMigrationsSqlGenerator>()
            .Generate(new ExpandGroupAutomaticNoteCapacity().UpOperations, db.GetService<IDesignTimeModel>().Model).Select(x => x.CommandText));
        using var reader = new StringReader(sql); new TSql160Parser(true).Parse(reader, out var errors); Assert.Empty(errors);
        foreach (var forbidden in new[] { "CREATE TABLE", "DROP TABLE", "DROP COLUMN", "ALTER COLUMN", "GRANT ", "DENY ", "REVOKE ", "INSERT ", "UPDATE ", "DELETE " })
            Assert.DoesNotContain(forbidden, sql, StringComparison.OrdinalIgnoreCase);
    }
}
