using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupOriginalClaimProvenanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalAuthorityAtCommitCanBeRetainedAfterItsLeaseExpired(bool maximum)
    {
        var f = new Fixture(); f.Claim.RequestedLifetimeTicks = (maximum ? GroupBatchClaimStore.MaximumLifetime : GroupBatchClaimStore.MinimumLifetime).Ticks;
        f.Claim.ExpiresAtUtc = f.Claim.IssuedAtUtc.AddTicks(f.Claim.RequestedLifetimeTicks);
        f.Work.CommittedAtUtc = maximum ? f.Claim.ExpiresAtUtc.AddTicks(-1) : f.Claim.IssuedAtUtc;
        var result = GroupOriginalClaimProvenance.Require(f.Work, f.Claim);
        Assert.Equal(f.Scope, result.Scope); Assert.Equal(f.Work.BatchId, result.BatchId);
        Assert.Equal(f.Work.OperationId, result.WorkOperationId); Assert.Equal(f.Claim.OperationId, result.ClaimOperationId);
        Assert.NotEqual(result.WorkOperationId, result.ClaimOperationId); Assert.Equal(f.Claim.OwnerId, result.OwnerId);
        Assert.Equal(2, result.Epoch); Assert.Equal(f.Claim.AuthoritySha256, result.AuthoritySha256);
        Assert.Equal(f.Claim.IssuedAtUtc, result.IssuedAtUtc); Assert.Equal(f.Claim.ExpiresAtUtc, result.ExpiresAtUtc);
        Assert.Equal(f.Work.CommittedAtUtc, result.CommittedAtUtc);
        var owner = result.OwnerId; var committed = result.CommittedAtUtc;
        f.Claim.OwnerId = Guid.NewGuid(); f.Work.CommittedAtUtc = f.Claim.ExpiresAtUtc;
        Assert.Equal(owner, result.OwnerId); Assert.Equal(committed, result.CommittedAtUtc);
        Assert.Equal("Group original claim provenance (private metadata).", result.ToString());
    }

    [Theory]
    [InlineData("work-tenant")]
    [InlineData("work-company")]
    [InlineData("work-binding")]
    [InlineData("work-batch")]
    [InlineData("work-operation")]
    [InlineData("work-count")]
    [InlineData("work-notes")]
    [InlineData("work-no-work-notes")]
    [InlineData("work-outcome")]
    [InlineData("legacy")]
    [InlineData("missing-manifest")]
    [InlineData("malformed-manifest")]
    [InlineData("claim-tenant")]
    [InlineData("claim-company")]
    [InlineData("claim-binding")]
    [InlineData("claim-batch")]
    [InlineData("claim-epoch")]
    [InlineData("claim-zero-epoch")]
    [InlineData("claim-owner")]
    [InlineData("claim-operation")]
    [InlineData("claim-service")]
    [InlineData("claim-zero-service")]
    [InlineData("credential")]
    [InlineData("zero-credential")]
    [InlineData("grant")]
    [InlineData("zero-grant")]
    [InlineData("source")]
    [InlineData("zero-source")]
    [InlineData("deletion")]
    [InlineData("negative-deletion")]
    [InlineData("account")]
    [InlineData("zero-account")]
    [InlineData("authority")]
    [InlineData("private-authority")]
    [InlineData("null-authority")]
    [InlineData("lower-authority")]
    [InlineData("short-lease")]
    [InlineData("long-lease")]
    [InlineData("wrong-duration")]
    [InlineData("issued-offset")]
    [InlineData("expires-offset")]
    [InlineData("committed-offset")]
    [InlineData("before-issued")]
    [InlineData("at-expiry")]
    [InlineData("after-expiry")]
    [InlineData("reverse-window")]
    public void OriginalChunkClaimScopeAuthorityAndCommitWindowAreRequired(string fault)
    {
        var f = new Fixture(); var work = f.Work; var claim = f.Claim;
        switch (fault)
        {
            case "work-tenant": work.TenantId = Guid.NewGuid(); break;
            case "work-company": work.CompanyId = Guid.NewGuid(); break;
            case "work-binding": work.BindingId = Guid.NewGuid(); break;
            case "work-batch": work.BatchId = Guid.NewGuid(); break;
            case "work-operation": work.OperationId = Guid.NewGuid(); break;
            case "work-count": work.SelectedMessageCount = 2; break;
            case "work-notes": work.NoteCount = GroupAutomaticNotePlan.MaximumNotes + 1; break;
            case "work-no-work-notes": work.NoteCount = 1; break;
            case "work-outcome": work.Outcome = (GroupWorkCommitOutcome)99; break;
            case "legacy": work.DependencyManifestVersion = 0; break;
            case "missing-manifest": work.DependencyManifest = null; break;
            case "malformed-manifest": work.DependencyManifest![0] ^= 1; break;
            case "claim-tenant": claim.TenantId = Guid.NewGuid(); break;
            case "claim-company": claim.CompanyId = Guid.NewGuid(); break;
            case "claim-binding": claim.BindingId = Guid.NewGuid(); break;
            case "claim-batch": claim.BatchId = Guid.NewGuid(); break;
            case "claim-epoch": claim.Epoch++; break;
            case "claim-zero-epoch": claim.Epoch = work.ClaimEpoch = 0; break;
            case "claim-owner": claim.OwnerId = Guid.Empty; break;
            case "claim-operation": claim.OperationId = Guid.Empty; break;
            case "claim-service": claim.ServiceId = Guid.NewGuid(); break;
            case "claim-zero-service": claim.ServiceId = work.ServiceId = Guid.Empty; break;
            case "credential": claim.CredentialEpoch++; break;
            case "zero-credential": claim.CredentialEpoch = work.CredentialEpoch = 0; break;
            case "grant": claim.GrantVersion++; break;
            case "zero-grant": claim.GrantVersion = work.GrantVersion = 0; break;
            case "source": claim.SourceVersion++; break;
            case "zero-source": claim.SourceVersion = work.SourceVersion = 0; break;
            case "deletion": claim.DeletionGeneration++; break;
            case "negative-deletion": claim.DeletionGeneration = work.DeletionGeneration = -1; break;
            case "account": claim.AccountVersion++; break;
            case "zero-account": claim.AccountVersion = work.AccountVersion = 0; break;
            case "authority": claim.AuthoritySha256 = new('B', 64); break;
            case "private-authority": claim.AuthoritySha256 = "PRIVATE_OWNED_AUTHORITY_MARKER"; break;
            case "null-authority": claim.AuthoritySha256 = null!; break;
            case "lower-authority": claim.AuthoritySha256 = claim.AuthoritySha256.ToLowerInvariant(); break;
            case "short-lease": claim.RequestedLifetimeTicks = GroupBatchClaimStore.MinimumLifetime.Ticks - 1; break;
            case "long-lease": claim.RequestedLifetimeTicks = GroupBatchClaimStore.MaximumLifetime.Ticks + 1; break;
            case "wrong-duration": claim.ExpiresAtUtc = claim.ExpiresAtUtc.AddTicks(1); break;
            case "issued-offset": claim.IssuedAtUtc = claim.IssuedAtUtc.ToOffset(TimeSpan.FromHours(7)); break;
            case "expires-offset": claim.ExpiresAtUtc = claim.ExpiresAtUtc.ToOffset(TimeSpan.FromHours(7)); break;
            case "committed-offset": work.CommittedAtUtc = work.CommittedAtUtc.ToOffset(TimeSpan.FromHours(7)); break;
            case "before-issued": work.CommittedAtUtc = claim.IssuedAtUtc.AddTicks(-1); break;
            case "at-expiry": work.CommittedAtUtc = claim.ExpiresAtUtc; break;
            case "after-expiry": work.CommittedAtUtc = claim.ExpiresAtUtc.AddTicks(1); break;
            case "reverse-window": claim.ExpiresAtUtc = claim.IssuedAtUtc.AddTicks(-1); break;
            default: throw new InvalidOperationException();
        }
        var error = Assert.Throws<InvalidOperationException>(() => GroupOriginalClaimProvenance.Require(work, claim));
        Assert.Null(error.InnerException); Assert.DoesNotContain("PRIVATE_OWNED", error.ToString());
    }

    [Fact]
    public void ValidMaximumUtcBoundaryUsesTheOriginalLeaseIntervalWithoutDateArithmeticOverflow()
    {
        var f = new Fixture(); f.Claim.ExpiresAtUtc = DateTimeOffset.MaxValue;
        f.Claim.IssuedAtUtc = f.Claim.ExpiresAtUtc.Subtract(TimeSpan.FromTicks(f.Claim.RequestedLifetimeTicks));
        f.Work.CommittedAtUtc = f.Claim.ExpiresAtUtc.AddTicks(-1);
        Assert.Equal(f.Work.CommittedAtUtc, GroupOriginalClaimProvenance.Require(f.Work, f.Claim).CommittedAtUtc);
    }

    private sealed class Fixture
    {
        internal GroupScope Scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        internal GroupWorkCommitReceiptRecord Work;
        internal GroupBatchClaimReceiptRecord Claim;
        internal Fixture()
        {
            var batch = Guid.NewGuid(); var operation = Guid.NewGuid(); var service = Guid.NewGuid();
            var issued = DateTimeOffset.Parse("2026-10-10T00:00:00Z");
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write("AIOGDEP1"u8);
            foreach (var id in new[] { Scope.TenantId, Scope.CompanyId, Scope.SourceBindingId, batch, operation }) writer.Write(id.ToByteArray());
            writer.Write(12L); writer.Write(Enumerable.Repeat((byte)0xAA, 32).ToArray()); writer.Write(Enumerable.Repeat((byte)0xBB, 32).ToArray());
            writer.Write((byte)1); writer.Write((byte)0); writer.Write(Guid.NewGuid().ToByteArray()); writer.Write(1L);
            writer.Write(Enumerable.Repeat((byte)0xCC, 32).ToArray()); writer.Flush();
            Work = new()
            {
                TenantId = Scope.TenantId,
                CompanyId = Scope.CompanyId,
                BindingId = Scope.SourceBindingId,
                BatchId = batch,
                OperationId = operation,
                DependencyManifestVersion = 1,
                DependencyManifest = stream.ToArray(),
                SourceSetSha256 = new('D', 64),
                SelectedMessageCount = 1,
                Outcome = GroupWorkCommitOutcome.NoWork,
                ServiceId = service,
                ClaimEpoch = 2,
                CredentialEpoch = 1,
                GrantVersion = 1,
                SourceVersion = 1,
                AccountVersion = 1,
                CommittedAtUtc = issued.AddSeconds(1)
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
                RequestedLifetimeTicks = TimeSpan.FromMinutes(2).Ticks,
                IssuedAtUtc = issued,
                ExpiresAtUtc = issued.AddMinutes(2),
                ServiceId = service,
                CredentialEpoch = 1,
                GrantVersion = 1,
                SourceVersion = 1,
                AccountVersion = 1,
                AuthoritySha256 = new('A', 64)
            };
        }
    }
}
