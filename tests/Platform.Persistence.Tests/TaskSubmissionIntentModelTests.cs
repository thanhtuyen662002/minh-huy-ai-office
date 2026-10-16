using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using MinhHuy.AIOffice.Platform.Persistence.Migrations;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class TaskSubmissionIntentModelTests
{
    [Fact]
    public void AdditiveMigrationPreservesTaskIndexesAndProtectsOwnerBoundImmutableIntentHistory()
    {
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=localhost;Database=Intent_Model;User Id=test;Password=test;TrustServerCertificate=true").Options);
        var model = db.GetService<IDesignTimeModel>().Model;
        var intent = model.FindEntityType(typeof(TaskSubmissionIntentRecord))!;
        Assert.Equal(new[] { "TenantId", "CompanyId", "UserId", "OperationId" }, intent.FindPrimaryKey()!.Properties.Select(row => row.Name));
        var owner = Assert.Single(intent.GetForeignKeys()); Assert.Equal(typeof(CompanyMembershipRecord), owner.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, owner.DeleteBehavior);
        var index = Assert.Single(intent.GetIndexes()); Assert.Equal(new[] { false, false, false, true, true }, index.IsDescending);
        Assert.Equal(4000, intent.FindProperty("Question")!.GetMaxLength()); Assert.Equal("Latin1_General_100_BIN2", intent.FindProperty("Question")!.GetCollation());
        Assert.Contains(model.FindEntityType(typeof(TaskRecord))!.GetIndexes(), item => item.Properties.Select(property => property.Name)
            .SequenceEqual(new[] { "TenantId", "CompanyId", "CreatedByUserId", "CreatedAtUtc", "Id" }));
        var migration = new AddTaskSubmissionIntents();
        var sql = string.Join("\n", db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, model).Select(command => command.CommandText));
        Assert.Contains("CREATE TABLE [aioffice].[TaskSubmissionIntents]", sql);
        Assert.Contains("GRANT SELECT, INSERT", sql); Assert.Contains("DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP", sql);
        Assert.Contains("[Question], [InputFingerprint], [CreatedAtUtc], [ExpiresAtUtc]", sql);
        Assert.DoesNotContain("DROP TABLE", sql); Assert.DoesNotContain("ALTER TABLE [aioffice].[Tasks]", sql);
        Assert.Throws<NotSupportedException>(() => migration.DownOperations);
    }
}
