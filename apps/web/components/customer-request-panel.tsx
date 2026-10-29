"use client";
import { useEffect, useRef, useState, type DragEvent, type FormEvent } from "react";
import { RefreshCw, Search } from "lucide-react";
import { parseMemberPage, type CompanyMember } from "../lib/company-members";
import { type CustomerRequest, type CustomerRequestDetail as Detail, type CustomerRequestPage, type CustomerRequestPatch, type RequestStatus,
  parseCustomerRequest, parseCustomerRequestDetail, parseCustomerRequestPage, requestConflict, requestStatuses } from "../lib/customer-requests";
import { CustomerRequestDetail, StatusChip, initials, requestAge as age, requestButton as button, requestField as field, requestTime, statusTone } from "./customer-request-detail";

type Props = { tenantId: string; companyId: string; userId: string; generation: number;
  isCurrent: (generation: number) => boolean; validate: (generation: number) => Promise<boolean>;
  request: (generation: number, url: string, init?: RequestInit) => Promise<Response>; onUnauthorized: () => void; initialCustomer?: string };
type View = "board" | "list";
type Selection = { view: View; status: string; customer: string; offset: number; code?: string };
// Rejected tickets stay out of the board; the list view still finds them.
const boardStatuses = ["new", "needs_clarification", "in_progress", "done"] as const satisfies readonly RequestStatus[];
type BoardStatus = typeof boardStatuses[number];
type Board = Record<BoardStatus, CustomerRequestPage>;
const boardLimit = 100, listLimit = 25;
type State = { scope: string; page: CustomerRequestPage | null; board: Board | null; detail: Detail | null; members: CompanyMember[] | null;
  memberError: string; loading: boolean; saving: boolean; conflict: boolean; error: string; notice: string; selection: Selection };
const initialSelection = (): Selection => ({ view: "board", status: "", customer: "", offset: 0 });
const empty = (scope: string, selection = initialSelection()): State => ({ scope, selection, page: null, board: null, detail: null, members: null,
  memberError: "", loading: false, saving: false, conflict: false, error: "", notice: "" });
class ReadFailure extends Error { constructor(readonly status: number) { super("Customer request unavailable"); } }

export function CustomerRequestPanel({ tenantId, companyId, userId, generation, isCurrent, validate, request, onUnauthorized, initialCustomer = "" }: Props) {
  const scope = `${tenantId}:${companyId}:${userId}:${generation}`;
  const [state, setState] = useState(() => empty(scope));
  const [statusFilter, setStatusFilter] = useState(""), [customerFilter, setCustomerFilter] = useState("");
  const [dropTarget, setDropTarget] = useState<BoardStatus | null>(null);
  const live = useRef(scope); live.current = scope;
  const mounted = useRef(false), serial = useRef(0), controller = useRef<AbortController | null>(null), saving = useRef<object | null>(null);
  const dragged = useRef<string | null>(null);
  const current = (value: number) => mounted.current && live.current === scope && serial.current === value && isCurrent(generation);
  function cancel() { serial.current++; controller.current?.abort(); controller.current = null; }
  async function confirm(value: number) {
    const valid = await validate(generation);
    if (!valid && current(value)) setState(empty(scope));
    return valid && current(value);
  }
  async function get(url: string, value: number, signal: AbortSignal, init?: RequestInit) {
    if (!await confirm(value)) return null;
    const response = await request(generation, url, { ...init, cache: "no-store", signal });
    if (!current(value)) return null;
    if (response.status === 401) { setState(empty(scope)); onUnauthorized(); return null; }
    if (!response.ok) throw new ReadFailure(response.status);
    const body: unknown = await response.json();
    return await confirm(value) ? body : null;
  }
  const root = `/api/local/customer-requests`, companyQuery = `companyId=${encodeURIComponent(companyId)}`;
  const pageUrl = (selection: Selection, status: string, offset: number, limit: number) => {
    const query = new URLSearchParams({ companyId, offset: String(offset), limit: String(limit) });
    if (status) query.set("status", status);
    if (selection.customer) query.set("customer", selection.customer);
    return `${root}?${query}`;
  };
  async function directory(value: number, signal: AbortSignal) {
    try {
      const members: CompanyMember[] = [];
      for (let offset = 0; offset <= 1000; offset += 100) {
        const body = await get(`/api/local/company/members?${companyQuery}&offset=${offset}&limit=100`, value, signal);
        if (body === null || !current(value)) return;
        const page = parseMemberPage(body);
        if (!page || page.companyId.toLowerCase() !== companyId.toLowerCase() || page.offset !== offset || page.limit !== 100
          || page.items.some(member => members.some(old => old.userId === member.userId))) throw new Error();
        members.push(...page.items);
        if (!page.hasMore) { setState(old => ({ ...old, members })); return; }
      }
      throw new Error(); // Never offer an incomplete directory as the full set of assignees.
    } catch (error) {
      if (current(value)) setState(old => ({ ...old, members: null, memberError: error instanceof ReadFailure && error.status === 403
        ? "Bạn không có quyền xem danh bạ thành viên. Vẫn có thể đổi trạng thái; quản trị viên có thể phân công người phụ trách."
        : "Không tải được đầy đủ danh bạ thành viên. Tải lại phiếu để thử lại; vẫn có thể đổi trạng thái." }));
    }
  }
  async function collection(selection: Selection, value: number, signal: AbortSignal) {
    if (selection.view === "list") {
      const body = await get(pageUrl(selection, selection.status, selection.offset, listLimit), value, signal);
      if (body === null) return null;
      const page = parseCustomerRequestPage(body, selection.offset, listLimit);
      if (!page) throw new Error();
      return { page, board: null };
    }
    const bodies = await Promise.all(boardStatuses.map(status => get(pageUrl(selection, status, 0, boardLimit), value, signal)));
    if (bodies.some(body => body === null)) return null;
    const board = {} as Board;
    boardStatuses.forEach((status, index) => {
      const page = parseCustomerRequestPage(bodies[index], 0, boardLimit);
      if (!page) throw new Error();
      // A ticket that changed column between the column reads shows only where its own status puts it.
      board[status] = { ...page, items: page.items.filter(item => item.status === status) };
    });
    return { page: null, board };
  }
  // Opening or closing a ticket keeps the loaded board; every other read replaces it.
  async function read(selection: Selection, notice = "", keepCollection = false) {
    if (!mounted.current || !isCurrent(generation) || saving.current) return;
    cancel(); const value = serial.current, abort = new AbortController(); controller.current = abort;
    setState(old => {
      const reusable = old.scope === scope ? old : empty(scope, selection);
      return { ...reusable, selection, loading: true, saving: false, notice: "", error: "", conflict: false,
        page: keepCollection ? reusable.page : null, board: keepCollection ? reusable.board : null,
        detail: selection.code && reusable.detail?.request.code === selection.code ? reusable.detail : null };
    });
    const members = keepCollection && state.members ? Promise.resolve() : directory(value, abort.signal);
    try {
      const [loaded, body] = await Promise.all([keepCollection ? Promise.resolve(undefined) : collection(selection, value, abort.signal),
        selection.code ? get(`${root}/${encodeURIComponent(selection.code)}?${companyQuery}`, value, abort.signal) : Promise.resolve(undefined)]);
      if (loaded === null || body === null || !current(value)) return;
      const detail = selection.code ? parseCustomerRequestDetail(body, selection.code) : null;
      if (selection.code && !detail) throw new Error();
      setState(old => ({ ...old, ...(loaded ?? {}), detail, notice, loading: false }));
    } catch (error) {
      if (current(value)) {
        cancel(); // Also discard a directory response racing a ticket refusal.
        setState({ ...empty(scope, selection), notice, error: error instanceof ReadFailure && error.status === 403
          ? "Bạn không có quyền xem yêu cầu khách hàng của công ty này."
          : error instanceof ReadFailure && error.status === 404 ? "Phiếu không còn khả dụng hoặc tính năng chưa được bật."
            : "Không tải được yêu cầu khách hàng. Hãy tải lại để thử lại." });
      }
    } finally {
      if (current(value)) setState(old => ({ ...old, loading: false }));
      await members;
    }
  }
  async function save(code: string, patch: CustomerRequestPatch) {
    if (saving.current || state.loading || state.conflict || !isCurrent(generation)) return;
    const operation = {}; saving.current = operation; cancel();
    const value = serial.current, abort = new AbortController(); controller.current = abort;
    setState(old => ({ ...old, saving: true, error: "", notice: "" }));
    try {
      const body = await get(`${root}/${encodeURIComponent(code)}?${companyQuery}`, value, abort.signal,
        { method: "PATCH", headers: { "Content-Type": "application/json" }, body: JSON.stringify(patch) });
      if (body === null || !current(value)) return;
      if (!parseCustomerRequest(body, code)) throw new Error();
      saving.current = null;
      await read(state.selection, "Đã lưu thay đổi.");
    } catch (error) {
      if (!current(value)) return;
      if (error instanceof ReadFailure && [403, 404].includes(error.status)) {
        setState({ ...empty(scope, state.selection), error: error.status === 403 ? "Bạn không còn quyền cập nhật phiếu này."
          : "Phiếu không còn khả dụng. Hãy tải lại danh sách." });
      } else {
        setState(old => ({ ...old, saving: false, conflict: !(error instanceof ReadFailure && error.status === 400),
          error: error instanceof ReadFailure && error.status === 409 ? requestConflict
            : error instanceof ReadFailure && error.status === 400 ? "Thông tin cập nhật không hợp lệ. Kiểm tra trạng thái và người phụ trách."
              : "Chưa xác nhận được thay đổi đã lưu. Tải lại phiếu để xem trạng thái mới trước khi cập nhật tiếp." }));
      }
    } finally {
      if (saving.current === operation) saving.current = null;
      if (current(value)) setState(old => ({ ...old, saving: false }));
    }
  }
  function close() {
    if (saving.current) return;
    cancel();
    setState(old => ({ ...old, loading: false, detail: null, selection: { ...old.selection, code: undefined } }));
  }
  const latest = useRef(read); latest.current = read;
  useEffect(() => {
    mounted.current = true; saving.current = null; setStatusFilter(""); setCustomerFilter(initialCustomer);
    void latest.current(initialCustomer ? { ...initialSelection(), view: "list", customer: initialCustomer } : initialSelection());
    return () => { mounted.current = false; cancel(); };
    // Scope replacement clears all data and cancels both reads and writes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scope]);
  const visible = state.scope === scope && isCurrent(generation) ? state : empty(scope);
  const disabled = visible.loading || visible.saving;
  const view = visible.selection.view;
  const memberName = (id: string | null) => id === null ? null : visible.members?.find(member => member.userId === id)?.displayName ?? null;
  function filter(event: FormEvent) {
    event.preventDefault();
    void read({ view, status: view === "list" ? statusFilter : "", customer: customerFilter.trim(), offset: 0 });
  }
  const openTicket = (code: string) => void read({ ...visible.selection, code }, "", true);
  function drop(event: DragEvent, status: BoardStatus) {
    event.preventDefault(); setDropTarget(null);
    const code = dragged.current; dragged.current = null;
    const item = boardStatuses.flatMap(column => visible.board?.[column].items ?? []).find(ticket => ticket.code === code);
    if (!item || item.status === status || disabled || visible.conflict) return;
    void save(item.code, { rowVersion: item.rowVersion, status });
  }
  function card(item: CustomerRequest) {
    const owner = memberName(item.assigneeUserId), selected = visible.selection.code === item.code;
    return <li key={item.id}>
      <button type="button" aria-label={item.code} aria-describedby={`request-${item.id}`} aria-pressed={selected} disabled={visible.saving}
        draggable={!disabled && !visible.conflict} onDragStart={event => { dragged.current = item.code; event.dataTransfer.effectAllowed = "move"; }}
        onDragEnd={() => { dragged.current = null; setDropTarget(null); }} onClick={() => openTicket(item.code)}
        className={`flex w-full flex-col gap-2 rounded-lg border bg-card p-3 text-left shadow-sm transition-colors hover:border-ring/60 disabled:opacity-60 ${selected ? "border-ring ring-2 ring-ring/20" : ""}`}>
        <span className="flex w-full items-center gap-2 text-xs text-muted-foreground"><span className="font-mono">{item.code}</span>
          <span className="ml-auto">{age(item.createdAt)}</span></span>
        <span id={`request-${item.id}`} className="line-clamp-3 whitespace-pre-wrap break-words text-sm font-medium leading-snug">{item.summary}</span>
        <span className="flex w-full items-center gap-2 text-xs text-muted-foreground">
          <span className="min-w-0 flex-1 truncate">{item.customerName}</span>
          <span title={owner ?? (item.assigneeUserId === null ? "Chưa phân công" : "Đã phân công")}
            className="flex size-6 shrink-0 items-center justify-center rounded-full bg-muted text-[10px] font-semibold text-foreground">
            {owner ? initials(owner) : item.assigneeUserId === null ? "—" : "•"}</span>
        </span>
      </button>
    </li>;
  }
  const viewButton = (value: View, label: string) => <button type="button" aria-pressed={view === value} disabled={disabled}
    onClick={() => view !== value && void read({ ...initialSelection(), view: value, customer: visible.selection.customer })}
    className={`h-7 rounded-md px-3 text-sm font-medium disabled:opacity-60 ${view === value ? "bg-card text-foreground shadow-sm" : "text-muted-foreground hover:text-foreground"}`}>{label}</button>;
  return <section aria-label="Yêu cầu khách hàng" aria-busy={disabled} className="min-w-0 space-y-4">
    <div className="flex flex-wrap items-center gap-2">
      <div role="group" aria-label="Kiểu xem" className="inline-flex gap-0.5 rounded-lg bg-muted p-1">
        {viewButton("board", "Bảng")}{viewButton("list", "Danh sách")}</div>
      <form onSubmit={filter} aria-label="Lọc yêu cầu khách hàng" className="flex flex-wrap items-center gap-2">
        {view === "list" ? <>
          <label htmlFor="request-status-filter" className="sr-only">Lọc trạng thái</label>
          <select id="request-status-filter" className={field} value={statusFilter} disabled={disabled}
            onChange={event => setStatusFilter(event.target.value)}><option value="">Tất cả trạng thái</option>
            {Object.entries(requestStatuses).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select>
        </> : null}
        <label htmlFor="request-customer-filter" className="sr-only">Khách hàng</label>
        <span className="relative"><Search className="pointer-events-none absolute left-2.5 top-2.5 size-4 text-muted-foreground" aria-hidden="true" />
          <input id="request-customer-filter" className={`${field} w-56 pl-8`} type="search" maxLength={200} value={customerFilter}
            disabled={disabled} onChange={event => setCustomerFilter(event.target.value)} placeholder="Tên khách hàng" /></span>
        <button type="submit" className={button} disabled={disabled}>Lọc phiếu</button>
      </form>
      <button type="button" className={`${button} ml-auto`} disabled={disabled} onClick={() => void read(visible.selection)}>
        <RefreshCw className={`size-4 ${visible.loading ? "animate-spin" : ""}`} aria-hidden="true" />Tải lại phiếu</button>
    </div>
    <p role="note" className="text-xs text-muted-foreground">AI tóm tắt từ Zalo · Nội dung chưa được nhân viên xác nhận.
      {view === "board" ? " Kéo thẻ sang cột khác để đổi trạng thái." : " Sắp xếp mới nhất trước."}</p>
    <div aria-live="polite" className="space-y-1 text-sm empty:hidden">
      {visible.loading && !visible.board && !visible.page ? <p role="status" className="text-muted-foreground">Đang tải yêu cầu khách hàng…</p> : null}
      {visible.saving ? <p role="status" className="text-muted-foreground">Đang lưu thay đổi…</p> : null}
      {visible.notice ? <p role="status" className="text-emerald-700 dark:text-emerald-300">{visible.notice}</p> : null}
    </div>
    {visible.error ? <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:border-rose-400/30 dark:bg-rose-400/10 dark:text-rose-200">{visible.error}</p> : null}
    <div className={visible.detail ? "grid items-start gap-4 xl:grid-cols-[minmax(0,1fr)_26rem]" : ""}>
      <div className="min-w-0 space-y-3">
        {visible.board ? <div className="grid auto-cols-[minmax(15rem,1fr)] grid-flow-col gap-3 overflow-x-auto pb-2">
          {boardStatuses.map(status => {
            const column = visible.board![status];
            return <section key={status} aria-label={`Cột ${requestStatuses[status]}`}
              onDragOver={event => { if (dragged.current) { event.preventDefault(); setDropTarget(status); } }}
              onDragLeave={() => setDropTarget(old => old === status ? null : old)} onDrop={event => drop(event, status)}
              className={`flex min-h-48 flex-col gap-2 rounded-xl bg-muted/70 p-2.5 transition-colors ${dropTarget === status ? "ring-2 ring-ring/40" : ""}`}>
              <h3 className="flex items-center gap-2 px-1 pt-0.5 text-sm font-semibold">
                <span className={`size-2 rounded-full ${statusTone[status].dot}`} aria-hidden="true" />{requestStatuses[status]}
                <span className="text-xs font-medium text-muted-foreground">{column.items.length}{column.hasMore ? "+" : ""}</span></h3>
              {column.items.length ? <ul className="m-0 flex list-none flex-col gap-2 p-0">{column.items.map(card)}</ul>
                : <p className="rounded-lg border border-dashed px-3 py-6 text-center text-xs text-muted-foreground">Không có phiếu</p>}
            </section>;
          })}
        </div> : null}
        {visible.page ? <>
          {visible.page.items.length ? <div className="overflow-x-auto rounded-xl border bg-card shadow-sm"><table className="w-full text-left text-sm" aria-label="Danh sách yêu cầu khách hàng">
            <thead className="border-b text-xs text-muted-foreground"><tr>{["Mã phiếu", "Khách hàng", "Nhóm Zalo", "Tóm tắt", "Trạng thái", "Người phụ trách", "Ngày tạo"].map(label => <th scope="col" className="px-3 py-2.5 font-medium" key={label}>{label}</th>)}</tr></thead>
            <tbody>{visible.page.items.map(item => <tr key={item.id} className={`border-t align-top hover:bg-muted/50 ${visible.selection.code === item.code ? "bg-accent/60" : ""}`}>
              <td className="px-3 py-2.5"><button className="whitespace-nowrap font-mono text-xs font-medium text-indigo-600 hover:underline disabled:opacity-50 dark:text-indigo-300" type="button" disabled={visible.saving}
                onClick={() => openTicket(item.code)}>{item.code}</button></td>
              <td className="px-3 py-2.5 break-words">{item.customerName}</td><td className="px-3 py-2.5 break-words text-muted-foreground">{item.sourceGroupName}</td>
              <td className="max-w-sm px-3 py-2.5"><p className="line-clamp-2 whitespace-pre-wrap break-words">{item.summary}</p></td>
              <td className="px-3 py-2.5"><StatusChip status={item.status} /></td><td className="px-3 py-2.5 break-all">{item.assigneeUserId === null ? "Chưa phân công"
                : memberName(item.assigneeUserId) ?? item.assigneeUserId}</td><td className="whitespace-nowrap px-3 py-2.5 text-muted-foreground">{requestTime(item.createdAt)}</td>
            </tr>)}</tbody></table></div> : <p className="rounded-xl border border-dashed bg-card p-8 text-center text-sm text-muted-foreground">Chưa có yêu cầu khách hàng phù hợp với bộ lọc.</p>}
          <div className="flex flex-wrap items-center gap-3 text-sm"><button type="button" className={button} disabled={disabled || visible.selection.offset === 0}
            onClick={() => void read({ ...visible.selection, code: undefined, offset: visible.selection.offset - listLimit })}>Trang trước</button>
            <span>Trang {Math.floor(visible.selection.offset / listLimit) + 1}</span>
            <button type="button" className={button} disabled={disabled || !visible.page.hasMore || visible.selection.offset >= 100000}
              onClick={() => void read({ ...visible.selection, code: undefined, offset: visible.selection.offset + listLimit })}>Trang sau</button></div>
        </> : null}
        {visible.board && boardStatuses.every(status => !visible.board![status].items.length)
          ? <p className="text-sm text-muted-foreground">Chưa có yêu cầu khách hàng phù hợp với bộ lọc.</p> : null}
        {visible.memberError && !visible.detail && (visible.page || visible.board) ? <p role="note" className="text-sm text-amber-700 dark:text-amber-200">{visible.memberError}</p> : null}
      </div>
      {visible.detail ? <div className="min-w-0 xl:sticky xl:top-20"><CustomerRequestDetail key={`${scope}:${visible.detail.request.code}:${visible.detail.request.rowVersion}`}
        detail={visible.detail} members={visible.members} memberError={visible.memberError} disabled={disabled || visible.conflict}
        onSave={patch => void save(visible.detail!.request.code, patch)} onClose={close} /></div> : null}
    </div>
  </section>;
}
