using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LockedDependencyEntriesRefuseNonSqlAndMissingSqlTransactionBeforeKeysOrWitness(bool sql, bool expired)
    {
        using var f = new Fixture(); var message = await f.CommitAsync(); var handle = await f.ClaimAsync();
        var source = await f.Reader.ReadAsync(handle, [message.MessageId]);
        var brain = await BrainReader(f).ReadAsync(handle, [], []);
        var reads = f.Keys.Reads; var counts = await f.CountsAsync();
        if (expired) f.Auth.Clock.Current = handle.Receipt.ExpiresAtUtc;
        using var alternate = sql ? new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=never_connect;Integrated Security=true;Encrypt=true;Connect Timeout=1").Options) : null;
        var database = alternate ?? f.Auth.Db;
        var sourceReader = new GroupBatchSourceReader(database, f.Worker, f.Auth.Clock, f.Keys, new GroupSourceContentProtector());
        var brainReader = new GroupBrainCurrentReader(database, f.Worker, f.Auth.Clock, f.Keys, new GroupBrainContentProtector());
        var sourceError = await Assert.ThrowsAsync<InvalidOperationException>(() => sourceReader.RequireUnchangedLockedAsync(source, default));
        var brainError = await Assert.ThrowsAsync<InvalidOperationException>(() => brainReader.RequireUnchangedLockedAsync(brain, default));
        Assert.Equal("Group batch source context is not available.", sourceError.Message);
        Assert.Equal("Group brain context is unavailable.", brainError.Message);
        Assert.Null(sourceError.InnerException); Assert.Null(brainError.InnerException);
        Assert.Equal(reads, f.Keys.Reads); Assert.Equal(counts, await f.CountsAsync());
        Assert.Null((await f.Auth.Db.GroupBatchClaimStates.AsNoTracking().SingleAsync()).ExpiryObservedAtUtc);
        Assert.Null(database.Database.CurrentTransaction); Assert.False(database.ChangeTracker.HasChanges());
    }
}
