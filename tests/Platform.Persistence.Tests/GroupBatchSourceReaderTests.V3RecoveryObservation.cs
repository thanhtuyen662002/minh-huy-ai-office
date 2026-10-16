extern alias GroupReferenceProof;

using Microsoft.EntityFrameworkCore;
using Xunit;
using Proof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupOriginalInputV3RuntimeProof;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task V3RecoveryObserverRejectsEveryActualSelectorQueryWhileDisarmed(int query)
    {
        using var f = new Fixture();
        var payload = f.Payload(text: HistoryCode(2));
        payload = payload with { Event = payload.Event with { ReplyToMessageId = "PRIVATE_REPLY" } };
        var source = await f.CommitAsync(payload);
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [source.MessageId]));
        using var db = OfflineV3ObservationDb();
        var selector = new GroupAutomaticHistorySelector(db, f.Worker, f.Auth.Clock, f.Reader);
        var sql = query == 3 ? selector.QueryGlossary(preparation).ToQueryString()
            : selector.QueryRequests(preparation, GroupAutomaticHistoryHints.Create(preparation), query).ToQueryString();
        var probe = new Proof.SelectionProbe(f.Auth.Scope);
        var error = Assert.Throws<InvalidOperationException>(() => probe.ObserveSelection(sql));
        Assert.Equal("Owned V3 recovery attempted history reselection.", error.Message);
        Assert.Equal(1, probe.Queries);
        probe.Armed = true;
        Assert.True(probe.ObserveSelection(sql));
        Assert.Equal(2, probe.Queries);
        probe.Armed = false;
        Assert.Throws<InvalidOperationException>(() => probe.ObserveSelection(sql));
        Assert.Equal(3, probe.Queries);
        Assert.Equal(0, probe.Commands);
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Fact]
    public void V3RecoveryObserverAllowsNormalExactIdHeadsOriginsAndFenceMetadata()
    {
        using var db = OfflineV3ObservationDb();
        using var f = new Fixture();
        var scope = f.Auth.Scope; var id = Guid.NewGuid();
        var requests = db.GroupCustomerRequests.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.Id == id);
        var glossary = db.GroupGlossaryEntries.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.Id == id);
        var sql = new[]
        {
            requests.Select(x => new GroupBrainRequestHead(x.CurrentRevision, x.BusinessVersion, x.RequestCode, x.Kind,
                x.SourceVersion, x.DeletionGeneration, x.BusinessStatus, x.AssignedToUserId, x.CommittedDueAtUtc,
                x.ConfirmedByUserId, x.ConfirmedAtUtc, x.CreatedAtUtc, x.UpdatedAtUtc)).Take(2).ToQueryString(),
            glossary.Select(x => new GroupBrainGlossaryHead(x.CurrentRevision, x.Version, x.SourceVersion, x.DeletionGeneration,
                x.IsEnabled, x.AllowExtraction, x.PublishedByUserId, x.CreatedAtUtc)).Take(2).ToQueryString(),
            requests.OrderBy(x => x.Id).Select(x => new GroupBrainRequestOriginMetadata(scope, x.Id, x.OriginBatchId,
                x.OriginOperationId, x.OriginCandidateOrdinal, x.CreatedAtUtc)).ToQueryString(),
            db.GroupBatchClaimReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).Take(2).ToQueryString()
        };
        var probe = new Proof.SelectionProbe(scope);
        foreach (var text in sql) Assert.False(probe.ObserveSelection(text));
        Assert.Equal(0, probe.Queries); Assert.Equal(0, probe.Commands);
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    private static PlatformDbContext OfflineV3ObservationDb() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer("Server=127.0.0.1,1;Database=offline_v3_recovery_observation;Integrated Security=true;Connect Timeout=1").Options);
}
