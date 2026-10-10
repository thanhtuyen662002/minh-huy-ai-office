extern alias GroupReferenceProof;

using Microsoft.EntityFrameworkCore;
using GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedNoteRuntimeObservationTests
{
    [Theory]
    [InlineData("note-expiry", true)]
    [InlineData("note-key-expiry", false)]
    public async Task ActualNegativeModeProbesObserveUnexpectedStagingAndSavepoints(string mode, bool expireAfterFlush)
    {
        var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var clock = new GroupNoteRuntimeProof.OwnedClock(DateTimeOffset.UtcNow);
        var evidence = new GroupNoteRuntimeProof.EffectEvidence(scope, clock);
        var operation = Guid.NewGuid(); evidence.ObserveCommit(mode, operation, clock.Current.AddMinutes(2));
        Assert.True(evidence.Armed); Assert.Equal(expireAfterFlush, evidence.ExpireAfterFlush);
        using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(new GroupNoteRuntimeProof.FlushProbe(evidence)).Options);
        database.Add(new GroupWorkCommitReceiptRecord
        {
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            BindingId = scope.SourceBindingId,
            BatchId = Guid.NewGuid(),
            OperationId = operation
        });
        // The actual interceptor must observe the write and reject this one-row
        // graph; an unarmed key-expiry observer would silently accept it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
        Assert.True(evidence.StagedObserved); Assert.False(evidence.Flushed);
        var probe = new GroupNoteRuntimeProof.RollbackProbe(evidence);
        // An armed actual savepoint callback must inspect the supplied SQL
        // transaction. A deliberately absent transaction cannot be ignored.
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await probe.CreatingSavepointAsync(null!, null!, default));
        Assert.Equal(0, evidence.SavepointChecks);
        Assert.False(evidence.RolledBack); Assert.NotEqual(evidence.Expires, clock.Current);
    }

    [Fact]
    public void CommitObservationRefusesUnknownModeAndZeroOperation()
    {
        var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var clock = new GroupNoteRuntimeProof.OwnedClock(DateTimeOffset.UtcNow);
        var evidence = new GroupNoteRuntimeProof.EffectEvidence(scope, clock);
        Assert.Throws<InvalidOperationException>(() => evidence.ObserveCommit("unknown", Guid.NewGuid(), clock.Current));
        Assert.Throws<InvalidOperationException>(() => evidence.ObserveCommit("note-key-expiry", Guid.Empty, clock.Current));
        evidence.ObserveCommit("note-commit", Guid.NewGuid(), clock.Current);
        Assert.False(evidence.Armed); Assert.False(evidence.ExpireAfterFlush);
    }

    [Theory]
    [InlineData("new_status")]
    [InlineData("new_updated_time")]
    [InlineData("protected_revision")]
    [InlineData("evidence")]
    [InlineData("receipt")]
    [InlineData("disposition")]
    [InlineData("outbox")]
    [InlineData("item")]
    public async Task ActualFullGraphOracleDetectsChangedRowsWithUnchangedCounts(string mutation)
    {
        var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var operation = Guid.NewGuid(); var batch = Guid.NewGuid(); var request = Guid.NewGuid(); var outbox = Guid.NewGuid();
        using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var head = new GroupCustomerRequestRecord
        {
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            BindingId = scope.SourceBindingId,
            Id = request,
            OriginBatchId = batch,
            OriginOperationId = operation,
            BusinessStatus = GroupNoteBusinessStatus.NeedsClarification,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        var revision = new GroupRequestRevisionRecord
        {
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            BindingId = scope.SourceBindingId,
            RequestId = request,
            Revision = 1,
            ProtectedContent = [1, 2, 3]
        };
        var evidence = new GroupRequestEvidenceRecord
        {
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            BindingId = scope.SourceBindingId,
            RequestId = request,
            RequestRevision = 1,
            Ordinal = 1,
            MessageId = Guid.NewGuid(),
            MessageRevision = 1
        };
        var receipt = new GroupWorkCommitReceiptRecord
        {
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            BindingId = scope.SourceBindingId,
            BatchId = batch,
            OperationId = operation,
            NoteCount = 1
        };
        var disposition = new GroupWorkSourceDispositionRecord
        {
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            BindingId = scope.SourceBindingId,
            BatchId = batch,
            OperationId = operation,
            MessageId = evidence.MessageId,
            MessageRevision = 1,
            Outcome = GroupWorkSourceOutcome.Work
        };
        var pending = new GroupNotesCommittedOutboxRecord
        {
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            BindingId = scope.SourceBindingId,
            Id = outbox,
            BatchId = batch,
            OperationId = operation,
            NoteCount = 1
        };
        var item = new GroupNotesCommittedItemRecord
        {
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            BindingId = scope.SourceBindingId,
            OutboxId = outbox,
            Ordinal = 1,
            RequestId = request,
            RequestRevision = 1
        };
        database.AddRange(head, revision, evidence, receipt, disposition, pending, item); await database.SaveChangesAsync();
        var original = await GroupNoteRuntimeProof.CommitGraphDigestAsync(database, scope, operation, default);
        Assert.Equal(original, await GroupNoteRuntimeProof.CommitGraphDigestAsync(database, scope, operation, default));
        switch (mutation)
        {
            case "new_status": head.BusinessStatus = GroupNoteBusinessStatus.New; break;
            case "new_updated_time": head.UpdatedAtUtc = head.UpdatedAtUtc.AddTicks(1); break;
            case "protected_revision": revision.ProtectedContent = [1, 2, 4]; break;
            case "evidence": evidence.Kind = GroupRequestEvidenceKind.HostMetadataAttention; break;
            case "receipt": receipt.NoteCount = 2; break;
            case "disposition": disposition.Outcome = GroupWorkSourceOutcome.NoWork; break;
            case "outbox": pending.PublishAttempts = 1; break;
            case "item": item.RequestRevision = 2; break;
            default: throw new InvalidOperationException();
        }
        await database.SaveChangesAsync();
        Assert.NotEqual(original, await GroupNoteRuntimeProof.CommitGraphDigestAsync(database, scope, operation, default));
        Assert.Equal(7, database.ChangeTracker.Entries().Count());
        Assert.False(database.ChangeTracker.HasChanges());
    }
}
