using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// New owned phase after terminal receipts. Root only: actual old graph reads and
// atomic key/advance/cursor effects; no successor/race/DI/model qualification.
internal static class GroupFrontierRootRuntimeProof
{
    internal const string Success = "PASS owned frontier root SQL original history after expired acquisition atomic key advance cursor actual all fields readback postflush clock rollback all three effects empty immutable26 and14 column denials missing rebound key no effects";
    internal const string ReplaySuccess = "PASS owned frontier root independent process replay original signed version time fingerprint all original graphs unchanged no extra advance key or cursor";
    internal const string KeyId = "owned-frontier-v1";

    internal static async Task RunAsync(string mode, GroupScope scope, Guid allocationOperation, GroupExtractionWorkerBinding worker,
        DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        if (mode is not ("frontier-root" or "frontier-root-replay")) throw new InvalidOperationException();
        var clock = new GroupNoteRuntimeProof.OwnedClock(TimeProvider.System.GetUtcNow());
        var probe = new Probe(scope, clock);
        await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>(options)
            .AddInterceptors(new FlushProbe(probe), new SavepointProbe(probe)).Options);
        var allocation = await db.GroupBatchAllocations.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == allocationOperation, token);
        var terminal = await db.GroupBatchTerminalReceipts.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
        var work = await db.GroupWorkCommitReceipts.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
        if (allocation.AfterSequence != 0 || allocation.RawRevisionCount is not (2 or 500) || work.SelectedMessageCount != 2
            || work.NoteCount is not (0 or 1 or 4) || terminal.ContributorCount != 1 || terminal.Manifest.Length != 257) throw new InvalidOperationException();
        clock.Current = terminal.CommittedAtUtc.AddHours(1);
        if (clock.Current < TimeProvider.System.GetUtcNow()) clock.Current = TimeProvider.System.GetUtcNow();
        var maximumExpiry = await db.GroupBatchClaimReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id).MaxAsync(x => x.ExpiresAtUtc, token);
        if (clock.Current <= maximumExpiry) throw new InvalidOperationException();
        var originalGraph = await GroupNoteRuntimeProof.CommitGraphDigestAsync(db, scope, work.OperationId, token);
        var originalRaw = await GroupAutomaticRawRuntimeProof.RequireAsync(db, scope, work.OperationId, allocation.RawRevisionCount, token);
        var originalClaims = await ClaimBytesAsync(); var originalTerminal = JsonSerializer.SerializeToUtf8Bytes(terminal);
        var authority = await AuthorityAsync(db, scope, worker, token);
        using var keys = new Keys(db, scope); var store = new GroupTerminalFrontierStore(db, worker, clock, keys);
        if (mode == "frontier-root-replay")
        {
            if (await CountsAsync() != (1, 1, 1)) throw new InvalidOperationException();
            var original = await WrittenAsync(); var snapshot = Snapshot(original);
            if (clock.Current < original.Advance.AdvancedAtUtc) clock.Current = original.Advance.AdvancedAtUtc;
            RequireValue(original.Advance, original.Cursor, original.Key, terminal, authority, original.Advance.AdvancedAtUtc, keys.Raw);
            var replay = await store.AdvanceAsync(scope, allocation.Id, token);
            var afterReplay = Snapshot(await WrittenAsync());
            if (!replay.WasAlreadyAdvanced || replay.Version != 1 || replay.ThroughSequence != allocation.AllocatedThroughSequence
                || replay.Fingerprint != Convert.ToHexString(original.Advance.Fingerprint) || replay.AdvancedAtUtc != original.Advance.AdvancedAtUtc
                || !snapshot.AsSpan().SequenceEqual(afterReplay) || keys.Reads != 1 || keys.Writes != 1) throw new InvalidOperationException();
            await OriginalsAsync(); Console.WriteLine(ReplaySuccess); return;
        }
        if (await CountsAsync() != (0, 0, 0)) throw new InvalidOperationException();
        var expected = clock.Current;
        probe.Armed = true;
        await RefusedAsync(() => store.AdvanceAsync(scope, allocation.Id, token));
        probe.Armed = false;
        if (!probe.Staged || !probe.Flushed || probe.LockChecks < 1 || await CountsAsync() != (0, 0, 0)) throw new InvalidOperationException();
        await OriginalsAsync();
        clock.Current = expected.AddTicks(1); var savedAt = clock.Current;
        var result = await store.AdvanceAsync(scope, allocation.Id, token);
        var saved = await WrittenAsync();
        RequireValue(saved.Advance, saved.Cursor, saved.Key, terminal, authority, savedAt, keys.Raw);
        if (result.WasAlreadyAdvanced || result.Version != 1 || result.BatchId != allocation.Id || result.Scope != scope
            || result.ThroughSequence != allocation.AllocatedThroughSequence || result.Fingerprint != Convert.ToHexString(saved.Advance.Fingerprint)
            || result.AdvancedAtUtc != savedAt || await CountsAsync() != (1, 1, 1) || keys.Reads != 0 || keys.Writes != 2) throw new InvalidOperationException();
        var snapshotAfter = Snapshot(saved);
        foreach (var fault in new[] { "missing", "rebound", "missing-write" })
        {
            keys.Fault = fault; await RefusedAsync(() => store.AdvanceAsync(scope, allocation.Id, token)); keys.Fault = null;
            var afterFault = Snapshot(await WrittenAsync());
            if (!snapshotAfter.AsSpan().SequenceEqual(afterFault)) throw new InvalidOperationException();
            await OriginalsAsync();
        }
        foreach (var (table, columns) in new[] { ("GroupTerminalAdvances", typeof(GroupTerminalAdvanceRecord).GetProperties()),
            ("GroupTerminalAdvanceKeys", typeof(GroupTerminalAdvanceKeyRecord).GetProperties()) })
        {
            if (columns.Length != (table == "GroupTerminalAdvances" ? 26 : 14)) throw new InvalidOperationException();
            foreach (var column in columns)
            {
                if (column.Name.Any(x => !char.IsAsciiLetterOrDigit(x))) throw new InvalidOperationException();
                var denied = false;
                try
                {
                    var update = "UPDATE aioffice.[" + table + "] SET [" + column.Name + "]=[" + column.Name
                        + "] WHERE TenantId=@tenant AND CompanyId=@company AND BindingId=@binding;";
                    await db.Database.ExecuteSqlRawAsync(update,
                        new object[] { new SqlParameter("@tenant", scope.TenantId), new SqlParameter("@company", scope.CompanyId),
                            new SqlParameter("@binding", scope.SourceBindingId) }, token);
                }
                catch (SqlException error) when (error.Number == 229) { denied = true; }
                if (!denied) throw new InvalidOperationException();
            }
        }
        var afterColumns = Snapshot(await WrittenAsync());
        if (!snapshotAfter.AsSpan().SequenceEqual(afterColumns)) throw new InvalidOperationException();
        await OriginalsAsync(); Console.WriteLine(Success);

        async Task<(int, int, int)> CountsAsync() => (await db.GroupTerminalAdvances.AsNoTracking().CountAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token),
            await db.GroupTerminalAdvanceKeys.AsNoTracking().CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token),
            await db.GroupTerminalFrontierStates.AsNoTracking().CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token));
        async Task<Written> WrittenAsync() => new(await db.GroupTerminalAdvances.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token),
            await db.GroupTerminalAdvanceKeys.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token),
            await db.GroupTerminalFrontierStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token));
        async Task<byte[]> ClaimBytesAsync() => JsonSerializer.SerializeToUtf8Bytes(await db.GroupBatchClaimReceipts.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id)
            .OrderBy(x => x.Epoch).ToArrayAsync(token));
        async Task OriginalsAsync()
        {
            var claims = await ClaimBytesAsync();
            if (db.ChangeTracker.HasChanges() || db.Database.CurrentTransaction is not null || db.Database.GetDbConnection().State != ConnectionState.Closed
                || db.ChangeTracker.Entries().Any(x => x.Entity is GroupTerminalAdvanceRecord or GroupTerminalAdvanceKeyRecord or GroupTerminalFrontierStateRecord)
                || originalGraph != await GroupNoteRuntimeProof.CommitGraphDigestAsync(db, scope, work.OperationId, token)
                || originalRaw != await GroupAutomaticRawRuntimeProof.RequireAsync(db, scope, work.OperationId, allocation.RawRevisionCount, token)
                || !originalClaims.AsSpan().SequenceEqual(claims)) throw new InvalidOperationException();
            var actual = await db.GroupBatchTerminalReceipts.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == allocation.Id, token);
            if (!originalTerminal.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(actual))) throw new InvalidOperationException();
        }
    }

    private sealed record Written(GroupTerminalAdvanceRecord Advance, GroupTerminalAdvanceKeyRecord Key, GroupTerminalFrontierStateRecord Cursor);
    private static byte[] Snapshot(Written value) => JsonSerializer.SerializeToUtf8Bytes(value);
    internal sealed record Authority(GroupScope Scope, Guid Service, long Credential, long Grant, long Source, long Deletion, long Account, byte[] Hash);
    private static async Task<Authority> AuthorityAsync(PlatformDbContext db, GroupScope scope, GroupExtractionWorkerBinding worker, CancellationToken token)
    {
        var binding = await db.GroupBindings.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.Id == scope.SourceBindingId, token);
        var service = await db.GroupServices.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.Id == worker.ServiceId, token);
        var grant = await db.GroupServiceGrants.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId
            && x.ServiceId == worker.ServiceId && x.BindingId == scope.SourceBindingId && x.Capability == GroupServiceCapability.Extract, token);
        var account = await db.GroupConnectorAccounts.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.Id == binding.ConnectorAccountId, token);
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
        writer.Write("aioffice-group-claim-authority-v1");
        foreach (var id in new[] { scope.TenantId, scope.CompanyId, worker.ServiceId, scope.TenantId, scope.CompanyId, worker.ServiceId,
            scope.SourceBindingId, scope.TenantId, scope.CompanyId, scope.SourceBindingId, binding.ConnectorAccountId }) writer.Write(id.ToByteArray());
        writer.Write(service.CredentialEpoch); writer.Write(service.IsEnabled); writer.Write((int)grant.Capability); writer.Write(grant.Version); writer.Write(grant.IsEnabled);
        writer.Write(binding.Provider); writer.Write(binding.ExternalAccountId); writer.Write(binding.ExternalGroupId); writer.Write(binding.DisplayName);
        writer.Write(binding.Version); writer.Write(binding.DeletionGeneration); writer.Write(binding.IsEnabled); writer.Write(account.Version); writer.Write(service.CredentialReference); writer.Flush();
        return new(scope, worker.ServiceId, worker.CredentialEpoch, grant.Version, binding.Version, binding.DeletionGeneration, account.Version, SHA256.HashData(stream.ToArray()));
    }

    internal static void RequireValue(GroupTerminalAdvanceRecord row, GroupTerminalFrontierStateRecord cursor, GroupTerminalAdvanceKeyRecord enrollment,
        GroupBatchTerminalReceiptRecord terminal, Authority authority, DateTimeOffset expectedUtc, byte[] rawKey)
    {
        if (row is null || cursor is null || enrollment is null || terminal is null || authority is null
            || rawKey is not { Length: 32 } || authority.Hash is not { Length: 32 }
            || row.Envelope is not { Length: >= 322 and <= 385 } || row.Fingerprint is not { Length: 32 }
            || row.TerminalManifestSha256 is not { Length: 32 } || row.AuthoritySha256 is not { Length: 32 }
            || terminal.ManifestSha256 is not { Length: 32 } || enrollment.MaterialFingerprint is not { Length: 32 }
            || enrollment.AuthoritySha256 is not { Length: 32 }
            || authority.Scope.TenantId == Guid.Empty || authority.Scope.CompanyId == Guid.Empty || authority.Scope.SourceBindingId == Guid.Empty
            || authority.Service == Guid.Empty || authority.Credential <= 0 || authority.Grant <= 0 || authority.Source <= 0 || authority.Deletion < 0 || authority.Account <= 0
            || terminal.TenantId != authority.Scope.TenantId || terminal.CompanyId != authority.Scope.CompanyId || terminal.BindingId != authority.Scope.SourceBindingId
            || terminal.BatchId == Guid.Empty || terminal.OperationId == Guid.Empty || terminal.AfterSequence != 0 || terminal.ThroughSequence <= 0
            || terminal.CommittedAtUtc > expectedUtc
            || expectedUtc.Offset != TimeSpan.Zero || row.TenantId != authority.Scope.TenantId || row.CompanyId != authority.Scope.CompanyId
            || row.BindingId != authority.Scope.SourceBindingId || row.Version != 1 || row.BatchId != terminal.BatchId || row.TerminalOperationId != terminal.OperationId
            || row.AfterSequence != 0 || row.ThroughSequence != terminal.ThroughSequence || row.TerminalCommittedAtUtc != terminal.CommittedAtUtc
            || row.TerminalCommittedAtUtc.Offset != TimeSpan.Zero || row.PreviousVersion is not null || row.PreviousThroughSequence is not null
            || row.PreviousFingerprint is not null || row.PreviousAdvancedAtUtc is not null || row.EnvelopeVersion != 1 || row.KeyId != KeyId
            || row.ServiceId != authority.Service || row.CredentialEpoch != authority.Credential || row.GrantVersion != authority.Grant
            || row.SourceVersion != authority.Source || row.DeletionGeneration != authority.Deletion || row.AccountVersion != authority.Account
            || row.AdvancedAtUtc != expectedUtc || row.AdvancedAtUtc.Offset != TimeSpan.Zero
            || !row.TerminalManifestSha256.AsSpan().SequenceEqual(terminal.ManifestSha256) || !row.AuthoritySha256.AsSpan().SequenceEqual(authority.Hash)) throw new InvalidOperationException();
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write("AIOGFAD1"u8);
        foreach (var id in new[] { authority.Scope.TenantId, authority.Scope.CompanyId, authority.Scope.SourceBindingId, terminal.BatchId, terminal.OperationId, authority.Service }) writer.Write(id.ToByteArray());
        foreach (var number in new[] { 0L, 1, 0, terminal.ThroughSequence, authority.Credential, authority.Grant, authority.Source, authority.Deletion, authority.Account }) writer.Write(number);
        writer.Write(terminal.CommittedAtUtc.UtcTicks); writer.Write(expectedUtc.UtcTicks); writer.Write(new byte[32]); writer.Write(terminal.ManifestSha256); writer.Write(authority.Hash);
        writer.Write((byte)KeyId.Length); writer.Write(Encoding.ASCII.GetBytes(KeyId)); writer.Flush();
        var derived = new byte[32];
        try
        {
            var salt = Encoding.ASCII.GetBytes(FormattableString.Invariant($"aioffice-group-frontier-key-v1\0{authority.Scope.TenantId:D}/{authority.Scope.CompanyId:D}/{authority.Scope.SourceBindingId:D}"));
            HKDF.DeriveKey(HashAlgorithmName.SHA256, rawKey.AsSpan(), derived.AsSpan(), salt.AsSpan(), "aioffice-group-terminal-advance-integrity-v1"u8);
            var body = stream.ToArray(); var mac = HMACSHA256.HashData(derived, body); var envelope = body.Concat(mac).ToArray();
            if (!row.Envelope.AsSpan().SequenceEqual(envelope) || !CryptographicOperations.FixedTimeEquals(row.Fingerprint, SHA256.HashData(envelope))) throw new InvalidOperationException();
            salt = Encoding.ASCII.GetBytes(FormattableString.Invariant($"aioffice-group-terminal-enrollment-key-v1\0{authority.Scope.TenantId:D}/{authority.Scope.CompanyId:D}/{authority.Scope.SourceBindingId:D}/{KeyId}"));
            HKDF.DeriveKey(HashAlgorithmName.SHA256, rawKey.AsSpan(), derived.AsSpan(), salt.AsSpan(), "aioffice-group-terminal-advance-key-enrollment-v1"u8);
            if (enrollment.TenantId != row.TenantId || enrollment.CompanyId != row.CompanyId || enrollment.BindingId != row.BindingId || enrollment.KeyId != KeyId
                || enrollment.FormatVersion != 1 || enrollment.EnrolledAtUtc != expectedUtc || enrollment.EnrolledAtUtc.Offset != TimeSpan.Zero
                || enrollment.ServiceId != authority.Service || enrollment.CredentialEpoch != authority.Credential || enrollment.GrantVersion != authority.Grant
                || enrollment.SourceVersion != authority.Source || enrollment.DeletionGeneration != authority.Deletion || enrollment.AccountVersion != authority.Account
                || !enrollment.AuthoritySha256.AsSpan().SequenceEqual(authority.Hash) || !CryptographicOperations.FixedTimeEquals(enrollment.MaterialFingerprint, SHA256.HashData(derived))
                || cursor.TenantId != row.TenantId || cursor.CompanyId != row.CompanyId || cursor.BindingId != row.BindingId || cursor.Version != 1
                || cursor.ThroughSequence != row.ThroughSequence || cursor.LastTerminalBatchId != row.BatchId || cursor.UpdatedAtUtc != expectedUtc || cursor.UpdatedAtUtc.Offset != TimeSpan.Zero) throw new InvalidOperationException();
        }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }

    private static async Task RefusedAsync(Func<Task<GroupTerminalFrontierAdvanceResult>> call)
    {
        var refused = false;
        try { await call(); } catch (InvalidOperationException error) when (error.Message == "Group terminal frontier advance is not available." && error.InnerException is null) { refused = true; }
        if (!refused) throw new InvalidOperationException();
    }
    private sealed class Keys : IGroupSourceKeyProvider, IDisposable
    {
        private readonly PlatformDbContext db; private readonly GroupScope scope; private readonly ConfiguredGroupSourceKeyProvider configured;
        internal int Reads, Writes; internal string? Fault;
        internal byte[] Raw { get; }
        internal Keys(PlatformDbContext db, GroupScope scope)
        {
            this.db = db; this.scope = scope;
            Raw = Convert.FromBase64String(Environment.GetEnvironmentVariable("AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY") ?? throw new InvalidOperationException());
            if (Raw.Length != 32) throw new InvalidOperationException();
            configured = new(new CompositeSecretResolver([new EnvironmentVariableSecretResolver()]),
                [new(scope, KeyId, SecretReference.Parse("secretref://env/AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY"), true)]);
        }
        private void RequireOutsideSql(GroupScope value)
        { if (value != scope || db.Database.CurrentTransaction is not null || db.Database.GetDbConnection().State != ConnectionState.Closed || db.ChangeTracker.HasChanges()) throw new InvalidOperationException(); }
        public async ValueTask<GroupSourceKeyMaterial> ResolveReadAsync(GroupScope value, string id, CancellationToken token = default)
        {
            RequireOutsideSql(value); Reads++; if (id != KeyId || Fault == "missing") throw new InvalidOperationException();
            if (Fault == "rebound") { var changed = Raw.ToArray(); try { changed[0] ^= 1; return new(KeyId, changed); } finally { CryptographicOperations.ZeroMemory(changed); } }
            return await configured.ResolveReadAsync(value, id, token);
        }
        public async ValueTask<GroupSourceKeyMaterial> ResolveWriteAsync(GroupScope value, CancellationToken token = default)
        { RequireOutsideSql(value); Writes++; if (Fault == "missing-write") throw new InvalidOperationException(); return await configured.ResolveWriteAsync(value, token); }
        public void Dispose() => CryptographicOperations.ZeroMemory(Raw);
    }
    private sealed class Probe(GroupScope scope, GroupNoteRuntimeProof.OwnedClock clock)
    {
        internal GroupScope Scope { get; } = scope;
        internal bool Armed, Staged, Flushed;
        internal int LockChecks;
        internal void RollClockBack() => clock.Current = clock.Current.AddTicks(-1);
    }
    private sealed class FlushProbe(Probe probe) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken token = default)
        {
            if (probe.Armed && data.Context is PlatformDbContext db)
            {
                var rows = db.ChangeTracker.Entries().Where(x => x.State is not (EntityState.Unchanged or EntityState.Detached)).ToArray();
                if (rows.Length != 3 || rows.Any(x => x.State != EntityState.Added)
                    || rows.Count(x => x.Entity is GroupTerminalAdvanceRecord) != 1 || rows.Count(x => x.Entity is GroupTerminalAdvanceKeyRecord) != 1
                    || rows.Count(x => x.Entity is GroupTerminalFrontierStateRecord) != 1) throw new InvalidOperationException();
                probe.Staged = true;
            }
            return ValueTask.FromResult(result);
        }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken token = default)
        {
            if (probe.Armed && data.Context is PlatformDbContext db)
            {
                var scope = probe.Scope;
                if (!probe.Staged || await db.GroupTerminalAdvances.AsNoTracking().CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token) != 1
                    || await db.GroupTerminalAdvanceKeys.AsNoTracking().CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token) != 1
                    || await db.GroupTerminalFrontierStates.AsNoTracking().CountAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token) != 1) throw new InvalidOperationException();
                probe.Flushed = true; probe.RollClockBack();
            }
            return result;
        }
    }
    private sealed class SavepointProbe(Probe probe) : DbTransactionInterceptor
    {
        public override async ValueTask<InterceptionResult> CreatingSavepointAsync(DbTransaction transaction, TransactionEventData data, InterceptionResult result, CancellationToken token = default)
        {
            if (!probe.Armed) return result;
            if (transaction.IsolationLevel != IsolationLevel.Serializable || transaction.Connection is null) throw new InvalidOperationException();
            await using var command = transaction.Connection.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 5;
            command.CommandText = "SELECT APPLOCK_MODE(N'public',@resource,N'Transaction');";
            var parameter = command.CreateParameter(); parameter.ParameterName = "@resource";
            parameter.Value = $"aioffice:group-ingest:{probe.Scope.TenantId:N}/{probe.Scope.CompanyId:N}/{probe.Scope.SourceBindingId:N}"; command.Parameters.Add(parameter);
            if (!string.Equals((string?)await command.ExecuteScalarAsync(token), "Exclusive", StringComparison.Ordinal)) throw new InvalidOperationException();
            probe.LockChecks++; return result;
        }
    }
}
