using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupWorkEffectLedgerTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(20, 20)]
    public void ExpectedEffectDigestStagesOnceAndComparesCompleteOriginalGraph(int ai, int host)
    {
        var f = new Fixture(ai, host); var original = f.Require();
        Assert.False(GroupWorkEffectDigest.HasExpectation(f.Work));
        GroupWorkEffectDigest.Stage(f.Work, original);
        Assert.Equal(1, f.Work.EffectLedgerVersion);
        Assert.Equal(Convert.FromHexString(original.Fingerprint), f.Work.ExpectedEffectSha256);
        Assert.True(GroupWorkEffectDigest.HasExpectation(f.Work));
        GroupWorkEffectDigest.RequireUnchanged(f.Work, f.Require());
        var stored = f.Work.ExpectedEffectSha256;
        var error = Assert.Throws<InvalidOperationException>(() => GroupWorkEffectDigest.Stage(f.Work, original));
        Assert.Equal("Group original effect expectation is not available.", error.Message); Assert.Null(error.InnerException);
        Assert.Same(stored, f.Work.ExpectedEffectSha256); Assert.Equal(1, f.Work.EffectLedgerVersion);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("binding")]
    [InlineData("batch")]
    [InlineData("operation")]
    [InlineData("source-hash")]
    [InlineData("manifest-version")]
    [InlineData("manifest-null")]
    [InlineData("manifest-bytes")]
    [InlineData("selected-count")]
    [InlineData("note-count")]
    [InlineData("outcome")]
    [InlineData("service")]
    [InlineData("claim")]
    [InlineData("credential")]
    [InlineData("grant")]
    [InlineData("source")]
    [InlineData("deletion")]
    [InlineData("account")]
    [InlineData("committed-time")]
    [InlineData("committed-offset")]
    public void DigestNeverBindsASealedGraphToAChangedReceiptOrMutatesOnRefusal(string fault)
    {
        var f = new Fixture(1, 1); var original = f.Require();
        switch (fault)
        {
            case "tenant": f.Work.TenantId = Guid.NewGuid(); break;
            case "company": f.Work.CompanyId = Guid.NewGuid(); break;
            case "binding": f.Work.BindingId = Guid.NewGuid(); break;
            case "batch": f.Work.BatchId = Guid.NewGuid(); break;
            case "operation": f.Work.OperationId = Guid.NewGuid(); break;
            case "source-hash": f.Work.SourceSetSha256 = new('C', 64); break;
            case "manifest-version": f.Work.DependencyManifestVersion = 0; break;
            case "manifest-null": f.Work.DependencyManifest = null; break;
            case "manifest-bytes": f.Work.DependencyManifest![^1] ^= 1; break;
            case "selected-count": f.Work.SelectedMessageCount++; break;
            case "note-count": f.Work.NoteCount++; break;
            case "outcome": f.Work.Outcome = GroupWorkCommitOutcome.Attention; break;
            case "service": f.Work.ServiceId = Guid.NewGuid(); break;
            case "claim": f.Work.ClaimEpoch++; break;
            case "credential": f.Work.CredentialEpoch++; break;
            case "grant": f.Work.GrantVersion++; break;
            case "source": f.Work.SourceVersion++; break;
            case "deletion": f.Work.DeletionGeneration++; break;
            case "account": f.Work.AccountVersion++; break;
            case "committed-time": f.Work.CommittedAtUtc = f.Work.CommittedAtUtc.AddTicks(1); break;
            case "committed-offset": f.Work.CommittedAtUtc = f.Work.CommittedAtUtc.ToOffset(TimeSpan.FromHours(7)); break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        Assert.False(original.MatchesReceipt(f.Work));
        var error = Assert.Throws<InvalidOperationException>(() => GroupWorkEffectDigest.Stage(f.Work, original));
        Assert.Equal("Group original effect expectation is not available.", error.Message); Assert.Null(error.InnerException);
        Assert.Equal(0, f.Work.EffectLedgerVersion); Assert.Null(f.Work.ExpectedEffectSha256);
        f.Work.EffectLedgerVersion = 1; f.Work.ExpectedEffectSha256 = Convert.FromHexString(original.Fingerprint);
        Assert.Throws<InvalidOperationException>(() => GroupWorkEffectDigest.RequireUnchanged(f.Work, original));
    }

    [Theory]
    [InlineData(-1, -1, 1)]
    [InlineData(2, 32, 1)]
    [InlineData(0, 0, 1)]
    [InlineData(0, 32, 1)]
    [InlineData(1, -1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 31, 1)]
    [InlineData(1, 33, 1)]
    [InlineData(1, 32, 0)]
    [InlineData(1, 32, 2)]
    public void MalformedVersionLengthOrLegacyDependencyRefusesWithFixedPrivateDiagnostics(int version, int length, int dependency)
    {
        var f = new Fixture(1, 0); var graph = f.Require();
        f.Work.EffectLedgerVersion = version; f.Work.ExpectedEffectSha256 = length < 0 ? null : new byte[length];
        f.Work.DependencyManifestVersion = dependency;
        var stored = f.Work.ExpectedEffectSha256;
        foreach (Action call in new Action[] { () => GroupWorkEffectDigest.HasExpectation(f.Work),
            () => GroupWorkEffectDigest.Stage(f.Work, graph), () => GroupWorkEffectDigest.RequireUnchanged(f.Work, graph) })
        {
            var error = Assert.Throws<InvalidOperationException>(call);
            Assert.Equal("Group original effect expectation is not available.", error.Message); Assert.Null(error.InnerException);
        }
        Assert.Same(stored, f.Work.ExpectedEffectSha256); Assert.Equal(version, f.Work.EffectLedgerVersion);
    }

    [Fact]
    public void StoredExpectationDetectsResignedCipherKeyAndDigestChangesButPermitsLaterItAndPublishProgress()
    {
        var f = new Fixture(1, 1); GroupWorkEffectDigest.Stage(f.Work, f.Require());
        f.Requests[0].CurrentRevision = 9; f.Requests[0].BusinessVersion = 11;
        f.Requests[0].BusinessStatus = GroupNoteBusinessStatus.Resolved;
        f.Outboxes[0].PublishedAtUtc = f.Work.CommittedAtUtc.AddMinutes(1); f.Outboxes[0].PublishAttempts = 3;
        f.Selected.Reverse(); f.Revisions.Reverse(); f.Requests.Reverse(); f.Evidence.Reverse(); f.Items.Reverse();
        GroupWorkEffectDigest.RequireUnchanged(f.Work, f.Require());
        var revision = f.Revisions[0]; revision.ContentKeyId = "replacement-key";
        Assert.Throws<InvalidOperationException>(() => GroupWorkEffectDigest.RequireUnchanged(f.Work, f.Require()));
        revision.ContentKeyId = "owned-key"; revision.ProtectedContent[10] ^= 1;
        revision.EnvelopeSha256 = Convert.ToHexString(SHA256.HashData(revision.ProtectedContent));
        Assert.Throws<InvalidOperationException>(() => GroupWorkEffectDigest.RequireUnchanged(f.Work, f.Require()));
        revision.ProtectedContent[10] ^= 1; revision.EnvelopeSha256 = Convert.ToHexString(SHA256.HashData(revision.ProtectedContent));
        GroupWorkEffectDigest.RequireUnchanged(f.Work, f.Require());
        f.Work.ExpectedEffectSha256![0] ^= 1;
        Assert.Throws<InvalidOperationException>(() => GroupWorkEffectDigest.RequireUnchanged(f.Work, f.Require()));
    }

    [Fact]
    public void LegacyMissingExpectationCannotQualifyEffectsAndSealedGraphDoesNotBorrowReceiptBuffers()
    {
        var f = new Fixture(0, 0); var graph = f.Require();
        Assert.Throws<InvalidOperationException>(() => GroupWorkEffectDigest.RequireUnchanged(f.Work, graph));
        var manifest = f.Work.DependencyManifest!.ToArray(); f.Work.DependencyManifest![^1] ^= 1;
        Assert.False(graph.MatchesReceipt(f.Work)); f.Work.DependencyManifest = manifest;
        Assert.True(graph.MatchesReceipt(f.Work)); GroupWorkEffectDigest.Stage(f.Work, graph);
        Assert.True(graph.MatchesReceipt(f.Work)); GroupWorkEffectDigest.RequireUnchanged(f.Work, graph);
        f.Work.DependencyManifestVersion = 0; f.Work.DependencyManifest = null;
        f.Work.EffectLedgerVersion = 0; f.Work.ExpectedEffectSha256 = null;
        Assert.False(GroupWorkEffectDigest.HasExpectation(f.Work));
        Assert.Throws<InvalidOperationException>(() => GroupWorkEffectDigest.RequireUnchanged(f.Work, graph));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(20, 20)]
    public void CompleteOriginalCreateGraphsAndEmptyNoWorkHavePrivateStableFingerprints(int ai, int host)
    {
        var f = new Fixture(ai, host); var result = f.Require();
        Assert.Equal(f.Scope, result.Scope); Assert.Equal(f.Work.BatchId, result.BatchId); Assert.Equal(f.Work.OperationId, result.OperationId);
        Assert.Equal(ai + host, result.NoteCount); Assert.Matches("^[0-9A-F]{64}$", result.Fingerprint);
        f.Selected.Reverse(); f.Requests.Reverse(); f.Revisions.Reverse(); f.Evidence.Reverse(); f.Items.Reverse();
        Assert.Equal(result.Fingerprint, f.Require().Fingerprint);
        Assert.Equal("Group original effect ledger (private metadata).", result.ToString());
    }

    [Fact]
    public void OriginalFingerprintExcludesLaterItHeadAndPublishProgressButIncludesResignedCipherAndKeyId()
    {
        var f = new Fixture(1, 1); var original = f.Require().Fingerprint;
        var head = f.Requests[0]; head.CurrentRevision = 8; head.BusinessVersion = 10; head.BusinessStatus = GroupNoteBusinessStatus.Resolved;
        head.AssignedToUserId = head.ConfirmedByUserId = Guid.NewGuid(); head.ConfirmedAtUtc = head.UpdatedAtUtc = head.CreatedAtUtc.AddDays(1);
        head.CommittedDueAtUtc = head.CreatedAtUtc.AddDays(2);
        var outbox = f.Outboxes[0]; outbox.AvailableAtUtc = outbox.CommittedAtUtc.AddDays(1); outbox.PublishedAtUtc = outbox.AvailableAtUtc; outbox.PublishAttempts = 9;
        Assert.Equal(original, f.Require().Fingerprint);
        f.Revisions[0].ContentKeyId = "rotated-original-key";
        Assert.NotEqual(original, f.Require().Fingerprint);
        f.Revisions[0].ContentKeyId = "owned-key"; f.Revisions[0].ProtectedContent[10] ^= 1;
        f.Revisions[0].EnvelopeSha256 = Convert.ToHexString(SHA256.HashData(f.Revisions[0].ProtectedContent));
        Assert.NotEqual(original, f.Require().Fingerprint);
        // This is a structural fingerprint, not storage or authorization.
    }

    [Theory]
    [InlineData("source-count")]
    [InlineData("source-duplicate")]
    [InlineData("source-tenant")]
    [InlineData("source-company")]
    [InlineData("source-binding")]
    [InlineData("source-batch")]
    [InlineData("source-operation")]
    [InlineData("source-revision")]
    [InlineData("source-hash")]
    [InlineData("source-outcome")]
    [InlineData("unproved-extraction-failed")]
    [InlineData("head-count")]
    [InlineData("head-duplicate")]
    [InlineData("head-tenant")]
    [InlineData("head-company")]
    [InlineData("head-binding")]
    [InlineData("head-id")]
    [InlineData("head-batch")]
    [InlineData("head-operation")]
    [InlineData("head-ordinal")]
    [InlineData("head-code")]
    [InlineData("head-kind")]
    [InlineData("head-source")]
    [InlineData("head-deletion")]
    [InlineData("head-time")]
    [InlineData("head-offset")]
    [InlineData("revision-count")]
    [InlineData("revision-duplicate")]
    [InlineData("revision-scope")]
    [InlineData("revision-number")]
    [InlineData("revision-service")]
    [InlineData("revision-user")]
    [InlineData("revision-batch")]
    [InlineData("revision-claim")]
    [InlineData("revision-source")]
    [InlineData("revision-deletion")]
    [InlineData("revision-time")]
    [InlineData("revision-origin")]
    [InlineData("revision-verification")]
    [InlineData("host-kind")]
    [InlineData("cipher-null")]
    [InlineData("cipher-short")]
    [InlineData("cipher-large")]
    [InlineData("cipher-header")]
    [InlineData("cipher-digest")]
    [InlineData("cipher-lower-digest")]
    [InlineData("key-id")]
    [InlineData("evidence-count")]
    [InlineData("evidence-duplicate")]
    [InlineData("evidence-orphan")]
    [InlineData("evidence-scope")]
    [InlineData("evidence-request-revision")]
    [InlineData("evidence-ordinal")]
    [InlineData("evidence-message")]
    [InlineData("evidence-source-revision")]
    [InlineData("evidence-origin")]
    [InlineData("evidence-no-work")]
    [InlineData("outbox-count")]
    [InlineData("outbox-duplicate")]
    [InlineData("outbox-scope")]
    [InlineData("outbox-id")]
    [InlineData("outbox-batch")]
    [InlineData("outbox-operation")]
    [InlineData("outbox-notes")]
    [InlineData("outbox-history")]
    [InlineData("outbox-time")]
    [InlineData("item-count")]
    [InlineData("item-scope")]
    [InlineData("item-outbox")]
    [InlineData("item-ordinal")]
    [InlineData("item-request")]
    [InlineData("item-revision")]
    public void MissingForeignCorruptOrUnlinkedOriginalEffectsRefuseWithoutPrivateDiagnostics(string fault)
    {
        var f = new Fixture(1, 1); var s = f.Selected[0]; var h = f.Requests[0]; var r = f.Revisions[0];
        var e = f.Evidence[0]; var o = f.Outboxes[0]; var i = f.Items[0];
        switch (fault)
        {
            case "source-count": f.Selected.RemoveAt(0); break;
            case "source-duplicate": f.Selected[1] = s; break;
            case "source-tenant": s.TenantId = Guid.NewGuid(); break;
            case "source-company": s.CompanyId = Guid.NewGuid(); break;
            case "source-binding": s.BindingId = Guid.NewGuid(); break;
            case "source-batch": s.BatchId = Guid.NewGuid(); break;
            case "source-operation": s.OperationId = Guid.NewGuid(); break;
            case "source-revision": s.MessageRevision++; break;
            case "source-hash": f.Work.SourceSetSha256 = new('B', 64); break;
            case "source-outcome": s.Outcome = (GroupWorkSourceOutcome)99; break;
            case "unproved-extraction-failed": s.Outcome = GroupWorkSourceOutcome.ExtractionFailed; break;
            case "head-count": f.Requests.RemoveAt(0); break;
            case "head-duplicate": f.Requests[1] = h; break;
            case "head-tenant": h.TenantId = Guid.NewGuid(); break;
            case "head-company": h.CompanyId = Guid.NewGuid(); break;
            case "head-binding": h.BindingId = Guid.NewGuid(); break;
            case "head-id": h.Id = Guid.NewGuid(); break;
            case "head-batch": h.OriginBatchId = Guid.NewGuid(); break;
            case "head-operation": h.OriginOperationId = Guid.NewGuid(); break;
            case "head-ordinal": h.OriginCandidateOrdinal = 2; break;
            case "head-code": h.RequestCode = "PRIVATE_OWNED_CODE"; break;
            case "head-kind": h.Kind = (GroupNoteKind)99; break;
            case "head-source": h.SourceVersion++; break;
            case "head-deletion": h.DeletionGeneration++; break;
            case "head-time": h.CreatedAtUtc = h.CreatedAtUtc.AddTicks(1); break;
            case "head-offset": h.CreatedAtUtc = h.CreatedAtUtc.ToOffset(TimeSpan.FromHours(7)); break;
            case "revision-count": f.Revisions.RemoveAt(0); break;
            case "revision-duplicate": f.Revisions[1] = r; break;
            case "revision-scope": r.BindingId = Guid.NewGuid(); break;
            case "revision-number": r.Revision = 2; break;
            case "revision-service": r.AuthorServiceId = Guid.NewGuid(); break;
            case "revision-user": r.AuthorUserId = Guid.NewGuid(); break;
            case "revision-batch": r.SourceBatchId = Guid.NewGuid(); break;
            case "revision-claim": r.ClaimEpoch++; break;
            case "revision-source": r.SourceVersion++; break;
            case "revision-deletion": r.DeletionGeneration++; break;
            case "revision-time": r.CreatedAtUtc = r.CreatedAtUtc.AddTicks(1); break;
            case "revision-origin": r.Origin = GroupRequestRevisionOrigin.ItEdited; break;
            case "revision-verification": r.VerificationLevel = GroupRequestVerificationLevel.ItConfirmed; break;
            case "host-kind": f.Requests[1].Kind = GroupNoteKind.Incident; break;
            case "cipher-null": r.ProtectedContent = null!; break;
            case "cipher-short": r.ProtectedContent = new byte[29]; break;
            case "cipher-large": r.ProtectedContent = new byte[GroupBrainContentProtector.MaximumEnvelopeLength + 1]; break;
            case "cipher-header": r.ProtectedContent[0] = 2; break;
            case "cipher-digest": r.ProtectedContent[10] ^= 1; break;
            case "cipher-lower-digest": r.EnvelopeSha256 = r.EnvelopeSha256.ToLowerInvariant(); break;
            case "key-id": r.ContentKeyId = "PRIVATE_OWNED_KEY bad"; break;
            case "evidence-count": f.Evidence.RemoveAt(0); break;
            case "evidence-duplicate": f.Evidence.Add(e); break;
            case "evidence-orphan": e.RequestId = Guid.NewGuid(); break;
            case "evidence-scope": e.TenantId = Guid.NewGuid(); break;
            case "evidence-request-revision": e.RequestRevision = 2; break;
            case "evidence-ordinal": e.Ordinal = 2; break;
            case "evidence-message": e.MessageId = Guid.NewGuid(); break;
            case "evidence-source-revision": e.MessageRevision++; break;
            case "evidence-origin": e.Kind = GroupRequestEvidenceKind.HostMetadataAttention; break;
            case "evidence-no-work": s.Outcome = GroupWorkSourceOutcome.NoWork; break;
            case "outbox-count": f.Outboxes.Clear(); break;
            case "outbox-duplicate": f.Outboxes.Add(o); break;
            case "outbox-scope": o.CompanyId = Guid.NewGuid(); break;
            case "outbox-id": o.Id = Guid.NewGuid(); break;
            case "outbox-batch": o.BatchId = Guid.NewGuid(); break;
            case "outbox-operation": o.OperationId = Guid.NewGuid(); break;
            case "outbox-notes": o.NoteCount++; break;
            case "outbox-history": o.IsHistoricalBackfill = true; break;
            case "outbox-time": o.CommittedAtUtc = o.CommittedAtUtc.AddTicks(1); break;
            case "item-count": f.Items.RemoveAt(0); break;
            case "item-scope": i.CompanyId = Guid.NewGuid(); break;
            case "item-outbox": i.OutboxId = Guid.NewGuid(); break;
            case "item-ordinal": i.Ordinal = 2; break;
            case "item-request": i.RequestId = Guid.NewGuid(); break;
            case "item-revision": i.RequestRevision = 2; break;
            default: throw new InvalidOperationException();
        }
        var error = Assert.Throws<InvalidOperationException>(() => f.Require());
        Assert.Null(error.InnerException); Assert.DoesNotContain("PRIVATE_OWNED", error.ToString());
    }

    [Fact]
    public void TotalCipherByteLimitIsInclusiveAndNoWorkCannotHideAnyGraphRow()
    {
        var f = new Fixture(4, 0);
        foreach (var revision in f.Revisions)
        { revision.ProtectedContent = new byte[64000]; revision.ProtectedContent[0] = 1; revision.EnvelopeSha256 = Convert.ToHexString(SHA256.HashData(revision.ProtectedContent)); }
        Assert.Equal(4, f.Require().NoteCount);
        f.Revisions[0].ProtectedContent = new byte[64001]; f.Revisions[0].ProtectedContent[0] = 1;
        f.Revisions[0].EnvelopeSha256 = Convert.ToHexString(SHA256.HashData(f.Revisions[0].ProtectedContent));
        Assert.Throws<InvalidOperationException>(() => f.Require());
        var empty = new Fixture(0, 0); empty.Evidence.Add(new());
        Assert.Throws<InvalidOperationException>(() => empty.Require());
    }

    [Fact]
    public void SuppliedCountIndexerAndCopyToAreNotEvidenceAndEachSnapshotPrecedesLaterMutation()
    {
        var f = new Fixture(1, 1); var expected = f.Require().Fingerprint;
        var selected = new UntrustedList<GroupWorkSourceDispositionRecord>(f.Selected, () =>
        { f.Work.OperationId = Guid.NewGuid(); f.Work.DependencyManifest![0] ^= 1; f.Claim.OwnerId = Guid.NewGuid(); });
        var revisions = new UntrustedList<GroupRequestRevisionRecord>(f.Revisions);
        var evidence = new UntrustedList<GroupRequestEvidenceRecord>(f.Evidence, () =>
        { f.Revisions[0].ProtectedContent[10] ^= 1; f.Revisions[0].ContentKeyId = "later-value"; });
        var actual = GroupWorkEffectLedger.Require(f.Work, f.Claim, false, selected,
            new UntrustedList<GroupCustomerRequestRecord>(f.Requests), revisions, evidence,
            new UntrustedList<GroupNotesCommittedOutboxRecord>(f.Outboxes), new UntrustedList<GroupNotesCommittedItemRecord>(f.Items));
        Assert.Equal(expected, actual.Fingerprint);
        Assert.All(new[] { selected.Disposed, revisions.Disposed, evidence.Disposed }, Assert.True);
    }

    [Fact]
    public void ActualOverflowStopsAtFirstExtraRowDisposesEnumeratorAndDoesNotRelayEnumeratorDiagnostic()
    {
        var f = new Fixture(20, 20); var heads = new UntrustedList<GroupCustomerRequestRecord>(f.Requests.Concat([f.Requests[0]]).ToArray());
        Assert.Throws<InvalidOperationException>(() => GroupWorkEffectLedger.Require(f.Work, f.Claim, false, f.Selected, heads,
            f.Revisions, f.Evidence, f.Outboxes, f.Items));
        Assert.Equal(41, heads.Observed); Assert.True(heads.Disposed);
        var secret = new UntrustedList<GroupCustomerRequestRecord>(f.Requests, () => throw new InvalidOperationException("PRIVATE_OWNED_ENUMERATOR"));
        var error = Assert.Throws<InvalidOperationException>(() => GroupWorkEffectLedger.Require(f.Work, f.Claim, false, f.Selected, secret,
            f.Revisions, f.Evidence, f.Outboxes, f.Items));
        Assert.Null(error.InnerException); Assert.DoesNotContain("PRIVATE_OWNED", error.ToString()); Assert.True(secret.Disposed);
    }

    private sealed class UntrustedList<T>(IEnumerable<T> rows, Action? afterFirst = null) : IReadOnlyList<T>
    {
        public int Count => throw new InvalidOperationException("Count must not be trusted.");
        public T this[int index] => throw new InvalidOperationException("Indexer must not be trusted.");
        internal bool Disposed; internal int Observed;
        public IEnumerator<T> GetEnumerator()
        {
            try { foreach (var row in rows) { Observed++; yield return row; if (Observed == 1) afterFirst?.Invoke(); } }
            finally { Disposed = true; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Fixture
    {
        internal GroupScope Scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        internal GroupWorkCommitReceiptRecord Work;
        internal GroupBatchClaimReceiptRecord Claim;
        internal List<GroupWorkSourceDispositionRecord> Selected = [];
        internal List<GroupCustomerRequestRecord> Requests = [];
        internal List<GroupRequestRevisionRecord> Revisions = [];
        internal List<GroupRequestEvidenceRecord> Evidence = [];
        internal List<GroupNotesCommittedOutboxRecord> Outboxes = [];
        internal List<GroupNotesCommittedItemRecord> Items = [];
        internal Fixture(int ai, int host)
        {
            var batch = Guid.NewGuid(); var operation = Guid.NewGuid(); var service = Guid.NewGuid();
            var now = DateTimeOffset.Parse("2026-10-10T00:00:00Z", CultureInfo.InvariantCulture);
            Work = new()
            {
                TenantId = Scope.TenantId,
                CompanyId = Scope.CompanyId,
                BindingId = Scope.SourceBindingId,
                BatchId = batch,
                OperationId = operation,
                SelectedMessageCount = 2,
                NoteCount = ai + host,
                Outcome = ai > 0 ? GroupWorkCommitOutcome.Notes : host > 0 ? GroupWorkCommitOutcome.Attention : GroupWorkCommitOutcome.NoWork,
                ServiceId = service,
                ClaimEpoch = 2,
                CredentialEpoch = 1,
                GrantVersion = 1,
                SourceVersion = 1,
                AccountVersion = 1,
                CommittedAtUtc = now.AddSeconds(1),
                DependencyManifestVersion = 1
            };
            Claim = new()
            {
                TenantId = Scope.TenantId,
                CompanyId = Scope.CompanyId,
                BindingId = Scope.SourceBindingId,
                BatchId = batch,
                OperationId = Guid.NewGuid(),
                OwnerId = Guid.NewGuid(),
                Epoch = 2,
                ServiceId = service,
                CredentialEpoch = 1,
                GrantVersion = 1,
                SourceVersion = 1,
                AccountVersion = 1,
                AuthoritySha256 = new('A', 64),
                RequestedLifetimeTicks = TimeSpan.FromMinutes(2).Ticks,
                IssuedAtUtc = now,
                ExpiresAtUtc = now.AddMinutes(2)
            };
            var sourceIds = new[] { Guid.NewGuid(), Guid.NewGuid() }.Order().ToArray();
            for (var index = 0; index < 2; index++) Selected.Add(new()
            {
                TenantId = Scope.TenantId,
                CompanyId = Scope.CompanyId,
                BindingId = Scope.SourceBindingId,
                BatchId = batch,
                OperationId = operation,
                MessageId = sourceIds[index],
                MessageRevision = 1,
                Outcome = index == 0 && ai > 0 ? GroupWorkSourceOutcome.Work : (index == (ai > 0 ? 1 : 0) && host > 0 ? GroupWorkSourceOutcome.Quarantined : GroupWorkSourceOutcome.NoWork)
            });
            Work.SourceSetSha256 = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(string.Join("\n", Selected.Select(x => x.MessageId.ToString("D") + "/1")))));
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write("AIOGDEP1"u8);
            foreach (var id in new[] { Scope.TenantId, Scope.CompanyId, Scope.SourceBindingId, batch, operation }) writer.Write(id.ToByteArray());
            writer.Write(2L); writer.Write(Enumerable.Repeat((byte)0xAA, 32).ToArray()); writer.Write(Enumerable.Repeat((byte)0xBB, 32).ToArray());
            writer.Write((byte)2); writer.Write((byte)0);
            foreach (var id in sourceIds) { writer.Write(id.ToByteArray()); writer.Write(1L); writer.Write(new byte[32]); }
            writer.Flush(); Work.DependencyManifest = stream.ToArray();
            var outbox = Identity("outbox", 0);
            for (var index = 0; index < ai + host; index++)
            {
                var id = Identity("request", index + 1); var literal = index < ai;
                var bytes = Enumerable.Repeat((byte)(index + 1), 30).ToArray(); bytes[0] = 1;
                Requests.Add(new()
                {
                    TenantId = Scope.TenantId,
                    CompanyId = Scope.CompanyId,
                    BindingId = Scope.SourceBindingId,
                    Id = id,
                    OriginBatchId = batch,
                    OriginOperationId = operation,
                    OriginCandidateOrdinal = index + 1,
                    RequestCode = "REQ-" + id.ToString("N").ToUpperInvariant(),
                    Kind = literal ? GroupNoteKind.Incident : GroupNoteKind.NeedsClarification,
                    SourceVersion = 1,
                    CreatedAtUtc = Work.CommittedAtUtc,
                    UpdatedAtUtc = Work.CommittedAtUtc
                });
                Revisions.Add(new()
                {
                    TenantId = Scope.TenantId,
                    CompanyId = Scope.CompanyId,
                    BindingId = Scope.SourceBindingId,
                    RequestId = id,
                    Revision = 1,
                    Origin = literal ? GroupRequestRevisionOrigin.AiExtracted : GroupRequestRevisionOrigin.HostAttention,
                    VerificationLevel = literal ? GroupRequestVerificationLevel.SourceBackedAiInterpretation : GroupRequestVerificationLevel.HostObserved,
                    AuthorServiceId = service,
                    SourceBatchId = batch,
                    ClaimEpoch = 2,
                    SourceVersion = 1,
                    ContentKeyId = "owned-key",
                    ProtectedContent = bytes,
                    EnvelopeSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                    CreatedAtUtc = Work.CommittedAtUtc
                });
                Evidence.Add(new()
                {
                    TenantId = Scope.TenantId,
                    CompanyId = Scope.CompanyId,
                    BindingId = Scope.SourceBindingId,
                    RequestId = id,
                    RequestRevision = 1,
                    Ordinal = 1,
                    MessageId = literal ? sourceIds[0] : sourceIds[ai > 0 ? 1 : 0],
                    MessageRevision = 1,
                    Kind = literal ? GroupRequestEvidenceKind.LiteralSourceQuote : GroupRequestEvidenceKind.HostMetadataAttention
                });
                Items.Add(new() { TenantId = Scope.TenantId, CompanyId = Scope.CompanyId, BindingId = Scope.SourceBindingId, OutboxId = outbox, Ordinal = index + 1, RequestId = id, RequestRevision = 1 });
            }
            if (ai + host > 0) Outboxes.Add(new()
            {
                TenantId = Scope.TenantId,
                CompanyId = Scope.CompanyId,
                BindingId = Scope.SourceBindingId,
                Id = outbox,
                BatchId = batch,
                OperationId = operation,
                NoteCount = ai + host,
                CommittedAtUtc = Work.CommittedAtUtc,
                AvailableAtUtc = Work.CommittedAtUtc
            });
        }
        internal GroupWorkEffectLedger Require() => GroupWorkEffectLedger.Require(Work, Claim, false, Selected, Requests, Revisions, Evidence, Outboxes, Items);
        private Guid Identity(string kind, int ordinal) => new(SHA256.HashData(Encoding.ASCII.GetBytes(FormattableString.Invariant(
            $"aioffice-group-note-id-v1/{Scope.TenantId:D}/{Scope.CompanyId:D}/{Scope.SourceBindingId:D}/{Work.BatchId:D}/{Work.OperationId:D}/{kind}/{ordinal}"))).AsSpan(0, 16));
    }
}
