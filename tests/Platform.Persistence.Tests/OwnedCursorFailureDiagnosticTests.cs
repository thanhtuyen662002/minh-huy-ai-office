extern alias GroupReferenceProof;

using Xunit;
using Proof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupSignedCursorMigrationRuntimeProof;
using Completion = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupCursorProofCompletion;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedCursorFailureDiagnosticTests
{
    [Theory]
    [InlineData("null", "shadow-create-null")]
    [InlineData("dbnull", "shadow-create-dbnull")]
    [InlineData("long", "shadow-create-non-int")]
    [InlineData("short", "shadow-create-non-int")]
    [InlineData("bool", "shadow-create-non-int")]
    [InlineData("string", "shadow-create-non-int")]
    [InlineData("object", "shadow-create-non-int")]
    [InlineData("negative", "shadow-create-system-id")]
    [InlineData("zero", "shadow-create-system-id")]
    [InlineData("four", "shadow-create-system-id")]
    public void CreateScalarRefusesUntrustedShapeWithoutConversionOrPrivateFormatting(string kind, string phase)
    {
        object? result = kind switch
        {
            "null" => null,
            "dbnull" => DBNull.Value,
            "long" => 5L,
            "short" => (short)5,
            "bool" => true,
            "string" => "PRIVATE_SQL_PASSWORD_AND_CHAT_7923",
            "object" => new UnformattableResult(),
            "negative" => -1,
            "zero" => 0,
            _ => 4
        };
        var error = Assert.Throws<InvalidOperationException>(() => Proof.RequireCreatedDatabaseId(result));
        Assert.Equal(phase, Proof.Phase);
        Assert.Equal("FAIL owned signed cursor migration diagnostic invalid-operation snapshot=-1", Proof.FailureDiagnostic(error));
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(int.MaxValue)]
    public void ValidCreateScalarAdvancesObservationBeforeCleanup(int id)
    {
        Assert.Equal(id, Proof.RequireCreatedDatabaseId(id));
        Assert.Equal("shadow-create-validated", Proof.Phase);
    }

    [Fact]
    public async Task ScalarFailureRemainsFirstWhenIndependentCleanupAlsoFails()
    {
        var completion = new Completion();
        var cleanupError = new NotSupportedException("PRIVATE_SQL_PASSWORD_AND_CHAT_7923");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => completion.RunAsync(() =>
        {
            Proof.RequireCreatedDatabaseId(DBNull.Value);
            return Task.CompletedTask;
        }, () => throw cleanupError));
        Assert.Equal("shadow-create-dbnull", Proof.Phase);
        Assert.Same(error, Assert.Throws<InvalidOperationException>(completion.RequireClean));
        Assert.NotSame(cleanupError, error);
    }

    [Fact]
    public async Task SuccessfulScalarWithCleanupFailureHasDistinctObservationAndSameOriginalError()
    {
        var completion = new Completion();
        var cleanupError = new InvalidOperationException("PRIVATE_SQL_PASSWORD_AND_CHAT_7923");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => completion.RunAsync(() =>
        {
            Assert.Equal(5, Proof.RequireCreatedDatabaseId(5));
            return Task.CompletedTask;
        }, () => throw cleanupError));
        Assert.Equal("shadow-create-validated", Proof.Phase);
        Assert.Same(cleanupError, error);
        Assert.Same(error, Assert.Throws<InvalidOperationException>(completion.RequireClean));
        Assert.DoesNotContain("PRIVATE_SQL", Proof.FailureDiagnostic(error));
    }

    private sealed class UnformattableResult
    {
        public override string ToString() => throw new NotSupportedException("PRIVATE_SQL_PASSWORD_AND_CHAT_7923");
    }

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
