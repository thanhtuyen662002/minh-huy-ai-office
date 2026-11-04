import { afterEach, expect, it } from "vitest";
import { conversationOf, groupConversations, readConversationMap, rememberConversation } from "./chat-conversations";
const item = (n: number, minute: number) => ({ taskId: `T${n}`, status: 6, createdAtUtc: `2026-10-09T00:${String(minute).padStart(2, "0")}:00Z`,
  updatedAtUtc: `2026-10-09T00:${String(minute).padStart(2, "0")}:01Z`, summary: `Q${n}`, metadataUnavailable: false });
afterEach(() => window.localStorage.clear());

it("remembers and groups tasks per company and user", () => {
  rememberConversation("c:u", "T2", "T1"); rememberConversation("c:u", "T3", "T1");
  expect(readConversationMap("c:other")).toEqual({});
  const map = readConversationMap("c:u"); expect(conversationOf(map, "T9")).toBe("t9");
  const groups = groupConversations([item(3, 30), item(4, 20), item(2, 10), item(1, 5)], map);
  expect(groups.map(group => group.id)).toEqual(["t1", "t4"]);
  expect(groups[0].items.map(entry => entry.taskId)).toEqual(["T1", "T2", "T3"]); expect(groups[0].title).toBe("Q1");
});

it("survives broken storage", () => {
  window.localStorage.setItem("aioffice-chat-conversations:c:u", "not json");
  expect(readConversationMap("c:u")).toEqual({});
});
