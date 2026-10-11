extern alias GroupReferenceProof;

using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;
using Probes = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupCursorLegacyProbes;
using Guard = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupCursorShadowGuard;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedCursorLegacyProbeTests
{
    private static SqlConnection Shadow() => new(Guard.DatabaseConnection(Guard.RequireOperatorConnection(
        "Server=sql;Database=AIOfficeLocal;User ID=sa;Password=" + new string('A', 48) + ";Encrypt=true;TrustServerCertificate=true"),
        Guard.DatabaseName(Guid.NewGuid().ToString("N"))));

    [Fact]
    public void ActualMigrationIdsExistAndPredecessorIsTheImmediatelyPreviousFrozenMigration()
    {
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=shadow_legacy_cold;Integrated Security=true;TrustServerCertificate=true").Options);
        var migrations = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToArray();
        Assert.Equal(Probes.Target, migrations[^1]); Assert.Equal(Probes.Predecessor, migrations[^2]);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Fact]
    public void AllActualLegacyProbesAreClosedSqlAndBindOnlyThreeTypedScopeValues()
    {
        var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        using var shadow = Shadow();
        var commands = Probes.MigrationRefusals(shadow, scope).Concat(Probes.LegacyConstraintRefusals(shadow, scope))
            .Concat([Probes.RestoreMatchingCursor(shadow, scope), Probes.RemoveAdvance(shadow, scope)]).ToArray();
        Assert.Equal(8, commands.Length);
        foreach (var command in commands)
        {
            using (command)
            {
                new TSql160Parser(true).Parse(new StringReader(command.CommandText), out var errors); Assert.Empty(errors);
                Assert.Equal(30, command.CommandTimeout); Assert.Equal(3, command.Parameters.Count);
                Assert.Equal(new object[] { scope.TenantId, scope.CompanyId, scope.SourceBindingId }, command.Parameters.Cast<SqlParameter>().Select(p => p.Value));
                Assert.All(command.Parameters.Cast<SqlParameter>(), p => Assert.Equal(SqlDbType.UniqueIdentifier, p.SqlDbType));
                Assert.DoesNotContain(scope.TenantId.ToString("D"), command.CommandText);
                Assert.Contains("[TenantId]=@tenant", command.CommandText);
                Assert.Contains("[CompanyId]=@company", command.CommandText);
                Assert.Contains("[BindingId]=@binding", command.CommandText);
            }
        }
        Assert.Equal(ConnectionState.Closed, shadow.State);
    }

    [Fact]
    public void RefusalCasesPreserveTheOldCheckDomainAndDistinguishItsOwnInvalidValues()
    {
        using var shadow = Shadow(); var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var migration = Probes.MigrationRefusals(shadow, scope); var legacy = Probes.LegacyConstraintRefusals(shadow, scope);
        try
        {
            Assert.Equal(4, migration.Length); Assert.Equal(2, legacy.Length);
            Assert.Contains("[ThroughSequence]=0,[LastTerminalBatchId]=NULL", migration[0].CommandText);
            Assert.Contains("[Version]=[Version]+1", migration[1].CommandText);
            Assert.Contains("[ThroughSequence]=[ThroughSequence]+1", migration[2].CommandText);
            Assert.Contains("DATEADD(nanosecond,100,[UpdatedAtUtc])", migration[3].CommandText);
            Assert.Contains("[Version]=0", legacy[0].CommandText);
            Assert.Contains("SWITCHOFFSET([UpdatedAtUtc],'+01:00')", legacy[1].CommandText);
            Assert.All(migration, c => Assert.DoesNotContain("SWITCHOFFSET", c.CommandText));
        }
        finally { foreach (var command in migration.Concat(legacy)) command.Dispose(); }
    }

    [Theory]
    [InlineData("AIOfficeLocal")]
    [InlineData("master")]
    [InlineData("CustomerERP")]
    public void EveryMutationRefusesAnOriginalOrArbitraryCatalogBeforeCommandCreation(string catalog)
    {
        using var connection = new SqlConnection("Server=sql;Database=" + catalog + ";User ID=sa;Password=" + new string('A', 48));
        var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() => Probes.MigrationRefusals(connection, scope));
        Assert.Throws<InvalidOperationException>(() => Probes.LegacyConstraintRefusals(connection, scope));
        Assert.Throws<InvalidOperationException>(() => Probes.RestoreMatchingCursor(connection, scope));
        Assert.Throws<InvalidOperationException>(() => Probes.RemoveAdvance(connection, scope));
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void EveryActualProbeHasItsOwnFreshNormalizedConnectionAndNeverOpensTheSharedTemplate()
    {
        using var template = Shadow(); var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var commands = Probes.MigrationRefusals(template, scope).Concat(Probes.LegacyConstraintRefusals(template, scope)).ToArray();
        try
        {
            Assert.Equal(6, commands.Length);
            Assert.Equal(6, commands.Select(x => x.Connection).Distinct().Count());
            foreach (var command in commands)
            {
                Assert.NotNull(command.Connection); Assert.NotSame(template, command.Connection);
                Assert.Equal(template.ConnectionString, command.Connection.ConnectionString);
                Assert.Equal(ConnectionState.Closed, command.Connection.State);
                Guard.RequireShadowConnection(command.Connection);
            }
            Assert.Equal(ConnectionState.Closed, template.State);
        }
        finally { foreach (var command in commands) { command.Connection?.Dispose(); command.Dispose(); } }
    }
}
