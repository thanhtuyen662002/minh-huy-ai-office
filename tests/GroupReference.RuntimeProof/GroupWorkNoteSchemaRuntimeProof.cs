using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

internal static class GroupWorkNoteSchemaRuntimeProof
{
    internal static async Task RunAsync(string mode, GroupScope scope, GroupExtractionWorkerBinding worker,
        DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        if (mode is not ("work-schema" or "work-unsafe")) throw new InvalidOperationException();
        scope.Validate(); worker.Validate();
        if (scope.TenantId != worker.TenantId || scope.CompanyId != worker.CompanyId) throw new InvalidOperationException();
        await using var db = new PlatformDbContext(options);
        var permissions = new GroupWorkNotePermissionVerifier(db);
        if (mode == "work-unsafe")
        {
            var denied = false;
            try { await permissions.RequireSafeRuntimeAsync(token); }
            catch (Exception error) when (OwnedGroupReferenceProofGuard.IsExpectedRefusal(mode, error)) { denied = true; }
            if (!denied || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            Console.WriteLine("PASS owned work schema runtime unsafe effective permission refusal"); return;
        }
        await permissions.RequireSafeRuntimeAsync(token);
        // This proof reads only scoped empty metadata; it establishes neither
        // current Extract/private content authorization nor note SQL effects.
        var counts = new[]
        {
            await db.GroupCustomerRequests.CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token),
            await db.GroupRequestRevisions.CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token),
            await db.GroupRequestEvidence.CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token),
            await db.GroupWorkCommitReceipts.CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token),
            await db.GroupWorkSourceDispositions.CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token),
            await db.GroupNotesCommittedOutbox.CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token),
            await db.GroupNotesCommittedItems.CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token),
            await db.GroupEditorGrants.CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token),
            await db.GroupGlossaryEntries.CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token),
            await db.GroupGlossaryRevisions.CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token)
        };
        if (counts.Any(x => x != 0) || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
        Console.WriteLine("PASS owned work schema runtime migrated empty scoped brain and effective least privilege");
    }
}
