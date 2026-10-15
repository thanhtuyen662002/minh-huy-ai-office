extern alias GroupReferenceProof;

using Xunit;
using Probe = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupOriginalAdmissionRuntimeProof.AdmittedExecutionSqlProbe;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

// Cold controls of the runtime observer only. Manually supplied command text
// and booleans cannot qualify SQL execution, authority, a provider or a model.
public sealed class OwnedAdmittedExecutionObservationTests
{
    private const string Companion = "SELECT RequestId FROM aioffice.GroupModelAttemptAdmissions WHERE RequestId=@id";

    [Fact]
    public void ObserverRequiresActualOrderAndBothSidesBeforeCompletion()
    {
        var probe = new Probe { Armed = true };
        probe.ObserveCommand(Companion); probe.ProviderStarted(true);
        Assert.Throws<InvalidOperationException>(probe.RequireCompleted);
        probe.ProviderReturned(true); Assert.Throws<InvalidOperationException>(probe.RequireCompleted);
        probe.ObserveCommand(Companion); probe.RequireCompleted();
        Assert.Equal(1, probe.BeforeCompanionReads); Assert.Equal(1, probe.AfterCompanionReads);
        Assert.Throws<InvalidOperationException>(() => probe.ProviderStarted(true));
        Assert.Throws<InvalidOperationException>(() => probe.ProviderReturned(true));
    }

    [Theory]
    [InlineData("unarmed")]
    [InlineData("no-before-read")]
    [InlineData("wrong-table")]
    [InlineData("inside-sql-start")]
    [InlineData("return-before-start")]
    [InlineData("inside-sql-return")]
    [InlineData("sql-during-provider")]
    [InlineData("no-after-read")]
    public void ObserverFaultCannotProduceCompletedEvidence(string fault)
    {
        var probe = new Probe { Armed = fault != "unarmed" };
        if (fault is not ("no-before-read" or "wrong-table")) probe.ObserveCommand(Companion);
        if (fault == "wrong-table") probe.ObserveCommand("SELECT RequestId FROM aioffice.GroupModelAttemptStarts");
        if (fault == "return-before-start") Assert.Throws<InvalidOperationException>(() => probe.ProviderReturned(true));
        else if (fault is "unarmed" or "no-before-read" or "wrong-table" or "inside-sql-start")
            Assert.Throws<InvalidOperationException>(() => probe.ProviderStarted(fault != "inside-sql-start"));
        else
        {
            probe.ProviderStarted(true);
            if (fault == "inside-sql-return") Assert.Throws<InvalidOperationException>(() => probe.ProviderReturned(false));
            if (fault == "sql-during-provider") Assert.Throws<InvalidOperationException>(() => probe.ObserveCommand("SELECT 1"));
            if (fault == "no-after-read") probe.ProviderReturned(true);
        }
        var error = Assert.Throws<InvalidOperationException>(probe.RequireCompleted);
        Assert.Equal("Owned admitted execution observation is not available.", error.Message); Assert.Null(error.InnerException);
    }
}
