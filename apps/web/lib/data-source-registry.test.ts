import { describe, expect, it } from "vitest";
import { applyMetadataEdit, createSecretRotationIntent, dataSourcesForCompany, demoDataSources } from "./data-source-registry";

describe("data source registry", () => {
  it("filters fixture state by active company and fails closed without company scope", () => {
    expect(dataSourcesForCompany("internal")).toHaveLength(2);
    expect(dataSourcesForCompany("")).toEqual([]);
    expect(dataSourcesForCompany("other")).toEqual([]);
  });

  it("preserves hidden secret state during metadata edits", () => {
    const current = demoDataSources[0];
    const edited = applyMetadataEdit(current, {
      logicalName: current.logicalName,
      kind: current.kind,
      environment: current.environment,
      purpose: "ERP đã cập nhật metadata",
      access: "read",
      maxConcurrency: 2,
      enabled: current.enabled,
    });
    expect(edited.secretConfigured).toBe(true);
    expect(edited.purpose).toContain("metadata");
    expect(JSON.stringify(edited)).not.toContain("password");
  });

  it("requires explicit non-empty secret rotation intent", () => {
    expect(createSecretRotationIntent("erp-production", "new-secret")).toEqual({ dataSourceId: "erp-production", replacementSecret: "new-secret" });
    expect(createSecretRotationIntent("erp-production", " ")).toBeNull();
  });

  it("uses sanitized connection-test copy without raw technical detail", () => {
    const warning = demoDataSources.find((source) => source.connectionStatus === "warning");
    expect(warning?.connectionMessage).toBe("Chưa thể kiểm tra kết nối. Hãy thử lại hoặc liên hệ quản trị hệ thống.");
    expect(warning?.connectionMessage).not.toMatch(/password|connection string|stack|exception/i);
  });
});
