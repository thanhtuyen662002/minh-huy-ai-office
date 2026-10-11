extern alias GroupReferenceProof;

using System.Buffers.Binary;
using System.Security.Cryptography;
using GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedTerminalReceiptObservationTests
{
    [Theory]
    [InlineData(2, 2)]
    [InlineData(500, 501)]
    public void IndependentObserverPinsOriginalAllocationPendingSuffixAndCompleteReceipt(int raw, long observed)
    {
        var f = new Fixture(raw, observed);
        var original = f.Observe();
        Assert.Matches("^[0-9A-F]{64}$", original);
        f.Row.CommittedAtUtc = f.Row.CommittedAtUtc.AddTicks(1);
        Assert.NotEqual(original, f.Observe());
    }

    [Theory]
    [InlineData("TenantId")]
    [InlineData("CompanyId")]
    [InlineData("BindingId")]
    [InlineData("BatchId")]
    [InlineData("OperationId")]
    [InlineData("ManifestVersion")]
    [InlineData("AfterSequence")]
    [InlineData("ThroughSequence")]
    [InlineData("RawRevisionCount")]
    [InlineData("SelectedMessageCount")]
    [InlineData("ContributorCount")]
    [InlineData("NoteCount")]
    [InlineData("ClaimOperationId")]
    [InlineData("ClaimOwnerId")]
    [InlineData("ClaimEpoch")]
    [InlineData("ServiceId")]
    [InlineData("CredentialEpoch")]
    [InlineData("GrantVersion")]
    [InlineData("SourceVersion")]
    [InlineData("DeletionGeneration")]
    [InlineData("AccountVersion")]
    [InlineData("CommittedAtUtc")]
    public void IndependentObserverRefusesEveryReceiptIdentityAuthorityRangeCountAndExpiredUtcSubstitution(string field)
    {
        var f = new Fixture(500, 501);
        _ = f.Observe();
        var property = typeof(GroupBatchTerminalReceiptRecord).GetProperty(field)!;
        object value = property.PropertyType == typeof(Guid) ? Guid.NewGuid()
            : property.PropertyType == typeof(long) ? (long)property.GetValue(f.Row)! + 1
            : property.PropertyType == typeof(int) ? (int)property.GetValue(f.Row)! + 1 : f.Claim.ExpiresAtUtc;
        property.SetValue(f.Row, value);
        Assert.Throws<InvalidOperationException>(() => f.Observe());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(24)]
    [InlineData(40)]
    [InlineData(56)]
    [InlineData(72)]
    [InlineData(88)]
    [InlineData(104)]
    [InlineData(112)]
    [InlineData(120)]
    [InlineData(128)]
    [InlineData(136)]
    [InlineData(137)]
    [InlineData(139)]
    [InlineData(140)]
    [InlineData(176)]
    [InlineData(177)]
    public void RecomputedHashDoesNotExcuseAChangedManifestMetadataField(int offset)
    {
        var f = new Fixture(500, 501);
        f.Row.Manifest[offset] ^= 1;
        f.Row.ManifestSha256 = SHA256.HashData(f.Row.Manifest);
        Assert.Throws<InvalidOperationException>(() => f.Observe());
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("length")]
    [InlineData("coverage")]
    [InlineData("dependency")]
    [InlineData("effect")]
    [InlineData("work-scope")]
    [InlineData("work-batch")]
    [InlineData("original-claim")]
    public void MissingEvidenceAndForeignOriginalRecordsAreRefused(string fault)
    {
        var f = new Fixture(500, 501);
        switch (fault)
        {
            case "digest": f.Row.ManifestSha256[0] ^= 1; break;
            case "length": f.Row.Manifest = [.. f.Row.Manifest, 0]; break;
            case "coverage": f.Row.Manifest.AsSpan(144, 32).Clear(); break;
            case "dependency": f.Row.Manifest.AsSpan(193, 32).Clear(); break;
            case "effect": f.Row.Manifest.AsSpan(225, 32).Clear(); break;
            case "work-scope": f.Work.BindingId = Guid.NewGuid(); break;
            case "work-batch": f.Work.BatchId = Guid.NewGuid(); break;
            case "original-claim": f.Claim.OperationId = Guid.NewGuid(); break;
        }
        if (fault is "coverage" or "dependency" or "effect") f.Row.ManifestSha256 = SHA256.HashData(f.Row.Manifest);
        Assert.Throws<InvalidOperationException>(() => f.Observe());
    }

    private sealed class Fixture
    {
        internal GroupBatchTerminalReceiptRecord Row { get; }
        internal GroupBatchAllocationRecord Allocation { get; }
        internal GroupWorkCommitReceiptRecord Work { get; }
        internal GroupBatchClaimReceiptRecord Claim { get; }
        private readonly Guid operation = Guid.NewGuid();
        internal Fixture(int raw, long observed)
        {
            var now = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
            Allocation = new()
            {
                TenantId = Guid.NewGuid(),
                CompanyId = Guid.NewGuid(),
                BindingId = Guid.NewGuid(),
                Id = Guid.NewGuid(),
                OperationId = Guid.NewGuid(),
                AfterSequence = 0,
                AllocatedThroughSequence = raw,
                ObservedCommittedThroughSequence = observed,
                RawRevisionCount = raw,
                AllocatedAtUtc = now
            };
            Work = new()
            {
                TenantId = Allocation.TenantId,
                CompanyId = Allocation.CompanyId,
                BindingId = Allocation.BindingId,
                BatchId = Allocation.Id,
                OperationId = Guid.NewGuid(),
                SelectedMessageCount = 2,
                NoteCount = 4,
                CommittedAtUtc = now.AddSeconds(1)
            };
            Claim = new()
            {
                TenantId = Allocation.TenantId,
                CompanyId = Allocation.CompanyId,
                BindingId = Allocation.BindingId,
                BatchId = Allocation.Id,
                OperationId = Guid.NewGuid(),
                OwnerId = Guid.NewGuid(),
                Epoch = 4,
                ServiceId = Guid.NewGuid(),
                CredentialEpoch = 1,
                GrantVersion = 2,
                SourceVersion = 3,
                DeletionGeneration = 0,
                AccountVersion = 5,
                IssuedAtUtc = now.AddSeconds(2),
                ExpiresAtUtc = now.AddMinutes(2)
            };
            Row = new()
            {
                TenantId = Allocation.TenantId,
                CompanyId = Allocation.CompanyId,
                BindingId = Allocation.BindingId,
                BatchId = Allocation.Id,
                OperationId = operation,
                ManifestVersion = 1,
                Manifest = new byte[257],
                AfterSequence = 0,
                ThroughSequence = raw,
                RawRevisionCount = raw,
                SelectedMessageCount = 2,
                ContributorCount = 1,
                NoteCount = 4,
                ClaimOperationId = Claim.OperationId,
                ClaimOwnerId = Claim.OwnerId,
                ClaimEpoch = Claim.Epoch,
                ServiceId = Claim.ServiceId,
                CredentialEpoch = Claim.CredentialEpoch,
                GrantVersion = Claim.GrantVersion,
                SourceVersion = Claim.SourceVersion,
                DeletionGeneration = Claim.DeletionGeneration,
                AccountVersion = Claim.AccountVersion,
                CommittedAtUtc = now.AddSeconds(3)
            };
            var bytes = Row.Manifest; "AIOGTRM1"u8.CopyTo(bytes);
            var ids = new[] { Row.TenantId, Row.CompanyId, Row.BindingId, Row.BatchId, Allocation.OperationId, operation };
            for (var index = 0; index < ids.Length; index++) ids[index].TryWriteBytes(bytes.AsSpan(8 + index * 16, 16));
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(104, 8), Row.AfterSequence);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(112, 8), raw);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(120, 8), observed);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(128, 8), Allocation.AllocatedAtUtc.UtcTicks);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(137, 2), (ushort)raw);
            bytes[139] = 2; BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(140, 4), 4); bytes.AsSpan(144, 32).Fill(1); bytes[176] = 1;
            Work.OperationId.TryWriteBytes(bytes.AsSpan(177, 16)); bytes.AsSpan(193, 64).Fill(2);
            Row.ManifestSha256 = SHA256.HashData(bytes);
        }
        internal string Observe() => GroupTerminalReceiptRuntimeProof.RequireValue(Row, Allocation, Work, Claim, operation);
    }
}
