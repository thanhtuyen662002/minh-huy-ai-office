using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    // Copies the frozen actual local FenceAsync body byte for byte. The only
    // seam is work-proof completion advancing clock across expiry; real claim
    // inspection and metadata witness retirement are unchanged InMemory code.
    [Fact]
    public async Task ReviewerBrainFinalWorkProofMustPrecedeExpiryVerdict()
    {
        using var f = new Fixture(); await f.CommitAsync(); var handle = await f.ClaimAsync();
        var claims = new GroupBatchClaimStore(f.Auth.Db, f.Worker, f.Auth.Clock);
        var permissions = new ReviewerExpiryAdvanceWorkProof(() => f.Auth.Clock.Current = handle.Receipt.ExpiresAtUtc);
        var token = CancellationToken.None;
        await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(f.Auth.Db, token);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(FenceAsync);
        var state = await f.Auth.Db.GroupBatchClaimStates.AsNoTracking().SingleAsync();
        Assert.Equal(handle.Receipt.ExpiresAtUtc, state.ExpiryObservedAtUtc);
        Assert.Equal(1, permissions.Calls);

        async Task FenceAsync()
        {
            await permissions.RequireSafeRuntimeAsync(token);
            var verdict = await claims.InspectCurrentLockedAsync(handle, token);
            if (verdict is GroupBatchClaimFenceVerdict.Current) return;
            // This fixed unit contains only reads. No staged effects are
            // committed with the durable metadata-only expiry witness.
            await claims.RetireExpiredLockedAsync(((GroupBatchClaimFenceVerdict.Expired)verdict).Observation, token);
            await transaction.CommitAsync(token);
            throw GroupServiceDirectory.Denied();
        }
    }
    private sealed class ReviewerExpiryAdvanceWorkProof(Action advance)
    {
        internal int Calls;
        internal async Task RequireSafeRuntimeAsync(CancellationToken token)
        { await Task.Yield(); token.ThrowIfCancellationRequested(); Calls++; advance(); }
    }
}
