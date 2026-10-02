import { describe, expect, it } from "vitest";
import {
  parseAiCheckpoint,
  parseAuthContext,
  parseDataSources,
  parseTaskSnapshot,
} from "./local-ai-workspace";

describe("local AI workspace parsers", () => {
  it("accepts authoritative auth and read-only data-source payloads", () => {
    expect(parseAuthContext({
      tenantId: "11111111-1111-1111-1111-111111111111",
      companyId: "22222222-2222-2222-2222-222222222222",
      userId: "33333333-3333-3333-3333-333333333333",
      roles: ["admin"],
    })?.roles).toEqual(["admin"]);

    expect(parseDataSources([{
      id: "c6488985-c602-43e6-b000-8f85ae374d61",
      logicalName: "erp.local-pilot",
      kind: "sqlserver",
      environment: "local",
      purpose: "read-only local pilot",
      allowRead: true,
      allowWrite: false,
      maxConcurrency: 1,
      isEnabled: true,
    }])?.[0]?.logicalName).toBe("erp.local-pilot");
  });

  it("parses grounded AI checkpoints and rejects malformed status payloads", () => {
    const checkpoint = parseAiCheckpoint(JSON.stringify({
      answer: "DATABASE=AIOffice_Pilot_CleanTest; TABLE_COUNT=19",
      provider: "openai-direct",
      model: "gpt-6-sol",
      usage: { inputTokens: 440, outputTokens: 19, totalTokens: 459 },
      evidence: "ai-provider-reasoning-after-bounded-read-only-erp-catalog",
      erpEvidence: {
        databaseName: "AIOffice_Pilot_CleanTest",
        tableCount: 19,
        sampledTableCount: 19,
        topTables: [{
          schema: "aioffice",
          table: "__EFMigrationsHistory",
          approximateRowCount: 12,
        }],
      },
    }));

    expect(checkpoint?.provider).toBe("openai-direct");
    expect(checkpoint?.erpTableCount).toBe(19);
    expect(checkpoint?.totalTokens).toBe(459);

    expect(parseTaskSnapshot({
      taskId: "f7a8976a-a763-0255-9e0b-d8c2094bff09",
      taskStatus: 6,
      stepStatus: 5,
      dispatchState: 2,
      attempt: 1,
      resultPayloadJson: "{}",
      failureReason: null,
    })?.attempt).toBe(1);

    expect(parseTaskSnapshot({ taskId: "x" })).toBeNull();
  });
});
