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
