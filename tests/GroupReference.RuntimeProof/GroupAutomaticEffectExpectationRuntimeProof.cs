using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Independently observes saved carrier format and immutable SQL rights.
// Exact original effect/replay/rollback graphs stay required by each caller;
// this observer does not reimplement the shipping fingerprint or prove completion.
internal static class GroupAutomaticEffectExpectationRuntimeProof
{
    internal static string RequireValue(GroupWorkCommitReceiptRecord receipt)
    {
        if (receipt.DependencyManifestVersion != 1 || receipt.EffectLedgerVersion != 1
            || receipt.ExpectedEffectSha256 is not { Length: 32 } digest || digest.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            throw new InvalidOperationException();
        return Convert.ToHexString(digest);
    }

    internal static async Task<string> RequireAsync(PlatformDbContext db, GroupScope scope, Guid operation, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        var rows = await db.GroupWorkCommitReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation).Take(2).ToArrayAsync(token);
        if (rows.Length != 1) throw new InvalidOperationException();
        return RequireValue(rows[0]);
    }

    internal static async Task RequireImmutableAsync(PlatformDbContext db, GroupScope scope, Guid operation, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
        var before = await RequireAsync(db, scope, operation, token);
        foreach (var sql in new[]
        {
            "UPDATE aioffice.GroupWorkCommitReceipts SET EffectLedgerVersion=EffectLedgerVersion WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding AND OperationId=@operation;",
            "UPDATE aioffice.GroupWorkCommitReceipts SET ExpectedEffectSha256=ExpectedEffectSha256 WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding AND OperationId=@operation;"
        })
        {
            var refused = false;
            try
            {
                await db.Database.ExecuteSqlRawAsync(sql, new object[] { new SqlParameter("@tenant", scope.TenantId),
                    new SqlParameter("@company", scope.CompanyId), new SqlParameter("@binding", scope.SourceBindingId),
                    new SqlParameter("@operation", operation) }, token);
            }
            catch (SqlException error) when (error.Number == 229) { refused = true; }
            if (!refused || await RequireAsync(db, scope, operation, token) != before) throw new InvalidOperationException();
        }
    }
}
