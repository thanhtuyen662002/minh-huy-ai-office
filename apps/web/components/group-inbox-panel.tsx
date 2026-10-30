"use client";
import { useEffect, useRef, useState } from "react";
import { RefreshCw } from "lucide-react";
import { GroupMessage, GroupMessages, GroupSources, parseGroupMessage, parseGroupMessages, parseGroupSources } from "../lib/group-source";
import { SkeletonRows } from "./skeleton";

type Props = { tenantId: string; companyId: string; userId: string; generation: number;
  isCurrent: (generation: number) => boolean; validate: (generation: number) => Promise<boolean>;
  request: (generation: number, url: string, init?: RequestInit) => Promise<Response>; onUnauthorized: () => void };
type Selector = { kind: "sources"; offset: number } | { kind: "messages"; sourceId: string; before?: number }
  | { kind: "message"; sourceId: string; messageId: string };
type State = { scope: string; sources: GroupSources | null; messages: GroupMessages | null; detail: GroupMessage | null;
  offset: number; sourceId: string | null; loading: boolean; error: string };
const empty = (scope: string): State => ({ scope, sources: null, messages: null, detail: null, offset: 0, sourceId: null, loading: false, error: "" });
const smallButton = "inline-flex h-8 items-center justify-center rounded-lg border bg-card px-2.5 text-xs font-medium hover:bg-muted disabled:pointer-events-none disabled:opacity-50";
const iconButton = "inline-flex size-8 items-center justify-center rounded-lg text-muted-foreground hover:bg-muted hover:text-foreground";
const time = (value: string) => new Date(value).toLocaleString("vi-VN");
const label = (kind: number) => ["", "Tin nhắn", "Tệp hoặc nội dung đa phương tiện", "Tin đã sửa", "Tin đã thu hồi"][kind];

export function GroupInboxPanel({ tenantId, companyId, userId, generation, isCurrent, validate, request, onUnauthorized }: Props) {
  const scope = `${tenantId}:${companyId}:${userId}:${generation}`;
  const [state, setState] = useState<State>(() => empty(scope));
  const live = useRef(scope); live.current = scope;
  const mounted = useRef(false), serial = useRef(0), controller = useRef<AbortController | null>(null);
  const selected = useRef<Selector>({ kind: "sources", offset: 0 });
  const current = (value: number) => mounted.current && live.current === scope && serial.current === value && isCurrent(generation);
  function cancel() { serial.current++; controller.current?.abort(); controller.current = null; }
  async function read(selector: Selector) {
    if (!mounted.current || !isCurrent(generation)) return;
    cancel(); selected.current = selector;
    const value = serial.current, abort = new AbortController(); controller.current = abort;
    setState(old => ({ ...(old.scope === scope ? old : empty(scope)), detail: null, loading: true, error: "",
      ...(selector.kind === "sources" ? { sources: null, messages: null, sourceId: null, offset: selector.offset }
        : selector.kind === "messages" ? { messages: null, sourceId: selector.sourceId } : {}) }));
    async function confirm() {
      const valid = await validate(generation);
      if (!valid && current(value)) setState(empty(scope));
      return valid && current(value);
    }
    try {
      if (!await confirm()) return;
      const root = "/api/local/group-sources", query = `companyId=${encodeURIComponent(companyId)}`;
      const path = selector.kind === "sources" ? `${root}?${query}&offset=${selector.offset}&limit=25`
        : `${root}/${selector.sourceId}/messages${selector.kind === "message" ? `/${selector.messageId}?${query}`
          : `?${query}&limit=25${selector.before === undefined ? "" : `&beforeSequence=${selector.before}`}`}`;
      const response = await request(generation, path, { cache: "no-store", signal: abort.signal });
      if (!current(value)) return;
      if (response.status === 401) { setState(empty(scope)); onUnauthorized(); return; }
      if (!response.ok) {
        setState(empty(scope));
        if (await confirm()) setState({ ...empty(scope), error: response.status === 403
          ? "Không còn quyền đọc nguồn này. Hãy tải lại nguồn được cấp quyền."
          : response.status === 404 ? "Nguồn hoặc tin chưa có sẵn. Hãy tải lại danh sách." : "Chưa đọc được hộp thư. Hãy thử lại sau." });
        return;
      }
      const body: unknown = await response.json(); if (!current(value)) return;
      const view = selector.kind === "sources" ? parseGroupSources(body, companyId, 25)
        : selector.kind === "messages" ? parseGroupMessages(body, companyId, selector.sourceId, 25, selector.before)
        : parseGroupMessage(body, companyId, selector.sourceId, selector.messageId);
      if (!view || ("source" in view ? view.source.tenantId !== tenantId : view.items.some(item => item.source.tenantId !== tenantId))) throw new Error("Unavailable");
      if (!await confirm()) return;
      setState(old => ({ ...(old.scope === scope ? old : empty(scope)), loading: false, error: "",
        ...(selector.kind === "sources" ? { sources: view as GroupSources } : selector.kind === "messages"
          ? { messages: view as GroupMessages } : { detail: view as GroupMessage }) }));
    } catch {
      if (current(value)) {
        setState(empty(scope));
        if (await confirm()) setState({ ...empty(scope), error: "Chưa đọc được hộp thư. Hãy tải lại để kiểm tra quyền hiện tại." });
      }
    } finally { if (current(value)) { controller.current = null; setState(old => ({ ...old, loading: false })); } }
  }
  const readLatest = useRef(read); readLatest.current = read;
  useEffect(() => {
    mounted.current = true; selected.current = { kind: "sources", offset: 0 }; setState(empty(scope));
    void readLatest.current(selected.current);
    const refresh = () => { if (!document.hidden) void readLatest.current(selected.current); };
    const visibility = () => {
      if (document.hidden) { cancel(); setState(empty(scope)); } else refresh();
    };
    const timer = setInterval(refresh, 30000);
    window.addEventListener("focus", refresh); document.addEventListener("visibilitychange", visibility);
    return () => { mounted.current = false; cancel(); clearInterval(timer); window.removeEventListener("focus", refresh);
      document.removeEventListener("visibilitychange", visibility); };
    // Only the authenticated scope owns this panel; event callbacks read latest props.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scope]);
  // Hide prior-scope private data during render, before effect cleanup can run.
  const visible = state.scope === scope ? state : empty(scope);
  const selectedSource = visible.sources?.items.find(item => item.source.sourceBindingId === visible.sourceId);
  const pane = "flex min-h-0 min-w-0 flex-col";
  // Three panes in the Chatwoot style: groups, the received messages of one group, and the one message opened.
  return <section aria-label="Hộp thư nguồn" aria-busy={visible.loading}
    className="grid min-w-0 max-w-full overflow-hidden rounded-xl border bg-card shadow-sm lg:h-[calc(100vh-13rem)] lg:min-h-[32rem] lg:grid-cols-[17rem_minmax(0,1fr)] xl:grid-cols-[17rem_minmax(0,1fr)_22rem]">
    <div className={`${pane} border-b lg:border-b-0 lg:border-r`}>
      <div className="flex items-center justify-between gap-2 border-b px-4 py-3">
        <h2 className="font-semibold">Nhóm khách</h2>
        <button type="button" className={iconButton} aria-label="Tải lại nguồn" title="Tải lại nguồn"
          onClick={() => void read({ kind: "sources", offset: visible.offset })}><RefreshCw className={`size-4 ${visible.loading ? "animate-spin" : ""}`} aria-hidden="true" /></button>
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto">
        {visible.sources ? visible.sources.items.length === 0 ? <p className="px-4 py-6 text-sm text-muted-foreground">Chưa có nguồn được cấp quyền trong trang này.</p>
          : <ul className="m-0 list-none p-0" aria-label="Nguồn được cấp quyền">
            {visible.sources.items.map(item => {
              const active = visible.sourceId === item.source.sourceBindingId, name = item.displayName || "Nhóm nguồn";
              return <li key={item.source.sourceBindingId} className="min-w-0 max-w-full">
                <button type="button" aria-pressed={active} onClick={() => void read({ kind: "messages", sourceId: item.source.sourceBindingId })}
                  className={`flex w-full min-w-0 items-center gap-3 border-l-2 px-4 py-3 text-left text-sm hover:bg-muted/60 ${active ? "border-primary bg-muted" : "border-transparent"}`}>
                  <span aria-hidden="true" className="flex size-8 shrink-0 items-center justify-center rounded-lg bg-accent text-xs font-semibold text-accent-foreground">{name.split(/\s+/)[0].slice(0, 2).toUpperCase()}</span>
                  <span className="min-w-0 flex-1 break-all font-medium leading-snug">{name}</span>
                </button></li>;
            })}</ul> : null}
      </div>
      {visible.sources && (visible.offset > 0 || visible.sources.hasMore) ? <div className="flex items-center justify-between gap-2 border-t px-4 py-2 text-xs text-muted-foreground">
        <button type="button" className={smallButton} disabled={visible.offset === 0 || visible.loading}
          onClick={() => void read({ kind: "sources", offset: visible.offset - 25 })}>Nguồn trước</button><span>Trang {Math.floor(visible.offset / 25) + 1}</span>
        <button type="button" className={smallButton} disabled={!visible.sources.hasMore || visible.loading || visible.offset >= 10000}
          onClick={() => void read({ kind: "sources", offset: visible.offset + 25 })}>Nguồn sau</button></div> : null}
    </div>
    <div className={`${pane} bg-background/60 xl:border-r`}>
      <div className="flex min-h-[3.25rem] flex-wrap items-center justify-between gap-2 border-b bg-card px-5 py-2.5">
        <div className="min-w-0"><h3 className="truncate font-semibold">{selectedSource?.displayName || (visible.messages ? "Nhóm nguồn" : "Tin nhắn")}</h3>
          <p className="text-xs text-muted-foreground">Tin được tiếp nhận tự động. Nội dung được kiểm tra lại quyền khi mở.</p></div>
        {visible.messages ? <button type="button" className={smallButton} onClick={() => void read({ kind: "messages", sourceId: visible.messages!.source.sourceBindingId })}>Tin mới nhất</button> : null}
      </div>
      <div className="min-h-0 flex-1 space-y-3 overflow-y-auto p-5">
        {visible.loading ? <SkeletonRows label="Đang đọc hộp thư…" rows={4} /> : null}
        {visible.error ? <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:border-rose-400/30 dark:bg-rose-400/10 dark:text-rose-200">{visible.error}</p> : null}
        {visible.messages ? <>
          {visible.messages.hasCoverageGap ? <p role="note" className="rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:bg-amber-400/10 dark:text-amber-200">Nguồn có khoảng gián đoạn. Lịch sử đang hiển thị có thể chưa đầy đủ.</p> : null}
          {visible.messages.items.length === 0 ? <p className="py-8 text-center text-sm text-muted-foreground">Chưa có tin đã tiếp nhận trong trang này.</p>
            : <ul className="m-0 list-none space-y-2 p-0" aria-label="Tin đã tiếp nhận">
              {visible.messages.items.map(item => {
                const open = visible.detail?.messageId === item.messageId;
                return <li key={item.messageId} className={`flex items-center gap-3 rounded-lg border bg-card px-3.5 py-2.5 text-sm shadow-sm ${open ? "border-ring ring-2 ring-ring/20" : ""}`}>
                  <span className="min-w-0 flex-1"><span className="font-medium">{label(item.kind)}</span>
                    <span className="text-muted-foreground"> · {time(item.occurredAtUtc)}{item.isHistoricalBackfill ? " · Lịch sử bổ sung" : ""}</span></span>
                  <button type="button" className={smallButton} aria-label={`Đọc tin ${item.lastChangedSequence}`}
                    onClick={() => void read({ kind: "message", sourceId: visible.messages!.source.sourceBindingId, messageId: item.messageId })}>Đọc tin</button></li>;
              })}</ul>}
          {visible.messages.nextBeforeSequence !== null ? <button type="button" className={smallButton}
            onClick={() => void read({ kind: "messages", sourceId: visible.messages!.source.sourceBindingId, before: visible.messages!.nextBeforeSequence! })}>Tin trước đó</button> : null}
        </> : !visible.loading && !visible.error && visible.sources?.items.length ? <p className="py-12 text-center text-sm text-muted-foreground">Chọn một nhóm bên trái để xem tin đã tiếp nhận.</p> : null}
      </div>
    </div>
    <div className={`${pane} border-t lg:col-span-2 xl:col-span-1 xl:border-t-0`}>
      {visible.detail ? <article aria-label="Nội dung tin" className="min-w-0 max-w-full space-y-3 overflow-y-auto p-5">
        <div className="flex items-center justify-between gap-2"><h3 className="font-semibold">{label(visible.detail.kind)}</h3>
          <button type="button" className={smallButton} onClick={() => void read({ kind: "message", sourceId: visible.detail!.source.sourceBindingId, messageId: visible.detail!.messageId })}>Cập nhật tin</button></div>
        <p className="break-words text-xs text-muted-foreground">Người gửi: <bdi className="break-all">{visible.detail.senderId}</bdi> · {time(visible.detail.occurredAtUtc)}</p>
        {visible.detail.hasCoverageGap && !visible.messages?.hasCoverageGap ? <p role="note" className="text-sm text-amber-700 dark:text-amber-200">Nguồn có khoảng gián đoạn; lịch sử có thể chưa đầy đủ.</p> : null}
        <div className="rounded-lg rounded-tl-sm bg-muted px-3.5 py-2.5 text-sm">
          {visible.detail.kind === 4 ? <p>Tin đã được thu hồi. Nội dung không còn hiển thị.</p>
            : <p className="whitespace-pre-wrap break-words">{visible.detail.text || (visible.detail.kind === 2 ? "Nội dung đa phương tiện được giữ làm bằng chứng; chưa có văn bản để hiển thị." : "Tin không có văn bản.")}</p>}
        </div>
      </article> : <p className="hidden p-8 text-center text-sm text-muted-foreground xl:block">Bấm “Đọc tin” để mở nội dung. Mỗi lần chỉ mở một tin.</p>}
    </div>
  </section>;
}

// The older group-intake inbox is shown only where that subsystem is switched on; elsewhere
// its source list answers 404 and Tin nhắn shows just the bot's messages.
export function OptionalGroupInboxPanel(props: Props) {
  const { companyId, generation, isCurrent, validate, request } = props;
  const [enabled, setEnabled] = useState(false);
  useEffect(() => {
    const abort = new AbortController();
    void (async () => {
      try {
        if (!await validate(generation) || !isCurrent(generation)) return;
        const response = await request(generation, `/api/local/group-sources?companyId=${encodeURIComponent(companyId)}&offset=0&limit=25`,
          { cache: "no-store", signal: abort.signal });
        void response.body?.cancel().catch(() => {});
        if (!abort.signal.aborted && isCurrent(generation)) setEnabled(response.status !== 404);
      } catch { /* Stays hidden; the bot messages panel is the page. */ }
    })();
    return () => abort.abort();
    // Probe once per authenticated scope.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [companyId, generation]);
  return enabled ? <div className="mt-5"><GroupInboxPanel {...props} /></div> : null;
}
