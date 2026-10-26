import type { BotAdminView } from "./bot-channels";
export const botCompany = "60000000-0000-4000-8000-000000000001";
export const botUser = "60000000-0000-4000-8000-000000000002";
export const botView: BotAdminView = { enabled: true, ai: { baseUrl: "https://ai.example.test/v1", model: "model-a", apiKeyConfigured: true, version: "1" },
  channels: ["zalo", "whatsapp"].map(channel => ({ channel: channel as "zalo" | "whatsapp", enabled: false, dryRun: true, summarySchedule: ["08:00", "17:30"],
    timeZone: "Asia/Ho_Chi_Minh", techGroupExternalId: "tech", customerGroups: [{ externalId: "customer", displayName: "Khách A", customerName: "Công ty A" }], configVersion: "1",
    status: { loginState: "logged_in", groups: [{ externalId: "tech", displayName: "Kỹ thuật" }, { externalId: "customer", displayName: "Khách A" }, { externalId: "second", displayName: "Khách B" }],
      lastSummaryAt: "2026-10-26T01:00:00Z", receivedAt: "2026-10-26T02:00:00Z", lastError: null } })) };
export const botPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGD4DwABBAEAX+XDSwAAAABJRU5ErkJggg==";
