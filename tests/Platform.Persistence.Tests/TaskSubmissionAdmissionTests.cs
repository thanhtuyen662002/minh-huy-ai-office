using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class TaskSubmissionAdmissionTests
{
    [Fact]
    public async Task ResolvedDirectoryCannotReplaceTheRequestedAuthorityOrAdmitAnyEffects()
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var owner = MemberDirectoryFixture.Authority();
        var source = SubmissionIntentFixture.Seed(db, owner); await db.SaveChangesAsync();
        var other = MemberDirectoryFixture.Authority(); SubmissionIntentFixture.Seed(db, other); await db.SaveChangesAsync();
        var directory = new ReplacedDirectory(db, other);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new PilotTaskSubmissionService(db, directory).SubmitAsync(owner,
            new PilotTaskSubmissionRequest("replaced", source, "Question")));
        Assert.Empty(db.Tasks); Assert.Empty(db.TaskDispatches);
    }

    [Theory]
    [InlineData("source", false)]
    [InlineData("grant", false)]
    [InlineData("membership", false)]
    [InlineData("source", true)]
    [InlineData("grant", true)]
    [InlineData("membership", true)]
    public async Task AuthorityLossBeforeGraphAdmissionDeniesNewAndReplayWithoutChangingEffects(string revoke, bool replay)
    {
        await using var db = new PlatformDbContext(MemberDirectoryFixture.Options()); var owner = MemberDirectoryFixture.Authority();
        var source = SubmissionIntentFixture.Seed(db, owner); await db.SaveChangesAsync();
        var request = new PilotTaskSubmissionRequest("admission-fence", source, "Question");
        if (replay) await new PilotTaskSubmissionService(db, new EfAuthorizationDirectory(db)).SubmitAsync(owner, request);
        var before = SubmissionIntentFixture.Effects(db);
        var directory = new RevokingDirectory(db, revoke);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new PilotTaskSubmissionService(db, directory).SubmitAsync(owner, request));
        Assert.Equal(before, SubmissionIntentFixture.Effects(db)); Assert.True(directory.Calls >= 2);
    }

    [Theory]
    [InlineData("legacy", "source")]
    [InlineData("legacy", "grant")]
    [InlineData("legacy", "membership")]
    [InlineData("intent", "source")]
    [InlineData("intent", "grant")]
    [InlineData("intent", "membership")]
    public async Task FinalDenialAfterRealSaveKeepsCommittedIdentityForExplicitReconciliationAndRetry(string mode, string revoke)
    {
        // A post-save interceptor simulates revocation after graph persistence.
        // Actual SQL commit/revocation serialization remains a mandatory hosted proof.
        var interceptor = new RevokeAfterSave(revoke);
        var options = new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(interceptor).Options;
        await using var db = new PlatformDbContext(options); var owner = MemberDirectoryFixture.Authority();
        var source = SubmissionIntentFixture.Seed(db, owner); await db.SaveChangesAsync();
        var directory = new EfAuthorizationDirectory(db); var intentService = new TaskSubmissionIntentService(db, directory, new SubmissionIntentFixture.Clock());
        var input = SubmissionIntentFixture.Request(source); var prepared = mode == "intent" ? await intentService.PrepareAsync(owner, input) : null;
        var key = mode == "intent" ? TaskSubmissionIntentIdentity.IdempotencyKey(input.OperationId) : "legacy-final-fence";
        var legacy = new PilotTaskSubmissionService(db, directory, new SubmissionIntentFixture.Clock());
        interceptor.Armed = true;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mode == "intent"
            ? Execute() : Submit());
        Assert.Single(db.Tasks); Assert.Single(db.TaskDispatches); var taskId = db.Tasks.Local.Single().Id;
        if (revoke != "membership" && mode == "intent") Assert.Equal(taskId, (await intentService.GetAsync(owner, input.OperationId))!.Accepted!.TaskId);
        if (revoke == "membership" && mode == "intent") await Assert.ThrowsAsync<UnauthorizedAccessException>(() => intentService.GetAsync(owner, input.OperationId));
        db.DataSources.Local.Single().IsEnabled = true; db.DataSourceSecretBindings.Local.Single().IsEnabled = true;
        db.CompanyMemberships.Local.Single().IsActive = true; await db.SaveChangesAsync();
        var before = SubmissionIntentFixture.Effects(db);
        if (mode == "intent") Assert.Equal(taskId, (await intentService.ExecuteAsync(owner, input.OperationId, new(prepared!.InputFingerprint!))).TaskId);
        else Assert.Equal(taskId, (await legacy.SubmitAsync(owner, new PilotTaskSubmissionRequest(key, source, input.Question))).TaskId);
        Assert.Equal(before, SubmissionIntentFixture.Effects(db));

        async Task Execute() => _ = await intentService.ExecuteAsync(owner, input.OperationId, new(prepared!.InputFingerprint!));
        async Task Submit() => _ = await legacy.SubmitAsync(owner, new PilotTaskSubmissionRequest(key, source, input.Question));
    }

    private sealed class ReplacedDirectory(PlatformDbContext db, AuthorizationContext replacement) : IAuthorizationDirectory
    {
        public Task<AuthorizationDirectoryEntry?> ResolveAsync(AuthorizationContext authority, CancellationToken cancellationToken = default) =>
            new EfAuthorizationDirectory(db).ResolveAsync(replacement, cancellationToken);
    }
    private sealed class RevokingDirectory(PlatformDbContext db, string revoke) : IAuthorizationDirectory
    {
        public int Calls { get; private set; }
        public async Task<AuthorizationDirectoryEntry?> ResolveAsync(AuthorizationContext authority, CancellationToken cancellationToken = default)
        {
            if (++Calls == 2) { Revoke(db, revoke); await db.SaveChangesAsync(cancellationToken); }
            return await new EfAuthorizationDirectory(db).ResolveAsync(authority, cancellationToken);
        }
    }
    private sealed class RevokeAfterSave(string revoke) : SaveChangesInterceptor
    {
        internal bool Armed { get; set; }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context is PlatformDbContext db && db.Tasks.Local.Any())
            {
                Armed = false; Revoke(db, revoke); await db.SaveChangesAsync(cancellationToken);
            }
            return result;
        }
    }
    private static void Revoke(PlatformDbContext db, string revoke)
    {
        if (revoke == "source") db.DataSources.Local.Single().IsEnabled = false;
        else if (revoke == "grant") db.DataSourceSecretBindings.Local.Single().IsEnabled = false;
        else db.CompanyMemberships.Local.Single().IsActive = false;
    }
}
