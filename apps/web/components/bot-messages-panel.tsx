"use client";
import { useEffect, useRef, useState } from "react";
import { ImageIcon, RefreshCw } from "lucide-react";
import { BotMessage, BotMessageGroup, parseBotMessageGroups, parseBotMessagePage } from "../lib/bot-messages";
import { SkeletonRows } from "./skeleton";

type Props = { companyId: string; userId: string; generation: number;
  isCurrent: (generation: number) => boolean; validate: (generation: number) => Promise<boolean>;
  request: (generation: number, url: string, init?: RequestInit) => Promise<Response>; onUnauthorized: () => void };
type Selected = { channel: string; externalId: string };
type State = { scope: string; groups: BotMessageGroup[] | null; selected: Selected | null; messages: BotMessage[] | null;
  hasMore: boolean; loading: boolean; error: string };
const empty = (scope: string): State => ({ scope, groups: null, selected: null, messages: null, hasMore: false, loading: false, error: "" });
const PAGE = 50;
const smallButton = "inline-flex h-8 items-center justify-center rounded-lg border bg-card px-2.5 text-xs font-medium hover:bg-muted disabled:pointer-events-none disabled:opacity-50";
const iconButton = "inline-flex size-8 items-center justify-center rounded-lg text-muted-foreground hover:bg-muted hover:text-foreground";
const time = (value: string) => new Date(value).toLocaleString("vi-VN", { hour: "2-digit", minute: "2-digit", day: "2-digit", month: "2-digit" });
const channelName = (channel: string) => channel === "whatsapp" ? "WhatsApp" : "Zalo";
const same = (a: Selected | null, b: Selected) => a?.channel === b.channel && a.externalId === b.externalId;
// Oldest first, as in a chat; a refreshed newest page merges with older pages already loaded.
const merge = (old: BotMessage[], next: BotMessage[]) => [...new Map([...old, ...next].map(m => [m.messageId, m])).values()]
  .sort((a, b) => Date.parse(a.sentAt) - Date.parse(b.sentAt) || a.messageId.localeCompare(b.messageId));

export function BotMessagesPanel({ companyId, userId, generation, isCurrent, validate, request, onUnauthorized }: Props) {
  const scope = `${companyId}:${userId}:${generation}`;
  const [state, setState] = useState<State>(() => empty(scope));
  const live = useRef(scope); live.current = scope;
  const latest = useRef(state); latest.current = state;
  const mounted = useRef(false), serial = useRef(0), controller = useRef<AbortController | null>(null);
  const bottom = useRef<HTMLDivElement | null>(null), stick = useRef(true);
  const current = (value: number) => mounted.current && live.current === scope && serial.current === value && isCurrent(generation);

  async function get(value: number, url: string, signal: AbortSignal): Promise<unknown | "unauthorized" | number> {
    // A failed session check already resets the workspace; nothing from this read is shown.
    if (!await validate(generation) || !current(value)) return 0;
    const response = await request(generation, url, { cache: "no-store", signal });
    if (!current(value)) return 0;
    if (response.status === 401) return "unauthorized";
    if (!response.ok) { void response.body?.cancel().catch(() => {}); return response.status; }
    return response.json();
  }

  // One read at a time: the group list, then (when a group is open) its newest page or an older page.
  async function load(selected: Selected | null, older = false) {
    if (!mounted.current || !isCurrent(generation)) return;
    serial.current++; controller.current?.abort();
    const value = serial.current, abort = new AbortController(); controller.current = abort;
    const before = latest.current.scope === scope && same(latest.current.selected, selected ?? { channel: "", externalId: "" }) ? latest.current.messages : null;
    if (!older) stick.current = true;
    setState(old => ({ ...(old.scope === scope ? old : empty(scope)), selected, loading: true, error: "",
      ...(selected && !same(old.selected, selected) ? { messages: null, hasMore: false } : {}) }));
    const fail = (status: number | "unauthorized") => {
      if (!current(value)) return;
      if (status === "unauthorized") { setState(empty(scope)); onUnauthorized(); return; }
      setState(old => ({ ...old, loading: false, error: status === 403 ? "Bạn không còn quyền xem tin nhắn của công ty này."
        : status === 404 && !selected ? "Kênh bot Zalo/WhatsApp chưa được bật trên máy chủ."
        : status === 404 ? "Nhóm này không còn được chọn ở Kết nối. Hãy tải lại danh sách." : "Chưa đọc được tin nhắn. Hãy thử lại sau." }));
    };
    try {
      const query = `companyId=${encodeURIComponent(companyId)}`;
      if (!older) {
        const raw = await get(value, `/api/local/bot-messages?${query}`, abort.signal);
        if (!current(value)) return;
        if (raw === "unauthorized" || typeof raw === "number") { fail(raw); return; }
        const groups = parseBotMessageGroups(raw); if (!groups) { fail(502); return; }
        const still = selected && groups.some(g => same(selected, g)) ? selected : null;
        setState(old => ({ ...old, groups, selected: still, ...(still ? {} : { messages: null, hasMore: false }), loading: !!still }));
        if (!still) return;
      }
      const sel = selected!;
      const oldest = older && before?.length ? `&before=${encodeURIComponent(before[0].sentAt)}` : "";
      const raw = await get(value, `/api/local/bot-messages/${sel.channel}?${query}&group=${encodeURIComponent(sel.externalId)}&limit=${PAGE}${oldest}`, abort.signal);
      if (!current(value)) return;
      if (raw === "unauthorized" || typeof raw === "number") { fail(raw); return; }
      const page = parseBotMessagePage(raw, PAGE); if (!page) { fail(502); return; }
      setState(old => ({ ...old, loading: false, error: "", messages: merge(before ?? [], page.items),
        hasMore: older || !before?.length ? page.hasMore : old.hasMore }));
    } catch { fail(503); }
    finally { if (current(value)) { controller.current = null; setState(old => old.loading ? { ...old, loading: false } : old); } }
  }
  const loadLatest = useRef(load); loadLatest.current = load;

  useEffect(() => {
    mounted.current = true; setState(empty(scope));
    void loadLatest.current(null);
    const refresh = () => { if (!document.hidden) void loadLatest.current(latest.current.selected); };
    const timer = setInterval(refresh, 30000);
    window.addEventListener("focus", refresh); document.addEventListener("visibilitychange", refresh);
    return () => { mounted.current = false; serial.current++; controller.current?.abort(); clearInterval(timer);
      window.removeEventListener("focus", refresh); document.removeEventListener("visibilitychange", refresh); };
    // Only the authenticated scope owns this panel; event callbacks read the latest props.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scope]);
  const visible = state.scope === scope ? state : empty(scope);
  const count = visible.messages?.length ?? 0;
  useEffect(() => { if (stick.current && count) bottom.current?.scrollIntoView?.({ block: "end" }); }, [count, visible.selected]);
  const group = visible.groups?.find(g => same(visible.selected, g));
  const pane = "flex min-h-0 min-w-0 flex-col";

  return <section aria-label="Tin nhắn nhóm khách" aria-busy={visible.loading}
    className="grid min-w-0 max-w-full overflow-hidden rounded-xl border bg-card shadow-sm lg:h-[calc(100vh-13rem)] lg:min-h-[32rem] lg:grid-cols-[19rem_minmax(0,1fr)]">
    <div className={`${pane} border-b lg:border-b-0 lg:border-r`}>
      <div className="flex items-center justify-between gap-2 border-b px-4 py-3">
        <h2 className="font-semibold">Nhóm khách</h2>
        <button type="button" className={iconButton} aria-label="Tải lại tin nhắn" title="Tải lại tin nhắn"
          onClick={() => void load(visible.selected)}><RefreshCw className={`size-4 ${visible.loading ? "animate-spin" : ""}`} aria-hidden="true" /></button>
      </div>
      <div className="max-h-72 min-h-0 flex-1 overflow-y-auto lg:max-h-none">
        {visible.groups === null && visible.loading ? <div className="p-4"><SkeletonRows label="Đang tải nhóm…" rows={4} /></div> : null}
        {visible.groups?.length === 0 ? <p className="px-4 py-6 text-sm text-muted-foreground">Chưa chọn nhóm khách nào. Vào Kết nối → Kênh &amp; Bot để chọn nhóm bot theo dõi.</p> : null}
        {visible.groups?.length ? <ul className="m-0 list-none p-0" aria-label="Nhóm bot đang theo dõi">
          {visible.groups.map(g => {
            const active = same(visible.selected, g);
            return <li key={`${g.channel}:${g.externalId}`} className="min-w-0">
              <button type="button" aria-pressed={active} aria-label={g.displayName} onClick={() => void load({ channel: g.channel, externalId: g.externalId })}
                className={`flex w-full min-w-0 items-center gap-3 border-l-2 px-4 py-3 text-left text-sm hover:bg-muted/60 ${active ? "border-primary bg-muted" : "border-transparent"}`}>
                <span aria-hidden="true" className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-accent text-xs font-semibold text-accent-foreground">{g.customerName.trim().split(/\s+/).slice(0, 2).map(w => w[0]).join("").toUpperCase()}</span>
                <span className="min-w-0 flex-1">
                  <span className="block truncate font-medium">{g.displayName}</span>
                  <span className="block truncate text-xs text-muted-foreground">{g.customerName} · {channelName(g.channel)}</span>
                </span>
                <span className="shrink-0 text-right text-xs text-muted-foreground">
                  {g.lastMessageAt ? <span className="block">{time(g.lastMessageAt)}</span> : null}
                  <span className="block">{g.messageCount ? `${g.messageCount} tin` : "Chưa có tin"}</span>
                </span>
              </button></li>;
          })}</ul> : null}
      </div>
    </div>
    <div className={`${pane} bg-background/60`}>
      <div className="flex min-h-[3.25rem] flex-wrap items-center justify-between gap-2 border-b bg-card px-5 py-2.5">
        <div className="min-w-0"><h3 className="truncate font-semibold">{group?.displayName ?? "Tin nhắn"}</h3>
          <p className="truncate text-xs text-muted-foreground">{group ? `${group.customerName} · ${channelName(group.channel)} · tự cập nhật mỗi 30 giây` : "Tin bot đọc được trong các nhóm khách, lưu 90 ngày."}</p></div>
      </div>
      <div className="min-h-[16rem] flex-1 space-y-3 overflow-y-auto p-5" onScroll={e => {
        const el = e.currentTarget; stick.current = el.scrollHeight - el.scrollTop - el.clientHeight < 80; }}>
        {visible.error ? <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:border-rose-400/30 dark:bg-rose-400/10 dark:text-rose-200">{visible.error}</p> : null}
        {visible.selected && visible.messages === null && visible.loading ? <SkeletonRows label="Đang đọc tin nhắn…" rows={4} /> : null}
        {visible.messages ? <>
          {visible.hasMore ? <div className="flex justify-center"><button type="button" className={smallButton} disabled={visible.loading}
            onClick={() => { stick.current = false; void load(visible.selected, true); }}>Xem tin cũ hơn</button></div> : null}
          {visible.messages.length === 0 ? <p className="py-12 text-center text-sm text-muted-foreground">Nhóm này chưa có tin nào kể từ khi bot bắt đầu theo dõi.</p>
            : <ol className="m-0 list-none space-y-3 p-0" aria-label="Tin nhắn trong nhóm">
              {visible.messages.map(m => <li key={m.messageId} className="max-w-[46rem]">
                <p className="mb-1 text-xs text-muted-foreground"><bdi className="font-medium text-foreground">{m.senderName}</bdi> · {time(m.sentAt)}</p>
                <div className="inline-block max-w-full rounded-lg rounded-tl-sm border bg-card px-3.5 py-2.5 text-sm shadow-sm">
                  {m.hasImage ? <p className="flex items-center gap-1.5 text-muted-foreground"><ImageIcon className="size-4" aria-hidden="true" />Có hình ảnh</p> : null}
                  {m.text ? <p className="whitespace-pre-wrap break-words">{m.text}</p> : null}
                </div>
              </li>)}</ol>}
          <div ref={bottom} />
        </> : !visible.selected && visible.groups?.length ? <p className="py-12 text-center text-sm text-muted-foreground">Chọn một nhóm bên trái để xem tin nhắn.</p> : null}
      </div>
    </div>
  </section>;
}
