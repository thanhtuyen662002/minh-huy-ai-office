import { expect, it } from "vitest";
import { runOutcome } from "./bot-channel-card";
import { parseBotChannel } from "../lib/bot-channels";
import { botView } from "../lib/bot-channel-test-fixture";

const at = "2026-11-02T03:15:00Z", since = "2026-11-02T01:30:00Z";
it("says what the latest summary run did", () => {
  expect(runOutcome({ at, trigger: "manual", since, generated: 0, delivered: 0, failed: 0 }).text).toBe("Tóm tắt ngay lúc 10:15: không có tin mới từ 08:30, không gửi gì.");
  expect(runOutcome({ at, trigger: "scheduled", since, generated: 2, delivered: 2, failed: 0 }).text).toBe("Lần tóm tắt lúc 10:15: đã gửi 2 bản tóm tắt.");
  expect(runOutcome({ at, trigger: "manual", since: null, generated: 3, delivered: 1, failed: 2 })).toEqual({ problem: true,
    text: "Tóm tắt ngay lúc 10:15: lỗi, không gửi được 2 bản tóm tắt (đã gửi 1). Xem nhật ký bot." });
  expect(runOutcome({ at, trigger: "manual", since, generated: 1, delivered: 0, failed: 0 }).text).toContain("đang chạy thử");
});
it("accepts a status with or without a run and refuses a malformed one", () => {
  const base = botView.channels[0];
  const lastRun = { at, trigger: "manual", since, generated: 0, delivered: 0, failed: 0 };
  expect(parseBotChannel({ ...base, status: { ...base.status, lastRun } })?.status?.lastRun).toEqual(lastRun);
  const { lastRun: _omitted, ...older } = base.status!;
  expect(parseBotChannel({ ...base, status: older })?.status?.lastRun).toBeNull();
  expect(parseBotChannel({ ...base, status: { ...base.status, lastRun: { ...lastRun, failed: -1 } } })).toBeNull();
  expect(parseBotChannel({ ...base, status: { ...base.status, lastRun: { ...lastRun, text: "x" } } })).toBeNull();
});
