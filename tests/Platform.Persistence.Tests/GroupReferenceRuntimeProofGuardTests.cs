extern alias GroupReferenceProof;

using GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupReferenceRuntimeProofGuardTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void NamespaceInitCannotStandInForActualProcessDeath(int processId) =>
        Assert.Throws<InvalidOperationException>(() => OwnedGroupReferenceProofGuard.RequireKillableChild(processId));

    [Fact]
    public void ActualChildCanReachGuardedCrashProof() => OwnedGroupReferenceProofGuard.RequireKillableChild(2);

    [Fact]
    public void AllocationRollbackRequiresActualSanitizedCommitBoundaryAndNeverAnUnrelatedOperation()
    {
        Assert.True(OwnedGroupReferenceProofGuard.IsExpectedRefusal("allocation-rollback", new GroupBatchAllocationCommitException()));
        Assert.False(OwnedGroupReferenceProofGuard.IsExpectedRefusal("allocation-rollback", new InvalidOperationException("PRIVATE_UNRELATED")));
        Assert.False(OwnedGroupReferenceProofGuard.IsExpectedRefusal("allocation-rollback", new DbUpdateException("PRIVATE_SQL")));
        foreach (var mode in new[] { "allocation-deny", "allocation-unsafe" })
        {
            Assert.True(OwnedGroupReferenceProofGuard.IsExpectedRefusal(mode, new UnauthorizedAccessException()));
            Assert.False(OwnedGroupReferenceProofGuard.IsExpectedRefusal(mode, new GroupBatchAllocationCommitException()));
        }
    }

    [Fact]
    public void ClaimRefusalsRequireCurrentAuthorityOrTheActualSanitizedCommitBoundary()
    {
        Assert.True(OwnedGroupReferenceProofGuard.IsExpectedRefusal("claim-rollback", new GroupBatchClaimCommitException()));
        foreach (var error in new Exception[] { new DbUpdateException("PRIVATE_SQL"), new GroupBatchAllocationCommitException(),
            new InvalidOperationException("PRIVATE_UNRELATED"), new UnauthorizedAccessException(), new IOException() })
            Assert.False(OwnedGroupReferenceProofGuard.IsExpectedRefusal("claim-rollback", error));
        foreach (var mode in new[] { "claim-deny", "claim-unsafe" })
        {
            Assert.True(OwnedGroupReferenceProofGuard.IsExpectedRefusal(mode, new UnauthorizedAccessException()));
            Assert.False(OwnedGroupReferenceProofGuard.IsExpectedRefusal(mode, new GroupBatchClaimCommitException()));
        }
    }

    [Theory]
    [InlineData("deny", true, false, false)]
    [InlineData("unsafe", true, false, false)]
    [InlineData("work-unsafe", true, false, false)]
    [InlineData("rollback", false, true, false)]
    [InlineData("publish", false, false, false)]
    [InlineData("unknown", false, false, false)]
    public void NativeRefusalRequiresTheActualShippingBoundaryType(string mode, bool authorization, bool update, bool operation)
    {
        // This is the actual permission verifier's refusal, including its
        // deliberate non-disclosure of SQL diagnostics/credential selectors.
        Assert.Equal(authorization, OwnedGroupReferenceProofGuard.IsExpectedRefusal(mode, DataSourceSecretBindingService.Unavailable()));
        Assert.Equal(update, OwnedGroupReferenceProofGuard.IsExpectedRefusal(mode, new DbUpdateException("PRIVATE_SQL_FAILURE")));
        Assert.Equal(operation, OwnedGroupReferenceProofGuard.IsExpectedRefusal(mode, new InvalidOperationException("PRIVATE_UNRELATED_OPERATION")));
        Assert.False(OwnedGroupReferenceProofGuard.IsExpectedRefusal(mode, new IOException("PRIVATE_TRANSPORT")));
        Assert.False(OwnedGroupReferenceProofGuard.IsExpectedRefusal(mode, new OperationCanceledException()));
    }

    [Theory]
    [InlineData("CI")]
    [InlineData("GITHUB_ACTIONS")]
    [InlineData("AIOFFICE_OWNED_GROUP_REFERENCE_PROOF")]
    public void AllOwnedFlagsAreRequiredBeforeConfigurationOrResources(string missing)
    {
        var requested = new List<string>();
        Assert.Throws<InvalidOperationException>(() => OwnedGroupReferenceProofGuard.RequireOwned(name =>
        { requested.Add(name); return name == missing ? null : "true"; }));
        Assert.All(requested, name => Assert.Contains(name, new[] { "CI", "GITHUB_ACTIONS", "AIOFFICE_OWNED_GROUP_REFERENCE_PROOF" }));
        OwnedGroupReferenceProofGuard.RequireOwned(_ => "true");
    }

    [Theory]
    [InlineData("Server", "customer-db")]
    [InlineData("Database", "CustomerERP")]
    [InlineData("User ID", "sa")]
    [InlineData("Integrated Security", "true")]
    [InlineData("Authentication", "Active Directory Default")]
    [InlineData("Failover Partner", "customer-db")]
    [InlineData("AttachDbFilename", "/customer/db.mdf")]
    [InlineData("Enlist", "false")]
    [InlineData("Password", "short")]
    public void CustomerElevatedAlternateAndMalformedConnectionsAreRejectedBeforeConnect(string key, string value)
    {
        var connection = "Server=sql;Database=AIOfficeLocal;User ID=aioffice_runtime;Password=" + new string('A', 48);
        Assert.ThrowsAny<Exception>(() => OwnedGroupReferenceProofGuard.RequireRuntimeConnection(connection + ";" + key + "=" + value));
        Assert.Contains("Connect Timeout=10", OwnedGroupReferenceProofGuard.RequireRuntimeConnection(connection), StringComparison.Ordinal);
    }
}
