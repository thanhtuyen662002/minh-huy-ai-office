extern alias GroupReferenceProof;

using System.Data;
using System.Reflection;
using GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedFirstHostBatchReadObservationTests
{
    private const string Accepted = "SELECT 1 FROM [aioffice].[GroupModelAttemptResults]";
    private const string Terminal = "SELECT 1 FROM [aioffice].[GroupBatchTerminalReceipts]";
    private const string Original = "SELECT 1 FROM aioffice.GroupAutomaticInputPlans";

    [Fact]
    public void ActualEfRawSqlTranslationKeepsTheUnbracketedOriginalReaderAndIsObserved()
    {
        // Offline translation using the actual private stored-plan projection;
        // no SQL connection is opened and no positive SQL proof is inferred.
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=unused.invalid;Database=owned-translation;Integrated Security=true;Encrypt=true").Options);
        var type = typeof(GroupAutomaticInputPlanStore).GetNestedType("StoredPlan", BindingFlags.NonPublic);
        Assert.NotNull(type);
        var method = typeof(RelationalDatabaseFacadeExtensions).GetMethods().Single(x => x.Name == "SqlQueryRaw"
            && x.IsGenericMethodDefinition && x.GetParameters().Length == 3);
        var query = (IQueryable)method.MakeGenericMethod(type).Invoke(null, [db.Database, """
            SELECT TenantId,CompanyId,BindingId,BatchId,FormatVersion,PlanSha256,OriginalClaimOperationId,OriginalClaimEpoch,CreatedAtUtc,
              CASE WHEN DATALENGTH([Plan]) BETWEEN 256 AND 32000 THEN [Plan] END AS [Plan]
            FROM aioffice.GroupAutomaticInputPlans
            """, Array.Empty<object>()])!;
        var sql = query.ToQueryString();
        Assert.Contains("FROM aioffice.GroupAutomaticInputPlans", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM [aioffice].[GroupAutomaticInputPlans]", sql, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
        var probe = new GroupFirstHostBatchRuntimeProof.ReadProbe();
        probe.ObserveSql(sql); probe.ObserveSql(Terminal);
        probe.Require(false);
    }

    [Theory]
    [InlineData("original")]
    [InlineData("terminal")]
    public void CompletionNeverPassesWithAMissingActualReadCategory(string missing)
    {
        var probe = new GroupFirstHostBatchRuntimeProof.ReadProbe();
        if (missing != "original") probe.ObserveSql(Original);
        if (missing != "terminal") probe.ObserveSql(Terminal);
        Assert.Throws<InvalidOperationException>(() => probe.Require(false));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TerminalRecoveryMustKeepBothOriginalAndAcceptedReadersCold(bool original, bool accepted)
    {
        var probe = new GroupFirstHostBatchRuntimeProof.ReadProbe();
        probe.ObserveSql(Terminal);
        if (original) probe.ObserveSql(Original);
        if (accepted) probe.ObserveSql(Accepted);
        if (original || accepted) Assert.Throws<InvalidOperationException>(() => probe.Require(true));
        else probe.Require(true);
    }

    [Fact]
    public void HostOnlyCreationRefusesAnyAcceptedResultRead()
    {
        var probe = new GroupFirstHostBatchRuntimeProof.ReadProbe();
        probe.ObserveSql(Original); probe.ObserveSql(Terminal); probe.ObserveSql(Accepted);
        Assert.Throws<InvalidOperationException>(() => probe.Require(false));
    }

    [Fact]
    public void ResetCannotReuseBaselineReadsToQualifyTheNextProcessCall()
    {
        var probe = new GroupFirstHostBatchRuntimeProof.ReadProbe();
        probe.ObserveSql(Original); probe.ObserveSql(Terminal); probe.Require(false);
        probe.Reset();
        Assert.Throws<InvalidOperationException>(() => probe.Require(false));
        Assert.Throws<InvalidOperationException>(() => probe.Require(true));
        probe.ObserveSql(Terminal); probe.Require(true);
        Assert.Throws<InvalidOperationException>(() => probe.Require(false));
    }
}
