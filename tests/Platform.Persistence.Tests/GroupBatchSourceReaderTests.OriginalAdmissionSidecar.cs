using System.Security.Cryptography;
using System.Text;
using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmissionSidecarRetainsExactOriginalPlanClaimAndExplicitModelOrHostChunk(bool hostOnly)
    {
        using var f = new Fixture();
        var (preparation, brain, selection) = await IssuedDisclosure(f, hostOnly ? "password=OWNED_ADMISSION_CANARY" : "Tra cứu tồn kho 😀");
        var original = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, hostOnly ? null : "model", hostOnly ? 0 : 64000, []);
        var restored = Assert.Single(original.RestoreChunks(preparation, brain, PlanProfile, []));
        var snapshot = hostOnly ? null : SidecarSnapshot(restored.Current.ModelContext!.CreateGatewayRequest(Guid.NewGuid()));
        var metadata = GroupOriginalAdmissionSidecar.Create(original, preparation.Context.Handle, [snapshot]);
        var bytes = metadata.Encode(); var decoded = GroupOriginalAdmissionSidecar.Read(bytes);
        decoded.RequirePlan(original);
        Assert.Equal(original.Sha256, decoded.PlanSha256); Assert.Equal(preparation.Scope, decoded.Scope);
        Assert.Equal(preparation.Context.Handle.Receipt.OperationId, decoded.OriginalClaimOperationId);
        Assert.Equal(preparation.Context.Handle.Receipt.Epoch, decoded.OriginalClaimEpoch);
        var entry = Assert.Single(decoded.Entries);
        Assert.Equal(original.Chunks[0].OperationId, entry.OperationId);
        Assert.Equal(original.Chunks[0].InputSha256, entry.OriginalInputIdentity);
        Assert.Equal(snapshot?.SnapshotSha256, entry.Snapshot?.SnapshotSha256);
        if (snapshot is not null) Assert.NotEqual(entry.OriginalInputIdentity, snapshot.InputSha256);
        else Assert.Equal(211, bytes.Length);
        Assert.Equal(bytes, decoded.Encode()); bytes[0] ^= 1;
        Assert.Equal(metadata.Sha256, decoded.Sha256);
        Assert.DoesNotContain("OWNED_ADMISSION_CANARY", decoded.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Tra cứu tồn kho", Encoding.UTF8.GetString(decoded.Encode()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("host-tag")]
    [InlineData("different-plan")]
    [InlineData("legacy")]
    public async Task AdmissionSidecarRejectsChangedPlanAndIncompleteOrMislabelledCoverage(string change)
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f);
        var original = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []);
        var request = Assert.Single(original.RestoreChunks(preparation, brain, PlanProfile, [])).Current.ModelContext!.CreateGatewayRequest(Guid.NewGuid());
        var snapshot = SidecarSnapshot(request);
        if (change == "different-plan")
        {
            var metadata = GroupOriginalAdmissionSidecar.Create(original, preparation.Context.Handle, [snapshot]);
            var changed = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, new('B', 64), "model", 64000, []);
            Assert.Throws<InvalidOperationException>(() => metadata.RequirePlan(changed));
        }
        else
        {
            if (change == "legacy") original = GroupAutomaticInputPlan.Create(preparation, brain, PlanProfile, "model", 64000, []);
            Assert.Throws<InvalidOperationException>(() => GroupOriginalAdmissionSidecar.Create(original, preparation.Context.Handle,
                change == "missing" ? [] : change == "extra" ? [snapshot, snapshot] : change == "host-tag" ? [null] : [snapshot]));
        }
    }

    [Theory]
    [InlineData("version")]
    [InlineData("scope")]
    [InlineData("count")]
    [InlineData("ordinal")]
    [InlineData("tag")]
    [InlineData("length")]
    [InlineData("nested-snapshot")]
    [InlineData("trailing")]
    public async Task AdmissionSidecarClosedReaderRefusesResignedStructuralCorruption(string change)
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f);
        var original = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []);
        var request = Assert.Single(original.RestoreChunks(preparation, brain, PlanProfile, [])).Current.ModelContext!.CreateGatewayRequest(Guid.NewGuid());
        var sidecar = GroupOriginalAdmissionSidecar.Create(original, preparation.Context.Handle, [SidecarSnapshot(request)]);
        var body = sidecar.Encode()[..^32];
        if (change == "version") body[7] = (byte)'2';
        if (change == "scope") Array.Clear(body, 8, 16);
        if (change == "count") body[128] = 101;
        if (change == "ordinal") body[129] = 2;
        if (change == "tag") body[178] = 2;
        if (change == "length") BitConverter.GetBytes(int.MaxValue).CopyTo(body, 179);
        if (change == "nested-snapshot")
        {
            var length = BitConverter.ToInt32(body, 179); body[190] = (byte)'2';
            SHA256.HashData(body.AsSpan(183, length - 32)).CopyTo(body, 183 + length - 32);
        }
        if (change == "trailing") body = [.. body, 0];
        var error = Assert.Throws<InvalidOperationException>(() => GroupOriginalAdmissionSidecar.Read([.. body, .. SHA256.HashData(body)]));
        Assert.Equal("Group original admission provenance is not available.", error.Message); Assert.Null(error.InnerException);
    }

    private static AiStructuredAdmissionSnapshot SidecarSnapshot(AiGatewayRequest request)
    {
        static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var route = new AiStructuredAdmissionRoute(0, "synthetic", "model", new('B', 64), new('C', 64), new('D', 64), 10000, 2, 8, 4, ["echo"]);
        return new(new('A', 64), TimeSpan.FromSeconds(60).Ticks, Hash(request.Input), Encoding.UTF8.GetByteCount(request.Input),
            Hash(request.ResponseSchema!), Encoding.UTF8.GetByteCount(request.ResponseSchema!), "synthetic-count-version", 1, 1, [route]);
    }
}
