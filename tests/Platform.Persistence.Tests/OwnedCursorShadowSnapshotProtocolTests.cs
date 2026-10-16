extern alias GroupReferenceProof;

using System.Data;
using Xunit;
using Proof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupSignedCursorMigrationRuntimeProof;
using Completion = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupCursorProofCompletion;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedCursorShadowSnapshotProtocolTests
{
    private static DataTable Table()
    {
        var table = new DataTable(); table.Columns.Add("Tag", typeof(string)); table.Columns.Add("Sha256", typeof(string)); return table;
    }

    [Fact]
    public async Task ActualReaderAcceptsOneExactInventoryDigestAndConsumesItsEntireResult()
    {
        using var table = Table(); table.Rows.Add("inventory", new string('A', 64)); using var reader = table.CreateDataReader();
        Assert.Equal(new string('A', 64), await Proof.ReadSnapshotHashAsync(reader, "inventory", CancellationToken.None));
        Assert.False(reader.Read()); Assert.False(reader.NextResult());
    }

    [Theory]
    [InlineData("wrong-tag")]
    [InlineData("short")]
    [InlineData("lowercase")]
    [InlineData("invalid-hex")]
    [InlineData("null-tag")]
    [InlineData("null-hash")]
    [InlineData("no-row")]
    [InlineData("extra-row")]
    public async Task MissingForeignNullTruncatedMalformedOrDuplicateDigestNeverQualifies(string fault)
    {
        using var table = Table();
        if (fault != "no-row") table.Rows.Add(fault == "wrong-tag" ? "rows:other" : fault == "null-tag" ? DBNull.Value : "inventory",
            fault == "short" ? new string('A', 63) : fault == "lowercase" ? new string('a', 64)
                : fault == "invalid-hex" ? new string('Z', 64) : fault == "null-hash" ? DBNull.Value : new string('A', 64));
        if (fault == "extra-row") table.Rows.Add("inventory", new string('A', 64));
        using var reader = table.CreateDataReader();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Proof.ReadSnapshotHashAsync(reader, "inventory", CancellationToken.None));
    }

    [Fact]
    public async Task AnAdditionalResultSetCannotHideBehindOneCorrectDigest()
    {
        using var first = Table(); using var second = Table(); first.Rows.Add("inventory", new string('A', 64));
        using var reader = new DataTableReader([first, second]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Proof.ReadSnapshotHashAsync(reader, "inventory", CancellationToken.None));
    }

    [Fact]
    public async Task WrongColumnCountAndPreCancelledReaderNeverQualify()
    {
        using var wrong = new DataTable(); wrong.Columns.Add("Tag", typeof(string)); wrong.Rows.Add("inventory");
        using var reader = wrong.CreateDataReader();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Proof.ReadSnapshotHashAsync(reader, "inventory", CancellationToken.None));
        using var valid = Table(); valid.Rows.Add("inventory", new string('A', 64)); using var untouched = valid.CreateDataReader();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Proof.ReadSnapshotHashAsync(untouched, "inventory", new CancellationToken(true)));
        Assert.True(untouched.Read());
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(5, 0)]
    [InlineData(5, -1)]
    [InlineData(5, 1)]
    public async Task OwnershipReaderReturnsOnlyOneCompleteTypedObservation(int? id, int state)
    {
        using var table = new DataTable(); table.Columns.Add("DatabaseId", typeof(int)); table.Columns.Add("OwnerState", typeof(int));
        table.Rows.Add(id is null ? DBNull.Value : id.Value, state); using var reader = table.CreateDataReader();
        Assert.Equal((id, state), await Proof.ReadOwnershipAsync(reader, CancellationToken.None));
        Assert.False(reader.Read()); Assert.False(reader.NextResult());
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(0, 1)]
    [InlineData(5, 2)]
    [InlineData(5, -2)]
    [InlineData(null, 1)]
    [InlineData(null, -1)]
    public async Task SystemOrMalformedOwnershipObservationRefuses(int? id, int state)
    {
        using var table = new DataTable(); table.Columns.Add("DatabaseId", typeof(int)); table.Columns.Add("OwnerState", typeof(int));
        table.Rows.Add(id is null ? DBNull.Value : id.Value, state); using var reader = table.CreateDataReader();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Proof.ReadOwnershipAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task MissingDuplicateExtraResultAndCancelledOwnershipObservationsCannotQualify()
    {
        using var table = new DataTable(); table.Columns.Add("DatabaseId", typeof(int)); table.Columns.Add("OwnerState", typeof(int));
        using (var empty = table.CreateDataReader())
            await Assert.ThrowsAsync<InvalidOperationException>(() => Proof.ReadOwnershipAsync(empty, CancellationToken.None));
        table.Rows.Add(5, 1); table.Rows.Add(5, 1);
        using (var duplicate = table.CreateDataReader())
            await Assert.ThrowsAsync<InvalidOperationException>(() => Proof.ReadOwnershipAsync(duplicate, CancellationToken.None));
        table.Rows.RemoveAt(1); using var extra = table.Clone();
        using (var multi = new DataTableReader([table, extra]))
            await Assert.ThrowsAsync<InvalidOperationException>(() => Proof.ReadOwnershipAsync(multi, CancellationToken.None));
        using var untouched = table.CreateDataReader();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Proof.ReadOwnershipAsync(untouched, new CancellationToken(true)));
        Assert.True(untouched.Read());
    }

    [Theory]
    [InlineData(false, false, null, 5, 1, true)]
    [InlineData(true, true, null, 5, 1, true)]
    [InlineData(true, false, null, 5, 0, true)]
    [InlineData(true, false, null, 5, -1, true)]
    [InlineData(true, false, 5, 6, 1, true)]
    [InlineData(true, false, null, 4, 1, true)]
    [InlineData(true, false, null, null, 0, false)]
    [InlineData(true, false, null, null, 1, true)]
    public void UnknownPreexistingUnmarkedForeignChangedOrUnverifiedIdentityNeverGrantsDrop(
        bool initiallyAbsent, bool refusedExisting, int? createdId, int? observedId, int state, bool failed) =>
        Assert.Throws<InvalidOperationException>(() => Proof.RequireCleanupIdentity(initiallyAbsent, refusedExisting, createdId, (observedId, state), failed));

    [Fact]
    public void LostReplyRecoveryRequiresTheSameExactObservedOwnershipAndInitialAbsence()
    {
        Assert.Equal(5, Proof.RequireCleanupIdentity(true, false, null, (5, 1), true));
        Assert.Equal(5, Proof.RequireCleanupIdentity(true, false, 5, (5, 1), false));
        Assert.Null(Proof.RequireCleanupIdentity(true, false, null, (null, 0), true));
    }

    [Fact]
    public async Task ActualCompletionPreservesOriginalFailureAndRunsEveryCleanupButWithholdsRefusalQualification()
    {
        var completion = new Completion(); var original = new InvalidOperationException("owned-primary");
        var actions = new List<string>();
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => completion.RunAsync(
            () => Task.FromException(original),
            () => { actions.Add("rollback"); throw new IOException("owned-cleanup"); },
            () => { actions.Add("close"); throw new OperationCanceledException(); },
            () => { actions.Add("dispose"); return ValueTask.CompletedTask; }));
        Assert.Same(original, observed); Assert.NotNull(observed.StackTrace);
        Assert.Equal(new[] { "rollback", "close", "dispose" }, actions);
        Assert.Throws<InvalidOperationException>(completion.RequireClean);
        await completion.RunAsync(() => Task.CompletedTask); // A later success cannot erase that failed cleanup.
        Assert.Throws<InvalidOperationException>(completion.RequireClean);
    }

    [Fact]
    public async Task SuccessfulOperationWithFailedCleanupThrowsTheFirstCleanupErrorAndStillRunsTheRest()
    {
        var completion = new Completion(); var first = new IOException("owned-cleanup"); var disposed = false;
        var observed = await Assert.ThrowsAsync<IOException>(() => completion.RunAsync(() => Task.FromResult(42),
            () => throw first, () => { disposed = true; return ValueTask.CompletedTask; }));
        Assert.Same(first, observed); Assert.True(disposed); Assert.Same(first, Assert.Throws<IOException>(completion.RequireClean));
    }

    [Fact]
    public async Task CleanExpectedFailureKeepsItsIdentityWithoutPoisoningLaterPositiveProof()
    {
        var completion = new Completion(); var original = new InvalidOperationException("owned-refusal"); var closed = false;
        Assert.Same(original, await Assert.ThrowsAsync<InvalidOperationException>(() => completion.RunAsync(
            () => Task.FromException(original), () => { closed = true; return ValueTask.CompletedTask; })));
        Assert.True(closed); completion.RequireClean();
        Assert.Equal(42, await completion.RunAsync(() => Task.FromResult(42))); completion.RequireClean();
    }
}
