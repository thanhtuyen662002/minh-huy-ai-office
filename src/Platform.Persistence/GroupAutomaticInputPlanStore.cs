using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MinhHuy.AIOffice.Platform.Persistence;

// SQL-only original selection acquisition. No key resolution, model call,
// terminal effect or provider release occurs in these locked units.
internal sealed partial class GroupAutomaticInputPlanStore(PlatformDbContext database, GroupExtractionWorkerBinding worker,
    TimeProvider clock, GroupBatchSourceReader sources, GroupBrainCurrentReader brain, GroupOriginalAdmissionPreparer? admissions = null)
{
    private const string Savepoint = "GroupOriginalInputPlanWrites";

    internal Task<GroupAutomaticInputPlan?> ReadAsync(GroupBatchClaimHandle handle, CancellationToken token = default)
    {
        ValidateEntry(handle, token);
        return InCurrentUnitAsync(handle, null, async (now, _, cancellationToken) =>
        {
            var row = await ReadLockedAsync(handle, cancellationToken);
            return row is null ? null : await DecodeAsync(handle, row, now, cancellationToken);
        }, token);
    }

    // Used only inside the journal's owned serializable unit after its full
    // source/brain/current-claim fences. It opens no nested transaction and
    // authenticates the original receipt and complete closed stored bytes.
    internal async Task RequireOriginalUnchangedLockedAsync(GroupBatchClaimHandle handle,
        GroupAutomaticInputPlan expected, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(handle); ArgumentNullException.ThrowIfNull(expected); token.ThrowIfCancellationRequested();
        worker.Validate(); handle.Receipt.Scope.Validate();
        if (handle.Receipt.Scope.TenantId != worker.TenantId || handle.Receipt.Scope.CompanyId != worker.CompanyId
            || handle.Receipt.ServiceId != worker.ServiceId || handle.Receipt.CredentialEpoch != worker.CredentialEpoch)
            throw GroupServiceDirectory.Denied();
        if (sources is null || brain is null || !sources.UsesContext(database, worker, clock) || !brain.UsesContext(database, worker, clock)
            || !database.Database.IsSqlServer() || database.Database.CurrentTransaction is not { } sql
            || sql.GetDbTransaction().IsolationLevel != System.Data.IsolationLevel.Serializable
            || !sql.SupportsSavepoints || System.Transactions.Transaction.Current is not null || database.ChangeTracker.HasChanges()) throw Unavailable();
        try { if (new SqlConnectionStringBuilder(database.Database.GetConnectionString()).MultipleActiveResultSets) throw Unavailable(); }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException) { throw Unavailable(); }
        RequireScope(handle, expected.Dependencies);
        await new GroupAutomaticInputPlanPermissionVerifier(database).RequireSafeRuntimeAsync(token);
        var row = await ReadLockedAsync(handle, token) ?? throw Unavailable();
        var original = await DecodeAsync(handle, row, UtcNow(), token);
        if (!CryptographicOperations.FixedTimeEquals(original.Encode(), expected.Encode())) throw Unavailable();
    }

    // Existing callers stay on the legacy Format2 admission path.
    internal Task<GroupAutomaticInputPlan> PersistAsync(GroupAutomaticInputPlan plan,
        GroupBatchSourcePreparation preparation, GroupBrainPrivateContext currentBrain,
        CancellationToken token = default) => PersistCurrentAsync(plan, preparation, currentBrain, 2, token);

    // Explicit new-original entry; decoded recovery carriers cannot insert.
    internal Task<GroupAutomaticInputPlan> PersistV3Async(GroupAutomaticInputPlan plan,
        GroupBatchSourcePreparation preparation, GroupBrainPrivateContext currentBrain,
        CancellationToken token = default) => PersistCurrentAsync(plan, preparation, currentBrain, 3, token);

    private async Task<GroupAutomaticInputPlan> PersistCurrentAsync(GroupAutomaticInputPlan plan,
        GroupBatchSourcePreparation preparation, GroupBrainPrivateContext currentBrain,
        int format, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(currentBrain); token.ThrowIfCancellationRequested();
        var handle = preparation.Context.Handle;
        ValidateEntry(handle, token);
        if (!ReferenceEquals(handle, currentBrain.Handle)) throw Unavailable();
        var frozen = GroupAutomaticInputPlan.Read(plan.Encode());
        if (format == 2) RequireSupportedFormat(2, frozen.WireFormatVersion);
        else
        {
            RequireDecodedFormat(format, frozen.WireFormatVersion);
            try { plan.RequireOriginalCarrier(preparation, currentBrain, token); }
            catch (InvalidOperationException) { throw Unavailable(); }
        }
        var manifest = frozen.Dependencies;
        RequireScope(handle, manifest);
        var result = await InCurrentUnitAsync(handle, manifest, async (now, staged, cancellationToken) =>
        {
            // Origins are read from current scoped SQL records, never copied
            // from the proposed carrier. Full recreation checks every origin
            // and exact private input against these authenticated metadata.
            var ids = manifest.Dependencies.Where(x => x.Kind == GroupBrainContentKind.RequestRevision).Select(x => x.RecordId).ToArray();
            var scope = handle.Receipt.Scope;
            var origins = await database.GroupCustomerRequests.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && ids.Contains(x.Id))
                .OrderBy(x => x.Id).Take(GroupBrainCurrentReader.MaximumSelectedRevisions + 1)
                .Select(x => new GroupBrainRequestOriginMetadata(scope, x.Id, x.OriginBatchId, x.OriginOperationId,
                    x.OriginCandidateOrdinal, x.CreatedAtUtc)).ToArrayAsync(cancellationToken);
            frozen.RequireRestored(preparation, currentBrain, frozen.ProfileSha256, origins, cancellationToken);
            if (format == 3) plan.RequireOriginalIssuance(preparation, currentBrain, origins, cancellationToken);
            var old = await ReadLockedAsync(handle, cancellationToken);
            if (old is not null)
            {
                var original = await DecodeAsync(handle, old, now, cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(original.Encode(), frozen.Encode())) throw Unavailable();
                return original;
            }
            var receipt = handle.Receipt;
            var record = new GroupAutomaticInputPlanRecord
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                BatchId = receipt.BatchId,
                FormatVersion = format,
                PlanSha256 = frozen.Sha256,
                Plan = frozen.Encode(),
                OriginalClaimOperationId = receipt.OperationId,
                OriginalClaimEpoch = receipt.Epoch,
                CreatedAtUtc = now
            };
            database.Add(record); staged.Add(record);
            await database.SaveChangesAsync(cancellationToken);
            return frozen;
        }, token);
        return result ?? throw Unavailable();
    }

    private async Task<GroupAutomaticInputPlan?> InCurrentUnitAsync(GroupBatchClaimHandle handle,
        GroupWorkDependencyManifest? original, Func<DateTimeOffset, List<object>, CancellationToken, Task<GroupAutomaticInputPlan?>> operation,
        CancellationToken token, Func<CancellationToken, Task<GroupBatchClaimExpiryObservation?>>? additionalFence = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2)); token = deadline.Token;
        var claims = new GroupBatchClaimStore(database, worker, clock);
        var permissions = new GroupAutomaticInputPlanPermissionVerifier(database);
        var staged = new List<object>();
        try
        {
            var began = UtcNow();
            await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, token);
            var sql = database.Database.CurrentTransaction ?? throw Unavailable();
            if (!sql.SupportsSavepoints) throw Unavailable();
            await RequireCurrentOrRetireAsync(await InspectAsync(original), false);
            await sql.CreateSavepointAsync(Savepoint, token);
            var now = UtcNow(); if (now < began) throw Unavailable();
            var result = await operation(now, staged, token);
            var final = result?.Dependencies ?? original;
            if (final is not null) RequireScope(handle, final);
            await RequireCurrentOrRetireAsync(await InspectAsync(final), true);
            if (UtcNow() < now || database.ChangeTracker.HasChanges()) throw Unavailable();
            await transaction.CommitAsync(token);
            token.ThrowIfCancellationRequested();
            if (UtcNow() < now) throw Unavailable();
            return result;

            async Task<GroupBatchClaimFenceVerdict> InspectAsync(GroupWorkDependencyManifest? expected)
            {
                await permissions.RequireSafeRuntimeAsync(token);
                // Inspect an already retained issuance expiry before a fresh
                // SQL claim read, which may reject UTC before IssuedAtUtc.
                if (additionalFence is not null)
                {
                    var retained = AdditionalExpiry(await additionalFence(token));
                    if (retained is not null) return retained;
                }
                var verdict = await claims.InspectCurrentLockedAsync(handle, token);
                if (verdict is GroupBatchClaimFenceVerdict.Current && additionalFence is not null)
                    verdict = await InspectAdditionalFenceAsync(verdict);
                if (verdict is GroupBatchClaimFenceVerdict.Current)
                {
                    var legacyActive = await new GroupOriginalActivityStore(database, worker).IsLegacyActiveLockedAsync(handle, token);
                    if (additionalFence is not null) verdict = await InspectAdditionalFenceAsync(verdict);
                    if (verdict is GroupBatchClaimFenceVerdict.Expired) return verdict;
                    verdict = await claims.InspectCurrentLockedAsync(handle, token);
                    if (verdict is GroupBatchClaimFenceVerdict.Expired) return verdict;
                    if (!legacyActive) throw Unavailable();
                }
                if (verdict is GroupBatchClaimFenceVerdict.Current && expected is not null)
                {
                    verdict = await sources.RequireManifestUnchangedLockedAsync(handle, expected, token);
                    if (verdict is GroupBatchClaimFenceVerdict.Current)
                        verdict = await brain.RequireManifestUnchangedLockedAsync(handle, expected, token);
                }
                if (verdict is GroupBatchClaimFenceVerdict.Current)
                {
                    await permissions.RequireSafeRuntimeAsync(token);
                    verdict = await claims.InspectCurrentLockedAsync(handle, token);
                }
                if (verdict is GroupBatchClaimFenceVerdict.Current && additionalFence is not null)
                {
                    verdict = await InspectAdditionalFenceAsync(verdict);
                    if (verdict is GroupBatchClaimFenceVerdict.Current)
                        verdict = await claims.InspectCurrentLockedAsync(handle, token);
                }
                return verdict;
            }
            async Task<GroupBatchClaimFenceVerdict> InspectAdditionalFenceAsync(GroupBatchClaimFenceVerdict current)
            {
                return AdditionalExpiry(await additionalFence!(token)) ?? current;
            }
            GroupBatchClaimFenceVerdict.Expired? AdditionalExpiry(GroupBatchClaimExpiryObservation? observed)
            {
                if (observed is null) return null;
                if (!ReferenceEquals(observed.Handle, handle) || observed.ObservedAtUtc.Offset != TimeSpan.Zero
                    || observed.ObservedAtUtc < handle.Receipt.ExpiresAtUtc) throw Unavailable();
                // Never replace known expiry with a later clock observation.
                // The existing retirement path authenticates its SQL receipt
                // and preserves this timestamp even after UTC rolls backward.
                return new GroupBatchClaimFenceVerdict.Expired(observed);
            }
            async Task RequireCurrentOrRetireAsync(GroupBatchClaimFenceVerdict verdict, bool rollbackWrites)
            {
                if (verdict is GroupBatchClaimFenceVerdict.Current) return;
                if (verdict is not GroupBatchClaimFenceVerdict.Expired expired) throw Unavailable();
                if (rollbackWrites)
                {
                    await sql.RollbackToSavepointAsync(Savepoint, token);
                    DetachStaged();
                }
                await claims.RetireExpiredLockedAsync(expired.Observation, token);
                await transaction.CommitAsync(token);
                token.ThrowIfCancellationRequested();
                throw GroupServiceDirectory.Denied();
            }
        }
        catch (Exception error) when (error is SqlException or DbUpdateException or ArgumentException or OverflowException)
        { throw Unavailable(); }
        finally { DetachStaged(); }

        void DetachStaged()
        { foreach (var entity in staged) database.Entry(entity).State = EntityState.Detached; staged.Clear(); }
    }

    private Task<StoredPlan?> ReadLockedAsync(GroupBatchClaimHandle handle, CancellationToken token)
    {
        var scope = handle.Receipt.Scope;
        // Bound allocation on the server before returning bytes, even if an
        // operator corrupted the row or disabled the size constraint.
        return database.Database.SqlQuery<StoredPlan>($"""
            SELECT TenantId,CompanyId,BindingId,BatchId,FormatVersion,PlanSha256,OriginalClaimOperationId,OriginalClaimEpoch,CreatedAtUtc,
              CASE WHEN DATALENGTH([Plan]) BETWEEN 256 AND 32000 THEN [Plan] END AS [Plan]
            FROM aioffice.GroupAutomaticInputPlans
            """).SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
                && x.BindingId == scope.SourceBindingId && x.BatchId == handle.Receipt.BatchId, token);
    }

    private async Task<GroupAutomaticInputPlan> DecodeAsync(GroupBatchClaimHandle handle, StoredPlan row,
        DateTimeOffset now, CancellationToken token)
    {
        if (row.FormatVersion is not (2 or 3) || row.OriginalClaimOperationId == Guid.Empty || row.OriginalClaimEpoch <= 0
            || row.CreatedAtUtc.Offset != TimeSpan.Zero || row.CreatedAtUtc > now || row.Plan is null) throw Unavailable();
        var plan = GroupAutomaticInputPlan.Read(row.Plan);
        RequireDecodedFormat(row.FormatVersion, plan.WireFormatVersion);
        RequireScope(handle, plan.Dependencies);
        if (row.TenantId != handle.Receipt.Scope.TenantId || row.CompanyId != handle.Receipt.Scope.CompanyId
            || row.BindingId != handle.Receipt.Scope.SourceBindingId || row.BatchId != handle.Receipt.BatchId
            || row.PlanSha256 != plan.Sha256) throw Unavailable();
        var receipt = await database.GroupBatchClaimReceipts.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == row.TenantId && x.CompanyId == row.CompanyId && x.BindingId == row.BindingId
            && x.OperationId == row.OriginalClaimOperationId && x.BatchId == row.BatchId, token) ?? throw Unavailable();
        if (receipt.Epoch != row.OriginalClaimEpoch || receipt.IssuedAtUtc.Offset != TimeSpan.Zero
            || receipt.ExpiresAtUtc.Offset != TimeSpan.Zero || row.CreatedAtUtc < receipt.IssuedAtUtc
            || row.CreatedAtUtc >= receipt.ExpiresAtUtc || receipt.AuthoritySha256 != plan.Dependencies.AuthoritySha256)
            throw Unavailable();
        return plan;
    }

    // Legacy store admission remains Format2 only. A decoded V3 carrier is
    // structural, never first-insert issuance or an expanded schema contract.
    internal static void RequireSupportedFormat(int rowFormat, int codecFormat)
    {
        if (rowFormat != 2 || codecFormat != rowFormat) throw Unavailable();
    }

    internal static void RequireDecodedFormat(int rowFormat, int codecFormat)
    {
        if (rowFormat is not (2 or 3) || codecFormat != rowFormat) throw Unavailable();
    }

    private void ValidateEntry(GroupBatchClaimHandle handle, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(handle); token.ThrowIfCancellationRequested();
        worker.Validate(); handle.Receipt.Scope.Validate();
        if (handle.Receipt.Scope.TenantId != worker.TenantId || handle.Receipt.Scope.CompanyId != worker.CompanyId
            || handle.Receipt.ServiceId != worker.ServiceId || handle.Receipt.CredentialEpoch != worker.CredentialEpoch)
            throw GroupServiceDirectory.Denied();
        if (sources is null || brain is null || !sources.UsesContext(database, worker, clock) || !brain.UsesContext(database, worker, clock)
            || !database.Database.IsSqlServer() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null || database.ChangeTracker.HasChanges()) throw Unavailable();
        try { if (new SqlConnectionStringBuilder(database.Database.GetConnectionString()).MultipleActiveResultSets) throw Unavailable(); }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException) { throw Unavailable(); }
    }
    private static void RequireScope(GroupBatchClaimHandle handle, GroupWorkDependencyManifest manifest)
    {
        if (manifest.Scope != handle.Receipt.Scope || manifest.BatchId != handle.Receipt.BatchId
            || manifest.AuthoritySha256 != GroupBatchClaimStore.AuthorityFingerprint(handle.Authority)) throw Unavailable();
    }
    private DateTimeOffset UtcNow()
    { var now = clock.GetUtcNow(); if (now.Offset != TimeSpan.Zero) throw Unavailable(); return now; }
    private static InvalidOperationException Unavailable() => new("Group original input plan store is not available.");
    private sealed record StoredPlan(Guid TenantId, Guid CompanyId, Guid BindingId, Guid BatchId, int FormatVersion,
        string PlanSha256, Guid OriginalClaimOperationId, long OriginalClaimEpoch, DateTimeOffset CreatedAtUtc, byte[]? Plan);
}
