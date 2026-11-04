import type { TaskHistoryItem } from "./task-history";

/**
 * Groups Trợ lý questions into conversations. Each question is its own task on the server; which
 * conversation a task belongs to is remembered in this browser (per company and user). A task with
 * no remembered conversation is a conversation of its own.
 */
export type ChatConversation = Readonly<{ id: string; items: readonly TaskHistoryItem[]; title: string; latest: TaskHistoryItem }>;

const PREFIX = "aioffice-chat-conversations:";
const LIMIT = 2000;

function key(scope: string) { return PREFIX + scope; }

export function readConversationMap(scope: string): Record<string, string> {
  try {
    const raw = window.localStorage.getItem(key(scope));
    const value: unknown = raw ? JSON.parse(raw) : null;
    if (!value || typeof value !== "object" || Array.isArray(value)) return {};
    const map: Record<string, string> = {};
    for (const [task, conversation] of Object.entries(value)) if (typeof conversation === "string") map[task.toLowerCase()] = conversation.toLowerCase();
    return map;
  } catch { return {}; }
}

export function rememberConversation(scope: string, taskId: string, conversationId: string) {
  try {
    const map = readConversationMap(scope);
    map[taskId.toLowerCase()] = conversationId.toLowerCase();
    const entries = Object.entries(map);
    window.localStorage.setItem(key(scope), JSON.stringify(Object.fromEntries(entries.slice(Math.max(0, entries.length - LIMIT)))));
  } catch { /* Storage may be unavailable; the question then stays its own conversation. */ }
}

export function conversationOf(map: Record<string, string>, taskId: string) {
  return map[taskId.toLowerCase()] ?? taskId.toLowerCase();
}

/** Newest conversation first; questions inside a conversation oldest first. */
export function groupConversations(items: readonly TaskHistoryItem[], map: Record<string, string>): ChatConversation[] {
  const groups = new Map<string, TaskHistoryItem[]>();
  for (const item of items) {
    const id = conversationOf(map, item.taskId);
    groups.set(id, [...(groups.get(id) ?? []), item]);
  }
  return [...groups.entries()].map(([id, members]) => {
    const ordered = [...members].sort((a, b) => Date.parse(a.createdAtUtc) - Date.parse(b.createdAtUtc));
    return { id, items: ordered, title: ordered[0].summary ?? "Câu hỏi đã lưu", latest: ordered[ordered.length - 1] };
  }).sort((a, b) => Date.parse(b.latest.createdAtUtc) - Date.parse(a.latest.createdAtUtc));
}
