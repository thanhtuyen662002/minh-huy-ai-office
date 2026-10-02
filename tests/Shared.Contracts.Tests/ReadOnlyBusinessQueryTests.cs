using MinhHuy.AiOffice.Shared.Contracts.Erp;
using Xunit;

namespace MinhHuy.AiOffice.Shared.Contracts.Tests;

public sealed class ReadOnlyBusinessQueryTests
{
    [Theory]
    [InlineData(ErpReadParameterKind.Text, "customer-a")]
    [InlineData(ErpReadParameterKind.Int64, "42")]
    [InlineData(ErpReadParameterKind.Decimal, "1234.50")]
    [InlineData(ErpReadParameterKind.Date, "2026-10-02")]
    [InlineData(ErpReadParameterKind.DateTimeOffset, "2026-10-02T09:00:00.0000000+00:00")]
    [InlineData(ErpReadParameterKind.Boolean, "true")]
    [InlineData(ErpReadParameterKind.Guid, "11111111-1111-1111-1111-111111111111")]
    public void Typed_parameter_values_accept_canonical_representations(
        ErpReadParameterKind kind,
        string value)
    {
        new ErpReadParameterValue(kind, value).Validate();
    }

    [Fact]
    public void Capability_request_contains_no_sql_authority()
    {
        var properties = typeof(ErpReadCapabilityRequest)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(properties, name =>
            name.Contains("sql", StringComparison.OrdinalIgnoreCase)
            || name.Contains("query", StringComparison.OrdinalIgnoreCase)
            || name.Contains("table", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Capability_request_rejects_noncanonical_capability_and_parameter_names()
    {
        var authority = (Tenant: Guid.NewGuid(), Company: Guid.NewGuid(), Source: Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => new ErpReadCapabilityRequest(
            authority.Tenant,
            authority.Company,
            authority.Source,
            "Inventory.Read",
            "1",
            new Dictionary<string, ErpReadParameterValue>())
            .Validate());

        Assert.Throws<ArgumentException>(() => new ErpReadCapabilityRequest(
            authority.Tenant,
            authority.Company,
            authority.Source,
            "inventory.read",
            "1",
            new Dictionary<string, ErpReadParameterValue>
            {
                ["CustomerId"] = new(ErpReadParameterKind.Text, "A")
            })
            .Validate());
    }

    [Fact]
    public void Evidence_rejects_global_bounds_and_duplicate_columns()
    {
        var authority = (Tenant: Guid.NewGuid(), Company: Guid.NewGuid(), Source: Guid.NewGuid());
        var duplicated = new ErpReadCapabilityEvidence(
            authority.Tenant,
            authority.Company,
            authority.Source,
            "inventory.read",
            "1",
            ["value", "value"],
            [],
            false,
            false,
            10);

        Assert.Throws<InvalidOperationException>(duplicated.Validate);

        var oversized = new ErpReadCapabilityEvidence(
            authority.Tenant,
            authority.Company,
            authority.Source,
            "inventory.read",
            "1",
            ["value"],
            [new ErpReadEvidenceRow([new string('x', ErpReadCapabilityEvidence.MaximumCellCharacters + 1)])],
            false,
            false,
            ErpReadCapabilityEvidence.MaximumCellCharacters + 1);

        Assert.Throws<InvalidOperationException>(oversized.Validate);
    }

    [Fact]
    public void Invalid_typed_parameter_fails_closed()
    {
        Assert.Throws<ArgumentException>(
            () => new ErpReadParameterValue(ErpReadParameterKind.Int64, "12.5").Validate());
        Assert.Throws<ArgumentException>(
            () => new ErpReadParameterValue(ErpReadParameterKind.Boolean, "TRUE").Validate());
        Assert.Throws<ArgumentException>(
            () => new ErpReadParameterValue(ErpReadParameterKind.Date, "02/10/2026").Validate());
    }
}
