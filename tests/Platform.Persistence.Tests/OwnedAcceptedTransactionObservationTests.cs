extern alias GroupReferenceProof;

using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Observation = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupOriginalAdmissionRuntimeProof.AcceptedTransactionObservation;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

// Cold observer controls only: no SQL, authenticated transaction or model.
public sealed class OwnedAcceptedTransactionObservationTests
{
    private const string Query = "SELECT CASE WHEN EXISTS (SELECT 1 FROM [aioffice].[GroupModelAttemptResults]) THEN 1 ELSE 0 END";
    private static PlatformDbContext Database() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer("Server=never-connect.invalid;Database=cold;Integrated Security=true").Options);

    [Fact]
    public void FreshReadOnlyTransactionDisposedWithoutCommitQualifiesOnlyItsOwnInvocation()
    {
        using var db = Database(); var observation = new Observation(); using var tx = new ColdTransaction();
        observation.Begin(); observation.Started(tx); observation.Query(tx, Query); observation.RequireRefused(db);
        observation.End(); observation.Begin(); Assert.Throws<InvalidOperationException>(() => observation.RequireRefused(db));
    }

    [Fact]
    public void SuccessfulPreflightCommitCannotBeMaskedByLaterConsumedPermitRefusal()
    {
        using var db = Database(); var observation = new Observation(); using var tx = new ColdTransaction();
        observation.Begin(); observation.Started(tx); observation.Query(tx, Query); observation.Committed(tx);
        var error = Assert.ThrowsAny<IOException>(() => observation.RequireRefused(db));
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("unarmed")]
    [InlineData("no-start")]
    [InlineData("no-query")]
    [InlineData("wrong-query")]
    [InlineData("duplicate-query")]
    public void MissingOrAmbiguousActualObservationNeverQualifiesRefusal(string fault)
    {
        using var db = Database(); var observation = new Observation(); using var tx = new ColdTransaction();
        if (fault != "unarmed") observation.Begin();
        if (fault != "no-start") observation.Started(tx);
        if (fault is not ("no-start" or "no-query")) observation.Query(tx, fault == "wrong-query" ? "SELECT 1" : Query);
        if (fault == "duplicate-query") observation.Query(tx, Query);
        Assert.Throws<InvalidOperationException>(() => observation.RequireRefused(db));
    }

    [Theory]
    [InlineData("foreign-query")]
    [InlineData("foreign-commit")]
    [InlineData("second-start")]
    [InlineData("double-arm")]
    public void AnotherTransactionOrInvocationCannotReplaceTheOwnedObservation(string fault)
    {
        var observation = new Observation(); using var tx = new ColdTransaction(); using var foreign = new ColdTransaction();
        observation.Begin(); observation.Started(tx);
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (fault == "foreign-query") observation.Query(foreign, Query);
            else if (fault == "foreign-commit") observation.Committed(foreign);
            else if (fault == "second-start") observation.Started(foreign);
            else observation.Begin();
        });
    }
    private sealed class ColdTransaction : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
        protected override DbConnection? DbConnection => null;
        public override void Commit() => throw new NotSupportedException();
        public override void Rollback() => throw new NotSupportedException();
    }
}
