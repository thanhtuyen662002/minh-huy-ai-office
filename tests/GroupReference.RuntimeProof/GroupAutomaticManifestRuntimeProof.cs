using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Independent observation of four owned two-source/zero-brain fixtures only.
// Does not borrow the shipping parser or qualify dependency reconstruction.
internal static class GroupAutomaticManifestRuntimeProof
{
    internal static async Task<string> RequireAsync(PlatformDbContext db, GroupScope scope, Guid operation, CancellationToken token)
    {
        var receipt = await db.GroupWorkCommitReceipts.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation, token);
        var cutoff = await db.GroupBatchAllocations.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.Id == receipt.BatchId)
            .Select(x => x.AllocatedThroughSequence).SingleAsync(token);
        var selected = await db.GroupWorkSourceDispositions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation).Take(3).ToArrayAsync(token);
        return RequireValue(receipt, cutoff, selected);
    }

    internal static string RequireValue(GroupWorkCommitReceiptRecord receipt, long cutoff, IReadOnlyList<GroupWorkSourceDispositionRecord> selected)
    {
        var value = receipt.DependencyManifest;
        if (receipt.DependencyManifestVersion != 1 || value is null || value.Length != 274 || cutoff <= 0
            || receipt.SelectedMessageCount != 2 || selected.Count != 2 || !value.AsSpan(0, 8).SequenceEqual("AIOGDEP1"u8)
            || selected.Select(x => x.MessageId).Distinct().Count() != 2) throw new InvalidOperationException();
        var ids = new[] { receipt.TenantId, receipt.CompanyId, receipt.BindingId, receipt.BatchId, receipt.OperationId };
        for (var index = 0; index < ids.Length; index++)
            if (ids[index] == Guid.Empty || new Guid(value.AsSpan(8 + index * 16, 16)) != ids[index]) throw new InvalidOperationException();
        if (BinaryPrimitives.ReadInt64LittleEndian(value.AsSpan(88, 8)) != cutoff || value[160] != 2 || value[161] != 0
            || value.AsSpan(96, 32).IndexOfAnyExcept((byte)0) < 0 || value.AsSpan(128, 32).IndexOfAnyExcept((byte)0) < 0)
            throw new InvalidOperationException();
        var ordered = selected.OrderBy(x => x.MessageId).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var row = ordered[index]; var offset = 162 + index * 56;
            if (row.TenantId != receipt.TenantId || row.CompanyId != receipt.CompanyId || row.BindingId != receipt.BindingId
                || row.BatchId != receipt.BatchId || row.OperationId != receipt.OperationId || row.MessageId == Guid.Empty || row.MessageRevision <= 0
                || new Guid(value.AsSpan(offset, 16)) != row.MessageId
                || BinaryPrimitives.ReadInt64LittleEndian(value.AsSpan(offset + 16, 8)) != row.MessageRevision
                || value.AsSpan(offset + 24, 32).IndexOfAnyExcept((byte)0) < 0) throw new InvalidOperationException();
        }
        return Convert.ToHexString(SHA256.HashData(value));
    }

    internal static async Task RequireImmutableAsync(PlatformDbContext db, GroupScope scope, Guid operation, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
        var before = await RequireAsync(db, scope, operation, token);
        foreach (var sql in new[]
        {
            "UPDATE aioffice.GroupWorkCommitReceipts SET DependencyManifestVersion=DependencyManifestVersion WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding AND OperationId=@operation;",
            "UPDATE aioffice.GroupWorkCommitReceipts SET DependencyManifest=DependencyManifest WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding AND OperationId=@operation;"
        })
        {
            var refused = false;
            try
            {
                await db.Database.ExecuteSqlRawAsync(sql,
                    new object[] { new SqlParameter("@tenant", scope.TenantId), new SqlParameter("@company", scope.CompanyId),
                        new SqlParameter("@binding", scope.SourceBindingId), new SqlParameter("@operation", operation) }, token);
            }
            catch (SqlException error) when (error.Number == 229) { refused = true; }
            if (!refused || await RequireAsync(db, scope, operation, token) != before) throw new InvalidOperationException();
        }
    }
}
