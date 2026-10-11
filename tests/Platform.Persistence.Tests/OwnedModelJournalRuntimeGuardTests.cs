extern alias GroupReferenceProof;

using Xunit;
using Proof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupModelAttemptJournalRuntimeProof;
using ExtractionProof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupExtractionFailureRuntimeProof;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedModelJournalRuntimeGuardTests
{
    [Theory]
    [InlineData("model-journal-prepare")]
    [InlineData("model-journal-lost-reply")]
    [InlineData("model-journal-resume")]
    [InlineData("model-journal-cap")]
    [InlineData("model-journal-expiry")]
    public async Task EveryJournalModeRequiresOwnedRuntimeBeforeConfigurationOrAnyConnection(string mode)
    {
        Assert.NotEqual("true", Environment.GetEnvironmentVariable("AIOFFICE_OWNED_GROUP_REFERENCE_PROOF"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Proof.RunAsync(mode, null!, Guid.Empty, null!, null!, default));
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task ExtractionFailureRequiresOwnedRuntimeBeforeConfigurationOrConnection()
    {
        Assert.NotEqual("true", Environment.GetEnvironmentVariable("AIOFFICE_OWNED_GROUP_REFERENCE_PROOF"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ExtractionProof.RunAsync(null!, Guid.Empty, null!, null!, default));
        Assert.Null(error.InnerException);
    }
}
