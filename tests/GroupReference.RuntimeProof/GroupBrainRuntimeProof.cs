using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Disposable CI fixture only: no production registration, provider or note
// effect store. The operator harness publishes these owned protected fixtures.
internal static class GroupBrainRuntimeProof
{
    private const string KeyId = "owned-native-source-v1";
    private const string RequestText = "{\"ownedNote\":\"Tồn kho 😀\uFEFF \"}";
    private const string GlossaryText = "{\"ownedGlossary\":\"Nhập xuất 😀\uFEFF \"}";
    internal static async Task RunAsync(string mode, GroupScope scope, Guid operation,
        GroupExtractionWorkerBinding worker, DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        await using var db = new PlatformDbContext(options);
        var batch = await db.GroupBatchAllocations.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation)
            .Select(x => x.Id).SingleAsync(token);
        var original = await db.GroupBatchClaimReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch)
            .OrderByDescending(x => x.Epoch).FirstAsync(token);
        var clock = new OwnedClock(TimeProvider.System.GetUtcNow());
        var claims = new GroupBatchClaimStore(db, worker, clock);
        GroupBatchClaimResult claim;
        if (mode == "brain-fixture")
        {
            // All original epoch3/source expiry assertions already completed.
            // Advance the owned clock monotonically past its durable witness;
            // use new immutable owner/nonce, never renew an original receipt.
            var state = await db.GroupBatchClaimStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch, token);
            if (state.Epoch != 3 || state.ExpiryObservedAtUtc != original.ExpiresAtUtc) throw new InvalidOperationException();
            clock.Current = original.ExpiresAtUtc.AddTicks(1);
            claim = await claims.TryAcquireAsync(scope, batch, Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(2), token)
                ?? throw new InvalidOperationException();
            if (claim.WasAlreadyClaimed || claim.Receipt.Epoch != 4 || claim.CurrentHandle is null) throw new InvalidOperationException();
            await FixtureAsync(claim.CurrentHandle); return;
        }
        if (original.Epoch != 4) throw new InvalidOperationException();
        if (clock.Current < original.IssuedAtUtc) clock.Current = original.IssuedAtUtc;
        if (mode == "brain-deny")
        {
            var denied = false;
            try
            {
                await claims.TryAcquireAsync(scope, batch, original.OwnerId, original.OperationId,
                TimeSpan.FromTicks(original.RequestedLifetimeTicks), token);
            }
            catch (UnauthorizedAccessException) { denied = true; }
            if (!denied || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            Console.WriteLine("PASS owned brain runtime current Extract refuses before keys"); return;
        }
        claim = await claims.TryAcquireAsync(scope, batch, original.OwnerId, original.OperationId,
            TimeSpan.FromTicks(original.RequestedLifetimeTicks), token) ?? throw new InvalidOperationException();
        if (claim.CurrentHandle is null || !claim.WasAlreadyClaimed || claim.Receipt.Epoch != 4) throw new InvalidOperationException();
        var requestId = await db.GroupCustomerRequests.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).Select(x => x.Id).SingleAsync(token);
        var glossaryId = await db.GroupGlossaryEntries.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId).Select(x => x.Id).SingleAsync(token);
        Func<CancellationToken, Task>? afterKey = mode == "brain-expiry" ? _ =>
            { clock.Current = original.ExpiresAtUtc; return Task.CompletedTask; }
        : mode == "brain-key-revoke" ? async cancellationToken =>
        {
            Console.WriteLine("CHECKPOINT owned brain key resolved outside SQL before final fence"); Console.Out.Flush();
            await File.WriteAllTextAsync("/tmp/aioffice-brain-proof-awaiting", "ready", cancellationToken);
            while (!File.Exists("/tmp/aioffice-brain-proof-release")) await Task.Delay(50, cancellationToken);
        }
        : null;
        var keys = new CountedKeys(db, scope, afterKey);
        var reader = new GroupBrainCurrentReader(db, worker, clock, keys, new());
        if (mode is "brain-foreign" or "brain-policy-deny")
        {
            var refused = false;
            try { await reader.ReadAsync(claim.CurrentHandle, [mode == "brain-foreign" ? Guid.NewGuid() : requestId], [glossaryId], token); }
            catch (InvalidOperationException error) when (error.Message == "Group brain context is unavailable." && error.InnerException is null)
            { refused = true; }
            if (!refused || keys.Reads != 0 || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            Console.WriteLine("PASS owned brain runtime selected identity or glossary policy refuses before keys"); return;
        }
        if (mode == "brain-key-revoke")
        {
            var denied = false;
            try { await reader.ReadAsync(claim.CurrentHandle, [requestId], [glossaryId], token); }
            catch (UnauthorizedAccessException) { denied = true; }
            if (!denied || keys.Reads != 1 || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            Console.WriteLine("PASS owned brain runtime SQL Extract revocation during key await denies private context"); return;
        }
        if (mode == "brain-expiry")
        {
            var before = clock.Current; var denied = false;
            try { await reader.ReadAsync(claim.CurrentHandle, [requestId], [glossaryId], token); }
            catch (UnauthorizedAccessException) { denied = true; }
            if (!denied || keys.Reads != 1 || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            clock.Current = before; denied = false;
            try { await reader.ReadAsync(claim.CurrentHandle, [requestId], [glossaryId], token); }
            catch (UnauthorizedAccessException) { denied = true; }
            var state = await db.GroupBatchClaimStates.AsNoTracking().SingleAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch, token);
            if (!denied || keys.Reads != 1 || state.Epoch != 4 || state.ExpiryObservedAtUtc != original.ExpiresAtUtc
                || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            Console.WriteLine("PASS owned brain runtime controlled key-await expiry commits SQL witness and denies after clock rollback"); return;
        }
        if (mode != "brain-read") throw new InvalidOperationException();
        var context = await reader.ReadAsync(claim.CurrentHandle, [requestId], [glossaryId], token);
        if (context.Scope != scope || context.Items.Count != 2 || keys.Reads != 1
            || context.Items[0].Kind != GroupBrainContentKind.RequestRevision || context.Items[0].Content != RequestText
            || context.Items[0].RecordId != requestId || context.Items[0].Revision != 1 || context.Items[0].IsItConfirmed
            || context.Items[0].BusinessStatus != GroupNoteBusinessStatus.New || context.Items[0].RequestCode != "REQ-" + requestId.ToString("N").ToUpperInvariant()
            || context.Items[1].Kind != GroupBrainContentKind.GlossaryRevision || context.Items[1].RecordId != glossaryId
            || context.Items[1].Revision != 1 || context.Items[1].Content != GlossaryText
            || context.ToString().Contains("ownedNote", StringComparison.Ordinal)
            || context.Items.Any(x => x.ToString().Contains("owned", StringComparison.Ordinal))) throw new InvalidOperationException();
        await reader.RequireCurrentAsync(context, token);
        if (keys.Reads != 1 || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
        Console.WriteLine("PASS owned brain runtime actual SQL request glossary exact evidence configured keys and final current context");

        async Task FixtureAsync(GroupBatchClaimHandle handle)
        {
            var sourceIds = await db.GroupBatchAllocatedRevisions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch)
                .Select(x => x.MessageId).Distinct().OrderBy(x => x).ToArrayAsync(token);
            var fixtureKeys = new CountedKeys(db, scope, null);
            var sourceReader = new GroupBatchSourceReader(db, worker, clock, fixtureKeys, new());
            var sourceContext = await sourceReader.ReadAsync(handle, sourceIds, token);
            if (sourceContext.Items.Count != 2 || sourceContext.Items.Any(x => x.Disposition != GroupBatchSourceDisposition.Readable)) throw new InvalidOperationException();
            var publisher = await db.CompanyMemberships.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.IsActive).OrderBy(x => x.UserId).Select(x => x.UserId).FirstAsync(token);
            // Fixture producer uses only the already-injected disposable key.
            // The actual reader independently resolves the configured provider.
            var fixtureKey = Convert.FromBase64String(Environment.GetEnvironmentVariable("AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY") ?? throw new InvalidOperationException());
            var request = Guid.NewGuid(); var glossary = Guid.NewGuid();
            var protector = new GroupBrainContentProtector();
            byte[] requestContent, glossaryContent;
            try
            {
                requestContent = protector.Protect(new(scope, GroupBrainContentKind.RequestRevision, request, 1,
                    handle.Receipt.SourceVersion, handle.Receipt.DeletionGeneration), RequestText, fixtureKey, KeyId);
                glossaryContent = protector.Protect(new(scope, GroupBrainContentKind.GlossaryRevision, glossary, 1,
                    handle.Receipt.SourceVersion, handle.Receipt.DeletionGeneration), GlossaryText, fixtureKey, KeyId);
            }
            finally { CryptographicOperations.ZeroMemory(fixtureKey); }
            await sourceReader.RequireCurrentAsync(sourceContext, token);
            if (fixtureKeys.Reads != 1 || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            // Protected bytes/closed fixture metadata are captured privately by
            // the owned operator harness, never printed into the CI log.
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                requestId = request,
                glossaryId = glossary,
                publisherId = publisher,
                batchId = batch,
                claimEpoch = handle.Receipt.Epoch,
                sourceVersion = handle.Receipt.SourceVersion,
                deletionGeneration = handle.Receipt.DeletionGeneration,
                credentialEpoch = handle.Receipt.CredentialEpoch,
                grantVersion = handle.Receipt.GrantVersion,
                accountVersion = handle.Receipt.AccountVersion,
                sourceSetHash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(
                    FormattableString.Invariant($"{sourceContext.Items[0].MessageId:D}/{sourceContext.Items[0].Revision}")))),
                createdAtUtc = clock.Current.ToString("O"),
                messageId = sourceContext.Items[0].MessageId,
                messageRevision = sourceContext.Items[0].Revision,
                requestEnvelope = Convert.ToHexString(requestContent),
                glossaryEnvelope = Convert.ToHexString(glossaryContent),
                requestHash = Convert.ToHexString(SHA256.HashData(requestContent)),
                glossaryHash = Convert.ToHexString(SHA256.HashData(glossaryContent))
            }));
        }
    }

    private sealed class OwnedClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Current = now;
        public override DateTimeOffset GetUtcNow() => Current;
    }
    private sealed class CountedKeys(PlatformDbContext db, GroupScope scope, Func<CancellationToken, Task>? afterKey) : IGroupSourceKeyProvider
    {
        private readonly ConfiguredGroupSourceKeyProvider configured = new(new CompositeSecretResolver([new EnvironmentVariableSecretResolver()]),
            [new(scope, KeyId, SecretReference.Parse("secretref://env/AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY"), true)]);
        internal int Reads;
        public ValueTask<GroupSourceKeyMaterial> ResolveWriteAsync(GroupScope source, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public async ValueTask<GroupSourceKeyMaterial> ResolveReadAsync(GroupScope source, string keyId, CancellationToken cancellationToken = default)
        {
            if (source != scope || keyId != KeyId || db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges()) throw new InvalidOperationException();
            Reads++; var material = await configured.ResolveReadAsync(source, keyId, cancellationToken);
            try { if (afterKey is not null) await afterKey(cancellationToken); return material; }
            catch { material.Dispose(); throw; }
        }
    }
}
