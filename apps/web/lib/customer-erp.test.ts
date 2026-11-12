import { describe, expect, it } from "vitest";
import { erpMessages, isErpConnection, isErpServerSave, isErpServerSource, parseCustomerErpSources, parseErpServers, readOnlyLoginSql } from "./customer-erp";

const input = { host: "erp.example.test", port: 1433, database: "Demo", userName: "reader", password: "synthetic", encrypt: true, trustServerCertificate: false };
describe("customer ERP contracts", () => {
  it("accepts structured SQL fields and refuses raw strings, injected hosts and extra authority", () => {
    expect(isErpConnection(input)).toBe(true);
    for (const change of [{ host: "host;User ID=sa" }, { host: "tcp:host,1433" }, { port: 0 }, { port: 65536 }, { port: 1.5 }, { password: "" }, { connectionString: "Server=bad" }, { tenantId: "foreign" }])
      expect(isErpConnection({ ...input, ...change })).toBe(false);
    expect(isErpConnection("Server=bad")).toBe(false);
  });
  it("requires an operation id and customer name for saves", () => {
    expect(isErpConnection(input, true)).toBe(false);
    expect(isErpConnection({ ...input, operationId: "12345678-1234-1234-1234-123456789abc", logicalName: "Customer" }, true)).toBe(true);
    expect(isErpConnection({ ...input, operationId: "bad", logicalName: "Customer" }, true)).toBe(false);
  });
  it("projects only safe list metadata, preserving exact binding versions", () => {
    const source = { id: "12345678-1234-1234-1234-123456789abc", logicalName: "Customer", isEnabled: true, bindingVersion: "9223372036854775807" };
    expect(parseCustomerErpSources([{ ...source, password: "must-never-render" }])).toEqual([{ ...source, serverId: null, database: null }]);
    // A source on a shared server carries its server and database; a malformed link is refused.
    const server = "aaaaaaaa-1234-1234-1234-123456789abc";
    expect(parseCustomerErpSources([{ ...source, serverId: server, database: "Demo" }])).toEqual([{ ...source, serverId: server, database: "Demo" }]);
    expect(parseCustomerErpSources([{ ...source, serverId: "bad", database: "Demo" }])).toBeNull();
    expect(parseCustomerErpSources([{ ...source, serverId: server, database: "" }])).toBeNull();
    expect(parseCustomerErpSources([{ ...source, bindingVersion: 1 }])).toBeNull();
    expect(parseCustomerErpSources([source, source])).toBeNull();
  });
  it("validates shared server saves and source moves strictly", () => {
    const save = { operationId: "12345678-1234-1234-1234-123456789abc", name: "Máy chủ", host: "sql.example.test", port: 1433,
      userName: "reader", password: "synthetic", encrypt: true, trustServerCertificate: false };
    expect(isErpServerSave(save)).toBe(true);
    expect(isErpServerSave({ ...save, password: "" })).toBe(false);
    expect(isErpServerSave({ ...save, userName: "", password: "", version: "2" })).toBe(true);
    expect(isErpServerSave({ ...save, host: "sql;x" })).toBe(false);
    expect(isErpServerSave({ ...save, connectionString: "x" })).toBe(false);
    const move = { operationId: save.operationId, serverId: "aaaaaaaa-1234-1234-1234-123456789abc", database: "", bindingVersion: "1" };
    expect(isErpServerSource(move, false)).toBe(true);
    expect(isErpServerSource({ ...move, bindingVersion: undefined, logicalName: "Khách" }, true)).toBe(false);
    const { bindingVersion: _bindingVersion, ...create } = move;
    expect(isErpServerSource({ ...create, logicalName: "Khách", database: "Demo" }, true)).toBe(true);
    expect(isErpServerSource({ ...create, logicalName: "Khách", database: "" }, true)).toBe(false);
    expect(parseErpServers([{ id: move.serverId, name: "Máy chủ", host: "sql.example.test", port: 1433, userName: "reader", encrypt: true,
      trustServerCertificate: false, version: "1", sourceCount: 4, password: "must-never-render" }])).toEqual([{ id: move.serverId, name: "Máy chủ",
      host: "sql.example.test", port: 1433, userName: "reader", encrypt: true, trustServerCertificate: false, version: "1", sourceCount: 4 }]);
  });
  it("quotes database identifiers in the copyable script and never includes form passwords", () => {
    const script = readOnlyLoginSql("Demo]; DROP LOGIN x;--");
    expect(script).toContain("USE [Demo]]; DROP LOGIN x;--];");
    expect(script).toContain("GRANT VIEW ANY DEFINITION");
    expect(script).toContain("GRANT VIEW DEFINITION");
    expect(script).toContain("GRANT SELECT");
    expect(script).not.toContain(input.password);
  });
  it.each(["unreachable", "login_failed", "database_not_found", "not_read_only", "missing_view_definition", "ok"])("has a friendly message for %s", code => {
    expect(erpMessages[code]?.length).toBeGreaterThan(15);
  });
});
