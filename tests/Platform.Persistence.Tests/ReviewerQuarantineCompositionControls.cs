using Xunit;
namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class ReviewerQuarantineCompositionControls
{
    [Theory]
    [InlineData("{\"password\":\"owned-synthetic-secret\"}")]
    [InlineData("{\"notes\":[{\"client_secret\":\"owned-synthetic-secret\"}]}")]
    [InlineData("{\"Authorization\":\"Basic owned-synthetic-secret\"}")]
    [InlineData("{\"password\":123456}")]
    public void AlreadyRecognizedCredentialAssignmentMustRemainQuarantinedAsDecodedJson(string output)
    {
        Assert.True(GroupSecretQuarantine.Inspect(output).RequiresQuarantine);
        Assert.True(GroupSecretQuarantine.InspectOutputJson(output).RequiresQuarantine);
    }
    [Fact]
    public void EscapedCredentialKeyMustPreserveRecognizedAssignment()
    {
        Assert.True(GroupSecretQuarantine.Inspect("{\"password\":\"owned-synthetic-secret\"}").RequiresQuarantine);
        Assert.True(GroupSecretQuarantine.InspectOutputJson("{\"pass\\u0077ord\":\"owned-synthetic-secret\"}").RequiresQuarantine);
    }
}
