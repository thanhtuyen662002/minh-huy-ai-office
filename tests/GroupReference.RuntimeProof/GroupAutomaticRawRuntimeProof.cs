using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Independent test-only observation of the additional raw ledger. The existing
// seven-table effect counts and snapshots remain unchanged.
internal static class GroupAutomaticRawRuntimeProof
{
    internal static async Task<string> RequireAsync(PlatformDbContext db, GroupScope scope,
        Guid operation, int expectedRows, CancellationToken token)
    {
        if (operation == Guid.Empty || expectedRows is < 1 or > 500) throw new InvalidOperationException();
        var receipt = await db.GroupWorkCommitReceipts.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation, token);
        var allocation = await db.GroupBatchAllocations.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.Id == receipt.BatchId, token);
        var allocated = await db.GroupBatchAllocatedRevisions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == receipt.BatchId)
            .OrderBy(x => x.CommittedSequence).Take(501).ToArrayAsync(token);
        var selected = await db.GroupWorkSourceDispositions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation)
            .OrderBy(x => x.MessageId).Take(101).ToArrayAsync(token);
        var rows = await db.GroupWorkRawDispositions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation)
            .OrderBy(x => x.CommittedSequence).Take(501).ToArrayAsync(token);
        if (allocated.Length is < 1 or > 500 || allocated.Length != allocation.RawRevisionCount
            || allocation.AllocatedThroughSequence - allocation.AfterSequence != allocated.Length
            || selected.Length is < 1 or > 100 || selected.Length != receipt.SelectedMessageCount
            || selected.Select(x => x.MessageId).Distinct().Count() != selected.Length
            || selected.Any(x => x.BatchId != receipt.BatchId || x.MessageRevision <= 0 || !Enum.IsDefined(x.Outcome))
            || allocated.Where((x, index) => x.CommittedSequence != allocation.AfterSequence + index + 1
                || x.MessageId == Guid.Empty || x.Revision <= 0).Any()) throw new InvalidOperationException();
        var expected = allocated.Where(x => selected.Any(y => y.MessageId == x.MessageId)).ToArray();
        if (rows.Length != expectedRows || expected.Length != expectedRows
            || expected.Select(x => x.MessageId).Distinct().Count() != selected.Length) throw new InvalidOperationException();
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index]; var raw = expected[index]; var head = selected.Single(x => x.MessageId == raw.MessageId);
            if (row.TenantId != scope.TenantId || row.CompanyId != scope.CompanyId || row.BindingId != scope.SourceBindingId
                || row.BatchId != receipt.BatchId || row.CommittedSequence != raw.CommittedSequence || row.MessageId != raw.MessageId
                || row.RawRevision != raw.Revision || row.SelectedMessageRevision != head.MessageRevision || row.OperationId != operation
                || row.Outcome != head.Outcome || row.Relation != (raw.Revision == head.MessageRevision
                    ? GroupWorkRawRelation.SelectedHead : GroupWorkRawRelation.SupersededBySelectedHead)) throw new InvalidOperationException();
        }
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(rows)));
    }

    internal static async Task RequireEmptyAsync(PlatformDbContext db, GroupScope scope, CancellationToken token)
    {
        if (await db.GroupWorkRawDispositions.AsNoTracking().AnyAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)) throw new InvalidOperationException();
    }

    internal static void RequireDetached(PlatformDbContext db)
    {
        if (db.ChangeTracker.Entries<GroupWorkRawDispositionRecord>().Any()) throw new InvalidOperationException();
    }

    internal static async Task RequireImmutableAsync(PlatformDbContext db, GroupScope scope, Guid operation, CancellationToken token)
    {
        if (operation == Guid.Empty || db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
        await new GroupWorkNotePermissionVerifier(db).RequireSafeRuntimeAsync(token);
        foreach (var column in new[] { "TenantId", "CompanyId", "BindingId", "BatchId", "CommittedSequence", "MessageId",
            "RawRevision", "SelectedMessageRevision", "OperationId", "Outcome", "Relation" })
        {
            // Closed test-only identifiers. A refused no-op UPDATE still checks
            // each actual column's effective permission without altering a row.
            await RefusedAsync($"UPDATE aioffice.GroupWorkRawDispositions SET [{column}]=[{column}] "
                + "WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding AND OperationId=@operation;");
        }
        await RefusedAsync("DELETE FROM aioffice.GroupWorkRawDispositions WHERE TenantId=@tenant AND CompanyId=@company "
            + "AND BindingId=@binding AND OperationId=@operation AND 1=0;");

        async Task RefusedAsync(string sql)
        {
            var denied = false;
            try
            {
                await db.Database.ExecuteSqlRawAsync(sql, new object[] { new SqlParameter("@tenant", scope.TenantId),
                    new SqlParameter("@company", scope.CompanyId), new SqlParameter("@binding", scope.SourceBindingId),
                    new SqlParameter("@operation", operation) }, token);
            }
            catch (SqlException error) when (error.Number == 229) { denied = true; }
            if (!denied) throw new InvalidOperationException();
        }
    }
}
