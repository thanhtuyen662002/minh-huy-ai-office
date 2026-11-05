"use client";
import { useEffect, useRef, useState, type DragEvent, type FormEvent } from "react";
import { Lock, RefreshCw, Search } from "lucide-react";
import { parseMemberPage, type CompanyMember } from "../lib/company-members";
import { type CustomerRequest, type CustomerRequestDetail as Detail, type CustomerRequestPage, type CustomerRequestPatch, type RequestStatus,
  parseCustomerRequest, parseCustomerRequestDetail, parseCustomerRequestPage, requestConflict, requestStatuses, requestTaken } from "../lib/customer-requests";
import { CustomerRequestDetail, StatusChip, initials, requestAge as age, requestButton as button, requestField as field, requestTime, statusTone } from "./customer-request-detail";
import { BoardSkeleton, SkeletonRows } from "./skeleton";

type Props = { tenantId: string; companyId: string; userId: string; isAdmin?: boolean; generation: number;
  isCurrent: (generation: number) => boolean; validate: (generation: number) => Promise<boolean>;
  request: (generation: number, url: string, init?: RequestInit) => Promise<Response>; onUnauthorized: () => void; initialCustomer?: string };
type View = "board" | "list";
type Selection = { view: View; status: string; customer: string; offset: number; code?: string };
// Rejected tickets stay out of the board; the list view still finds them.
const boardStatuses = ["new", "needs_clarification", "in_progress", "done"] as const satisfies readonly RequestStatus[];
type BoardStatus = typeof boardStatuses[number];
type Board = Record<BoardStatus, CustomerRequestPage & { failed?: boolean }>;
const boardLimit = 100, listLimit = 25;
type State = { scope: string; page: CustomerRequestPage | null; board: Board | null; detail: Detail | null; members: CompanyMember[] | null;
  memberError: string; loading: boolean; saving: boolean; conflict: boolean; error: string; notice: string; selection: Selection };
const initialSelection = (): Selection => ({ view: "board", status: "", customer: "", offset: 0 });
const empty = (scope: string, selection = initialSelection()): State => ({ scope, selection, page: null, board: null, detail: null, members: null,
  memberError: "", loading: false, saving: false, conflict: false, error: "", notice: "" });
class ReadFailure extends Error { constructor(readonly status: number) { super("Customer request unavailable"); } }
// Puts a ticket in the column its status names, newest first like the server, and takes it out of every other column.
function place(board: Board, item: CustomerRequest): Board {
  const next = {} as Board;
  for (const status of boardStatuses) {
    const column = board[status], items = column.items.filter(old => old.code !== item.code);
    if (status === item.status && !column.failed) {
      const at = items.findIndex(old => Date.parse(old.createdAt) < Date.parse(item.createdAt));
      items.splice(at < 0 ? items.length : at, 0, item);
    }
    next[status] = items.length === column.items.length && items.every((old, index) => old === column.items[index]) ? column : { ...column, items };
  }
  return next;
}
const findOnBoard = (board: Board | null, code: string) => board ? boardStatuses.flatMap(status => board[status].items).find(item => item.code === code) : undefined;

export function CustomerRequestPanel({ tenantId, companyId, userId, isAdmin = false, generation, isCurrent, validate, request, onUnauthorized, initialCustomer = "" }: Props) {
  const scope = `${tenantId}:${companyId}:${userId}:${generation}`;
  const [state, setState] = useState(() => empty(scope));
  const [statusFilter, setStatusFilter] = useState(""), [customerFilter, setCustomerFilter] = useState("");
  const [dropTarget, setDropTarget] = useState<BoardStatus | null>(null);
  const live = useRef(scope); live.current = scope;
  const mounted = useRef(false), serial = useRef(0), controller = useRef<AbortController | null>(null), saving = useRef<object | null>(null);
  const dragged = useRef<string | null>(null);
  // Cards being saved after a drag; the rest of the board stays usable meanwhile.
  const [moving, setMoving] = useState<readonly string[]>([]);
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
    // One slow or failing column shows its own error instead of blanking the board; refusals still clear everything.
    const failed = Symbol();
    const bodies = await Promise.all(boardStatuses.map(status => get(pageUrl(selection, status, 0, boardLimit), value, signal)
      .catch((error: unknown) => { if (error instanceof ReadFailure && error.status < 500) throw error; return failed; })));
    if (bodies.some(body => body === null)) return null;
    if (bodies.every(body => body === failed)) throw new Error();
    const board = {} as Board;
    boardStatuses.forEach((status, index) => {
      if (bodies[index] === failed) { board[status] = { items: [], offset: 0, limit: boardLimit, hasMore: false, failed: true }; return; }
      const page = parseCustomerRequestPage(bodies[index], 0, boardLimit);
      if (!page) throw new Error();
      // A ticket that changed column between the column reads shows only where its own status puts it.
      board[status] = { ...page, items: page.items.filter(item => item.status === status) };
    });
    return { page: null, board };
  }
  // Opening or closing a ticket keeps the loaded board; every other read replaces it.
  // A quiet read keeps the current board on screen until the fresh one arrives.
  async function read(selection: Selection, notice = "", keepCollection = false, quiet = false) {
    if (!mounted.current || !isCurrent(generation) || saving.current) return;
    cancel(); const value = serial.current, abort = new AbortController(); controller.current = abort;
    setState(old => {
      const reusable = old.scope === scope ? old : empty(scope, selection);
      return { ...reusable, selection, loading: true, saving: false, notice: "", error: quiet ? reusable.error : "", conflict: false,
        page: keepCollection || quiet ? reusable.page : null, board: keepCollection || quiet ? reusable.board : null,
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
      const saved = parseCustomerRequest(body, code);
      if (!saved) throw new Error();
      saving.current = null;
      // On the board only the saved card moves; reloading every column made the whole board wait.
      if (state.selection.view === "board" && state.board) {
        setState(old => old.board ? { ...old, board: place(old.board, saved) } : old);
        await read(state.selection, "Đã lưu thay đổi.", true);
      } else await read(state.selection, "Đã lưu thay đổi.");
    } catch (error) {
      if (!current(value)) return;
      if (error instanceof ReadFailure && [403, 404].includes(error.status)) {
        setState({ ...empty(scope, state.selection), error: error.status === 403 ? "Bạn không còn quyền cập nhật phiếu này."
          : "Phiếu không còn khả dụng. Hãy tải lại danh sách." });
      } else {
        setState(old => ({ ...old, saving: false, conflict: !(error instanceof ReadFailure && error.status === 400),
          error: error instanceof ReadFailure && error.status === 423 ? requestTaken
            : error instanceof ReadFailure && error.status === 409 ? requestConflict
            : error instanceof ReadFailure && error.status === 400 ? "Thông tin cập nhật không hợp lệ. Kiểm tra trạng thái và người phụ trách."
              : "Chưa xác nhận được thay đổi đã lưu. Tải lại phiếu để xem trạng thái mới trước khi cập nhật tiếp." }));
      }
    } finally {
      if (saving.current === operation) saving.current = null;
      if (current(value)) setState(old => ({ ...old, saving: false }));
    }
  }
  // A dragged card moves at once; the save runs in the background and a refused save puts the card back.
  async function move(item: CustomerRequest, status: BoardStatus) {
    const alive = () => mounted.current && live.current === scope && isCurrent(generation);
    const code = item.code, selection = state.selection;
    const patch: CustomerRequestPatch = item.assigneeUserId === null && status === "in_progress"
      ? { rowVersion: item.rowVersion, status, assigneeUserId: userId } : { rowVersion: item.rowVersion, status };
    setMoving(old => [...old, code]);
    setState(old => old.board ? { ...old, error: "", notice: "",
      board: place(old.board, { ...item, status, assigneeUserId: patch.assigneeUserId ?? item.assigneeUserId }) } : old);
    const undo = (error: string) => setState(old => {
      const now = findOnBoard(old.board, code);
      // A newer read already shows the server's truth for this card; only undo our own optimistic copy.
      const board = old.board && now && now.rowVersion === item.rowVersion ? place(old.board, item) : old.board;
      return { ...old, board, error: `${code}: ${error}` };
    });
    try {
      if (!await validate(generation)) { if (alive()) setState(empty(scope)); return; }
      if (!alive()) return;
      const response = await request(generation, `${root}/${encodeURIComponent(code)}?${companyQuery}`,
        { method: "PATCH", headers: { "Content-Type": "application/json" }, body: JSON.stringify(patch), cache: "no-store" });
      if (!alive()) return;
      if (response.status === 401) { setState(empty(scope)); onUnauthorized(); return; }
      if (response.status === 403) { setState({ ...empty(scope, selection), error: "Bạn không còn quyền cập nhật phiếu này." }); return; }
      if (!response.ok) throw new ReadFailure(response.status);
      const saved = parseCustomerRequest(await response.json(), code);
      if (!alive()) return;
      if (!saved) throw new Error();
      setState(old => old.board ? { ...old, board: place(old.board, saved) } : old);
    } catch (error) {
      if (!alive()) return;
      const status = error instanceof ReadFailure ? error.status : 0;
      if (status === 400) { undo("Thông tin cập nhật không hợp lệ. Kiểm tra trạng thái và người phụ trách."); return; }
      undo(status === 423 ? requestTaken : status === 409 ? requestConflict : status === 404 ? "Phiếu không còn khả dụng."
        : "Chưa lưu được thay đổi. Bảng đã tải lại để hiện trạng thái mới nhất.");
      // The board may be stale: fetch the server's state without blanking it.
      if (!saving.current) void latest.current(selection, "", false, true);
    } finally {
      setMoving(old => old.filter(value => value !== code));
    }
  }
  function close() {
    if (saving.current) return;
    cancel();
    setState(old => ({ ...old, loading: false, detail: null, selection: { ...old.selection, code: undefined } }));
  }
  const latest = useRef(read); latest.current = read;
  const closeLatest = useRef(close); closeLatest.current = close;
  const hasDetail = !!state.detail;
  const drawer = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!hasDetail) return;
    const onKey = (event: KeyboardEvent) => { if (event.key === "Escape") closeLatest.current(); };
    // A click outside the drawer closes it and still reaches what was clicked, such as the sidebar.
    const onPointer = (event: PointerEvent) => {
      if (event.target instanceof Node && !drawer.current?.contains(event.target)) closeLatest.current();
    };
    window.addEventListener("keydown", onKey);
    document.addEventListener("pointerdown", onPointer, true);
    return () => { window.removeEventListener("keydown", onKey); document.removeEventListener("pointerdown", onPointer, true); };
  }, [hasDetail]);
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
  // A ticket someone has taken stays theirs: only they or an administrator may move it (the server enforces the same rule).
  const canChange = (item: CustomerRequest) => isAdmin || item.assigneeUserId === null || item.assigneeUserId === userId;
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
    if (!item || item.status === status || disabled || visible.conflict || moving.includes(item.code) || !canChange(item)) return;
    // Dragging an unassigned ticket into "Đang xử lý" also takes it, as the Telegram "Nhận việc" button does.
    void move(item, status);
  }
  function card(item: CustomerRequest) {
    const owner = memberName(item.assigneeUserId), selected = visible.selection.code === item.code, locked = !canChange(item);
    const pending = moving.includes(item.code);
    return <li key={item.id} className="shrink-0">
      <button type="button" aria-label={item.code} aria-describedby={`request-${item.id}`} aria-pressed={selected} aria-busy={pending} disabled={visible.saving}
        draggable={!disabled && !visible.conflict && !locked && !pending} title={locked ? `${owner ?? "Người khác"} đã nhận phiếu này` : undefined} onDragStart={event => { dragged.current = item.code; event.dataTransfer.effectAllowed = "move"; }}
        onDragEnd={() => { dragged.current = null; setDropTarget(null); }} onClick={() => openTicket(item.code)}
        className={`flex w-full flex-col gap-2 rounded-lg border bg-card p-3 text-left shadow-sm transition-colors hover:border-ring/60 disabled:opacity-60 ${pending ? "opacity-70" : ""} ${selected ? "border-ring ring-2 ring-ring/20" : ""}`}>
        <span className="flex w-full items-center gap-2 text-xs text-muted-foreground"><span className="font-mono">{item.code}</span>
          {locked ? <Lock className="size-3" aria-label="Người khác đã nhận" /> : null}
          {pending ? <span className="ml-auto flex items-center gap-1" role="status"><RefreshCw className="size-3 animate-spin" aria-hidden="true" />Đang lưu</span>
            : <span className="ml-auto">{age(item.createdAt)}</span>}</span>
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
    <p role="note" className="text-xs text-muted-foreground">AI tóm tắt từ Zalo/WhatsApp · Nội dung chưa được nhân viên xác nhận.
      {view === "board" ? isAdmin ? " Kéo thẻ sang cột khác để đổi trạng thái." : " Kéo thẻ sang cột khác để đổi trạng thái; phiếu người khác đã nhận thì chỉ người đó hoặc quản trị viên đổi được." : " Sắp xếp mới nhất trước."}</p>
    <div aria-live="polite" className="space-y-1 text-sm empty:hidden">
      {visible.saving ? <p role="status" className="text-muted-foreground">Đang lưu thay đổi…</p> : null}
      {visible.notice ? <p role="status" className="text-emerald-700 dark:text-emerald-300">{visible.notice}</p> : null}
    </div>
    {visible.error ? <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:border-rose-400/30 dark:bg-rose-400/10 dark:text-rose-200">{visible.error}</p> : null}
    <div>
      <div className="min-w-0 space-y-3">
        {visible.loading && !visible.board && !visible.page ? view === "board" ? <BoardSkeleton label="Đang tải yêu cầu khách hàng…" />
          : <SkeletonRows label="Đang tải yêu cầu khách hàng…" rows={6} className="rounded-xl border bg-card p-4" /> : null}
        {visible.board ? <div className="grid auto-cols-[minmax(15rem,1fr)] grid-flow-col gap-3 overflow-x-auto pb-2">
          {boardStatuses.map(status => {
            const column = visible.board![status];
            return <section key={status} aria-label={`Cột ${requestStatuses[status]}`}
              onDragOver={event => { if (dragged.current) { event.preventDefault(); setDropTarget(status); } }}
              onDragLeave={() => setDropTarget(old => old === status ? null : old)} onDrop={event => drop(event, status)}
              className={`flex h-[calc(100dvh-22rem)] min-h-80 flex-col gap-2 rounded-xl bg-muted/70 p-2.5 transition-colors ${dropTarget === status ? "ring-2 ring-ring/40" : ""}`}>
              <h3 className="flex shrink-0 items-center gap-2 px-1 pt-0.5 text-sm font-semibold">
                <span className={`size-2 rounded-full ${statusTone[status].dot}`} aria-hidden="true" />{requestStatuses[status]}
                <span className="text-xs font-medium text-muted-foreground">{column.failed ? "" : column.items.length}{column.hasMore ? "+" : ""}</span></h3>
              {column.failed ? <p role="alert" className="rounded-lg border border-dashed border-rose-300 px-3 py-6 text-center text-xs text-rose-700 dark:border-rose-400/40 dark:text-rose-200">Không tải được cột này. Bấm Tải lại phiếu để thử lại.</p>
                : column.items.length ? <ul className="my-0 ml-0 -mr-1.5 flex min-h-0 flex-1 list-none flex-col gap-2 overflow-y-auto overscroll-contain p-0 pr-1.5 [scrollbar-width:thin]">{column.items.map(card)}</ul>
                : <p className="rounded-lg border border-dashed px-3 py-6 text-center text-xs text-muted-foreground">Không có phiếu</p>}
            </section>;
          })}
        </div> : null}
        {visible.page ? <>
          {visible.page.items.length ? <div className="overflow-x-auto rounded-xl border bg-card shadow-sm"><table className="w-full text-left text-sm" aria-label="Danh sách yêu cầu khách hàng">
            <thead className="border-b text-xs text-muted-foreground"><tr>{["Mã phiếu", "Khách hàng", "Nhóm khách", "Tóm tắt", "Trạng thái", "Người phụ trách", "Ngày tạo"].map(label => <th scope="col" className="px-3 py-2.5 font-medium" key={label}>{label}</th>)}</tr></thead>
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
        {visible.board && boardStatuses.every(status => !visible.board![status].items.length && !visible.board![status].failed)
          ? <p className="text-sm text-muted-foreground">Chưa có yêu cầu khách hàng phù hợp với bộ lọc.</p> : null}
        {visible.memberError && !visible.detail && (visible.page || visible.board) ? <p role="note" className="text-sm text-amber-700 dark:text-amber-200">{visible.memberError}</p> : null}
      </div>
      {/* The ticket opens over the board so the columns keep their width. */}
      {visible.detail ? <div className="pointer-events-none fixed inset-0 z-40 flex justify-end">
        <div aria-hidden="true" className="absolute inset-0 bg-black/25" />
        <div ref={drawer} className="pointer-events-auto relative h-full w-full max-w-[30rem] overflow-y-auto bg-background p-3 shadow-2xl">
          <CustomerRequestDetail key={`${scope}:${visible.detail.request.code}:${visible.detail.request.rowVersion}`}
            detail={visible.detail} members={visible.members} userId={userId} isAdmin={isAdmin} memberError={visible.memberError} disabled={disabled || visible.conflict}
            onSave={patch => void save(visible.detail!.request.code, patch)} onClose={close} /></div></div> : null}
    </div>
  </section>;
}
