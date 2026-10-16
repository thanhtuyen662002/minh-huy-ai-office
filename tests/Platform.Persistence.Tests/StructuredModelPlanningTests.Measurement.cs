extern alias RuntimeWorker;

using MinhHuyAiOffice.Shared.Contracts;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class StructuredModelPlanningTests
{
    [Fact]
    public async Task PlanningMatchesActualSameGateMeasurementWithoutExecutingAnyProvider()
    {
        var f = new Fixture(); var planning = await f.Gate.DescribePlanningAsync(Schema);
        // A separate counter delegate admits the complete input too, on the
        // SAME Gateway/catalog. This is synthetic metadata, not model quality.
        var gate = new StructuredModelAdmission(f.Gateway, f.Catalog,
            new BoundedStructuredTextTokenCounter((_, _) => 10, TimeSpan.FromSeconds(1)));
        var prepared = await gate.PrepareAsync(new("tenant", "company", "task", "request", AiCapability.StructuredGeneration, "Tồn kho", Schema));
        var actual = gate.DescribePrepared(prepared);
        Assert.True(planning.MatchesMeasurement(actual));
        var rebuilt = AiStructuredPlanningMetadata.Create(actual.GatewaySha256, actual.SchemaSha256, actual.TokenizerVersion,
            actual.Routes[0].ConfiguredModel, 3814, actual.SchemaTokens, actual.Routes.Select(x => x.CandidateSha256).ToArray());
        Assert.Equal(planning.ProfileSetSha256, rebuilt.ProfileSetSha256);
        Assert.All(f.Adapters, x => Assert.Equal(0, x.Executions));
        f.Catalog.RevokeForSyntheticControls(f.Profiles[1].CandidateSha256);
        // Historical matching remains pure; the actual gate independently
        // refuses the revoked current profile. Matching is not authority.
        Assert.True(planning.MatchesMeasurement(actual));
        Assert.Throws<InvalidOperationException>(() => gate.DescribePrepared(prepared));
    }

    [Theory]
    [InlineData("gateway")]
    [InlineData("schema")]
    [InlineData("tokenizer")]
    [InlineData("primary-model")]
    [InlineData("primary-candidate")]
    [InlineData("backup-candidate")]
    [InlineData("backup-framing")]
    [InlineData("backup-window")]
    [InlineData("schema-count")]
    [InlineData("route-removed")]
    public async Task PlanningRefusesDifferentMeasurementAcrossBothRoutes(string fault)
    {
        var f = new Fixture(); var planning = await f.Gate.DescribePlanningAsync(Schema);
        var gate = new StructuredModelAdmission(f.Gateway, f.Catalog,
            new BoundedStructuredTextTokenCounter((_, _) => 10, TimeSpan.FromSeconds(1)));
        var prepared = await gate.PrepareAsync(new("tenant", "company", "task", "request", AiCapability.StructuredGeneration, "Tồn kho", Schema));
        var before = gate.DescribePrepared(prepared); var schemaTokens = before.SchemaTokens + (fault == "schema-count" ? 1 : 0);
        var routes = before.Routes.Take(fault == "route-removed" ? 1 : 2).Select(r =>
        {
            var framing = r.FramingReserveTokens + (fault == "backup-framing" && r.Ordinal == 1 ? 1 : 0);
            return new AiStructuredAdmissionRoute(r.Ordinal, r.ProviderId,
                fault == "primary-model" && r.Ordinal == 0 ? "different" : r.ConfiguredModel, r.EndpointSha256,
                fault == (r.Ordinal == 0 ? "primary-candidate" : "backup-candidate") ? new('E', 64) : r.CandidateSha256,
                r.EvidenceSha256, r.ContextWindowTokens - (fault == "backup-window" && r.Ordinal == 1 ? 1 : 0), framing,
                r.MaxOutputTokens, before.InputTokens + schemaTokens + framing, r.EchoedModels);
        }).ToArray();
        var changed = new AiStructuredAdmissionSnapshot(fault == "gateway" ? new('F', 64) : before.GatewaySha256,
            before.WholeOperationTimeoutTicks, before.InputSha256, before.InputBytes, fault == "schema" ? new('F', 64) : before.SchemaSha256,
            before.SchemaBytes, fault == "tokenizer" ? "different" : before.TokenizerVersion, before.InputTokens, schemaTokens, routes);
        Assert.False(planning.MatchesMeasurement(changed)); Assert.All(f.Adapters, x => Assert.Equal(0, x.Executions));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("too-many")]
    [InlineData("invalid-hash")]
    [InlineData("schema-count")]
    public void CommonPlanningFingerprintRejectsInvalidMetadataWithFixedError(string fault)
    {
        string[] hashes = fault switch
        {
            "empty" => [],
            "too-many" => [new('A', 64), new('B', 64), new('C', 64)],
            "invalid-hash" => ["PRIVATE_CANDIDATE_CANARY"],
            _ => [new('A', 64)]
        };
        var error = Assert.Throws<ArgumentException>(() => AiStructuredPlanningMetadata.Create(new('A', 64), new('B', 64), "counter", "model",
            100, fault == "schema-count" ? 0 : 10, hashes));
        Assert.Equal("Structured planning metadata is invalid.", error.Message); Assert.Null(error.InnerException);
    }
}
