using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record GroupTerminalFrontierAdvanceResult(GroupScope Scope, Guid BatchId, long Version,
    long ThroughSequence, string Fingerprint, DateTimeOffset AdvancedAtUtc, bool WasAlreadyAdvanced);

// Historical bookkeeping under fresh Extract authority. The batch identity is
// the idempotency key: retrying a lost reply never advances another interval.
public sealed partial class GroupTerminalFrontierStore(PlatformDbContext database, GroupExtractionWorkerBinding worker,
    TimeProvider clock, IGroupSourceKeyProvider keys)
{
    internal const int MaximumRetainedKeys = 4096;
    private const string EffectSavepoint = "aioffice_group_frontier_effect";

    public async Task<GroupTerminalFrontierAdvanceResult> AdvanceAsync(GroupScope scope, Guid batch,
        CancellationToken token = default)
    {
        ValidateEntry(scope, batch, token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2)); token = deadline.Token;
        var material = new Dictionary<string, GroupSourceKeyMaterial>(StringComparer.Ordinal);
        GroupSourceKeyMaterial? writeKey = null;
        var staged = new List<object>(); var observedClock = new MonotoneClock(clock);
        try
        {
            var directory = new GroupExtractionDirectory(database, worker);
            var permissions = new GroupTerminalAdvanceKeyPermissionVerifier(database);
            GroupExtractionAuthority authority; Snapshot initial;
            // Dispose the first pinned unit before any external key await.
            await using (var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, token))
            {
                await permissions.RequireSafeRuntimeAsync(token);
                authority = await directory.RequireAsync(scope, token);
                await GroupSourceTransactionLock.RequireAsync(database, scope, token);
                await directory.RequireCurrentAsync(authority, token);
                initial = await ReadSnapshotAsync(scope, batch, token);
                _ = ObserveNow();
                await permissions.RequireSafeRuntimeAsync(token);
                await directory.RequireCurrentAsync(authority, token);
                await transaction.CommitAsync(token);
            }
            RequireOutsideSql();
            // The complete finite enrolled inventory is checked, including keys
            // older than the leaf pair. No unavailable/rebound ID is skipped.
            foreach (var row in initial.Keys)
            {
                token.ThrowIfCancellationRequested(); RequireOutsideSql();
                var key = await AwaitMaterialAsync(() => keys.ResolveReadAsync(scope, row.KeyId, token), token);
                try
                {
                    RequireOutsideSql(); GroupTerminalAdvanceKeyProof.Require(row, scope, key);
                    if (!material.TryAdd(row.KeyId, key)) throw Unavailable();
                }
                catch { key?.Dispose(); throw; }
            }
            RequireOutsideSql(); writeKey = await AwaitMaterialAsync(() => keys.ResolveWriteAsync(scope, token), token); RequireOutsideSql();
            if (writeKey is null) throw Unavailable();
            if (initial.Keys.SingleOrDefault(x => x.KeyId == writeKey.KeyId) is { } enrolled)
                GroupTerminalAdvanceKeyProof.Require(enrolled, scope, writeKey);

            await using (var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, token))
            {
                await permissions.RequireSafeRuntimeAsync(token);
                await directory.RequireCurrentAsync(authority, token);
                await GroupSourceTransactionLock.RequireAsync(database, scope, token);
                await directory.RequireCurrentAsync(authority, token);
                var current = await ReadSnapshotAsync(scope, batch, token);
                RequireSameSnapshot(initial, current);
                RequireKeys(current);
                var latest = RequireLeaf(current);
                if (latest is not null && latest.Metadata.DeletionGeneration != authority.Source.DeletionGeneration) throw Unavailable();
                var historical = await new GroupBatchHistoricalSqlReader(database, worker, observedClock).ReadLockedAsync(authority, batch, token);
                var now = ObserveNow();
                if (now < historical.CommittedAtUtc || latest is not null && now < latest.Metadata.AdvancedAtUtc) throw Unavailable();
                await FenceAsync();
                if (current.Target is { } prior)
                {
                    if (latest is null || prior.Version > latest.Metadata.Version) throw Unavailable();
                    var authenticated = RequireTarget(current);
                    RequireHistorical(authenticated, historical);
                    RequireSameSnapshot(current, await ReadSnapshotAsync(scope, batch, token));
                    await FenceAsync();
                    await transaction.CommitAsync(token);
                    return Result(authenticated, true);
                }
                var previous = latest?.Metadata;
                if (historical.AfterSequence != (previous?.ThroughSequence ?? 0)
                    || previous?.Version == long.MaxValue) throw Unavailable();
                var metadata = new GroupTerminalAdvanceMetadata(scope, batch, historical.OperationId,
                    previous?.Version ?? 0, checked((previous?.Version ?? 0) + 1), historical.AfterSequence,
                    historical.ThroughSequence, previous is null ? new byte[32] : Convert.FromHexString(latest!.Fingerprint),
                    Convert.FromHexString(historical.ManifestSha256), historical.CommittedAtUtc, now,
                    authority.Principal.ServiceId, authority.Principal.CredentialEpoch, authority.Grant.Version,
                    authority.Source.Version, authority.Source.DeletionGeneration, authority.AccountVersion,
                    Convert.FromHexString(GroupBatchClaimStore.AuthorityFingerprint(authority)), writeKey.KeyId);
                // No Sign occurs before genuine original history, current
                // authority, enrolled retained keys and the latest leaf agree.
                var signed = GroupTerminalAdvanceIntegrity.Sign(metadata, writeKey);
                var receipt = GroupTerminalAdvanceRowProof.Stage(signed, latest);
                var cursor = current.Cursor ?? new GroupTerminalFrontierStateRecord
                { TenantId = scope.TenantId, CompanyId = scope.CompanyId, BindingId = scope.SourceBindingId };
                var sql = database.Database.CurrentTransaction;
                if (sql is null || !sql.SupportsSavepoints) throw Unavailable();
                await sql.CreateSavepointAsync(EffectSavepoint, token);
                // The source APPLOCK was acquired before this savepoint. The
                // enclosing owned transaction rolls every effect back on error.
                if (!current.Keys.Any(x => x.KeyId == writeKey.KeyId))
                {
                    if (current.Keys.Length == MaximumRetainedKeys) throw Unavailable();
                    var enrollment = GroupTerminalAdvanceKeyProof.Stage(authority, writeKey, now);
                    staged.Add(enrollment); database.Add(enrollment);
                }
                staged.Add(receipt); database.Add(receipt);
                staged.Add(cursor);
                if (current.Cursor is null) database.Add(cursor); else database.Attach(cursor);
                cursor.Version = metadata.Version; cursor.ThroughSequence = metadata.ThroughSequence;
                cursor.LastTerminalBatchId = batch; cursor.UpdatedAtUtc = now;
                await database.SaveChangesAsync(token);
                await FenceAsync();
                var written = await ReadSnapshotAsync(scope, batch, token);
                RequireWrittenSnapshot(current, written, receipt, cursor, authority, writeKey, now);
                RequireKeys(written);
                var writtenLeaf = RequireLeaf(written);
                if (writtenLeaf is null || writtenLeaf.Fingerprint != signed.Fingerprint
                    || written.Target is null || written.Target.Version != metadata.Version) throw Unavailable();
                var repeated = await new GroupBatchHistoricalSqlReader(database, worker, observedClock).ReadLockedAsync(authority, batch, token);
                RequireHistorical(writtenLeaf, repeated);
                if (!historical.WriteManifest().AsSpan().SequenceEqual(repeated.WriteManifest())) throw Unavailable();
                await FenceAsync();
                RequireSameSnapshot(written, await ReadSnapshotAsync(scope, batch, token));
                token.ThrowIfCancellationRequested();
                await transaction.CommitAsync(token);
                return Result(writtenLeaf, false);

                async Task FenceAsync()
                {
                    await permissions.RequireSafeRuntimeAsync(token);
                    await directory.RequireCurrentAsync(authority, token);
                    _ = ObserveNow();
                    if (database.ChangeTracker.HasChanges()) throw Unavailable();
                    token.ThrowIfCancellationRequested();
                }
            }

            GroupTerminalAdvanceIntegrity? RequireLeaf(Snapshot value) => GroupTerminalFrontierLeafProof.Require(scope,
                value.Latest, value.Cursor, value.Latest.Length == 0 ? null : ReadKey(value.Latest[0]),
                value.Latest.Length < 2 ? null : ReadKey(value.Latest[1]), token);
            GroupSourceKeyMaterial ReadKey(GroupTerminalAdvanceRecord value)
            {
                var id = GroupTerminalAdvanceIntegrity.PeekKeyId(value.Envelope, scope);
                return id == writeKey.KeyId ? writeKey : material.TryGetValue(id, out var key) ? key : throw Unavailable();
            }
            GroupTerminalAdvanceIntegrity RequireTarget(Snapshot value)
            {
                var target = value.Target ?? throw Unavailable();
                var previous = value.TargetPrevious is null ? null : AuthenticatePrevious(value.TargetPrevious, scope, ReadKey(value.TargetPrevious));
                return GroupTerminalAdvanceRowProof.Require(target, scope, ReadKey(target), previous);
            }
            void RequireKeys(Snapshot snapshot)
            {
                var rows = snapshot.Keys;
                foreach (var row in rows)
                {
                    var key = row.KeyId == writeKey.KeyId ? writeKey : material.TryGetValue(row.KeyId, out var old) ? old : throw Unavailable();
                    GroupTerminalAdvanceKeyProof.Require(row, scope, key);
                }
                // Every envelope key also needs a durable enrolled row.
                foreach (var row in snapshot.Latest.Concat(snapshot.Target is null ? [] : new[] { snapshot.Target })
                    .Concat(snapshot.TargetPrevious is null ? [] : new[] { snapshot.TargetPrevious }))
                    if (!rows.Any(x => x.KeyId == GroupTerminalAdvanceIntegrity.PeekKeyId(row.Envelope, scope))) throw Unavailable();
            }
            DateTimeOffset ObserveNow() => observedClock.GetUtcNow();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw Unavailable(); }
        finally
        {
            foreach (var entity in staged) database.Entry(entity).State = EntityState.Detached;
            foreach (var key in material.Values) key.Dispose(); writeKey?.Dispose();
        }
    }

    internal sealed record Snapshot(GroupTerminalAdvanceRecord[] Latest, GroupTerminalFrontierStateRecord? Cursor,
        GroupTerminalAdvanceRecord? Target, GroupTerminalAdvanceRecord? TargetPrevious, GroupTerminalAdvanceKeyRecord[] Keys);
    internal sealed record AdvanceBlob(long Version, Guid BatchId, int? Envelope, int? Fingerprint,
        int? PreviousFingerprint, int? TerminalHash, int? AuthorityHash);
    internal sealed record KeyBlob(string KeyId, int? Material, int? Authority);

    internal static async Task<GroupSourceKeyMaterial> AwaitMaterialAsync(Func<ValueTask<GroupSourceKeyMaterial>> resolve, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Task<GroupSourceKeyMaterial>? pending = null;
        try
        {
            pending = resolve().AsTask();
            return await pending.WaitAsync(token) ?? throw Unavailable();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A resolver may ignore cancellation. Observe its eventual fault or
            // dispose its late result without awaiting it or exposing material.
            if (pending is not null) _ = pending.ContinueWith(completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion) completed.Result?.Dispose();
                else _ = completed.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
        catch (Exception) { throw Unavailable(); }
    }

    // Share one monotone observation path with the original-history reader, so
    // rollback observed inside that reader cannot be hidden by later recovery.
    internal sealed class MonotoneClock(TimeProvider inner) : TimeProvider
    {
        private DateTimeOffset? minimum;
        public override DateTimeOffset GetUtcNow()
        {
            var now = inner.GetUtcNow();
            if (now.Offset != TimeSpan.Zero || minimum is { } prior && now < prior) throw Unavailable();
            minimum = now; return now;
        }
    }

    private async Task<Snapshot> ReadSnapshotAsync(GroupScope scope, Guid batch, CancellationToken token)
    {
        if (database.ChangeTracker.HasChanges()) throw Unavailable();
        var latest = await ReadRowsAsync(LatestRows(scope), token);
        var cursors = await CursorRows(scope).ToArrayAsync(token);
        if (cursors.Length > 1) throw Unavailable();
        var targets = await ReadRowsAsync(TargetRows(scope, batch), token);
        if (targets.Length > 1) throw Unavailable();
        var prior = targets.Length == 0 || targets[0].Version == 1 ? [] :
            await ReadRowsAsync(VersionRows(scope, checked(targets[0].Version - 1)), token);
        if (prior.Length > 1 || targets.Length != 0 && targets[0].Version > 1 && prior.Length != 1) throw Unavailable();
        var metadata = await KeyRows(scope).Select(x => new KeyBlob(x.KeyId, EF.Functions.DataLength(x.MaterialFingerprint),
            EF.Functions.DataLength(x.AuthoritySha256))).ToArrayAsync(token);
        RequireKeyBlobs(metadata);
        var registry = await KeyRows(scope).ToArrayAsync(token);
        if (registry.Length != metadata.Length || registry.Where((x, i) => x.KeyId != metadata[i].KeyId
            || x.MaterialFingerprint.Length != metadata[i].Material || x.AuthoritySha256.Length != metadata[i].Authority).Any()) throw Unavailable();
        return new(latest, cursors.SingleOrDefault(), targets.SingleOrDefault(), prior.SingleOrDefault(), registry);
    }

    private static async Task<GroupTerminalAdvanceRecord[]> ReadRowsAsync(IQueryable<GroupTerminalAdvanceRecord> query, CancellationToken token)
    {
        var metadata = await query.Select(x => new AdvanceBlob(x.Version, x.BatchId, EF.Functions.DataLength(x.Envelope),
            EF.Functions.DataLength(x.Fingerprint), EF.Functions.DataLength(x.PreviousFingerprint),
            EF.Functions.DataLength(x.TerminalManifestSha256), EF.Functions.DataLength(x.AuthoritySha256))).ToArrayAsync(token);
        RequireAdvanceBlobs(metadata);
        var rows = await query.ToArrayAsync(token);
        if (rows.Length != metadata.Length || rows.Where((x, i) => x.Version != metadata[i].Version || x.BatchId != metadata[i].BatchId
            || x.Envelope.Length != metadata[i].Envelope || x.Fingerprint.Length != metadata[i].Fingerprint
            || x.PreviousFingerprint?.Length != metadata[i].PreviousFingerprint || x.TerminalManifestSha256.Length != metadata[i].TerminalHash
            || x.AuthoritySha256.Length != metadata[i].AuthorityHash).Any()) throw Unavailable();
        return rows;
    }
    internal static void RequireAdvanceBlobs(AdvanceBlob[] rows)
    {
        if (rows is null || rows.Length > 2 || rows.Any(x => x is null || x.Version <= 0 || x.BatchId == Guid.Empty
            || x.Envelope is null or < GroupTerminalAdvanceIntegrity.MinimumBytes or > GroupTerminalAdvanceIntegrity.MaximumBytes
            || x.Fingerprint != 32 || x.TerminalHash != 32 || x.AuthorityHash != 32 || x.PreviousFingerprint is not (null or 32))
            || rows.Select(x => x.Version).Distinct().Count() != rows.Length) throw Unavailable();
    }
    internal static void RequireKeyBlobs(KeyBlob[] rows)
    {
        if (rows is null || rows.Length > MaximumRetainedKeys || rows.Any(x => x is null || string.IsNullOrEmpty(x.KeyId)
            || x.KeyId.Length > 64 || x.KeyId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')
            || x.Material != 32 || x.Authority != 32) || rows.Select(x => x.KeyId).Distinct(StringComparer.Ordinal).Count() != rows.Length) throw Unavailable();
    }
    internal static void RequireSameSnapshot(Snapshot before, Snapshot after)
    {
        if (!JsonSerializer.SerializeToUtf8Bytes(before).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(after))) throw Unavailable();
    }
    private static void RequireWrittenSnapshot(Snapshot before, Snapshot after, GroupTerminalAdvanceRecord receipt,
        GroupTerminalFrontierStateRecord cursor, GroupExtractionAuthority authority, GroupSourceKeyMaterial key, DateTimeOffset now)
    {
        var enrolled = before.Keys.Any(x => x.KeyId == key.KeyId) ? before.Keys : before.Keys.Append(
            GroupTerminalAdvanceKeyProof.Stage(authority, key, now)).OrderBy(x => x.KeyId, StringComparer.Ordinal).ToArray();
        var expected = new Snapshot(new[] { receipt }.Concat(before.Latest.Take(1)).ToArray(), cursor,
            receipt, before.Latest.FirstOrDefault(), enrolled);
        RequireSameSnapshot(expected, after);
    }
    private static GroupTerminalAdvanceIntegrity AuthenticatePrevious(GroupTerminalAdvanceRecord row, GroupScope scope, GroupSourceKeyMaterial key)
    {
        var proof = GroupTerminalAdvanceIntegrity.Require(row.Envelope, scope, key); var metadata = proof.Metadata;
        if (row.TenantId != scope.TenantId || row.CompanyId != scope.CompanyId || row.BindingId != scope.SourceBindingId
            || row.Version != metadata.Version || row.ThroughSequence != metadata.ThroughSequence || row.AdvancedAtUtc != metadata.AdvancedAtUtc
            || row.AdvancedAtUtc.Offset != TimeSpan.Zero || row.EnvelopeVersion != 1 || row.KeyId != metadata.KeyId
            || !CryptographicOperations.FixedTimeEquals(row.Fingerprint, Convert.FromHexString(proof.Fingerprint))) throw Unavailable();
        return proof;
    }
    private static void RequireHistorical(GroupTerminalAdvanceIntegrity proof, GroupBatchHistoricalTerminalEvidence history)
    {
        var value = proof.Metadata;
        if (value.Scope != history.Scope || value.BatchId != history.BatchId || value.TerminalOperationId != history.OperationId
            || value.AfterSequence != history.AfterSequence || value.ThroughSequence != history.ThroughSequence
            || value.TerminalCommittedAtUtc != history.CommittedAtUtc
            || !CryptographicOperations.FixedTimeEquals(value.TerminalManifestSha256, Convert.FromHexString(history.ManifestSha256))) throw Unavailable();
    }
    private static GroupTerminalFrontierAdvanceResult Result(GroupTerminalAdvanceIntegrity proof, bool prior)
    { var value = proof.Metadata; return new(value.Scope, value.BatchId, value.Version, value.ThroughSequence, proof.Fingerprint, value.AdvancedAtUtc, prior); }
    internal IQueryable<GroupTerminalAdvanceRecord> LatestRows(GroupScope scope) => ScopedRows(scope).OrderByDescending(x => x.Version).Take(2);
    internal IQueryable<GroupTerminalAdvanceRecord> TargetRows(GroupScope scope, Guid batch) => ScopedRows(scope).Where(x => x.BatchId == batch).Take(2);
    internal IQueryable<GroupTerminalAdvanceRecord> VersionRows(GroupScope scope, long version) => ScopedRows(scope).Where(x => x.Version == version).Take(2);
    private IQueryable<GroupTerminalAdvanceRecord> ScopedRows(GroupScope scope) => database.GroupTerminalAdvances.AsNoTracking()
        .Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId);
    internal IQueryable<GroupTerminalFrontierStateRecord> CursorRows(GroupScope scope) => database.GroupTerminalFrontierStates.AsNoTracking()
        .Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).Take(2);
    internal IQueryable<GroupTerminalAdvanceKeyRecord> KeyRows(GroupScope scope) => database.GroupTerminalAdvanceKeys.AsNoTracking()
        .Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId)
        .OrderBy(x => x.KeyId).Take(MaximumRetainedKeys + 1);
    private void ValidateEntry(GroupScope scope, Guid batch, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(scope); token.ThrowIfCancellationRequested(); scope.Validate(); worker.Validate();
        if (scope.TenantId != worker.TenantId || scope.CompanyId != worker.CompanyId) throw GroupServiceDirectory.Denied();
        if (batch == Guid.Empty || clock is null || keys is null || !database.Database.IsSqlServer()
            || database.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null
            || database.ChangeTracker.HasChanges()) throw Unavailable();
        try { if (new SqlConnectionStringBuilder(database.Database.GetConnectionString()).MultipleActiveResultSets) throw Unavailable(); }
        catch (Exception) { throw Unavailable(); }
        RequireOutsideSql();
    }
    private void RequireOutsideSql()
    {
        if (database.Database.GetDbConnection().State != ConnectionState.Closed || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null || database.ChangeTracker.HasChanges()) throw Unavailable();
    }
    private static InvalidOperationException Unavailable() => new("Group terminal frontier advance is not available.");
}
