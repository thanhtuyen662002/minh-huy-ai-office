extern alias GroupReferenceProof;

using System.Data;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;
using Proof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupModelAttemptResultRuntimeProof;
using NoteProof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupNoteRuntimeProof;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

// Synthetic ADO proves dispatch/admission and refusal controls only. It cannot
// prove physical SQL disposal, Exclusive retirement, commit or model quality.
public sealed class OwnedFirstSharedExpiryObserverTests
{
    [Fact]
    public async Task Actual_EF_claim_reader_observes_Shared_without_suppressing_the_provider()
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection("Shared");
        var scope = Scope(); var batch = Guid.NewGuid(); var clock = new NoteProof.OwnedClock(DateTimeOffset.UtcNow);
        var expiry = clock.Current.AddMinutes(2);
        var evidence = new Proof.SharedExpiryEvidence(scope, batch, clock, clock.Current, expiry);
        await using var db = Database(connection, evidence); evidence.Arm(db); clock.Current = expiry;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            await Assert.ThrowsAsync<NotSupportedException>(() => ReadAsync(db, scope, batch));
            Assert.Single(connection.Commands);
            Assert.Equal("SELECT APPLOCK_MODE('public',@resource,'Transaction');", connection.Commands[0]);
        }
        finally { evidence.Disarm(); }
        Assert.Throws<InvalidOperationException>(() => evidence.RequireComplete(db));
    }

    [Theory]
    [InlineData("Exclusive")]
    [InlineData("NoLock")]
    [InlineData("Update")]
    [InlineData("SharedIntentExclusive")]
    [InlineData("Unknown")]
    [InlineData(null)]
    public async Task Claim_reader_refuses_every_non_Shared_mode(object? mode)
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(mode);
        var scope = Scope(); var batch = Guid.NewGuid(); var clock = new NoteProof.OwnedClock(DateTimeOffset.UtcNow);
        var expiry = clock.Current.AddMinutes(2);
        var evidence = new Proof.SharedExpiryEvidence(scope, batch, clock, clock.Current, expiry);
        await using var db = Database(connection, evidence); evidence.Arm(db); clock.Current = expiry;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(db, scope, batch));
            Assert.Single(connection.Commands);
        }
        finally { evidence.Disarm(); }
    }

    [Theory]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    public async Task First_transaction_refuses_weaker_isolation_before_provider_begin(IsolationLevel level)
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection("Shared");
        var clock = new NoteProof.OwnedClock(DateTimeOffset.UtcNow); var expiry = clock.Current.AddMinutes(2);
        var evidence = new Proof.SharedExpiryEvidence(Scope(), Guid.NewGuid(), clock, clock.Current, expiry);
        await using var db = Database(connection, evidence); evidence.Arm(db); clock.Current = expiry;
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.BeginTransactionAsync(level)); }
        finally { evidence.Disarm(); }
        Assert.Null(connection.LastTransaction); Assert.Empty(connection.Commands);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Foreign_scope_or_batch_is_refused_before_mode_lookup(bool foreignScope)
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection("Shared");
        var scope = Scope(); var batch = Guid.NewGuid(); var clock = new NoteProof.OwnedClock(DateTimeOffset.UtcNow);
        var expiry = clock.Current.AddMinutes(2);
        var evidence = new Proof.SharedExpiryEvidence(scope, batch, clock, clock.Current, expiry);
        await using var db = Database(connection, evidence); evidence.Arm(db); clock.Current = expiry;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(db, foreignScope ? Scope() : scope,
                foreignScope ? batch : Guid.NewGuid()));
            Assert.Empty(connection.Commands);
        }
        finally { evidence.Disarm(); }
    }

    [Fact]
    public async Task Shared_transaction_cannot_save_or_commit_as_retirement()
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection("Shared");
        var scope = Scope(); var batch = Guid.NewGuid(); var clock = new NoteProof.OwnedClock(DateTimeOffset.UtcNow);
        var expiry = clock.Current.AddMinutes(2);
        var evidence = new Proof.SharedExpiryEvidence(scope, batch, clock, clock.Current, expiry);
        await using var db = Database(connection, evidence); evidence.Arm(db); clock.Current = expiry;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());
            Assert.Empty(connection.Commands);
        }
        finally { evidence.Disarm(); }
    }

    [Fact]
    public async Task Missing_first_observation_cannot_be_replaced_by_a_second_begin()
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection("Shared");
        var clock = new NoteProof.OwnedClock(DateTimeOffset.UtcNow); var restore = clock.Current;
        var expiry = restore.AddMinutes(2);
        var evidence = new Proof.SharedExpiryEvidence(Scope(), Guid.NewGuid(), clock, restore, expiry);
        await using var db = Database(connection, evidence); evidence.Arm(db); clock.Current = expiry;
        try
        {
            await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable)) { }
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.BeginTransactionAsync(IsolationLevel.Serializable));
            Assert.Equal(expiry, clock.Current);
            Assert.Throws<InvalidOperationException>(() => evidence.RequireComplete(db));
        }
        finally { evidence.Disarm(); }
    }

    [Fact]
    public async Task Only_the_exact_shipping_denial_is_consumed_and_success_is_refused()
    {
        await Proof.RequireShippingExpiryDenialAsync(() => Task.FromException(new UnauthorizedAccessException("Group service access is not available.")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Proof.RequireShippingExpiryDenialAsync(() => Task.CompletedTask));
        var generic = new InvalidOperationException("synthetic probe failure");
        Assert.Same(generic, await Assert.ThrowsAsync<InvalidOperationException>(() => Proof.RequireShippingExpiryDenialAsync(() => Task.FromException(generic))));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Proof.RequireShippingExpiryDenialAsync(() => Task.FromException(new UnauthorizedAccessException("other denial"))));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Proof.RequireShippingExpiryDenialAsync(() => Task.FromCanceled(new CancellationToken(true))));
    }

    private static PlatformDbContext Database(BindingStorePermissionTests.SyntheticConnection connection, Proof.SharedExpiryEvidence evidence) =>
        new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(connection).AddInterceptors(
            new Proof.SharedExpiryReadProbe(evidence), new Proof.SharedExpirySaveProbe(evidence),
            new Proof.SharedExpiryTransactionProbe(evidence)).Options);
    private static Task<long[]> ReadAsync(PlatformDbContext db, GroupScope scope, Guid batch) =>
        db.GroupBatchClaimStates.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
            && x.BindingId == scope.SourceBindingId && x.BatchId == batch).Select(x => x.Epoch).ToArrayAsync();
    private static GroupScope Scope() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void Actual_EF_tracker_accepts_exactly_one_NULL_to_original_expiry_change()
    {
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var scope = Scope(); var row = State(scope); db.Attach(row);
        row.ExpiryObservedAtUtc = row.ExpiresAtUtc;
        Proof.RequireFirstExpiryWitnessChange(db, scope, row.BatchId, row.ExpiresAtUtc);
        Assert.Equal(EntityState.Modified, db.Entry(row).State);
        Assert.Single(db.Entry(row).Properties, x => x.IsModified);
    }

    [Theory]
    [InlineData("prior-witness")]
    [InlineData("rewound-time")]
    [InlineData("wrong-batch")]
    [InlineData("foreign-source")]
    [InlineData("epoch2")]
    [InlineData("owner-change")]
    [InlineData("added")]
    [InlineData("deleted")]
    [InlineData("second-row")]
    [InlineData("no-change")]
    public void Actual_EF_tracker_refuses_non_first_or_extra_writes(string fault)
    {
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var scope = Scope(); var row = State(scope); var batch = row.BatchId;
        if (fault == "prior-witness") row.ExpiryObservedAtUtc = row.ExpiresAtUtc.AddTicks(1);
        db.Attach(row);
        row.ExpiryObservedAtUtc = fault == "rewound-time" ? row.IssuedAtUtc : row.ExpiresAtUtc;
        if (fault == "wrong-batch") batch = Guid.NewGuid();
        if (fault == "foreign-source") scope = Scope();
        if (fault == "epoch2") row.Epoch = 2;
        if (fault == "owner-change") row.OwnerId = Guid.NewGuid();
        if (fault == "added") db.Entry(row).State = EntityState.Added;
        if (fault == "deleted") db.Entry(row).State = EntityState.Deleted;
        if (fault == "second-row") db.Add(State(scope));
        if (fault == "no-change") row.ExpiryObservedAtUtc = null;
        Assert.Throws<InvalidOperationException>(() => Proof.RequireFirstExpiryWitnessChange(db, scope, batch, row.ExpiresAtUtc));
    }
    private static GroupBatchClaimStateRecord State(GroupScope scope) => new()
    {
        TenantId = scope.TenantId,
        CompanyId = scope.CompanyId,
        BindingId = scope.SourceBindingId,
        BatchId = Guid.NewGuid(),
        Epoch = 1,
        OwnerId = Guid.NewGuid(),
        OperationId = Guid.NewGuid(),
        IssuedAtUtc = DateTimeOffset.UtcNow,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(2)
    };
}
