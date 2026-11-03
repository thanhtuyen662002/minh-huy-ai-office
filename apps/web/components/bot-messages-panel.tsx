"use client";
import { Fragment, ReactNode, useEffect, useRef, useState } from "react";
import { ExternalLink, ImageIcon, RefreshCw, X } from "lucide-react";
import { BotMessage, BotMessageGroup, parseBotMessageGroups, parseBotMessagePage } from "../lib/bot-messages";
import { SkeletonRows } from "./skeleton";

type Props = { companyId: string; userId: string; generation: number;
  isCurrent: (generation: number) => boolean; validate: (generation: number) => Promise<boolean>;
  request: (generation: number, url: string, init?: RequestInit) => Promise<Response>; onUnauthorized: () => void;
  onGroups?: (count: number) => void };
type Selected = { channel: string; externalId: string };
type State = { scope: string; groups: BotMessageGroup[] | null; selected: Selected | null; messages: BotMessage[] | null;
  hasMore: boolean; loading: boolean; error: string };
const empty = (scope: string): State => ({ scope, groups: null, selected: null, messages: null, hasMore: false, loading: false, error: "" });
const PAGE = 50;
const smallButton = "inline-flex h-8 items-center justify-center rounded-lg border bg-card px-2.5 text-xs font-medium hover:bg-muted disabled:pointer-events-none disabled:opacity-50";
const iconButton = "inline-flex size-8 items-center justify-center rounded-lg text-muted-foreground hover:bg-muted hover:text-foreground";
const time = (value: string) => new Date(value).toLocaleString("vi-VN", { hour: "2-digit", minute: "2-digit", day: "2-digit", month: "2-digit" });
const channelName = (channel: string) => channel === "whatsapp" ? "WhatsApp" : "Zalo";
// Each person (and group) keeps one colour everywhere, so a long thread is easy to follow.
const palette = [
  { name: "text-sky-700 dark:text-sky-300", avatar: "bg-sky-500", bar: "border-l-sky-400", soft: "bg-sky-50 dark:bg-sky-400/10" },
  { name: "text-violet-700 dark:text-violet-300", avatar: "bg-violet-500", bar: "border-l-violet-400", soft: "bg-violet-50 dark:bg-violet-400/10" },
  { name: "text-emerald-700 dark:text-emerald-300", avatar: "bg-emerald-500", bar: "border-l-emerald-400", soft: "bg-emerald-50 dark:bg-emerald-400/10" },
  { name: "text-amber-700 dark:text-amber-300", avatar: "bg-amber-500", bar: "border-l-amber-400", soft: "bg-amber-50 dark:bg-amber-400/10" },
  { name: "text-rose-700 dark:text-rose-300", avatar: "bg-rose-500", bar: "border-l-rose-400", soft: "bg-rose-50 dark:bg-rose-400/10" },
  { name: "text-indigo-700 dark:text-indigo-300", avatar: "bg-indigo-500", bar: "border-l-indigo-400", soft: "bg-indigo-50 dark:bg-indigo-400/10" },
  { name: "text-teal-700 dark:text-teal-300", avatar: "bg-teal-500", bar: "border-l-teal-400", soft: "bg-teal-50 dark:bg-teal-400/10" },
  { name: "text-fuchsia-700 dark:text-fuchsia-300", avatar: "bg-fuchsia-500", bar: "border-l-fuchsia-400", soft: "bg-fuchsia-50 dark:bg-fuchsia-400/10" },
  { name: "text-orange-700 dark:text-orange-300", avatar: "bg-orange-500", bar: "border-l-orange-400", soft: "bg-orange-50 dark:bg-orange-400/10" },
  { name: "text-cyan-700 dark:text-cyan-300", avatar: "bg-cyan-500", bar: "border-l-cyan-400", soft: "bg-cyan-50 dark:bg-cyan-400/10" },
];
const colour = (key: string) => { let h = 0; for (const c of key) h = (h * 31 + c.codePointAt(0)!) >>> 0; return palette[h % palette.length]; };
// People: the last two words (Vietnamese given name comes last); groups: the first two words.
const initials = (name: string, group = false) => { const words = name.trim().split(/\s+/).filter(Boolean);
  return (group ? words.slice(0, 2) : words.slice(-2)).map(w => [...w][0]).join("").toUpperCase() || "?"; };
const channelBadge = (channel: string) => channel === "whatsapp"
  ? "bg-emerald-100 text-emerald-800 dark:bg-emerald-400/15 dark:text-emerald-200" : "bg-sky-100 text-sky-800 dark:bg-sky-400/15 dark:text-sky-200";
const clock = (value: string) => new Date(value).toLocaleTimeString("vi-VN", { hour: "2-digit", minute: "2-digit" });
const dayKey = (value: string) => new Date(value).toLocaleDateString("vi-VN");
function dayLabel(value: string) {
  const d = new Date(value), today = new Date(), yesterday = new Date(); yesterday.setDate(today.getDate() - 1);
  if (d.toDateString() === today.toDateString()) return "Hôm nay";
  if (d.toDateString() === yesterday.toDateString()) return "Hôm qua";
  const text = d.toLocaleDateString("vi-VN", { weekday: "long", day: "2-digit", month: "2-digit", year: "numeric" });
  return text.charAt(0).toUpperCase() + text.slice(1);
}
// Mentions stand out and links open in a new tab; everything else stays plain text (React escapes it).
function richText(text: string): ReactNode[] {
  const parts: ReactNode[] = [], pattern = /(https?:\/\/[^\s<>"]+)|(@[\p{L}\p{N}_.]+(?:[ \u00a0][\p{Lu}][\p{L}\p{N}_.]*){0,3})/gu;
  let last = 0, key = 0;
  for (const match of text.matchAll(pattern)) {
    const at = match.index ?? 0;
    if (at > last) parts.push(text.slice(last, at));
    if (match[1]) {
      const url = match[1].replace(/[),.;:!?]+$/, "");
      parts.push(<a key={key++} href={url} target="_blank" rel="noopener noreferrer nofollow" className="break-all font-medium text-primary underline underline-offset-2">{url}</a>);
      if (url.length < match[1].length) parts.push(match[1].slice(url.length));
    } else parts.push(<strong key={key++} className="rounded bg-sky-100 px-1 font-semibold text-sky-800 dark:bg-sky-400/15 dark:text-sky-200">{match[2]}</strong>);
    last = at + match[0].length;
  }
  if (last < text.length) parts.push(text.slice(last));
  return parts;
}
type Viewing = { src: string; label: string };
function MessageImage({ src, label, onOpen, onLoad }: { src: string; label: string; onOpen: (image: Viewing) => void; onLoad: () => void }) {
  const [failed, setFailed] = useState(false);
  if (failed) return <span className="flex h-24 w-32 items-center justify-center gap-1.5 rounded-lg border border-dashed text-xs text-muted-foreground"><ImageIcon className="size-4" aria-hidden="true" />Không tải được ảnh</span>;
  return <button type="button" onClick={() => onOpen({ src, label })} aria-label={`Xem ${label}`}
    className="group/img block overflow-hidden rounded-lg border bg-muted/40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary">
    {/* eslint-disable-next-line @next/next/no-img-element -- private pictures behind the session cookie, not static assets */}
    <img src={src} alt={label} loading="lazy" decoding="async" onLoad={onLoad} onError={() => setFailed(true)}
      className="block max-h-64 max-w-[16rem] object-cover transition-transform duration-200 group-hover/img:scale-[1.02] sm:max-w-[20rem]" />
  </button>;
}
function ImageViewer({ image, onClose }: { image: Viewing; onClose: () => void }) {
  useEffect(() => {
    const key = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", key); return () => window.removeEventListener("keydown", key);
  }, [onClose]);
  return <div role="dialog" aria-modal="true" aria-label={image.label} onClick={onClose}
    className="fixed inset-0 z-50 flex flex-col items-center justify-center gap-3 bg-black/85 p-4 backdrop-blur-sm">
    <div className="flex w-full max-w-5xl items-center justify-between gap-2 text-sm text-white" onClick={e => e.stopPropagation()}>
      <span className="min-w-0 truncate font-medium">{image.label}</span>
      <span className="flex shrink-0 items-center gap-1">
        <a href={image.src} target="_blank" rel="noopener noreferrer" className="inline-flex h-9 items-center gap-1.5 rounded-lg px-3 hover:bg-white/15"><ExternalLink className="size-4" aria-hidden="true" />Mở ảnh gốc</a>
        <button type="button" onClick={onClose} aria-label="Đóng ảnh" className="inline-flex size-9 items-center justify-center rounded-lg hover:bg-white/15"><X className="size-5" aria-hidden="true" /></button>
      </span>
    </div>
    {/* eslint-disable-next-line @next/next/no-img-element -- private pictures behind the session cookie, not static assets */}
    <img src={image.src} alt={image.label} onClick={e => e.stopPropagation()} className="max-h-[85vh] max-w-full rounded-lg object-contain shadow-2xl" />
  </div>;
}
const same = (a: Selected | null, b: Selected) => a?.channel === b.channel && a.externalId === b.externalId;
// Oldest first, as in a chat; a refreshed newest page merges with older pages already loaded.
const merge = (old: BotMessage[], next: BotMessage[]) => [...new Map([...old, ...next].map(m => [m.messageId, m])).values()]
  .sort((a, b) => Date.parse(a.sentAt) - Date.parse(b.sentAt) || a.messageId.localeCompare(b.messageId));

export function BotMessagesPanel({ companyId, userId, generation, isCurrent, validate, request, onUnauthorized, onGroups }: Props) {
  const scope = `${companyId}:${userId}:${generation}`;
  const [state, setState] = useState<State>(() => empty(scope));
  const live = useRef(scope); live.current = scope;
  const latest = useRef(state); latest.current = state;
  const mounted = useRef(false), serial = useRef(0), controller = useRef<AbortController | null>(null);
  const bottom = useRef<HTMLDivElement | null>(null), stick = useRef(true);
  const [viewing, setViewing] = useState<Viewing | null>(null);
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
      if (!selected) onGroups?.(0);
      setState(old => ({ ...old, loading: false, error: status === 403 ? "Bạn không còn quyền xem tin nhắn của công ty này."
        : status === 404 && !selected ? "Kênh bot Zalo/WhatsApp chưa được bật trên máy chủ."
        : status === 404 ? "Nhóm này không còn được chọn ở Kết nối. Hãy tải lại danh sách." : "Chưa đọc được tin nhắn. Hãy thử lại sau." }));
    };
    let target = selected;
    try {
      const query = `companyId=${encodeURIComponent(companyId)}`;
      if (!older) {
        const raw = await get(value, `/api/local/bot-messages?${query}`, abort.signal);
        if (!current(value)) return;
        if (raw === "unauthorized" || typeof raw === "number") { fail(raw); return; }
        const groups = parseBotMessageGroups(raw); if (!groups) { fail(502); return; }
        onGroups?.(groups.length);
        // On first open, show the group with the newest messages instead of an empty pane.
        const first = !selected ? groups.filter(g => g.lastMessageAt).sort((x, y) => Date.parse(y.lastMessageAt!) - Date.parse(x.lastMessageAt!))[0] : undefined;
        const still = selected && groups.some(g => same(selected, g)) ? selected : first ? { channel: first.channel, externalId: first.externalId } : null;
        setState(old => ({ ...old, groups, selected: still, ...(still && same(old.selected, still) ? {} : { messages: null, hasMore: false }), loading: !!still }));
        if (!still) return;
        target = still;
      }
      const sel = target!;
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
  // Scroll only the message list; scrollIntoView would also scroll the page and hide the header on phones.
  useEffect(() => {
    const list = bottom.current?.parentElement;
    if (stick.current && count && list) list.scrollTop = list.scrollHeight;
  }, [count, visible.selected]);
  const group = visible.groups?.find(g => same(visible.selected, g));
  // Pictures arrive after the text; stay at the newest message while the reader has not scrolled up.
  const keepBottom = () => { const list = bottom.current?.parentElement; if (stick.current && list) list.scrollTop = list.scrollHeight; };
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
                className={`flex w-full min-w-0 items-center gap-3 border-l-[3px] px-4 py-3 text-left text-sm transition-colors hover:bg-muted/60 ${active ? "border-primary bg-primary/5" : "border-transparent"}`}>
                <span aria-hidden="true" className={`flex size-10 shrink-0 items-center justify-center rounded-full text-xs font-bold text-white shadow-sm ${colour(g.displayName).avatar}`}>{initials(g.customerName || g.displayName, true)}</span>
                <span className="min-w-0 flex-1">
                  <span className={`block truncate ${active ? "font-bold" : "font-semibold"}`}>{g.displayName}</span>
                  <span className="mt-0.5 flex min-w-0 items-center gap-1.5 text-xs text-muted-foreground">
                    <span className={`shrink-0 rounded-full px-1.5 py-px text-[0.65rem] font-semibold ${channelBadge(g.channel)}`}>{channelName(g.channel)}</span>
                    <span className="truncate">{g.customerName}</span>
                  </span>
                </span>
                <span className="shrink-0 text-right text-xs text-muted-foreground">
                  {g.lastMessageAt ? <span className="block">{time(g.lastMessageAt)}</span> : null}
                  {g.messageCount ? <span className="mt-0.5 inline-block rounded-full bg-primary/10 px-1.5 py-px font-semibold text-primary">{g.messageCount} tin</span>
                    : <span className="block">Chưa có tin</span>}
                </span>
              </button></li>;
          })}</ul> : null}
      </div>
    </div>
    <div className={`${pane} bg-background/60`}>
      <div className="flex min-h-[3.25rem] flex-wrap items-center justify-between gap-2 border-b bg-card px-5 py-2.5">
        <div className="flex min-w-0 items-center gap-3">
          {group ? <span aria-hidden="true" className={`flex size-9 shrink-0 items-center justify-center rounded-full text-xs font-bold text-white ${colour(group.displayName).avatar}`}>{initials(group.customerName || group.displayName, true)}</span> : null}
          <div className="min-w-0"><h3 className="truncate font-bold">{group?.displayName ?? "Tin nhắn"}</h3>
            <p className="flex min-w-0 items-center gap-1.5 truncate text-xs text-muted-foreground">{group ? <>
              <span className={`shrink-0 rounded-full px-1.5 py-px text-[0.65rem] font-semibold ${channelBadge(group.channel)}`}>{channelName(group.channel)}</span>
              <span className="truncate">{group.customerName} · tự cập nhật mỗi 30 giây</span></> : "Tin bot đọc được trong các nhóm khách, lưu 90 ngày."}</p></div>
        </div>
      </div>
      <div className="min-h-[16rem] flex-1 space-y-3 overflow-y-auto bg-gradient-to-b from-muted/30 to-background p-5" onScroll={e => {
        const el = e.currentTarget; stick.current = el.scrollHeight - el.scrollTop - el.clientHeight < 80; }}>
        {visible.error ? <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:border-rose-400/30 dark:bg-rose-400/10 dark:text-rose-200">{visible.error}</p> : null}
        {visible.selected && visible.messages === null && visible.loading ? <SkeletonRows label="Đang đọc tin nhắn…" rows={4} /> : null}
        {visible.messages ? <>
          {visible.hasMore ? <div className="flex justify-center"><button type="button" className={smallButton} disabled={visible.loading}
            onClick={() => { stick.current = false; void load(visible.selected, true); }}>Xem tin cũ hơn</button></div> : null}
          {visible.messages.length === 0 ? <p className="py-12 text-center text-sm text-muted-foreground">Nhóm này chưa có tin nào kể từ khi bot bắt đầu theo dõi.</p>
            : <ol className="m-0 list-none p-0" aria-label="Tin nhắn trong nhóm">
              {visible.messages.map((m, i) => {
                const previous = visible.messages![i - 1], newDay = !previous || dayKey(previous.sentAt) !== dayKey(m.sentAt);
                // Back-to-back messages from one person within 5 minutes share one name and avatar.
                const follow = !newDay && previous.senderName === m.senderName && Date.parse(m.sentAt) - Date.parse(previous.sentAt) < 5 * 60_000;
                const c = colour(m.senderName), sel = visible.selected!;
                const images = Array.from({ length: m.imageCount }, (_, n) => `/api/local/bot-messages/${sel.channel}/image?${new URLSearchParams({ companyId, group: sel.externalId, message: m.messageId, position: String(n) })}`);
                return <li key={m.messageId} className={follow ? "mt-1" : "mt-4 first:mt-0"}>
                  {newDay ? <div className="my-4 flex items-center gap-3 text-xs font-semibold text-muted-foreground" aria-hidden="true">
                    <span className="h-px flex-1 bg-border" /><span className="rounded-full border bg-card px-3 py-1 shadow-sm">{dayLabel(m.sentAt)}</span><span className="h-px flex-1 bg-border" /></div> : null}
                  <div className="flex max-w-[48rem] gap-3">
                    {follow ? <span className="w-9 shrink-0" aria-hidden="true" />
                      : <span aria-hidden="true" className={`mt-0.5 flex size-9 shrink-0 items-center justify-center rounded-full text-xs font-bold text-white shadow-sm ${c.avatar}`}>{initials(m.senderName)}</span>}
                    <div className="min-w-0 flex-1">
                      {follow ? null : <p className="mb-1 flex items-baseline gap-2 text-sm"><bdi className={`font-bold ${c.name}`}>{m.senderName}</bdi>
                        <span className="text-xs text-muted-foreground">{clock(m.sentAt)}</span></p>}
                      <div className={`inline-block max-w-full rounded-xl rounded-tl-sm border border-l-4 bg-card px-3.5 py-2.5 text-sm leading-relaxed shadow-sm ${c.bar}`}
                        title={follow ? `${m.senderName} · ${time(m.sentAt)}` : undefined}>
                        {images.length ? <div className={`flex flex-wrap gap-2 ${m.text ? "mb-2" : ""}`}>
                          {images.map((src, n) => <MessageImage key={src} src={src} label={`Ảnh ${n + 1} của ${m.senderName} lúc ${time(m.sentAt)}`} onOpen={setViewing} onLoad={keepBottom} />)}</div>
                          : m.hasImage ? <p className={`flex items-center gap-1.5 rounded-lg px-2 py-1 text-xs text-muted-foreground ${c.soft} ${m.text ? "mb-2" : ""}`} title="Ảnh gửi trước khi bot bắt đầu lưu ảnh">
                            <ImageIcon className="size-4" aria-hidden="true" /><span>Có hình ảnh</span></p> : null}
                        {m.text ? <p className="whitespace-pre-wrap break-words">{richText(m.text).map((part, n) => <Fragment key={n}>{part}</Fragment>)}</p> : null}
                      </div>
                    </div>
                  </div>
                </li>;
              })}</ol>}
          <div ref={bottom} />
        </> : !visible.selected && visible.groups?.length ? <p className="py-12 text-center text-sm text-muted-foreground">Chọn một nhóm bên trái để xem tin nhắn.</p> : null}
      </div>
    </div>
    {viewing ? <ImageViewer image={viewing} onClose={() => setViewing(null)} /> : null}
  </section>;
}
