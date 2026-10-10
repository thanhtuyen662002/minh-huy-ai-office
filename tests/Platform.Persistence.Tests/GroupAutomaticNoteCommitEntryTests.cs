using System.Data.Common;
using System.Text.Json;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData("non_sql")]
    [InlineData("mars")]
    [InlineData("zero_operation")]
    [InlineData("no_notes")]
    [InlineData("media")]
    [InlineData("quarantined_media")]
    [InlineData("empty_media")]
    [InlineData("coverage_gap")]
    [InlineData("foreign_dependencies")]
    [InlineData("cancelled")]
    [InlineData("dirty")]
    [InlineData("ambient")]
    [InlineData("foreign_worker")]
    [InlineData("valid_guarded_open")]
    [InlineData("invalid_connection")]
    public async Task AutomaticNoteCommitFencesMixedAndHostOnlyPlansBeforeKeysEffectsOrWitness(string fault)
    {
        using var f = new Fixture(); using var foreign = new Fixture();
        var message = await f.CommitAsync(f.Payload(text: fault == "quarantined_media" ? "Password=PRIVATE_SENTINEL_72691" : fault == "empty_media" ? "" : "Tra cứu tồn kho", kind:
            fault is "media" or "quarantined_media" or "empty_media" ? GroupSourceEventKind.Media : GroupSourceEventKind.NewText));
        var handle = await f.ClaimAsync();
        if (fault == "coverage_gap")
        {
            f.Auth.Db.Add(new GroupCoverageGapRecord
            {
                TenantId = f.Auth.Scope.TenantId,
                CompanyId = f.Auth.Scope.CompanyId,
                BindingId = f.Auth.Scope.SourceBindingId,
                Id = Guid.NewGuid(),
                AfterCommittedSequence = 1,
                Reason = "owned-gap",
                OpenedAtUtc = f.Auth.Clock.Current
            });
            await f.Auth.Db.SaveChangesAsync();
        }
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(handle, [message.MessageId]));
        GroupGroundedWorkProposal? proposal = null;
        if (preparation.Candidates.Count != 0)
        {
            var wire = fault == "no_notes" ? JsonSerializer.Serialize(new
            {
                version = GroupGroundedWorkProposal.FormatVersion,
                notes = Array.Empty<object>(),
                source_dispositions = preparation.Candidates.Select(x => new { message_id = x.MessageId.ToString("D"), revision = x.Revision, disposition = "no_work" })
            }) : ProposalWire(Assert.Single(preparation.Candidates)).ToJsonString();
            proposal = GroupGroundedWorkProposal.Parse(preparation, wire);
        }
        var plan = GroupAutomaticNotePlan.Create(preparation, proposal);
        var dependencies = await BrainReader(f).ReadAsync(handle, [], []);
        if (fault == "foreign_dependencies")
        {
            await foreign.CommitAsync(); dependencies = await BrainReader(foreign).ReadAsync(await foreign.ClaimAsync(), [], []);
        }
        var interceptor = new NoteForbiddenOpen();
        using var alternate = fault == "non_sql" ? null : new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer(fault == "invalid_connection" ? "NotAConnectionProperty=unavailable" :
                "Server=127.0.0.1,1;Database=never_connect;Integrated Security=true;Encrypt=true;Connect Timeout=1;MultipleActiveResultSets=" +
                (fault == "mars" ? "true" : "false")).AddInterceptors(interceptor).Options);
        var database = alternate ?? f.Auth.Db;
        if (fault == "dirty") database.Add(new GroupNotesCommittedOutboxRecord());
        var ownedKeys = new NoteForbiddenKeys();
        var worker = fault == "foreign_worker" ? f.Worker with { TenantId = Guid.NewGuid() } : f.Worker;
        var store = new GroupNoteCommitStore(database, worker, f.Auth.Clock,
            new(database, worker, f.Auth.Clock, ownedKeys, new()), new(database, worker, f.Auth.Clock, ownedKeys, new()), ownedKeys, new());
        var counts = await f.CountsAsync(); var reads = f.Keys.Reads;
        using var cancellation = new CancellationTokenSource(); if (fault == "cancelled") cancellation.Cancel();
        using var ambient = fault == "ambient" ? new TransactionScope(TransactionScopeAsyncFlowOption.Enabled) : null;
        var operation = fault == "zero_operation" ? Guid.Empty : Guid.NewGuid();
        if (fault == "cancelled") await Assert.ThrowsAsync<OperationCanceledException>(() => store.CommitAutomaticAsync(plan, dependencies, operation, cancellation.Token));
        else if (fault == "foreign_worker") await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.CommitAutomaticAsync(plan, dependencies, operation));
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAutomaticAsync(plan, dependencies, operation));
            Assert.Equal("Group note commit is unavailable.", error.Message); Assert.Null(error.InnerException);
        }
        Assert.Equal(fault is "valid_guarded_open" or "media" or "quarantined_media" or "empty_media" ? 1 : 0, interceptor.Attempts);
        Assert.Equal(0, ownedKeys.Calls); Assert.Equal(reads, f.Keys.Reads); Assert.Equal(counts, await f.CountsAsync());
        Assert.Empty(await f.Auth.Db.GroupWorkCommitReceipts.ToArrayAsync()); Assert.Empty(await f.Auth.Db.GroupWorkSourceDispositions.ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupCustomerRequests.ToArrayAsync()); Assert.Empty(await f.Auth.Db.GroupRequestRevisions.ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupRequestEvidence.ToArrayAsync()); Assert.Empty(await f.Auth.Db.GroupNotesCommittedOutbox.ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupNotesCommittedItems.ToArrayAsync());
        Assert.Null((await f.Auth.Db.GroupBatchClaimStates.AsNoTracking().SingleAsync()).ExpiryObservedAtUtc);
        if (fault != "dirty") Assert.False(database.ChangeTracker.HasChanges());
        Assert.Null(database.Database.CurrentTransaction);
    }

}
