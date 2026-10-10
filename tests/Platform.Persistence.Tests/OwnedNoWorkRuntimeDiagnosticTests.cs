extern alias GroupReferenceProof;

using GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedNoWorkRuntimeDiagnosticTests
{
    [Theory]
    [InlineData((int)GroupNoWorkRuntimeProof.ProofPhase.Setup, "setup")]
    [InlineData((int)GroupNoWorkRuntimeProof.ProofPhase.Claim, "claim")]
    [InlineData((int)GroupNoWorkRuntimeProof.ProofPhase.Source, "source")]
    [InlineData((int)GroupNoWorkRuntimeProof.ProofPhase.Preparation, "preparation")]
    [InlineData((int)GroupNoWorkRuntimeProof.ProofPhase.Brain, "brain")]
    [InlineData((int)GroupNoWorkRuntimeProof.ProofPhase.Dependencies, "dependencies")]
    [InlineData((int)GroupNoWorkRuntimeProof.ProofPhase.Commit, "commit")]
    [InlineData((int)GroupNoWorkRuntimeProof.ProofPhase.Savepoint, "savepoint")]
    [InlineData((int)GroupNoWorkRuntimeProof.ProofPhase.Flush, "flush")]
    [InlineData((int)GroupNoWorkRuntimeProof.ProofPhase.Rollback, "rollback")]
    [InlineData((int)GroupNoWorkRuntimeProof.ProofPhase.ExpiryChecks, "expirychecks")]
    [InlineData((int)GroupNoWorkRuntimeProof.ProofPhase.ClockRollback, "clockrollback")]
    [InlineData((int)GroupNoWorkRuntimeProof.ProofPhase.Replay, "replay")]
    [InlineData((int)GroupNoWorkRuntimeProof.ProofPhase.DuplicateChecks, "duplicatechecks")]
    [InlineData(int.MaxValue, "unknown")]
    public void ActualProgressExportsOnlyClosedFixedTokens(int phase, string token)
    {
        var progress = new GroupNoWorkRuntimeProof.ProofProgress { Phase = (GroupNoWorkRuntimeProof.ProofPhase)phase };
        Assert.Equal("FAIL owned NoWork runtime phase-" + token, progress.FailureLine());
    }
}
