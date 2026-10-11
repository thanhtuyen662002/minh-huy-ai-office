using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Structural metadata/cipher prerequisite for a later original-effect digest.
// It does not read SQL, authorize a caller, store a digest or finish a batch.
// Mutable IT heads and publishing progress are excluded from the original
// create graph; immutable original revision/evidence/items remain included.
internal sealed class GroupWorkEffectLedger
{
    private readonly GroupWorkCommitReceiptRecord originalReceipt;
    private GroupWorkEffectLedger(GroupScope scope, GroupWorkCommitReceiptRecord work, string fingerprint)
    { Scope = scope; BatchId = work.BatchId; OperationId = work.OperationId; NoteCount = work.NoteCount; Fingerprint = fingerprint; originalReceipt = work; }
    internal GroupScope Scope { get; }
    internal Guid BatchId { get; }
    internal Guid OperationId { get; }
    internal int NoteCount { get; }
    internal string Fingerprint { get; }
    public override string ToString() => "Group original effect ledger (private metadata).";

    // A later digest helper must bind the observed graph to the complete
    // immutable receipt snapshot. Expected-digest fields are excluded to
    // avoid a self-reference while staging or comparing that expectation.
    internal bool MatchesReceipt(GroupWorkCommitReceiptRecord current) => current.TenantId == originalReceipt.TenantId
        && current.CompanyId == originalReceipt.CompanyId && current.BindingId == originalReceipt.BindingId
        && current.BatchId == originalReceipt.BatchId && current.OperationId == originalReceipt.OperationId
        && current.SourceSetSha256 == originalReceipt.SourceSetSha256 && current.DependencyManifestVersion == originalReceipt.DependencyManifestVersion
        && current.DependencyManifest is not null && current.DependencyManifest.AsSpan().SequenceEqual(originalReceipt.DependencyManifest)
        && current.SelectedMessageCount == originalReceipt.SelectedMessageCount && current.NoteCount == originalReceipt.NoteCount
        && current.Outcome == originalReceipt.Outcome && current.ServiceId == originalReceipt.ServiceId && current.ClaimEpoch == originalReceipt.ClaimEpoch
        && current.CredentialEpoch == originalReceipt.CredentialEpoch && current.GrantVersion == originalReceipt.GrantVersion
        && current.SourceVersion == originalReceipt.SourceVersion && current.DeletionGeneration == originalReceipt.DeletionGeneration
        && current.AccountVersion == originalReceipt.AccountVersion && current.CommittedAtUtc == originalReceipt.CommittedAtUtc
        && current.CommittedAtUtc.Offset == originalReceipt.CommittedAtUtc.Offset;

    internal static GroupWorkEffectLedger Require(GroupWorkCommitReceiptRecord receipt, GroupBatchClaimReceiptRecord originalClaim,
        bool historical, IReadOnlyList<GroupWorkSourceDispositionRecord> selectedRows,
        IReadOnlyList<GroupCustomerRequestRecord> requestRows, IReadOnlyList<GroupRequestRevisionRecord> revisionRows,
        IReadOnlyList<GroupRequestEvidenceRecord> evidenceRows, IReadOnlyList<GroupNotesCommittedOutboxRecord> outboxRows,
        IReadOnlyList<GroupNotesCommittedItemRecord> itemRows)
    {
        ArgumentNullException.ThrowIfNull(receipt); ArgumentNullException.ThrowIfNull(originalClaim);
        // Copy before any supplied enumerator can mutate an earlier input.
        if (receipt.DependencyManifest is null || receipt.DependencyManifest.Length > GroupWorkDependencyManifest.MaximumBytes) throw Unavailable();
        var work = CopyReceipt(receipt);
        var provenance = GroupOriginalClaimProvenance.Require(work, originalClaim);
        var manifest = GroupWorkDependencyManifest.Read(work.DependencyManifest);
        var scope = manifest.Scope;
        var selected = Freeze(selectedRows, 100, x => new GroupWorkSourceDispositionRecord
        {
            TenantId = x.TenantId,
            CompanyId = x.CompanyId,
            BindingId = x.BindingId,
            BatchId = x.BatchId,
            MessageId = x.MessageId,
            MessageRevision = x.MessageRevision,
            OperationId = x.OperationId,
            Outcome = x.Outcome
        });
        var requests = Freeze(requestRows, GroupAutomaticNotePlan.MaximumNotes, x => new GroupCustomerRequestRecord
        {
            TenantId = x.TenantId,
            CompanyId = x.CompanyId,
            BindingId = x.BindingId,
            Id = x.Id,
            OriginBatchId = x.OriginBatchId,
            OriginOperationId = x.OriginOperationId,
            OriginCandidateOrdinal = x.OriginCandidateOrdinal,
            RequestCode = x.RequestCode,
            Kind = x.Kind,
            SourceVersion = x.SourceVersion,
            DeletionGeneration = x.DeletionGeneration,
            CreatedAtUtc = x.CreatedAtUtc
        });
        var envelopeBytes = 0;
        var revisions = Freeze(revisionRows, GroupAutomaticNotePlan.MaximumNotes, CopyRevision);
        var evidence = Freeze(evidenceRows, GroupGroundedWorkProposal.MaximumEvidence + 200, x => new GroupRequestEvidenceRecord
        {
            TenantId = x.TenantId,
            CompanyId = x.CompanyId,
            BindingId = x.BindingId,
            RequestId = x.RequestId,
            RequestRevision = x.RequestRevision,
            Ordinal = x.Ordinal,
            MessageId = x.MessageId,
            MessageRevision = x.MessageRevision,
            Kind = x.Kind
        });
        var outboxes = Freeze(outboxRows, 1, x => new GroupNotesCommittedOutboxRecord
        {
            TenantId = x.TenantId,
            CompanyId = x.CompanyId,
            BindingId = x.BindingId,
            Id = x.Id,
            BatchId = x.BatchId,
            OperationId = x.OperationId,
            NoteCount = x.NoteCount,
            IsHistoricalBackfill = x.IsHistoricalBackfill,
            CommittedAtUtc = x.CommittedAtUtc
        });
        var items = Freeze(itemRows, GroupAutomaticNotePlan.MaximumNotes, x => new GroupNotesCommittedItemRecord
        {
            TenantId = x.TenantId,
            CompanyId = x.CompanyId,
            BindingId = x.BindingId,
            OutboxId = x.OutboxId,
            Ordinal = x.Ordinal,
            RequestId = x.RequestId,
            RequestRevision = x.RequestRevision
        });
        if (selected.Length != manifest.Sources.Count || selected.Select(x => x.MessageId).Distinct().Count() != selected.Length
            || selected.Any(x => !InScope(x.TenantId, x.CompanyId, x.BindingId) || x.BatchId != work.BatchId || x.OperationId != work.OperationId
                || !Enum.IsDefined(x.Outcome) || x.Outcome == GroupWorkSourceOutcome.ExtractionFailed
                || !manifest.Sources.Any(s => s.MessageId == x.MessageId && s.Revision == x.MessageRevision))) throw Unavailable();
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(string.Join("\n", selected.OrderBy(x => x.MessageId)
            .Select(x => x.MessageId.ToString("D") + "/" + x.MessageRevision.ToString(CultureInfo.InvariantCulture))))));
        if (work.SourceSetSha256 != sourceHash || requests.Length != work.NoteCount || revisions.Length != work.NoteCount
            || items.Length != work.NoteCount || requests.Select(x => x.Id).Distinct().Count() != requests.Length
            || revisions.Select(x => x.RequestId).Distinct().Count() != revisions.Length) throw Unavailable();
        var bySource = selected.ToDictionary(x => x.MessageId);
        var literalSources = new HashSet<Guid>(); var hostSources = new HashSet<Guid>();
        var aiCount = 0; var hostCount = 0; var aiEvidence = 0; var hostEvidence = 0;
        var outboxId = Identity("outbox", 0);
        if (work.NoteCount == 0)
        {
            if (evidence.Length != 0 || outboxes.Length != 0 || selected.Any(x => x.Outcome is GroupWorkSourceOutcome.Work
                or GroupWorkSourceOutcome.Quarantined or GroupWorkSourceOutcome.Attention)) throw Unavailable();
        }
        else if (outboxes.Length != 1 || !InScope(outboxes[0].TenantId, outboxes[0].CompanyId, outboxes[0].BindingId)
            || outboxes[0].Id != outboxId || outboxes[0].BatchId != work.BatchId || outboxes[0].OperationId != work.OperationId
            || outboxes[0].NoteCount != work.NoteCount || outboxes[0].IsHistoricalBackfill != historical
            || outboxes[0].CommittedAtUtc != work.CommittedAtUtc || outboxes[0].CommittedAtUtc.Offset != TimeSpan.Zero) throw Unavailable();
        var orderedRequests = requests.OrderBy(x => x.OriginCandidateOrdinal).ToArray();
        var orderedItems = items.OrderBy(x => x.Ordinal).ToArray();
        for (var index = 0; index < orderedRequests.Length; index++)
        {
            var head = orderedRequests[index]; var id = Identity("request", index + 1);
            if (!InScope(head.TenantId, head.CompanyId, head.BindingId) || head.Id != id || head.OriginBatchId != work.BatchId
                || head.OriginOperationId != work.OperationId || head.OriginCandidateOrdinal != index + 1
                || head.RequestCode != "REQ-" + id.ToString("N").ToUpperInvariant() || !Enum.IsDefined(head.Kind)
                || head.SourceVersion != work.SourceVersion || head.DeletionGeneration != work.DeletionGeneration
                || head.CreatedAtUtc != work.CommittedAtUtc || head.CreatedAtUtc.Offset != TimeSpan.Zero) throw Unavailable();
            var revision = revisions.SingleOrDefault(x => x.RequestId == id) ?? throw Unavailable();
            if (!InScope(revision.TenantId, revision.CompanyId, revision.BindingId) || revision.Revision != 1
                || revision.AuthorServiceId != work.ServiceId || revision.AuthorUserId is not null || revision.SourceBatchId != work.BatchId
                || revision.ClaimEpoch != work.ClaimEpoch || revision.SourceVersion != work.SourceVersion
                || revision.DeletionGeneration != work.DeletionGeneration || revision.CreatedAtUtc != work.CommittedAtUtc
                || revision.CreatedAtUtc.Offset != TimeSpan.Zero) throw Unavailable();
            var ai = revision.Origin == GroupRequestRevisionOrigin.AiExtracted;
            if (ai)
            {
                if (revision.VerificationLevel != GroupRequestVerificationLevel.SourceBackedAiInterpretation
                    || head.Kind is not (GroupNoteKind.Incident or GroupNoteKind.ChangeRequest or GroupNoteKind.Question or GroupNoteKind.NeedsClarification)
                    || ++aiCount > GroupGroundedWorkProposal.MaximumNotes) throw Unavailable();
            }
            else if (revision.Origin != GroupRequestRevisionOrigin.HostAttention || revision.VerificationLevel != GroupRequestVerificationLevel.HostObserved
                || head.Kind != GroupNoteKind.NeedsClarification || ++hostCount > 20) throw Unavailable();
            var refs = evidence.Where(x => x.RequestId == id).OrderBy(x => x.Ordinal).ToArray();
            if (refs.Length is < 1 or > 10 || refs.Select(x => (x.MessageId, x.MessageRevision)).Distinct().Count() != refs.Length) throw Unavailable();
            for (var ordinal = 0; ordinal < refs.Length; ordinal++)
            {
                var reference = refs[ordinal];
                if (!InScope(reference.TenantId, reference.CompanyId, reference.BindingId) || reference.RequestRevision != 1
                    || reference.Ordinal != ordinal + 1 || !bySource.TryGetValue(reference.MessageId, out var source)
                    || reference.MessageRevision != source.MessageRevision
                    || reference.Kind != (ai ? GroupRequestEvidenceKind.LiteralSourceQuote : GroupRequestEvidenceKind.HostMetadataAttention)
                    || (ai ? source.Outcome != GroupWorkSourceOutcome.Work
                        : source.Outcome is not (GroupWorkSourceOutcome.Work or GroupWorkSourceOutcome.Quarantined or GroupWorkSourceOutcome.Attention))) throw Unavailable();
                if (ai) { literalSources.Add(source.MessageId); if (++aiEvidence > GroupGroundedWorkProposal.MaximumEvidence) throw Unavailable(); }
                else { hostSources.Add(source.MessageId); if (++hostEvidence > 200) throw Unavailable(); }
            }
            var item = orderedItems[index];
            if (!InScope(item.TenantId, item.CompanyId, item.BindingId) || item.OutboxId != outboxId || item.Ordinal != index + 1
                || item.RequestId != id || item.RequestRevision != 1) throw Unavailable();
        }
        if (evidence.Any(x => !requests.Any(h => h.Id == x.RequestId))
            || selected.Any(x => x.Outcome == GroupWorkSourceOutcome.Work && !literalSources.Contains(x.MessageId)
                || x.Outcome is GroupWorkSourceOutcome.Quarantined or GroupWorkSourceOutcome.Attention && !hostSources.Contains(x.MessageId))
            || (work.Outcome == GroupWorkCommitOutcome.Notes) != (aiCount > 0)) throw Unavailable();

        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write("AIOGEFF1"u8);
        foreach (var id in new[] { scope.TenantId, scope.CompanyId, scope.SourceBindingId, work.BatchId, work.OperationId, work.ServiceId,
            provenance.ClaimOperationId, provenance.OwnerId }) writer.Write(id.ToByteArray());
        foreach (var value in new[] { work.ClaimEpoch, work.CredentialEpoch, work.GrantVersion, work.SourceVersion, work.DeletionGeneration,
            work.AccountVersion, provenance.IssuedAtUtc.UtcTicks, provenance.ExpiresAtUtc.UtcTicks, work.CommittedAtUtc.UtcTicks }) writer.Write(value);
        writer.Write(historical); writer.Write((int)work.Outcome); writer.Write(work.NoteCount); writer.Write(work.SelectedMessageCount);
        writer.Write(work.SourceSetSha256); writer.Write(work.DependencyManifestVersion);
        writer.Write(work.DependencyManifest!.Length); writer.Write(work.DependencyManifest);
        foreach (var row in selected.OrderBy(x => x.MessageId))
        { writer.Write(row.MessageId.ToByteArray()); writer.Write(row.MessageRevision); writer.Write((int)row.Outcome); }
        foreach (var head in orderedRequests)
        {
            writer.Write(head.Id.ToByteArray()); writer.Write(head.OriginCandidateOrdinal); writer.Write(head.RequestCode); writer.Write((int)head.Kind);
            var revision = revisions.Single(x => x.RequestId == head.Id);
            writer.Write((int)revision.Origin); writer.Write((int)revision.VerificationLevel); writer.Write(revision.ContentKeyId);
            writer.Write(revision.ProtectedContent.Length); writer.Write(revision.ProtectedContent); writer.Write(revision.EnvelopeSha256);
            var refs = evidence.Where(x => x.RequestId == head.Id).OrderBy(x => x.Ordinal).ToArray(); writer.Write(refs.Length);
            foreach (var reference in refs)
            { writer.Write(reference.Ordinal); writer.Write(reference.MessageId.ToByteArray()); writer.Write(reference.MessageRevision); writer.Write((int)reference.Kind); }
        }
        // All remaining original outbox/item fields have been compared with
        // this exact deterministic graph. Progress is deliberately excluded.
        writer.Write(outboxes.Length); if (outboxes.Length != 0) writer.Write(outboxId.ToByteArray());
        foreach (var item in orderedItems) { writer.Write(item.Ordinal); writer.Write(item.RequestId.ToByteArray()); writer.Write(item.RequestRevision); }
        writer.Flush();
        return new(scope, work, Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)))));

        bool InScope(Guid tenant, Guid company, Guid binding) => tenant == scope.TenantId && company == scope.CompanyId && binding == scope.SourceBindingId;
        Guid Identity(string kind, int ordinal) => new(SHA256.HashData(Encoding.ASCII.GetBytes(FormattableString.Invariant(
            $"aioffice-group-note-id-v1/{scope.TenantId:D}/{scope.CompanyId:D}/{scope.SourceBindingId:D}/{work.BatchId:D}/{work.OperationId:D}/{kind}/{ordinal}"))).AsSpan(0, 16));
        GroupRequestRevisionRecord CopyRevision(GroupRequestRevisionRecord row)
        {
            if (row.ProtectedContent is null || row.ProtectedContent.Length is < 30 or > GroupBrainContentProtector.MaximumEnvelopeLength
                || row.ProtectedContent[0] != 1 || string.IsNullOrEmpty(row.ContentKeyId) || row.ContentKeyId.Length > 64
                || row.ContentKeyId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw Unavailable();
            envelopeBytes += row.ProtectedContent.Length;
            if (envelopeBytes > GroupBrainCurrentReader.MaximumSelectedEnvelopeBytes) throw Unavailable();
            var bytes = row.ProtectedContent.ToArray();
            if (row.EnvelopeSha256 != Convert.ToHexString(SHA256.HashData(bytes))) throw Unavailable();
            return new()
            {
                TenantId = row.TenantId,
                CompanyId = row.CompanyId,
                BindingId = row.BindingId,
                RequestId = row.RequestId,
                Revision = row.Revision,
                Origin = row.Origin,
                VerificationLevel = row.VerificationLevel,
                AuthorServiceId = row.AuthorServiceId,
                AuthorUserId = row.AuthorUserId,
                SourceBatchId = row.SourceBatchId,
                ClaimEpoch = row.ClaimEpoch,
                SourceVersion = row.SourceVersion,
                DeletionGeneration = row.DeletionGeneration,
                ContentKeyId = row.ContentKeyId,
                ProtectedContent = bytes,
                EnvelopeSha256 = row.EnvelopeSha256,
                CreatedAtUtc = row.CreatedAtUtc
            };
        }
    }

    private static T[] Freeze<T>(IReadOnlyList<T> source, int maximum, Func<T, T> copy) where T : class
    {
        ArgumentNullException.ThrowIfNull(source); var result = new List<T>();
        try
        {
            foreach (var row in source)
            { if (row is null || result.Count == maximum) throw Unavailable(); result.Add(copy(row)); }
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { throw Unavailable(); }
        return result.ToArray();
    }
    private static GroupWorkCommitReceiptRecord CopyReceipt(GroupWorkCommitReceiptRecord x) => new()
    {
        TenantId = x.TenantId,
        CompanyId = x.CompanyId,
        BindingId = x.BindingId,
        BatchId = x.BatchId,
        OperationId = x.OperationId,
        SourceSetSha256 = x.SourceSetSha256,
        DependencyManifestVersion = x.DependencyManifestVersion,
        DependencyManifest = x.DependencyManifest!.ToArray(),
        SelectedMessageCount = x.SelectedMessageCount,
        NoteCount = x.NoteCount,
        Outcome = x.Outcome,
        ServiceId = x.ServiceId,
        ClaimEpoch = x.ClaimEpoch,
        CredentialEpoch = x.CredentialEpoch,
        GrantVersion = x.GrantVersion,
        SourceVersion = x.SourceVersion,
        DeletionGeneration = x.DeletionGeneration,
        AccountVersion = x.AccountVersion,
        CommittedAtUtc = x.CommittedAtUtc
    };
    private static InvalidOperationException Unavailable() => new("Group original effect ledger is not available.");
}
