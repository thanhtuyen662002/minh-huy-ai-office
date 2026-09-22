using MinhHuy.AiOffice.Shared.Contracts.Erp;
using Xunit;

namespace MinhHuy.AiOffice.Shared.Contracts.Tests;

public sealed class ErpSchemaSnapshotTests
{
    [Fact]
    public void Diff_IsDeterministicAndClassifiesAddRemoveAndMutation()
    {
        var previous = Snapshot(1,
            Object(ErpSchemaObjectKind.Table, "dbo", "Invoice", "h1"),
            Object(ErpSchemaObjectKind.View, "dbo", "InvoiceView", "h2"));
        var current = Snapshot(2,
            Object(ErpSchemaObjectKind.Function, "dbo", "TaxFn", "h4"),
            Object(ErpSchemaObjectKind.Table, "dbo", "Invoice", "h3"));

        var changes = ErpSchemaDiffer.Diff(previous, current);

        Assert.Collection(changes,
            change => Assert.Equal(ErpSchemaChangeKind.Additive, change.Kind),
            change => Assert.Equal(ErpSchemaChangeKind.Unknown, change.Kind),
            change => Assert.Equal(ErpSchemaChangeKind.Breaking, change.Kind));
    }

    [Fact]
    public void Diff_FailsClosedAcrossCompanyAuthority()
    {
        var previous = Snapshot(1, Object(ErpSchemaObjectKind.Table, "dbo", "Invoice", "h1"));
        var current = previous with { CompanyId = "company-b", Version = 2 };

        Assert.Throws<InvalidOperationException>(() => ErpSchemaDiffer.Diff(previous, current));
    }

    [Fact]
    public void Validate_RejectsDuplicateObjectIdentity()
    {
        var snapshot = Snapshot(1,
            Object(ErpSchemaObjectKind.Index, "dbo", "IX_Invoice", "h1"),
            Object(ErpSchemaObjectKind.Index, "dbo", "IX_Invoice", "h2"));

        Assert.Throws<InvalidOperationException>(() => snapshot.Validate());
    }

    [Fact]
    public void Diff_RequiresMonotonicVersion()
    {
        var previous = Snapshot(2, Object(ErpSchemaObjectKind.Table, "dbo", "Invoice", "h1"));
        var current = Snapshot(2, Object(ErpSchemaObjectKind.Table, "dbo", "Invoice", "h1"));

        Assert.Throws<InvalidOperationException>(() => ErpSchemaDiffer.Diff(previous, current));
    }

    private static ErpSchemaSnapshot Snapshot(long version, params ErpSchemaObject[] objects) =>
        new("tenant-a", "company-a", "sql-primary", version, objects);

    private static ErpSchemaObject Object(ErpSchemaObjectKind kind, string schema, string name, string hash) =>
        new(kind, schema, name, hash);
}
