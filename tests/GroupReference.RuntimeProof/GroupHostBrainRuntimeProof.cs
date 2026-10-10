using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Disposable protected reader fixtures only. Operator publication is test
// setup, not the production host consumer or proof of attention reason truth.
internal static class GroupHostBrainRuntimeProof
{
    private const string KeyId = "owned-native-source-v1";
    internal static async Task RunAsync(string mode, GroupScope scope, Guid operation,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        await using var db = new PlatformDbContext(options);
        var batch = await db.GroupBatchAllocations.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation).Select(x => x.Id).SingleAsync(token);
        var original = await db.GroupBatchClaimReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch)
            .OrderByDescending(x => x.Epoch).FirstAsync(token);
        if (original.Epoch != 9) throw new InvalidOperationException();
        var clock = new OwnedClock(original.IssuedAtUtc.AddTicks(1));
        var claim = await new GroupBatchClaimStore(db, worker, clock).TryAcquireAsync(scope, batch, original.OwnerId, original.OperationId,
            TimeSpan.FromTicks(original.RequestedLifetimeTicks), token) ?? throw new InvalidOperationException();
        if (!claim.WasAlreadyClaimed || claim.CurrentHandle is null || claim.Receipt.Epoch != 9) throw new InvalidOperationException();
        var handle = claim.CurrentHandle;
        var source = await db.GroupWorkSourceDispositions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch
            && x.Outcome == GroupWorkSourceOutcome.Work).Select(x => new { x.MessageId, x.MessageRevision }).SingleAsync(token);
        var fixtureOperation = Identity(0);
        var requestIds = Enumerable.Range(1, 3).Select(Identity).ToArray();
        var reasons = new[] { GroupHostAttentionReason.UnsupportedMedia, GroupHostAttentionReason.SecretQuarantine, GroupHostAttentionReason.ExtractionFailed };
        if (mode == "host-brain-fixture")
        {
            var keys = new CountedKeys(db, scope);
            var sourceReader = new GroupBatchSourceReader(db, worker, clock, keys, new());
            var context = await sourceReader.ReadAsync(handle, [source.MessageId], token);
            if (context.Items.Count != 1 || context.Items[0].Revision != source.MessageRevision
                || context.Items[0].Disposition != GroupBatchSourceDisposition.Readable || keys.Reads != 1) throw new InvalidOperationException();
            await new GroupWorkNotePermissionVerifier(db).RequireSafeRuntimeAsync(token);
            var key = Convert.FromBase64String(Environment.GetEnvironmentVariable("AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY") ?? throw new InvalidOperationException());
            var protector = new GroupBrainContentProtector();
            var notes = new List<object>();
            try
            {
                for (var index = 0; index < reasons.Length; index++)
                {
                    var payload = GroupBrainPayloadCodec.EncodeHostAttention(reasons[index], false, [new(source.MessageId, source.MessageRevision)]);
                    var envelope = protector.Protect(new(scope, GroupBrainContentKind.RequestRevision, requestIds[index], 1,
                        handle.Receipt.SourceVersion, handle.Receipt.DeletionGeneration), payload, key, KeyId);
                    notes.Add(new
                    {
                        requestId = requestIds[index],
                        ordinal = index + 1,
                        kind = index == 2 ? 5 : 4,
                        envelope = Convert.ToHexString(envelope),
                        envelopeHash = Convert.ToHexString(SHA256.HashData(envelope))
                    });
                }
            }
            finally { CryptographicOperations.ZeroMemory(key); }
            await sourceReader.RequireCurrentAsync(context, token);
            if (keys.Reads != 1 || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            // Captured privately by the operator harness; never printed there.
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                operationId = fixtureOperation,
                batchId = batch,
                messageId = source.MessageId,
                messageRevision = source.MessageRevision,
                claimEpoch = 9,
                sourceVersion = handle.Receipt.SourceVersion,
                deletionGeneration = handle.Receipt.DeletionGeneration,
                credentialEpoch = handle.Receipt.CredentialEpoch,
                grantVersion = handle.Receipt.GrantVersion,
                accountVersion = handle.Receipt.AccountVersion,
                sourceSetHash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(FormattableString.Invariant($"{source.MessageId:D}/{source.MessageRevision}")))),
                createdAtUtc = clock.GetUtcNow().ToString("O"),
                notes
            }));
            return;
        }
        if (mode != "host-brain-read") throw new InvalidOperationException();
        var glossaryId = await db.GroupGlossaryEntries.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).Select(x => x.Id).SingleAsync(token);
        var readKeys = new CountedKeys(db, scope); var reader = new GroupBrainCurrentReader(db, worker, clock, readKeys, new());
        var brain = await reader.ReadAsync(handle, requestIds, [glossaryId], token);
        if (brain.Scope != scope || brain.Items.Count != 4 || readKeys.Reads != 1 || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
        for (var index = 0; index < reasons.Length; index++)
        {
            var item = brain.Items[index]; var payload = GroupBrainPayloadCodec.DecodeHostAttention(item.Content);
            if (item.RecordId != requestIds[index] || item.Revision != 1 || item.Origin != GroupRequestRevisionOrigin.HostAttention
                || item.VerificationLevel != GroupRequestVerificationLevel.HostObserved || item.IsItConfirmed
                || item.BusinessStatus != GroupNoteBusinessStatus.NeedsClarification || payload.Reason != reasons[index]
                || payload.HasCoverageGap || payload.SourceReferences.Count != 1
                || payload.SourceReferences[0] != new GroupHostAttentionReference(source.MessageId, source.MessageRevision)) throw new InvalidOperationException();
        }
        if (brain.Items[3].Kind != GroupBrainContentKind.GlossaryRevision || brain.Items[3].Origin is not null
            || brain.Items[3].VerificationLevel is not null || brain.Items[3].IsItConfirmed) throw new InvalidOperationException();
        await reader.RequireCurrentAsync(brain, token);
        if (readKeys.Reads != 1 || db.ChangeTracker.HasChanges() || db.Database.CurrentTransaction is not null) throw new InvalidOperationException();
        Console.WriteLine("PASS owned host brain actual SQL three distinct observed reasons exact protected metadata evidence current scoped keys glossary and final fence");

        Guid Identity(int ordinal) => new(SHA256.HashData(Encoding.ASCII.GetBytes(FormattableString.Invariant(
            $"aioffice-owned-host-reader-v1/{scope.TenantId:D}/{scope.CompanyId:D}/{scope.SourceBindingId:D}/{operation:D}/{ordinal}"))).AsSpan(0, 16));
    }
    private sealed class OwnedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class CountedKeys(PlatformDbContext db, GroupScope scope) : IGroupSourceKeyProvider
    {
        private readonly ConfiguredGroupSourceKeyProvider configured = new(new CompositeSecretResolver([new EnvironmentVariableSecretResolver()]),
            [new(scope, KeyId, SecretReference.Parse("secretref://env/AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY"), true)]);
        internal int Reads;
        public ValueTask<GroupSourceKeyMaterial> ResolveWriteAsync(GroupScope source, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public async ValueTask<GroupSourceKeyMaterial> ResolveReadAsync(GroupScope source, string keyId, CancellationToken cancellationToken = default)
        {
            if (source != scope || keyId != KeyId || db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            Reads++; return await configured.ResolveReadAsync(source, keyId, cancellationToken);
        }
    }
}
