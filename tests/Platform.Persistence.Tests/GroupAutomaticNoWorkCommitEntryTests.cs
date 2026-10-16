using System.Text.Json;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData("non_sql")]
    [InlineData("mars")]
    [InlineData("zero_operation")]
    [InlineData("notes")]
    [InlineData("media")]
    [InlineData("empty_media")]
    [InlineData("quarantined")]
    [InlineData("coverage_gap")]
    [InlineData("foreign_dependencies")]
    [InlineData("cancelled")]
    [InlineData("dirty")]
    [InlineData("ambient")]
    [InlineData("foreign_worker")]
    [InlineData("valid_guarded_open")]
    [InlineData("invalid_connection")]
    [InlineData("empty_plain")]
    [InlineData("recalled")]
    [InlineData("obsolete")]
    [InlineData("changed_cutoff")]
    public async Task AutomaticNoWorkEntryKeepsSealedOutcomesAndDeniesUnsafeEffectsBeforeConnectionOrWitness(string fault)
    {
        using var f = new Fixture(); using var foreign = new Fixture();
        var message = await f.CommitAsync(f.Payload(text: fault == "quarantined" ? "Password=PRIVATE_SENTINEL_72691"
            : fault is "empty_plain" or "empty_media" ? "" : "Cảm ơn", kind:
            fault is "media" or "empty_media" ? GroupSourceEventKind.Media : GroupSourceEventKind.NewText));
        GroupBatchClaimHandle handle;
        if (fault is "obsolete" or "changed_cutoff")
        {
            var allocation = await f.AllocateAsync();
            if (fault == "obsolete")
            {
                f.Auth.Binding.DeletionGeneration++; f.Auth.Binding.Version++; await f.Auth.Db.SaveChangesAsync();
            }
            else
            {
                (await f.Auth.Db.GroupListenerLeases.SingleAsync()).ExpiresAtUtc = f.Auth.Clock.Current.AddSeconds(30);
                await f.Auth.Db.SaveChangesAsync();
                await f.CommitAsync(f.Payload(eventId: "automatic-no-work-post-cutoff", kind: GroupSourceEventKind.Edit, text: "changed"));
            }
            handle = await f.ClaimAllocatedAsync(allocation.BatchId);
        }
        else
        {
            if (fault == "recalled") await f.CommitAsync(f.Payload(eventId: "automatic-no-work-recall", kind: GroupSourceEventKind.Recall, text: ""));
            handle = await f.ClaimAsync();
        }
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
            var wire = fault == "notes" ? ProposalWire(Assert.Single(preparation.Candidates)).ToJsonString() : JsonSerializer.Serialize(new
            {
                version = GroupGroundedWorkProposal.FormatVersion,
                notes = Array.Empty<object>(),
                source_dispositions = preparation.Candidates.Select(x => new { message_id = x.MessageId.ToString("D"), revision = x.Revision, disposition = "no_work" })
            });
            proposal = GroupGroundedWorkProposal.Parse(preparation, wire);
        }
        var plan = GroupAutomaticNotePlan.Create(preparation, proposal);
        if (fault is "recalled" or "obsolete" or "changed_cutoff" or "empty_plain" or "valid_guarded_open")
        {
            Assert.Equal(0, plan.NoteCount);
            Assert.Equal(fault switch
            {
                "recalled" => GroupWorkSourceOutcome.Recalled,
                "obsolete" => GroupWorkSourceOutcome.ObsoleteGeneration,
                "changed_cutoff" => GroupWorkSourceOutcome.ChangedAfterCutoff,
                _ => GroupWorkSourceOutcome.NoWork
            }, Assert.Single(plan.SourceDispositions).Outcome);
        }
        var dependencies = await BrainReader(f).ReadAsync(handle, [], []);
        if (fault == "foreign_dependencies")
        {
            await foreign.CommitAsync(); dependencies = await BrainReader(foreign).ReadAsync(await foreign.ClaimAsync(), [], []);
        }
        var interceptor = new NoWorkForbiddenOpen();
        using var alternate = fault == "non_sql" ? null : new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer(fault == "invalid_connection" ? "NotAConnectionProperty=unavailable" :
                "Server=127.0.0.1,1;Database=never_connect;Integrated Security=true;Encrypt=true;Connect Timeout=1;MultipleActiveResultSets=" +
                (fault == "mars" ? "true" : "false")).AddInterceptors(interceptor).Options);
        var database = alternate ?? f.Auth.Db;
        if (fault == "dirty") database.Add(new GroupWorkCommitReceiptRecord());
        var worker = fault == "foreign_worker" ? f.Worker with { TenantId = Guid.NewGuid() } : f.Worker;
        var store = new GroupNoWorkCommitStore(database, worker, f.Auth.Clock,
            new(database, worker, f.Auth.Clock, f.Keys, new()), new(database, worker, f.Auth.Clock, f.Keys, new()));
        var counts = await f.CountsAsync(); var reads = f.Keys.Reads;
        using var cancellation = new CancellationTokenSource(); if (fault == "cancelled") cancellation.Cancel();
        using var ambient = fault == "ambient" ? new TransactionScope(TransactionScopeAsyncFlowOption.Enabled) : null;
        var operation = fault == "zero_operation" ? Guid.Empty : Guid.NewGuid();
        if (fault == "cancelled") await Assert.ThrowsAsync<OperationCanceledException>(() => store.CommitAutomaticAsync(plan, dependencies, operation, cancellation.Token));
        else if (fault == "foreign_worker") await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.CommitAutomaticAsync(plan, dependencies, operation));
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAutomaticAsync(plan, dependencies, operation));
            Assert.Equal("Group work commit is unavailable.", error.Message); Assert.Null(error.InnerException);
        }
        Assert.Equal(fault is "valid_guarded_open" or "empty_plain" or "recalled" or "obsolete" or "changed_cutoff" ? 1 : 0, interceptor.Attempts);
        Assert.Equal(counts, await f.CountsAsync()); Assert.Equal(reads, f.Keys.Reads);
        Assert.Empty(await f.Auth.Db.GroupWorkCommitReceipts.ToArrayAsync()); Assert.Empty(await f.Auth.Db.GroupWorkSourceDispositions.ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupCustomerRequests.ToArrayAsync()); Assert.Empty(await f.Auth.Db.GroupRequestRevisions.ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupRequestEvidence.ToArrayAsync()); Assert.Empty(await f.Auth.Db.GroupNotesCommittedOutbox.ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupNotesCommittedItems.ToArrayAsync());
        Assert.Null((await f.Auth.Db.GroupBatchClaimStates.AsNoTracking().SingleAsync()).ExpiryObservedAtUtc);
        if (fault != "dirty") Assert.False(database.ChangeTracker.HasChanges());
        Assert.Null(database.Database.CurrentTransaction);
    }
}
