using System.Data.Common;
using System.Text.Json;
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
    [InlineData("notes")]
    [InlineData("media")]
    [InlineData("coverage_gap")]
    [InlineData("foreign_dependencies")]
    [InlineData("cancelled")]
    public async Task NoWorkCommitRefusesUnsupportedInputAndStorageBeforeConnectionKeysEffectsOrWitness(string fault)
    {
        using var f = new Fixture(); using var foreign = new Fixture();
        var message = await f.CommitAsync(f.Payload(text: "Tra cứu tồn kho", kind:
            fault == "media" ? GroupSourceEventKind.Media : GroupSourceEventKind.NewText));
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
        var wire = fault == "notes" ? ProposalWire(Assert.Single(preparation.Candidates)).ToJsonString() :
            JsonSerializer.Serialize(new
            {
                version = GroupGroundedWorkProposal.FormatVersion,
                notes = Array.Empty<object>(),
                source_dispositions = preparation.Candidates.Select(x => new { message_id = x.MessageId.ToString("D"), revision = x.Revision, disposition = "no_work" })
            });
        var proposal = GroupGroundedWorkProposal.Parse(preparation, wire);
        var dependencies = await BrainReader(f).ReadAsync(handle, [], []);
        if (fault == "foreign_dependencies")
        {
            await foreign.CommitAsync(); dependencies = await BrainReader(foreign).ReadAsync(await foreign.ClaimAsync(), [], []);
        }
        var interceptor = new NoWorkForbiddenOpen();
        using var alternate = fault == "non_sql" ? null : new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=never_connect;Integrated Security=true;Encrypt=true;Connect Timeout=1;MultipleActiveResultSets=" +
                (fault == "mars" ? "true" : "false")).AddInterceptors(interceptor).Options);
        var database = alternate ?? f.Auth.Db;
        var sourceReader = new GroupBatchSourceReader(database, f.Worker, f.Auth.Clock, f.Keys, new GroupSourceContentProtector());
        var brainReader = new GroupBrainCurrentReader(database, f.Worker, f.Auth.Clock, f.Keys, new GroupBrainContentProtector());
        var store = new GroupNoWorkCommitStore(database, f.Worker, f.Auth.Clock, sourceReader, brainReader);
        var counts = await f.CountsAsync(); var reads = f.Keys.Reads;
        using var cancellation = new CancellationTokenSource(); if (fault == "cancelled") cancellation.Cancel();
        var operation = fault == "zero_operation" ? Guid.Empty : Guid.NewGuid();
        if (fault == "cancelled") await Assert.ThrowsAsync<OperationCanceledException>(() => store.CommitAsync(proposal, dependencies, operation, cancellation.Token));
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(proposal, dependencies, operation));
            Assert.Equal("Group work commit is unavailable.", error.Message); Assert.Null(error.InnerException);
        }
        Assert.Equal(0, interceptor.Attempts); Assert.Equal(reads, f.Keys.Reads); Assert.Equal(counts, await f.CountsAsync());
        Assert.Empty(await f.Auth.Db.GroupWorkCommitReceipts.ToArrayAsync()); Assert.Empty(await f.Auth.Db.GroupWorkSourceDispositions.ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupCustomerRequests.ToArrayAsync()); Assert.Empty(await f.Auth.Db.GroupNotesCommittedOutbox.ToArrayAsync());
        Assert.Null((await f.Auth.Db.GroupBatchClaimStates.AsNoTracking().SingleAsync()).ExpiryObservedAtUtc);
        Assert.False(database.ChangeTracker.HasChanges()); Assert.Null(database.Database.CurrentTransaction);
    }

    private sealed class NoWorkForbiddenOpen : DbConnectionInterceptor
    {
        internal int Attempts;
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        { Attempts++; throw new InvalidOperationException("Forbidden owned test connection."); }
    }
}
