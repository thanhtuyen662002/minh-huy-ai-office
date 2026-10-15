extern alias GroupReferenceProof;

using Xunit;
using Proof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupOriginalAdmissionRuntimeProof;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedOriginalAdmissionDiagnosticTests
{
    [Theory]
    [InlineData("empty-operation", "FK_GroupOriginalAdmissionSidecars_OriginalClaim")]
    [InlineData("empty-operation", "CK_GroupOriginalAdmissionSidecars_Identity")]
    [InlineData("zero-epoch", "FK_GroupOriginalAdmissionSidecars_OriginalClaim")]
    [InlineData("zero-epoch", "CK_GroupOriginalAdmissionSidecars_Identity")]
    [InlineData("negative-epoch", "FK_GroupOriginalAdmissionSidecars_OriginalClaim")]
    [InlineData("negative-epoch", "CK_GroupOriginalAdmissionSidecars_Identity")]
    [InlineData("lowercase-plan-hash", "FK_GroupOriginalAdmissionSidecars_OriginalPlan")]
    [InlineData("lowercase-plan-hash", "CK_GroupOriginalAdmissionSidecars_Identity")]
    [InlineData("foreign-tenant", "FK_GroupOriginalAdmissionSidecars_OriginalPlan")]
    [InlineData("foreign-tenant", "FK_GroupOriginalAdmissionSidecars_OriginalClaim")]
    [InlineData("foreign-company", "FK_GroupOriginalAdmissionSidecars_OriginalPlan")]
    [InlineData("foreign-company", "FK_GroupOriginalAdmissionSidecars_OriginalClaim")]
    [InlineData("foreign-binding", "FK_GroupOriginalAdmissionSidecars_OriginalPlan")]
    [InlineData("foreign-binding", "FK_GroupOriginalAdmissionSidecars_OriginalClaim")]
    [InlineData("missing-plan", "FK_GroupOriginalAdmissionSidecars_OriginalPlan")]
    [InlineData("wrong-plan-hash", "FK_GroupOriginalAdmissionSidecars_OriginalPlan")]
    [InlineData("wrong-claim-operation", "FK_GroupOriginalAdmissionSidecars_OriginalClaim")]
    [InlineData("wrong-claim-epoch", "FK_GroupOriginalAdmissionSidecars_OriginalClaim")]
    [InlineData("format-version", "CK_GroupOriginalAdmissionSidecars_Format")]
    [InlineData("short-payload", "CK_GroupOriginalAdmissionSidecars_Format")]
    [InlineData("oversized-payload", "CK_GroupOriginalAdmissionSidecars_Format")]
    [InlineData("nonhex-sidecar-hash", "CK_GroupOriginalAdmissionSidecars_Identity")]
    [InlineData("lowercase-sidecar-hash", "CK_GroupOriginalAdmissionSidecars_Identity")]
    [InlineData("non-UTC-time", "CK_GroupOriginalAdmissionSidecars_Time")]
    public void RelevantNamedConstraintOnlyIsAcceptedForEachInvalidDomain(string name, string constraint)
    {
        Assert.True(Proof.IsExpectedConstraintFailure(name, 547, "Owned conflict \"" + constraint + "\"."));
        Assert.True(Proof.IsExpectedConstraintFailure(name, 547, "Owned conflict '" + constraint + "'."));
        foreach (var number in new[] { -2, 229, 1205, 2627, 2601 })
            Assert.False(Proof.IsExpectedConstraintFailure(name, number, '"' + constraint + '"'));
        Assert.False(Proof.IsExpectedConstraintFailure(name, 547, '"' + constraint + "_UNRELATED\""));
        Assert.False(Proof.IsExpectedConstraintFailure(name, 547, "\"FK_OtherTable_OriginalPlan\""));
    }

    [Theory]
    [InlineData("valid-pair", "CK_GroupOriginalAdmissionSidecars_Identity")]
    [InlineData("nonhex-sidecar-hash", "FK_GroupOriginalAdmissionSidecars_OriginalPlan")]
    [InlineData("lowercase-sidecar-hash", "FK_GroupOriginalAdmissionSidecars_OriginalClaim")]
    [InlineData("wrong-claim-epoch", "CK_GroupOriginalAdmissionSidecars_Identity")]
    [InlineData("format-version", "FK_GroupOriginalAdmissionSidecars_OriginalPlan")]
    [InlineData("PRIVATE_UNKNOWN_CASE", "CK_GroupOriginalAdmissionSidecars_Identity")]
    public void UnrelatedOrPositiveConstraintErrorsNeverQualify(string name, string constraint) =>
        Assert.False(Proof.IsExpectedConstraintFailure(name, 547, '"' + constraint + '"'));

    [Theory]
    [InlineData("constraint-empty-operation", "invariant")]
    [InlineData("constraint-non-UTC-time", "denied")]
    [InlineData("committed-reply-loss", "cancelled")]
    [InlineData("allocation", "other")]
    public void DiagnosticContainsOnlyClosedPhaseAndTypeWithoutReadingPrivateError(string phase, string kind)
    {
        const string secret = "PRIVATE_CONNECTION_AND_SOURCE_BODY";
        Exception error = kind switch
        {
            "invariant" => new InvalidOperationException(secret),
            "denied" => new UnauthorizedAccessException(secret),
            "cancelled" => new OperationCanceledException(secret),
            _ => new HostileException()
        };
        Assert.Equal("FAIL owned original admission runtime phase-" + phase + " kind-" + kind, Proof.FailureDiagnostic(phase, error));
        Assert.DoesNotContain(secret, Proof.FailureDiagnostic(secret, error));
        Assert.Equal("FAIL owned original admission runtime phase-unknown kind-" + kind, Proof.FailureDiagnostic(secret, error));
    }

    private sealed class HostileException : Exception
    {
        public override string Message => throw new NotSupportedException("PRIVATE_MESSAGE_GETTER");
        public override string ToString() => throw new NotSupportedException("PRIVATE_FORMATTER");
    }
}
