using System.Collections;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupBatchOwnInputTests
{
    [Fact]
    public void EqualTimeFiveContributorChainUsesAllOriginalEffectsAndHasDeterministicOrder()
    {
        var f = new Fixture(); for (var index = 1; index < 5; index++) f.Dependencies[index].Add(f.Ids[index - 1]);
        var current = f.Build(); var plan = GroupBatchOwnInputPlan.Require(current, f.Origins);
        Assert.Equal(f.Input.Receipts.Select(x => x.OperationId), plan.ContributorOrder);
        Assert.Equal(4, plan.OwnDependencyCount); Assert.Equal(5, current.Coverage.NoteCount);
        var reversed = current with { OriginalEffects = current.OriginalEffects.Reverse().ToArray() };
        Assert.Equal(plan.ContributorOrder, GroupBatchOwnInputPlan.Require(reversed, f.Origins.AsEnumerable().Reverse()).ContributorOrder);
        Assert.All(current.OriginalEffects, effect => Assert.Equal(current.OriginalEffects[0].CommittedAtUtc, effect.CommittedAtUtc));
    }

    [Fact]
    public void ExternalRequestOriginsAndGlossaryDoNotCreateSameBatchEdgesOrRaiseBrainBounds()
    {
        var f = new Fixture(); var batch = Guid.NewGuid(); var operation = Guid.NewGuid();
        var external = f.ExternalIdentity(batch, operation); f.Dependencies[2].Add(external);
        f.Origins.Add(new(f.Input.Allocation.Scope, external, batch, operation, 1, f.Input.Allocation.AllocatedAtUtc.AddTicks(-1)));
        f.Glossary[4] = Guid.NewGuid(); var current = f.Build(); var plan = GroupBatchOwnInputPlan.Require(current, f.Origins);
        Assert.Equal(0, plan.OwnDependencyCount); Assert.Equal(5, plan.ContributorOrder.Count);
        Assert.Equal(20, GroupBrainCurrentReader.MaximumSelectedRevisions); Assert.Equal(256000, GroupBrainCurrentReader.MaximumSelectedEnvelopeBytes);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("two-cycle")]
    [InlineData("three-cycle")]
    [InlineData("unknown-own")]
    [InlineData("foreign-scope")]
    [InlineData("wrong-operation")]
    [InlineData("wrong-ordinal")]
    [InlineData("own-as-external")]
    [InlineData("future")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("empty-batch")]
    [InlineData("empty-operation")]
    [InlineData("invalid-ordinal")]
    [InlineData("offset")]
    public void InvalidOriginsOrOwnInputCyclesFailWithFixedPrivateDiagnostics(string fault)
    {
        var f = new Fixture(); f.Dependencies[1].Add(f.Ids[0]); var scope = f.Input.Allocation.Scope;
        switch (fault)
        {
            case "self": f.Dependencies[1].Clear(); f.Dependencies[1].Add(f.Ids[1]); break;
            case "two-cycle": f.Dependencies[0].Add(f.Ids[1]); break;
            case "three-cycle": f.Dependencies[2].Add(f.Ids[1]); f.Dependencies[0].Add(f.Ids[2]); break;
            case "unknown-own":
                var operation = Guid.NewGuid(); var unknown = f.ExternalIdentity(f.Input.Allocation.BatchId, operation);
                f.Dependencies[1].Clear(); f.Dependencies[1].Add(unknown);
                f.Origins.Add(new(scope, unknown, f.Input.Allocation.BatchId, operation, 1, f.Input.Allocation.AllocatedAtUtc)); break;
        }
        var current = f.Build(); var origins = f.Origins.ToList(); var original = origins[0];
        switch (fault)
        {
            case "foreign-scope": origins[0] = original with { Scope = scope with { CompanyId = Guid.NewGuid() } }; break;
            case "wrong-operation": origins[0] = original with { OriginOperationId = Guid.NewGuid() }; break;
            case "wrong-ordinal": origins[0] = original with { OriginCandidateOrdinal = 2 }; break;
            case "own-as-external": origins[0] = original with { OriginBatchId = Guid.NewGuid() }; break;
            case "future": origins[0] = original with { CreatedAtUtc = original.CreatedAtUtc.AddTicks(1) }; break;
            case "missing": origins.Clear(); break;
            case "duplicate": origins.Add(original); break;
            case "extra": origins.Add(original with { RequestId = Guid.NewGuid() }); break;
            case "empty-batch": origins[0] = original with { OriginBatchId = Guid.Empty }; break;
            case "empty-operation": origins[0] = original with { OriginOperationId = Guid.Empty }; break;
            case "invalid-ordinal": origins[0] = original with { OriginCandidateOrdinal = 41 }; break;
            case "offset": origins[0] = original with { CreatedAtUtc = original.CreatedAtUtc.ToOffset(TimeSpan.FromHours(7)) }; break;
        }
        var error = Assert.Throws<InvalidOperationException>(() => GroupBatchOwnInputPlan.Require(current, origins));
        Assert.Equal("Group own-input dependencies are not available.", error.Message); Assert.Null(error.InnerException);
    }

    [Fact]
    public void ExternalOriginCannotSubstituteBatchOperationOrOrdinalForAnExistingRequestId()
    {
        var f = new Fixture(); var batch = Guid.NewGuid(); var operation = Guid.NewGuid(); var id = f.ExternalIdentity(batch, operation);
        f.Dependencies[1].Add(id);
        f.Origins.Add(new(f.Input.Allocation.Scope, id, batch, operation, 1, f.Input.Allocation.AllocatedAtUtc.AddTicks(-1)));
        var current = f.Build(); Assert.Equal(0, GroupBatchOwnInputPlan.Require(current, f.Origins).OwnDependencyCount);
        foreach (var changed in new[] { f.Origins[0] with { OriginBatchId = Guid.NewGuid() }, f.Origins[0] with { OriginOperationId = Guid.NewGuid() },
            f.Origins[0] with { OriginCandidateOrdinal = 2 } })
            Assert.Throws<InvalidOperationException>(() => GroupBatchOwnInputPlan.Require(current, [changed]));
    }

    [Fact]
    public void OriginalRequestProjectionIsDetachedAndMutableItHeadsDoNotChangeOriginalCreateProof()
    {
        var f = new Fixture(); f.Dependencies[1].Add(f.Ids[0]); var current = f.Build(); var effect = current.OriginalEffects[0];
        var original = effect.OriginalRequestIds.ToArray(); var fingerprint = effect.Fingerprint; var time = effect.CommittedAtUtc;
        f.Heads[0].CurrentRevision = 2; f.Heads[0].BusinessVersion = 2; f.Heads[0].Id = Guid.NewGuid();
        f.Input.Receipts[0].CommittedAtUtc = time.AddMinutes(1);
        Assert.Equal(original, effect.OriginalRequestIds); Assert.Equal(fingerprint, effect.Fingerprint); Assert.Equal(time, effect.CommittedAtUtc);
        Assert.Throws<NotSupportedException>(() => ((IList<Guid>)effect.OriginalRequestIds)[0] = Guid.NewGuid());
        Assert.Equal(1, GroupBatchOwnInputPlan.Require(current, f.Origins).OwnDependencyCount);
    }

    [Fact]
    public void OriginEnumerationIsActualDisposedAndNeverRelaysSuppliedDiagnostics()
    {
        var f = new Fixture(); f.Dependencies[1].Add(f.Ids[0]); var current = f.Build(); var rows = new UntrustedRows(f.Origins);
        Assert.Equal(1, GroupBatchOwnInputPlan.Require(current, rows).OwnDependencyCount); Assert.True(rows.Disposed);
        var throwing = new UntrustedRows(f.Origins, () => throw new Exception("PRIVATE_ORIGIN_ENUMERATOR"));
        var error = Assert.Throws<InvalidOperationException>(() => GroupBatchOwnInputPlan.Require(current, throwing));
        Assert.Null(error.InnerException); Assert.DoesNotContain("PRIVATE_ORIGIN", error.ToString()); Assert.True(throwing.Disposed);
    }

    [Fact]
    public void CancellationRemainsCancellationAndMissingOriginalEffectsCannotEstablishAPlan()
    {
        var f = new Fixture(); var current = f.Build(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => GroupBatchOwnInputPlan.Require(current, f.Origins, cancellation.Token));
        Assert.Throws<InvalidOperationException>(() => GroupBatchOwnInputPlan.Require(current with { OriginalEffects = current.OriginalEffects.Skip(1).ToArray() }, f.Origins));
    }

    [Fact]
    public void ActualScopedOriginQueryIsMetadataOnlyBoundedAndParsesOnClosedSqlConnection()
    {
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=owned_origin_design;Integrated Security=true;TrustServerCertificate=true").Options);
        var f = new Fixture(); var reader = new GroupBatchOwnInputReader(db); var sql = reader.OriginRows(f.Input.Allocation.Scope, [f.Ids[0], f.Ids[1]]).ToQueryString();
        foreach (var field in new[] { "TenantId", "CompanyId", "BindingId", "OriginBatchId", "OriginOperationId", "OriginCandidateOrdinal", "CreatedAtUtc" }) Assert.Contains(field, sql);
        foreach (var field in new[] { "ProtectedContent", "ContentKeyId", "CurrentRevision", "BusinessStatus", "RequestCode" }) Assert.DoesNotContain(field, sql);
        Assert.Contains("TOP(@", sql); Assert.Contains("2001", sql); Assert.Contains("[Id] IN (", sql);
        Assert.Contains("[TenantId] =", sql); Assert.Contains("[CompanyId] =", sql); Assert.Contains("[BindingId] =", sql);
        new TSql160Parser(true).Parse(new StringReader(sql), out var errors); Assert.Empty(errors);
        Assert.Throws<InvalidOperationException>(() => reader.OriginRows(f.Input.Allocation.Scope, []));
        Assert.Throws<InvalidOperationException>(() => reader.OriginRows(f.Input.Allocation.Scope, [f.Ids[0], f.Ids[0]]));
        Assert.Throws<InvalidOperationException>(() => reader.OriginRows(f.Input.Allocation.Scope, Enumerable.Range(0, 2001).Select(_ => Guid.NewGuid()).ToArray()));
        var maximumSql = reader.OriginRows(f.Input.Allocation.Scope, Enumerable.Range(0, 2000).Select(_ => Guid.NewGuid()).ToArray()).ToQueryString();
        Assert.InRange(System.Text.RegularExpressions.Regex.Matches(maximumSql, @"DECLARE @").Count, 4, 2100);
        new TSql160Parser(true).Parse(new StringReader(maximumSql), out var maximumErrors); Assert.Empty(maximumErrors);
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task LockedOriginReaderRefusesAbsentOwnedSqlUnitWithoutOpeningOrTracking()
    {
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=owned_origin_design;Integrated Security=true;TrustServerCertificate=true").Options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GroupBatchOwnInputReader(db).RequireLockedAsync(new Fixture().Build()));
        Assert.Empty(db.ChangeTracker.Entries()); Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Fact]
    public void HistoricalOriginalGraphRetainsFiveContributorOwnInputDagWithoutCurrentVerdict()
    {
        var f = new Fixture(); for (var index = 1; index < 5; index++) f.Dependencies[index].Add(f.Ids[index - 1]);
        var current = f.Build(); var now = f.Input.Allocation.AllocatedAtUtc.AddDays(30);
        var historical = GroupBatchHistoricalGraph.Require(current.Coverage, current.OriginalEffects, f.Origins, now);
        Assert.Equal(4, historical.OwnInputs.OwnDependencyCount);
        Assert.Equal(GroupBatchOwnInputPlan.Require(current, f.Origins).ContributorOrder, historical.OwnInputs.ContributorOrder);
        f.Heads[0].CurrentRevision = 2; f.Heads[0].BusinessVersion++;
        Assert.Equal(5, historical.Coverage.NoteCount); Assert.Equal(4, historical.OwnInputs.OwnDependencyCount);
        Assert.Equal(GroupBatchTerminalManifest.Create(current, Guid.Parse("ed136d1e-afd0-4011-981f-18eb98f4d13d")).Write(),
            GroupBatchTerminalManifest.CreateOriginal(historical, Guid.Parse("ed136d1e-afd0-4011-981f-18eb98f4d13d")).Write());
    }

    [Theory]
    [InlineData("self")]
    [InlineData("cycle")]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("origin-substitution")]
    public void HistoricalGraphCannotBypassOriginalOwnInputDependencies(string fault)
    {
        var f = new Fixture(); f.Dependencies[1].Add(f.Ids[0]);
        if (fault == "self") f.Dependencies[0].Add(f.Ids[0]);
        if (fault == "cycle") f.Dependencies[0].Add(f.Ids[1]);
        var current = f.Build(); var origins = f.Origins.ToList();
        if (fault == "missing") origins.Clear();
        if (fault == "foreign") origins[0] = origins[0] with { Scope = origins[0].Scope with { CompanyId = Guid.NewGuid() } };
        if (fault == "origin-substitution") origins[0] = origins[0] with { OriginBatchId = Guid.NewGuid() };
        var error = Assert.Throws<InvalidOperationException>(() => GroupBatchHistoricalGraph.Require(current.Coverage,
            current.OriginalEffects, origins, f.Input.Allocation.AllocatedAtUtc.AddDays(30)));
        Assert.Equal("Group historical original graph is not available.", error.Message); Assert.Null(error.InnerException);
    }

    private sealed class UntrustedRows(IEnumerable<GroupBrainRequestOriginMetadata> rows, Action? after = null) : IEnumerable<GroupBrainRequestOriginMetadata>
    {
        internal bool Disposed;
        public IEnumerator<GroupBrainRequestOriginMetadata> GetEnumerator()
        { try { foreach (var row in rows) { yield return row; after?.Invoke(); } } finally { Disposed = true; } }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Fixture
    {
        internal readonly GroupWholeBatchCoverageTests.Fixture Input = new();
        internal readonly Guid[] Ids;
        internal readonly List<Guid>[] Dependencies = Enumerable.Range(0, 5).Select(_ => new List<Guid>()).ToArray();
        internal readonly Guid?[] Glossary = new Guid?[5];
        internal readonly List<GroupBrainRequestOriginMetadata> Origins = [];
        internal readonly List<GroupCustomerRequestRecord> Heads = [];
        internal Fixture()
        {
            Ids = Input.Receipts.Select(work => Identity(work, "request", 1)).ToArray();
        }
        internal Guid ExternalIdentity(Guid batch, Guid operation) => new(SHA256.HashData(Encoding.ASCII.GetBytes(FormattableString.Invariant(
            $"aioffice-group-note-id-v1/{Input.Allocation.Scope.TenantId:D}/{Input.Allocation.Scope.CompanyId:D}/{Input.Allocation.Scope.SourceBindingId:D}/{batch:D}/{operation:D}/request/1"))).AsSpan(0, 16));
        internal GroupWholeBatchDependencyVerdict.Current Build()
        {
            var effects = new List<GroupWorkEffectLedger>(); var claims = new List<GroupOriginalClaimProvenance>();
            for (var index = 0; index < Input.Receipts.Count; index++)
            {
                var work = Input.Receipts[index]; work.NoteCount = 1; work.Outcome = GroupWorkCommitOutcome.Attention;
                var selected = Input.Selected.Where(x => x.OperationId == work.OperationId).OrderBy(x => x.MessageId).ToArray();
                selected[0].Outcome = GroupWorkSourceOutcome.Attention;
                foreach (var raw in Input.Raw.Where(x => x.MessageId == selected[0].MessageId)) raw.Outcome = GroupWorkSourceOutcome.Attention;
                using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
                var original = work.DependencyManifest!.ToArray(); original[161] = (byte)(Dependencies[index].Count + (Glossary[index] is null ? 0 : 1));
                writer.Write(original);
                foreach (var dependencyId in Dependencies[index].Order()) WriteDependency(GroupBrainContentKind.RequestRevision, dependencyId);
                if (Glossary[index] is { } glossary) WriteDependency(GroupBrainContentKind.GlossaryRevision, glossary);
                writer.Flush(); work.DependencyManifest = stream.ToArray();
                var claim = new GroupBatchClaimReceiptRecord
                {
                    TenantId = work.TenantId,
                    CompanyId = work.CompanyId,
                    BindingId = work.BindingId,
                    BatchId = work.BatchId,
                    OperationId = Guid.NewGuid(),
                    OwnerId = Guid.NewGuid(),
                    Epoch = work.ClaimEpoch,
                    ServiceId = work.ServiceId,
                    CredentialEpoch = work.CredentialEpoch,
                    GrantVersion = work.GrantVersion,
                    SourceVersion = work.SourceVersion,
                    DeletionGeneration = work.DeletionGeneration,
                    AccountVersion = work.AccountVersion,
                    AuthoritySha256 = GroupWorkDependencyManifest.Read(work.DependencyManifest).AuthoritySha256,
                    RequestedLifetimeTicks = TimeSpan.FromMinutes(2).Ticks,
                    IssuedAtUtc = work.CommittedAtUtc.AddMinutes(-1),
                    ExpiresAtUtc = work.CommittedAtUtc.AddMinutes(1)
                };
                var id = Ids[index]; var outbox = Identity(work, "outbox", 0); var bytes = new byte[30]; bytes[0] = 1;
                var head = new GroupCustomerRequestRecord
                {
                    TenantId = work.TenantId,
                    CompanyId = work.CompanyId,
                    BindingId = work.BindingId,
                    Id = id,
                    OriginBatchId = work.BatchId,
                    OriginOperationId = work.OperationId,
                    OriginCandidateOrdinal = 1,
                    RequestCode = "REQ-" + id.ToString("N").ToUpperInvariant(),
                    Kind = GroupNoteKind.NeedsClarification,
                    SourceVersion = work.SourceVersion,
                    DeletionGeneration = work.DeletionGeneration,
                    CreatedAtUtc = work.CommittedAtUtc
                }; Heads.Add(head);
                var revision = new GroupRequestRevisionRecord
                {
                    TenantId = work.TenantId,
                    CompanyId = work.CompanyId,
                    BindingId = work.BindingId,
                    RequestId = id,
                    Revision = 1,
                    Origin = GroupRequestRevisionOrigin.HostAttention,
                    VerificationLevel = GroupRequestVerificationLevel.HostObserved,
                    AuthorServiceId = work.ServiceId,
                    SourceBatchId = work.BatchId,
                    ClaimEpoch = work.ClaimEpoch,
                    SourceVersion = work.SourceVersion,
                    DeletionGeneration = work.DeletionGeneration,
                    ContentKeyId = "owned-origin-fixture",
                    ProtectedContent = bytes,
                    EnvelopeSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                    CreatedAtUtc = work.CommittedAtUtc
                };
                var evidence = new GroupRequestEvidenceRecord
                {
                    TenantId = work.TenantId,
                    CompanyId = work.CompanyId,
                    BindingId = work.BindingId,
                    RequestId = id,
                    RequestRevision = 1,
                    Ordinal = 1,
                    MessageId = selected[0].MessageId,
                    MessageRevision = selected[0].MessageRevision,
                    Kind = GroupRequestEvidenceKind.HostMetadataAttention
                };
                var publication = new GroupNotesCommittedOutboxRecord
                {
                    TenantId = work.TenantId,
                    CompanyId = work.CompanyId,
                    BindingId = work.BindingId,
                    Id = outbox,
                    BatchId = work.BatchId,
                    OperationId = work.OperationId,
                    NoteCount = 1,
                    IsHistoricalBackfill = false,
                    CommittedAtUtc = work.CommittedAtUtc
                };
                var item = new GroupNotesCommittedItemRecord
                {
                    TenantId = work.TenantId,
                    CompanyId = work.CompanyId,
                    BindingId = work.BindingId,
                    OutboxId = outbox,
                    Ordinal = 1,
                    RequestId = id,
                    RequestRevision = 1
                };
                var effect = GroupWorkEffectLedger.Require(work, claim, false, selected, [head], [revision], [evidence], [publication], [item]);
                GroupWorkEffectDigest.Stage(work, effect); effects.Add(effect); claims.Add(GroupOriginalClaimProvenance.Require(work, claim));
                void WriteDependency(GroupBrainContentKind kind, Guid record)
                { writer.Write((byte)kind); writer.Write(record.ToByteArray()); writer.Write(2L); writer.Write(Enumerable.Repeat((byte)3, 32).ToArray()); }
            }
            var requested = Dependencies.SelectMany(x => x).ToHashSet();
            for (var index = 0; index < Ids.Length; index++)
                if (requested.Contains(Ids[index])) Origins.Add(new(Input.Allocation.Scope, Ids[index], Input.Allocation.BatchId,
                    Input.Receipts[index].OperationId, 1, Input.Receipts[index].CommittedAtUtc));
            return new(Input.Require(), claims.AsReadOnly(), effects.AsReadOnly());
        }
        private static Guid Identity(GroupWorkCommitReceiptRecord work, string kind, int ordinal) => new(SHA256.HashData(Encoding.ASCII.GetBytes(FormattableString.Invariant(
            $"aioffice-group-note-id-v1/{work.TenantId:D}/{work.CompanyId:D}/{work.BindingId:D}/{work.BatchId:D}/{work.OperationId:D}/{kind}/{ordinal}"))).AsSpan(0, 16));
    }
}
