extern alias GroupReferenceProof;

using GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupReferenceRuntimeProofGuardTests
{
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
