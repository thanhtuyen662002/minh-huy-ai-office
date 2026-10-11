using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupBatchTerminalCommitProofTests
{
    [Fact]
    public void CompleteZeroNoteBatchSealsEveryContributorAndDetachedManifestWithOriginalAcquisition()
    {
        var f = new Fixture(); var receipt = f.Stage(); f.Require(receipt);
        Assert.Equal(500, receipt.RawRevisionCount); Assert.Equal(100, receipt.SelectedMessageCount);
        Assert.Equal(5, receipt.ContributorCount); Assert.Equal(0, receipt.NoteCount);
        Assert.Equal(f.Original.OperationId, receipt.ClaimOperationId); Assert.Equal(f.Original.OwnerId, receipt.ClaimOwnerId);
        Assert.Equal(5, GroupBatchTerminalManifest.Read(receipt.Manifest).Contributors.Count);
        Assert.Equal(SHA256.HashData(receipt.Manifest), receipt.ManifestSha256);
        var other = f.Stage(); receipt.Manifest[0] ^= 1;
        Assert.Equal((byte)'A', other.Manifest[0]);
        Assert.Throws<InvalidOperationException>(() => f.Require(receipt));
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("binding")]
    [InlineData("batch")]
    [InlineData("operation")]
    [InlineData("version")]
    [InlineData("manifest")]
    [InlineData("digest")]
    [InlineData("after")]
    [InlineData("through")]
    [InlineData("raw")]
    [InlineData("selected")]
    [InlineData("contributors")]
    [InlineData("notes")]
    [InlineData("claim-operation")]
    [InlineData("claim-owner")]
    [InlineData("claim-epoch")]
    [InlineData("service")]
    [InlineData("credential")]
    [InlineData("grant")]
    [InlineData("source")]
    [InlineData("deletion")]
    [InlineData("account")]
    [InlineData("before-original")]
    [InlineData("at-expiry")]
    [InlineData("future")]
    [InlineData("offset")]
    public void SubstitutedTerminalReceiptCannotReplayWithMatchingOrRecomputedCarrierDigest(string fault)
    {
        var f = new Fixture(); var r = f.Stage();
        switch (fault)
        {
            case "tenant": r.TenantId = Guid.NewGuid(); break;
            case "company": r.CompanyId = Guid.NewGuid(); break;
            case "binding": r.BindingId = Guid.NewGuid(); break;
            case "batch": r.BatchId = Guid.NewGuid(); break;
            case "operation": r.OperationId = Guid.NewGuid(); break;
            case "version": r.ManifestVersion = 0; break;
            case "manifest": r.Manifest[104] ^= 1; r.ManifestSha256 = SHA256.HashData(r.Manifest); break;
            case "digest": r.ManifestSha256[0] ^= 1; break;
            case "after": r.AfterSequence++; break;
            case "through": r.ThroughSequence++; break;
            case "raw": r.RawRevisionCount--; break;
            case "selected": r.SelectedMessageCount--; break;
            case "contributors": r.ContributorCount--; break;
            case "notes": r.NoteCount++; break;
            case "claim-operation": r.ClaimOperationId = Guid.NewGuid(); break;
            case "claim-owner": r.ClaimOwnerId = Guid.NewGuid(); break;
            case "claim-epoch": r.ClaimEpoch++; break;
            case "service": r.ServiceId = Guid.NewGuid(); break;
            case "credential": r.CredentialEpoch++; break;
            case "grant": r.GrantVersion++; break;
            case "source": r.SourceVersion++; break;
            case "deletion": r.DeletionGeneration++; break;
            case "account": r.AccountVersion++; break;
            case "before-original": r.CommittedAtUtc = f.Original.IssuedAtUtc.AddTicks(-1); break;
            case "at-expiry": r.CommittedAtUtc = f.Original.ExpiresAtUtc; break;
            case "future": r.CommittedAtUtc = f.Now.AddTicks(1); break;
            case "offset": r.CommittedAtUtc = r.CommittedAtUtc.ToOffset(TimeSpan.FromHours(7)); break;
        }
        var error = Assert.Throws<InvalidOperationException>(() => f.Require(r));
        Assert.Equal("Group terminal receipt proof is not available.", error.Message); Assert.Null(error.InnerException);
    }

    [Fact]
    public void LaterCurrentLeaseCanReplayOnlySameOriginalReceiptAndCompleteGraph()
    {
        var f = new Fixture(); var receipt = f.Stage();
        var later = f.Lease with
        {
            OperationId = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            Epoch = 2,
            IssuedAtUtc = f.Now,
            ExpiresAtUtc = f.Now.AddSeconds(30)
        };
        GroupBatchTerminalCommitProof.Require(f.Current, later, f.Authority, f.Original, receipt, f.Operation, f.Now.AddSeconds(1));
        Assert.Throws<InvalidOperationException>(() => GroupBatchTerminalCommitProof.Stage(f.Current, later, f.Authority, f.Original, f.Operation, f.Now.AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(() => GroupBatchTerminalCommitProof.Require(f.Current with { OriginalEffects = f.Current.OriginalEffects.Skip(1).ToArray() }, later, f.Authority, f.Original, receipt, f.Operation, f.Now.AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(() => GroupBatchTerminalCommitProof.Require(f.Current with { OriginalClaims = f.Current.OriginalClaims.Skip(1).ToArray() }, later, f.Authority, f.Original, receipt, f.Operation, f.Now.AddSeconds(1)));
    }

    [Theory]
    [InlineData("foreign-original")]
    [InlineData("authority")]
    [InlineData("original-expiry")]
    [InlineData("active-expiry")]
    [InlineData("clock-before-issue")]
    [InlineData("original-epoch")]
    public void OriginalProvenanceAndLiveLeaseAreRequiredInAdditionToTheManifest(string fault)
    {
        var f = new Fixture(); var receipt = f.Stage();
        switch (fault)
        {
            case "foreign-original": f.Original.BindingId = Guid.NewGuid(); break;
            case "authority": f.Original.AuthoritySha256 = new string('A', 64); break;
            case "original-expiry": f.Original.ExpiresAtUtc = f.Original.ExpiresAtUtc.AddTicks(1); break;
            case "active-expiry": f.Now = f.Lease.ExpiresAtUtc; break;
            case "clock-before-issue": f.Now = f.Lease.IssuedAtUtc.AddTicks(-1); break;
            case "original-epoch": f.Original.Epoch = f.Lease.Epoch + 1; break;
        }
        Assert.Throws<InvalidOperationException>(() => f.Require(receipt));
    }

    [Fact]
    public void ActualScopedReplayQueryDetectsBatchAndOperationCollisionWithoutOpeningSql()
    {
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(
            "Server=127.0.0.1,1;Database=owned_terminal_cold;Integrated Security=true;TrustServerCertificate=true").Options);
        var f = new Fixture(); var store = new GroupBatchTerminalStore(db, new(f.Lease.Scope.TenantId, f.Lease.Scope.CompanyId,
            f.Lease.ServiceId, f.Lease.CredentialEpoch), TimeProvider.System, null!, null!);
        var sql = store.ReceiptRows(f.Lease.Scope, f.Lease.BatchId, f.Operation).ToQueryString();
        foreach (var predicate in new[] { "[TenantId] =", "[CompanyId] =", "[BindingId] =", "[BatchId] =", "[OperationId] =", " OR " }) Assert.Contains(predicate, sql);
        Assert.Contains("TOP(@", sql); Assert.Contains("= 3", sql);
        new TSql160Parser(true).Parse(new StringReader(sql), out var errors); Assert.Empty(errors);
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Fact]
    public void StoredWriteRequiresExactPreparedUtcAndManifestBeyondValidReplayProvenance()
    {
        var f = new Fixture(); var prepared = f.Stage(); var observed = f.Stage();
        GroupBatchTerminalCommitProof.RequireWritten(f.Current, f.Lease, f.Authority, f.Original, observed,
            f.Operation, prepared.Manifest, prepared.CommittedAtUtc, f.Now);
        observed.CommittedAtUtc = prepared.CommittedAtUtc.AddTicks(-1);
        f.Require(observed); // Valid provenance alone does not prove this write.
        Assert.Throws<InvalidOperationException>(() => GroupBatchTerminalCommitProof.RequireWritten(f.Current,
            f.Lease, f.Authority, f.Original, observed, f.Operation, prepared.Manifest, prepared.CommittedAtUtc, f.Now));
        observed = f.Stage(); var wrongManifest = prepared.Manifest.ToArray(); wrongManifest[104] ^= 1;
        Assert.Throws<InvalidOperationException>(() => GroupBatchTerminalCommitProof.RequireWritten(f.Current,
            f.Lease, f.Authority, f.Original, observed, f.Operation, wrongManifest, prepared.CommittedAtUtc, f.Now));
    }

    [Fact]
    public void HistoricalReceiptUsesActualOriginalWindowAfterExpiryAndPreservesCanonicalBytes()
    {
        var f = new Fixture(); var receipt = f.Stage(); var later = f.Lease.ExpiresAtUtc.AddDays(30);
        var historical = GroupBatchHistoricalGraph.Require(f.Current.Coverage, f.Current.OriginalEffects, [], later);
        GroupBatchHistoricalReceiptProof.Require(historical, f.Original, receipt, f.Operation, later);
        Assert.Equal(receipt.Manifest, GroupBatchTerminalManifest.CreateOriginal(historical, f.Operation).Write());
        Assert.Equal(5, historical.OriginalEffects.Count); Assert.Equal(500, historical.Coverage.RawCount);
        Assert.Equal(5, historical.OwnInputs.ContributorOrder.Count);
        Assert.Throws<InvalidOperationException>(() => GroupBatchTerminalCommitProof.Require(f.Current,
            f.Lease, f.Authority, f.Original, receipt, f.Operation, later));
        Assert.DoesNotContain("Current", historical.GetType().Name);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("binding")]
    [InlineData("batch")]
    [InlineData("operation")]
    [InlineData("version")]
    [InlineData("manifest")]
    [InlineData("digest")]
    [InlineData("after")]
    [InlineData("through")]
    [InlineData("raw")]
    [InlineData("selected")]
    [InlineData("contributors")]
    [InlineData("notes")]
    [InlineData("claim-operation")]
    [InlineData("claim-owner")]
    [InlineData("claim-epoch")]
    [InlineData("service")]
    [InlineData("credential")]
    [InlineData("grant")]
    [InlineData("source")]
    [InlineData("deletion")]
    [InlineData("account")]
    [InlineData("before-original")]
    [InlineData("at-expiry")]
    [InlineData("future")]
    [InlineData("offset")]
    public void HistoricalReceiptRejectsEverySubstitutedFieldAndForgedCarrierHash(string fault)
    {
        var f = new Fixture(); var r = f.Stage();
        var historical = GroupBatchHistoricalGraph.Require(f.Current.Coverage, f.Current.OriginalEffects, [], f.Now);
        switch (fault)
        {
            case "tenant": r.TenantId = Guid.NewGuid(); break;
            case "company": r.CompanyId = Guid.NewGuid(); break;
            case "binding": r.BindingId = Guid.NewGuid(); break;
            case "batch": r.BatchId = Guid.NewGuid(); break;
            case "operation": r.OperationId = Guid.NewGuid(); break;
            case "version": r.ManifestVersion = 0; break;
            case "manifest": r.Manifest[104] ^= 1; r.ManifestSha256 = SHA256.HashData(r.Manifest); break;
            case "digest": r.ManifestSha256[0] ^= 1; break;
            case "after": r.AfterSequence++; break;
            case "through": r.ThroughSequence++; break;
            case "raw": r.RawRevisionCount--; break;
            case "selected": r.SelectedMessageCount--; break;
            case "contributors": r.ContributorCount--; break;
            case "notes": r.NoteCount++; break;
            case "claim-operation": r.ClaimOperationId = Guid.NewGuid(); break;
            case "claim-owner": r.ClaimOwnerId = Guid.NewGuid(); break;
            case "claim-epoch": r.ClaimEpoch++; break;
            case "service": r.ServiceId = Guid.NewGuid(); break;
            case "credential": r.CredentialEpoch++; break;
            case "grant": r.GrantVersion++; break;
            case "source": r.SourceVersion++; break;
            case "deletion": r.DeletionGeneration++; break;
            case "account": r.AccountVersion++; break;
            case "before-original": r.CommittedAtUtc = f.Original.IssuedAtUtc.AddTicks(-1); break;
            case "at-expiry": r.CommittedAtUtc = f.Original.ExpiresAtUtc; break;
            case "future": r.CommittedAtUtc = f.Now.AddTicks(1); break;
            case "offset": r.CommittedAtUtc = r.CommittedAtUtc.ToOffset(TimeSpan.FromHours(7)); break;
        }
        var error = Assert.Throws<InvalidOperationException>(() => GroupBatchHistoricalReceiptProof.Require(
            historical, f.Original, r, f.Operation, f.Now));
        Assert.Equal("Group historical terminal receipt proof is not available.", error.Message); Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("lifetime")]
    [InlineData("issue-offset")]
    [InlineData("expiry-offset")]
    [InlineData("service")]
    [InlineData("credential")]
    [InlineData("grant")]
    [InlineData("source")]
    [InlineData("deletion")]
    [InlineData("account")]
    public void MatchingTerminalAndClaimMutationsStillCannotReplaceOriginalEffectAuthority(string fault)
    {
        var f = new Fixture(); var r = f.Stage();
        var historical = GroupBatchHistoricalGraph.Require(f.Current.Coverage, f.Current.OriginalEffects, [], f.Now);
        switch (fault)
        {
            case "authority": f.Original.AuthoritySha256 = new string('A', 64); break;
            case "lifetime": f.Original.RequestedLifetimeTicks++; break;
            case "issue-offset": f.Original.IssuedAtUtc = f.Original.IssuedAtUtc.ToOffset(TimeSpan.FromHours(7)); break;
            case "expiry-offset": f.Original.ExpiresAtUtc = f.Original.ExpiresAtUtc.ToOffset(TimeSpan.FromHours(7)); break;
            case "service": r.ServiceId = f.Original.ServiceId = Guid.NewGuid(); break;
            case "credential": r.CredentialEpoch = ++f.Original.CredentialEpoch; break;
            case "grant": r.GrantVersion = ++f.Original.GrantVersion; break;
            case "source": r.SourceVersion = ++f.Original.SourceVersion; break;
            case "deletion": r.DeletionGeneration = ++f.Original.DeletionGeneration; break;
            case "account": r.AccountVersion = ++f.Original.AccountVersion; break;
        }
        Assert.Throws<InvalidOperationException>(() => GroupBatchHistoricalReceiptProof.Require(historical, f.Original, r, f.Operation, f.Now));
    }

    [Fact]
    public void HistoricalGraphRequiresCompleteImmutableEffectsAndActualUtcWithoutOpeningSql()
    {
        var f = new Fixture(); var coverage = f.Current.Coverage; var effects = f.Current.OriginalEffects;
        foreach (var substituted in new[] { effects.Skip(1).ToArray(), effects.Append(effects[0]).ToArray(),
            new Fixture().Current.OriginalEffects.ToArray() })
            Assert.Throws<InvalidOperationException>(() => GroupBatchHistoricalGraph.Require(coverage, substituted, [], f.Now));
        Assert.Throws<InvalidOperationException>(() => GroupBatchHistoricalGraph.Require(coverage, effects, [], f.Input.Allocation.AllocatedAtUtc.AddTicks(-1)));
        Assert.Throws<InvalidOperationException>(() => GroupBatchHistoricalGraph.Require(coverage, effects, [], f.Now.ToOffset(TimeSpan.FromHours(7))));
        var historical = GroupBatchHistoricalGraph.Require(coverage, effects, [], f.Now);
        Assert.Throws<InvalidOperationException>(() => GroupBatchHistoricalReceiptProof.Require(historical, f.Original, f.Stage(), f.Operation, f.Now.AddTicks(-1)));
        f.Input.Receipts[0].SourceSetSha256 = new string('F', 64);
        f.Original.OwnerId = Guid.NewGuid(); // The sealed original acquisition is detached.
        Assert.Equal(effects[0].OriginalClaim.OwnerId, historical.OriginalEffects[0].OriginalClaim.OwnerId);
        Assert.Equal(5, historical.OriginalEffects.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<GroupWorkEffectLedger>)historical.OriginalEffects)[0] = effects[1]);
    }

    [Fact]
    public void HistoricalEnumerationBoundsCancellationDisposalAndPrivateFailureAreReal()
    {
        var f = new Fixture(); var coverage = f.Current.Coverage; var effects = f.Current.OriginalEffects;
        var valid = new HistoricalRows(effects);
        Assert.Equal(5, GroupBatchHistoricalGraph.Require(coverage, valid, [], f.Now).OriginalEffects.Count); Assert.True(valid.Disposed);
        var oversized = new HistoricalRows(Enumerable.Repeat(effects[0], 102));
        Assert.Throws<InvalidOperationException>(() => GroupBatchHistoricalGraph.Require(coverage, oversized, [], f.Now));
        Assert.Equal(101, oversized.Observed); Assert.True(oversized.Disposed);
        var throwing = new HistoricalRows(effects, () => throw new Exception("PRIVATE_HISTORICAL_ENUMERATOR"));
        var error = Assert.Throws<InvalidOperationException>(() => GroupBatchHistoricalGraph.Require(coverage, throwing, [], f.Now));
        Assert.Equal("Group historical original graph is not available.", error.Message); Assert.Null(error.InnerException);
        Assert.True(throwing.Disposed); Assert.DoesNotContain("PRIVATE_HISTORICAL", error.ToString());
        using var cts = new CancellationTokenSource(); var canceled = new HistoricalRows(effects, cts.Cancel);
        Assert.Throws<OperationCanceledException>(() => GroupBatchHistoricalGraph.Require(coverage, canceled, [], f.Now, cts.Token));
        Assert.True(canceled.Disposed); Assert.Equal(2, canceled.Observed);
    }

    private sealed class HistoricalRows(IEnumerable<GroupWorkEffectLedger> rows, Action? after = null)
        : IEnumerable<GroupWorkEffectLedger>, IReadOnlyCollection<GroupWorkEffectLedger>
    {
        internal bool Disposed; internal int Observed;
        public int Count => throw new Exception("Untrusted Count accessed.");
        public IEnumerator<GroupWorkEffectLedger> GetEnumerator()
        { try { foreach (var row in rows) { Observed++; yield return row; after?.Invoke(); } } finally { Disposed = true; } }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("owner")]
    [InlineData("window")]
    public void SameEpochCannotNameAnotherAcquisitionEvenWhenTerminalFieldsAreChangedTogether(string fault)
    {
        var f = new Fixture(); var r = f.Stage();
        var historical = GroupBatchHistoricalGraph.Require(f.Current.Coverage, f.Current.OriginalEffects, [], f.Now);
        if (fault == "operation") r.ClaimOperationId = f.Original.OperationId = Guid.NewGuid();
        if (fault == "owner") r.ClaimOwnerId = f.Original.OwnerId = Guid.NewGuid();
        if (fault == "window")
        { f.Original.IssuedAtUtc = f.Original.IssuedAtUtc.AddTicks(-1); f.Original.ExpiresAtUtc = f.Original.ExpiresAtUtc.AddTicks(-1); }
        Assert.Throws<InvalidOperationException>(() => GroupBatchHistoricalReceiptProof.Require(historical, f.Original, r, f.Operation, f.Now));
    }

    [Fact]
    public void GenuineLaterTerminalAcquisitionRetainsEarlierContributorEvidenceAfterRestart()
    {
        var f = new Fixture();
        f.Original.Epoch = 2; f.Original.OperationId = Guid.NewGuid(); f.Original.OwnerId = Guid.NewGuid();
        f.Original.IssuedAtUtc = f.Now; f.Original.ExpiresAtUtc = f.Now.AddSeconds(30);
        var laterLease = f.Lease with
        {
            Epoch = 2,
            OperationId = f.Original.OperationId,
            OwnerId = f.Original.OwnerId,
            IssuedAtUtc = f.Original.IssuedAtUtc,
            ExpiresAtUtc = f.Original.ExpiresAtUtc
        };
        var receipt = GroupBatchTerminalCommitProof.Stage(f.Current, laterLease, f.Authority, f.Original, f.Operation, f.Now.AddSeconds(1));
        var historical = GroupBatchHistoricalGraph.Require(f.Current.Coverage, f.Current.OriginalEffects, [], f.Now.AddDays(1));
        GroupBatchHistoricalReceiptProof.Require(historical, f.Original, receipt, f.Operation, historical.ObservedAtUtc);
        Assert.All(historical.OriginalEffects, x => Assert.Equal(1, x.OriginalClaim.Epoch)); Assert.Equal(2, receipt.ClaimEpoch);
    }

    private sealed class Fixture
    {
        internal readonly GroupWholeBatchCoverageTests.Fixture Input = new();
        internal readonly GroupWholeBatchDependencyVerdict.Current Current;
        internal readonly GroupBatchClaimReceiptRecord Original;
        internal readonly GroupBatchClaimReceipt Lease;
        internal readonly Guid Operation = Guid.NewGuid();
        internal readonly string Authority;
        internal DateTimeOffset Now;
        internal Fixture()
        {
            var claims = new List<GroupOriginalClaimProvenance>(); var effects = new List<GroupWorkEffectLedger>();
            var work = Input.Receipts[0]; Authority = GroupWorkDependencyManifest.Read(work.DependencyManifest).AuthoritySha256;
            Original = new()
            {
                TenantId = work.TenantId,
                CompanyId = work.CompanyId,
                BindingId = work.BindingId,
                BatchId = work.BatchId,
                Epoch = work.ClaimEpoch,
                OwnerId = Guid.NewGuid(),
                OperationId = Guid.NewGuid(),
                ServiceId = work.ServiceId,
                CredentialEpoch = work.CredentialEpoch,
                GrantVersion = work.GrantVersion,
                SourceVersion = work.SourceVersion,
                DeletionGeneration = work.DeletionGeneration,
                AccountVersion = work.AccountVersion,
                AuthoritySha256 = Authority,
                RequestedLifetimeTicks = TimeSpan.FromSeconds(30).Ticks,
                IssuedAtUtc = work.CommittedAtUtc.AddSeconds(-1),
                ExpiresAtUtc = work.CommittedAtUtc.AddSeconds(29)
            };
            foreach (var row in Input.Receipts)
            {
                var selected = Input.Selected.Where(x => x.OperationId == row.OperationId).ToArray();
                var effect = GroupWorkEffectLedger.Require(row, Original, false, selected, [], [], [], [], []);
                GroupWorkEffectDigest.Stage(row, effect); effects.Add(effect); claims.Add(GroupOriginalClaimProvenance.Require(row, Original));
            }
            Current = new(Input.Require(), claims.AsReadOnly(), effects.AsReadOnly()); Now = work.CommittedAtUtc.AddSeconds(1);
            Lease = new(Current.Coverage.Scope, Original.OperationId, Original.BatchId, Original.OwnerId, Original.Epoch,
                Original.RequestedLifetimeTicks, Original.IssuedAtUtc, Original.ExpiresAtUtc, Original.ServiceId, Original.CredentialEpoch,
                Original.GrantVersion, Original.SourceVersion, Original.DeletionGeneration, Original.AccountVersion);
        }
        internal GroupBatchTerminalReceiptRecord Stage() => GroupBatchTerminalCommitProof.Stage(Current, Lease, Authority, Original, Operation, Now);
        internal void Require(GroupBatchTerminalReceiptRecord receipt) => GroupBatchTerminalCommitProof.Require(Current, Lease, Authority, Original, receipt, Operation, Now);
    }
}
