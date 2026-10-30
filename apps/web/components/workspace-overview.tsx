"use client";

import { useEffect, useState } from "react";
import { ArrowRight, Bot, MessagesSquare, Plug } from "lucide-react";
import type { BotChannelView } from "../lib/bot-channels";
import type { WorkspaceSection } from "../lib/workspace-sections";
import { type CustomerRequest, type RequestStatus, parseCustomerRequestPage, requestStatuses } from "../lib/customer-requests";
import { parseMemberPage } from "../lib/company-members";
import { StatusChip, requestAge, statusTone } from "./customer-request-detail";
import { SectionHeader } from "./workspace-shell";
import { SkeletonRows } from "./skeleton";

const shortcuts: { section: WorkspaceSection; title: string; text: string; icon: typeof Bot }[] = [
  { section: "tin-nhan", title: "Tin nhắn", text: "Tin nhắn từ các nhóm Zalo khách hàng mà bot đang theo dõi.", icon: MessagesSquare },
  { section: "tro-ly", title: "Trợ lý", text: "Hỏi AI về số liệu trong ERP của khách hàng.", icon: Bot },
  { section: "ket-noi", title: "Kết nối", text: "Bật/tắt bot, chọn nhóm khách, nguồn dữ liệu ERP và thành viên.", icon: Plug },
];
const counted = ["new", "in_progress", "needs_clarification", "done"] as const satisfies readonly RequestStatus[];
const kpis: { status: typeof counted[number]; label: string }[] = [
  { status: "new", label: "Việc mới" }, { status: "in_progress", label: "Đang xử lý" },
  { status: "needs_clarification", label: "Cần hỏi lại khách" }, { status: "done", label: "Xong hôm nay" },
];
const limit = 100;
const retryDelayMs = 400;
type Pages = Record<typeof counted[number], { items: CustomerRequest[]; hasMore: boolean }>;
export type OverviewGlance = { displayName: string | null; openTickets: string };
export type OverviewData = { companyId: string; userId?: string; generation: number; isCurrent: (generation: number) => boolean;
  validate: (generation: number) => Promise<boolean>; request: (generation: number, url: string, init?: RequestInit) => Promise<Response>;
  onUnauthorized: () => void; onGlance?: (glance: OverviewGlance) => void };
type Load = { state: "loading" } | { state: "ready"; pages: Pages; displayName: string | null } | { state: "error"; message: string };

const sameDay = (value: string, day: Date) => new Date(value).toDateString() === day.toDateString();
function overviewNumbers(pages: Pages, now = new Date()) {
  const doneToday = pages.done.items.filter(item => sameDay(item.updatedAt, now)).length;
  const value = (status: typeof counted[number]) => status === "done" ? String(doneToday)
    : `${pages[status].items.length}${pages[status].hasMore ? "+" : ""}`;
  // Oldest open questions first, then the oldest new tickets nobody has picked up.
  const attention = [...pages.needs_clarification.items, ...pages.new.items]
    .sort((a, b) => (a.status === b.status ? 0 : a.status === "needs_clarification" ? -1 : 1) || Date.parse(a.createdAt) - Date.parse(b.createdAt)).slice(0, 5);
  const days = Array.from({ length: 7 }, (_, index) => { const day = new Date(now); day.setDate(now.getDate() - 6 + index); return day; });
  const all = counted.flatMap(status => pages[status].items);
  const perDay = days.map(day => ({ label: index(day, now), count: all.filter(item => sameDay(item.createdAt, day)).length }));
  const recent = [...all].sort((a, b) => Date.parse(b.updatedAt) - Date.parse(a.updatedAt)).slice(0, 4);
  const waiting = pages.new.items.length + pages.needs_clarification.items.length;
  const more = pages.new.hasMore || pages.needs_clarification.hasMore;
  return { value, attention, perDay, recent, waiting: `${waiting}${more ? "+" : ""}`, total: perDay.reduce((sum, day) => sum + day.count, 0) };
}
function index(day: Date, now: Date) {
  return day.toDateString() === now.toDateString() ? "Hôm nay" : ["CN", "T2", "T3", "T4", "T5", "T6", "T7"][day.getDay()];
}

function useOverview(data: OverviewData | undefined): Load | null {
  const [load, setLoad] = useState<Load>({ state: "loading" });
  useEffect(() => {
    if (!data) return;
    const { companyId, userId, generation, isCurrent, validate, request, onUnauthorized, onGlance } = data;
    const abort = new AbortController();
    let live = true;
    const current = () => live && isCurrent(generation);
    setLoad({ state: "loading" });
    void (async () => {
      try {
        if (!await validate(generation) || !current()) return;
        // The member directory only supplies the greeting name; a reader without it still gets the overview.
        const directory = userId ? request(generation, `/api/local/company/members?${new URLSearchParams({ companyId, offset: "0", limit: String(limit) })}`,
          { cache: "no-store", signal: abort.signal }).then(async response => response.ok ? parseMemberPage(await response.json()) : null).catch(() => null) : Promise.resolve(null);
        // A read that hits a transient server error is retried once before the whole overview reports failure.
        const read = (status: RequestStatus) => request(generation,
          `/api/local/customer-requests?${new URLSearchParams({ companyId, status, offset: "0", limit: String(limit) })}`, { cache: "no-store", signal: abort.signal });
        const responses = await Promise.all(counted.map(async status => {
          const first = await read(status);
          if (first.status < 500 || !current()) return first;
          void first.body?.cancel().catch(() => {});
          await new Promise(resolve => setTimeout(resolve, retryDelayMs));
          return current() ? read(status) : first;
        }));
        const members = await directory;
        if (!current()) return;
        if (responses.some(response => response.status === 401)) { onUnauthorized(); return; }
        if (responses.some(response => !response.ok)) {
          setLoad({ state: "error", message: responses.some(response => response.status === 403)
            ? "Bạn không có quyền xem yêu cầu khách hàng của công ty này."
            : responses.some(response => response.status === 404) ? "Tính năng phiếu yêu cầu khách hàng chưa được bật."
              : "Chưa tải được số liệu công việc." });
          return;
        }
        const bodies: unknown[] = await Promise.all(responses.map(response => response.json()));
        if (!await validate(generation) || !current()) return;
        const pages = {} as Pages;
        counted.forEach((status, position) => {
          const page = parseCustomerRequestPage(bodies[position], 0, limit);
          if (!page) throw new Error();
          pages[status] = { items: page.items.filter(item => item.status === status), hasMore: page.hasMore };
        });
        const displayName = members?.items.find(member => member.userId.toLowerCase() === userId?.toLowerCase())?.displayName ?? null;
        const open = (["new", "needs_clarification", "in_progress"] as const).map(status => pages[status]);
        setLoad({ state: "ready", pages, displayName });
        onGlance?.({ displayName, openTickets: `${open.reduce((sum, page) => sum + page.items.length, 0)}${open.some(page => page.hasMore) ? "+" : ""}` });
      } catch {
        if (current()) setLoad({ state: "error", message: "Chưa tải được số liệu công việc." });
      }
    })();
    return () => { live = false; abort.abort(); };
    // A new company or session remounts this view; the callbacks stay bound to that generation.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [data?.companyId, data?.generation]);
  return data ? load : null;
}

const card = "rounded-xl border bg-card shadow-sm";

const botNames = { zalo: "Zalo", whatsapp: "WhatsApp" } as const;
const loginLabels = { logged_out: "Chưa đăng nhập", awaiting_qr: "Chờ quét mã QR", logged_in: "Đã đăng nhập", error: "Có lỗi" } as const;
function BotRow({ channel }: { channel: BotChannelView }) {
  const running = channel.enabled && channel.status?.loginState === "logged_in";
  const detail = !channel.status ? "Chưa nhận trạng thái" : channel.status.loginState !== "logged_in" ? loginLabels[channel.status.loginState]
    : channel.summarySchedule.length ? `Tóm tắt lúc ${channel.summarySchedule.join(", ")}` : "Chưa đặt lịch tóm tắt";
  return <li className="flex items-center gap-3 py-2.5">
    <span aria-hidden="true" className={`flex size-9 shrink-0 items-center justify-center rounded-lg text-xs font-bold ${channel.channel === "zalo"
      ? "bg-blue-50 text-blue-700 dark:bg-blue-400/15 dark:text-blue-200" : "bg-emerald-50 text-emerald-700 dark:bg-emerald-400/15 dark:text-emerald-200"}`}>
      {channel.channel === "zalo" ? "Zalo" : "WA"}</span>
    <span className="min-w-0 flex-1"><span className="block truncate text-sm font-medium">Bot {botNames[channel.channel]} · {channel.customerGroups.length} nhóm khách</span>
      <span className="block truncate text-xs text-muted-foreground">{detail}</span></span>
    <span className={`inline-flex h-6 shrink-0 items-center rounded-full px-2.5 text-xs font-medium ${running
      ? "bg-emerald-50 text-emerald-800 dark:bg-emerald-400/15 dark:text-emerald-200" : "bg-muted text-muted-foreground"}`}>{running ? "Đang chạy" : channel.enabled ? "Đã bật" : "Đang tắt"}</span>
  </li>;
}

export function WorkspaceOverview({ companyName, onOpen, data, bots }: { companyName: string; onOpen: (section: WorkspaceSection) => void;
  data?: OverviewData; bots?: readonly BotChannelView[] | null }) {
  const load = useOverview(data);
  const userName = load?.state === "ready" ? load.displayName : null;
  const numbers = load?.state === "ready" ? overviewNumbers(load.pages) : null;
  const peak = Math.max(1, ...(numbers?.perDay.map(day => day.count) ?? [1]));
  const today = new Date().toLocaleDateString("vi-VN", { weekday: "long", day: "2-digit", month: "2-digit", year: "numeric" });
  const hello = `Chào ${userName || "bạn"}`;
  const title = !numbers ? hello : numbers.waiting === "0" ? `${hello}, hôm nay chưa có việc mới cần xem` : `${hello}, hôm nay có ${numbers.waiting} việc cần bạn xem`;
  return <>
    <SectionHeader eyebrow={`${today.charAt(0).toUpperCase()}${today.slice(1)} · ${companyName}`} title={load ? title : "Tổng quan"}
      actions={<button type="button" onClick={() => onOpen("cong-viec")} aria-label="Mở Công việc"
        className="inline-flex h-9 items-center gap-2 rounded-lg bg-primary px-3.5 text-sm font-medium text-primary-foreground hover:opacity-90">
        Mở bảng công việc<ArrowRight className="size-4" aria-hidden="true" /></button>} />
    {load ? <div className="space-y-5">
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4" aria-busy={load.state === "loading"}>
        {kpis.map(({ status, label }) => <div key={status} className={`${card} space-y-2 p-5`}>
          <p className="flex items-center gap-2 text-sm font-medium text-muted-foreground"><span className={`size-2 rounded-sm ${statusTone[status].dot}`} aria-hidden="true" />{label}</p>
          <p className="text-3xl font-semibold tracking-tight">{numbers ? numbers.value(status) : <span className="inline-block h-8 w-12 animate-pulse rounded bg-muted" aria-label="Đang tải" />}</p>
        </div>)}
      </div>
      {load.state === "error" ? <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:border-rose-400/30 dark:bg-rose-400/10 dark:text-rose-200">{load.message}</p> : null}
      <div className="grid items-start gap-4 xl:grid-cols-[minmax(0,2fr)_minmax(20rem,1fr)]">
        <section aria-label="Việc cần chú ý" className={`${card} min-w-0 overflow-hidden`}>
          <div className="flex items-center justify-between border-b px-5 py-3.5">
            <h2 className="font-semibold">Việc cần chú ý</h2>
            <button type="button" onClick={() => onOpen("cong-viec")} className="text-sm font-medium text-indigo-600 hover:underline dark:text-indigo-300">Xem tất cả</button>
          </div>
          {numbers ? numbers.attention.length ? <ul className="m-0 list-none divide-y p-0">{numbers.attention.map(item => <li key={item.id}>
            <button type="button" onClick={() => onOpen("cong-viec")} className="grid w-full grid-cols-[5rem_minmax(0,1fr)_auto] items-center gap-3 px-5 py-3.5 text-left hover:bg-muted/50">
              <span className="font-mono text-xs text-muted-foreground">{item.code}</span>
              <span className="min-w-0"><span className="block truncate text-sm font-medium">{item.summary}</span>
                <span className="block truncate text-xs text-muted-foreground">{item.customerName} · {requestAge(item.createdAt)}</span></span>
              <StatusChip status={item.status} />
            </button></li>)}</ul>
            : <p className="px-5 py-8 text-center text-sm text-muted-foreground">Không có việc mới hay việc cần hỏi lại. Mọi thứ đang ổn.</p>
            : load.state === "loading" ? <SkeletonRows label="Đang tải…" rows={4} className="px-5 py-4" />
            : <p className="px-5 py-8 text-center text-sm text-muted-foreground">—</p>}
        </section>
        <div className="grid gap-4">
          {bots?.length ? <section aria-label="Kênh và bot" className={`${card} px-5 pb-2.5 pt-4`}>
            <div className="flex items-center justify-between"><h2 className="font-semibold">Kênh & bot</h2>
              <button type="button" onClick={() => onOpen("ket-noi")} className="text-sm font-medium text-indigo-600 hover:underline dark:text-indigo-300">Cài đặt</button></div>
            <ul className="m-0 list-none p-0">{bots.map(channel => <BotRow key={channel.channel} channel={channel} />)}</ul>
          </section> : null}
          <section aria-label="Yêu cầu mới 7 ngày" className={`${card} p-5`}>
            <div className="mb-4 flex items-baseline justify-between"><h2 className="font-semibold">Yêu cầu mới, 7 ngày</h2>
              {numbers ? <span className="text-xs text-muted-foreground">Tổng {numbers.total}</span> : null}</div>
            <div className="flex h-28 items-end gap-2" role="img" aria-label={numbers ? `Số yêu cầu mới mỗi ngày: ${numbers.perDay.map(day => `${day.label} ${day.count}`).join(", ")}` : "Đang tải biểu đồ"}>
              {(numbers?.perDay ?? Array.from({ length: 7 }, () => ({ label: "", count: 0 }))).map((day, position) =>
                <div key={position} className="flex h-full flex-1 flex-col items-center justify-end gap-1.5">
                  <div className={`w-full max-w-8 rounded-t ${position === 6 ? "bg-indigo-500" : "bg-indigo-200 dark:bg-indigo-400/30"}`}
                    style={{ height: `${Math.max(4, (day.count / peak) * 100)}%` }} />
                  <span className="whitespace-nowrap text-[11px] text-muted-foreground">{day.label}</span>
                </div>)}
            </div>
          </section>
          {numbers?.recent.length ? <section aria-label="Hoạt động gần đây" className={`${card} p-5`}>
            <h2 className="mb-3 font-semibold">Hoạt động gần đây</h2>
            <ol className="m-0 list-none space-y-3 border-l pl-4">{numbers.recent.map(item => <li key={item.id} className="relative">
              <span aria-hidden="true" className={`absolute -left-[1.29rem] top-1.5 size-2 rounded-full ${statusTone[item.status].dot}`} />
              <p className="truncate text-sm"><span className="font-mono text-xs text-muted-foreground">{item.code}</span> · {requestStatuses[item.status]} · {item.customerName}</p>
              <p className="text-xs text-muted-foreground">{requestAge(item.updatedAt)}</p></li>)}</ol>
          </section> : null}
          {bots?.length ? null : shortcuts.map(({ section, title, text, icon: Icon }) => <button key={section} type="button" onClick={() => onOpen(section)} aria-label={`Mở ${title}`}
            className={`${card} group flex items-start gap-3 p-4 text-left transition-colors hover:border-ring/50`}>
            <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-accent text-accent-foreground"><Icon className="size-4" aria-hidden="true" /></span>
            <span className="min-w-0 flex-1">
              <span className="flex items-center gap-1.5 text-sm font-semibold">{title}<ArrowRight className="size-4 opacity-0 transition-opacity group-hover:opacity-100" aria-hidden="true" /></span>
              <span className="mt-0.5 block text-sm leading-5 text-muted-foreground">{text}</span>
            </span>
          </button>)}
        </div>
      </div>
    </div> : <div className="grid gap-4 sm:grid-cols-2">
      {shortcuts.map(({ section, title, text, icon: Icon }) => <button key={section} type="button" onClick={() => onOpen(section)} aria-label={`Mở ${title}`}
        className={`${card} flex items-start gap-4 p-5 text-left hover:border-ring/50`}>
        <span className="flex size-10 shrink-0 items-center justify-center rounded-lg bg-accent text-accent-foreground"><Icon className="size-5" aria-hidden="true" /></span>
        <span className="min-w-0 flex-1"><span className="font-semibold">{title}</span><span className="mt-1 block text-sm leading-6 text-muted-foreground">{text}</span></span>
      </button>)}
    </div>}
  </>;
}
