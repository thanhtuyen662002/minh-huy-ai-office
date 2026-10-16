extern alias GroupReferenceProof;

using GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedRestoredBatchDiagnosticTests
{
    [Theory]
    [InlineData("setup")]
    [InlineData("claim")]
    [InlineData("baseline")]
    [InlineData("process")]
    [InlineData("process-result")]
    [InlineData("process-reads")]
    [InlineData("model-factory")]
    [InlineData("verify")]
    [InlineData("verify-counts")]
    [InlineData("verify-immutable")]
    [InlineData("verify-effects")]
    [InlineData("verify-terminal")]
    [InlineData("verify-clean")]
    [InlineData("unknown")]
    public void KnownPhasesRetainOnlyClosedObservations(string phase)
    {
        Assert.Equal("FAIL owned restored batch runtime phase-" + phase + " kind-invariant reads-101",
            GroupRestoredBatchRuntimeProof.FailureDiagnostic(phase, "101",
                new InvalidOperationException("private-canary", new Exception("inner-private-canary"))));
    }

    [Theory]
    [InlineData("private-canary", "1010")]
    [InlineData("process\nprivate-canary", "1\n1")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void UnknownInputCannotBecomeAFreeformDiagnostic(string? phase, string? reads)
    {
        var text = GroupRestoredBatchRuntimeProof.FailureDiagnostic(phase!, reads!, new Exception("private-canary"));
        Assert.Equal("FAIL owned restored batch runtime phase-unknown kind-other reads-unknown", text);
        Assert.DoesNotContain("canary", text);
    }

    [Fact]
    public void ExceptionKindsNeverExposeMessagesOrInnerExceptions()
    {
        foreach (var (error, kind) in new (Exception, string)[]
        {
            (new OperationCanceledException("private-canary"), "cancelled"),
            (new UnauthorizedAccessException("private-canary"), "denied"),
            (new InvalidOperationException("private-canary", new Exception("inner-private-canary")), "invariant"),
            (new Exception("private-canary"), "other")
        })
        {
            var text = GroupRestoredBatchRuntimeProof.FailureDiagnostic("process", "000", error);
            Assert.Equal("FAIL owned restored batch runtime phase-process kind-" + kind + " reads-000", text);
            Assert.DoesNotContain("canary", text);
        }
    }

    [Fact]
    public void ReadSignatureCannotSupplyMissingReadsOrSurviveReset()
    {
        var probe = new GroupRestoredBatchRuntimeProof.ReadProbe(); Assert.Equal("000", probe.Signature);
        probe.ObserveSql("SELECT 1 FROM aioffice.GroupAutomaticInputPlans"); Assert.Equal("100", probe.Signature);
        probe.ObserveSql("SELECT 1 FROM [aioffice].[GroupModelAttemptResults]"); Assert.Equal("110", probe.Signature);
        Assert.Throws<InvalidOperationException>(() => probe.Require(false));
        probe.ObserveSql("SELECT 1 FROM [aioffice].[GroupBatchTerminalReceipts]"); Assert.Equal("111", probe.Signature);
        probe.Require(false); probe.Reset(); Assert.Equal("000", probe.Signature);
        Assert.Throws<InvalidOperationException>(() => probe.Require(false));
        Assert.Throws<InvalidOperationException>(() => probe.Require(true));
        probe.ObserveSql("SELECT 1 FROM [aioffice].[GroupBatchTerminalReceipts]"); Assert.Equal("001", probe.Signature);
        probe.Require(true);
    }
}
