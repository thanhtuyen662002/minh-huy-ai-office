using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MetadataInspectionRequiresOwnedSqlBeforeKeysOrEffects(bool sql, bool brain)
    {
        using var f = new Fixture(); var input = await MetadataInspectionInputAsync(f);
        using var db = new PlatformDbContext(sql
            ? new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(
                "Server=127.0.0.1,1;Database=never_connect;Integrated Security=true;Encrypt=true;Connect Timeout=1").Options
            : new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var sourceReader = new GroupBatchSourceReader(db, f.Worker, f.Auth.Clock, f.Keys, new());
        var brainReader = new GroupBrainCurrentReader(db, f.Worker, f.Auth.Clock, f.Keys, new());
        var reads = f.Keys.Reads; var counts = await f.CountsAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => brain
            ? brainReader.InspectManifestLockedAsync(input.Handle, input.Manifest, default)
            : sourceReader.InspectManifestLockedAsync(input.Handle, input.Manifest, default));
        Assert.Equal(brain ? "Group brain context is unavailable." : "Group batch source context is not available.", error.Message);
        Assert.Equal(reads, f.Keys.Reads); Assert.Equal(counts, await f.CountsAsync());
        Assert.False(db.ChangeTracker.HasChanges()); Assert.Empty(await f.Auth.Db.GroupWorkCommitReceipts.ToArrayAsync());
        if (sql) Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataInspectionPreservesCallerCancellationBeforeDisposedContext(bool brain)
    {
        using var f = new Fixture(); var input = await MetadataInspectionInputAsync(f); var reads = f.Keys.Reads;
        var sourceReader = f.Reader; var brainReader = BrainReader(f); f.Auth.Db.Dispose();
        using var caller = new CancellationTokenSource(); caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => brain
            ? brainReader.InspectManifestLockedAsync(input.Handle, input.Manifest, caller.Token)
            : sourceReader.InspectManifestLockedAsync(input.Handle, input.Manifest, caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Equal(reads, f.Keys.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataInspectionRejectsForeignWorkerBeforeDisposedContext(bool brain)
    {
        using var f = new Fixture(); var input = await MetadataInspectionInputAsync(f); var reads = f.Keys.Reads;
        var worker = f.Worker with { TenantId = Guid.NewGuid() };
        var sourceReader = new GroupBatchSourceReader(f.Auth.Db, worker, f.Auth.Clock, f.Keys, new());
        var brainReader = new GroupBrainCurrentReader(f.Auth.Db, worker, f.Auth.Clock, f.Keys, new());
        f.Auth.Db.Dispose();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain
            ? brainReader.InspectManifestLockedAsync(input.Handle, input.Manifest, default)
            : sourceReader.InspectManifestLockedAsync(input.Handle, input.Manifest, default));
        Assert.Equal(reads, f.Keys.Reads);
    }

    [Fact]
    public async Task MetadataInspectionPureProjectionsAreStableScopedAndContainNoNewPrivateReads()
    {
        using var f = new Fixture(); var input = await MetadataInspectionInputAsync(f);
        var reads = f.Keys.Reads; var counts = await f.CountsAsync();
        var source = GroupDependencyMetadataInspection.CompareSource(input.Manifest, input.Source.Coverage, input.Source.Snapshots.ToArray());
        var brain = GroupDependencyMetadataInspection.CompareBrain(input.Manifest, input.Brain.Snapshots.ToArray());
        Assert.True(source.Matches); Assert.True(brain.Matches); Assert.NotEqual(source.SnapshotSha256, brain.SnapshotSha256);
        Assert.Equal(source, GroupDependencyMetadataInspection.CompareSource(input.Manifest, input.Source.Coverage, input.Source.Snapshots.ToArray()));
        Assert.Equal(brain, GroupDependencyMetadataInspection.CompareBrain(input.Manifest, input.Brain.Snapshots.ToArray()));
        foreach (var comparison in new[] { source, brain })
        {
            var observation = new GroupDependencyMetadataInspection(input.Verdict, comparison.Matches, comparison.SnapshotSha256);
            Assert.Same(input.Verdict, observation.Verdict); Assert.True(observation.Matches);
            Assert.Matches("^[0-9A-F]{64}$", observation.SnapshotSha256!);
            Assert.Equal("Group dependency inspection (private metadata, no authority).", observation.ToString());
            Assert.DoesNotContain(observation.SnapshotSha256!, observation.ToString(), StringComparison.Ordinal);
        }
        Assert.False(typeof(GroupDependencyMetadataInspection).IsPublic); Assert.True(typeof(GroupDependencyMetadataInspection).IsSealed);
        Assert.Empty(typeof(GroupDependencyMetadataInspection).GetProperties());
        Assert.Equal(reads, f.Keys.Reads); Assert.Equal(counts, await f.CountsAsync());
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges()); Assert.Empty(await f.Auth.Db.GroupWorkCommitReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData("current-head")]
    [InlineData("cipher")]
    [InlineData("receipt")]
    [InlineData("reply")]
    [InlineData("sender")]
    [InlineData("disposition")]
    [InlineData("missing")]
    [InlineData("order")]
    public async Task MetadataInspectionSourceChangesRemainNonspecificAndBindTheFullCurrentProjection(string change)
    {
        using var f = new Fixture(); var input = await MetadataInspectionInputAsync(f); var current = input.Source.Snapshots.ToArray();
        var original = GroupDependencyMetadataInspection.CompareSource(input.Manifest, input.Source.Coverage, current);
        switch (change)
        {
            case "current-head": current[0] = current[0] with { CurrentHead = current[0].CurrentHead with { Revision = 2 } }; break;
            case "cipher": current[0] = current[0] with { ProtectedContent = current[0].ProtectedContent!.Select(x => (byte)(x ^ 1)).ToArray() }; break;
            case "receipt": current[0] = current[0] with { Receipt = current[0].Receipt with { ListenerEpoch = 2 } }; break;
            case "reply": current[0] = current[0] with { ReplyToMessageId = "different" }; break;
            case "sender": current[0] = current[0] with { SenderId = "different" }; break;
            case "disposition": current[0] = current[0] with { Disposition = GroupBatchSourceDisposition.ObsoleteGeneration }; break;
            case "missing": current = current[1..]; break;
            case "order": Array.Reverse(current); break;
        }
        // Pure metadata discrimination only; mutated objects do not qualify SQL validation.
        var changed = GroupDependencyMetadataInspection.CompareSource(input.Manifest, input.Source.Coverage, current);
        Assert.False(changed.Matches); Assert.NotEqual(original.SnapshotSha256, changed.SnapshotSha256);
        var observation = new GroupDependencyMetadataInspection(input.Verdict, changed.Matches, changed.SnapshotSha256);
        Assert.False(observation.Matches); Assert.DoesNotContain("edit", observation.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("business")]
    [InlineData("cipher")]
    [InlineData("evidence")]
    [InlineData("source-head")]
    [InlineData("glossary")]
    [InlineData("missing")]
    [InlineData("order")]
    public async Task MetadataInspectionBrainHashBindsBusinessCipherEvidenceAndSelectedOrder(string change)
    {
        using var f = new Fixture(); var input = await MetadataInspectionInputAsync(f); var current = input.Brain.Snapshots.ToArray();
        var original = GroupDependencyMetadataInspection.CompareBrain(input.Manifest, current);
        switch (change)
        {
            case "business": current[0] = current[0] with { Request = current[0].Request! with { BusinessVersion = 2 } }; break;
            case "cipher": current[0] = current[0] with { Revision = current[0].Revision with { ProtectedContent = current[0].Revision.ProtectedContent!.Select(x => (byte)(x ^ 1)).ToArray() } }; break;
            case "evidence": current[0] = current[0] with { Evidence = [current[0].Evidence[0] with { Kind = GroupRequestEvidenceKind.HostMetadataAttention }] }; break;
            case "source-head": current[0] = current[0] with { SourceHeads = [current[0].SourceHeads[0] with { Revision = 2 }] }; break;
            case "glossary": current[1] = current[1] with { Glossary = current[1].Glossary! with { Version = 2 } }; break;
            case "missing": current = current[1..]; break;
            case "order": Array.Reverse(current); break;
        }
        var changed = GroupDependencyMetadataInspection.CompareBrain(input.Manifest, current);
        Assert.False(changed.Matches); Assert.NotEqual(original.SnapshotSha256, changed.SnapshotSha256);
    }

    [Fact]
    public async Task MetadataInspectionCoverageChangeIsBoundEvenWhenAllSelectedSourceHashesAreUnchanged()
    {
        using var f = new Fixture(); var input = await MetadataInspectionInputAsync(f);
        var original = GroupDependencyMetadataInspection.CompareSource(input.Manifest, input.Source.Coverage, input.Source.Snapshots.ToArray());
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
        var coverage = await GroupBatchCoverageSnapshot.ReadAsync(f.Auth.Db, f.Auth.Scope, f.Auth.Account.Id, default);
        var changed = GroupDependencyMetadataInspection.CompareSource(input.Manifest, coverage, input.Source.Snapshots.ToArray());
        Assert.False(changed.Matches); Assert.NotEqual(original.SnapshotSha256, changed.SnapshotSha256);
    }

    [Fact]
    public async Task MetadataInspectionAlreadyChangedAfterCutoffDoesNotInventANewlyProvedEdit()
    {
        using var f = new Fixture(); var first = await f.CommitAsync(); var claim = await f.ClaimAsync();
        await f.CommitAsync(f.Payload(eventId: "already-edited", kind: GroupSourceEventKind.Edit, text: "later private"));
        var source = await f.Reader.ReadAsync(claim, [first.MessageId]); var brain = await BrainReader(f).ReadAsync(claim, [], []);
        var snapshot = Assert.Single(source.Snapshots); Assert.Equal(GroupBatchSourceDisposition.ChangedAfterCutoff, snapshot.Disposition);
        var manifest = GroupWorkDependencyManifest.Read(GroupWorkDependencyManifest.Create(source, brain, Guid.NewGuid()));
        var same = GroupDependencyMetadataInspection.CompareSource(manifest, source.Coverage, [snapshot]);
        var noBrain = GroupDependencyMetadataInspection.CompareBrain(manifest, []);
        Assert.True(noBrain.Matches); Assert.NotEqual(same.SnapshotSha256, noBrain.SnapshotSha256);
        Assert.True(same.Matches); Assert.Equal(0, f.Keys.Reads);
        var changed = GroupDependencyMetadataInspection.CompareSource(manifest, source.Coverage,
            [snapshot with { CurrentHead = snapshot.CurrentHead with { Revision = snapshot.CurrentHead.Revision + 1 } }]);
        Assert.False(changed.Matches); Assert.NotEqual(same.SnapshotSha256, changed.SnapshotSha256);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("batch")]
    [InlineData("operation")]
    [InlineData("cutoff")]
    [InlineData("authority")]
    public async Task MetadataInspectionHashCannotBeSubstitutedAcrossOriginalScopeOrIdentity(string change)
    {
        using var f = new Fixture(); var input = await MetadataInspectionInputAsync(f);
        var bytes = GroupWorkDependencyManifest.Create(input.Source, input.Brain, input.Manifest.OperationId);
        var offset = change switch { "scope" => 8, "batch" => 56, "operation" => 72, "cutoff" => 88, _ => 96 };
        bytes[offset] ^= 1; var other = GroupWorkDependencyManifest.Read(bytes);
        // Pure hash binding is not authorization. The reader separately checks scope/batch/authority before SQL.
        var originalSource = GroupDependencyMetadataInspection.CompareSource(input.Manifest, input.Source.Coverage, input.Source.Snapshots.ToArray());
        var changedSource = GroupDependencyMetadataInspection.CompareSource(other, input.Source.Coverage, input.Source.Snapshots.ToArray());
        var originalBrain = GroupDependencyMetadataInspection.CompareBrain(input.Manifest, input.Brain.Snapshots.ToArray());
        var changedBrain = GroupDependencyMetadataInspection.CompareBrain(other, input.Brain.Snapshots.ToArray());
        Assert.NotEqual(originalSource.SnapshotSha256, changedSource.SnapshotSha256);
        Assert.NotEqual(originalBrain.SnapshotSha256, changedBrain.SnapshotSha256);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataInspectionRetainsExactExpiryVerdictAndDiscardsAnyComputedComparison(bool matches)
    {
        using var f = new Fixture(); var input = await MetadataInspectionInputAsync(f);
        var retained = new GroupBatchClaimExpiryObservation(input.Handle, input.Handle.Receipt.ExpiresAtUtc);
        var verdict = new GroupBatchClaimFenceVerdict.Expired(retained);
        var observation = new GroupDependencyMetadataInspection(verdict, matches, new string('A', 64));
        Assert.Same(verdict, observation.Verdict);
        Assert.Same(retained, ((GroupBatchClaimFenceVerdict.Expired)observation.Verdict).Observation);
        Assert.Null(observation.Matches); Assert.Null(observation.SnapshotSha256);
    }

    private static async Task<MetadataInspectionInput> MetadataInspectionInputAsync(Fixture f)
    {
        var source = await f.CommitAsync(f.Payload(text: "owned-private-source"));
        var other = await f.CommitAsync(f.Payload(messageId: "other-metadata", eventId: "other-metadata", text: "other-private-source"));
        var allocation = await f.AllocateAsync(); var claim = await f.ClaimAllocatedAsync(allocation.BatchId);
        var seed = await SeedBrainAsync(f, claim, source, "owned-private-brain");
        var selected = new[] { source.MessageId, other.MessageId }.Order().ToArray();
        var context = await f.Reader.ReadAsync(claim, selected);
        var brain = await BrainReader(f).ReadAsync(claim, [seed.Request.Id], [seed.Glossary.Id]);
        var manifest = GroupWorkDependencyManifest.Read(GroupWorkDependencyManifest.Create(context, brain, Guid.NewGuid()));
        return new(claim, new(allocation), context, brain, manifest);
    }

    private sealed record MetadataInspectionInput(GroupBatchClaimHandle Handle, GroupBatchClaimFenceVerdict.Current Verdict,
        GroupBatchSourceContext Source, GroupBrainPrivateContext Brain, GroupWorkDependencyManifest Manifest);
}
