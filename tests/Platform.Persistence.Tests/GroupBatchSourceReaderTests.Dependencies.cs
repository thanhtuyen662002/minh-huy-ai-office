using System.Text;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Fact]
    public async Task DependencyManifestCapturesActualScopedSourceBrainAuthorityWithoutPlaintextOrEffects()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(f.Payload(text: "owned-private-source 😀"));
        var claim = await f.ClaimAsync(); var seed = await SeedBrainAsync(f, claim, source, "owned-private-brain 😀");
        var context = await f.Reader.ReadAsync(claim, [source.MessageId]);
        var brain = await BrainReader(f).ReadAsync(claim, [seed.Request.Id], [seed.Glossary.Id]);
        var operation = Guid.NewGuid(); var counts = await f.CountsAsync();
        var bytes = GroupWorkDependencyManifest.Create(context, brain, operation);
        var manifest = GroupWorkDependencyManifest.Read(bytes);
        Assert.Equal(f.Auth.Scope, manifest.Scope); Assert.Equal(claim.Receipt.BatchId, manifest.BatchId);
        Assert.Equal(operation, manifest.OperationId); Assert.Equal(context.AllocatedThroughSequence, manifest.AllocatedThroughSequence);
        Assert.Equal(GroupBatchClaimStore.AuthorityFingerprint(claim.Authority), manifest.AuthoritySha256);
        var actual = Assert.Single(manifest.Sources); Assert.Equal(source.MessageId, actual.MessageId); Assert.Equal(source.Revision, actual.Revision);
        Assert.Equal(GroupWorkDependencyManifest.SourceFingerprint(context.Snapshots.Single()), actual.SnapshotSha256);
        Assert.Equal(2, manifest.Dependencies.Count);
        Assert.Equal(new[] { GroupBrainContentKind.RequestRevision, GroupBrainContentKind.GlossaryRevision }, manifest.Dependencies.Select(x => x.Kind));
        Assert.Equal(seed.Request.Id, manifest.Dependencies[0].RecordId); Assert.Equal(seed.Glossary.Id, manifest.Dependencies[1].RecordId);
        foreach (var phrase in new[] { "owned-private-source", "owned-private-brain", "nhập–xuất", "owned", f.Auth.Service.CredentialReference })
            Assert.DoesNotContain(phrase, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("owned-private", manifest.ToString());
        Assert.Equal(bytes, GroupWorkDependencyManifest.Create(context, brain, operation));
        Assert.Equal(counts, await f.CountsAsync()); Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
        Assert.Empty(await f.Auth.Db.GroupWorkCommitReceipts.ToArrayAsync());
    }

    [Fact]
    public async Task DependencyManifestPinsEveryBoundedContributorAtMaximumWithoutTruncation()
    {
        using var f = new Fixture(); var sourceIds = new List<Guid>(); GroupIngressCommittedReceipt? source = null;
        for (var index = 0; index < 100; index++)
        {
            source = await f.CommitAsync(f.Payload(messageId: "manifest-" + index, eventId: "manifest-" + index));
            sourceIds.Add(source.MessageId);
        }
        var claim = await f.ClaimAsync(); var requestIds = new List<Guid>(); var glossaryIds = new List<Guid>();
        for (var index = 0; index < 10; index++)
        {
            var seed = await SeedBrainAsync(f, claim, source!); requestIds.Add(seed.Request.Id); glossaryIds.Add(seed.Glossary.Id);
        }
        var context = await f.Reader.ReadAsync(claim, sourceIds.AsReadOnly());
        var brain = await BrainReader(f).ReadAsync(claim, requestIds.AsReadOnly(), glossaryIds.AsReadOnly());
        var operation = Guid.NewGuid(); var bytes = GroupWorkDependencyManifest.Create(context, brain, operation);
        Assert.Equal(6902, bytes.Length); var manifest = GroupWorkDependencyManifest.Read(bytes);
        Assert.Equal(sourceIds.Order(), manifest.Sources.Select(x => x.MessageId));
        Assert.Equal(100, manifest.Sources.Count); Assert.Equal(20, manifest.Dependencies.Count);
        Assert.Equal(requestIds.Order(), manifest.Dependencies.Where(x => x.Kind == GroupBrainContentKind.RequestRevision).Select(x => x.RecordId));
        Assert.Equal(glossaryIds.Order(), manifest.Dependencies.Where(x => x.Kind == GroupBrainContentKind.GlossaryRevision).Select(x => x.RecordId));
        var reversed = await f.Reader.ReadAsync(claim, sourceIds.AsEnumerable().Reverse().ToArray());
        Assert.Equal(bytes, GroupWorkDependencyManifest.Create(reversed, brain, operation));
        Assert.Empty(await f.Auth.Db.GroupWorkRawDispositions.ToArrayAsync());
    }

    [Theory]
    [InlineData("source-current-head")]
    [InlineData("source-cipher")]
    [InlineData("source-receipt")]
    [InlineData("source-reply")]
    [InlineData("brain-business")]
    [InlineData("brain-cipher")]
    [InlineData("brain-evidence")]
    [InlineData("brain-source-head")]
    [InlineData("glossary-version")]
    [InlineData("glossary-authority")]
    public async Task DependencyFingerprintChangesForEveryContributingPrivateMetadataOrCipherChange(string change)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var seed = await SeedBrainAsync(f, claim, source); var context = await f.Reader.ReadAsync(claim, [source.MessageId]);
        var brain = await BrainReader(f).ReadAsync(claim, [seed.Request.Id], [seed.Glossary.Id]);
        var snapshot = context.Snapshots.Single(); var request = brain.Snapshots[0]; var glossary = brain.Snapshots[1];
        var sourceHash = GroupWorkDependencyManifest.SourceFingerprint(snapshot);
        var brainHash = GroupWorkDependencyManifest.BrainFingerprint(request);
        var glossaryHash = GroupWorkDependencyManifest.BrainFingerprint(glossary);
        switch (change)
        {
            case "source-current-head": snapshot = snapshot with { CurrentHead = snapshot.CurrentHead with { Revision = 2 } }; break;
            case "source-cipher": snapshot = snapshot with { ProtectedContent = snapshot.ProtectedContent!.Select(x => (byte)(x ^ 1)).ToArray() }; break;
            case "source-receipt": snapshot = snapshot with { Receipt = snapshot.Receipt with { ListenerEpoch = 2 } }; break;
            case "source-reply": snapshot = snapshot with { ReplyToMessageId = "different" }; break;
            case "brain-business": request = request with { Request = request.Request! with { BusinessVersion = 2 } }; break;
            case "brain-cipher": request = request with { Revision = request.Revision with { ProtectedContent = request.Revision.ProtectedContent!.Select(x => (byte)(x ^ 1)).ToArray() } }; break;
            case "brain-evidence": request = request with { Evidence = [request.Evidence[0] with { Kind = GroupRequestEvidenceKind.HostMetadataAttention }] }; break;
            case "brain-source-head": request = request with { SourceHeads = [request.SourceHeads[0] with { Revision = 2 }] }; break;
            case "glossary-version": glossary = glossary with { Glossary = glossary.Glossary! with { Version = 2 } }; break;
            case "glossary-authority": glossary = glossary with { Glossary = glossary.Glossary! with { AllowExtraction = false } }; break;
        }
        Assert.True(sourceHash != GroupWorkDependencyManifest.SourceFingerprint(snapshot)
            || brainHash != GroupWorkDependencyManifest.BrainFingerprint(request)
            || glossaryHash != GroupWorkDependencyManifest.BrainFingerprint(glossary));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("truncated")]
    [InlineData("extra")]
    [InlineData("magic")]
    [InlineData("scope")]
    [InlineData("batch")]
    [InlineData("operation")]
    [InlineData("cutoff")]
    [InlineData("source-count")]
    [InlineData("brain-count")]
    [InlineData("message")]
    [InlineData("revision")]
    [InlineData("brain-kind")]
    [InlineData("brain-id")]
    [InlineData("brain-revision")]
    public async Task DependencyManifestClosedParserRefusesMalformedFramingIdentityAndBounds(string change)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var seed = await SeedBrainAsync(f, claim, source); var context = await f.Reader.ReadAsync(claim, [source.MessageId]);
        var brain = await BrainReader(f).ReadAsync(claim, [seed.Request.Id], [seed.Glossary.Id]);
        var bytes = GroupWorkDependencyManifest.Create(context, brain, Guid.NewGuid());
        switch (change)
        {
            case "empty": bytes = []; break;
            case "truncated": bytes = bytes[..^1]; break;
            case "extra": bytes = [.. bytes, 0]; break;
            case "magic": bytes[0] ^= 1; break;
            case "scope": Array.Clear(bytes, 8, 16); break;
            case "batch": Array.Clear(bytes, 56, 16); break;
            case "operation": Array.Clear(bytes, 72, 16); break;
            case "cutoff": Array.Clear(bytes, 88, 8); break;
            case "source-count": bytes[160] = 101; break;
            case "brain-count": bytes[161] = 21; break;
            case "message": Array.Clear(bytes, 162, 16); break;
            case "revision": Array.Clear(bytes, 178, 8); break;
            case "brain-kind": bytes[218] = 255; break;
            case "brain-id": Array.Clear(bytes, 219, 16); break;
            case "brain-revision": Array.Clear(bytes, 235, 8); break;
        }
        var error = Assert.Throws<InvalidOperationException>(() => GroupWorkDependencyManifest.Read(bytes));
        Assert.Equal("Group work dependencies are not available.", error.Message); Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("null")]
    [InlineData("version")]
    [InlineData("legacy-with-bytes")]
    [InlineData("legacy-entry")]
    public async Task DependencyManifestReplayRequiresExactOriginalAndLegacyAbsenceCannotBeParsedAsTerminalProof(string change)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var context = await f.Reader.ReadAsync(claim, [source.MessageId]); var brain = await BrainReader(f).ReadAsync(claim, [], []);
        var bytes = GroupWorkDependencyManifest.Create(context, brain, Guid.NewGuid());
        var receipt = new GroupWorkCommitReceiptRecord { DependencyManifestVersion = 1, DependencyManifest = bytes.ToArray() };
        GroupWorkDependencyManifest.RequireReplay(receipt, bytes);
        switch (change)
        {
            case "bytes": receipt.DependencyManifest[^1] ^= 1; break;
            case "null": receipt.DependencyManifest = null; break;
            case "version": receipt.DependencyManifestVersion = 2; break;
            case "legacy-with-bytes": receipt.DependencyManifestVersion = 0; break;
            case "legacy-entry": bytes = null!; break;
        }
        Assert.Throws<InvalidOperationException>(() => GroupWorkDependencyManifest.RequireReplay(receipt, bytes));
        receipt.DependencyManifestVersion = 0; receipt.DependencyManifest = null;
        GroupWorkDependencyManifest.RequireReplay(receipt, bytes); // Existing receipt compatibility only.
        Assert.Throws<InvalidOperationException>(() => GroupWorkDependencyManifest.Read(receipt.DependencyManifest));
    }

    [Theory]
    [InlineData("duplicate-source")]
    [InlineData("reorder-source")]
    [InlineData("duplicate-brain")]
    [InlineData("reorder-brain")]
    public async Task DependencyManifestRefusesDuplicateOrNoncanonicalContributorSets(string change)
    {
        using var f = new Fixture(); var first = await f.CommitAsync();
        var second = await f.CommitAsync(f.Payload(messageId: "second-manifest", eventId: "second-manifest"));
        var claim = await f.ClaimAsync(); var seed = await SeedBrainAsync(f, claim, first);
        var context = await f.Reader.ReadAsync(claim, [first.MessageId, second.MessageId]);
        var brain = await BrainReader(f).ReadAsync(claim, [seed.Request.Id], [seed.Glossary.Id]);
        var bytes = GroupWorkDependencyManifest.Create(context, brain, Guid.NewGuid());
        var offset = change.EndsWith("source", StringComparison.Ordinal) ? 162 : 274;
        var length = offset == 162 ? 56 : 57;
        var firstBytes = bytes.AsSpan(offset, length).ToArray();
        if (change.StartsWith("reorder", StringComparison.Ordinal)) bytes.AsSpan(offset + length, length).CopyTo(bytes.AsSpan(offset, length));
        firstBytes.CopyTo(bytes, offset + length);
        Assert.Throws<InvalidOperationException>(() => GroupWorkDependencyManifest.Read(bytes));
    }

    [Fact]
    public async Task DependencyManifestRevalidationCannotBorrowAnUnownedTransactionOrPerformInMemoryEffects()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var context = await f.Reader.ReadAsync(claim, [source.MessageId]); var brain = await BrainReader(f).ReadAsync(claim, [], []);
        var manifest = GroupWorkDependencyManifest.Read(GroupWorkDependencyManifest.Create(context, brain, Guid.NewGuid()));
        var counts = await f.CountsAsync(); var reads = f.Keys.Reads;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.RequireManifestUnchangedLockedAsync(claim, manifest, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => BrainReader(f).RequireManifestUnchangedLockedAsync(claim, manifest, default));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Reader.RequireManifestUnchangedLockedAsync(claim, manifest, canceled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => BrainReader(f).RequireManifestUnchangedLockedAsync(claim, manifest, canceled.Token));
        Assert.Equal(reads, f.Keys.Reads); Assert.Equal(counts, await f.CountsAsync());
        Assert.Empty(await f.Auth.Db.GroupWorkCommitReceipts.ToArrayAsync()); Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
    }
}
