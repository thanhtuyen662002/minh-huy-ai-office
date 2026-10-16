extern alias GroupReferenceProof;

using System.Buffers.Binary;
using GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedManifestRuntimeObservationTests
{
    [Fact]
    public void ObserverPinsCompleteOriginalPrivateMetadataAndCanonicalSources()
    {
        var (receipt, selected) = Values();
        var hash = GroupAutomaticManifestRuntimeProof.RequireValue(receipt, 500, selected);
        Assert.Matches("^[0-9A-F]{64}$", hash);
        Assert.Equal(hash, GroupAutomaticManifestRuntimeProof.RequireValue(receipt, 500, selected.Reverse().ToArray()));
        receipt.DependencyManifest![186] ^= 1;
        Assert.NotEqual(hash, GroupAutomaticManifestRuntimeProof.RequireValue(receipt, 500, selected));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("length")]
    [InlineData("magic")]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("binding")]
    [InlineData("batch")]
    [InlineData("operation")]
    [InlineData("cutoff")]
    [InlineData("authority")]
    [InlineData("coverage")]
    [InlineData("source-count")]
    [InlineData("brain-count")]
    [InlineData("message")]
    [InlineData("revision")]
    [InlineData("source-hash")]
    [InlineData("duplicate")]
    [InlineData("foreign-disposition")]
    [InlineData("empty-disposition")]
    [InlineData("selection-count")]
    [InlineData("extra")]
    public void ObserverRefusesIncompleteForeignOrMalformedReceiptMetadata(string change)
    {
        var (receipt, selected) = Values(); var value = receipt.DependencyManifest!;
        switch (change)
        {
            case "missing": receipt.DependencyManifest = null; break;
            case "version": receipt.DependencyManifestVersion = 0; break;
            case "length": receipt.DependencyManifest = value[..^1]; break;
            case "magic": value[0] ^= 1; break;
            case "tenant": value[8] ^= 1; break;
            case "company": value[24] ^= 1; break;
            case "binding": value[40] ^= 1; break;
            case "batch": value[56] ^= 1; break;
            case "operation": value[72] ^= 1; break;
            case "cutoff": value[88] ^= 1; break;
            case "authority": value.AsSpan(96, 32).Clear(); break;
            case "coverage": value.AsSpan(128, 32).Clear(); break;
            case "source-count": value[160] = 1; break;
            case "brain-count": value[161] = 1; break;
            case "message": value[162] ^= 1; break;
            case "revision": value[178] ^= 1; break;
            case "source-hash": value.AsSpan(186, 32).Clear(); break;
            case "duplicate": selected[1].MessageId = selected[0].MessageId; break;
            case "foreign-disposition": selected[1].CompanyId = Guid.NewGuid(); break;
            case "empty-disposition": selected[1].MessageRevision = 0; break;
            case "selection-count": receipt.SelectedMessageCount = 1; break;
            case "extra": selected = [.. selected, new()]; break;
            default: throw new InvalidOperationException();
        }
        Assert.Throws<InvalidOperationException>(() => GroupAutomaticManifestRuntimeProof.RequireValue(receipt, 500, selected));
    }

    private static (GroupWorkCommitReceiptRecord Receipt, GroupWorkSourceDispositionRecord[] Selected) Values()
    {
        var receipt = new GroupWorkCommitReceiptRecord
        {
            TenantId = Guid.NewGuid(),
            CompanyId = Guid.NewGuid(),
            BindingId = Guid.NewGuid(),
            BatchId = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            SelectedMessageCount = 2,
            DependencyManifestVersion = 1,
            DependencyManifest = new byte[274]
        };
        var selected = Enumerable.Range(0, 2).Select(index => new GroupWorkSourceDispositionRecord
        {
            TenantId = receipt.TenantId,
            CompanyId = receipt.CompanyId,
            BindingId = receipt.BindingId,
            BatchId = receipt.BatchId,
            OperationId = receipt.OperationId,
            MessageId = Guid.NewGuid(),
            MessageRevision = index == 0 ? 499 : 1
        }).OrderBy(x => x.MessageId).ToArray();
        var value = receipt.DependencyManifest; "AIOGDEP1"u8.CopyTo(value);
        var ids = new[] { receipt.TenantId, receipt.CompanyId, receipt.BindingId, receipt.BatchId, receipt.OperationId };
        for (var index = 0; index < ids.Length; index++) ids[index].TryWriteBytes(value.AsSpan(8 + index * 16, 16));
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(88, 8), 500);
        value.AsSpan(96, 64).Fill(1); value[160] = 2;
        for (var index = 0; index < selected.Length; index++)
        {
            var offset = 162 + index * 56;
            selected[index].MessageId.TryWriteBytes(value.AsSpan(offset, 16));
            BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(offset + 16, 8), selected[index].MessageRevision);
            value.AsSpan(offset + 24, 32).Fill(2);
        }
        return (receipt, selected);
    }
}
