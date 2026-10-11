extern alias GroupReferenceProof;

using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;
using Guard = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupCursorShadowGuard;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedCursorShadowGuardTests
{
    private static string Operator() => Guard.RequireOperatorConnection(
        "Server=sql;Database=AIOfficeLocal;User ID=sa;Password=" + new string('A', 48) + ";Encrypt=true;TrustServerCertificate=true");

    [Theory]
    [InlineData("Server", "customer-server")]
    [InlineData("Database", "master")]
    [InlineData("User ID", "aioffice_runtime")]
    [InlineData("Password", "short")]
    [InlineData("Encrypt", "false")]
    [InlineData("TrustServerCertificate", "false")]
    [InlineData("Integrated Security", "true")]
    [InlineData("Authentication", "Active Directory Default")]
    [InlineData("Failover Partner", "customer-server")]
    [InlineData("AttachDbFilename", "customer.mdf")]
    [InlineData("Application Intent", "ReadOnly")]
    [InlineData("MultipleActiveResultSets", "true")]
    [InlineData("Pooling", "true")]
    [InlineData("Enlist", "false")]
    public void OperatorRejectsCustomerAndAlternateConnectionBeforeConnect(string key, string value)
    {
        var raw = "Server=sql;Database=AIOfficeLocal;User ID=sa;Password=" + new string('A', 48)
            + ";Encrypt=true;TrustServerCertificate=true;" + key + "=" + value;
        Assert.Throws<InvalidOperationException>(() => Guard.RequireOperatorConnection(raw));
    }

    [Fact]
    public void OperatorNormalizationHasFixedNetworkPrincipalAndNoPoolingOrRetries()
    {
        var value = new SqlConnectionStringBuilder(Operator());
        Assert.Equal("sql", value.DataSource); Assert.Equal("AIOfficeLocal", value.InitialCatalog); Assert.Equal("sa", value.UserID);
        Assert.Equal(10, value.ConnectTimeout); Assert.Equal(0, value.ConnectRetryCount); Assert.False(value.Pooling);
        Assert.False(value.MultipleActiveResultSets); Assert.False(value.IntegratedSecurity);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, value.Encrypt); Assert.True(value.TrustServerCertificate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("0000000000000000000000000000000")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("00000000000000000000000000000000x")]
    [InlineData("0000000000000000000000000000000]")]
    [InlineData("master; DROP DATABASE customer;--")]
    public void ShadowSuffixRejectsIdentifiersOutsideClosedLowerHexGrammar(string suffix) =>
        Assert.Throws<InvalidOperationException>(() => Guard.DatabaseName(suffix));

    [Theory]
    [InlineData("master")]
    [InlineData("AIOfficeLocal")]
    [InlineData("GroupCursorShadow_")]
    [InlineData("GroupCursorShadow_0000000000000000000000000000000A")]
    [InlineData("GroupCursorShadow_00000000000000000000000000000000.extra")]
    public void CreateAndDropRejectEveryArbitraryDatabaseNameBeforeAnyCommand(string name)
    {
        using var master = new SqlConnection(Guard.DatabaseConnection(Operator(), "master"));
        Assert.Throws<InvalidOperationException>(() => Guard.CreateDatabase(master, name));
        Assert.Throws<InvalidOperationException>(() => Guard.DropDatabase(master, name, Guid.NewGuid(), 10));
        Assert.Equal(System.Data.ConnectionState.Closed, master.State);
    }

    [Fact]
    public void EveryActualLifecycleCommandParsesAndBindsTheExactOwnedIdentity()
    {
        var owner = Guid.NewGuid(); var name = Guard.DatabaseName(owner.ToString("N"));
        using var master = new SqlConnection(Guard.DatabaseConnection(Operator(), "master"));
        using var shadow = new SqlConnection(Guard.DatabaseConnection(Operator(), name));
        using var create = Guard.CreateDatabase(master, name);
        using var mark = Guard.MarkOwnership(shadow, name, owner);
        using var drop = Guard.DropDatabase(master, name, owner, 10);
        using var inspect = Guard.InspectOwnership(master, name, owner);
        foreach (var command in new[] { create, mark, drop, inspect })
        {
            new TSql160Parser(true).Parse(new StringReader(command.CommandText), out var errors); Assert.Empty(errors);
            Assert.Equal(30, command.CommandTimeout); Assert.Null(command.Transaction);
            Assert.DoesNotContain(new string('A', 48), command.CommandText);
        }
        Assert.Equal(name, create.Parameters["@database"].Value); Assert.Equal(SqlDbType.NVarChar, create.Parameters["@database"].SqlDbType);
        Assert.Equal(128, create.Parameters["@database"].Size); Assert.Equal(owner.ToString("N"), mark.Parameters["@owner"].Value);
        Assert.Equal(10, drop.Parameters["@id"].Value); Assert.Equal(SqlDbType.Int, drop.Parameters["@id"].SqlDbType);
        Assert.Equal(owner.ToString("N"), drop.Parameters["@owner"].Value);
        Assert.Contains("DB_ID(@database)<>@id", drop.CommandText);
        Assert.Contains("class=0 AND major_id=0 AND minor_id=0", drop.CommandText);
        Assert.Contains("IF DB_ID(@database) IS NOT NULL THROW", drop.CommandText);
        var inspectionSql = Assert.IsType<string>(inspect.Parameters["@inspection"].Value);
        new TSql160Parser(true).Parse(new StringReader(inspectionSql), out var inspectionErrors); Assert.Empty(inspectionErrors);
        Assert.Equal(name, inspect.Parameters["@database"].Value); Assert.Equal(owner.ToString("N"), inspect.Parameters["@owner"].Value);
        Assert.Equal(SqlDbType.NVarChar, inspect.Parameters["@inspection"].SqlDbType); Assert.Equal(-1, inspect.Parameters["@inspection"].Size);
        Assert.Contains("IF DB_ID(@database) IS NULL", inspect.CommandText);
        foreach (var sql in new[] { drop.CommandText, inspectionSql })
        {
            Assert.Contains("SQL_VARIANT_PROPERTY(value,'BaseType')", sql);
            Assert.Contains("SQL_VARIANT_PROPERTY(value,'MaxLength')", sql);
            Assert.Contains("DATALENGTH(CONVERT(nvarchar(max),value))=64", sql);
            Assert.Contains("COLLATE Latin1_General_100_BIN2=@owner COLLATE Latin1_General_100_BIN2", sql);
            Assert.DoesNotContain("CONVERT(nvarchar(32),value)", sql);
        }
        Assert.Equal(ConnectionState.Closed, master.State); Assert.Equal(ConnectionState.Closed, shadow.State);
    }

    [Fact]
    public void CleanupRefusesWrongOwnerIdCatalogOrServerBeforeAnyCommand()
    {
        var owner = Guid.NewGuid(); var name = Guard.DatabaseName(owner.ToString("N"));
        using var master = new SqlConnection(Guard.DatabaseConnection(Operator(), "master"));
        Assert.Throws<InvalidOperationException>(() => Guard.DropDatabase(master, name, Guid.NewGuid(), 10));
        Assert.Throws<InvalidOperationException>(() => Guard.DropDatabase(master, name, owner, 4));
        using var wrong = new SqlConnection(Operator());
        Assert.Throws<InvalidOperationException>(() => Guard.CreateDatabase(wrong, name));
        Assert.Throws<InvalidOperationException>(() => Guard.DropDatabase(wrong, name, owner, 10));
        using var foreign = new SqlConnection(Operator() + ";Server=customer-server");
        Assert.Throws<InvalidOperationException>(() => Guard.CreateDatabase(foreign, name));
        Assert.Throws<InvalidOperationException>(() => Guard.DropDatabase(foreign, name, owner, 10));
        Assert.Equal(ConnectionState.Closed, wrong.State); Assert.Equal(ConnectionState.Closed, foreign.State);
    }
}
