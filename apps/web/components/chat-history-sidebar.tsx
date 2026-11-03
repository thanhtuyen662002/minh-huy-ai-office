"use client";
import { useEffect, useRef, useState } from "react";
import { Plus } from "lucide-react";
import { parseTaskHistoryDetail, parseTaskHistoryPage, type TaskHistoryDetail, type TaskHistoryItem, taskHistoryStatus } from "../lib/task-history";

type Props = { companyId: string; userId: string; generation: number; isCurrent: (generation: number) => boolean;
  validate: (generation: number) => Promise<boolean>; request: (generation: number, url: string, init?: RequestInit) => Promise<Response>;
  onUnauthorized: () => void; activeTaskId: string | null; refreshKey: number; disabled?: boolean;
  onOpen: (detail: TaskHistoryDetail) => void; onNew: () => void };

const PAGE = 25;
const dot = (status: number | null) => status === 6 ? "bg-emerald-500" : status === 5 ? "bg-rose-500" : "bg-amber-500";

function when(value: string) {
  const date = new Date(value), now = new Date();
  return date.toDateString() === now.toDateString()
    ? date.toLocaleTimeString("vi-VN", { hour: "2-digit", minute: "2-digit" })
    : date.toLocaleDateString("vi-VN", { day: "2-digit", month: "2-digit" });
}

/** Past Trợ lý conversations, newest first. Reads only; opening one never reruns the task. */
export function ChatHistorySidebar({ companyId, userId, generation, isCurrent, validate, request, onUnauthorized,
  activeTaskId, refreshKey, disabled, onOpen, onNew }: Props) {
  const [items, setItems] = useState<TaskHistoryItem[] | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [loading, setLoading] = useState(false);
  const [opening, setOpening] = useState<string | null>(null);
  const [error, setError] = useState("");
  const scope = `${companyId}:${userId}:${generation}`, scopeRef = useRef(scope); scopeRef.current = scope;
  const serial = useRef(0);
  const current = (value: number) => scopeRef.current === scope && serial.current === value && isCurrent(generation);

  async function list(offset: number) {
    const value = ++serial.current; setLoading(true); setError("");
    try {
      if (!await validate(generation) || !current(value)) return;
      const response = await request(generation, `/api/local/tasks?companyId=${encodeURIComponent(companyId)}&offset=${offset}&limit=${PAGE}`, { cache: "no-store" });
      if (!current(value)) return;
      if (response.status === 401) { onUnauthorized(); return; }
      if (!response.ok) throw new Error("Unavailable");
      const page = parseTaskHistoryPage(await response.json());
      if (!current(value)) return;
      if (!page || page.companyId !== companyId || page.offset !== offset) throw new Error("Invalid page");
      setItems(old => offset === 0 ? [...page.items] : [...(old ?? []), ...page.items.filter(item => !old?.some(seen => seen.taskId === item.taskId))]);
      setHasMore(page.hasMore && offset + PAGE <= 10000);
    } catch {
      if (current(value)) setError("Chưa tải được lịch sử.");
    } finally {
      if (current(value)) setLoading(false);
    }
  }

  async function open(taskId: string) {
    if (disabled || opening) return;
    const value = ++serial.current; setOpening(taskId); setError("");
    try {
      if (!await validate(generation) || !current(value)) return;
      const response = await request(generation, `/api/local/tasks/${encodeURIComponent(taskId)}/history?companyId=${encodeURIComponent(companyId)}`, { cache: "no-store" });
      if (!current(value)) return;
      if (response.status === 401) { onUnauthorized(); return; }
      if (!response.ok) throw new Error("Unavailable");
      const detail = parseTaskHistoryDetail(await response.json());
      if (!current(value)) return;
      if (!detail || detail.companyId !== companyId || detail.task.taskId !== taskId) throw new Error("Invalid detail");
      onOpen(detail);
    } catch {
      if (current(value)) setError("Chưa mở được cuộc trò chuyện này.");
    } finally {
      if (scopeRef.current === scope) setOpening(null);
      if (current(value)) setLoading(false);
    }
  }

  useEffect(() => {
    void list(0);
    // Company, user, session generation and a finished question own the list.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scope, refreshKey]);

  return <nav aria-label="Lịch sử trò chuyện" className="flex max-h-72 flex-col overflow-hidden rounded-xl border bg-card shadow-sm lg:max-h-[calc(100vh-8rem)]">
    <div className="border-b p-3">
      <button type="button" onClick={onNew} disabled={disabled}
        className="flex w-full items-center justify-center gap-2 rounded-lg bg-primary px-3 py-2 text-sm font-medium text-primary-foreground hover:opacity-90 disabled:opacity-50">
        <Plus className="size-4" aria-hidden="true" /> Cuộc trò chuyện mới
      </button>
    </div>
    <div className="flex-1 overflow-y-auto p-2">
      <p className="px-2 pb-1 pt-1 text-xs font-semibold uppercase tracking-[0.14em] text-muted-foreground">Gần đây</p>
      {items === null && loading ? <p className="px-2 py-3 text-sm text-muted-foreground">Đang tải…</p> : null}
      {items?.length === 0 ? <p className="px-2 py-3 text-sm text-muted-foreground">Chưa có câu hỏi nào.</p> : null}
      <ul className="space-y-0.5">
        {items?.map(item => {
          const active = activeTaskId?.toLowerCase() === item.taskId.toLowerCase();
          return <li key={item.taskId}>
            <button type="button" onClick={() => void open(item.taskId)} disabled={disabled || !!opening} aria-current={active ? "true" : undefined}
              title={taskHistoryStatus(item.status)}
              className={`w-full rounded-lg px-2 py-2 text-left text-sm transition-colors disabled:cursor-wait ${active ? "bg-accent text-accent-foreground" : "hover:bg-muted"}`}>
              <span className="line-clamp-2 break-words">{item.summary ?? "Câu hỏi đã lưu"}</span>
              <span className="mt-1 flex items-center gap-1.5 text-xs text-muted-foreground">
                <span aria-hidden="true" className={`size-1.5 rounded-full ${dot(item.status)}`} />
                {opening === item.taskId ? "Đang mở…" : `${taskHistoryStatus(item.status)} · ${when(item.createdAtUtc)}`}
              </span>
            </button>
          </li>;
        })}
      </ul>
      {hasMore ? <button type="button" disabled={loading} onClick={() => void list(items?.length ?? 0)}
        className="mt-2 w-full rounded-lg px-2 py-2 text-xs text-muted-foreground hover:bg-muted disabled:opacity-50">Xem thêm</button> : null}
      {error ? <p role="alert" className="px-2 py-2 text-xs text-rose-600">{error} <button type="button" className="underline" onClick={() => void list(0)}>Thử lại</button></p> : null}
    </div>
  </nav>;
}
