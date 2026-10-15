extern alias GroupReferenceProof;

using System.Data;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Sql = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupShadowCursorSql;
using Proof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupSignedCursorMigrationRuntimeProof;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedCursorSnapshotTagTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryActualGeneratedSnapshotTagPassesTheExactRuntimeReader(bool shadow)
    {
        using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(
            "Server=sql;Database=AIOfficeLocal;User ID=aioffice_runtime;Password=synthetic;Encrypt=true;TrustServerCertificate=true").Options);
        var templates = shadow ? Sql.ShadowSnapshots(database, Sql.NewShadow()) : Sql.SourceSnapshots(database);
        Assert.Equal(shadow ? 72 : 77, templates.Count);
        try
        {
            foreach (var template in templates)
            {
                // Feed the literal actually returned by the generated SQL into
                // the actual strict reader. Substituting template.Tag here
                // would hide a producer/consumer mismatch.
                var matches = Regex.Matches(template.Command.CommandText, @"SELECT N'([A-Za-z0-9_:]+)' AS \[Tag\]");
                var tag = Assert.Single(matches.Cast<Match>()).Groups[1].Value;
                using var table = new DataTable(); table.Columns.Add("Tag", typeof(string)); table.Columns.Add("Sha256", typeof(string));
                table.Rows.Add(tag, new string('A', 64));
                await using var reader = table.CreateDataReader();
                Assert.Equal(new string('A', 64), await Proof.ReadSnapshotHashAsync(reader, template.Tag, default));
            }
        }
        finally { foreach (var template in templates) template.Command.Dispose(); }
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        Assert.Empty(database.ChangeTracker.Entries());
    }
}
