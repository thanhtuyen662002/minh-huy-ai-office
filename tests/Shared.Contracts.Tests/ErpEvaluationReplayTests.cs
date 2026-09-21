using MinhHuy.AIOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class ErpEvaluationReplayTests
{
    [Fact]
    public void Serialize_IsStableAcrossCaseOrdering()
    {
        var first = new[] { Case("b"), Case("a") };
        var second = first.Reverse().ToArray();

        Assert.Equal(ErpEvaluationReplay.Serialize(first), ErpEvaluationReplay.Serialize(second));
    }

    [Fact]
    public void Deserialize_RestoresCanonicalHistoricalDataset()
    {
        var payload = ErpEvaluationReplay.Serialize(new[] { Case("b"), Case("a") });

        var snapshot = ErpEvaluationReplay.Deserialize(payload);

        Assert.Equal("ledger-2026-09", snapshot.DatasetVersion);
        Assert.Equal(new[] { "a", "b" }, snapshot.Cases.Select(x => x.CaseId));
    }

    [Fact]
    public void Serialize_RejectsMixedDatasetVersions()
    {
        var cases = new[] { Case("a"), Case("b") with { DatasetVersion = "ledger-other" } };

        Assert.Throws<InvalidOperationException>(() => ErpEvaluationReplay.Serialize(cases));
    }

    [Fact]
    public void Deserialize_FailsClosedWhenDatasetAuthorityIsTampered()
    {
        var payload = ErpEvaluationReplay.Serialize(new[] { Case("a") })
            .Replace(
                "\"datasetVersion\":\"ledger-2026-09\",\"cases\"",
                "\"datasetVersion\":\"ledger-tampered\",\"cases\"",
                StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => ErpEvaluationReplay.Deserialize(payload));
    }

    [Fact]
    public void Deserialize_RejectsCrossTenantCaseMutation()
    {
        var payload = ErpEvaluationReplay.Serialize(new[] { Case("a") })
            .Replace("tenant-a", " tenant-a ", StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => ErpEvaluationReplay.Deserialize(payload));
    }

    private static ErpEvaluationCase Case(string id) =>
        new(id, "tenant-a", "company-a", $"input-{id}", $"output-{id}", "ledger-2026-09");
}
