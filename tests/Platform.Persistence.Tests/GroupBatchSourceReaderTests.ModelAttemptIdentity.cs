using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Fact]
    public async Task ModelAttemptIdentityIsStableAcrossClaimReplacementForExactOriginalInput()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var first = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(first, [source.MessageId]));
        var brain = await BrainReader(f).ReadAsync(first, [], []);
        var original = GroupAutomaticModelContext.Create(preparation, brain, "existing-model", 64000);
        var digest = GroupModelAttemptBudget.InputIdentity(original);
        f.Auth.Clock.Current = first.Receipt.ExpiresAtUtc.AddSeconds(1);
        var replacement = await f.ClaimAllocatedAsync(first.Receipt.BatchId);
        Assert.NotEqual(first.Receipt.OperationId, replacement.Receipt.OperationId);
        Assert.Equal(first.Receipt.Epoch + 1, replacement.Receipt.Epoch);
        var nextPreparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(replacement, [source.MessageId]));
        var nextBrain = await BrainReader(f).ReadAsync(replacement, [], []);
        var next = GroupAutomaticModelContext.Create(nextPreparation, nextBrain, "existing-model", 64000);
        Assert.Equal(digest, GroupModelAttemptBudget.InputIdentity(next));
        Assert.Matches("^[0-9A-F]{64}$", digest);
        Assert.NotEqual(digest, GroupModelAttemptBudget.InputIdentity(GroupAutomaticModelContext.Create(nextPreparation, nextBrain, "another-model", 64000)));
        Assert.NotEqual(digest, GroupModelAttemptBudget.InputIdentity(GroupAutomaticModelContext.Create(nextPreparation, nextBrain, "existing-model", 63999)));
    }

    [Fact]
    public async Task ModelAttemptIdentityBindsQuarantinedDependenciesEvenWhenProviderInputIsIdentical()
    {
        using var f = new Fixture(); var source = await f.CommitAsync();
        var secret = await f.CommitAsync(f.Payload("secret", "secret-event", "password=PRIVATE_SECRET_VALUE_7241"));
        var claim = await f.ClaimAsync();
        var first = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var all = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId, secret.MessageId]));
        var brain = await BrainReader(f).ReadAsync(claim, [], []);
        var one = GroupAutomaticModelContext.Create(first, brain, "existing-model", 64000);
        var two = GroupAutomaticModelContext.Create(all, brain, "existing-model", 64000); var id = Guid.NewGuid();
        Assert.Equal(one.CreateGatewayRequest(id), two.CreateGatewayRequest(id));
        var before = await f.CountsAsync(); var keys = f.Keys.Reads;
        Assert.NotEqual(GroupModelAttemptBudget.InputIdentity(one), GroupModelAttemptBudget.InputIdentity(two));
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(keys, f.Keys.Reads);
    }

    [Fact]
    public async Task ModelAttemptIdentityBindsFullScopedHistoryAndItsBusinessMetadata()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var empty = await BrainReader(f).ReadAsync(claim, [], []);
        var noHistory = GroupAutomaticModelContext.Create(preparation, empty, "existing-model", 64000);
        var brain = await ModelBrainAsync(f, claim, source, preparation);
        var history = GroupAutomaticModelContext.Create(preparation, brain, "existing-model", 64000);
        Assert.NotEqual(GroupModelAttemptBudget.InputIdentity(noHistory), GroupModelAttemptBudget.InputIdentity(history));
        var stored = await f.Auth.Db.GroupCustomerRequests.SingleAsync();
        stored.BusinessVersion++; await f.Auth.Db.SaveChangesAsync();
        var changedBrain = await BrainReader(f).ReadAsync(claim,
            brain.Items.Where(x => x.Kind == GroupBrainContentKind.RequestRevision).Select(x => x.RecordId).ToArray(),
            brain.Items.Where(x => x.Kind == GroupBrainContentKind.GlossaryRevision).Select(x => x.RecordId).ToArray());
        var changedHistory = GroupAutomaticModelContext.Create(preparation, changedBrain, "existing-model", 64000);
        var requestId = Guid.NewGuid();
        Assert.Equal(history.CreateGatewayRequest(requestId), changedHistory.CreateGatewayRequest(requestId));
        Assert.NotEqual(GroupModelAttemptBudget.InputIdentity(history), GroupModelAttemptBudget.InputIdentity(changedHistory));
        Assert.Throws<ArgumentNullException>(() => GroupModelAttemptBudget.InputIdentity(null!));
    }
}
