"use client";

import { useEffect, useState } from "react";
import { ArrowRight, Bot, MessagesSquare, Plug } from "lucide-react";
import type { WorkspaceSection } from "../lib/workspace-sections";
import { type CustomerRequest, type RequestStatus, parseCustomerRequestPage } from "../lib/customer-requests";
import { StatusChip, statusTone } from "./customer-request-detail";
import { SectionHeader } from "./workspace-shell";

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
type Pages = Record<typeof counted[number], { items: CustomerRequest[]; hasMore: boolean }>;
export type OverviewData = { companyId: string; generation: number; isCurrent: (generation: number) => boolean;
  validate: (generation: number) => Promise<boolean>; request: (generation: number, url: string, init?: RequestInit) => Promise<Response>;
  onUnauthorized: () => void };
type Load = { state: "loading" } | { state: "ready"; pages: Pages } | { state: "error"; message: string };

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
  return { value, attention, perDay, total: perDay.reduce((sum, day) => sum + day.count, 0) };
}
function index(day: Date, now: Date) {
  return day.toDateString() === now.toDateString() ? "Hôm nay" : ["CN", "T2", "T3", "T4", "T5", "T6", "T7"][day.getDay()];
}

function useOverview(data: OverviewData | undefined): Load | null {
  const [load, setLoad] = useState<Load>({ state: "loading" });
  useEffect(() => {
    if (!data) return;
    const { companyId, generation, isCurrent, validate, request, onUnauthorized } = data;
    const abort = new AbortController();
    let live = true;
    const current = () => live && isCurrent(generation);
    setLoad({ state: "loading" });
    void (async () => {
      try {
        if (!await validate(generation) || !current()) return;
        const responses = await Promise.all(counted.map(status => request(generation,
          `/api/local/customer-requests?${new URLSearchParams({ companyId, status, offset: "0", limit: String(limit) })}`, { cache: "no-store", signal: abort.signal })));
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
        setLoad({ state: "ready", pages });
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

export function WorkspaceOverview({ companyName, onOpen, data }: { companyName: string; onOpen: (section: WorkspaceSection) => void; data?: OverviewData }) {
  const load = useOverview(data);
  const numbers = load?.state === "ready" ? overviewNumbers(load.pages) : null;
  const peak = Math.max(1, ...(numbers?.perDay.map(day => day.count) ?? [1]));
  const today = new Date().toLocaleDateString("vi-VN", { weekday: "long", day: "2-digit", month: "2-digit", year: "numeric" });
  return <>
    <SectionHeader title="Tổng quan" description={`${today.charAt(0).toUpperCase()}${today.slice(1)} · ${companyName}`}
      actions={<button type="button" onClick={() => onOpen("cong-viec")} aria-label="Mở Công việc"
        className="inline-flex h-9 items-center gap-2 rounded-lg bg-primary px-3.5 text-sm font-medium text-primary-foreground hover:opacity-90">
        Mở bảng công việc<ArrowRight className="size-4" aria-hidden="true" /></button>} />
    {load ? <div className="space-y-5">
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4" aria-busy={load.state === "loading"}>
        {kpis.map(({ status, label }) => <div key={status} className={`${card} space-y-2.5 p-4`}>
          <p className="flex items-center gap-2 text-sm font-medium text-muted-foreground"><span className={`size-2 rounded-sm ${statusTone[status].dot}`} aria-hidden="true" />{label}</p>
          <p className="text-3xl font-semibold tracking-tight">{numbers ? numbers.value(status) : <span className="inline-block h-8 w-12 animate-pulse rounded bg-muted" aria-label="Đang tải" />}</p>
        </div>)}
      </div>
      {load.state === "error" ? <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:border-rose-400/30 dark:bg-rose-400/10 dark:text-rose-200">{load.message}</p> : null}
      <div className="grid items-start gap-4 xl:grid-cols-[minmax(0,2fr)_minmax(18rem,1fr)]">
        <section aria-label="Việc cần chú ý" className={`${card} min-w-0 overflow-hidden`}>
          <div className="flex items-center justify-between border-b px-5 py-3.5">
            <h2 className="text-sm font-semibold">Việc cần chú ý</h2>
            <button type="button" onClick={() => onOpen("cong-viec")} className="text-sm font-medium text-indigo-600 hover:underline dark:text-indigo-300">Xem tất cả</button>
          </div>
          {numbers ? numbers.attention.length ? <ul className="m-0 list-none divide-y p-0">{numbers.attention.map(item => <li key={item.id}>
            <button type="button" onClick={() => onOpen("cong-viec")} className="grid w-full grid-cols-[5rem_minmax(0,1fr)_auto] items-center gap-3 px-5 py-3 text-left hover:bg-muted/50">
              <span className="font-mono text-xs text-muted-foreground">{item.code}</span>
              <span className="min-w-0"><span className="block truncate text-sm font-medium">{item.summary}</span>
                <span className="block truncate text-xs text-muted-foreground">{item.customerName} · {item.sourceGroupName}</span></span>
              <StatusChip status={item.status} />
            </button></li>)}</ul>
            : <p className="px-5 py-8 text-center text-sm text-muted-foreground">Không có việc mới hay việc cần hỏi lại. Mọi thứ đang ổn.</p>
            : <p className="px-5 py-8 text-center text-sm text-muted-foreground">{load.state === "loading" ? "Đang tải…" : "—"}</p>}
        </section>
        <div className="grid gap-4">
          <section aria-label="Yêu cầu mới 7 ngày" className={`${card} p-5`}>
            <div className="mb-4 flex items-baseline justify-between"><h2 className="text-sm font-semibold">Yêu cầu mới, 7 ngày</h2>
              {numbers ? <span className="text-xs text-muted-foreground">Tổng {numbers.total}</span> : null}</div>
            <div className="flex h-24 items-end gap-2" role="img" aria-label={numbers ? `Số yêu cầu mới mỗi ngày: ${numbers.perDay.map(day => `${day.label} ${day.count}`).join(", ")}` : "Đang tải biểu đồ"}>
              {(numbers?.perDay ?? Array.from({ length: 7 }, () => ({ label: "", count: 0 }))).map((day, position) =>
                <div key={position} className="flex h-full flex-1 flex-col items-center justify-end gap-1.5">
                  <div className={`w-full max-w-7 rounded-t ${position === 6 ? "bg-indigo-500" : "bg-indigo-200 dark:bg-indigo-400/30"}`}
                    style={{ height: `${Math.max(4, (day.count / peak) * 100)}%` }} />
                  <span className="text-[11px] text-muted-foreground">{day.label}</span>
                </div>)}
            </div>
          </section>
          {shortcuts.map(({ section, title, text, icon: Icon }) => <button key={section} type="button" onClick={() => onOpen(section)} aria-label={`Mở ${title}`}
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
