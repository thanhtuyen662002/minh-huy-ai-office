using MinhHuy.AIOffice.Shared.Contracts.Erp;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class TaxInvoiceImportTests
{
    [Fact]
    public void Idempotency_key_is_stable_for_normalized_provider_and_document_identity()
    {
        var request = CreateRequest(provider: " tax-provider ", documentId: " inv-001 ");
        var equivalent = CreateRequest(provider: "TAX-PROVIDER", documentId: "INV-001");

        Assert.Equal(TaxInvoiceImportContract.GetIdempotencyKey(request), TaxInvoiceImportContract.GetIdempotencyKey(equivalent));
    }

    [Fact]
    public void Request_rejects_missing_opaque_secret_reference()
    {
        var request = CreateRequest(secretReference: "   ");

        Assert.Throws<ArgumentException>(() => TaxInvoiceImportContract.ValidateRequest(request));
    }

    [Fact]
    public void Result_rejects_cross_authority_scope()
    {
        var request = CreateRequest();
        var result = CreateResult(request) with { CompanyId = Guid.NewGuid() };

        Assert.Throws<InvalidOperationException>(() => TaxInvoiceImportContract.ValidateResult(request, result));
    }

    [Fact]
    public void Result_requires_evidence_and_matching_audit_correlation()
    {
        var request = CreateRequest();
        var missingEvidence = CreateResult(request) with { EvidenceReference = "" };
        var wrongAudit = CreateResult(request) with { AuditCorrelationId = "other-audit" };

        Assert.Throws<ArgumentException>(() => TaxInvoiceImportContract.ValidateResult(request, missingEvidence));
        Assert.Throws<InvalidOperationException>(() => TaxInvoiceImportContract.ValidateResult(request, wrongAudit));
    }

    [Fact]
    public void Result_rejects_contract_or_provider_identity_drift()
    {
        var request = CreateRequest();

        Assert.Throws<InvalidOperationException>(() => TaxInvoiceImportContract.ValidateResult(request, CreateResult(request) with { ContractVersion = "v2" }));
        Assert.Throws<InvalidOperationException>(() => TaxInvoiceImportContract.ValidateResult(request, CreateResult(request) with { Provider = "other-provider" }));
    }

    private static TaxInvoiceImportRequest CreateRequest(string provider = "tax-provider", string documentId = "INV-001", string secretReference = "secretref://tenant/tax-provider")
    {
        var authority = new TaxInvoiceImportAuthority(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            provider,
            "tax.invoice.import",
            "erp-2026.09",
            "schema-42",
            "v1");
        return new TaxInvoiceImportRequest(authority, documentId, DateTimeOffset.Parse("2026-09-22T00:00:00Z"), "VND", 100m, 10m, secretReference, "audit-001");
    }

    private static TaxInvoiceImportResult CreateResult(TaxInvoiceImportRequest request) => new(
        request.Authority.TenantId,
        request.Authority.CompanyId,
        request.Authority.DataSourceId,
        request.Authority.Provider,
        request.ExternalDocumentId,
        request.Authority.ContractVersion,
        "evidence://invoice/INV-001",
        request.AuditCorrelationId);
}
