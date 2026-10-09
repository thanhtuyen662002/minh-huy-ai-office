"use client";
import { useEffect, useRef, useState } from "react";
import { GroupMessage, GroupMessages, GroupSources, parseGroupMessage, parseGroupMessages, parseGroupSources } from "../lib/group-source";

type Props = { tenantId: string; companyId: string; userId: string; generation: number;
  isCurrent: (generation: number) => boolean; validate: (generation: number) => Promise<boolean>;
  request: (generation: number, url: string, init?: RequestInit) => Promise<Response>; onUnauthorized: () => void };
type Selector = { kind: "sources"; offset: number } | { kind: "messages"; sourceId: string; before?: number }
  | { kind: "message"; sourceId: string; messageId: string };
type State = { scope: string; sources: GroupSources | null; messages: GroupMessages | null; detail: GroupMessage | null;
  offset: number; sourceId: string | null; loading: boolean; error: string };
const empty = (scope: string): State => ({ scope, sources: null, messages: null, detail: null, offset: 0, sourceId: null, loading: false, error: "" });
const button = "rounded-lg border border-slate-200 px-3 py-2 text-sm disabled:opacity-50 dark:border-white/15";
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
  return <section aria-label="Hộp thư nguồn" aria-busy={visible.loading} className="space-y-4 rounded-2xl border border-slate-200 bg-white p-5 dark:border-white/10 dark:bg-[#11182a]">
    <div className="flex flex-wrap items-center justify-between gap-3"><h2 className="text-lg font-semibold">Hộp thư nguồn</h2>
      <button type="button" className={button} onClick={() => void read({ kind: "sources", offset: visible.offset })}>Tải lại nguồn</button></div>
    <p className="text-sm text-slate-500">Tin được tiếp nhận tự động. Bạn chỉ thấy các nguồn có quyền đọc; nội dung được kiểm tra lại khi mở.</p>
    {visible.loading ? <p role="status">Đang đọc hộp thư…</p> : null}
    {visible.error ? <p role="alert" className="text-sm text-rose-600">{visible.error}</p> : null}
    {visible.sources ? <div className="space-y-3">
      {visible.sources.items.length === 0 ? <p>Chưa có nguồn được cấp quyền trong trang này.</p> : <ul className="flex flex-wrap gap-2" aria-label="Nguồn được cấp quyền">
        {visible.sources.items.map(item => <li key={item.source.sourceBindingId}><button type="button" className={button}
          aria-pressed={visible.sourceId === item.source.sourceBindingId} onClick={() => void read({ kind: "messages", sourceId: item.source.sourceBindingId })}>
          {item.displayName || "Nhóm nguồn"}</button></li>)}</ul>}
      <div className="flex items-center gap-2"><button type="button" className={button} disabled={visible.offset === 0 || visible.loading}
        onClick={() => void read({ kind: "sources", offset: visible.offset - 25 })}>Nguồn trước</button><span>Trang {Math.floor(visible.offset / 25) + 1}</span>
        <button type="button" className={button} disabled={!visible.sources.hasMore || visible.loading || visible.offset >= 10000}
          onClick={() => void read({ kind: "sources", offset: visible.offset + 25 })}>Nguồn sau</button></div>
    </div> : null}
    {visible.messages ? <div className="space-y-3">
      {visible.messages.hasCoverageGap ? <p role="note" className="text-sm text-amber-700">Nguồn có khoảng gián đoạn. Lịch sử đang hiển thị có thể chưa đầy đủ.</p> : null}
      <button type="button" className={button} onClick={() => void read({ kind: "messages", sourceId: visible.messages!.source.sourceBindingId })}>Tin mới nhất</button>
      {visible.messages.items.length === 0 ? <p>Chưa có tin đã tiếp nhận trong trang này.</p> : <ul className="space-y-2" aria-label="Tin đã tiếp nhận">
        {visible.messages.items.map(item => <li key={item.messageId} className="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-slate-100 p-3 dark:border-white/10">
          <span>{label(item.kind)} · {time(item.occurredAtUtc)}{item.isHistoricalBackfill ? " · Lịch sử bổ sung" : ""}</span>
          <button type="button" className={button} aria-label={`Đọc tin ${item.lastChangedSequence}`}
            onClick={() => void read({ kind: "message", sourceId: visible.messages!.source.sourceBindingId, messageId: item.messageId })}>Đọc tin</button></li>)}</ul>}
      {visible.messages.nextBeforeSequence !== null ? <button type="button" className={button}
        onClick={() => void read({ kind: "messages", sourceId: visible.messages!.source.sourceBindingId, before: visible.messages!.nextBeforeSequence! })}>Tin trước đó</button> : null}
    </div> : null}
    {visible.detail ? <article aria-label="Nội dung tin" className="space-y-3 rounded-xl bg-slate-50 p-4 dark:bg-white/5">
      <h3 className="font-semibold">{label(visible.detail.kind)}</h3><p className="text-sm">Người gửi: <bdi>{visible.detail.senderId}</bdi> · {time(visible.detail.occurredAtUtc)}</p>
      {visible.detail.hasCoverageGap && !visible.messages?.hasCoverageGap ? <p role="note">Nguồn có khoảng gián đoạn; lịch sử có thể chưa đầy đủ.</p> : null}
      {visible.detail.kind === 4 ? <p>Tin đã được thu hồi. Nội dung không còn hiển thị.</p>
        : <p className="whitespace-pre-wrap break-words">{visible.detail.text || (visible.detail.kind === 2 ? "Nội dung đa phương tiện được giữ làm bằng chứng; chưa có văn bản để hiển thị." : "Tin không có văn bản.")}</p>}
      <button type="button" className={button} onClick={() => void read({ kind: "message", sourceId: visible.detail!.source.sourceBindingId, messageId: visible.detail!.messageId })}>Cập nhật tin</button>
    </article> : null}
  </section>;
}
