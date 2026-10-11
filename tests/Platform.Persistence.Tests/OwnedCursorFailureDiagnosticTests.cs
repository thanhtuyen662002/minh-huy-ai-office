extern alias GroupReferenceProof;

using Xunit;
using Proof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupSignedCursorMigrationRuntimeProof;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedCursorFailureDiagnosticTests
{
    [Theory]
    [InlineData("argument")]
    [InlineData("invalid-operation")]
    [InlineData("cancelled")]
    [InlineData("not-supported")]
    [InlineData("other")]
    public void FailureCategoryNeverIncludesPrivateMessageOrInnerException(string kind)
    {
        const string secret = "PRIVATE_SQL_PASSWORD_AND_CHAT_7923";
        Exception error = kind switch
        {
            "argument" => new ArgumentException(secret, new Exception(secret)),
            "invalid-operation" => new InvalidOperationException(secret, new Exception(secret)),
            "cancelled" => new OperationCanceledException(secret, new Exception(secret)),
            "not-supported" => new NotSupportedException(secret, new Exception(secret)),
            _ => new Exception(secret, new Exception(secret))
        };
        var result = Proof.FailureDiagnostic(error);
        Assert.Equal("FAIL owned signed cursor migration diagnostic " + kind + " snapshot=-1", result);
        Assert.DoesNotContain(secret, result);
    }
}
